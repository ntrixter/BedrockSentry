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
    /// Where this instance's Bedrock server executable would be.
    /// </summary>
    public static string ExecutablePath(string workingDirectory, InstanceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return Path.Combine(workingDirectory, definition.Executable);
    }

    /// <summary>
    /// Whether the one file an instance cannot run without is there.
    /// </summary>
    /// <remarks>
    /// <para>Three places ask this and each does something different with the
    /// answer: the panel offers an Install button, adoption refuses a folder that
    /// is not a server, and <c>BedrockProcessFactory</c> answers the launch guard
    /// that keeps a missing server off the watchdog's ladder (<c>T245</c>). The
    /// question is one question, so it is composed once — a path built inline is
    /// how the third caller ends up with a third spelling of the file name.</para>
    ///
    /// <para><strong>It returns three states rather than a bool, and that is
    /// <c>T246</c>.</strong> <see cref="File.Exists(string)"/> answers
    /// <see langword="false"/> for a file it is not allowed to see, so absent and
    /// refused are one value to it — and all three callers then said the file was
    /// not there, which for a present-but-denied executable is a false statement
    /// with an Install button under it. There is deliberately no bool overload:
    /// the caller that wanted one is exactly the caller this defect was made
    /// of.</para>
    /// </remarks>
    public static ServerPresence PresenceOf(
        string workingDirectory, InstanceDefinition definition) =>
        PresenceOf(workingDirectory, definition, NameIsInTheDirectory);

    /// <summary>
    /// <see cref="PresenceOf(string, InstanceDefinition)"/> with the probe injectable.
    /// </summary>
    /// <remarks>
    /// A seam for the reason <c>ServerProperties.ReadFile</c> has one: the
    /// <see cref="ServerPresenceState.Unreadable"/> arm cannot be produced where
    /// this is written. The build runs as root, so a rig that denies itself a file
    /// and then looks for it <em>succeeds</em>, and the arm that renders a claim
    /// about access control would stay uncovered exactly where the code is
    /// (<c>T176</c>).
    /// </remarks>
    internal static ServerPresence PresenceOf(
        string workingDirectory,
        InstanceDefinition definition,
        Func<string, string, bool> nameIsInTheDirectory)
    {
        ArgumentNullException.ThrowIfNull(nameIsInTheDirectory);

        if (File.Exists(ExecutablePath(workingDirectory, definition)))
        {
            return new ServerPresence(ServerPresenceState.Installed);
        }

        // Absent or refused, and File.Exists cannot say which. The PARENT's own
        // index separates them: listing a directory is a right on the directory,
        // and a Deny ACE on one file does not take its name out of that listing.
        // So a name that is there while File.Exists says no is a file this
        // service is not allowed to see.
        try
        {
            return nameIsInTheDirectory(workingDirectory, definition.Executable)
                ? new ServerPresence(ServerPresenceState.Unreadable)
                : new ServerPresence(ServerPresenceState.Missing);
        }
        catch (Exception ex) when (ServerPresence.From(ex) is { } failure)
        {
            // Nothing whatever was established about the file — a different
            // sentence and a different repair from a file that is there and
            // refused. Which failure it was is decided in one place, because the
            // launch's own check asks the identical question and the two would
            // otherwise drift about what a missing folder means (`T252`).
            return failure;
        }
    }

    private static bool NameIsInTheDirectory(string directory, string name) =>
        Directory.EnumerateFileSystemEntries(directory, name).Any();

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
