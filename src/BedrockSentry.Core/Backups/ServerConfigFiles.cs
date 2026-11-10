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
/// Backups written before <c>D110</c> stored them under a reserved prefix,
/// because the archive's root was the <c>worlds/</c> namespace. From <c>D110</c>
/// a backup is laid out as the server's own folder: the world under
/// <c>worlds/</c> and these files at the root, where BDS reads them, so an
/// unzipped backup with <c>bedrock_server.exe</c> dropped in is a server.
/// <see cref="NewServerWorld"/> reads both layouts.
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
    /// Archive prefix for configuration entries in a backup written before
    /// <c>D110</c>. Read, never written.
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

    /// <summary>The entry name for one configuration file in a backup written before <c>D110</c>.</summary>
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
