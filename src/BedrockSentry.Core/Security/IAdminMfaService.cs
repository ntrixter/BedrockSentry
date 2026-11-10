namespace BedrockSentry.Core.Security;

/// <summary>What checking a second-step code found (<c>D125</c>).</summary>
public enum MfaCheck
{
    /// <summary>Not a current code, not an unused recovery code, or already used.</summary>
    Rejected = 0,

    /// <summary>A current authenticator code.</summary>
    Accepted = 1,

    /// <summary>An unused recovery code, now used up.</summary>
    AcceptedRecoveryCode = 2,

    /// <summary>
    /// The stored secret cannot be decrypted, which means the host's data-protection
    /// key ring changed. Only a recovery code signs in until the second step is set up
    /// again.
    /// </summary>
    SecretUnreadable = 3,
}

/// <summary>
/// A secret offered for enrolment and not yet confirmed. Nothing is stored until a
/// code from it is confirmed, so an abandoned enrolment changes nothing.
/// </summary>
/// <param name="Secret">Base32, for typing into an app that cannot scan.</param>
/// <param name="ProvisioningUri">The <c>otpauth://</c> address the QR code carries.</param>
/// <param name="QrCodePngDataUri">The QR code, as a <c>data:image/png</c> URI.</param>
public sealed record MfaEnrolment(string Secret, string ProvisioningUri, string QrCodePngDataUri);

/// <summary>
/// The authenticator-app second step on panel sign-in (<c>D125</c>): off by default,
/// switched on by the administrator from Settings.
/// </summary>
public interface IAdminMfaService
{
    /// <summary>True when sign-in asks for a code after the password.</summary>
    bool IsEnabled { get; }

    /// <summary>Unused recovery codes, zero when the step is off.</summary>
    int RecoveryCodesLeft { get; }

    /// <summary>A new secret to show, for <paramref name="account"/> in the app's list.</summary>
    MfaEnrolment BeginEnrolment(string account);

    /// <summary>
    /// Switches the step on with <paramref name="secret"/> when <paramref name="code"/>
    /// is current for it, and returns the recovery codes to show once. Null when the
    /// code is not, and nothing is stored.
    /// </summary>
    /// <remarks>Signs every session out, this one included.</remarks>
    Task<IReadOnlyList<string>?> CompleteEnrolmentAsync(string secret, string code, CancellationToken ct = default);

    /// <summary>Checks a code typed at the second sign-in step, in Settings, or in a confirm dialog.</summary>
    /// <param name="code">What was typed.</param>
    /// <param name="allowRecoveryCode">
    /// False for a confirm dialog: a recovery code is for getting back in, and using one
    /// writes a new security stamp, which would sign out the session confirming.
    /// </param>
    /// <param name="ct">Cancels the wait for another check in progress.</param>
    Task<MfaCheck> VerifyAsync(string code, bool allowRecoveryCode = true, CancellationToken ct = default);

    /// <summary>Replaces the recovery codes and returns the new ones to show once.</summary>
    Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(CancellationToken ct = default);

    /// <summary>Switches the step off. Signs every session out.</summary>
    Task DisableAsync(CancellationToken ct = default);
}
