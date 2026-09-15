using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Security;
using Microsoft.Extensions.Options;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Whether the destructive dialogs still ask for the administrator's password.
/// </summary>
/// <remarks>
/// <para>One object read by three dialogs — Remove and Retire on an instance's
/// Configuration tab, Restore on its Backups tab. Stated in three <c>.razor</c>
/// files it would be stated three times and enforced once, which is this
/// codebase's recurring defect and would here mean a dialog that silently keeps
/// asking, or worse, one that silently stops.</para>
///
/// <para><strong>The flag was shared from the start; the two rules built on it were
/// not.</strong> Each dialog wrote its own <c>!PasswordRequired || typed.Length > 0</c>
/// for the confirm button and its own <c>if (!PasswordRequired) return true</c> in the
/// handler — so the one boolean was shared and the two decisions that matter were
/// copied, in files the test suite cannot reach — found by reading them, since
/// nothing could fail. Both now live here, where
/// <c>DestructiveConfirmTests</c> can pin them, and in particular can pin the one
/// agreement nothing else could check: that the button and the handler accept exactly
/// the same inputs.</para>
/// </remarks>
public sealed class DestructiveConfirmPolicy(
    IOptionsMonitor<TestingOptions> options, IAdminCredentialService credentials)
{
    /// <summary>
    /// The banner shown in place of the password field, so the weakened state is
    /// visible where it applies and not only in a log nobody reads.
    /// </summary>
    public const string Waiver =
        "The password check is switched off by configuration "
        + "(Sentry:Testing:SkipDestructiveConfirmPassword). This action will run on confirmation "
        + "alone. Turn it back on before this install is used for anything real.";

    /// <summary>True unless an administrator has turned the check off by hand.</summary>
    /// <remarks>
    /// Read live from the monitor rather than captured, so turning it back on
    /// takes effect without a service restart — the direction that matters, since
    /// leaving it off is the dangerous state.
    /// </remarks>
    public bool PasswordRequired => !options.CurrentValue.SkipDestructiveConfirmPassword;

    /// <summary>
    /// Whether the confirm button may be enabled, given what has been typed so far.
    /// </summary>
    /// <remarks>
    /// A hint rather than the rule — <see cref="Accepts"/> is the rule, and runs again
    /// when the button is pressed. They have to agree about <em>emptiness</em> though,
    /// and that agreement is the thing a page could not check: a button freed while the
    /// handler still rejects is a dialog that looks ready and does nothing, which is the
    /// failure `T172` names and the symmetric one a UI-only observation cannot exclude.
    /// </remarks>
    public bool MayConfirm(string? typedPassword) =>
        !PasswordRequired || !string.IsNullOrEmpty(typedPassword);

    /// <summary>
    /// Whether a destructive action may proceed.
    /// </summary>
    /// <param name="signedInUser">
    /// The name from the current authentication state, not a typed one. The point is to
    /// prove the person at the keyboard is the account already signed in, rather than to
    /// sign in again as somebody else — so an empty name refuses even with a correct
    /// password.
    /// </param>
    /// <param name="typedPassword">What they typed into the dialog.</param>
    public bool Accepts(string? signedInUser, string? typedPassword) =>
        !PasswordRequired
        || (signedInUser is { Length: > 0 }
            && typedPassword is { Length: > 0 }
            && credentials.Verify(signedInUser, typedPassword) is not CredentialVerification.Failed);
}
