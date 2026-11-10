using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BedrockSentry.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BedrockSentry.Services.Security;

/// <summary>
/// Supplies the TLS certificate Kestrel serves the web UI with.
/// </summary>
/// <remarks>
/// Three sources, per <see cref="TlsOptions.Source"/>: a self-signed certificate the
/// wrapper generates and renews itself, a PFX file on disk, or a certificate already
/// installed in the Windows certificate store.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ServerCertificateProvider
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    private readonly ISentryPaths _paths;
    private readonly IOptionsMonitor<TlsOptions> _options;
    private readonly ILogger<ServerCertificateProvider> _logger;
    private readonly Lock _gate = new();

    private X509Certificate2? _cached;

    public ServerCertificateProvider(
        ISentryPaths paths,
        IOptionsMonitor<TlsOptions> options,
        ILogger<ServerCertificateProvider> logger)
    {
        _paths = paths;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Returns the certificate to bind, loading or generating it on first call.
    /// </summary>
    /// <remarks>
    /// Cached for the lifetime of the process. Kestrel captures the certificate when
    /// the endpoint is configured and will not pick up a replacement, so changing the
    /// certificate requires a service restart either way. Milestone 14's settings page
    /// will need <c>ServerCertificateSelector</c> to change that.
    /// </remarks>
    public X509Certificate2 GetCertificate()
    {
        lock (_gate)
        {
            return _cached ??= Load();
        }
    }

    private X509Certificate2 Load()
    {
        var options = _options.CurrentValue;

        var certificate = options.Source switch
        {
            TlsCertificateSource.PfxFile => LoadFromPfxFile(options),
            TlsCertificateSource.WindowsStore => LoadFromStore(options),
            _ => LoadOrCreateSelfSigned(options),
        };

        // Touch the private key now. Without this an ACL problem or a missing key
        // container surfaces later as a TLS handshake that resets with no useful
        // error — at which point it looks like a network fault rather than a
        // permissions one.
        using (var key = certificate.GetRSAPrivateKey())
        {
            if (key is null)
            {
                throw new InvalidOperationException(
                    $"The certificate '{certificate.Subject}' has no accessible RSA private key. " +
                    "If it came from the certificate store, confirm the service account can read the key.");
            }
        }

        _logger.LogInformation(
            "TLS certificate loaded: subject {Subject}, thumbprint {Thumbprint}, expires {NotAfter:u}.",
            certificate.Subject,
            certificate.Thumbprint,
            certificate.NotAfter);

        return certificate;
    }

    /// <summary>
    /// Where a certificate file's private key is imported: the service account's own
    /// key store, persisted.
    /// </summary>
    /// <remarks>
    /// Persisted because Schannel completes a handshake only with a key in a container,
    /// never an ephemeral one. The account's own store because the machine's refused
    /// the import with "Access denied" under <c>NT SERVICE\&lt;ServiceName&gt;</c>, and
    /// the service never started (<c>T104</c> clause 1); a service account's profile is
    /// loaded for it, and LocalSystem's is its own.
    /// </remarks>
    internal const X509KeyStorageFlags KeyStorage = X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet;

    private X509Certificate2 LoadFromPfxFile(TlsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PfxPath))
        {
            throw new InvalidOperationException(
                $"{TlsOptions.SectionName}:Source is PfxFile but no PfxPath was configured.");
        }

        if (!File.Exists(options.PfxPath))
        {
            throw new FileNotFoundException(
                $"The configured TLS certificate file was not found: '{options.PfxPath}'.",
                options.PfxPath);
        }

        // X509CertificateLoader, not new X509Certificate2(path, password) — the
        // constructors that take a path or a byte array are obsolete as of SYSLIB0057.
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(
                options.PfxPath,
                options.PfxPassword,
                KeyStorage);
        }
        catch (CryptographicException ex)
        {
            // `T144`: the two guards above name the section and the path, and this
            // call — the one an operator is most likely to get wrong — used to raise
            // the loader's own message, which names neither. "The certificate data
            // cannot be read with the provided password" does not say *which*
            // certificate or which setting to go and fix.
            //
            // Deliberately not naming whether a password was configured at all, let
            // alone its value: an empty PfxPassword against an encrypted file and a
            // wrong one fail identically here, and both are answered by the same
            // sentence.
            throw new InvalidOperationException(
                $"The TLS certificate '{options.PfxPath}' could not be read. Check "
                + $"{TlsOptions.SectionName}:PfxPassword — the usual cause is a wrong or "
                + "missing password for this file.",
                ex);
        }
    }

    private X509Certificate2 LoadFromStore(TlsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Thumbprint))
        {
            throw new InvalidOperationException(
                $"{TlsOptions.SectionName}:Source is WindowsStore but no Thumbprint was configured.");
        }

        // certmgr.msc prefixes a copied thumbprint with U+200E (left-to-right mark) and
        // separates the bytes with spaces. Both are invisible in a config file and both
        // make the lookup fail with nothing to see.
        var thumbprint = new string(options.Thumbprint.Where(Uri.IsHexDigit).ToArray());

        if (thumbprint.Length == 0)
        {
            throw new InvalidOperationException(
                $"{TlsOptions.SectionName}:Thumbprint contains no hexadecimal characters.");
        }

        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);

            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);

            if (found.Count > 0)
            {
                return found[0];
            }
        }

        throw new InvalidOperationException(
            $"No certificate with thumbprint '{thumbprint}' was found in the LocalMachine or CurrentUser personal store. " +
            "The service reads LocalMachine; a certificate imported into a user's store is not visible to it.");
    }

    private X509Certificate2 LoadOrCreateSelfSigned(TlsOptions options)
    {
        var pfxPath = SecretFiles.Certificate(_paths);
        var passwordPath = SecretFiles.CertificatePassword(_paths);

        if (File.Exists(pfxPath) && File.Exists(passwordPath))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(
                    pfxPath,
                    ReadProtectedPassword(passwordPath),
                    KeyStorage);

                var remaining = existing.NotAfter - DateTime.Now;
                var missing = CertificateNames.Missing(existing, options.AdditionalSubjectAltNames);

                if (missing.Count > 0)
                {
                    // T324: the names are written only when a certificate is made, so one
                    // added to the configuration since was never applied.
                    _logger.LogWarning(
                        "The self-signed certificate does not name {Missing}, which "
                        + "{Section}:AdditionalSubjectAltNames asks for; generating a replacement.",
                        string.Join(", ", missing),
                        TlsOptions.SectionName);
                }
                else if (remaining > TimeSpan.FromDays(options.RenewWithinDays))
                {
                    return existing;
                }
                else
                {
                    _logger.LogInformation(
                        "The self-signed certificate expires in {Days} days; generating a replacement.",
                        (int)remaining.TotalDays);
                }

                existing.Dispose();
            }
            catch (CryptographicException ex)
            {
                _logger.LogWarning(ex,
                    "The stored self-signed certificate could not be loaded; generating a replacement.");
            }
        }

        return CreateSelfSigned(options, pfxPath, passwordPath);
    }

    private X509Certificate2 CreateSelfSigned(TlsOptions options, string pfxPath, string passwordPath)
    {
        var hostName = Dns.GetHostName();

        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={hostName}, O=BedrockSentry"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], critical: false));

        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(hostName);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);

        // Loopback entries are load-bearing rather than cosmetic: the server binds to
        // localhost only until first-run setup completes, so without these the very
        // first connection an admin makes fails its name check.
        var (addresses, names) = CertificateNames.Split(options.AdditionalSubjectAltNames);

        foreach (var address in addresses)
        {
            san.AddIpAddress(address);
        }

        foreach (var name in names)
        {
            san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;

        using var generated = request.CreateSelfSigned(
            now.AddMinutes(-5),
            now.AddDays(options.SelfSignedValidityDays));

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        Directory.CreateDirectory(_paths.Certificates);

        File.WriteAllBytes(pfxPath, generated.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password));
        WriteProtectedPassword(passwordPath, password);

        WindowsAcl.SecureSecretFile(pfxPath);
        WindowsAcl.SecureSecretFile(passwordPath);

        _logger.LogWarning(
            "Generated a self-signed TLS certificate for {HostName}, valid until {NotAfter:u}. " +
            "Browsers will warn until it is trusted or replaced with a real certificate.",
            hostName,
            generated.NotAfter);

        // Reload from disk rather than returning the in-memory instance. The generated
        // certificate's key is ephemeral; the reloaded one is bound to a key container,
        // which is what Schannel needs to complete a handshake (KeyStorage).
        return X509CertificateLoader.LoadPkcs12FromFile(
            pfxPath,
            password,
            KeyStorage);
    }

    /// <summary>
    /// Reads the DPAPI-protected PFX password.
    /// </summary>
    /// <remarks>
    /// The directory ACL is the real control here — this is defence in depth for the
    /// case where the file is copied somewhere less protected, since LocalMachine-scoped
    /// DPAPI ciphertext is useless on another machine (<c>D12</c>).
    /// </remarks>
    private static string ReadProtectedPassword(string path) =>
        Encoding.UTF8.GetString(
            ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.LocalMachine));

    private static void WriteProtectedPassword(string path, string password) =>
        File.WriteAllBytes(
            path,
            ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.LocalMachine));
}
