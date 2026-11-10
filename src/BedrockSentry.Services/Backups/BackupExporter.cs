using System.IO.Compression;
using BedrockSentry.Core.Backups;
using BedrockSentry.Services.Instances;

namespace BedrockSentry.Services.Backups;

/// <summary>What a backup is downloaded as (<c>D110</c>).</summary>
public enum BackupDownloadFormat
{
    /// <summary>The server's own folder, zipped: unzip it, add <c>bedrock_server.exe</c>, and it runs.</summary>
    Zip,

    /// <summary>One world, as Minecraft exports and imports one. No configuration goes in.</summary>
    McWorld,
}

/// <summary>
/// One download: a backup file sent as it is, or one packed as it is sent.
/// </summary>
/// <param name="FileName">The name the browser saves it under.</param>
/// <param name="AsIs">The file to send unchanged, or null when it is packed.</param>
/// <param name="Pack">Writes the packed archive to a stream, or null when it is sent as it is.</param>
public sealed record BackupDownload(string FileName, string? AsIs, Action<Stream>? Pack)
{
    /// <summary>The media type it is sent with.</summary>
    public string ContentType => "application/zip";
}

/// <summary>
/// Gives a backup back as a file to keep (<c>D110</c>): a <c>.zip</c> laid out as the server's
/// own folder, or a <c>.mcworld</c> holding only the world.
/// </summary>
/// <remarks>
/// <para><strong>A ZIP that is already one is sent unchanged</strong>, whichever layout it has.
/// A folder copy, or an imported <c>.mcworld</c>, is packed into the server-folder layout as it
/// is sent: the worlds folder under <c>worlds/</c>, the configuration and version file at the
/// top.</para>
///
/// <para><strong>A <c>.mcworld</c> is one world with nothing else</strong>: the world the
/// backup's own <c>server.properties</c> names, or its only one, with its <c>level.dat</c> at the
/// top. An imported <c>.mcworld</c> is sent unchanged.</para>
///
/// <para><strong>Packed at <see cref="CompressionLevel.SmallestSize"/></strong>, Deflate's
/// strongest. Minecraft reads Deflate only, so nothing stronger would import.</para>
/// </remarks>
public sealed class BackupExporter
{
    private readonly WorldRestorer _restorer;
    private readonly InstanceRegistry _registry;

    /// <summary>The download endpoint's route.</summary>
    /// <remarks>
    /// Outside <c>/instances/</c>, which is the panel's own pages, so no page route can
    /// match it first.
    /// </remarks>
    public const string Route = "/backup-download/{instanceId}/{name}";

    /// <summary>The link that downloads one backup in one format.</summary>
    public static string UrlFor(string instanceId, string name, BackupDownloadFormat format) =>
        $"/backup-download/{Uri.EscapeDataString(instanceId)}/{Uri.EscapeDataString(name)}"
        + $"?as={(format is BackupDownloadFormat.McWorld ? "mcworld" : "zip")}";

    /// <summary>The format a link's <c>as</c> names, or null for anything else.</summary>
    public static BackupDownloadFormat? FormatNamed(string? value) => value?.ToLowerInvariant() switch
    {
        "zip" => BackupDownloadFormat.Zip,
        "mcworld" => BackupDownloadFormat.McWorld,
        _ => null,
    };

    public BackupExporter(WorldRestorer restorer, InstanceRegistry registry)
    {
        _restorer = restorer;
        _registry = registry;
    }

    /// <summary>
    /// The download for one of a server's backups, by its name on disk, or null when the server has
    /// no backup by that name.
    /// </summary>
    /// <remarks>Looked up in the server's own listing: the name reaches here from a URL.</remarks>
    public BackupDownload? Prepare(string instanceId, string name, BackupDownloadFormat format)
    {
        var backup = _restorer
            .ListBackups(instanceId)
            .FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

        return backup is null || !_registry.TryGet(instanceId, out var supervisor)
            ? null
            : Prepare(backup, format, NewServerLevel.LevelOf(supervisor.WorkingDirectory));
    }

