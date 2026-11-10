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

    private readonly Action<string>? _lockNewDataRoot;

    /// <param name="dataRoot">The data root; null reads the environment, then ProgramData.</param>
    /// <param name="lockNewDataRoot">
    /// What <see cref="EnsureCreated"/> does to a data root it had to create; null is
    /// <see cref="AccessPlan.ForDataRoot"/> for this process's account on Windows.
    /// </param>
    public SentryPaths(string? dataRoot = null, Action<string>? lockNewDataRoot = null)
    {
        _lockNewDataRoot = lockNewDataRoot;

        DataRoot = dataRoot
            ?? Environment.GetEnvironmentVariable(DataRootEnvironmentVariable)
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "BedrockSentry");

        DataRoot = Path.GetFullPath(DataRoot);
    }

    /// <summary>
    /// The value of <c>--dataroot</c>, which <c>install.ps1</c> writes into each
    /// service's ImagePath (<c>D36</c>); null when it is absent. Read by both services,
    /// so the broker resolves the same root the main service writes.
    /// </summary>
    /// <exception cref="ArgumentException">The switch is there with no path after it.</exception>
    public static string? DataRootArgument(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];

            if (argument.StartsWith("--dataroot=", StringComparison.OrdinalIgnoreCase))
            {
                return NonEmpty(argument["--dataroot=".Length..]);
            }

            if (!argument.Equals("--dataroot", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return NonEmpty(i + 1 < args.Count ? args[i + 1] : null);
        }

        return null;

        // A registration carrying --dataroot with nothing usable after it is malformed.
        // Falling through to ProgramData would put the data somewhere the operator
        // explicitly did not ask for, and would look like it had worked, so this fails
        // the start instead. It throws before logging exists — see T104 — which is a
        // known gap in its own right rather than something this switch introduces.
        static string NonEmpty(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("--dataroot was given without a path.")
                : value;
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

    public string AddonLibrary => Path.Combine(DataRoot, "addon-library");

    public string Completion => Path.Combine(DataRoot, "completion");

    public string PlayerFaces => Path.Combine(DataRoot, "player-faces");

    private string? _backupsOverride;
    private string? _serverPackagesOverride;

    private void LockNewDataRoot(string root)
    {
        if (_lockNewDataRoot is { } lockRoot)
        {
            lockRoot(root);
        }
        else if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            WindowsAcl.Apply(new DirectoryInfo(root), AccessPlan.ForDataRoot(identity.User!.Value));
        }
    }

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
        // The installer owns the data root's access list (milestone 18.5): it grants the
        // service's account Modify, which carries no WRITE_DAC, so a service that re-locked
        // the root here at every start would fail at its first. Only a root this call
        // creates is locked, before anything is created beneath it: ProgramData grants
        // BUILTIN\Users read by default and children inherit it, so a certificate written
        // first and secured second would be briefly world-readable.
        if (!Directory.Exists(DataRoot))
        {
            Directory.CreateDirectory(DataRoot);
            LockNewDataRoot(DataRoot);
        }

        foreach (var dir in new[]
                 {
                     Config, Certificates, DataProtectionKeys, Data, Update, Logs, InstanceLogs, Instances,
                     ServerPackages, AddonLibrary, Completion, PlayerFaces,
                 })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
