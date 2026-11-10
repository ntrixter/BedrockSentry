namespace BedrockSentry.Updater;

/// <summary>Where the helper may run from, relative to the install it changes.</summary>
/// <remarks>
/// Apart from <see cref="UpdateRunner"/> because it needs nothing Windows-only, so the
/// suite compiles this file on its own, as it does <see cref="HealthProbe"/>.
/// </remarks>
internal static class HelperLocation
{
    /// <summary>
    /// Whether <paramref name="helperDirectory"/> is inside a build the helper would
    /// change: under <c>versions</c>, which it prunes, or under <c>current</c>, which it
    /// flips. The install root's own <c>update</c> folder is neither; the privilege
    /// broker stages the helper there so it runs from a folder the service cannot write.
    /// </summary>
    public static bool InsideABuild(string helperDirectory, string installRoot)
    {
        var here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(helperDirectory)) + Path.DirectorySeparatorChar;

        return new[] { "versions", "current" }.Any(name =>
            here.StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(installRoot, name))) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }
}
