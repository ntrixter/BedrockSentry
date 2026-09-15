using System.ComponentModel.DataAnnotations;

namespace BedrockSentry.Core.Configuration;

/// <summary>Where the wrapper's TLS certificate comes from.</summary>
public enum TlsCertificateSource
{
    /// <summary>Generate and self-renew a self-signed certificate. The default.</summary>
    SelfSigned = 0,

    /// <summary>Load a PFX file from disk, using <see cref="TlsOptions.PfxPassword"/>.</summary>
    PfxFile = 1,

    /// <summary>Find a certificate in the Windows certificate store by thumbprint.</summary>
    WindowsStore = 2,
}

/// <summary>
/// TLS configuration, bound from the <c>Sentry:Tls</c> configuration section.
/// </summary>
/// <remarks>
/// Deliberately <em>not</em> Kestrel's own <c>Kestrel:Endpoints</c> schema.
/// Two reasons. Kestrel's certificate schema has no thumbprint key at all, and
/// supplying one is silently ignored rather than rejected — the server comes up
/// serving a different certificate than the operator configured, with nothing in
/// the log. And endpoints defined in configuration are cumulative with endpoints
/// defined in code, not overriding, so shipping both produces a port bind
/// conflict at startup. The host asserts that no <c>Kestrel</c> section exists.
/// </remarks>
public sealed class TlsOptions
{
    public const string SectionName = "Sentry:Tls";

    /// <summary>Which of the three certificate sources to use.</summary>
    public TlsCertificateSource Source { get; set; } = TlsCertificateSource.SelfSigned;

    /// <summary>HTTPS port for the web UI.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 8443;

    /// <summary>Path to a PFX file. Required when <see cref="Source"/> is <see cref="TlsCertificateSource.PfxFile"/>.</summary>
    public string? PfxPath { get; set; }

    /// <summary>Password for <see cref="PfxPath"/>.</summary>
    public string? PfxPassword { get; set; }

    /// <summary>
    /// Certificate thumbprint. Required when <see cref="Source"/> is
    /// <see cref="TlsCertificateSource.WindowsStore"/>.
    /// </summary>
    /// <remarks>
    /// Non-hex characters are stripped before lookup: copying a thumbprint out of
    /// certmgr.msc brings an invisible left-to-right mark with it, and the
    /// resulting "certificate not found" is impossible to see by eye.
    /// </remarks>
    public string? Thumbprint { get; set; }

    /// <summary>
    /// Extra DNS names to place in the self-signed certificate's SAN, on top of
    /// the machine name, <c>localhost</c>, <c>127.0.0.1</c>, and <c>::1</c>.
    /// </summary>
    public IList<string> AdditionalSubjectAltNames { get; set; } = [];

    /// <summary>Lifetime of a generated self-signed certificate, in days.</summary>
    /// <remarks>
    /// Defaults to 398 because browsers reject publicly-trusted server
    /// certificates with longer lifetimes. Whether that cap also applies to a
    /// locally-installed trust anchor is unconfirmed, so this stays conservative.
    /// Renewal happens at service start when fewer than
    /// <see cref="RenewWithinDays"/> remain, which means a service that runs
    /// uninterrupted past expiry will not renew itself. Self-update restarts make
    /// that unlikely in practice, but it is a real limitation rather than an
    /// oversight.
    /// </remarks>
    [Range(1, 3650)]
    public int SelfSignedValidityDays { get; set; } = 398;

    /// <summary>Regenerate a self-signed certificate when fewer than this many days remain.</summary>
    [Range(1, 365)]
    public int RenewWithinDays { get; set; } = 30;
}
