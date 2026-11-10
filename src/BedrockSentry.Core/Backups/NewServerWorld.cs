namespace BedrockSentry.Core.Backups;

/// <summary>How a world file holds its world (<c>T334</c>).</summary>
public enum WorldFileLayout
{
    /// <summary>No <c>level.dat</c> where a world keeps one, so it holds no world.</summary>
    None,

    /// <summary>
    /// <c>level.dat</c> at the top of the archive: a <c>.mcworld</c> as Minecraft exports one,
    /// which is one world with no folder of its own.
    /// </summary>
    WorldAtRoot,

    /// <summary>
    /// Folders at the top of the archive, each holding a <c>level.dat</c>: a BedrockSentry
    /// backup written before <c>D110</c>, which holds the <c>worlds</c> folder's contents and
    /// its configuration under <c>_serverconfig/</c>, or a world folder zipped whole.
    /// </summary>
    WorldFolders,

    /// <summary>
    /// A server's own folder (<c>D110</c>): <c>worlds/{level}/level.dat</c>, with
    /// <c>server.properties</c>, the allowlist and the permissions file at the top. What
    /// BedrockSentry writes from <c>D110</c> on, and what a copy of a BDS folder is.
    /// </summary>
    ServerFolder,
}

/// <summary>
/// Where a world from a backup or a world file goes in a new server, and which world that
/// server loads (<c>T334</c>, <c>T335</c>).
/// </summary>
/// <remarks>
/// BDS loads the folder under <c>worlds</c> that <c>level-name</c> names, and generates a new
/// world when there is none. A new server's <c>server.properties</c> comes from the package,
/// which names Bedrock's default level; a backup of a server whose level has another name
/// holds a folder by that name, which would sit unread beside the new world BDS generated.
/// </remarks>
public static class NewServerWorld
{
    /// <summary>What <c>level-name</c> means when <c>server.properties</c> does not set it.</summary>
    public const string DefaultLevelName = "Bedrock level";

    /// <summary>The folder a <see cref="WorldFileLayout.ServerFolder"/> keeps its worlds in.</summary>
    public const string WorldsFolder = "worlds";

    /// <summary>The file a <see cref="WorldFileLayout.ServerFolder"/> backup names its Bedrock build in.</summary>
    /// <remarks>Optional everywhere it is read: backups made by hand do not carry it (<c>D110</c>).</remarks>
    public const string VersionFile = "bds_version.txt";

    /// <summary>
    /// The file a <see cref="WorldFileLayout.ServerFolder"/> backup names the server it was
    /// taken from in (<c>D89</c>), as <see cref="BackupOrigin"/> writes it.
    /// </summary>
    /// <remarks>Optional everywhere it is read, as <see cref="VersionFile"/> is.</remarks>
    public const string OriginFile = "bedrocksentry_origin.json";

    /// <summary>The files a backup carries about itself, beside its worlds and configuration.</summary>
    public static readonly IReadOnlyList<string> RecordFiles = [VersionFile, OriginFile];

