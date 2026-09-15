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

    /// <summary>True when a usable credential is present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(PasswordHash);
}
