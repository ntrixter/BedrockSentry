namespace BedrockSentry.Core.Configuration;

/// <summary>
/// The single web-UI administrator credential, bound from <c>Sentry:Admin</c>.
/// </summary>
/// <remarks>
/// <para>
/// Lives in its own file (<c>auth.json</c>) rather than alongside operational
/// settings, and that file is loaded with change-watching <em>off</em>. Both
/// choices are deliberate.
/// </para>
/// <para>
/// When a watched JSON file becomes malformed, the configuration provider
/// resets its keys to empty rather than keeping the last good values. If
/// credentials were watched, a single typo during a hand-edit would blank
/// <see cref="PasswordHash"/>, which reads as "never configured" — signing every
/// admin out and reopening the unauthenticated first-run wizard on a
/// network-reachable box. That is a credential-takeover path with a typo as its
/// trigger. A file that is never watched cannot be blanked by a reload.
/// </para>
/// <para>
/// The file boundary also makes the export/import tool's "excludes auth
/// credentials" rule (SPEC section 13) structural rather than a filter someone
/// has to remember to apply.
/// </para>
/// </remarks>
public sealed class AdminCredentialOptions
{
    public const string SectionName = "Sentry:Admin";

    public string? Username { get; set; }

    /// <summary>
    /// Opaque password hash. <strong>Never the password itself</strong>, anywhere it is
    /// stored, logged or exported — including the config file the first-run wizard writes.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Changes whenever the credential changes, so existing sessions can be
    /// invalidated after a password change.
    /// </summary>
    public string? SecurityStamp { get; set; }

    /// <summary>When the credential was last set. Informational.</summary>
    public DateTimeOffset? UpdatedUtc { get; set; }

    /// <summary>
    /// The authenticator-app second step (<c>D125</c>), or null when it is off.
    /// </summary>
    public AdminMfaOptions? Mfa { get; set; }

    /// <summary>True when sign-in asks for an authenticator code after the password.</summary>
    public bool MfaEnabled => !string.IsNullOrWhiteSpace(Mfa?.Secret);

    /// <summary>True when a usable credential is present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(PasswordHash);
}

/// <summary>
/// The stored half of the authenticator-app second step, bound from
/// <c>Sentry:Admin:Mfa</c> (<c>D125</c>).
/// </summary>
/// <remarks>
/// Under <c>Sentry:Admin</c> so it lives in <c>auth.json</c> and the settings export
/// leaves it out for the same structural reason it leaves out the password hash.
/// </remarks>
public sealed class AdminMfaOptions
{
    /// <summary>
    /// The shared secret, encrypted with the host's data-protection key ring. Unlike the
    /// password it cannot be hashed: the host has to compute the same codes the phone
    /// does.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// SHA-256 hashes of the unused recovery codes, hex, separated by <c>;</c>. One
    /// string rather than an array, so a used code's removal is a plain value write.
    /// </summary>
    public string? RecoveryCodes { get; set; }

    /// <summary>When the second step was switched on. Informational.</summary>
    public DateTimeOffset? EnabledUtc { get; set; }
}
