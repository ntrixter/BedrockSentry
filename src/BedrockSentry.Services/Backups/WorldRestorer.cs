using System.Globalization;
using System.IO.Compression;
using BedrockSentry.Core.Addons;
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
    string? RestartRefusal = null,
    int? WorldFilesReplaced = 0,
    AddonsRestored Addons = default,
    CopiedProperties Properties = CopiedProperties.NotRestored,
    string? FromServer = null)
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
        // `T236`. "(1 file)" was the only signal that a restore had put back one
        // file over a world of eight, and it rendered as an ordinary fact in the
        // success style. Said against what it replaced, the same number carries
        // the whole of what somebody needs.
        //
        // A FACT AND NOT A VERDICT, which is why there is no threshold and no
        // warning word: a legitimately older backup is smaller too, and nothing
        // here can tell that from a damaged one. `DamagedBackups` refuses the case
        // the wrapper can be certain about; this covers the rest by saying what
        // happened rather than by guessing at it.
        //
        // THREE STATES, and the third is `T240`: a world directory that could not
        // be counted is NOT a world of zero files. Comparing against zero made the
        // shortfall false and silenced this sentence in exactly the case `T234`
        // exists for, so an uncountable world says so rather than saying nothing.
        var shortfall = WorldFilesReplaced switch
        {
            null => " How many files it replaced could not be read.",
            { } replaced when FilesRestored < replaced => $" It replaced a world of {replaced} files.",
            _ => "",
        };

        // `D97`. Where the add-ons came from, when any world had some to settle.
        var addons = Addons.Clause() is { Length: > 0 } clause ? " " + clause : "";

        // `D127`. The configuration came across without this server's name and ports.
        var properties = Properties switch
        {
            CopiedProperties.NotRestored => "",
            CopiedProperties.Rewritten => " This server kept its own name and ports.",
            CopiedProperties.LeftAsTheOriginals =>
                " This server's own name and ports could not be kept, so it has the backup's; "
                + "check them on its Server properties tab.",
            _ => throw new ArgumentOutOfRangeException(nameof(Properties), Properties, "Unhandled properties state."),
        };

        // `T359` clause 4. Two servers' worlds can share a name, so a backup taken from
        // another server (`D89`) names it.
        var from = FromServer is { } source ? $" from {source}" : "";

        return $"Restored {ArchiveFileName}{from} ({files}{config})."
               + properties + shortfall + addons + " " + server + Safety.SummaryClause;
    }
}

/// <summary>How a backup is stored on disk (<c>D110</c>).</summary>
/// <remarks>
/// Three, so not a flag: an imported <c>.mcworld</c> is a ZIP file that holds a world at its
/// top and no configuration, and reading it as an archive of the <c>worlds</c> folder would put
/// its <c>db</c> folder where a world belongs (<c>T176</c>).
/// </remarks>
public enum BackupFileFormat
{
    /// <summary>A ZIP archive, <c>backup-{stamp}.zip</c>.</summary>
    Zip,

    /// <summary>A folder copy, <c>folder-{stamp}</c>.</summary>
    Folder,

    /// <summary>A Minecraft world export, <c>backup-{stamp}.mcworld</c>, only ever imported.</summary>
    McWorld,
}

/// <summary>
/// A backup the wrapper knows how to put back.
/// </summary>
/// <remarks>
/// Two shapes, one list. The ZIP strategy writes an archive and the folder-copy
/// strategy writes a folder, and the picker has no business caring which — a
/// restore is "put this state back", and the file layout is the strategy's
/// business (<c>docs/design.md</c> § 8.5). Custom-command backups are in neither,
/// because the wrapper does not know their format and offering to restore one
/// would be offering something it cannot do.
/// </remarks>
public sealed record RestorableBackup(
    string FullName, string Name, DateTimeOffset TakenUtc, long SizeBytes, BackupFileFormat Format)
{
    /// <summary>Whether it is a folder rather than a file.</summary>
    public bool IsFolder => Format is BackupFileFormat.Folder;

    /// <summary>Which format wrote it, in the picker's words.</summary>
    /// <remarks>
    /// <strong>This said "snapshot", which is the `D66` mislabel one level
    /// down.</strong> Nothing in the restore picker is a snapshot: a shadow copy is
    /// released as soon as the backup that used it finishes, so every row here is a
    /// file or a folder on an ordinary disk. Somebody choosing between "archive"
    /// and "snapshot" would reasonably read the second as the cheap one to keep.
    /// </remarks>
    public string Kind => KindOf(Format);

    /// <summary>What <see cref="Kind"/> calls a ZIP archive.</summary>
    /// <remarks>
    /// Constants because the Backups tab also names a row whose file is gone, from
    /// the strategy that wrote it, and two spellings of one label is how a pruned
    /// row and its surviving twin come to disagree (<c>T282</c>). Named for the file
    /// type an operator sees on disk (<c>D110</c>).
    /// </remarks>
    public const string ZipKind = "zip";

    /// <summary>What <see cref="Kind"/> calls a folder copy.</summary>
    public const string FolderKind = "folder";

    /// <summary>What <see cref="Kind"/> calls an imported <c>.mcworld</c>.</summary>
    public const string McWorldKind = "mcworld";

    /// <summary>The label for a format.</summary>
    public static string KindOf(BackupFileFormat format) => format switch
    {
        BackupFileFormat.Zip => ZipKind,
        BackupFileFormat.Folder => FolderKind,
        BackupFileFormat.McWorld => McWorldKind,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unhandled backup format."),
    };
}

/// <summary>How many backups a restore could use, and what they occupy summed.</summary>
/// <remarks>
/// One statement of the figure the instance tab's Kept on disk card shows and the
/// all-instances Backups page repeats (<c>T276</c>). The page counted
/// <c>backup-*.zip</c> alone from its own enumeration, so the two pages gave two
/// storage figures for one server, and a server backed up by folder copies read
/// nothing at all.
/// </remarks>
public readonly record struct KeptOnDisk(int Count, long Bytes)
{
    /// <summary>The figure for a listing from <see cref="WorldRestorer.ListBackups"/>.</summary>
    public static KeptOnDisk Of(IReadOnlyList<RestorableBackup> backups)
    {
        ArgumentNullException.ThrowIfNull(backups);
        return new KeptOnDisk(backups.Count, backups.Sum(b => b.SizeBytes));
    }
}

