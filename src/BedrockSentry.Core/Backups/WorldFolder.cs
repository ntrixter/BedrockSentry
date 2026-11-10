namespace BedrockSentry.Core.Backups;

/// <summary>
/// The files a running server's backup copies beside the ones <c>save query</c>
/// names (<c>D97</c>).
/// </summary>
/// <param name="Files">
/// Paths relative to the <c>worlds</c> folder, with forward slashes, each copied
/// whole.
/// </param>
/// <param name="Reserved">
/// Files left out because their path starts with the name backups keep for the
/// server's configuration (<see cref="ServerConfigFiles.ArchivePrefix"/>). Inside a
/// backup that name means configuration, so a restore would skip such a file or
/// write it into the server's own folder rather than into a world.
/// </param>
public sealed record BeyondTheSave(IReadOnlyList<string> Files, IReadOnlyList<string> Reserved)
{
    /// <summary>Nothing beyond the save, which is a stopped server's answer.</summary>
    public static BeyondTheSave None { get; } = new([], []);
}

/// <summary>
/// Which files under <c>worlds</c> a backup of a running server carries.
/// </summary>
/// <remarks>
/// <para><strong>The whole folder, with the save deciding only its database</strong>
/// (<c>D97</c>). <c>save query</c> names the files of the save: the world's
/// <c>db</c> folder, <c>level.dat</c> and <c>levelname.txt</c>. Those are copied at
/// the lengths it gives, because only that list is consistent while saves are held.
/// Everything else under <c>worlds</c> is copied whole: the pack folders, the pack
/// lists, other worlds, and any file an administrator put there. Before this a
/// running server's backup held the save's files and nothing else, so it carried no
/// add-ons while a stopped server's backup of the same world did.</para>
///
/// <para><strong>A <c>db</c> folder the save names is the save's alone.</strong> The
/// files in it that the list leaves out are LevelDB's own: <c>LOCK</c>, which BDS
/// holds shut, its text logs, and tables it has finished with and may delete at any
/// moment. None of them is needed to open the world, and copying a table the list
/// left out would put a file in the backup that the save does not refer to. A
/// <c>db</c> folder the save does not name belongs to a world the server has not
/// loaded, which nothing is writing, so it is copied whole like any other file.</para>
/// </remarks>
public static class WorldFolder
{
    /// <summary>The folder name LevelDB keeps a world's database in.</summary>
    public const string DatabaseFolder = "db";

    /// <summary>
    /// The files to copy whole, given every file under <c>worlds</c> and what the
    /// save named.
    /// </summary>
    /// <param name="onDisk">
    /// Every file under the <c>worlds</c> folder, relative to it. Either separator.
    /// </param>
    /// <param name="save">What <c>save query</c> answered.</param>
    public static BeyondTheSave BeyondTheSave(
        IEnumerable<string> onDisk, IReadOnlyList<SaveManifestEntry> save)
    {
        ArgumentNullException.ThrowIfNull(onDisk);
        ArgumentNullException.ThrowIfNull(save);

        // Ignoring case, as Windows names files. BDS prints the paths it opened,
        // and a listing prints the names on disk; nothing promises they are spelled
        // alike.
        var named = save
            .Select(e => Normalise(e.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var databases = save
            .Select(e => DatabaseOf(Normalise(e.RelativePath)))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var files = new List<string>();
        var reserved = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in onDisk.Select(Normalise))
        {
            if (!seen.Add(path) || named.Contains(path))
            {
                continue;
            }

            if (DatabaseOf(path) is { } database && databases.Contains(database))
            {
                continue;
            }

            (IsReserved(path) ? reserved : files).Add(path);
        }

        files.Sort(StringComparer.Ordinal);
        reserved.Sort(StringComparer.Ordinal);

        return new BeyondTheSave(files, reserved);
    }

    /// <summary>
    /// What a backup's result says about files beside the save that are not in it,
    /// or null when nothing was left out.
    /// </summary>
    /// <remarks>
    /// <para>One sentence for both formats, since the ZIP and the folder copy leave
    /// files out for the same reasons. It is stored in the backup's history, so it
    /// describes the backup rather than the moment: "is not in it" stays true.</para>
    ///
    /// <para>A file that was gone by the time it was copied is not here. It was
    /// listed, then deleted, and the backup is a copy of the folder as it stood when
    /// the copy reached it.</para>
    /// </remarks>
    /// <param name="unlisted">Why the folder could not be listed, when it could not.</param>
    /// <param name="reserved">Files under the configuration name (<see cref="BeyondTheSave.Reserved"/>).</param>
    /// <param name="unreadable">Files that were listed and could not be opened.</param>
    public static string? LeftOut(
        string? unlisted, IReadOnlyList<string> reserved, IReadOnlyList<string> unreadable)
    {
        ArgumentNullException.ThrowIfNull(reserved);
        ArgumentNullException.ThrowIfNull(unreadable);

        var parts = new List<string>();

        if (unlisted is not null)
        {
            parts.Add(
                "the rest of the worlds folder could not be listed, so only the files the "
                + $"save named are in it ({unlisted.TrimEnd('.')})");
        }

        if (unreadable.Count > 0)
        {
            parts.Add($"{Files(unreadable.Count)} could not be read and "
                      + $"{(unreadable.Count == 1 ? "is" : "are")} not in it: {Names(unreadable)}");
        }

        if (reserved.Count > 0)
        {
            parts.Add($"{Files(reserved.Count)} under {ServerConfigFiles.ArchivePrefix.TrimEnd('/')} "
                      + $"{(reserved.Count == 1 ? "is" : "are")} not in it, because backups keep that "
                      + $"name for the server's configuration: {Names(reserved)}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>How many names a sentence lists before it counts the rest.</summary>
    internal const int NamesShown = 5;

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    private static string Names(IReadOnlyList<string> paths) =>
        paths.Count <= NamesShown
            ? string.Join(", ", paths)
            : string.Join(", ", paths.Take(NamesShown)) + $" and {paths.Count - NamesShown} more";

    /// <summary>
    /// The <c>world/db</c> folder a path sits in, or null when it is not inside a
    /// world's database.
    /// </summary>
    private static string? DatabaseOf(string path)
    {
        var segments = path.Split('/');

        return segments.Length >= 3
               && string.Equals(segments[1], DatabaseFolder, StringComparison.OrdinalIgnoreCase)
            ? $"{segments[0]}/{segments[1]}"
            : null;
    }

    /// <summary>
    /// Whether a path would land in the name backups keep for configuration.
    /// </summary>
    /// <remarks>
    /// Ignoring case, unlike <see cref="ServerConfigFiles.IsConfigEntry"/>: an
    /// archive entry is matched exactly, but a folder copy is written to NTFS, where
    /// <c>_ServerConfig</c> is the configuration folder. The bare name counts too, as
    /// a file of that name would stand where the folder copy puts that folder.
    /// </remarks>
    private static bool IsReserved(string path) =>
        string.Equals(
            path.Split('/')[0],
            ServerConfigFiles.ArchivePrefix.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string path) => path.Replace('\\', '/').Trim('/');
}
