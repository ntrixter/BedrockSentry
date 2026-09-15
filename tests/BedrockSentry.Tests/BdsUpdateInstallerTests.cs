using System.IO.Compression;
using System.Text;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Packages;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Packages;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 11. The health gate is the reason this is safe to offer at all:
/// Mojang ships the occasional build that will not run on somebody's box, and
/// the test that matters is that finding out costs an outage of minutes rather
/// than an evening.
/// </summary>
public sealed class BdsUpdateInstallerTests : IAsyncDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"bs-bdsupd-{Guid.NewGuid():N}");

    private readonly FakeProcessFactory _factory = new();
    private readonly SentryPaths _paths;
    private readonly ServerPackageStore _store;
    private readonly InstanceSupervisor _supervisor;

    private string WorkingDirectory => Path.Combine(_root, "instance");

    public BdsUpdateInstallerTests()
    {
        _paths = new SentryPaths(Path.Combine(_root, "data"));
        Directory.CreateDirectory(_paths.ServerPackages);
        _store = new ServerPackageStore(_paths, NullLogger<ServerPackageStore>.Instance);

        var definition = new InstanceDefinition { Name = "Survival" };
        definition.Backup.OnShutdown.OnServerUpdate = true;

        _supervisor = new InstanceSupervisor(
            "survival", definition, WorkingDirectory, _factory, TimeProvider.System,
            NullLogger<InstanceSupervisor>.Instance);

        Install(("bedrock_server.exe", "the 1.26.36.1 executable"));
        Install(("server.properties", "server-name=My Carefully Tuned Server\nserver-port=19134"));
        Install(("allowlist.json", """[{"name":"Steve"}]"""));
        Install(("worlds/Bedrock level/level.dat", "years of building"));
    }

    public async ValueTask DisposeAsync()
    {
        await _supervisor.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Install((string Path, string Content) file)
    {
        var path = Path.Combine(WorkingDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, file.Content);
    }

    private string Read(string relative) =>
        File.ReadAllText(Path.Combine(WorkingDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));

    private ServerPackage WritePackage(
        string fileName, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_paths.ServerPackages, fileName);

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (entryPath, content) in entries)
            {
                using var writer = new StreamWriter(
                    archive.CreateEntry(entryPath).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return _store.Find(fileName)!;
    }

    private ServerPackage TheNextBuild() =>
        WritePackage("bedrock-server-1.26.40.zip",
            ("bedrock_server.exe", "the 1.26.40 executable"),
            ("server.properties", "server-name=Dedicated Server\nserver-port=19132"),
            ("allowlist.json", "[]"),
            ("worlds/Bedrock level/level.dat", "the zip's empty world"));

    private BdsUpdateInstaller Installer(
        TimeSpan? healthTimeout = null, ServerVersionLog? versions = null)
    {
        var history = new BackupHistory(_paths);

        var orchestrator = new BackupOrchestrator(
            [], history, TimeProvider.System, NullLogger<BackupOrchestrator>.Instance);

        return new BdsUpdateInstaller(
            _store,
            new ShutdownBackups(orchestrator, TimeProvider.System, NullLogger<ShutdownBackups>.Instance),
            versions ?? new ServerVersionLog(_paths, NullLogger<ServerVersionLog>.Instance),
            TimeProvider.System,
            NullLogger<BdsUpdateInstaller>.Instance,
            healthTimeout);
    }

    /// <summary>
    /// Reports the next server started as soon as one is launched, the way a
    /// build that works does.
    /// </summary>
    private Task HealthyOnNextStartAsync(int afterProcessCount)
    {
        return Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);

            while (DateTime.UtcNow < deadline)
            {
                if (_factory.Created.Count > afterProcessCount)
                {
                    _factory.Latest.Ready();
                    return;
                }

                await Task.Delay(10);
            }
        });
    }

    private async Task StartAndBeHealthyAsync()
    {
        _factory.ProcessesExitOnStop = true;
        await _supervisor.StartAsync();
        _factory.Latest.Ready();
        Assert.True(await _supervisor.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_stopped_server_is_updated_where_it_stands_and_not_started()
    {
        var result = await Installer().ApplyAsync(_supervisor, TheNextBuild(), []);

        Assert.True(result.Applied);
        Assert.False(result.RolledBack);
        Assert.Equal("1.26.40", result.Version);
        Assert.Contains("has not been started", result.Detail);

        Assert.Equal("the 1.26.40 executable", Read("bedrock_server.exe"));
        Assert.Empty(_factory.Created);

        // The point of the whole exercise: everything that is not Bedrock's own
        // files is exactly as it was.
        Assert.Contains("My Carefully Tuned Server", Read("server.properties"));
        Assert.Contains("Steve", Read("allowlist.json"));
        Assert.Equal("years of building", Read("worlds/Bedrock level/level.dat"));
    }

    [Fact]
    public async Task A_running_server_is_stopped_updated_and_started_again()
    {
        await StartAndBeHealthyAsync();

        var healthy = HealthyOnNextStartAsync(afterProcessCount: 1);
        var result = await Installer().ApplyAsync(_supervisor, TheNextBuild(), []);
        await healthy;

        Assert.True(result.Applied);
        Assert.False(result.RolledBack);
        Assert.Contains("is installed and the server is running", result.Detail);

        Assert.Equal("the 1.26.40 executable", Read("bedrock_server.exe"));
        Assert.Contains("My Carefully Tuned Server", Read("server.properties"));

        // One process stopped, a second launched on the new build.
        Assert.Equal(2, _factory.Created.Count);
        Assert.True(_factory.Created[0].HasExited);
    }

    // The previous binaries are kept only until the new build proves itself.
    [Fact]
    public async Task A_successful_update_clears_the_binaries_it_kept()
    {
        await StartAndBeHealthyAsync();

        var healthy = HealthyOnNextStartAsync(afterProcessCount: 1);
        await Installer().ApplyAsync(_supervisor, TheNextBuild(), []);
        await healthy;

        Assert.Empty(Directory.GetDirectories(WorkingDirectory, ".previous-*"));
    }

    /// <summary>
    /// `T116`. A stopped update is the one path where no server will ever announce
    /// what is on disk, so the wrapper has to record what it wrote.
    /// </summary>
    /// <remarks>
    /// It used to call <c>ServerVersionLog.Forget</c>, which exists for removing an
    /// instance (<c>D14</c>) and deletes the record outright. So pressing the
    /// wrapper's own update button turned a server it held a first-seen version and
    /// date for into one it knew nothing about, and the panel went from naming the
    /// version to "unknown — this server has not run yet".
    /// </remarks>
    [Fact]
    public async Task A_stopped_update_records_what_it_installed_instead_of_forgetting()
    {
        var versions = new ServerVersionLog(_paths, NullLogger<ServerVersionLog>.Instance);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        // What an earlier run announced.
        versions.Record("survival", "1.26.36.1", before.AddDays(-7));

        await Installer(versions: versions).ApplyAsync(_supervisor, TheNextBuild(), []);

        var record = versions.Read("survival");

        Assert.NotNull(record);
        Assert.Equal("1.26.40", record.Version);

        // The history survives, which is the whole complaint: this is an update
        // from something, and the page can say so.
        Assert.True(record.IsAnUpdate);
        Assert.Equal("1.26.36.1", record.PreviousVersion);
        Assert.InRange(record.FirstSeenUtc, before, DateTimeOffset.UtcNow.AddSeconds(1));

        // But marked as the wrapper's account rather than the server's, because no
        // server has run on these files.
        Assert.False(record.Reported);

        // And the version the old process announced is dropped, since it describes
        // files that are no longer there.
        Assert.Null(_supervisor.Status.ServerVersion);
    }

    /// <summary>
    /// A server starting on files the wrapper installed confirms them, without
    /// that reading as a second update.
    /// </summary>
    [Fact]
    public async Task Starting_on_an_installed_build_confirms_it_rather_than_redating_it()
    {
        var versions = new ServerVersionLog(_paths, NullLogger<ServerVersionLog>.Instance);
        versions.Record("survival", "1.26.36.1", DateTimeOffset.UtcNow.AddDays(-7));

        await Installer(versions: versions).ApplyAsync(_supervisor, TheNextBuild(), []);

        var installed = versions.Read("survival")!;
        Assert.False(installed.Reported);

        // The server now starts and announces the same version.
        Assert.Null(versions.Record("survival", "1.26.40", DateTimeOffset.UtcNow));

        var confirmed = versions.Read("survival")!;

        Assert.True(confirmed.Reported);

        // Same update, not a new one: re-dating it here would make "last updated"
        // jump to whenever somebody got round to starting the server.
        Assert.Equal(installed.FirstSeenUtc, confirmed.FirstSeenUtc);
        Assert.Equal("1.26.36.1", confirmed.PreviousVersion);
    }

    /// <summary>
    /// `D37`. A stopped update has no health gate to clear the copy, so it keeps
    /// one deliberately — but only ever one.
    /// </summary>
    /// <remarks>
    /// The test above only ever walks the running path, which is why this went
    /// unnoticed until `T54` was run on a real box: the stopped branch returns
    /// before the cleanup, nothing else in the codebase sweeps these, and an
    /// instance updated twice carried two full copies of Bedrock — measured at
    /// 303 MB each against a 303 MB server (`T115`).
    /// </remarks>
    [Fact]
    public async Task A_stopped_update_keeps_one_previous_build_and_clears_any_older_one()
    {
        // What an earlier update left behind.
        var older = Path.Combine(WorkingDirectory, ".previous-20200101-000000");
        Directory.CreateDirectory(older);
        File.WriteAllText(Path.Combine(older, "bedrock_server.exe"), "a build from years ago");

        await Installer().ApplyAsync(_supervisor, TheNextBuild(), []);

        var kept = Directory.GetDirectories(WorkingDirectory, ".previous-*");

        Assert.Single(kept);
        Assert.False(Directory.Exists(older));

        // And the survivor is this update's, holding the build it replaced —
        // rather than the old folder having been kept and the new one dropped.
        Assert.Equal(
            "the 1.26.36.1 executable",
            File.ReadAllText(Path.Combine(kept[0], "bedrock_server.exe")));
    }

    /// <summary>
    /// The page says a copy is there and what it costs, because otherwise it is a
    /// few hundred megabytes appearing in the server's folder unannounced.
    /// </summary>
    [Fact]
    public async Task The_kept_build_can_be_described_to_whoever_is_paying_for_it()
    {
        var installer = Installer();

        Assert.Null(installer.KeptBuildFor(WorkingDirectory));

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await installer.ApplyAsync(_supervisor, TheNextBuild(), []);

        var kept = installer.KeptBuildFor(WorkingDirectory);

        Assert.NotNull(kept);

        // Only bedrock_server.exe is replaced: server.properties, the allowlist
        // and the world are preserved, so the package never writes over them and
        // there is nothing of theirs to keep.
        Assert.Equal(
            new FileInfo(Path.Combine(kept.Path, "bedrock_server.exe")).Length,
            kept.Bytes);

        // Read out of the folder's own name, which is stamped in UTC — the point
        // of parsing it rather than showing the text is that a page can then say
        // it in the reader's own time.
        Assert.NotNull(kept.Taken);
        Assert.InRange(kept.Taken.Value, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    /// <summary>
    /// A size asked for before an update must not still be the answer after it.
    /// </summary>
    /// <remarks>
    /// The size is cached because walking one of these folders means ten thousand
    /// files and the page that asks ticks every second. That is only sound once the
    /// folder has stopped growing, and on a real install the page does ask mid-copy:
    /// the panel reported 209 MB for a folder that finished at 302 MB and kept
    /// saying so until the service restarted.
    ///
    /// This covers the part with a seam — that a cached answer does not survive the
    /// next update. The mid-copy window itself has none, since nothing can be
    /// interleaved between `UpdateInPlaceAsync` starting and finishing from out here;
    /// it is verified manually on Windows instead (`T115`).
    /// </remarks>
    [Fact]
    public async Task A_size_asked_for_before_an_update_is_not_the_answer_after_it()
    {
        var installer = Installer();

        // An earlier update's copy, and a page that has already priced it.
        var older = Path.Combine(WorkingDirectory, ".previous-20200101-000000");
        Directory.CreateDirectory(older);
        File.WriteAllText(Path.Combine(older, "bedrock_server.exe"), "a build from years ago");

        var stale = installer.KeptBuildFor(WorkingDirectory)!;
        Assert.Equal(older, stale.Path);
        Assert.Equal("a build from years ago".Length, stale.Bytes);

        await installer.ApplyAsync(_supervisor, TheNextBuild(), []);

        var kept = installer.KeptBuildFor(WorkingDirectory)!;

        Assert.NotEqual(older, kept.Path);
        Assert.Equal(
            new FileInfo(Path.Combine(kept.Path, "bedrock_server.exe")).Length,
            kept.Bytes);
    }

    /// <summary>A folder nobody can date is still worth reporting the size of.</summary>
    [Fact]
    public void A_kept_build_whose_name_is_not_a_stamp_is_still_described()
    {
        var odd = Path.Combine(WorkingDirectory, ".previous-renamed-by-somebody");
        Directory.CreateDirectory(odd);
        File.WriteAllText(Path.Combine(odd, "bedrock_server.exe"), "12345");

        var kept = Installer().KeptBuildFor(WorkingDirectory);

        Assert.NotNull(kept);
        Assert.Null(kept.Taken);
        Assert.Equal(5, kept.Bytes);
    }

    /// <summary>
    /// The build that will not run. Nothing reports the server started, the
    /// health budget runs out, and the previous binaries go back.
    /// </summary>
    [Fact]
    public async Task A_build_that_never_starts_is_rolled_back_and_the_old_one_runs_again()
    {
        await StartAndBeHealthyAsync();

        var package = WritePackage("bedrock-server-1.26.40.zip",
            ("bedrock_server.exe", "the build that will not run"),
            ("bedrock_server_how_to.html", "a file the old build never had"));

        // The rollback's own start is the one that gets to be healthy — the
        // update's is deliberately left to time out.
        var recovered = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (_factory.Created.Count > 2)
                {
                    _factory.Latest.Ready();
                    return;
                }

                await Task.Delay(10);
            }
        });

        var result = await Installer(TimeSpan.FromMilliseconds(300))
            .ApplyAsync(_supervisor, package, []);

        await recovered;

        Assert.False(result.Applied);
        Assert.True(result.RolledBack);

        Assert.Equal("the 1.26.36.1 executable", Read("bedrock_server.exe"));
        Assert.False(File.Exists(Path.Combine(WorkingDirectory, "bedrock_server_how_to.html")));

        // Kept deliberately: somebody diagnosing why a build would not run wants
        // the binaries that would not run.
        Assert.Single(Directory.GetDirectories(WorkingDirectory, ".previous-*"));

        // Started, timed out, stopped, started again on the previous build.
        Assert.Equal(3, _factory.Created.Count);
    }

    [Fact]
    public async Task The_rollback_says_so_plainly_when_the_old_build_comes_back_up()
    {
        await StartAndBeHealthyAsync();

        var recovered = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (_factory.Created.Count > 2)
                {
                    _factory.Latest.Ready();
                    return;
                }

                await Task.Delay(10);
            }
        });

        var result = await Installer(TimeSpan.FromMilliseconds(300))
            .ApplyAsync(_supervisor, TheNextBuild(), []);

        await recovered;

        Assert.Contains("previous build was put back", result.Detail);
        Assert.Contains("Pin the working version", result.Detail);
    }

    // The instance's backup-on-shutdown toggle for updates has existed in config
    // since milestone 7 waiting for a caller. This is that caller.
    [Fact]
    public async Task The_instances_backup_on_update_toggle_is_honoured()
    {
        await StartAndBeHealthyAsync();

        var history = new BackupHistory(_paths);

        var orchestrator = new BackupOrchestrator(
            [new ZipBackupStrategy(TimeProvider.System, NullLogger<ZipBackupStrategy>.Instance)],
            history, TimeProvider.System, NullLogger<BackupOrchestrator>.Instance);

        var installer = new BdsUpdateInstaller(
            _store,
            new ShutdownBackups(orchestrator, TimeProvider.System, NullLogger<ShutdownBackups>.Instance),
            new ServerVersionLog(_paths, NullLogger<ServerVersionLog>.Instance),
            TimeProvider.System,
            NullLogger<BdsUpdateInstaller>.Instance);

        var healthy = HealthyOnNextStartAsync(afterProcessCount: 1);
        await installer.ApplyAsync(_supervisor, TheNextBuild(), []);
        await healthy;

        var record = Assert.Single(await history.ReadAsync("survival"));
        Assert.Equal(BackupReason.Shutdown, record.Reason);
        Assert.Contains("server update", record.Note);
    }

    // Backup-on-shutdown defaults off, and an instance that has not asked for
    // one must not get a pause in the middle of its update.
    [Fact]
    public async Task An_instance_that_did_not_ask_for_a_backup_does_not_get_one()
    {
        _supervisor.Definition.Backup.OnShutdown.OnServerUpdate = false;

        await Installer().ApplyAsync(_supervisor, TheNextBuild(), []);

        Assert.Empty(await new BackupHistory(_paths).ReadAsync("survival"));
    }
}