/// <summary>
/// How far a restore had got when a backup turned out to hold no world.
/// </summary>
/// <remarks>
/// Named rather than a flag because the three are not degrees of one thing: they
/// differ in what the refusal has already cost, and one of them
/// (<see cref="IntoANewServer"/>) leaves something behind that the other two do
/// not. <see cref="WorldRestorer.EmptySnapshotRefusal"/> has the sentences.
/// </remarks>
internal enum EmptySnapshotStage
{
    /// <summary>Nothing has been touched: the pre-flight caught it (<c>T168</c>).</summary>
    BeforeTheStop,

    /// <summary>The server is down and the world has been moved aside.</summary>
    AfterTheStop,

    /// <summary>A new server had already been created for the copy (<c>D40</c>).</summary>
    IntoANewServer,

    /// <summary>
    /// The server is down and its world untouched: a sync extracts beside the world
    /// before swapping it in (<c>D103</c>).
    /// </summary>
    BesideTheWorld,

    /// <summary>
    /// Extracted into a throwaway server to read one area out of (<c>D108</c>); the
    /// server's own world is untouched.
    /// </summary>
    IntoAThrowaway,
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

    /// <summary>
    /// The backups the picker offers: this instance's own ZIPs, folder copies and imported
    /// worlds, newest first.
    /// </summary>
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
                f.FullName,
                f.Name,
                TakenAt(f.Name, ZipBackupStrategy.ArchivePrefix, ZipBackupStrategy.ArchiveSuffix, f.LastWriteTimeUtc),
                LengthOf(f),
                Format: BackupFileFormat.Zip));

        // Imported only (`D110`): a world exported from Minecraft, kept as it came.
        var worlds = Directory
            .EnumerateFiles(directory, $"{ZipBackupStrategy.ArchivePrefix}*{ZipBackupStrategy.McWorldSuffix}")
            .Select(f => new FileInfo(f))
            .Select(f => new RestorableBackup(
                f.FullName,
                f.Name,
                TakenAt(f.Name, ZipBackupStrategy.ArchivePrefix, ZipBackupStrategy.McWorldSuffix, f.LastWriteTimeUtc),
                LengthOf(f),
                Format: BackupFileFormat.McWorld));

        var snapshots = Directory
            .EnumerateDirectories(directory, $"{FolderBackupStrategy.FolderPrefix}*")
            .Select(d => new DirectoryInfo(d))
            .Select(d => new RestorableBackup(
                d.FullName,
                d.Name,
                TakenAt(d.Name, FolderBackupStrategy.FolderPrefix, suffix: "", d.LastWriteTimeUtc),
                FolderBackupStrategy.SizeOf(d),
                Format: BackupFileFormat.Folder));

        return archives
            .Concat(worlds)
            .Concat(snapshots)
            .OrderByDescending(b => b.TakenUtc)
            .ToList();
    }

    /// <summary>
    /// When a backup was taken, from the stamp in its NAME.
    /// </summary>
    /// <remarks>
    /// <para><c>T198</c>'s rule, which reached retention and not this picker. The
    /// filesystem timestamp moves whenever anything touches the artifact, and the
    /// case that matters is not hypothetical: a folder snapshot that retention
    /// began deleting and could not finish has its clock reset to the moment of the
    /// prune, so a gutted shell sorted FIRST and presented as the newest backup,
    /// with a working Restore button (`T229`). The name does not move.</para>
    ///
    /// <para>Falls back to the clock for anything whose name does not carry a
    /// stamp, which is a hand-placed archive rather than one of ours. Those are
    /// listed — somebody put them there deliberately — and dating them by the only
    /// evidence available is better than hiding them.</para>
    /// </remarks>
    private static DateTime TakenAt(string name, string prefix, string suffix, DateTime fromTheClock) =>
        BackupRetentionPlan.TryReadStamp(name, prefix, suffix, out var stamp) ? stamp : fromTheClock;

    /// <summary>
    /// The size of an archive that may already be gone.
    /// </summary>
    /// <remarks>
    /// Retention prunes archives out of the directory this lists, so a backup
    /// finishing mid-listing can delete one between the enumeration and this
    /// read, and <see cref="FileInfo.Length"/> throws for a file that has gone.
    /// A pruned archive counts as nothing rather than taking the page down with
    /// it. The all-instances Backups page carried this rule before it read this
    /// listing (<c>T276</c>); it moved here rather than being lost.
    /// </remarks>
    internal static long LengthOf(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Opens a backup and says what is in it, or why it could not be opened
    /// (<c>T251</c>).
    /// </summary>
    /// <remarks>
    /// <para><strong>It used to return the count alone</strong>, and a backup that
    /// could not be opened returned zero — which is also what an ordinary backup
    /// carrying no configuration returns. So the dialog rendered its ordinary shape
    /// over an ACL-denied archive, with the tickbox absent and the confirm live, and
    /// the failure arrived after the server was down. <c>T176</c> in a count rather
    /// than in a return value: the one that should refuse looked exactly like the
    /// one that is fine.</para>
    ///
    /// <para><c>T244</c>'s half of this survives and is the reason the filter is
    /// wide: this is called one line before <see cref="NeedFor(InstanceSupervisor,
    /// bool)"/>, so an unguarded throw here killed the circuit and took every
    /// backup row's Restore button with it, with no involvement of the world at
    /// all. Catching is still right; what was missing is that the caught state had
    /// somewhere to go.</para>
    /// </remarks>
    public BackupContents ReadContents(string backupPath)
    {
        try
        {
            if (Directory.Exists(backupPath))
            {
                // Listed first, and the listing is what matters: Directory.Exists
                // answers false for a folder it may not traverse, so a denied snapshot
                // is indistinguishable from one carrying no configuration — the swallow
                // File.Exists does, one directory up (`T246`). Listing the snapshot
                // itself throws where that is the case, and is the only thing that
                // separates them.
                _ = SnapshotLister(backupPath).ToList();

                var paths = FolderPaths(backupPath);
                var folderLayout = NewServerWorld.ForRestore(paths);

                var folderRecords = RecordsIn(
                    paths, folderLayout, p => File.ReadAllText(Path.Combine(backupPath, p)));

                return new BackupContents(
                    BackupReadState.Read,
                    paths.Count(p => NewServerWorld.ConfigNameOf(p, folderLayout) is not null),
                    AddonFiles: WorldFoldersIn(backupPath, folderLayout)
                        .Sum(world => AddonItemsIn(world).Sum(FileCountOf)),
                    BdsVersion: folderRecords.Version,
                    Origin: folderRecords.Origin);
            }

            using var zip = ZipFile.OpenRead(backupPath);

            var entries = ArchivePaths(zip);
            var layout = ArchiveLayout(backupPath, entries);

            var records = RecordsIn(entries, layout, entry =>
            {
                using var reader = new StreamReader(
                    zip.Entries.First(e => Normalised(e) == entry).Open());
                return reader.ReadToEnd();
            });

            return new BackupContents(
                BackupReadState.Read,
                entries.Count(e => NewServerWorld.ConfigNameOf(e, layout) is not null),
                AddonFiles: entries.Count(e =>
                    NewServerWorld.WorldsPathOf(e, layout, NewServerWorld.DefaultLevelName) is { } relative
                    && WorldAddonFiles.IsAddonPath(relative)),
                BdsVersion: records.Version,
                Origin: records.Origin);
        }
        // BackupContents.From is both the filter and the mapping, so which
        // exceptions count as a read failure is stated once. Anything it does not
        // recognise fails the filter and propagates, which is what a bug should do.
        catch (Exception ex) when (BackupContents.From(ex) is { } failure)
        {
            return failure;
        }
    }

    /// <summary>
    /// What a backup's record files say (<c>D110</c>, <c>D89</c>): the build and the server it
    /// was taken from, each null when it carries none or one that does not parse.
    /// </summary>
    private static (string? Version, BackupOrigin? Origin) RecordsIn(
        IEnumerable<string> paths, WorldFileLayout layout, Func<string, string> read)
    {
        string? version = null;
        BackupOrigin? origin = null;

        foreach (var path in paths)
        {
            switch (NewServerWorld.RecordNameOf(path, layout))
            {
                case NewServerWorld.VersionFile:
                    version = read(path).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                    break;

                case NewServerWorld.OriginFile:
                    origin = BackupOrigin.Parse(read(path));
                    break;
            }
        }

        return (version, origin);
    }

    /// <param name="restoreConfig">
    /// Also put back <c>server.properties</c>, the allowlist and the permissions
    /// file. Off by default and deliberately a separate decision from restoring
    /// the world: an old <c>server.properties</c> can move the port, which is
    /// the one change that makes a server unreachable rather than merely wrong.
    /// </param>
    /// <param name="restoreAddons">
    /// Put back the add-ons the backup carries for each world (<c>D97</c>). When false,
    /// or for a world the backup carries none for, the world keeps the add-ons the
    /// server has now: its two pack folders and its pack lists come across from the
    /// world being replaced. On by default, which is a faithful restore.
    /// </param>
    /// <param name="progress">Told each step as it begins, and when the backup's world is in place (<c>D111</c>).</param>
    public async Task<RestoreResult> RestoreAsync(
        InstanceSupervisor supervisor,
        string archivePath,
        bool restoreConfig = false,
        bool restoreAddons = true,
        CancellationToken cancellationToken = default,
        IProgress<WorldRestoreReport>? progress = null)
    {
        // The path must be one of this instance's own archives, not whatever
        // string reached the endpoint — a picker is not an authorisation.
        var archive = ListBackups(supervisor.InstanceId)
            .FirstOrDefault(f => string.Equals(f.FullName, Path.GetFullPath(archivePath), StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException(
                $"'{Path.GetFileName(archivePath)}' is not one of this instance's backups.");

        // `T251`, and first among these because it is the only one that can tell
        // whether the others were answered at all. Four guards stood here and not
        // one asked whether the backup could be opened, so a denied archive passed
        // every one of them, the dialog offered an enabled Restore naming nothing,
        // and the failure landed after the server was down. It is also what stops
        // the folder question below reading a denied snapshot as an empty one --
        // the wrong sentence, about a folder that is not empty.
        var contents = ReadContents(archive.FullName);

        if (!contents.CanRestore)
        {
            throw new InvalidOperationException(contents.Refusal(archive.Name));
        }

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
        // Also before the stop, and for `T168`'s reason rather than a new one: a
        // folder snapshot holding no world files cannot be restored, and finding
        // that out after the server is down and the world is moved aside costs
        // uptime for an answer that was available from the disk all along. The copy
        // refuses too -- that is the guard that has to hold, since anything could
        // empty the folder between here and there -- but by then the world has been
        // moved and has to be moved back.
        if (archive.IsFolder && !HoldsAWorld(archive.FullName))
        {
            throw new InvalidOperationException(EmptySnapshotRefusal(archive.Name));
        }

        // `T236`. The guard above asks whether there is anything to copy, and ONE
        // surviving file answers yes -- so a folder the prune gutted down to a
        // single 16-byte world file passed it, replaced a live world of eight, and
        // reported "(1 file)" in the success style.
        //
        // Nothing on disk distinguishes that from a small intact backup, because a
        // world is opaque bytes here. What does distinguish it is the wrapper's own
        // record of what it broke, so the question is asked of the history rather
        // than of the folder. `DamagedBackups` carries the rule and the sentence.
        //
        // Before the stop for `T168`'s reason, like the guard above it: the answer
        // was available without anybody losing uptime for it.
        if (DamagedBackups.WasDestroyed(
                await _history.ReadAsync(supervisor.InstanceId, cancellationToken),
                archive.Name))
        {
            throw new InvalidOperationException(DamagedBackups.Refusal(archive.Name));
        }

        // Milestone 18.5: the restore copies the server's add-ons out of the world it
        // replaces and writes the configuration beside it, both as this service. A
        // link the server made in either place would carry the copy elsewhere. Asked
        // here so a refusal costs no outage (`T168`), and again once the server is
        // stopped. The development pack folders are the administrator's, and are left.
        if (LinkInTheWay(supervisor.WorkingDirectory) is { } link)
        {
            throw new InvalidOperationException(ServerTree.Refusal(link));
        }

        // Claimed from here to the end (`ClaimText.Restore`): a sync or an update
        // that began between this restore's stop and its start would run on a world
        // half replaced, and a scheduled restart waits for it rather than landing there.
        using var claim = await supervisor
            .ClaimAsync(ClaimText.Restore(archive.Name), cancellationToken)
            .ConfigureAwait(false);

        var need = NeedFor(supervisor, restoreConfig);

        // `T244`, beside the guard below and for `T168`'s reason: before the stop, so a
        // world nobody can read costs an explanation rather than an outage.
        if (need is SafetyBackupNeed.WorldUnreadable)
        {
            throw new InvalidOperationException(SafetyBackupRule.UnreadableWorldRefusal);
        }

        if (need is SafetyBackupNeed.ImpossibleForConfig)
        {
            throw new InvalidOperationException(SafetyBackupRule.Refusal);
        }

        var wasRunning = supervisor.Status.State is InstanceState.Running;

        if (wasRunning)
        {
            progress?.Report(new WorldRestoreReport(WorldRestoreStep.Stopping));

            supervisor.Console.Append(
                ConsoleOrigin.Notice,
                $"restoring {archive.Name}; stopping the server first",
                _time.GetUtcNow());

            await claim
                .StopAsync(
                    GracefulStopRequest.Immediate(StopReason.Restore, _drainBudget),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var worldsDirectory = Path.Combine(supervisor.WorkingDirectory, "worlds");
        var stamp = _time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        SafetyBackup safety;
        int restored;
        int? replaced = 0;
        AddonsRestored addons;

        try
        {
            if (LinkInTheWay(supervisor.WorkingDirectory) is { } made)
            {
                throw new InvalidOperationException(ServerTree.Refusal(made));
            }

            progress?.Report(new WorldRestoreReport(WorldRestoreStep.SafetyBackup));

            safety = need is SafetyBackupNeed.Required
                ? await TakeSafetyBackupAsync(
                        supervisor, worldsDirectory, $"Before restoring {archive.Name}", cancellationToken)
                    .ConfigureAwait(false)
                : SafetyBackup.NothingToProtect;

            var setAside = $"{worldsDirectory}.restoring-{stamp}";
            var hadWorld = Directory.Exists(worldsDirectory);

            // `T236`. Counted here because this is the last moment it can be:
            // the next line moves the world away. It is a FACT for the summary
            // and never a verdict -- a legitimately older backup is smaller too,
            // and nothing here can tell that from a damaged one. What it removes
            // is the arm where a restore that put back one file over a world of
            // eight read exactly like a restore that put back all eight.
            replaced = hadWorld ? WorldFileCount(worldsDirectory) : 0;

            progress?.Report(new WorldRestoreReport(WorldRestoreStep.Replacing));

            if (hadWorld)
            {
                MoveDirectory(worldsDirectory, setAside);
            }

            try
            {
                // An imported world goes into the folder this server loads (`D110`).
                restored = Extract(
                    archive, worldsDirectory, EmptySnapshotStage.AfterTheStop,
                    NewServerLevel.LevelOf(supervisor.WorkingDirectory));

                // Inside the move-aside, so a failure here puts the whole world back
                // rather than leaving one with half its add-ons (`D97`).
                var settled = SettleAddons(worldsDirectory, hadWorld ? setAside : null, restoreAddons);
                addons = settled.Outcome;

                // The counts describe what the backup put back and what it took
                // away (`T236`): add-on files dropped from the backup were not
                // restored, and the server's that were kept were not replaced.
                restored -= settled.FilesLeftOut;
                replaced -= settled.FilesKept;
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

            progress?.Report(new WorldRestoreReport(WorldRestoreStep.Replacing, WorldReplaced: true));
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
                    await claim.StartAsync(cancellationToken).ConfigureAwait(false);

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

        // `D127`. Read before the backup's file goes over it: the ports and name this
        // server is on now stay, wherever the backup came from (`T359` clause 4).
        var identity = restoreConfig ? NewServerLevel.IdentityOf(supervisor.WorkingDirectory) : null;
        var configFiles = restoreConfig
            ? RestoreConfig(archive, supervisor.WorkingDirectory)
            : [];
        var configRestored = configFiles.Count;
        var properties = await NewServerLevel
            .KeepIdentityAsync(supervisor.WorkingDirectory, identity, configFiles, _log, cancellationToken)
            .ConfigureAwait(false);

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
            progress?.Report(new WorldRestoreReport(WorldRestoreStep.Starting, WorldReplaced: true));
            await claim.StartAsync(cancellationToken).ConfigureAwait(false);

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
            archive.Name, wasRunning, restarted, safety, restored, configRestored, refusal,
            replaced, addons, properties, OtherServerOf(contents.Origin, supervisor.InstanceId));
    }

    /// <summary>The name of the server a backup records it was taken from, when that is another server.</summary>
    internal static string? OtherServerOf(BackupOrigin? origin, string instanceId) =>
        origin is { } o && !string.Equals(o.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase)
            ? o.Name
            : null;

    /// <summary>
    /// Why a restore into this server would be refused for a link in its folder, or
    /// null: asked by the restore dialog, so it names the link rather than calling the
    /// world unreadable when RedirectionGuard refuses the walk past it (<c>T392</c> clause 2).
    /// </summary>
    public string? LinkRefusal(InstanceSupervisor supervisor)
    {
        try
        {
            return LinkInTheWay(supervisor.WorkingDirectory) is { } link ? ServerTree.Refusal(link) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A link where a restore reads or writes the server's folder, or null.</summary>
    private static string? LinkInTheWay(string workingDirectory) =>
        ServerTree.LinkedFileIn(workingDirectory)
        ?? ServerTree.FirstLink(Path.Combine(workingDirectory, "worlds"));

    /// <summary>
    /// Settles each restored world's add-ons (<c>D97</c>): the backup's, or the ones
    /// the world being replaced had.
    /// </summary>
    /// <remarks>
    /// <para>Per world, by folder name. A world keeps the backup's add-ons when they
    /// were asked for and the backup has some for it. Otherwise the backup's are
    /// removed and the replaced world's are copied in, when there was one of that
    /// name with any.</para>
    ///
    /// <para>Shared with restoring into a new server, which has no world being
    /// replaced and passes null, so leaving the backup's add-ons out leaves that
    /// world with none.</para>
    ///
    /// <para>Throws on a failure, and each caller treats that as the world failing:
    /// in place the previous world is put back, and a new server reports it.</para>
    /// </remarks>
    /// <param name="worldsDirectory">The restored <c>worlds</c> folder.</param>
    /// <param name="replacedWorlds">
    /// The <c>worlds</c> folder it replaced, moved aside, or null when there was none.
    /// </param>
    /// <param name="restoreAddons">Whether the backup's add-ons were asked for.</param>
    internal static (AddonsRestored Outcome, int FilesLeftOut, int FilesKept) SettleAddons(
        string worldsDirectory, string? replacedWorlds, bool restoreAddons)
    {
        int fromBackup = 0, leftOut = 0, fromServer = 0, filesLeftOut = 0, filesKept = 0;

        foreach (var world in Directory.EnumerateDirectories(worldsDirectory).ToList())
        {
            var restoredItems = AddonItemsIn(world);

            if (restoreAddons && restoredItems.Count > 0)
            {
                fromBackup++;
                continue;
            }

            if (restoredItems.Count > 0)
            {
                foreach (var item in restoredItems)
                {
                    filesLeftOut += FileCountOf(item);
                    DeleteItem(item);
                }

                leftOut++;
            }

            var replaced = replacedWorlds is null
                ? null
                : Path.Combine(replacedWorlds, Path.GetFileName(world));

            if (replaced is null || !Directory.Exists(replaced))
            {
                continue;
            }

            var kept = AddonItemsIn(replaced);

            if (kept.Count == 0)
            {
                continue;
            }

            foreach (var item in kept)
            {
                filesKept += CopyItem(item, Path.Combine(world, Path.GetFileName(item)));
            }

            fromServer++;
        }

        return (new AddonsRestored(restoreAddons, fromBackup, leftOut, fromServer), filesLeftOut, filesKept);
    }

    /// <summary>The add-on folders and files directly inside one world folder.</summary>
    private static List<string> AddonItemsIn(string world) =>
        Directory.EnumerateFileSystemEntries(world)
            .Where(entry => WorldAddonFiles.IsAddonItem(Path.GetFileName(entry), Directory.Exists(entry)))
            .ToList();

    /// <summary>How many files one add-on item is: one for a file, all under a folder.</summary>
    private static int FileCountOf(string item) =>
        Directory.Exists(item)
            ? Directory.EnumerateFiles(item, "*", SearchOption.AllDirectories).Count()
            : 1;

    private static void DeleteItem(string item)
    {
        if (Directory.Exists(item))
        {
            Directory.Delete(item, recursive: true);
        }
        else
        {
            File.Delete(item);
        }
    }

    /// <summary>Copies one add-on item, returning how many files it wrote.</summary>
    private static int CopyItem(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            File.Copy(source, target, overwrite: false);
            return 1;
        }

        var copied = 0;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
            copied++;
        }

        return copied;
    }

    /// <summary>
    /// How many files the world directory holds, for the summary's comparison
    /// (<c>T236</c>), or null when it could not be counted (<c>T240</c>).
    /// </summary>
    /// <remarks>
    /// <para>Every file under it, with no notion of which ones matter: the wrapper
    /// knows no world format, and a count that tried to be selective would be
    /// asserting one.</para>
    ///
    /// <para><strong>Null rather than zero, and that distinction is the whole of
    /// <c>T240</c>.</strong> Not throwing is right — a summary clause is not worth
    /// failing a restore over — but returning <em>zero</em> is a different claim
    /// from <em>unknown</em>, and zero makes the shortfall comparison false. So the
    /// disclosure sentence added for a partly-deleted backup was silenced by
    /// exactly the condition <c>T234</c> was fixed for: a world directory nobody
    /// could read. Two states for three, inside the fix written for that class.</para>
    ///
    /// <para>Internal so the catch arm is pinned rather than reasoned about. The
    /// operator's case is an ACL-denied directory, which this build cannot arrange
    /// while it runs as root — but the filter catches every <see cref="IOException"/>,
    /// and a path that is not a directory raises one here, so the arm is reachable
    /// on the platform the code is written on.</para>
    /// </remarks>
    internal static int? WorldFileCount(string worldsDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(worldsDirectory, "*", SearchOption.AllDirectories).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A keep-all ZIP of a stopped server's worlds, recorded in its history like any
    /// other backup so the picker itself offers it: undoing a restore or a sync is
    /// choosing the safety archive in the same list.
    /// </summary>
    /// <param name="note">What the history row says, such as <c>Before restoring …</c>.</param>
    /// <param name="rule">
    /// The ZIP settings whose compression and retention this run applies, sparing
    /// <paramref name="locked"/>, as an ordinary ZIP run would; null takes a keep-all
    /// ZIP at the default compression, which prunes nothing.
    /// </param>
    /// <param name="locked">The artifacts the administrator locked against retention (<c>D68</c>).</param>
    /// <remarks>Shared with a sync (<c>D103</c>), which replaces a world too and passes its server's own rule (<c>D104</c>).</remarks>
    internal async Task<SafetyBackup> TakeSafetyBackupAsync(
        InstanceSupervisor supervisor,
        string worldsDirectory,
        string note,
        CancellationToken cancellationToken,
        ZipBackupSettings? rule = null,
        IReadOnlySet<string>? locked = null)
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

        // Always ZIP, so the wrapper can restore it. A restore's run prunes nothing;
        // a sync's applies its server's ZIP rule (`D104`). Either way a later ZIP run
        // applies the server's retention to this archive like any other
        // (`ZipBackupStrategy.Prune` matches every backup-*.zip).
        var zip = new ZipBackupStrategy(_time, Microsoft.Extensions.Logging.Abstractions.NullLogger<ZipBackupStrategy>.Instance);
        ZipBackupSettings settings = rule is null
            ? new() { Enabled = true, Retention = new() }
            : new() { Enabled = true, Compression = rule.Compression, Retention = rule.Retention };

        var context = new BackupContext(
            supervisor.InstanceId,
            supervisor.Definition.Name,
            supervisor.WorkingDirectory,
            worldsDirectory,

            // The live world, both times. A safety copy is taken with the server
            // stopped, so there is nothing to snapshot around and nothing holding
            // saves to shorten (`D66`).
            worldsDirectory,
            manifest,
            artifactDirectory,
            new BackupSettings { Zip = settings },
            BackupReason.Safety,
            note,
            startedUtc,
            locked,
            ServerVersion: supervisor.Status.ServerVersion);

        var result = await zip.RunAsync(context, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || result.ArtifactPath is null)
        {
            // No safety copy, no restore. Proceeding would make the one
            // operation built to recover data into the one that loses it.
            throw new InvalidOperationException(
                $"The safety backup failed, so the world was not replaced: {result.Detail}");
        }

        await _history.AppendAsync(
            supervisor.InstanceId,
            new BackupRecord(
                startedUtc, _time.GetUtcNow(), BackupReason.Safety,
                note,
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
    /// <see cref="ReadContents"/> answers.
    /// </remarks>
    /// <summary>
    /// How the worlds directory is read, so the unreadable arm can be exercised.
    /// </summary>
    /// <remarks>
    /// A settable seam rather than a parameter because <see cref="RestoreAsync"/> needs
    /// the same arm and threading it through four call sites would put the seam in the
    /// public signature. The reason it exists is <c>WorldFileCount</c>'s: the arm an
    /// operator meets is an ACL-denied directory, which a build running as root cannot
    /// arrange (<c>T176</c>).
    /// </remarks>
    internal Func<string, bool?> WorldReader { get; set; } = WorldHasFiles;

    /// <summary>
    /// How a folder snapshot's own contents are listed, so the refused arm can be
    /// exercised (<c>T251</c>).
    /// </summary>
    /// <remarks>
    /// <see cref="WorldReader"/>'s reason exactly, one directory along: the state an
    /// operator meets is a snapshot folder this service may not traverse, and a build
    /// running as root cannot arrange one — a rig that denies itself a folder and
    /// then lists it succeeds. Only this one call is substituted; the existence
    /// checks either side of it are the real ones, so what the test drives is the
    /// same shape a denied folder produces rather than a story about it (<c>T174</c>).
    /// </remarks>
    internal Func<string, IEnumerable<string>> SnapshotLister { get; set; } =
        Directory.EnumerateFileSystemEntries;

    public SafetyBackupNeed NeedFor(InstanceSupervisor supervisor, bool restoringConfig) =>
        NeedFor(supervisor, restoringConfig, WorldReader);

    internal SafetyBackupNeed NeedFor(
        InstanceSupervisor supervisor,
        bool restoringConfig,
        Func<string, bool?> worldHasFiles)
    {
        var worldsDirectory = Path.Combine(supervisor.WorkingDirectory, "worlds");

        // `T244`. THIS ENUMERATION WAS UNGUARDED AND IT KILLED THE WHOLE PAGE. It runs
        // from the dialog-OPEN handler, so an ACL-denied worlds/ threw out of
        // BeginRestore, the circuit died with "An unhandled error has occurred", the
        // dialog never opened and nothing said why -- on EVERY backup row, including a
        // known-good archive, which is exactly what somebody reaches for when a world
        // has gone wrong.
        //
        // `T240` gave WorldFileCount a catch for these two exceptions in this same file,
        // about a hundred lines away, reading the SAME directory. The sibling was not
        // taught. Moving a rule is half the fix; the other half is grepping for every
        // reader of the thing it reads.
        //
        // A FOURTH STATE RATHER THAN A CATCH. Treating unreadable as "it has files"
        // reaches Required, whose promise says a safety backup IS taken -- and one
        // cannot be taken of a directory nobody can enumerate. The conservative branch
        // is the right action and the wrong sentence.
        var known = worldHasFiles(worldsDirectory);

        if (known is null)
        {
            return SafetyBackupNeed.WorldUnreadable;
        }

        var configPresent = ServerConfigFiles.Present(supervisor.WorkingDirectory).Any();

        return SafetyBackupRule.For(known.Value, configPresent, restoringConfig);
    }

    /// <summary>
    /// Whether the worlds directory holds any file, and null when it could not be read.
    /// </summary>
    private static bool? WorldHasFiles(string worldsDirectory)
    {
        try
        {
            // Directory.Exists swallows an access failure and answers false, so it is
            // NOT the guard it looks like -- the enumeration below is where a denied
            // directory actually throws.
            return Directory.Exists(worldsDirectory)
                && Directory.EnumerateFiles(worldsDirectory, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
    /// <returns>
    /// The names actually written, which is more than the count the in-place
    /// restore reports. <c>D40</c>'s path needs to know <em>which</em>: a restored
    /// <c>server.properties</c> carries the source server's own port and name, and
    /// leaving those in place would put the copy on the original's port.
    /// </returns>
    internal IReadOnlyList<string> RestoreConfig(
        RestorableBackup backup, string instanceDirectory)
    {
        var written = new List<string>();

        foreach (var (name, read) in ConfigIn(backup))
        {
            try
            {
                read(Path.Combine(instanceDirectory, name));
                written.Add(name);
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
    /// Which of the worlds extracted from <paramref name="backup"/> to take: the one its own
    /// <c>server.properties</c> named, or its only world; null with the reason otherwise.
    /// </summary>
    /// <remarks>
    /// A backup holding several and naming none is refused, since any choice could be the
    /// wrong world. A sync and an area restore both read a backup's world this way.
    /// </remarks>
    internal static (string? Level, string? Refusal) WorldToTake(RestorableBackup backup, IReadOnlyList<string> found)
    {
        var named = LevelNameIn(backup);

        if (named is not null && found.Contains(named, StringComparer.Ordinal))
        {
            return (named, null);
        }

        return found.Count switch
        {
            1 => (found[0], null),
            0 => (null, $"{backup.Name} holds no world."),
            _ => (null, $"{backup.Name} holds {found.Count} worlds and its server.properties names none of them, "
                + "so which to copy in is not known."),
        };
    }

    /// <summary>
    /// The <c>level-name</c> the backup's own <c>server.properties</c> set, or null when it
    /// carries none or it cannot be read (<c>T335</c>).
    /// </summary>
    /// <remarks>
    /// Read whether or not the configuration is restored: it names the world the server
    /// the backup came from loaded, which a new server needs either way.
    /// </remarks>
    internal static string? LevelNameIn(RestorableBackup backup)
    {
        try
        {
            foreach (var (name, lines) in ConfigLinesIn(backup))
            {
                if (name == ServerProperties.FileName)
                {
                    return NewServerLevel.LevelNameIn(lines());
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>A backup's configuration files and how to read each one's lines.</summary>
    private static IEnumerable<(string Name, Func<IEnumerable<string>> Lines)> ConfigLinesIn(RestorableBackup backup)
    {
        if (backup.IsFolder)
        {
            var paths = FolderPaths(backup.FullName);
            var layout = NewServerWorld.ForRestore(paths);

            foreach (var path in paths)
            {
                if (NewServerWorld.ConfigNameOf(path, layout) is { } name)
                {
                    var file = Path.Combine(backup.FullName, path);
                    yield return (name, () => File.ReadAllLines(file));
                }
            }

            yield break;
        }

        using var zip = ZipFile.OpenRead(backup.FullName);
        var layoutOfZip = ArchiveLayout(backup.FullName, ArchivePaths(zip));

        foreach (var entry in zip.Entries)
        {
            if (NewServerWorld.ConfigNameOf(Normalised(entry), layoutOfZip) is { } name)
            {
                yield return (name, () => NewServerLevel.LinesOf(entry));
            }
        }
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
            var paths = FolderPaths(backup.FullName);
            var layout = NewServerWorld.ForRestore(paths);

            foreach (var path in paths)
            {
                // Through the same name rule the archive path uses, so a file somebody
                // dropped in there cannot be written anywhere the archive form would
                // refuse to write it.
                if (NewServerWorld.ConfigNameOf(path, layout) is { } name)
                {
                    var file = Path.Combine(backup.FullName, path);
                    yield return (name, target => File.Copy(file, target, overwrite: true));
                }
            }

            yield break;
        }

        using var zip = ZipFile.OpenRead(backup.FullName);
        var layoutOfZip = ArchiveLayout(backup.FullName, ArchivePaths(zip));

        foreach (var entry in zip.Entries)
        {
            if (NewServerWorld.ConfigNameOf(Normalised(entry), layoutOfZip) is { } name)
            {
                yield return (name, target => entry.ExtractToFile(target, overwrite: true));
            }
        }
    }

    /// <summary>
    /// Puts a backup's world into <paramref name="worldsDirectory"/>.
    /// </summary>
    /// <remarks>
    /// <para>Shared with <c>D40</c>'s restore-as-a-new-server, which wants exactly
    /// this and none of what surrounds it: no stop, no safety backup, no
    /// move-aside, because the directory it writes into was made moments earlier by
    /// the wrapper and holds nothing of anybody's. <strong>Sharing a rule shares
    /// its blind spot</strong> (<c>T246</c>), so what is shared here is deliberately
    /// only the copying — every guard that depends on there being an existing world
    /// stays with the caller that has one.</para>
    ///
    /// <para><paramref name="stage"/> travels with it for the one refusal this can
    /// still raise, which reads differently depending on what the caller has
    /// already done.</para>
    /// </remarks>
    /// <param name="level">
    /// The folder a world with no folder of its own goes into: an imported <c>.mcworld</c>
    /// (<c>D110</c>). Bedrock's default level when not given.
    /// </param>
    internal static int Extract(
        RestorableBackup backup,
        string worldsDirectory,
        EmptySnapshotStage stage,
        string? level = null) =>
        backup.IsFolder
            ? CopyFolder(backup.FullName, worldsDirectory, stage, level ?? NewServerWorld.DefaultLevelName)
            : ExtractArchive(backup.FullName, worldsDirectory, level ?? NewServerWorld.DefaultLevelName);

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
    private static int CopyFolder(
        string snapshotPath, string worldsDirectory, EmptySnapshotStage stage, string level)
    {
        var root = Path.GetFullPath(worldsDirectory);
        var source = Path.GetFullPath(snapshotPath);
        var files = WorldFilesIn(source, level);

        // The guard that has to hold, `RestoreAsync`'s earlier one being the one
        // that saves the uptime (`T229`). A folder snapshot retention destroyed
        // partway holds whatever the delete did not reach, and the configuration is
        // skipped below -- so a snapshot whose only survivor is a config file copies
        // ZERO world files over a world the caller has already moved aside, and
        // reports success. The caller puts the old world back on a throw; it cannot
        // put it back after a copy that succeeded at copying nothing.
        //
        // Asked from the same walk the copy uses, so the two cannot disagree about
        // what is in there.
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                EmptySnapshotRefusal(Path.GetFileName(snapshotPath), stage));
        }

        Directory.CreateDirectory(worldsDirectory);
        var restored = 0;

        foreach (var (file, relative) in files)
        {
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

    /// <summary>Whether a folder snapshot holds anything that is part of a world.</summary>
    /// <remarks>
    /// Internal because <c>D40</c>'s path asks it too, before it creates anything —
    /// <c>T168</c>'s ordering rule with a stray instance in place of an outage.
    /// </remarks>
    internal static bool HoldsAWorld(string snapshotPath)
    {
        var source = Path.GetFullPath(snapshotPath);

        return Directory.Exists(source)
               && WorldFilesIn(source, NewServerWorld.DefaultLevelName).Count > 0;
    }

    /// <summary>
    /// The refusal, in the two places it can be raised (<c>T240</c>).
    /// </summary>
    /// <remarks>
    /// <para>It names the backup and what restoring it would have done, because
    /// "the restore failed" sends somebody to try again.</para>
    ///
    /// <para><strong>The producers had identical text and are on different sides of
    /// what has already been done.</strong> The first runs before anything is touched
    /// (<c>T168</c>); the second runs after the server is down and the world has been
    /// moved aside, so the caller puts the world back and the operator has lost their
    /// uptime for an answer the first guard usually gives for free. Nothing an
    /// operator or a test could see distinguished them, while <c>T229</c>'s clause
    /// asserts exactly that property — so each says what it cost.</para>
    ///
    /// <para><strong>The third is <c>D40</c>'s</strong>: restoring into a new server
    /// touches no existing world at all, so neither of the first two sentences is true
    /// there — nothing is replaced and nothing was stopped. What it costs instead is a
    /// server that now exists with no world in it, which is a state only this arm can
    /// describe. A boolean could not have said it (<c>T176</c>).</para>
    ///
    /// <para><strong>Internal rather than private so both spellings can be pinned.</strong>
    /// The after-the-stop arm needs a snapshot that holds a world at the pre-flight and
    /// none by the time the copy walks it, which is a race no test can arrange on any
    /// platform — and <c>T176</c>'s rule is a seam rather than an uncovered arm. The
    /// before-the-stop arm is pinned through the real path as well, so the pair is
    /// covered from both directions.</para>
    /// </remarks>
    /// <param name="name">The backup's file or folder name.</param>
    /// <param name="stage">What had already been done when this was found.</param>
    internal static string EmptySnapshotRefusal(
        string name, EmptySnapshotStage stage = EmptySnapshotStage.BeforeTheStop)
    {
        // Two reads of one value answering two questions, rather than a sentence per
        // arm: what restoring it would have done, and what finding out this late has
        // already cost. Written per arm, the first sentence would appear twice three
        // lines apart inside one control, which is `T145` exactly.
        var found = $"'{name}' holds no world files. It is an empty or partly deleted backup";

        var consequence = stage switch
        {
            EmptySnapshotStage.IntoANewServer => "so there was nothing in it to give the new server a world.",
            EmptySnapshotStage.BesideTheWorld => "and syncing from it would replace this world with nothing.",
            EmptySnapshotStage.IntoAThrowaway => "so there is no area in it to restore.",
            _ => "and restoring it would replace this world with nothing.",
        };

        var cost = stage switch
        {
            EmptySnapshotStage.BeforeTheStop => "",

            EmptySnapshotStage.AfterTheStop =>
                " This was found after the server had been stopped, so its world has "
                + "been put back and the server can be started again.",

            EmptySnapshotStage.IntoANewServer =>
                " The new server had already been made by then, so it is there with no "
                + "world in it — remove it from its own tab, or start it and Bedrock "
                + "generates a fresh world.",

            // The sync's own sentence says the world did not change and the sync
            // starts the server again if it was running (`D103`).
            EmptySnapshotStage.BesideTheWorld => "",

            // The restore's own sentence says this server's world did not change (`D108`).
            EmptySnapshotStage.IntoAThrowaway => "",

            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unhandled stage."),
        };

        return $"{found}, {consequence}{cost}";
    }

    /// <summary>
    /// The files in a folder snapshot that are part of its <c>worlds</c> folder, each with
    /// where it goes under <c>worlds</c> (<c>D110</c>).
    /// </summary>
    private static List<(string File, string Relative)> WorldFilesIn(string source, string level)
    {
        var paths = FolderPaths(source);
        var layout = NewServerWorld.ForRestore(paths);

        return
        [
            .. paths
                .Select(p => (File: Path.Combine(source, p), Relative: NewServerWorld.WorldsPathOf(p, layout, level)))
                .Where(f => f.Relative is not null)
                .Select(f => (f.File, f.Relative!)),
        ];
    }

    /// <summary>A folder's files, relative to it with forward slashes, as an archive names them.</summary>
    private static List<string> FolderPaths(string folder) =>
    [
        .. Directory
            .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace(Path.DirectorySeparatorChar, '/')),
    ];

    /// <summary>
    /// An archive's file entries, with backslashes read as separators: Windows PowerShell 5.1's
    /// <c>Compress-Archive</c> writes them, and a backup zipped by hand may well have been.
    /// </summary>
    private static List<string> ArchivePaths(ZipArchive zip) =>
        [.. zip.Entries.Select(Normalised).Where(p => p.Length > 0 && !p.EndsWith('/'))];

    private static string Normalised(ZipArchiveEntry entry) => entry.FullName.Replace('\\', '/');

    /// <summary>How to read an archive: an imported <c>.mcworld</c> always holds one world at its top.</summary>
    private static WorldFileLayout ArchiveLayout(string archivePath, IReadOnlyList<string> paths) =>
        archivePath.EndsWith(ZipBackupStrategy.McWorldSuffix, StringComparison.OrdinalIgnoreCase)
            ? WorldFileLayout.WorldAtRoot
            : NewServerWorld.ForRestore(paths);

    /// <summary>The folders in a folder snapshot that are worlds, for counting their add-ons.</summary>
    private static IEnumerable<string> WorldFoldersIn(string folder, WorldFileLayout layout)
    {
        switch (layout)
        {
            case WorldFileLayout.WorldAtRoot:
                return [folder];

            case WorldFileLayout.ServerFolder:
                var worlds = Directory
                    .EnumerateDirectories(folder)
                    .FirstOrDefault(d => string.Equals(
                        Path.GetFileName(d), NewServerWorld.WorldsFolder, StringComparison.OrdinalIgnoreCase));

                return worlds is null ? [] : Directory.EnumerateDirectories(worlds);

            case WorldFileLayout.WorldFolders:
            case WorldFileLayout.None:
                return Directory
                    .EnumerateDirectories(folder)
                    .Where(d => !string.Equals(
                        Path.GetFileName(d), FolderBackupStrategy.ConfigFolder, StringComparison.OrdinalIgnoreCase));

            default:
                throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unhandled world file layout.");
        }
    }

    private static int ExtractArchive(string archivePath, string worldsDirectory, string level)
    {
        Directory.CreateDirectory(worldsDirectory);
        var root = Path.GetFullPath(worldsDirectory);
        var restored = 0;

        using var zip = ZipFile.OpenRead(archivePath);
        var layout = ArchiveLayout(archivePath, ArchivePaths(zip));

        foreach (var entry in zip.Entries)
        {
            var path = Normalised(entry);

            if (path.Length == 0 || path.EndsWith('/'))
            {
                continue;
            }

            // Configuration is restored, if at all, by the caller writing to the
            // instance root — never into worlds. A server folder's other contents
            // (its binary, its own packs, keys) are not part of it either (`D110`).
            if (NewServerWorld.WorldsPathOf(path, layout, level) is not { } relative)
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, relative));

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