    /// <param name="level">
    /// The folder a world with no folder of its own goes into when it is zipped as a server's
    /// folder: the level the server loads.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A <c>.mcworld</c> is asked of a backup holding several worlds that names none of them.
    /// </exception>
    internal static BackupDownload Prepare(RestorableBackup backup, BackupDownloadFormat format, string level)
    {
        var stem = backup.IsFolder ? backup.Name : Path.GetFileNameWithoutExtension(backup.Name);

        return (format, backup.Format) switch
        {
            (BackupDownloadFormat.Zip, BackupFileFormat.Zip) or (BackupDownloadFormat.McWorld, BackupFileFormat.McWorld) =>
                new BackupDownload(backup.Name, backup.FullName, null),

            (BackupDownloadFormat.Zip, _) =>
                new BackupDownload(stem + ZipBackupStrategy.ArchiveSuffix, null, output => PackServerFolder(backup, level, output)),

            (BackupDownloadFormat.McWorld, _) =>
                new BackupDownload(stem + ZipBackupStrategy.McWorldSuffix, null, PackWorld(backup)),

            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unhandled download format."),
        };
    }

    /// <summary>One file of a backup, by its relative path with forward slashes.</summary>
    private sealed record Source(string Path, Func<Stream> Open, DateTimeOffset LastWrite);

    private static void PackServerFolder(RestorableBackup backup, string level, Stream output)
    {
        using var sources = SourcesOf(backup);
        var layout = LayoutOf(backup, sources.Files);

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var file in sources.Files)
        {
            var name = NewServerWorld.WorldsPathOf(file.Path, layout, level) is { } relative
                ? $"{NewServerWorld.WorldsFolder}/{relative}"
                : NewServerWorld.ConfigNameOf(file.Path, layout) ?? NewServerWorld.RecordNameOf(file.Path, layout);

            if (name is not null)
            {
                Add(archive, name, file);
            }
        }
    }

    /// <summary>
    /// Chooses the world before anything is sent, so a backup that names none of its several
    /// worlds is refused rather than sent as a broken download.
    /// </summary>
    private static Action<Stream> PackWorld(RestorableBackup backup)
    {
        string level;

        using (var sources = SourcesOf(backup))
        {
            var paths = sources.Files.Select(f => f.Path).ToList();
            var layout = LayoutOf(backup, sources.Files);

            var worlds = layout is WorldFileLayout.WorldAtRoot
                ? [NewServerWorld.DefaultLevelName]
                : NewServerWorld.Classify(paths).Worlds;

            var (chosen, refusal) = layout is WorldFileLayout.WorldAtRoot
                ? (NewServerWorld.DefaultLevelName, null)
                : WorldRestorer.WorldToTake(backup, worlds);

            level = chosen ?? throw new InvalidOperationException(refusal);
        }

        return output =>
        {
            using var sources = SourcesOf(backup);
            var layout = LayoutOf(backup, sources.Files);
            var prefix = level + "/";

            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

            foreach (var file in sources.Files)
            {
                if (NewServerWorld.WorldsPathOf(file.Path, layout, level) is { } relative
                    && relative.StartsWith(prefix, StringComparison.Ordinal)
                    && relative.Length > prefix.Length)
                {
                    Add(archive, relative[prefix.Length..], file);
                }
            }
        };
    }

    private static void Add(ZipArchive archive, string name, Source file)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        entry.LastWriteTime = ArchiveTimes.ForEntry(file.LastWrite);

        using var input = file.Open();
        using var target = entry.Open();
        input.CopyTo(target);
    }

    private static WorldFileLayout LayoutOf(RestorableBackup backup, IReadOnlyList<Source> files) =>
        backup.Format is BackupFileFormat.McWorld
            ? WorldFileLayout.WorldAtRoot
            : NewServerWorld.ForRestore(files.Select(f => f.Path).ToList());

    /// <summary>A backup's files, holding its archive open while they are read.</summary>
    private sealed class Sources(IReadOnlyList<Source> files, IDisposable? archive) : IDisposable
    {
        public IReadOnlyList<Source> Files { get; } = files;

        public void Dispose() => archive?.Dispose();
    }

    private static Sources SourcesOf(RestorableBackup backup)
    {
        if (backup.IsFolder)
        {
            return new Sources(
            [
                .. Directory
                    .EnumerateFiles(backup.FullName, "*", SearchOption.AllDirectories)
                    .Select(f => new Source(
                        Path.GetRelativePath(backup.FullName, f).Replace(Path.DirectorySeparatorChar, '/'),
                        () => File.OpenRead(f),
                        new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero))),
            ], null);
        }

        var zip = ZipFile.OpenRead(backup.FullName);

        return new Sources(
        [
            .. zip.Entries
                .Where(e => e.FullName.Length > 0 && !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\'))
                .Select(e => new Source(e.FullName.Replace('\\', '/'), e.Open, e.LastWriteTime)),
        ], zip);
    }
}
