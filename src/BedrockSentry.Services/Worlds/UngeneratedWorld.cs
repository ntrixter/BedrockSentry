using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Instances;

namespace BedrockSentry.Services.Worlds;

/// <summary>
/// A world folder made before the server first generated it: the companion pack switched on
/// at creation (<c>D93</c>) is all it holds (<c>T424</c> clause 2).
/// </summary>
public static class UngeneratedWorld
{
    /// <summary>
    /// Moves the world folder <paramref name="from"/> names to <paramref name="to"/> when it
    /// holds no world yet, so a <c>level-name</c> changed before the first start takes the
    /// pre-seeded packs with it rather than leaving them in a folder nothing loads.
    /// </summary>
    /// <returns>True when the folder was moved.</returns>
    /// <remarks>
    /// A folder with <c>level.dat</c> or <c>db</c> is a world and stays where it is; so does
    /// any folder when <paramref name="to"/> already exists, or a link (<see cref="ServerTree"/>).
    /// </remarks>
    public static bool FollowLevelName(string workingDirectory, string from, string to)
    {
        if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.Ordinal))
        {
            return false;
        }

        var worlds = Path.Combine(workingDirectory, "worlds");
        var source = Path.Combine(worlds, from);
        var target = Path.Combine(worlds, to);

        if (!Directory.Exists(source)
            || Directory.Exists(target)
            || File.Exists(target)
            || Path.GetDirectoryName(Path.GetFullPath(target)) != Path.GetFullPath(worlds)
            || ServerTree.FirstLink(source) is not null
            || File.Exists(Path.Combine(source, "level.dat"))
            || Directory.Exists(Path.Combine(source, "db")))
        {
            return false;
        }

        Directory.Move(source, target);
        return true;
    }

    /// <summary>The <c>level-name</c> a server's <c>server.properties</c> holds, or empty.</summary>
    public static string LevelNameOf(string workingDirectory) =>
        ServerProperties.Read(ServerProperties.PathFor(workingDirectory)).TryGetValue("level-name", out var level)
            ? level
            : string.Empty;
}
