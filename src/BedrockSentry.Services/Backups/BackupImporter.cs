using System.Globalization;
using System.IO.Compression;
using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>What a backup offered for import holds, or why it cannot be imported (<c>D110</c>).</summary>
/// <param name="SourceName">The file's name, or the folder's path.</param>
/// <param name="Format">How it will be kept: as the ZIP or <c>.mcworld</c> it is, or as a folder.</param>
/// <param name="Layout">How it holds its world.</param>
/// <param name="Worlds">For folder layouts, the worlds it holds, by folder name.</param>
/// <param name="LevelName">The <c>level-name</c> its own <c>server.properties</c> sets, if it carries one.</param>
/// <param name="ConfigFiles">The configuration it carries, by file name.</param>
/// <param name="BdsVersion">What its <c>bds_version.txt</c> says; optional, and older backups have none.</param>
/// <param name="TakenUtc">When it was taken, the time it is listed under.</param>
/// <param name="Files">How many files are kept.</param>
/// <param name="Bytes">What those files come to unpacked.</param>
/// <param name="LeftOut">
/// What a copy of a server's whole folder holds that a backup does not, by top-level name with
/// a count: its binary, its own packs and its identity key (<c>D73</c>).
/// </param>
/// <param name="Refusal">Why it cannot be imported, or null when it can.</param>
/// <param name="Origin">The server it says it was taken from (<c>D89</c>); optional, as the version is.</param>
public sealed record BackupImportPreview(
    string SourceName,
    BackupFileFormat Format,
    WorldFileLayout Layout,
    IReadOnlyList<string> Worlds,
    string? LevelName,
    IReadOnlyList<string> ConfigFiles,
    string? BdsVersion,
    DateTimeOffset TakenUtc,
    int Files,
    long Bytes,
    IReadOnlyList<string> LeftOut,
    string? Refusal = null,
    BackupOrigin? Origin = null)
{
    /// <summary>Whether it can be imported.</summary>
    public bool CanImport => Refusal is null;

    /// <summary>The Type column's word for it.</summary>
    public string Kind => RestorableBackup.KindOf(Format);

    internal static BackupImportPreview Refused(string sourceName, BackupFileFormat format, string refusal) =>
        new(sourceName, format, WorldFileLayout.None, [], null, [], null, default, 0, 0, [], refusal);

    /// <summary>How it holds its world, in the import panel's words.</summary>
    public string LayoutText() => Layout switch
    {
        WorldFileLayout.ServerFolder => @"A server's folder (worlds\, server.properties at the top)",
        WorldFileLayout.WorldFolders => @"An older BedrockSentry backup (world folders, _serverconfig\)",
        WorldFileLayout.WorldAtRoot => "One Minecraft world (level.dat at the top)",
        WorldFileLayout.None => "No world",
        _ => throw new ArgumentOutOfRangeException(nameof(Layout), Layout, "Unhandled world file layout."),
    };

    /// <summary>Its worlds, in the import panel's words.</summary>
    public string WorldsText() => Layout switch
    {
        WorldFileLayout.WorldAtRoot => "One world",
        _ when Worlds.Count == 0 => "None",
        _ => string.Join(", ", Worlds) + (LevelName is { } level && Worlds.Contains(level, StringComparer.Ordinal)
            ? $" (server.properties loads {level})"
            : ""),
    };

    /// <summary>Its size, in the import panel's words.</summary>
    public string SizeText() => $"{Files:N0} file{(Files == 1 ? "" : "s")}, {ByteSize.Format(Bytes)} unpacked";
}

/// <summary>What importing a backup did (<c>D110</c>).</summary>
/// <param name="Name">The name it is kept under in the server's backups folder.</param>
public sealed record BackupImportResult(string Name, BackupImportPreview Preview)
{
    /// <summary>The sentence the panel shows.</summary>
    public string Summary() =>
        $"Imported {Preview.SourceName} as {Name} ({Preview.SizeText()}), taken "
        + $"{Preview.TakenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}. "
        + "It is marked Keep, so retention never removes it.";
}

