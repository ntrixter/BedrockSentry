using BedrockSentry.Services.Security;

namespace BedrockSentry.Tests;

/// <summary>
/// <c>D58</c>. What <strong>Keep me signed in</strong> does, and what leaving it
/// unticked does.
/// </summary>
/// <remarks>
/// The rule had nowhere to live but <c>Login.razor</c>, which this suite cannot
/// reference — so it is in <c>Services</c>, and these are the assertions that would
/// otherwise have had to be made by signing in on a real box and waiting two weeks.
/// </remarks>
public class SignInLifetimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Unticked_is_a_session_sign_in_exactly_as_before()
    {
        var properties = SignInLifetime.For(remember: false, Now);

        Assert.False(properties.IsPersistent);

        // Null rather than twelve hours: the handler derives the ticket's life from
        // ExpireTimeSpan in Program.cs, and writing the number here too would be the
        // same value in two places with only one of them obeyed.
        Assert.Null(properties.ExpiresUtc);
    }

    [Fact]
    public void Ticked_lasts_two_weeks_from_the_sign_in()
    {
        var properties = SignInLifetime.For(remember: true, Now);

        Assert.True(properties.IsPersistent);
        Assert.Equal(Now.AddDays(14), properties.ExpiresUtc);
    }

    /// <summary>
    /// The operator asked for two weeks. Pinned as a number rather than left to the
    /// constant, so shortening or lengthening it is a deliberate edit to a test that
    /// says who chose it.
    /// </summary>
    [Fact]
    public void Two_weeks_means_fourteen_days()
    {
        Assert.Equal(TimeSpan.FromDays(14), SignInLifetime.Remembered);
        Assert.Equal(14, SignInLifetime.Days);
    }

    /// <summary>
    /// THE ONE THAT MATTERS FOR HONESTY. The help text has to describe both states and
    /// the renewal, because all three are things somebody would otherwise discover by
    /// being signed out when they did not expect it.
    /// </summary>
    /// <remarks>
    /// The renewal clause in particular: <c>SlidingExpiration</c> renews a ticket by its
    /// own original span, so a remembered session is "fourteen days since you last used
    /// it" rather than "fourteen days from now". A label that implied the latter would
    /// understate it, and one that said nothing would leave the difference to be guessed.
    /// </remarks>
    [Fact]
    public void The_help_text_describes_both_states_the_renewal_and_the_password_case()
    {
        var help = SignInLifetime.Help;

        Assert.Contains("14 days", help);
        Assert.Contains("renewed", help);
        Assert.Contains("unticked", help);
        Assert.Contains("browser closes", help);

        // A password change invalidates every session through the security stamp,
        // remembered or not. Somebody trusting a two-week box is entitled to know that
        // is still their way to revoke it.
        Assert.Contains("password", help);
    }

    [Fact]
    public void The_label_says_it_is_per_device()
    {
        // It is a cookie. Ticking it on one machine says nothing about another, and the
        // label should not imply an account-wide setting.
        Assert.Contains("this device", SignInLifetime.Label);
    }
}
