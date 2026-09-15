using BedrockSentry.Core.Configuration;

namespace BedrockSentry.Core.Security;

/// <summary>Outcome of checking a submitted password against the stored hash.</summary>
public enum CredentialVerification
{
    /// <summary>Wrong password, or no credential is configured.</summary>
    Failed = 0,

    /// <summary>Correct.</summary>
    Success = 1,

    /// <summary>
    /// Correct, but the stored hash used weaker parameters than current policy.
    /// The caller should rehash and persist while it has the plaintext.
    /// </summary>
    SuccessRehashNeeded = 2,
}

/// <summary>
/// Hashes and verifies the administrator password, and persists the credential.
/// </summary>
/// <remarks>
/// The interface lives in Core (platform-neutral <c>net10.0</c>) while the
/// implementation lives in Services. That split is load-bearing: hashing pulls in
/// <c>Microsoft.AspNetCore.App</c>, and referencing that from Core would forfeit
/// exactly the platform-neutrality guarantee D9 exists to provide.
/// </remarks>
public interface IAdminCredentialService
{
    /// <summary>True when an administrator account has been set up.</summary>
    bool IsConfigured { get; }

    /// <summary>Hashes <paramref name="password"/> using current policy.</summary>
    string HashPassword(string password);

    /// <summary>
    /// Checks a submitted username and password against the stored credential.
    /// </summary>
    CredentialVerification Verify(string username, string password);

    /// <summary>
    /// Writes a new credential to disk and reloads configuration so it takes
    /// effect immediately.
    /// </summary>
    /// <remarks>
    /// Issues a fresh security stamp, which invalidates existing sessions.
    /// </remarks>
    Task SetCredentialAsync(string username, string password, CancellationToken ct = default);

    /// <summary>Current credential snapshot, for reading the security stamp.</summary>
    AdminCredentialOptions Current { get; }
}
