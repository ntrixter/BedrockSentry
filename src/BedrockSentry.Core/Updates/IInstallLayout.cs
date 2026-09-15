namespace BedrockSentry.Core.Updates;

/// <summary>
/// The on-disk shape of an installed BedrockSentry, as laid down by <c>install.ps1</c>.
/// </summary>
/// <remarks>
/// <code>
/// InstallRoot\
///   current\                  junction -> versions\&lt;identity&gt;
///   versions\
///     0.3.0-build.56_a1b2c3d\
///     0.3.0-build.57_f0cb4a6\
/// </code>
/// The service's registered image path points at <c>current</c> and never changes.
/// That is deliberate beyond the convenience: a program path that moved with every
/// update would silently invalidate every firewall rule, AppLocker rule and antivirus
/// exclusion keyed to the executable — on a product whose whole job is to listen on
/// a network port.
/// </remarks>
public interface IInstallLayout
{
    /// <summary>Directory holding <c>current</c> and <c>versions</c>.</summary>
    string InstallRoot { get; }

    /// <summary>The <c>current</c> junction the service runs from.</summary>
    string CurrentLink { get; }

    /// <summary>Parent directory of the per-version directories.</summary>
    string VersionsRoot { get; }

    /// <summary>Directory for a given build identity.</summary>
    string VersionDirectory(string identity);

    /// <summary>
    /// True when the wrapper is running from a proper versioned install rather than
    /// from a development build.
    /// </summary>
    /// <remarks>
    /// Self-update is meaningless — and destructive — against a <c>bin\Debug</c>
    /// directory, so everything is gated on this.
    /// </remarks>
    bool IsVersionedInstall { get; }
}
