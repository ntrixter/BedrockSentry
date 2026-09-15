using BedrockSentry.Core.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// The Configuration tab's watchdog ladder, which described a sequence the
/// wrapper does not run (`T140`).
/// </summary>
/// <remarks>
/// Every expectation here is checkable against `BackoffFor` and against a real
/// crash loop, measured at 4.9s, 14.9s, 44.9s and 135.0s for 5s/x3, each named
/// exactly by the console. The panel said "2 minutes" for the last one.
/// </remarks>
public class WatchdogLadderTests
{
    private static WatchdogOptions Watchdog(
        int giveUpAfter = 3,
        int initialSeconds = 5,
        double multiplier = 3,
        int maxMinutes = 5) => new()
        {
            MaxConsecutiveFailures = giveUpAfter,
            InitialBackoff = TimeSpan.FromSeconds(initialSeconds),
            BackoffMultiplier = multiplier,
            MaxBackoff = TimeSpan.FromMinutes(maxMinutes),
        };

    /// <summary>
    /// The exact defect. 135 seconds is what the console says and what
    /// `BackoffFor` returns; the panel rounded it to "2 minutes".
    /// </summary>
    [Fact]
    public void A_rung_over_a_minute_keeps_its_seconds()
    {
        var sentence = WatchdogLadder.Describe(Watchdog(giveUpAfter: 5));

        Assert.Contains("2 minutes 15 seconds", sentence);
        Assert.DoesNotContain("then 2 minutes,", sentence);
    }

    /// <summary>
    /// The rounding understated a real 4m30s wait by half a minute. Reached with
    /// a raised give-up, which is how the box reached it too — at the shipped
    /// give-up of 3 the fourth rung cannot occur at all, which is the other half
    /// of this entry.
    /// </summary>
    [Fact]
    public void A_wait_is_never_rounded_down_to_whole_minutes()
    {
        var sentence = WatchdogLadder.Describe(
            Watchdog(giveUpAfter: 6, initialSeconds: 10, maxMinutes: 60));

        Assert.Contains("1 minute 30 seconds", sentence);
        Assert.Contains("4 minutes 30 seconds", sentence);
    }

    /// <summary>
    /// The second half of `T140`: the give-up branch returns before
    /// `BackoffFor` is called, so N failures allow only N-1 retries. The panel
    /// always printed four regardless.
    /// </summary>
    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    public void Only_the_retries_that_can_happen_are_listed(int giveUpAfter, int expected)
    {
        var sentence = WatchdogLadder.Describe(Watchdog(giveUpAfter, maxMinutes: 600));

        // One "then" between each pair of rungs.
        Assert.Equal(expected - 1, sentence.Split(", then ").Length - 1);
    }

    /// <summary>
    /// At the shipped give-up of 3 only two waits are reachable, and the third
    /// and fourth rungs the panel used to advertise must be gone.
    /// </summary>
    [Fact]
    public void The_shipped_give_up_advertises_no_unreachable_wait()
    {
        var sentence = WatchdogLadder.Describe(Watchdog());

        Assert.Equal("It is restarted after 5 seconds, then 15 seconds.", sentence);
        Assert.DoesNotContain("45 seconds", sentence);
    }

    /// <summary>
    /// Giving up on the first failure means no retry at all, and the sentence
    /// has to be able to say so — which is why the lead-in lives in here rather
    /// than in the page.
    /// </summary>
    [Fact]
    public void Giving_up_on_the_first_failure_promises_no_restart()
    {
        var sentence = WatchdogLadder.Describe(Watchdog(giveUpAfter: 1));

        Assert.Contains("never restarted", sentence);
        Assert.DoesNotContain("restarted after", sentence);
    }

    /// <summary>
    /// Zero does not turn the watchdog off — it removes the limit (`T145`).
    /// </summary>
    /// <remarks>
    /// This test asserted the opposite until 2026-09-04, which is the whole
    /// lesson: it was written from `MaxConsecutiveFailures`'s doc comment, the
    /// comment was wrong, and a green test then defended the wrong sentence.
    /// `InstanceSupervisor` gates its fault branch on `> 0`, so zero never faults.
    /// The severity is why it matters — the old text told an operator the
    /// watchdog was off at the one setting that makes it unbounded, so somebody
    /// choosing zero to mean "leave it down" got an endless crash loop and a
    /// panel reassuring them.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Zero_or_less_never_gives_up_rather_than_switching_off(int giveUpAfter)
    {
        var sentence = WatchdogLadder.Describe(Watchdog(giveUpAfter, maxMinutes: 1));

        Assert.Contains("never gives up", sentence);
        Assert.DoesNotContain("watchdog is off", sentence);

        // And it still describes the waits, because they still happen.
        Assert.Contains("It is restarted after 5 seconds", sentence);
    }

    /// <summary>
    /// Once the ceiling is reached every later rung is the same number, so the
    /// sentence names the ceiling instead of repeating it.
    /// </summary>
    [Fact]
    public void The_ceiling_is_named_rather_than_repeated()
    {
        var sentence = WatchdogLadder.Describe(
            Watchdog(giveUpAfter: 10, initialSeconds: 30, maxMinutes: 1));

        Assert.Equal(
            "It is restarted after 30 seconds, then 1 minute, "
                + "and 1 minute for each retry after that.",
            sentence);
    }

    /// <summary>
    /// A long ladder is summarised rather than listed to its end.
    /// </summary>
    [Fact]
    public void A_long_ladder_stops_at_four_rungs_and_names_the_ceiling()
    {
        var sentence = WatchdogLadder.Describe(
            Watchdog(giveUpAfter: 12, initialSeconds: 10, multiplier: 2, maxMinutes: 90));

        Assert.Equal(
            "It is restarted after 10 seconds, then 20 seconds, then 40 seconds, "
                + "then 1 minute 20 seconds, and so on up to 1 hour 30 minutes.",
            sentence);
    }

    [Fact]
    public void One_of_a_unit_is_singular()
    {
        var sentence = WatchdogLadder.Describe(
            Watchdog(giveUpAfter: 2, initialSeconds: 1, maxMinutes: 600));

        Assert.Equal("It is restarted after 1 second.", sentence);
    }

    // T145, second fix. The first moved the rule into Describe and left the Status
    // card rendering "off" at zero from its own copy. Every surface now reads the
    // short form from here, and zero must never read as off anywhere.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void The_short_form_at_zero_or_less_never_says_off(int giveUpAfter)
    {
        var summary = WatchdogLadder.Summary(Watchdog(giveUpAfter: giveUpAfter));

        Assert.Equal("never gives up", summary);
        Assert.DoesNotContain("off", summary);
    }

    [Theory]
    [InlineData(1, "gives up on the first failure")]
    [InlineData(3, "gives up after 3 failures")]
    public void The_short_form_names_the_limit(int giveUpAfter, string expected) =>
        Assert.Equal(expected, WatchdogLadder.Summary(Watchdog(giveUpAfter: giveUpAfter)));

    // The console's tag on an unexpected exit read "failure 1 of 0" at no limit,
    // which is the same wrong claim as "off" in a different font.
    [Fact]
    public void The_failure_count_at_no_limit_does_not_say_of_zero()
    {
        Assert.Equal("failure 1, no limit", WatchdogLadder.FailureCount(1, Watchdog(giveUpAfter: 0)));
        Assert.Equal("failure 2 of 3", WatchdogLadder.FailureCount(2, Watchdog(giveUpAfter: 3)));
    }
}
