namespace BedrockSentry.Core.Addons;

/// <summary>
/// What in a world folder belongs to its add-ons (<c>D97</c>): the two pack folders,
/// and the files that switch packs on, record their history and hold their settings.
/// </summary>
/// <remarks>
/// <para>A restore can put these back with the world or keep the ones the server has
/// now, so the set is stated once, here, for the restore, its dialog and the backup
/// reader alike.</para>
///
/// <para>The experiments a script pack needs are not in this set. They live in
/// <c>level.dat</c>, which is the world itself, and a restore cannot take one half of
/// that file without the other.</para>
///
/// <para>Compared ignoring case, as Windows compares file names. A folder called
/// <c>Behavior_Packs</c> is the same folder there.</para>
/// </remarks>
public static class WorldAddonFiles
{
    /// <summary>A world's settings for the packs that declare some (<c>D81</c>).</summary>
    public const string SettingsFileName = "world_behavior_pack_settings.json";

    /// <summary>The folders in a world that hold pack copies.</summary>
    public static IReadOnlyList<string> Folders { get; } =
    [
        PackPlacementLayout.ServerFolder(PackPlacement.Behavior)!,
        PackPlacementLayout.ServerFolder(PackPlacement.Resource)!,
    ];

    /// <summary>The files in a world that say which packs it uses and how.</summary>
    /// <remarks>
    /// The two history files are ones BDS never writes (measured for <c>D83</c>),
    /// and a world made in the game and copied to a server can carry them. They
    /// are listed so a restore that keeps the server's add-ons does not keep the
    /// backup's history of different ones.
    /// </remarks>
    public static IReadOnlyList<string> Files { get; } =
    [
        PackPlacementLayout.WorldListFileName(PackPlacement.Behavior)!,
        PackPlacementLayout.WorldListFileName(PackPlacement.Resource)!,
        PackHistoryFile.BehaviorFileName,
        PackHistoryFile.ResourceFileName,
        SettingsFileName,
    ];

    /// <summary>
    /// Whether a path relative to the <c>worlds</c> folder is part of a world's
    /// add-ons.
    /// </summary>
    /// <remarks>
    /// The first segment is the world. A file directly inside it counts when it is
    /// one of <see cref="Files"/>; anything deeper counts when its folder is one of
    /// <see cref="Folders"/>. A file at the <c>worlds</c> root belongs to no world
    /// and is never an add-on.
    /// </remarks>
    public static bool IsAddonPath(string worldsRelativePath)
    {
        ArgumentNullException.ThrowIfNull(worldsRelativePath);

        var segments = worldsRelativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length switch
        {
            < 2 => false,
            2 => Files.Contains(segments[1], StringComparer.OrdinalIgnoreCase),
            _ => Folders.Contains(segments[1], StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Whether an entry directly inside a world folder is one of its add-on items.
    /// </summary>
    /// <param name="name">The file or folder name, with no path.</param>
    /// <param name="isFolder">Whether it is a folder.</param>
    public static bool IsAddonItem(string name, bool isFolder) =>
        (isFolder ? Folders : Files).Contains(name, StringComparer.OrdinalIgnoreCase);
}
