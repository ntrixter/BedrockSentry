using Microsoft.AspNetCore.Authentication;

namespace BedrockSentry.Services.Security;

/// <summary>
/// How long a sign-in lasts, with and without <strong>Keep me signed in</strong>
/// (<c>D58</c>).
/// </summary>
/// <remarks>
/// <para><strong>Why this exists.</strong> The auth cookie was a session cookie with a
/// 12-hour sliding ticket, so closing the browser — or rebooting — signed the
/// administrator out. That is a defensible default and it had a concrete, repeated
/// cost: an entire unattended Windows test run was lost to it on 2026-09-07, because
/// every item in it needed a signed-in panel and no unauthenticated route can drive
/// one.</para>
///
/// <para><strong>It is opt-in, per sign-in, and off by default.</strong> The checkbox
/// unticked is exactly the behaviour that shipped before, so nobody's security posture
/// changes unless they ask for it.</para>
///
/// <para><strong>Here in <c>Services</c> rather than in <c>Login.razor</c>.</strong> The
/// duration, the two ticket shapes and the wording are one rule; stated in a page the
/// test project cannot reference, it would be enforced nowhere. <c>Core</c> was not an
/// option — <see cref="AuthenticationProperties"/> is ASP.NET, and <c>D9</c> keeps
/// <c>Core</c> free of it.</para>
/// </remarks>
public static class SignInLifetime
{
    /// <summary>How long a remembered sign-in lasts.</summary>
    /// <remarks>
    /// <para>Chosen by the operator. It is long enough to span a holiday and short
    /// enough that a forgotten browser on a shared machine is not an indefinite
    /// door.</para>
    ///
    /// <para><strong>It slides, and that is not an accident of the existing
    /// configuration.</strong> <c>SlidingExpiration</c> renews a ticket using its own
    /// original span rather than <c>ExpireTimeSpan</c>, so a remembered session renews
    /// by fourteen days each time it is used past its halfway point. The promise on the
    /// label is therefore "fourteen days since you last used it", not "fourteen days
    /// from now" — which is what anybody ticking such a box expects, and the help text
    /// says so rather than leaving it to be discovered.</para>
    /// </remarks>
    public static readonly TimeSpan Remembered = TimeSpan.FromDays(14);

    /// <summary>The label on the checkbox.</summary>
    public const string Label = "Keep me signed in on this device";

    /// <summary>
    /// The sentence under it. Says what both states do, because the cost of the
    /// unticked one is the reason this was added.
    /// </summary>
    public static string Help =>
        $"Stays signed in for {Days} days, renewed each time you use the panel — so a reboot or a "
        + "closed browser does not sign you out. Leave it unticked and the sign-in ends when the "
        + "browser closes. Changing the password ends every session either way.";

    /// <summary>The duration in whole days, for the wording.</summary>
    public static int Days => (int)Remembered.TotalDays;

    /// <summary>
    /// The ticket to issue.
    /// </summary>
    /// <param name="remember">Whether the box was ticked.</param>
    /// <param name="now">
    /// Taken rather than read from the clock, so a test can assert the expiry instead of
    /// asserting that some expiry was set.
    /// </param>
    /// <remarks>
    /// <c>ExpiresUtc</c> is left null on the unremembered path on purpose: the cookie
    /// handler then derives the ticket's life from <c>ExpireTimeSpan</c>, which is where
    /// the twelve hours is configured. Writing twelve hours here as well would be the
    /// same number in two places, and the one in <c>Program.cs</c> is the one the
    /// handler obeys.
    /// </remarks>
    public static AuthenticationProperties For(bool remember, DateTimeOffset now) =>
        remember
            ? new AuthenticationProperties { IsPersistent = true, ExpiresUtc = now + Remembered }
            : new AuthenticationProperties { IsPersistent = false };
}