/// <summary>
/// Brings a backup made elsewhere into a server's backups (<c>D110</c>): a <c>.zip</c> or a
/// <c>.mcworld</c> uploaded from the panel, or a folder already on this machine.
/// </summary>
/// <remarks>
/// <para><strong>One backup at a time.</strong> Each is read and described before anything is
/// written, and refused whole on any entry that would escape its folder (<c>D82</c>).</para>
///
/// <para><strong>It is kept as it came</strong>, named from the time it was taken so it sorts
/// with the server's own, unless it is a copy of a server's whole folder: then only the worlds
/// folder, the configuration and the version file are kept, and the binary, the server's own
/// packs and its identity key are left out and named (<c>D73</c>).</para>
///
/// <para><strong>It is marked Keep</strong>, so retention never removes it, and recorded as
/// <see cref="BackupReason.Imported"/> where its time puts it in the history. It is never this
/// server's "last backup" (<see cref="BackupRecord.IsImported"/>).</para>
/// </remarks>
public sealed class BackupImporter
{
    private readonly BackupHistory _history;
    private readonly BackupArtifactStore _artifacts;
    private readonly WorldFileImport _uploads;
    private readonly ILogger<BackupImporter> _log;

    public BackupImporter(
        BackupHistory history,
        BackupArtifactStore artifacts,
        WorldFileImport uploads,
        ILogger<BackupImporter> log)
    {
        _history = history;
        _artifacts = artifacts;
        _uploads = uploads;
        _log = log;
    }

    /// <summary>The unpacked bound this instance enforces; a seam, since no test imports gigabytes.</summary>
    internal long UnpackedLimit { get; set; } = WorldFileImport.MaxUnpackedBytes;

    /// <summary>One file in a backup being imported, by its name in the backup.</summary>
    internal sealed record Item(string Name, long Length, DateTimeOffset LastWriteUtc, Func<Stream> Open);

    /// <summary>What an uploaded <c>.zip</c> or <c>.mcworld</c> holds.</summary>
    /// <param name="uploadPath">Where <see cref="WorldFileImport.SaveUploadAsync"/> stored it.</param>
    /// <param name="fileName">The name it was uploaded under.</param>
    public BackupImportPreview ReadUpload(string uploadPath, string fileName)
    {
        _uploads.RefuseAPathNotUploadedHere(uploadPath);

        var format = FormatOf(fileName);

        try
        {
            using var zip = ZipFile.OpenRead(uploadPath);
            return Describe(fileName, format, ItemsIn(zip));
        }
        catch (InvalidDataException)
        {
            return BackupImportPreview.Refused(fileName, format, $"{fileName} is not a ZIP archive Windows can open.");
        }
    }

    /// <summary>What a folder on this machine holds.</summary>
    public BackupImportPreview ReadFolder(string folderPath)
    {
        if (FolderRefusal(folderPath) is { } refusal)
        {
            return BackupImportPreview.Refused(folderPath, BackupFileFormat.Folder, refusal);
        }

        try
        {
            return Describe(folderPath, BackupFileFormat.Folder, ItemsIn(Path.GetFullPath(folderPath)));
        }
        catch (UnauthorizedAccessException)
        {
            return BackupImportPreview.Refused(folderPath, BackupFileFormat.Folder, DeniedRefusal(folderPath));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return BackupImportPreview.Refused(
                folderPath, BackupFileFormat.Folder, $"{folderPath} could not be read: {ex.Message}");
        }
    }

