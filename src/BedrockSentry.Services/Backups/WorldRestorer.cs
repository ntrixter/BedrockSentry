using System.Globalization;
using System.IO.Compression;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>What a restore did, for the UI to report rather than imply.</summary>
public sealed record RestoreResult(
    string ArchiveFileName,
    bool ServerWasRunning,
    bool ServerRestarted,
    SafetyBackup Safety,
    int FilesRestored,
    int ConfigFilesRestored = 0,
    string? RestartRefusal = null)
{
    /// <summary>
    /// The whole sentence the panel shows after a restore.
    /// </summary>
    /// <remarks>
    /// Here rather than in <c>BackupsTab.razor</c> so a test can reach it. The
    /// restart half has three outcomes and had two sentences: a start refused by
    /// the pre-flight (<c>T151</c>) rendered as "the server is starting again",
    /// which is the one case where the operator most needs to be told otherwise.
    /// A refusal is not a restore failure — the world went back exactly as asked,
    /// and the refusal names its own remedy — so it reads as a third clause on a
    /// successful restore rather than as an error.
    /// </remarks>
    public string Summary()
    {
        var files = $"{FilesRestored} file{(FilesRestored == 1 ? "" : "s")}";

        var config = ConfigFilesRestored > 0
            ? $", plus {ConfigFilesRestored} configuration "
              + $"file{(ConfigFilesRestored == 1 ? "" : "s")}"
            : "";

        // The refusal is already a complete explanation with a remedy, and both
        // producers of one — PortConflictText and OrphanedServer — open it with
        // "not started: ". Reading straight on from "The server was" keeps that
        // one sentence rather than writing a second that could disagree with it.
        // WorldRestorerTests pins the join against the real strings, which is
        // what stops the shared opening from being a convention nothing checks.
        var server = (ServerWasRunning, RestartRefusal) switch
        {
            (false, _) => "The server was stopped and stays stopped.",
            (true, null) => "The server is starting again.",
            (true, var refusal) => $"The server was {refusal}",
        };

        // Was an unconditional "The previous state is in the safety backup above."
        // — false on the one branch where no safety backup is taken (`T168`), which
        // is also the branch where configuration files can be overwritten. The
        // clause comes from the outcome now, so the sentence cannot outrun it.
        return $"Restored {ArchiveFileName} ({files}{config}). " + server + Safety.SummaryClause;
    }
}

/// <summary>
/// A backup the wrapper knows how to put back.
/// </summary>
/// <remarks>
/// Two shapes, one list. The ZIP strategy writes an archive and the VSS strategy
/// writes a folder, and the picker has no business caring which — a restore is
/// "put this state back", and the file layout is the strategy's business
/// (<c>docs/design.md</c> § 8.5). Custom-command backups are in neither, because the
/// wrapper does not know their format and offering to restore one would be
/// offering something it cannot do.
/// </remarks>
public sealed record RestorableBackup(
    string FullName, string Name, DateTimeOffset TakenUtc, long SizeBytes, bool IsFolder)
{
    /// <summary>Which strategy wrote it, in the picker's words.</summary>
    public string Kind => IsFolder ? "snapshot" : "archive";
}

/// <summary>
/// Applies a backup over an instance's world (<c>docs/design.md</c> § 8.5): stop
/// the server if running → safety backup of the current state → apply → restart.
/// </summary>
/// <remarks>
/// Two rules carry the safety of the whole operation.
///
/// The safety backup is always a ZIP, whatever strategies the instance has
/// enabled — it exists so "restored the wrong backup" is itself recoverable,
/// and a safety copy in a format the wrapper cannot restore would be a comfort
/// object, not a safety net. It is taken while the server is stopped, so it is
/// a static copy with nothing mid-write.
///
/// The old world is moved aside, never deleted, until the new one is fully
/// extracted. Extracting over the top would leave files the backup does not
/// contain — database segments written since — mixed into the restored world,
/// which LevelDB reads as corruption. Moving it back is the failure path: a
/// restore that dies halfway leaves the world it found, not half of each.
/// </remarks>
public sealed class WorldRestorer
{
    private readonly BackupHistory _history;
    private readonly TimeProvider _time;
    private readonly ILogger<WorldRestorer> _log;
    private readonly TimeSpan _drainBudget;

