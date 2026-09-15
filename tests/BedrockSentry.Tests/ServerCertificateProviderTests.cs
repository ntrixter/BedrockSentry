using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// What an operator is told when the configured TLS certificate will not load
/// (`T144-PfxPasswordNoContext`).
/// </summary>
/// <remarks>
/// The two guards before the loader — no `PfxPath`, and a path that is not there —
/// already quote the section and the file. The load itself did not, so the case an
/// operator is most likely to get wrong was the one case with no help: a bare
/// *"The certificate data cannot be read with the provided password"*, naming
/// neither the file nor the setting to go and change.
///
/// Windows-gated because the provider is `[SupportedOSPlatform("windows")]` and the
/// key storage flags it passes are Windows-only. That means CI proves this and the
/// Linux side cannot, which is the same split every certificate test here lives with.
/// </remarks>
public class ServerCertificateProviderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bs-cert-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    private ServerCertificateProvider Provider(TlsOptions options) =>
        new(new SentryPaths(_root),
            new StaticOptionsMonitor<TlsOptions>(options),
            NullLogger<ServerCertificateProvider>.Instance);

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "The certificate provider is Windows-only.";
            }
        }
    }

    [WindowsFact]
    public void A_pfx_that_will_not_open_names_the_file_and_the_setting()
    {
        Directory.CreateDirectory(_root);

        var pfxPath = Path.Combine(_root, "server.pfx");

        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest(
                "CN=BedrockSentry Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            File.WriteAllBytes(pfxPath, certificate.Export(X509ContentType.Pfx, "right-password"));
        }

        var failure = Assert.Throws<InvalidOperationException>(() =>
            Provider(new TlsOptions
            {
                Source = TlsCertificateSource.PfxFile,
                PfxPath = pfxPath,
                PfxPassword = "wrong-password",
            }).GetCertificate());

        // The three things a person needs in order to act, and the one thing that
        // must not be there.
        Assert.Contains(pfxPath, failure.Message);
        Assert.Contains("PfxPassword", failure.Message);
        Assert.IsType<CryptographicException>(failure.InnerException);
        Assert.DoesNotContain("wrong-password", failure.Message);
    }
}
