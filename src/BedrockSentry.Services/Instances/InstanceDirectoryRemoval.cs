using BedrockSentry.Core.Instances;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Decides whether a directory may be deleted along with an instance.
/// </summary>
/// <remarks>
/// <para>
/// This is the most dangerous operation in the wrapper. Everything else it does
/// is recoverable; a recursive delete of the wrong folder is not, and the input
/// is a path an administrator typed into a config file months ago. The working
/// directory override exists precisely so a world can live anywhere, which means
/// it can also point at somewhere catastrophic — a drive root, a documents
/// folder, or BedrockSentry's own data root.
/// </para>
/// <para>
/// So the rule is not "delete what you were told to". It is "delete only
/// something that looks like a Bedrock server directory, and nothing that
/// contains anything else we know about". A refusal costs the administrator one
/// manual delete. The alternative costs them a hard drive.
/// </para>
/// </remarks>
public static class InstanceDirectoryRemoval
{
    /// <summary>Files and folders that make a directory recognisably a BDS server.</summary>
    private static readonly string[] ServerMarkers =
        ["bedrock_server.exe", "server.properties", "worlds", "allowlist.json", "permissions.json"];

    /// <summary>
    /// Why <paramref name="directory"/> must not be deleted, or null when it may
    /// be. <paramref name="protectedPaths"/> are locations the wrapper itself
    /// owns — the data root, the config folder, the instance root.
    /// </summary>
    public static string? Refuse(string? directory, IEnumerable<string> protectedPaths)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return "there is no directory recorded for it";
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"'{directory}' is not a usable path";
        }

        if (!Directory.Exists(full))
        {
            // Already gone. Not a refusal — the caller has nothing to do.
            return null;
        }

        // Shared with the create side (D43) so the two say the same words about the
        // same rule. The sentence is byte-identical to the one that was here.
        if (ReservedSystemDirectories.IsVolumeRoot(full))
        {
            return $"'{full}' is the root of a drive";
        }

        foreach (var reserved in protectedPaths)
        {
            if (string.IsNullOrWhiteSpace(reserved))
            {
                continue;
            }

            var other = Path.TrimEndingDirectorySeparator(Path.GetFullPath(reserved));

            if (full.Equals(other, PathComparison))
            {
                return $"'{full}' is one of BedrockSentry's own folders";
            }

            // Deleting an ancestor takes the descendant with it. A working
            // directory set to C:\ProgramData\BedrockSentry would otherwise
            // remove the config, the credentials and every other instance.
            if (Contains(full, other))
            {
                return $"'{full}' contains '{other}', which BedrockSentry needs";
            }
        }

        // A junction or symlink deletes as a link rather than as its contents,
        // which means the answer to "what did that just remove" depends on
        // details nobody has in mind at the time. Let the administrator do it.
        if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
        {
            return $"'{full}' is a junction or symbolic link";
        }

        var empty = !Directory.EnumerateFileSystemEntries(full).Any();
        if (empty)
        {
            return null;
        }

        // The last guard, and the one that catches a working directory pointed
        // somewhere it never should have been: if there is no sign a Bedrock
        // server ever lived here, this is somebody's folder, not ours.
        var looksLikeAServer = ServerMarkers.Any(marker =>
            File.Exists(Path.Combine(full, marker)) || Directory.Exists(Path.Combine(full, marker)));

        return looksLikeAServer
            ? null
            : $"'{full}' holds files but nothing that looks like a Bedrock server";
    }

    /// <summary>Whether <paramref name="ancestor"/> contains <paramref name="descendant"/>.</summary>
    private static bool Contains(string ancestor, string descendant)
    {
        // The separator matters: without it "C:\Bedrock" would be read as
        // containing "C:\BedrockOther".
        var prefix = ancestor + Path.DirectorySeparatorChar;
        return descendant.StartsWith(prefix, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
