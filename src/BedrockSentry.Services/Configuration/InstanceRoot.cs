namespace BedrockSentry.Services.Configuration;

/// <summary>
/// Checking the folder instance directories will be created under, before it is
/// accepted rather than the first time a server is created in it.
/// </summary>
/// <remarks>
/// This is asked once, during first-run setup, and then lived with. A root that
/// turns out to be unusable does not fail here — it fails much later, when
/// somebody creates their first server, as an error about that server rather
/// than about a choice made weeks earlier. So the cost of getting it wrong is
/// paid at the furthest possible remove from the decision, which is the argument
/// for checking it properly at the moment it is made.
/// </remarks>
public static class InstanceRoot
{
    /// <summary>
    /// Why <paramref name="path"/> cannot be the instance root, or null if it can.
    /// </summary>
    /// <remarks>
    /// Pure and syntactic, so it can be exercised without touching a disk.
    /// Whether the folder can actually be written to is a separate question with
    /// a separate answer — see <see cref="Prepare"/>.
    /// </remarks>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Choose a folder for instances.";
        }

        var trimmed = path.Trim();

        // A relative path would resolve against the service's working directory,
        // which is the install tree — the one place nothing mutable may live,
        // because a self-update replaces it wholesale. Rejecting it here is the
        // same rule ISentryPaths exists to enforce.
        if (!Path.IsPathRooted(trimmed))
        {
            return "Enter a full path, such as D:\\BedrockSentry\\instances.";
        }

        try
        {
            var full = Path.GetFullPath(trimmed);

            // A file where the folder should be is worth naming specifically:
            // "access denied" from a later mkdir sends somebody to permissions
            // when the answer is that the name is already taken.
            if (File.Exists(full))
            {
                return $"{full} is a file, not a folder.";
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That is not a valid folder path.";
        }
    }

    /// <summary>
    /// Whether the chosen root is the one that would be used anyway.
    /// </summary>
    /// <remarks>
    /// Accepting the default must leave the setting *absent*, not written out as
    /// an absolute path. <c>Sentry:Instances:Root</c> is empty by design, meaning
    /// "under the data root" — pinning the resolved path instead would quietly
    /// detach instances from the data root the moment anybody relocated it.
    /// </remarks>
    public static bool IsDefault(string? chosen, string fallback) =>
        !string.IsNullOrWhiteSpace(chosen)
        && Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen.Trim()))
            .Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(fallback)),
                StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates the folder and proves something can be written into it.
    /// </summary>
    /// <remarks>
    /// The write is the point. The service runs as SYSTEM, and a path that exists
    /// and is readable is not necessarily one it can create a world in — a network
    /// share and a folder owned by another account both look fine until the first
    /// server tries to start.
    /// </remarks>
    /// <exception cref="IOException"/>
    /// <exception cref="UnauthorizedAccessException"/>
    public static void Prepare(string path)
    {
        var full = Path.GetFullPath(path.Trim());

        Directory.CreateDirectory(full);

        var probe = Path.Combine(full, $".bedrocksentry-write-test-{Guid.NewGuid():N}");

        try
        {
            File.WriteAllText(probe, string.Empty);
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The probe wrote, which is what was being asked. A leftover
                // zero-byte file is not worth failing setup over.
            }
        }
    }
}
