namespace BedrockSentry.Core.Backups;

/// <summary>
/// The server's own configuration, carried inside a backup alongside the world
/// (<c>docs/design.md</c> § 8.3).
/// </summary>
/// <remarks>
/// A world without its <c>server.properties</c> restores onto a server that may
/// be on the wrong port, the wrong difficulty and the wrong name; without
/// <c>allowlist.json</c> and <c>permissions.json</c> it restores onto one that
/// lets in the wrong people. Archiving the world alone was archiving most of
/// the answer.
///
/// They are stored under a reserved prefix rather than at the archive root,
/// because the root is the <c>worlds/</c> namespace: an entry there is a path
/// inside a world folder, and a file called <c>server.properties</c> sitting
/// beside <c>Bedrock level/</c> would be indistinguishable from a world called
/// <c>server.properties</c>. The prefix keeps the two apart and lets an older
/// archive — which simply has no such entries — restore exactly as it always
/// did.
///
/// <b>Restoring them is a separate, opt-in choice.</b> Putting an old
/// <c>server.properties</c> back can move the port, which is the one change
/// that makes a server unreachable rather than merely wrong, so the world and
/// the configuration are restored independently and the configuration half is
/// off unless asked for.
/// </remarks>
public static class ServerConfigFiles
{
    /// <summary>
    /// Archive prefix for configuration entries. Chosen so no plausible world
    /// folder collides with it, and checked against the manifest anyway.
    /// </summary>
    public const string ArchivePrefix = "_serverconfig/";

    /// <summary>
    /// What is carried, relative to the instance directory.
    /// </summary>
    /// <remarks>
    /// The same set an update preserves, and for the same reason: between them
    /// these four files are everything an administrator configured that does not
    /// live in the world. <c>whitelist.json</c> is the pre-1.16 name Bedrock
    /// still honours and is exactly the kind of file that gets forgotten.
    /// </remarks>
    public static readonly string[] Names =
    [
        "server.properties",
        "allowlist.json",
        "whitelist.json",
        "permissions.json",
    ];

    /// <summary>The archive entry name for one configuration file.</summary>
    public static string EntryFor(string fileName) => ArchivePrefix + fileName;

    /// <summary>True when an archive entry is configuration rather than world.</summary>
    public static bool IsConfigEntry(string entryName) =>
        entryName.StartsWith(ArchivePrefix, StringComparison.Ordinal);

    /// <summary>The file name an entry refers to, or null when it is not one.</summary>
    public static string? FileNameOf(string entryName)
    {
        if (!IsConfigEntry(entryName))
        {
            return null;
        }

        var name = entryName[ArchivePrefix.Length..];

        // Nothing nested, and nothing that could climb out. These are four flat
        // files by definition, so anything else in this namespace is not
        // something the wrapper wrote.
        return name.Length > 0 && !name.Contains('/') && !name.Contains('\\') && name != ".."
            ? name
            : null;
    }

    /// <summary>Which of the files actually exist in an instance directory.</summary>
    public static IEnumerable<(string Name, string FullPath)> Present(string instanceDirectory)
    {
        foreach (var name in Names)
        {
            var path = Path.Combine(instanceDirectory, name);
            if (File.Exists(path))
            {
                yield return (name, path);
            }
        }
    }
}
