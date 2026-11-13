using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Security;
using BedrockSentry.Services.Security;
using Microsoft.Extensions.Options;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Whether the destructive dialogs still ask the administrator to prove who they are,
/// and with what: the password, or with the authenticator step on, a current code
/// from the app (<c>D125</c>).
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
///
/// <para><strong>A code in place of the password.</strong> With the authenticator step
/// on, the code proves more than the password does: it is something the attacker who
/// read the password over a shoulder does not have. Recovery codes are refused here,
/// since using one signs every session out, this one included. Wrong codes count
/// against <see cref="SignInThrottle.SecondStepKey"/>, the same count the sign-in's
/// code step keeps, so a stolen session cannot guess codes here faster than at the
/// door.</para>
/// </remarks>
public sealed class DestructiveConfirmPolicy(
    IOptionsMonitor<TestingOptions> options,
    IAdminCredentialService credentials,
    IAdminMfaService mfa,
    SignInThrottle throttle,
    TimeProvider time)
{
    /// <summary>
    /// The banner shown in place of the password field, so the weakened state is
    /// visible where it applies and not only in a log nobody reads.
    /// </summary>
    public const string Waiver =
        "The password check is switched off by configuration "
        + "(Sentry:Testing:SkipDestructiveConfirmPassword). This action will run on confirmation "
        + "alone. Turn it back on before this install is used for anything real.";

    /// <summary>What a dialog says when the password typed is not right.</summary>
    public const string WrongPassword = "That password is not right.";

    /// <summary>What a dialog says when the code typed is not current.</summary>
    public const string WrongCode =
        "That code was not accepted. Use the code the app shows now; each works once, so a code "
        + "just used to sign in means waiting for the next one.";

    /// <summary>What a dialog says when the host can no longer read the authenticator secret.</summary>
    public const string SecretUnreadable =
        "This server can no longer read its authenticator secret. Set the authenticator up again in "
        + "Settings, or clear it with install.ps1 -ClearPanelMfa.";

    /// <summary>True when the dialogs ask for an authenticator code in place of the password.</summary>
    /// <remarks>Read live, so switching the step on or off changes the next dialog opened.</remarks>
    public bool AsksForCode => mfa.IsEnabled;

    /// <summary>The label over the field.</summary>
    public string FieldLabel => AsksForCode
        ? "Confirm with the code from your authenticator app"
        : "Confirm with your panel sign-in password (not your Windows one)";

    /// <summary>The field's <c>type</c>: a code is shown as typed, a password is not.</summary>
    public string FieldType => AsksForCode ? "text" : "password";

    /// <summary>The field's <c>autocomplete</c>, so a password manager offers the right thing.</summary>
    public string FieldAutocomplete => AsksForCode ? "one-time-code" : "current-password";

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
    /// A hint rather than the rule — <see cref="RefusalAsync"/> is the rule, and runs again
    /// when the button is pressed. They have to agree about <em>emptiness</em> though,
    /// and that agreement is the thing a page could not check: a button freed while the
    /// handler still rejects is a dialog that looks ready and does nothing, which is the
    /// failure `T172` names and the symmetric one a UI-only observation cannot exclude.
    /// </remarks>
    public bool MayConfirm(string? typedPassword) =>
        !PasswordRequired || !string.IsNullOrEmpty(typedPassword);

    /// <summary>
    /// Why a destructive action may not proceed, or null when it may.
    /// </summary>
    /// <param name="signedInUser">
    /// The name from the current authentication state, not a typed one. The point is to
    /// prove the person at the keyboard is the account already signed in, rather than to
    /// sign in again as somebody else — so an empty name refuses even with a correct
    /// password or code.
    /// </param>
    /// <param name="typed">What they typed into the dialog: the password, or the code.</param>
    public async Task<string?> RefusalAsync(string? signedInUser, string? typed)
    {
        if (!PasswordRequired)
        {
            return null;
        }

        return await IdentityRefusalAsync(signedInUser, typed).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="RefusalAsync"/> without the configuration waiver, for a dialog the waiver
    /// was never meant to reach (erasing a player).
    /// </summary>
    public async Task<string?> IdentityRefusalAsync(string? signedInUser, string? typed)
    {
        var wrong = AsksForCode ? WrongCode : WrongPassword;

        if (signedInUser is not { Length: > 0 } || typed is not { Length: > 0 })
        {
            return wrong;
        }

        if (!AsksForCode)
        {
            return credentials.Verify(signedInUser, typed) is CredentialVerification.Failed ? wrong : null;
        }

        if (throttle.RefusedUntil(SignInThrottle.SecondStepKey) is { } until)
        {
            return SignInThrottle.RefusalMessage(until, time.GetUtcNow());
        }

        var check = await mfa.VerifyAsync(typed, allowRecoveryCode: false).ConfigureAwait(false);

        if (check is MfaCheck.Accepted)
        {
            return null;
        }

        if (check is MfaCheck.SecretUnreadable)
        {
            return SecretUnreadable;
        }

        throttle.RecordFailure(SignInThrottle.SecondStepKey);
        return wrong;
    }
}
