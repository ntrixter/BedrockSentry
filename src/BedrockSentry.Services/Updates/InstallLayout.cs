using BedrockSentry.Core.Updates;

namespace BedrockSentry.Services.Updates;

/// <inheritdoc cref="IInstallLayout"/>
public sealed class InstallLayout : IInstallLayout
{
    private const string CurrentLinkName = "current";
    private const string VersionsDirectoryName = "versions";

    public InstallLayout(string? baseDirectory = null)
    {
        // The running assembly sits in InstallRoot\current\, so the install root is
        // two levels up. Derived rather than configured so it cannot drift out of
        // agreement with where the service was actually registered from.
        var here = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        RunningDirectory = here;

        var parent = Path.GetDirectoryName(here);

        InstallRoot = parent ?? here;
        CurrentLink = Path.Combine(InstallRoot, CurrentLinkName);
        VersionsRoot = Path.Combine(InstallRoot, VersionsDirectoryName);

        IsVersionedInstall =
            string.Equals(Path.GetFileName(here), CurrentLinkName, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(VersionsRoot);
    }

    /// <summary>The directory this process is actually running from.</summary>
    public string RunningDirectory { get; }

    public string InstallRoot { get; }

    public string CurrentLink { get; }

    public string VersionsRoot { get; }

    public bool IsVersionedInstall { get; }

    /// <summary>
    /// Maps a build identity to its directory name.
    /// </summary>
    /// <remarks>
    /// '+' is replaced because although it is a legal path character, enough tooling
    /// mishandles it to be worth avoiding. Must match <c>install.ps1</c>.
    /// </remarks>
    public string VersionDirectory(string identity) =>
        Path.Combine(VersionsRoot, identity.Replace('+', '_'));

    /// <summary>
    /// The version directory the <c>current</c> junction resolves to, or null if it
    /// cannot be determined.
    /// </summary>
    public string? ResolveCurrentVersionDirectory()
    {
        try
        {
            var info = new DirectoryInfo(CurrentLink);

            // LinkTarget is null when this is an ordinary directory rather than a
            // junction — which is the case for a development build.
            return info.LinkTarget is { } target
                ? Path.GetFullPath(target)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
