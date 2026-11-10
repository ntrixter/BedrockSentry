using BedrockSentry.Core.Configuration;

namespace BedrockSentry.Services.Security;

/// <summary>
/// The files under the data root that hold a credential, the setup token or the
/// certificate's key, and where each lives.
/// </summary>
/// <remarks>
/// Each is written with inheritance broken (<see cref="AccessPlan.ForSecretFile"/>), so
/// a grant on the data root does not reach it. One list, read by every writer and by
/// <see cref="DataRootPreparation"/>, so a secret added here is re-granted on a
/// reinstall and a secret added anywhere else is not a secret this code knows of.
/// </remarks>
public static class SecretFiles
{
    public const string AuthFileName = "auth.json";
    public const string SetupTokenFileName = "setup-token.txt";
    public const string CertificateFileName = "bedrocksentry.pfx";
    public const string CertificatePasswordFileName = "bedrocksentry.pfx.pwd";

    /// <summary>The administrator credential, kept apart from operational settings.</summary>
    public static string Auth(ISentryPaths paths) => Path.Combine(paths.Config, AuthFileName);

    /// <summary>The first-run setup token.</summary>
    public static string SetupToken(ISentryPaths paths) => Path.Combine(paths.Config, SetupTokenFileName);

    /// <summary>The generated TLS certificate with its private key.</summary>
    public static string Certificate(ISentryPaths paths) => Path.Combine(paths.Certificates, CertificateFileName);

    /// <summary>The DPAPI-protected password of <see cref="Certificate"/>.</summary>
    public static string CertificatePassword(ISentryPaths paths) =>
        Path.Combine(paths.Certificates, CertificatePasswordFileName);

    /// <summary>Every one of them, whether or not it exists yet.</summary>
    public static IReadOnlyList<string> All(ISentryPaths paths) =>
        [Auth(paths), SetupToken(paths), Certificate(paths), CertificatePassword(paths)];
}
