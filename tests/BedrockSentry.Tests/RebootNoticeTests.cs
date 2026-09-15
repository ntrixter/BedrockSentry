using BedrockSentry.Core.Diagnostics;

namespace BedrockSentry.Tests;

/// <summary>
/// The reboot warning says what is actually pending (<c>T188</c>).
/// </summary>
/// <remarks>
/// <para>The arm that lied is <see cref="RebootUrgency.Queued"/>, so it is first.
/// Three registry signals were ORed into one <c>bool</c> and given the severe
/// headline, so <c>PendingFileRenameOperations</c> — a passive queue that cannot
/// restart anything — carried the claim that Windows was about to take every server
/// down. That banner stood for two days on a machine with nothing whatever pending,
/// fired by OneDrive queuing its own previous version for deletion.</para>
///
/// <para>These assertions are about what a sentence <em>claims</em>, which is the
/// thing that was wrong; the panel that renders them is a <c>.razor</c> the suite
/// cannot reach, which is why the rule is here and not there.</para>
/// </remarks>
public class RebootNoticeTests
{
    private static RebootSignal Queue() =>
        new("files are queued to be removed or replaced on the next restart", false);

    private static RebootSignal Update() =>
        new("Windows Update is waiting on a restart", true);

    private static RebootSignal Component() =>
        new("a component install is waiting on a restart", true);

    [Fact]
    public void A_queue_that_cannot_restart_anything_does_not_claim_a_restart_is_coming()
    {
        var urgency = RebootNotice.UrgencyOf([Queue()]);

        Assert.Equal(RebootUrgency.Queued, urgency);

        var headline = RebootNotice.Headline(urgency);
        var consequence = RebootNotice.Consequence(urgency);

        Assert.NotNull(headline);
        Assert.DoesNotContain("waiting to restart", headline);
        Assert.DoesNotContain("every server", consequence);
        Assert.DoesNotContain("moment of its choosing", consequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_signal_that_can_restart_the_machine_still_says_so(bool alsoQueued)
    {
        RebootSignal[] signals = alsoQueued ? [Queue(), Update()] : [Update()];

        var urgency = RebootNotice.UrgencyOf(signals);

        Assert.Equal(RebootUrgency.Restarting, urgency);
        Assert.Contains("waiting to restart", RebootNotice.Headline(urgency));
        Assert.Contains("every server", RebootNotice.Consequence(urgency));
    }

    /// <summary>
    /// The strongest signal wins wherever it sits in the list.
    /// </summary>
    /// <remarks>
    /// The mild one is first here on purpose: a first-match-wins implementation that
    /// read the list in order would pass every other case in this file.
    /// </remarks>
    [Fact]
    public void The_mild_signal_first_does_not_hide_a_real_one_behind_it()
    {
        Assert.Equal(
            RebootUrgency.Restarting,
            RebootNotice.UrgencyOf([Queue(), Queue(), Component()]));
    }

    [Fact]
    public void Nothing_pending_says_nothing()
    {
        var urgency = RebootNotice.UrgencyOf([]);

        Assert.Equal(RebootUrgency.None, urgency);
        Assert.Null(RebootNotice.Headline(urgency));
        Assert.Null(RebootNotice.Consequence(urgency));
    }

    /// <summary>
    /// The reason text stops overstating what is queued.
    /// </summary>
    /// <remarks>
    /// On a healthy Windows install every observed entry had an empty destination,
    /// which is delete-on-boot. "Replaced" alone suggests a half-finished install; a queued
    /// deletion suggests the opposite, something that finished and is tidying up.
    /// </remarks>
    [Fact]
    public void The_queue_reason_does_not_promise_a_replacement_it_has_not_seen() =>
        Assert.Contains("removed or replaced", Queue().Reason);
}
