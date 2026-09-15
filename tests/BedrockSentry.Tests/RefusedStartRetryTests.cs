using BedrockSentry.Core.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// The rule that decides whether a faulted instance gets re-checked (<c>T167</c>).
/// </summary>
/// <remarks>
/// The loop that runs the schedule is exercised through
/// <c>InstanceHostedServiceTests</c>; what is here is the eligibility rule and the
/// shape of the schedule, because those are the two things a later change is most
/// likely to get wrong by widening.
/// </remarks>
public class RefusedStartRetryTests
{
    private static InstanceStatus Faulted(InstanceFault kind) =>
        InstanceStatus.Stopped("survival", "Survival") with
        {
            State = InstanceState.Faulted,
            FaultReason = kind is InstanceFault.RefusedToStart
                ? "refused to start: port 19132 is already open"
                : "gave up after 3 consecutive failures",
            FaultKind = kind,
        };

    [Fact]
    public void A_refusal_on_an_auto_start_instance_is_worth_retrying()
    {
        Assert.True(RefusedStartRetry.StillWorthRetrying(
            Faulted(InstanceFault.RefusedToStart), autoStart: true, retired: false));
    }

    /// <summary>
    /// THE LINE THIS TYPE EXISTS TO DRAW, and the one a later widening would cross.
    /// </summary>
    /// <remarks>
    /// A give-up means a launch has already been tried and failed repeatedly.
    /// Re-checking it is the crash loop <c>D15</c> exists to end — and unlike a
    /// refusal, where the guard runs before anything is launched, repeating this one
    /// actually starts BDS against a world.
    /// </remarks>
    [Fact]
    public void A_give_up_is_never_worth_retrying()
    {
        Assert.False(RefusedStartRetry.StillWorthRetrying(
            Faulted(InstanceFault.GaveUp), autoStart: true, retired: false));
    }

    [Theory]
    [InlineData(InstanceState.Stopped)]
    [InlineData(InstanceState.Starting)]
    [InlineData(InstanceState.Running)]
    [InlineData(InstanceState.Stopping)]
    [InlineData(InstanceState.Backoff)]
    public void Anything_that_is_no_longer_faulted_is_somebody_elses_decision(InstanceState state)
    {
        // Stopped is the interesting one: pressing Stop on a faulted instance lands
        // here, and reads as "I know, leave it down".
        //
        // This test passed while the gesture was unreachable, because it builds the
        // status directly: the panel disabled Stop on a faulted instance, so nobody
        // could make the move this asserts is respected (T184). A unit test over a
        // constructed state cannot see that, which is the argument for reading the
        // one .razor that offers the control whenever a rule names a gesture.
        var moved = Faulted(InstanceFault.RefusedToStart) with { State = state };

        Assert.False(RefusedStartRetry.StillWorthRetrying(moved, autoStart: true, retired: false));
    }

    [Fact]
    public void An_instance_that_does_not_auto_start_is_not_retried()
    {
        Assert.False(RefusedStartRetry.StillWorthRetrying(
            Faulted(InstanceFault.RefusedToStart), autoStart: false, retired: false));
    }

    [Fact]
    public void A_retired_instance_is_not_retried()
    {
        // StartAsync throws on a retired instance, so this is a guard against an
        // exception per attempt rather than a policy question.
        Assert.False(RefusedStartRetry.StillWorthRetrying(
            Faulted(InstanceFault.RefusedToStart), autoStart: true, retired: true));
    }

    /// <summary>
    /// The first delay has to cover the whole measured overlap of a host outliving
    /// its own <c>SERVICE_STOPPED</c>: 0.03 s to 8.8 s on a real box.
    /// </summary>
    [Fact]
    public void The_first_recheck_clears_the_measured_overlap()
    {
        Assert.True(RefusedStartRetry.Schedule[0] >= TimeSpan.FromSeconds(9));
    }

    [Fact]
    public void The_schedule_is_finite_and_its_window_is_the_sum_of_its_delays()
    {
        Assert.NotEmpty(RefusedStartRetry.Schedule);
        Assert.Equal(RefusedStartRetry.Schedule.Aggregate(TimeSpan.Zero, (a, b) => a + b),
            RefusedStartRetry.Window);
    }

    // The operator's question at this point is whether anything tried at all, so
    // the count and the span both have to be in the sentence.
    [Fact]
    public void The_give_up_message_says_how_many_times_and_over_how_long()
    {
        var message = RefusedStartRetry.GaveUpMessage(4, TimeSpan.FromMinutes(2));

        Assert.Contains("4 re-check", message);
        Assert.Contains("2 minute", message);
        Assert.Contains("stay down", message);
    }
}

/// <summary>
/// <see cref="InstanceStatus.FaultKind"/> and <see cref="InstanceStatus.FaultReason"/>
/// have to move together: either both are set or neither is.
/// </summary>
/// <remarks>
/// Two fields stating one thing is this codebase's recurring defect class, so the
/// invariant gets a test rather than a comment. What is here is only the starting
/// point; the real check is <c>InstanceHostedServiceTests</c>, which asserts the
/// pairing on <em>every</em> status the supervisor publishes across a refusal and
/// the re-check that clears it. Asserting it against a hand-written table here
/// instead would have tested the table.
/// </remarks>
public class FaultKindPairingTests
{
    [Fact]
    public void A_fresh_status_has_neither()
    {
        var status = InstanceStatus.Stopped("survival", "Survival");

        Assert.Null(status.FaultReason);
        Assert.Equal(InstanceFault.None, status.FaultKind);
    }
}
