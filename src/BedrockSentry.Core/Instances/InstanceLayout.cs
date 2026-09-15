namespace BedrockSentry.Core.Instances;

/// <summary>
/// Works out where an instance lives on disk.
///
/// The rule from <c>docs/design.md</c> § 4 is that the instance root is a convenience,
/// not a constraint: an instance may override its working directory outright, so
/// a large world can sit on another volume. Nothing may assume an instance lives
/// beneath the root.
/// </summary>
public static class InstanceLayout
{
    /// <summary>Names Windows refuses to use for a directory, whatever the extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The directory an instance runs from: its own override if it has one,
    /// otherwise a folder under <paramref name="root"/> named after the instance.
    /// </summary>
    public static string ResolveWorkingDirectory(
        string root, string instanceId, InstanceDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        if (!string.IsNullOrWhiteSpace(definition.WorkingDirectory))
        {
            return Path.GetFullPath(definition.WorkingDirectory);
        }

        var folder = string.IsNullOrWhiteSpace(definition.Name) ? instanceId : definition.Name;
        return Path.GetFullPath(Path.Combine(root, SanitiseFolderName(folder, instanceId)));
    }

    /// <summary>
    /// Turns an administrator-chosen display name into something safe to use as a
    /// single directory name.
    ///
    /// The names come from a hand-edited config file, so this is not a hostile
    /// input in the usual sense — but a name containing a separator would place
    /// the instance somewhere other than where the administrator is looking, and
    /// one containing <c>..</c> would place it outside the root entirely. Both are
    /// silent: the server starts, in the wrong folder, and the mistake surfaces as
    /// a missing world.
    /// </summary>
    public static string SanitiseFolderName(string name, string fallback)
    {
        var cleaned = new string(name.Trim()
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)
            .ToArray());

        // Separators survive GetInvalidFileNameChars on some platforms, and a
        // name that is nothing but dots resolves to the parent directory.
        cleaned = cleaned.Replace('/', '-').Replace('\\', '-');

        // Windows silently strips trailing dots and spaces from directory names,
        // so "backup." and "backup" become the same folder while looking distinct
        // in config.
        cleaned = cleaned.TrimEnd('.', ' ');

        if (cleaned.Length == 0 || cleaned.All(c => c == '.'))
        {
            return fallback;
        }

        // CreateDirectory("CON") fails outright on Windows. Renaming beats a
        // start-up crash an administrator cannot interpret.
        var withoutExtension = Path.GetFileNameWithoutExtension(cleaned);
        return ReservedNames.Contains(withoutExtension) ? $"_{cleaned}" : cleaned;
    }
}