    /// <summary>
    /// Which of <see cref="RecordFiles"/> an entry is, by its own spelling, or null. Only at
    /// the top of a server folder: anywhere else the name is a world's file.
    /// </summary>
    public static string? RecordNameOf(string path, WorldFileLayout layout)
    {
        ArgumentNullException.ThrowIfNull(path);

        return layout is WorldFileLayout.ServerFolder && !path.Contains('/')
            ? RecordFiles.FirstOrDefault(known => string.Equals(known, path, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>The world file's layout and, for folders, the worlds it holds.</summary>
    /// <param name="paths">Its entries' relative paths, as <c>PackArchiveRules</c> normalises them.</param>
    /// <remarks>
    /// <para>A root <c>level.dat</c> wins: the folders beside it are that world's own. The
    /// configuration a backup carries is never a world.</para>
    ///
    /// <para><strong>Server folder or world folders (<c>D110</c>).</strong> An entry under
    /// <c>_serverconfig/</c> is only ever written by the older layout, so it decides. Then a
    /// <c>worlds/{level}/level.dat</c>, or a <c>worlds/</c> folder beside a file only a server's
    /// own folder has at its top. An older backup whose world is called <c>worlds</c> has its
    /// <c>level.dat</c> at <c>worlds/level.dat</c> and reads as world folders.</para>
    /// </remarks>
    public static (WorldFileLayout Layout, IReadOnlyList<string> Worlds) Classify(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var folders = new SortedSet<string>(StringComparer.Ordinal);
        var serverWorlds = new SortedSet<string>(StringComparer.Ordinal);
        var olderConfig = false;
        var underWorlds = false;
        var serverFile = false;

        foreach (var path in paths)
        {
            if (ServerConfigFiles.IsConfigEntry(path))
            {
                olderConfig = true;
                continue;
            }

            var segments = path.Split('/');

            if (segments.Length == 1)
            {
                if (IsLevelDat(segments[0]))
                {
                    return (WorldFileLayout.WorldAtRoot, []);
                }

                serverFile |= IsServerFile(segments[0]);
                continue;
            }

            var inWorlds = IsWorldsFolder(segments[0]);
            underWorlds |= inWorlds;

            if (segments.Length == 2 && IsLevelDat(segments[1]))
            {
                folders.Add(segments[0]);
            }

            if (segments.Length == 3 && inWorlds && IsLevelDat(segments[2]))
            {
                serverWorlds.Add(segments[1]);
            }
        }

        var serverFolder = !olderConfig
                           && !folders.Any(IsWorldsFolder)
                           && (serverWorlds.Count > 0 || (underWorlds && serverFile));

        if (serverFolder)
        {
            return serverWorlds.Count > 0 ? (WorldFileLayout.ServerFolder, [.. serverWorlds]) : (WorldFileLayout.None, []);
        }

        return folders.Count > 0 ? (WorldFileLayout.WorldFolders, [.. folders]) : (WorldFileLayout.None, []);
    }

    /// <summary>
    /// The layout to read a backup by when restoring it (<c>D110</c>): as
    /// <see cref="Classify"/> finds, with an archive whose layout it cannot tell read the way
    /// every backup was before <c>D110</c>.
    /// </summary>
    /// <remarks>
    /// <para>A restore puts back the whole <c>worlds</c> folder, not only the folders holding a
    /// <c>level.dat</c>, so a server-folder backup is told apart even when none of its worlds
    /// has one yet: by a file only a server's folder has at its top.</para>
    ///
    /// <para><strong>With or without a <c>worlds/</c> folder beside it</strong> (<c>T236</c>).
    /// The older layout kept its configuration under <c>_serverconfig/</c> and never wrote
    /// those names at its top, so one there decides. A folder backup gutted down to the files
    /// at its top, read the older way, took each of them for part of the worlds folder: the
    /// restore copied them over a world it had moved aside and reported success.</para>
    /// </remarks>
    public static WorldFileLayout ForRestore(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var list = paths as IReadOnlyCollection<string> ?? [.. paths];
        var (layout, _) = Classify(list);

        if (layout is not WorldFileLayout.None)
        {
            return layout;
        }

        var olderConfig = list.Any(ServerConfigFiles.IsConfigEntry);
        var serverFile = list.Any(p => !p.Contains('/') && IsServerFile(p));

        return !olderConfig && serverFile ? WorldFileLayout.ServerFolder : WorldFileLayout.WorldFolders;
    }

    /// <summary>
    /// Where one entry goes, relative to <c>worlds</c>, when the whole of a backup's
    /// <c>worlds</c> folder is put back; null when it is not part of it.
    /// </summary>
    /// <param name="path">The entry's normalised relative path.</param>
    /// <param name="layout">What <see cref="ForRestore"/> found.</param>
    /// <param name="level">The folder a <see cref="WorldFileLayout.WorldAtRoot"/> world goes into.</param>
    /// <remarks>
    /// Everything in the <c>worlds</c> folder comes back, not only the worlds (<c>D97</c>). A
    /// server folder's other contents (the binary, its own packs, <c>keys</c>) are never
    /// part of it.
    /// </remarks>
    public static string? WorldsPathOf(string path, WorldFileLayout layout, string level)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (ServerConfigFiles.IsConfigEntry(path))
        {
            return null;
        }

        return layout switch
        {
            WorldFileLayout.WorldAtRoot => $"{level}/{path}",
            WorldFileLayout.WorldFolders or WorldFileLayout.None => path,
            WorldFileLayout.ServerFolder =>
                path.IndexOf('/') is var slash and > 0 && IsWorldsFolder(path[..slash]) && slash + 1 < path.Length
                    ? path[(slash + 1)..]
                    : null,
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unhandled world file layout."),
        };
    }

    /// <summary>Which configuration file an entry is, by the name the server reads, or null.</summary>
    /// <remarks>
    /// Only the four <see cref="ServerConfigFiles.Names"/>: any other name would be written
    /// into the server's own folder, <c>bedrock_server.exe</c> included. A world at the
    /// root carries none.
    /// </remarks>
    public static string? ConfigNameOf(string path, WorldFileLayout layout)
    {
        ArgumentNullException.ThrowIfNull(path);

        var name = layout switch
        {
            WorldFileLayout.WorldAtRoot => null,
            WorldFileLayout.WorldFolders or WorldFileLayout.None => ServerConfigFiles.FileNameOf(path),
            WorldFileLayout.ServerFolder => path.Contains('/') ? null : path,
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unhandled world file layout."),
        };

        return name is null
            ? null
            : ServerConfigFiles.Names.FirstOrDefault(
                known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether an entry is part of a backup: its <c>worlds</c> folder, its configuration, or the
    /// record files beside them (<c>D110</c>, <c>D89</c>).
    /// </summary>
    /// <remarks>
    /// What an import keeps. A copy of a server's whole folder also holds its binary, its own
    /// packs and its identity key, and a backup never holds the key (<c>D73</c>).
    /// </remarks>
    public static bool IsBackupPart(string path, WorldFileLayout layout)
    {
        ArgumentNullException.ThrowIfNull(path);

        return layout switch
        {
            WorldFileLayout.ServerFolder =>
                WorldsPathOf(path, layout, DefaultLevelName) is not null
                || ConfigNameOf(path, layout) is not null
                || RecordNameOf(path, layout) is not null,
            WorldFileLayout.WorldAtRoot or WorldFileLayout.WorldFolders or WorldFileLayout.None => true,
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unhandled world file layout."),
        };
    }

    /// <summary>Where one entry goes, relative to <c>worlds</c>, or null when it is left out.</summary>
    /// <param name="path">The entry's normalised relative path.</param>
    /// <param name="layout">What <see cref="Classify"/> found.</param>
    /// <param name="level">The folder a <see cref="WorldFileLayout.WorldAtRoot"/> world goes into.</param>
    /// <param name="worlds">The worlds <see cref="Classify"/> found.</param>
    /// <remarks>
    /// Configuration is written to the server's own folder when it is asked for, never into
    /// <c>worlds</c>. For folders, only the worlds go in: anything else is not a world.
    /// </remarks>
    public static string? TargetOf(
        string path, WorldFileLayout layout, string level, IReadOnlyCollection<string> worlds)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(worlds);

        if (layout is WorldFileLayout.None)
        {
            return null;
        }

        if (WorldsPathOf(path, layout, level) is not { } relative)
        {
            return null;
        }

        return layout is WorldFileLayout.WorldAtRoot
               || (relative.Split('/') is { Length: > 1 } segments && worlds.Contains(segments[0], StringComparer.Ordinal))
            ? relative
            : null;
    }

    /// <summary>
    /// The first path that lands where an earlier one already did, or null when none does.
    /// </summary>
    /// <remarks>
    /// Windows folds case, so <c>World/level.dat</c> and <c>world/LEVEL.DAT</c> are one file
    /// there, and a file named like a folder another entry needs is a write that fails. Asked
    /// before anything is created, so an archive that would fail halfway is refused whole
    /// (<c>D82</c>).
    /// </remarks>
    public static string? Collision(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (!files.Add(path) || folders.Contains(path))
            {
                return path;
            }

            for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                var folder = path[..slash];

                if (files.Contains(folder))
                {
                    return path;
                }

                folders.Add(folder);
            }
        }

        return null;
    }

    /// <summary>The <c>level-name</c> to write, or null when the one set already names the right world.</summary>
    /// <param name="worlds">The folders under <c>worlds</c> that hold a <c>level.dat</c>.</param>
    /// <param name="levelName">What <c>server.properties</c> sets, or null when it sets nothing.</param>
    /// <param name="backupLevelName">What the backup's own <c>server.properties</c> set, if it carried one.</param>
    /// <remarks>
    /// <para>The backup's own <c>level-name</c> comes first when it names a world here: that is
    /// the world the server it came from loaded, and a new server's <c>level-name</c> is the
    /// package's, which nobody chose. Then the one set, then the first by name, and the caller
    /// says which when there were several.</para>
    ///
    /// <para>Compared exactly, case included, since a <c>level-name</c> that differs from its
    /// folder only in case is one every other reader here treats as naming no folder.</para>
    /// </remarks>
    public static string? LevelToSet(IReadOnlyCollection<string> worlds, string? levelName, string? backupLevelName)
    {
        ArgumentNullException.ThrowIfNull(worlds);

        if (worlds.Count == 0)
        {
            return null;
        }

        var current = string.IsNullOrEmpty(levelName) ? DefaultLevelName : levelName;

        var wanted =
            backupLevelName is { Length: > 0 } backup && worlds.Contains(backup, StringComparer.Ordinal) ? backup
            : worlds.Contains(current, StringComparer.Ordinal) ? current
            : worlds.Order(StringComparer.Ordinal).First();

        return string.Equals(wanted, current, StringComparison.Ordinal) ? null : wanted;
    }

    private static bool IsLevelDat(string name) =>
        string.Equals(name, "level.dat", StringComparison.OrdinalIgnoreCase);

    private static bool IsWorldsFolder(string name) =>
        string.Equals(name, WorldsFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>A file only a server's own folder has at its top.</summary>
    private static bool IsServerFile(string name) =>
        RecordFiles.Concat(ServerConfigFiles.Names)
            .Any(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase));
}
