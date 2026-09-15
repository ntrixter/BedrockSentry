using System.IO.Compression;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 8.5. The test that matters most is the failure path: a restore
/// that dies halfway must leave the world it found, not half of each.
/// </summary>
public sealed class WorldRestorerTests : IAsyncDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-restore-{Guid.NewGuid():N}");

    private readonly FakeProcessFactory _factory = new();
    private readonly InstanceSupervisor _supervisor;
    private readonly BackupHistory _history;
    private readonly WorldRestorer _restorer;

    private string WorldsDirectory => Path.Combine(_directory, "instance", "worlds");

    public WorldRestorerTests()
    {
        Directory.CreateDirectory(_directory);

        _supervisor = new InstanceSupervisor(
            "survival",
            new InstanceDefinition { Name = "Survival" },
            Path.Combine(_directory, "instance"),
            _factory,
            TimeProvider.System,
            NullLogger<InstanceSupervisor>.Instance);

        _history = new BackupHistory(new TestPaths(Path.Combine(_directory, "data")));
        _restorer = new WorldRestorer(_history, TimeProvider.System, NullLogger<WorldRestorer>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _supervisor.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private void WriteWorld(params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(WorldsDirectory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    /// <summary>An archive shaped exactly as ZipBackupStrategy writes them.</summary>
    private string WriteArchive(string name, params (string Path, string Content)[] entries)
    {
        var directory = _history.DirectoryFor("survival");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (entryPath, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryPath).Open());
            writer.Write(content);
        }

        return path;
    }

    [Fact]
    public async Task Restores_the_archive_and_removes_what_the_backup_never_contained()
    {
        WriteWorld(
            ("Bedrock level/level.dat", "current"),
            ("Bedrock level/db/000099.ldb", "written after the backup"));

        var archive = WriteArchive(
            "backup-20260801-120000.zip",
            ("Bedrock level/level.dat", "from the backup"));

        var result = await _restorer.RestoreAsync(_supervisor, archive);

        Assert.Equal(1, result.FilesRestored);
        Assert.False(result.ServerWasRunning);

        Assert.Equal(
            "from the backup",
            File.ReadAllText(Path.Combine(WorldsDirectory, "Bedrock level", "level.dat")));

        // The newer db segment is gone. Left in place, LevelDB would read the
        // mixture as a corrupt world — extracting over the top is the bug.
        Assert.False(File.Exists(Path.Combine(WorldsDirectory, "Bedrock level", "db", "000099.ldb")));
    }

    [Fact]
    public async Task Takes_a_zip_safety_backup_first_and_records_it()
    {
        WriteWorld(("Bedrock level/level.dat", "precious"));
        var archive = WriteArchive("backup-20260801-120000.zip", ("Bedrock level/level.dat", "old"));

        var result = await _restorer.RestoreAsync(_supervisor, archive);

        // The safety copy is itself in the picker's list and holds the
        // pre-restore bytes — undoing a restore is restoring it.
        Assert.True(File.Exists(result.Safety.ArchivePath));

        using var zip = ZipFile.OpenRead(result.Safety.ArchivePath);
        using var reader = new StreamReader(zip.GetEntry("Bedrock level/level.dat")!.Open());
        Assert.Equal("precious", reader.ReadToEnd());

        var record = Assert.Single(
            await _history.ReadAsync("survival"), r => r.Reason == BackupReason.Safety);
        Assert.Contains("backup-20260801-120000.zip", record.Note);
    }

    [Fact]
    public async Task A_failed_restore_puts_the_old_world_back()
    {
        WriteWorld(("Bedrock level/level.dat", "precious"));

        // Two entries with the same path: the second ExtractToFile finds the
        // first already on disk and throws — a genuinely corrupt archive, and
        // it fails after real work has happened.
        var archive = WriteArchive(
            "backup-20260801-120000.zip",
            ("Bedrock level/level.dat", "first"),
            ("Bedrock level/level.dat", "second"));

        await Assert.ThrowsAnyAsync<IOException>(() => _restorer.RestoreAsync(_supervisor, archive));

        Assert.Equal(
            "precious",
            File.ReadAllText(Path.Combine(WorldsDirectory, "Bedrock level", "level.dat")));

        Assert.Empty(Directory.GetDirectories(
            Path.Combine(_directory, "instance"), "worlds.restoring-*"));
    }

    // A restore that fails leaves the world alone — and must leave the server
    // alone too. Stopping it and then not bringing it back would turn one
    // failure into an outage nobody asked for.
    [Fact]
    public async Task A_failed_restore_starts_the_server_again()
    {
        WriteWorld(("Bedrock level/level.dat", "precious"));

        var archive = WriteArchive(
            "backup-20260801-120000.zip",
            ("Bedrock level/level.dat", "first"),
            ("Bedrock level/level.dat", "second"));

        _factory.ProcessesExitOnStop = true;
        await _supervisor.StartAsync();
        _factory.Latest.Ready();
        Assert.True(await _supervisor.WaitForReadyAsync(TimeSpan.FromSeconds(5)));

        await Assert.ThrowsAnyAsync<IOException>(() => _restorer.RestoreAsync(_supervisor, archive));

        // Two processes: the one that was stopped, and the one started to put
        // the instance back the way it was found.
        Assert.Equal(2, _factory.Created.Count);
        Assert.True(_factory.Created[0].HasExited);

        Assert.Equal(
            "precious",
            File.ReadAllText(Path.Combine(WorldsDirectory, "Bedrock level", "level.dat")));
    }

    // The safety backup failing aborts before anything is touched, and that is
    // still a failure the server has to come back from.
    [Fact]
    public async Task A_restore_refused_before_it_starts_also_restarts_the_server()
    {
        // No world at all, so the safety backup has nothing to protect and the
        // archive's own entries collide on extract.
        var archive = WriteArchive(
            "backup-20260801-120000.zip",
            ("Bedrock level/level.dat", "first"),
            ("Bedrock level/level.dat", "second"));

        _factory.ProcessesExitOnStop = true;
        await _supervisor.StartAsync();
        _factory.Latest.Ready();
        Assert.True(await _supervisor.WaitForReadyAsync(TimeSpan.FromSeconds(5)));

        await Assert.ThrowsAnyAsync<Exception>(() => _restorer.RestoreAsync(_supervisor, archive));

        Assert.Equal(2, _factory.Created.Count);
    }

    [Fact]
    public async Task An_entry_pointing_outside_worlds_stops_the_restore_cold()
    {
        WriteWorld(("Bedrock level/level.dat", "precious"));

        var archive = WriteArchive(
            "backup-20260801-120000.zip",
            ("../escaped.txt", "zip-slip"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _restorer.RestoreAsync(_supervisor, archive));

        Assert.False(File.Exists(Path.Combine(_directory, "instance", "escaped.txt")));
        Assert.Equal(
            "precious",
            File.ReadAllText(Path.Combine(WorldsDirectory, "Bedrock level", "level.dat")));
    }

    [Fact]
    public async Task Only_this_instances_own_archives_are_accepted()
    {
        WriteWorld(("Bedrock level/level.dat", "precious"));

        var elsewhere = Path.Combine(_directory, "somewhere-else.zip");
        using (var zip = ZipFile.Open(elsewhere, ZipArchiveMode.Create))
        {
            zip.CreateEntry("Bedrock level/level.dat");
        }

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _restorer.RestoreAsync(_supervisor, elsewhere));
    }

    [Fact]
    public async Task A_running_server_is_stopped_for_the_restore_and_started_after()
    {
        WriteWorld(("Bedrock level/level.dat", "precious"));
        var archive = WriteArchive("backup-20260801-120000.zip", ("Bedrock level/level.dat", "old"));

        _factory.ProcessesExitOnStop = true;
        await _supervisor.StartAsync();
        _factory.Latest.Ready();
        Assert.True(await _supervisor.WaitForReadyAsync(TimeSpan.FromSeconds(5)));

        var result = await _restorer.RestoreAsync(_supervisor, archive);

        Assert.True(result.ServerWasRunning);
        Assert.True(result.ServerRestarted);

        // One process stopped, a second launched over the restored world.
        Assert.Equal(2, _factory.Created.Count);
        Assert.True(_factory.Created[0].HasExited);
    }

    /// <summary>
    /// `T151`. The restart at the end of a restore goes through the same
    /// pre-flight as any other start, and a refusal faults the instance instead
    /// of throwing — so the call returning proves nothing. This is the case the
    /// panel used to report as "The server is starting again."
    /// </summary>
    [Fact]
    public async Task A_restart_refused_by_the_preflight_is_not_reported_as_a_restart()
    {
        WriteWorld(("Bedrock level/level.dat", "precious"));
        var archive = WriteArchive("backup-20260801-120000.zip", ("Bedrock level/level.dat", "old"));

        // Clear for the start, held for the restart.
        var preflight = new FakeStartPreflight()
            .Then()
            .Then(new PortConflict(19132, "is already open on this machine by ncat (PID 4242)"));

        var factory = new FakeProcessFactory { ProcessesExitOnStop = true };
        await using var supervisor = new InstanceSupervisor(
            "survival",
            new InstanceDefinition { Name = "Survival" },
            Path.Combine(_directory, "instance"),
            factory,
            TimeProvider.System,
            NullLogger<InstanceSupervisor>.Instance,
            startPreflight: () => preflight);

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await supervisor.WaitForReadyAsync(TimeSpan.FromSeconds(5)));

        var result = await _restorer.RestoreAsync(supervisor, archive);

        // The restore itself succeeded — the world went back exactly as asked.
        Assert.Equal(1, result.FilesRestored);
        Assert.True(result.ServerWasRunning);

        // The restart did not.
        Assert.False(result.ServerRestarted);
        Assert.Equal(supervisor.Status.FaultReason, result.RestartRefusal);

        // The join, against the real refusal rather than a literal of my own —
        // this is what ties the panel's sentence to PortConflictText.
        Assert.StartsWith(
            "Restored backup-20260801-120000.zip (1 file). "
            + "The server was not started: Port 19132 is already open on this machine "
            + "by ncat (PID 4242).",
            result.Summary(),
            StringComparison.Ordinal);

        Assert.Equal(InstanceState.Faulted, supervisor.Status.State);

        // The refused launch never reached the factory: one process ever ran.
        Assert.Single(factory.Created);
    }

    /// <summary>
    /// The sentence the panel shows, pinned here because `BackupsTab.razor`
    /// cannot be reached by this suite at all — which is why it carried its own
    /// copy of the rule and got the third case wrong.
    /// </summary>
    [Fact]
    public void The_summary_has_a_sentence_for_each_of_the_three_restart_outcomes()
    {
        var stopped = new RestoreResult("b.zip", false, false, SafetyBackup.Taken("safety"), 12);

        Assert.Equal(
            "Restored b.zip (12 files). The server was stopped and stays stopped. "
            + "The previous state is in the safety backup above.",
            stopped.Summary());

        var restarted = new RestoreResult("b.zip", true, true, SafetyBackup.Taken("safety"), 12);

        Assert.Equal(
            "Restored b.zip (12 files). The server is starting again. "
            + "The previous state is in the safety backup above.",
            restarted.Summary());

        var refused = new RestoreResult(
            "b.zip", true, false, SafetyBackup.Taken("safety"), 12,
            RestartRefusal: "not started: Port 19132 is already open.");

        Assert.Equal(
            "Restored b.zip (12 files). "
            + "The server was not started: Port 19132 is already open. "
            + "The previous state is in the safety backup above.",
            refused.Summary());
    }

    [Fact]
    public void The_summary_counts_one_file_and_one_configuration_file_singly()
    {
        var one = new RestoreResult("b.zip", false, false, SafetyBackup.Taken("safety"), 1, ConfigFilesRestored: 1);

        Assert.Equal(
            "Restored b.zip (1 file, plus 1 configuration file). "
            + "The server was stopped and stays stopped. "
            + "The previous state is in the safety backup above.",
            one.Summary());
    }

    // docs/design.md § 8.5. Restoring configuration is a separate decision from
    // restoring the world, and the default has to be "world only" — an old
    // server.properties can move the port, which is the one change that makes a
    // server unreachable rather than merely wrong.
    [Fact]
    public async Task Configuration_in_the_archive_is_left_alone_unless_it_is_asked_for()
    {
        WriteWorld(("Bedrock level/level.dat", "current"));
        File.WriteAllText(Path.Combine(_directory, "instance", "server.properties"), "server-port=19134");

        var archive = WriteArchive(
            "backup-20260801-120000.zip",
            ("Bedrock level/level.dat", "from the backup"),
            ("_serverconfig/server.properties", "server-port=19132"));

        var result = await _restorer.RestoreAsync(_supervisor, archive);

        // One world file. The configuration entry is not counted as world, and
        // above all is not extracted into worlds/ — where it would appear as a
        // world folder called "_serverconfig".
        Assert.Equal(1, result.FilesRestored);
        Assert.Equal(0, result.ConfigFilesRestored);
        Assert.False(Directory.Exists(Path.Combine(WorldsDirectory, "_serverconfig")));

        Assert.Equal(
            "server-port=19134",
            File.ReadAllText(Path.Combine(_directory, "instance", "server.properties")));
    }

    [Fact]
    public async Task Asking_for_configuration_puts_it_back_in_the_instance_root()
    {
        WriteWorld(("Bedrock level/level.dat", "current"));
        File.WriteAllText(Path.Combine(_directory, "instance", "server.properties"), "server-port=19134");

        var archive = WriteArchive(
            "backup-20260801-120000.zip",
            ("Bedrock level/level.dat", "from the backup"),
            ("_serverconfig/server.properties", "server-port=19132"),
            ("_serverconfig/allowlist.json", """[{"name":"Steve"}]"""));

        var result = await _restorer.RestoreAsync(_supervisor, archive, restoreConfig: true);

        Assert.Equal(1, result.FilesRestored);
        Assert.Equal(2, result.ConfigFilesRestored);

        Assert.Equal(
            "server-port=19132",
            File.ReadAllText(Path.Combine(_directory, "instance", "server.properties")));
        Assert.Contains(
            "Steve",
            File.ReadAllText(Path.Combine(_directory, "instance", "allowlist.json")));
    }

    // An archive written before configuration was ever carried has no such
    // entries, and has to restore exactly as it always did.
    [Fact]
    public void An_archive_with_no_configuration_reports_none()
    {
        var archive = WriteArchive(
            "backup-20260801-120000.zip", ("Bedrock level/level.dat", "old"));

        Assert.Equal(0, _restorer.CountConfigFiles(archive));

        var withConfig = WriteArchive(
            "backup-20260802-120000.zip",
            ("Bedrock level/level.dat", "old"),
            ("_serverconfig/server.properties", "server-port=19132"),
            ("_serverconfig/permissions.json", "[]"));

        Assert.Equal(2, _restorer.CountConfigFiles(withConfig));
    }

    [Fact]
    public async Task The_safety_backup_carries_the_configuration_it_is_about_to_replace()
    {
        WriteWorld(("Bedrock level/level.dat", "current"));
        File.WriteAllText(
            Path.Combine(_directory, "instance", "server.properties"), "server-port=19134");

        var archive = WriteArchive(
            "backup-20260801-120000.zip",
            ("Bedrock level/level.dat", "from the backup"),
            ("_serverconfig/server.properties", "server-port=19132"));

        var result = await _restorer.RestoreAsync(_supervisor, archive, restoreConfig: true);

        // Undoing a config restore is restoring the safety archive with the box
        // ticked again, which only works if the safety copy took it too.
        // Asserted rather than bang-suppressed: a restore with a world present must
        // take one, and this clause is meaningless if it did not.
        Assert.True(result.Safety.Exists);

        using var zip = ZipFile.OpenRead(result.Safety.ArchivePath!);
        using var reader = new StreamReader(zip.GetEntry("_serverconfig/server.properties")!.Open());
        Assert.Equal("server-port=19134", reader.ReadToEnd());
    }

    [Fact]
    public async Task The_backup_list_is_newest_first_and_restorable_only()
    {
        var directory = _history.DirectoryFor("survival");
        Directory.CreateDirectory(directory);

        // Stamped rather than left to the order they were written in. The list
        // sorts on write time, and three files created in the same millisecond
        // tie — which on Windows they did, so the assertion was really testing
        // the filesystem's enumeration order and passed on Linux by luck.
        Stamp(WriteArchive("backup-20260801-120000.zip", ("a/b", "x")), "2026-08-01T12:00:00Z");
        Stamp(WriteArchive("backup-20260803-120000.zip", ("a/b", "x")), "2026-08-03T12:00:00Z");
        Stamp(WriteSnapshot("vss-20260802-120000", ("a/b", "x")), "2026-08-02T12:00:00Z");

        // Neither of these is something the wrapper knows how to put back.
        await File.WriteAllTextAsync(Path.Combine(directory, "history.json"), "[]");
        Directory.CreateDirectory(Path.Combine(directory, "somebody-elses-folder"));

        var backups = _restorer.ListBackups("survival");

        Assert.Equal(
            ["backup-20260803-120000.zip", "vss-20260802-120000", "backup-20260801-120000.zip"],
            backups.Select(b => b.Name));

        Assert.Equal(["archive", "snapshot", "archive"], backups.Select(b => b.Kind));
    }

    /// <summary>
    /// Milestone 9's half of § 8.5: a snapshot restores like an archive.
    /// </summary>
    /// <remarks>
    /// The two shapes go through the same flow — safety copy, move the old world
    /// aside, apply, delete — so what is worth asserting is that the folder path
    /// obeys the same two rules the archive path does: the world is replaced
    /// rather than merged into, and the reserved configuration folder does not
    /// arrive as a world called <c>_serverconfig</c>.
    /// </remarks>
    [Fact]
    public async Task Restores_a_vss_snapshot_the_same_way_as_an_archive()
    {
        WriteWorld(
            ("Bedrock level/level.dat", "current"),
            ("Bedrock level/db/000099.ldb", "written after the backup"));

        var snapshot = WriteSnapshot(
            "vss-20260801-120000",
            ("Bedrock level/level.dat", "from the snapshot"),
            ("_serverconfig/server.properties", "server-port=19199"));

        var result = await _restorer.RestoreAsync(_supervisor, snapshot);

        Assert.Equal(1, result.FilesRestored);

        Assert.Equal(
            "from the snapshot",
            File.ReadAllText(Path.Combine(WorldsDirectory, "Bedrock level", "level.dat")));

        Assert.False(File.Exists(Path.Combine(WorldsDirectory, "Bedrock level", "db", "000099.ldb")));

        // Configuration is restored into the instance root or not at all — never
        // into worlds, where it would read as a world folder.
        Assert.False(Directory.Exists(Path.Combine(WorldsDirectory, "_serverconfig")));
        Assert.Equal(0, result.ConfigFilesRestored);
    }

    [Fact]
    public async Task A_snapshots_configuration_is_restored_only_when_asked_for()
    {
        WriteWorld(("Bedrock level/level.dat", "current"));

        await File.WriteAllTextAsync(
            Path.Combine(_supervisor.WorkingDirectory, "server.properties"), "server-port=19132");

        var snapshot = WriteSnapshot(
            "vss-20260801-120000",
            ("Bedrock level/level.dat", "from the snapshot"),
            ("_serverconfig/server.properties", "server-port=19199"));

        var result = await _restorer.RestoreAsync(_supervisor, snapshot, restoreConfig: true);

        Assert.Equal(1, result.ConfigFilesRestored);

        Assert.Equal(
            "server-port=19199",
            await File.ReadAllTextAsync(
                Path.Combine(_supervisor.WorkingDirectory, "server.properties")));
    }

    [Fact]
    public void A_snapshot_reports_how_many_configuration_files_it_carries()
    {
        var snapshot = WriteSnapshot(
            "vss-20260801-120000",
            ("Bedrock level/level.dat", "x"),
            ("_serverconfig/server.properties", "y"),
            ("_serverconfig/allowlist.json", "[]"));

        Assert.Equal(2, _restorer.CountConfigFiles(snapshot));
    }

    /// <summary>
    /// Backdates a backup, so a test asserting "newest first" says which is
    /// newest instead of hoping the clock was kind.
    /// </summary>
    private static string Stamp(string path, string utc)
    {
        var when = DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)
            .UtcDateTime;

        if (Directory.Exists(path))
        {
            Directory.SetLastWriteTimeUtc(path, when);
        }
        else
        {
            File.SetLastWriteTimeUtc(path, when);
        }

        return path;
    }

    /// <summary>A folder shaped exactly as VssBackupStrategy writes them.</summary>
    private string WriteSnapshot(string name, params (string Path, string Content)[] entries)
    {
        var directory = Path.Combine(_history.DirectoryFor("survival"), name);

        foreach (var (entryPath, content) in entries)
        {
            var full = Path.Combine(
                directory, entryPath.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return directory;
    }

    // ---- T168: the safety backup the dialog promised ------------------------

    /// <summary>
    /// THE DEFECT, on the branch it actually lives on. An instance with
    /// configuration and no world, restoring with the configuration box ticked, is
    /// refused rather than overwriting files nothing holds a copy of.
    /// </summary>
    /// <remarks>
    /// The filed cause — an instance with no backup strategy enabled — is not one:
    /// the restore builds its own <c>ZipBackupStrategy</c> with <c>Enabled = true</c>
    /// and never consults the instance's strategy settings. The real hole is that the
    /// safety backup is skipped when <c>worlds/</c> is empty, and the configuration
    /// files it would have carried do not live under <c>worlds/</c>.
    /// </remarks>
    [Fact]
    public async Task A_config_restore_with_no_world_to_copy_is_refused_before_anything_is_touched()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "instance"));
        File.WriteAllText(Path.Combine(_directory, "instance", "server.properties"), "server-port=19134");
        var archive = WriteArchive(
            "backup-20260801-120000.zip",
            ("Bedrock level/level.dat", "new"),
            ("_serverconfig/server.properties", "server-port=19132"));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _restorer.RestoreAsync(_supervisor, archive, restoreConfig: true));

        Assert.Equal(SafetyBackupRule.Refusal, refusal.Message);

        // Nothing was touched: not the configuration it would have overwritten, and
        // not the world either.
        Assert.Equal(
            "server-port=19134",
            File.ReadAllText(Path.Combine(_directory, "instance", "server.properties")));
        Assert.False(Directory.Exists(WorldsDirectory));
        Assert.Empty(await _history.ReadAsync("survival"));
    }

    // Untick the configuration and the same restore goes ahead: the world lands in an
    // empty worlds/, nothing is overwritten, and no safety backup is claimed.
    [Fact]
    public async Task The_same_restore_without_the_configuration_goes_ahead_and_promises_no_undo()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "instance"));
        File.WriteAllText(Path.Combine(_directory, "instance", "server.properties"), "server-port=19134");
        var archive = WriteArchive("backup-20260801-120000.zip", ("Bedrock level/level.dat", "new"));

        var result = await _restorer.RestoreAsync(_supervisor, archive);

        Assert.False(result.Safety.Exists);
        Assert.Null(result.Safety.ArchivePath);
        Assert.Contains("no safety backup was taken", result.Summary());
        Assert.DoesNotContain("previous state is in the safety backup", result.Summary());

        // And the configuration it did not touch is still there.
        Assert.Equal(
            "server-port=19134",
            File.ReadAllText(Path.Combine(_directory, "instance", "server.properties")));
    }

    /// <summary>
    /// The question the dialog asks, against the three answers it has.
    /// </summary>
    /// <remarks>
    /// Through <c>NeedFor</c> rather than <c>SafetyBackupRule.For</c> directly, because
    /// what a unit test of the rule cannot check is that the restorer counts the
    /// configuration files in the <em>instance</em> directory — the ones that would be
    /// overwritten — rather than in the archive.
    /// </remarks>
    [Fact]
    public void The_need_for_a_safety_backup_is_read_off_the_instance_directory()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "instance"));

        // Nothing at all: no world, no configuration.
        Assert.Equal(SafetyBackupNeed.NotNeeded, _restorer.NeedFor(_supervisor, restoringConfig: true));

        // Configuration but no world. Only the ticked box makes it impossible.
        File.WriteAllText(Path.Combine(_directory, "instance", "allowlist.json"), "[]");
        Assert.Equal(SafetyBackupNeed.NotNeeded, _restorer.NeedFor(_supervisor, restoringConfig: false));
        Assert.Equal(
            SafetyBackupNeed.ImpossibleForConfig,
            _restorer.NeedFor(_supervisor, restoringConfig: true));

        // A world to protect covers both halves, because the archive carries the
        // configuration files under _serverconfig/ too.
        WriteWorld(("Bedrock level/level.dat", "precious"));
        Assert.Equal(SafetyBackupNeed.Required, _restorer.NeedFor(_supervisor, restoringConfig: true));
        Assert.Equal(SafetyBackupNeed.Required, _restorer.NeedFor(_supervisor, restoringConfig: false));
    }

    // The promise the dialog prints has to differ per answer, or printing it is
    // decoration. The refusal in particular must carry a remedy somebody can act on,
    // which is why it is not "take a backup first" -- Back up now on an empty world
    // refuses for the same reason this does.
    [Fact]
    public void Each_answer_has_its_own_sentence_and_the_refusal_names_a_way_out()
    {
        var required = SafetyBackupRule.Promise(SafetyBackupNeed.Required);
        var notNeeded = SafetyBackupRule.Promise(SafetyBackupNeed.NotNeeded);
        var impossible = SafetyBackupRule.Promise(SafetyBackupNeed.ImpossibleForConfig);

        Assert.Equal(3, new HashSet<string> { required, notNeeded, impossible }.Count);

        Assert.Contains("undoing this is just restoring that one", required);
        Assert.Contains("no safety backup is taken", notNeeded);

        Assert.Contains("Untick the configuration", impossible);
        Assert.DoesNotContain("Back up now", impossible);
        Assert.DoesNotContain("take a backup first", impossible);
    }

}
