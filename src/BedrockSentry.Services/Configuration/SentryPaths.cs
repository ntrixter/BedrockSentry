using BedrockSentry.Core.Configuration;
using BedrockSentry.Services.Security;

namespace BedrockSentry.Services.Configuration;

/// <inheritdoc cref="ISentryPaths"/>
public sealed class SentryPaths : ISentryPaths
{
    /// <summary>
    /// Environment variable that relocates the entire data root.
    /// </summary>
    /// <remarks>
    /// Exists mainly for tests and for admins who keep application data off the
    /// system volume. Because every path derives from <see cref="DataRoot"/>,
    /// setting this moves certificates and key material along with the config —
    /// which is exactly why nothing is allowed to compute its own directory.
    /// </remarks>
    public const string DataRootEnvironmentVariable = "BEDROCKSENTRY_DATAROOT";

    public SentryPaths(string? dataRoot = null)
    {
        DataRoot = dataRoot
            ?? Environment.GetEnvironmentVariable(DataRootEnvironmentVariable)
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "BedrockSentry");

        DataRoot = Path.GetFullPath(DataRoot);
    }

    public string DataRoot { get; }

    public string Config => Path.Combine(DataRoot, "config");

    public string Certificates => Path.Combine(DataRoot, "certs");

    public string DataProtectionKeys => Path.Combine(DataRoot, "keys");

    public string Data => Path.Combine(DataRoot, "data");

    public string Backups => _backupsOverride ?? Path.Combine(DataRoot, "backups");

    public string Update => Path.Combine(DataRoot, "update");

    public string Logs => Path.Combine(DataRoot, "logs");

    public string InstanceLogs => Path.Combine(Logs, "instances");

    public string Instances => Path.Combine(DataRoot, "instances");

    public string ServerPackages => _serverPackagesOverride ?? Path.Combine(DataRoot, "server-packages");

    private string? _backupsOverride;
    private string? _serverPackagesOverride;

    /// <summary>
    /// Points the space-hungry folders somewhere else (<c>docs/design.md</c> § 4.3).
    /// </summary>
    /// <remarks>
    /// Backups and server packages are the two directories under the data root
    /// that grow without bound, and the only ones an administrator has asked to
    /// put on another volume. They can be plain configuration — unlike the data
    /// root itself, whose location has to be known before configuration can be
    /// read — and because every consumer resolves these properties per call
    /// rather than caching them, a change here takes effect on the next backup
    /// or download with no restart. Blank or null means the default under the
    /// data root. Applied at startup from <c>Sentry:Storage</c> and again, live,
    /// when the Settings page saves a change.
    /// </remarks>
    public void ApplyStorageOverrides(string? backupsRoot, string? serverPackagesRoot)
    {
        _backupsOverride = string.IsNullOrWhiteSpace(backupsRoot)
            ? null
            : Path.GetFullPath(backupsRoot);

        _serverPackagesOverride = string.IsNullOrWhiteSpace(serverPackagesRoot)
            ? null
            : Path.GetFullPath(serverPackagesRoot);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);

        // Lock the root down before creating anything beneath it. ProgramData
        // grants BUILTIN\Users read access by default and children inherit it, so
        // a certificate written first and secured second is briefly world-readable.
        if (OperatingSystem.IsWindows())
        {
            WindowsAcl.RestrictToSystemAndAdministrators(DataRoot);
        }

        foreach (var dir in new[]
                 {
                     Config, Certificates, DataProtectionKeys, Data, Update, Logs, InstanceLogs, Instances,
                     ServerPackages,
                 })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