    public WorldRestorer(
        BackupHistory history,
        TimeProvider time,
        ILogger<WorldRestorer> log,
        TimeSpan? drainBudget = null)
    {
        _history = history;
        _time = time;
        _log = log;

        // Generous for the D22 reason; the measured stop was 422 ms.
        _drainBudget = drainBudget ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>The archives the picker offers: this instance's own ZIPs, newest first.</summary>
    /// <remarks>
    /// Custom-command backups appear in history for reference but are not
    /// listed here — the wrapper does not know their format, so offering to
    /// restore one would be offering something it cannot do (§ 8.5).
    /// </remarks>
    public IReadOnlyList<RestorableBackup> ListBackups(string instanceId)
    {
        var directory = _history.DirectoryFor(instanceId);

        if (!Directory.Exists(directory))
        {
            return [];
        }

        var archives = Directory
            .EnumerateFiles(directory, $"{ZipBackupStrategy.ArchivePrefix}*{ZipBackupStrategy.ArchiveSuffix}")
            .Select(f => new FileInfo(f))
            .Select(f => new RestorableBackup(
                f.FullName, f.Name, f.LastWriteTimeUtc, f.Length, IsFolder: false));

        var snapshots = Directory
            .EnumerateDirectories(directory, $"{VssBackupStrategy.FolderPrefix}*")
            .Select(d => new DirectoryInfo(d))
            .Select(d => new RestorableBackup(
                d.FullName, d.Name, d.LastWriteTimeUtc, SizeOf(d), IsFolder: true));

        return archives
            .Concat(snapshots)
            .OrderByDescending(b => b.TakenUtc)
            .ToList();
    }

    /// <summary>
    /// A folder's total size, for the picker's one number.
    /// </summary>
    /// <remarks>
    /// Best effort. A file that vanishes mid-walk — a prune running alongside a
    /// page load — is worth showing a smaller number for, not worth failing the
    /// whole list over.
    /// </remarks>
    private static long SizeOf(DirectoryInfo folder)
    {
        try
        {
            return folder
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>How many configuration files a backup carries, for the picker.</summary>
    public int CountConfigFiles(string backupPath)
    {
        try
        {
            if (Directory.Exists(backupPath))
            {
                var folder = Path.Combine(backupPath, VssBackupStrategy.ConfigFolder);

                return Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder).Count(
                        f => ServerConfigFiles.FileNameOf(
                            ServerConfigFiles.EntryFor(Path.GetFileName(f))) is not null)
                    : 0;
            }

            using var zip = ZipFile.OpenRead(backupPath);
            return zip.Entries.Count(e => ServerConfigFiles.FileNameOf(e.FullName) is not null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return 0;
        }
    }

    /// <param name="restoreConfig">
    /// Also put back <c>server.properties</c>, the allowlist and the permissions
    /// file. Off by default and deliberately a separate decision from restoring
    /// the world: an old <c>server.properties</c> can move the port, which is
    /// the one change that makes a server unreachable rather than merely wrong.
    /// </param>
    public async Task<RestoreResult> RestoreAsync(
        InstanceSupervisor supervisor,
        string archivePath,
        bool restoreConfig = false,
        CancellationToken cancellationToken = default)
    {
        // The path must be one of this instance's own archives, not whatever
        // string reached the endpoint — a picker is not an authorisation.
        var archive = ListBackups(supervisor.InstanceId)
            .FirstOrDefault(f => string.Equals(f.FullName, Path.GetFullPath(archivePath), StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException(
                $"'{Path.GetFileName(archivePath)}' is not one of this instance's backups.");

        // Before the stop, deliberately (`T168`). A restore that is going to be
        // refused must not first take a live server down to say so.
        //
        // The second half of this used to read "and the answer cannot change in
        // between: the refusal only arises with an empty world, which means a server
        // that has never run". THAT IS FALSE, and reaching the refusal is what shows
        // it: with the server RUNNING and the world folder empty, by swapping in a
        // stub binary. A running server and an empty world are not
        // mutually exclusive, so the ordering is not justified by the states being
        // impossible to leave. It is justified by the ordering alone: ask first, and
        // a refusal costs nobody their uptime.
        // The stale claim survived because the clause it guards passed either way,
        // which is the `T176` shape — a comment asserting an invariant that is simply
        // not true, in code whose behaviour is correct.
        var need = NeedFor(supervisor, restoreConfig);

        if (need is SafetyBackupNeed.ImpossibleForConfig)
        {
            throw new InvalidOperationException(SafetyBackupRule.Refusal);
        }

        var wasRunning = supervisor.Status.State is InstanceState.Running;

        if (wasRunning)
        {
            supervisor.Console.Append(
                ConsoleOrigin.Notice,
                $"restoring {archive.Name}; stopping the server first",
                _time.GetUtcNow());

            await supervisor
                .StopAsync(
                    GracefulStopRequest.Immediate(StopReason.Restore, _drainBudget),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var worldsDirectory = Path.Combine(supervisor.WorkingDirectory, "worlds");
        var stamp = _time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        SafetyBackup safety;
        int restored;

        try
        {
            safety = need is SafetyBackupNeed.Required
                ? await TakeSafetyBackupAsync(
                        supervisor, worldsDirectory, archive.Name, cancellationToken)
                    .ConfigureAwait(false)
                : SafetyBackup.NothingToProtect;

            var setAside = $"{worldsDirectory}.restoring-{stamp}";
            var hadWorld = Directory.Exists(worldsDirectory);

            if (hadWorld)
            {
                MoveDirectory(worldsDirectory, setAside);
            }

            try
            {
                restored = Extract(archive, worldsDirectory);
            }
            catch
            {
                // The failure path: put back what was there. The half-extracted
                // directory is deleted first so the move cannot collide with it
                // — and TryDeleteDirectory must not throw, or the put-back below
                // never runs and the only copy of the world is left sitting in a
                // .restoring- folder nobody is looking for.
                TryDeleteDirectory(worldsDirectory);

                if (hadWorld)
                {
                    MoveDirectory(setAside, worldsDirectory);
                }

                _log.LogError(
                    "Restoring {Archive} onto {InstanceId} failed; the previous world was put back",
                    archive.Name, supervisor.InstanceId);
                throw;
            }

            // Only after a complete extraction does the old world go.
            if (hadWorld)
            {
                TryDeleteDirectory(setAside);
            }
        }
        catch
        {
            // The world is as it was, so the server should be too. Every failure
            // above this point leaves the previous world in place, and a restore
            // that failed and also left the server down would be a second outage
            // on top of whatever went wrong.
            if (wasRunning)
            {
                try
                {
                    await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);

                    supervisor.Console.Append(
                        ConsoleOrigin.Notice,
                        "the restore failed; the world was left as it was and the server is starting again",
                        _time.GetUtcNow());
                }
                catch (Exception restartFailure)
                {
                    // Swallowed so it cannot replace the real reason the restore
                    // failed, which is the more useful of the two.
                    _log.LogError(
                        restartFailure,
                        "Could not restart {InstanceId} after a failed restore", supervisor.InstanceId);
                }
            }

            throw;
        }

        var configRestored = restoreConfig
            ? RestoreConfig(archive, supervisor.WorkingDirectory)
            : 0;

        supervisor.Console.Append(
            ConsoleOrigin.Notice,
            $"world restored from {archive.Name} ({restored} files"
            + (configRestored > 0 ? $", plus {configRestored} configuration file{(configRestored == 1 ? "" : "s")}" : "")
            + ")"
            // Same rule as the summary, from the same outcome rather than from a
            // second copy of the sentence (`T168`).
            + (safety.Exists
                ? "; the previous state is in the safety backup"
                : "; there was no world here before, so no safety backup was taken"),
            _time.GetUtcNow());

        _log.LogInformation(
            "Restored {Archive} onto {InstanceId}: {Files} files", archive.Name, supervisor.InstanceId, restored);

        var restarted = false;
        string? refusal = null;

        if (wasRunning)
        {
            await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);

            // `T151`: a start can be refused before a process exists — the port
            // is held, or the world already has a process on it — and the
            // supervisor faults rather than throwing, so the call returning is
            // not evidence that anything started. Reading the state back is the
            // only way to tell a restart from a refusal, and it is the same
            // check `BdsUpdateInstaller` makes for the same reason. Saying "the
            // server is starting again" over a refusal tells the operator the
            // opposite of the truth at the exact moment they are watching for it.
            refusal = supervisor.Status is
                { State: InstanceState.Faulted, FaultReason: { } reason }
                ? reason
                : null;

            restarted = refusal is null;
        }

        return new RestoreResult(
            archive.Name, wasRunning, restarted, safety, restored, configRestored, refusal);
    }

    /// <summary>
    /// The pre-restore copy, recorded in history like any other backup so the
    /// picker itself offers it — undoing a restore is choosing the safety
    /// archive in the same list.
    /// </summary>
    private async Task<SafetyBackup> TakeSafetyBackupAsync(
        InstanceSupervisor supervisor,
        string worldsDirectory,
        string aboutToRestore,
        CancellationToken cancellationToken)
    {
        var startedUtc = _time.GetUtcNow();
        var artifactDirectory = _history.DirectoryFor(supervisor.InstanceId);
        Directory.CreateDirectory(artifactDirectory);

        var manifest = Directory.Exists(worldsDirectory)
            ? Directory
                .EnumerateFiles(worldsDirectory, "*", SearchOption.AllDirectories)
                .Select(file => new SaveManifestEntry(
                    Path.GetRelativePath(worldsDirectory, file).Replace('\\', '/'),
                    new FileInfo(file).Length))
                .ToList()
            : [];

        if (manifest.Count == 0)
        {
            // Unreachable from RestoreAsync, which asks SafetyBackupRule first and
            // either skips this method or refuses the restore outright. Kept as a
            // guard rather than deleted: the ZIP strategy below refuses an empty
            // manifest anyway, and a throw here names the reason where that one
            // would arrive as "the safety backup failed".
            throw new InvalidOperationException(
                "No safety backup was needed and this should not have been asked for: "
                + "there is no world to archive.");
        }

        // Always ZIP, always keep-all: the safety copy must be restorable by
        // the wrapper, and no retention rule gets to age out the undo button.
        var zip = new ZipBackupStrategy(_time, Microsoft.Extensions.Logging.Abstractions.NullLogger<ZipBackupStrategy>.Instance);

        var context = new BackupContext(
            supervisor.InstanceId,
            supervisor.Definition.Name,
            supervisor.WorkingDirectory,
            worldsDirectory,
            manifest,
            artifactDirectory,
            new BackupSettings { Zip = new() { Enabled = true, Retention = new() } },
            BackupReason.Safety,
            $"Before restoring {aboutToRestore}",
            startedUtc);

        var result = await zip.RunAsync(context, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || result.ArtifactPath is null)
        {
            // No safety copy, no restore. Proceeding would make the one
            // operation built to recover data into the one that loses it.
            throw new InvalidOperationException(
                $"The safety backup failed, so the restore was not started: {result.Detail}");
        }

        await _history.AppendAsync(
            supervisor.InstanceId,
            new BackupRecord(
                startedUtc, _time.GetUtcNow(), BackupReason.Safety,
                $"Before restoring {aboutToRestore}",
                Skipped: false, SkipReason: null, Succeeded: true, [result]),
            cancellationToken)
            .ConfigureAwait(false);

        return SafetyBackup.Taken(result.ArtifactPath);
    }

    /// <summary>
    /// Whether restoring onto <paramref name="supervisor"/> right now would take a
    /// safety backup (<c>T168</c>). The question the dialog asks before offering the
    /// button, and the one <see cref="RestoreAsync"/> asks before touching anything.
    /// </summary>
    /// <remarks>
    /// One method for both on purpose: they want the same answer, which is what
    /// separates this from the kind of sharing <c>T151</c> clause 6 was. The
    /// configuration files are counted in the <em>instance</em> directory — what
    /// would be overwritten — not in the archive, which is what
    /// <see cref="CountConfigFiles"/> answers.
    /// </remarks>
    public SafetyBackupNeed NeedFor(InstanceSupervisor supervisor, bool restoringConfig)
    {
        var worldsDirectory = Path.Combine(supervisor.WorkingDirectory, "worlds");

        var worldHasFiles = Directory.Exists(worldsDirectory)
            && Directory.EnumerateFiles(worldsDirectory, "*", SearchOption.AllDirectories).Any();

        var configPresent = ServerConfigFiles.Present(supervisor.WorkingDirectory).Any();

        return SafetyBackupRule.For(worldHasFiles, configPresent, restoringConfig);
    }

    /// <summary>
    /// Writes the archive's configuration files back into the instance root.
    /// </summary>
    /// <remarks>
    /// Deliberately after the world and outside the move-aside dance: these are four
    /// small files, and a failure here is worth reporting rather than worth unwinding
    /// a completed world restore for.
    ///
    /// This used to add "whose previous contents are already in the safety backup",
    /// which was the justification and was not always true (<c>T168</c>). The safety
    /// archive does carry them — <c>ZipBackupStrategy.AddConfigAsync</c> puts them
    /// under <c>_serverconfig/</c> — but it is only taken when there is a world to
    /// archive, and these files live in the instance directory rather than under
    /// <c>worlds/</c>. So on an empty world this ran with no copy of what it
    /// overwrote. <see cref="SafetyBackupRule"/> now refuses that combination before
    /// anything is touched, which is what makes the sentence true rather than the
    /// sentence making it true.
    /// </remarks>
    private int RestoreConfig(RestorableBackup backup, string instanceDirectory)
    {
        var written = 0;

        foreach (var (name, read) in ConfigIn(backup))
        {
            try
            {
                read(Path.Combine(instanceDirectory, name));
                written++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(
                    ex, "Could not restore {File} from {Backup}", name, backup.Name);
            }
        }

        return written;
    }

    /// <summary>
    /// The configuration files a backup carries, and how to write each one out.
    /// </summary>
    /// <remarks>
    /// A pair rather than two branches through <see cref="RestoreConfig"/>: the
    /// per-file error handling and the counting are identical for both shapes,
    /// and only the reading differs.
    /// </remarks>
    private static IEnumerable<(string Name, Action<string> WriteTo)> ConfigIn(RestorableBackup backup)
    {
        if (backup.IsFolder)
        {
            var folder = Path.Combine(backup.FullName, VssBackupStrategy.ConfigFolder);

            if (!Directory.Exists(folder))
            {
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                // Back through the same name check the archive path uses, so a
                // file somebody dropped in there cannot be written anywhere the
                // archive form would refuse to write it.
                if (ServerConfigFiles.FileNameOf(
                        ServerConfigFiles.EntryFor(Path.GetFileName(file))) is not { } name)
                {
                    continue;
                }

                yield return (name, target => File.Copy(file, target, overwrite: true));
            }

            yield break;
        }

        using var zip = ZipFile.OpenRead(backup.FullName);

        foreach (var entry in zip.Entries)
        {
            if (ServerConfigFiles.FileNameOf(entry.FullName) is not { } name)
            {
                continue;
            }

            yield return (name, target => entry.ExtractToFile(target, overwrite: true));
        }
    }

    private static int Extract(RestorableBackup backup, string worldsDirectory) =>
        backup.IsFolder
            ? CopyFolder(backup.FullName, worldsDirectory)
            : ExtractArchive(backup.FullName, worldsDirectory);

    /// <summary>
    /// Copies a VSS snapshot's world back.
    /// </summary>
    /// <remarks>
    /// Same two rules the archive path follows and for the same reasons: the
    /// reserved configuration folder is skipped, because it is restored — if at
    /// all — into the instance root and would otherwise appear as a world called
    /// <c>_serverconfig</c>; and every target is checked to be inside the worlds
    /// directory, because the wrapper's own snapshots cannot contain a traversal
    /// and the one that can is the folder somebody placed there by hand.
    /// </remarks>
    private static int CopyFolder(string snapshotPath, string worldsDirectory)
    {
        Directory.CreateDirectory(worldsDirectory);

        var root = Path.GetFullPath(worldsDirectory);
        var source = Path.GetFullPath(snapshotPath);
        var configFolder = Path.Combine(source, VssBackupStrategy.ConfigFolder);
        var restored = 0;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(configFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(source, file);
            var target = Path.GetFullPath(Path.Combine(root, relative));

            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"'{relative}' points outside the worlds directory. "
                    + "This snapshot was not written by BedrockSentry.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            restored++;
        }

        return restored;
    }

    private static int ExtractArchive(string archivePath, string worldsDirectory)
    {
        Directory.CreateDirectory(worldsDirectory);
        var root = Path.GetFullPath(worldsDirectory);
        var restored = 0;

        using var zip = ZipFile.OpenRead(archivePath);

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            // Configuration lives in its own namespace and is restored, if at
            // all, by the caller writing to the instance root — never into
            // worlds, where it would appear as a world folder called
            // "_serverconfig".
            if (ServerConfigFiles.IsConfigEntry(entry.FullName))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));

            // Zip-slip: an entry named "../../anything" must never write outside
            // worlds. Our own archives cannot contain one, which is exactly why
            // the check exists — the dangerous file is the one somebody placed.
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The archive entry '{entry.FullName}' points outside the worlds directory. "
                    + "This is not an archive the wrapper wrote; nothing was restored from it.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
            restored++;
        }

        return restored;
    }

    /// <summary>
    /// Renames a directory, retrying while Windows still has it.
    /// </summary>
    /// <remarks>
    /// The server was stopped moments ago, and a stopped process is not the same
    /// thing as a released handle: Windows tears the process down
    /// asynchronously, and a virus scanner opening a world file it just saw
    /// change will hold one too. <c>Directory.Move</c> refuses while anything in
    /// the tree is open, and the world directory is the one place in this
    /// codebase where losing that race matters — it happens after the safety
    /// backup and before the extract, so a bare throw would leave a stopped
    /// server and an unexplained failure.
    ///
    /// Unix has no such rule, so this can only ever fail on the platform the
    /// wrapper ships to, and no test on Linux can prove it fixed. <c>T17</c>
    /// covers it.
    /// </remarks>
    private void MoveDirectory(string source, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex)
                when (attempt < 20 && ex is UnauthorizedAccessException or IOException)
            {
                if (attempt == 0)
                {
                    _log.LogInformation(
                        "Waiting for {Source} to be released before moving it", source);
                }

                Thread.Sleep(100);
            }
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never rethrown, and the UnauthorizedAccessException half is the
            // reason why: this is called on the failure path immediately before
            // the old world is moved back, so throwing here would skip the
            // put-back and leave the only copy of somebody's world sitting in a
            // .restoring- folder they have no reason to look in. A read-only
            // file anywhere in the tree is enough to raise it.
            _log.LogWarning(ex, "Could not remove {Path} after the restore", path);
        }
    }
}