    /// <summary>Imports an uploaded <c>.zip</c> or <c>.mcworld</c>, then discards the upload.</summary>
    public async Task<BackupImportResult> ImportUploadAsync(
        string instanceId, string uploadPath, string fileName, string? note,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var preview = ReadUpload(uploadPath, fileName);

            var name = await KeepAsync(instanceId, preview, note, target =>
            {
                if (preview.LeftOut.Count == 0)
                {
                    File.Copy(uploadPath, target);
                    return;
                }

                using var zip = ZipFile.OpenRead(uploadPath);
                WriteZip(target, preview.Layout, ItemsIn(zip));
            }, cancellationToken).ConfigureAwait(false);

            return new BackupImportResult(name, preview);
        }
        finally
        {
            _uploads.Discard(uploadPath);
        }
    }

    /// <summary>Imports a copy of a folder on this machine. The folder itself is only read.</summary>
    public async Task<BackupImportResult> ImportFolderAsync(
        string instanceId, string folderPath, string? note, CancellationToken cancellationToken = default)
    {
        var preview = ReadFolder(folderPath);

        var name = await KeepAsync(instanceId, preview, note, target =>
        {
            foreach (var item in Kept(preview.Layout, ItemsIn(Path.GetFullPath(folderPath))))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var destination = Path.Combine(target, item.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                using var input = item.Open();
                using var output = File.Create(destination);
                input.CopyTo(output);
            }
        }, cancellationToken).ConfigureAwait(false);

        return new BackupImportResult(name, preview);
    }

    /// <summary>
    /// Writes the backup beside a temporary name, moves it into place, then records it.
    /// </summary>
    private async Task<string> KeepAsync(
        string instanceId,
        BackupImportPreview preview,
        string? note,
        Action<string> write,
        CancellationToken cancellationToken)
    {
        if (preview.Refusal is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        var directory = _history.DirectoryFor(instanceId);
        Directory.CreateDirectory(directory);

        var name = NameFor(preview);

        if (Taken(directory, preview.TakenUtc) is { } existing)
        {
            throw new InvalidOperationException(
                $"This server already has a backup taken at that second, {existing}. "
                + "It may be this same backup, imported before.");
        }

        var final = Path.Combine(directory, name);
        var temporary = $"{final}.importing";

        try
        {
            await Task.Run(() => write(temporary), cancellationToken).ConfigureAwait(false);

            if (preview.Format is BackupFileFormat.Folder)
            {
                Directory.Move(temporary, final);
            }
            else
            {
                File.Move(temporary, final);
            }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        var detail =
            $"Imported from {preview.SourceName}: {preview.SizeText()}"
            + (preview.LeftOut.Count > 0 ? $"; left out {string.Join(", ", preview.LeftOut)}" : "")
            + ".";

        var record = new BackupRecord(
            preview.TakenUtc,
            preview.TakenUtc,
            BackupReason.Imported,
            string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Skipped: false,
            SkipReason: null,
            Succeeded: true,
            [new BackupStrategyResult(StrategyNameOf(preview.Format), true, detail, final)]);

        await _history.InsertAsync(instanceId, record, cancellationToken).ConfigureAwait(false);
        await _artifacts.SetAsync(instanceId, name, new BackupArtifactState(Locked: true), cancellationToken)
            .ConfigureAwait(false);

        _log.LogInformation(
            "Imported {Source} into {InstanceId} as {Name}", preview.SourceName, instanceId, name);

        return name;
    }

    /// <summary>The name it is kept under, which carries the time it was taken.</summary>
    internal static string NameFor(BackupImportPreview preview) => preview.Format switch
    {
        BackupFileFormat.Zip => BackupRetentionPlan.ComposeName(
            ZipBackupStrategy.ArchivePrefix, preview.TakenUtc, ZipBackupStrategy.ArchiveSuffix),
        BackupFileFormat.McWorld => BackupRetentionPlan.ComposeName(
            ZipBackupStrategy.ArchivePrefix, preview.TakenUtc, ZipBackupStrategy.McWorldSuffix),
        BackupFileFormat.Folder => BackupRetentionPlan.ComposeName(
            FolderBackupStrategy.FolderPrefix, preview.TakenUtc),
        _ => throw new ArgumentOutOfRangeException(nameof(preview), preview.Format, "Unhandled backup format."),
    };

    /// <summary>The strategy name its history row carries, which the Type column reads.</summary>
    internal static string StrategyNameOf(BackupFileFormat format) => format switch
    {
        BackupFileFormat.Zip => ZipBackupStrategy.StrategyName,
        BackupFileFormat.Folder => FolderBackupStrategy.StrategyName,
        BackupFileFormat.McWorld => RestorableBackup.McWorldKind,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unhandled backup format."),
    };

    /// <summary>A backup of any format already here with the same stamp, by name, or null.</summary>
    private static string? Taken(string directory, DateTimeOffset takenUtc)
    {
        string[] names =
        [
            BackupRetentionPlan.ComposeName(ZipBackupStrategy.ArchivePrefix, takenUtc, ZipBackupStrategy.ArchiveSuffix),
            BackupRetentionPlan.ComposeName(ZipBackupStrategy.ArchivePrefix, takenUtc, ZipBackupStrategy.McWorldSuffix),
            BackupRetentionPlan.ComposeName(FolderBackupStrategy.FolderPrefix, takenUtc),
        ];

        return names.FirstOrDefault(n =>
            File.Exists(Path.Combine(directory, n)) || Directory.Exists(Path.Combine(directory, n)));
    }

    private BackupImportPreview Describe(string sourceName, BackupFileFormat format, IReadOnlyList<Item> items)
    {
        var plan = PackArchiveRules.Plan(
            items.Select(i => new ArchiveEntry(i.Name, i.Length)), UnpackedLimit, WorldFileImport.MaxEntries);

        if (!plan.Accepted)
        {
            return BackupImportPreview.Refused(sourceName, format, plan.Verdict switch
            {
                ArchiveEntryVerdict.TooLarge =>
                    $"{sourceName} unpacks to more than {ByteSize.Format(UnpackedLimit)}, the most a backup may.",
                ArchiveEntryVerdict.TooManyEntries =>
                    $"{sourceName} holds more than {WorldFileImport.MaxEntries:N0} files, the most a backup may.",
                _ => $"{plan.Reason ?? PackArchiveRules.Describe(plan.Verdict)} Nothing in {sourceName} was used.",
            });
        }

        if (NewServerWorld.Collision(plan.Paths) is { } collision)
        {
            return BackupImportPreview.Refused(
                sourceName, format,
                $"Two entries in {sourceName} are the same file on Windows, '{collision}' among them.");
        }

        var (layout, worlds) = NewServerWorld.Classify(plan.Paths);

        if (layout is WorldFileLayout.None)
        {
            return BackupImportPreview.Refused(
                sourceName, format,
                $"{sourceName} holds no world: no level.dat at its top, one folder down, or under worlds\\.");
        }

        if (format is BackupFileFormat.McWorld && layout is not WorldFileLayout.WorldAtRoot)
        {
            return BackupImportPreview.Refused(
                sourceName, format,
                $"{sourceName} is named .mcworld but holds its world in a folder; a .mcworld has level.dat "
                + "at its top. Rename it to .zip to import it as a backup.");
        }

        // Named by the plan's destination, which reads backslashes as separators. Matched by
        // the entry's own name, since the plan skips directory entries.
        var destinations = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var planned in plan.Entries)
        {
            destinations.TryAdd(planned.Source, planned.Destination);
        }

        var named = items
            .Where(i => destinations.ContainsKey(i.Name))
            .Select(i => i with { Name = destinations[i.Name] })
            .ToList();

        var kept = Kept(layout, named).ToList();
        var config = kept
            .Select(i => (Item: i, Name: NewServerWorld.ConfigNameOf(i.Name, layout)))
            .Where(c => c.Name is not null)
            .ToList();

        var level = config
            .Where(c => c.Name == ServerProperties.FileName)
            .Select(c => NewServerLevel.LevelNameIn(LinesOf(c.Item)))
            .FirstOrDefault();

        var records = kept
            .Select(i => (Item: i, Name: NewServerWorld.RecordNameOf(i.Name, layout)))
            .Where(r => r.Name is not null)
            .ToList();

        var version = records
            .Where(r => r.Name == NewServerWorld.VersionFile)
            .Select(r => LinesOf(r.Item).FirstOrDefault(l => l.Trim().Length > 0)?.Trim())
            .FirstOrDefault();

        var origin = records
            .Where(r => r.Name == NewServerWorld.OriginFile)
            .Select(r => BackupOrigin.Parse(string.Join('\n', LinesOf(r.Item))))
            .FirstOrDefault();

        var leftOut = named
            .Where(i => !NewServerWorld.IsBackupPart(i.Name, layout))
            .GroupBy(i => i.Name.Split('/')[0], StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() == 1 && !g.First().Name.Contains('/')
                ? g.Key
                : $"{g.Key}\\ ({g.Count()} file{(g.Count() == 1 ? "" : "s")})")
            .ToList();

        return new BackupImportPreview(
            sourceName, format, layout, worlds, level,
            [.. config.Select(c => c.Name!).Order(StringComparer.Ordinal)],
            version,
            TakenAt(sourceName, format, kept, DateTimeOffset.UtcNow),
            kept.Count,
            kept.Sum(i => i.Length),
            leftOut,
            Origin: origin);
    }

    /// <summary>When it was taken.</summary>
    /// <remarks>
    /// <para>The stamp in a BedrockSentry backup's own name first. Then, for a ZIP or a
    /// <c>.mcworld</c>, the newest <c>LastPlayed</c> its <c>level.dat</c> files carry
    /// (<see cref="ArchiveTimes.LastPlayed"/>): a ZIP keeps its entries' times as clock readings
    /// with no zone, and writers disagree on whose clock, so a <c>.mcworld</c> read by its
    /// entries listed late by the offset on a machine behind UTC (<c>T359</c> clause 3).</para>
    ///
    /// <para>Then the newest <c>level.dat</c>'s file time, which is written whenever the world
    /// is saved, then the newest file. A folder's file times are in UTC, so a folder goes
    /// straight there.</para>
    /// </remarks>
    internal static DateTimeOffset TakenAt(
        string sourceName, BackupFileFormat format, IReadOnlyList<Item> kept, DateTimeOffset now)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceName));

        foreach (var (prefix, suffix) in new[]
                 {
                     (ZipBackupStrategy.ArchivePrefix, ZipBackupStrategy.ArchiveSuffix),
                     (ZipBackupStrategy.ArchivePrefix, ZipBackupStrategy.McWorldSuffix),
                     (FolderBackupStrategy.FolderPrefix, ""),
                 })
        {
            if (BackupRetentionPlan.TryReadStamp(name, prefix, suffix, out var stamp))
            {
                return new DateTimeOffset(stamp, TimeSpan.Zero);
            }
        }

        var levels = kept
            .Where(i => i.Name.Split('/')[^1].Equals("level.dat", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (format is not BackupFileFormat.Folder
            && levels.Select(l => LastPlayedIn(l, now)).Max() is { } played)
        {
            return played;
        }

        var newest = levels.Count > 0 ? levels.Max(i => i.LastWriteUtc) : kept.Max(i => i.LastWriteUtc);

        // A backup's name holds whole seconds; so does the record it is listed beside.
        return new DateTimeOffset(newest.UtcTicks - (newest.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    /// <summary>The <c>LastPlayed</c> one <c>level.dat</c> carries, or null.</summary>
    private static DateTimeOffset? LastPlayedIn(Item levelDat, DateTimeOffset now)
    {
        // A level.dat is a few kilobytes; one far larger is not one to trust.
        if (levelDat.Length is <= 0 or > MaxLevelDatBytes)
        {
            return null;
        }

        try
        {
            using var input = levelDat.Open();
            var bytes = new byte[levelDat.Length];
            input.ReadExactly(bytes);
            return ArchiveTimes.LastPlayed(bytes, now);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    private const long MaxLevelDatBytes = 1024 * 1024;

    private static IEnumerable<Item> Kept(WorldFileLayout layout, IEnumerable<Item> items) =>
        items.Where(i => !i.Name.EndsWith('/') && NewServerWorld.IsBackupPart(i.Name.Replace('\\', '/'), layout));

    /// <summary>A ZIP of only the kept entries, for a copy of a server's whole folder.</summary>
    private static void WriteZip(string target, WorldFileLayout layout, IReadOnlyList<Item> items)
    {
        using var output = File.Create(target);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);

        foreach (var item in Kept(layout, items.Select(i => i with { Name = i.Name.Replace('\\', '/') })))
        {
            var entry = archive.CreateEntry(item.Name, CompressionLevel.Optimal);
            entry.LastWriteTime = ArchiveTimes.ForEntry(item.LastWriteUtc);

            using var input = item.Open();
            using var stream = entry.Open();
            input.CopyTo(stream);
        }
    }

    private static List<Item> ItemsIn(ZipArchive zip) =>
    [
        .. zip.Entries.Select(e => new Item(e.FullName, e.Length, e.LastWriteTime.ToUniversalTime(), e.Open)),
    ];

    /// <summary>A folder's files, refusing any symbolic link or junction inside it.</summary>
    /// <remarks>
    /// A link would copy whatever it points at, which may be outside the folder somebody chose.
    /// </remarks>
    private static List<Item> ItemsIn(string root)
    {
        var items = new List<Item>();
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);

        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (Security.ReparseTags.Why(entry) is { } link)
                {
                    throw new InvalidDataException(
                        $"'{Path.GetRelativePath(root, entry.FullName)}' is {link}, a link to somewhere else, "
                        + "so the folder was not imported.");
                }

                switch (entry)
                {
                    case DirectoryInfo folder:
                        pending.Push(folder);
                        break;

                    case FileInfo file:
                        var path = file.FullName;
                        items.Add(new Item(
                            Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                            file.Length,
                            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                            () => File.OpenRead(path)));
                        break;
                }
            }
        }

        return items;
    }

    private string? FolderRefusal(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Path.IsPathFullyQualified(folderPath))
        {
            return "Give the folder's full path, such as D:\\Backups\\2024-03-11.";
        }

        // `T359` clause 3. Directory.Exists answers false for a folder this service may not
        // read as well as for one that is not there, and said "There is no folder" about a
        // folder under somebody's profile, which the service's account cannot open.
        switch (Probe(folderPath, File.GetAttributes))
        {
            case FolderState.Missing:
                return $"There is no folder at {folderPath}.";

            case FolderState.NotAFolder:
                return $"{folderPath} is a file, not a folder. Use Upload a file for it.";

            case FolderState.Denied:
                return DeniedRefusal(folderPath);

            case FolderState.Unreadable:
                return $"{folderPath} could not be read.";

            case FolderState.Present:
                break;

            default:
                throw new InvalidOperationException("Unhandled folder state.");
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        var backups = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_history.DirectoryFor("_")));
        var root = Path.GetDirectoryName(backups)!;

        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? "That folder is already in BedrockSentry's backups."
            : null;
    }

    /// <summary>What a path given to import a folder from turned out to be.</summary>
    internal enum FolderState
    {
        Present,
        Missing,
        NotAFolder,

        /// <summary>Windows refused this service's account even a look at it.</summary>
        Denied,

        /// <summary>Windows answered with another failure, such as a network path that is down.</summary>
        Unreadable,
    }

    /// <summary>
    /// What is at <paramref name="path"/>, as this service's account sees it, with the read
    /// injected so the refused arm is reachable where the suite runs as an administrator.
    /// </summary>
    internal static FolderState Probe(string path, Func<string, FileAttributes> attributes)
    {
        try
        {
            return attributes(path).HasFlag(FileAttributes.Directory) ? FolderState.Present : FolderState.NotAFolder;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return FolderState.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return FolderState.Denied;
        }
        catch (IOException)
        {
            return FolderState.Unreadable;
        }
    }

    /// <summary>
    /// Why a folder the service may not read is not imported, and what to do instead.
    /// </summary>
    /// <remarks>
    /// A limit, not a fault: the service runs under its own account (<c>D44</c>), which is not
    /// given a user's own folders, and the panel does not grant it a folder (<c>D120</c>).
    /// An upload goes through the browser as the user, so it reaches what this cannot.
    /// </remarks>
    internal static string DeniedRefusal(string folderPath) =>
        $"BedrockSentry's service account may not read {folderPath}. "
        + "Upload a .zip of the folder instead, or copy the folder somewhere the service can read.";

    private static BackupFileFormat FormatOf(string fileName) =>
        fileName.EndsWith(ZipBackupStrategy.McWorldSuffix, StringComparison.OrdinalIgnoreCase)
            ? BackupFileFormat.McWorld
            : BackupFileFormat.Zip;

    private static List<string> LinesOf(Item item)
    {
        using var reader = new StreamReader(item.Open());
        var lines = new List<string>();

        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not remove {Path} after a failed import", path);
        }
    }
}
