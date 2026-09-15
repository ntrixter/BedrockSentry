using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Packages;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 11.3. The delay is the per-instance update schedule, and the
/// countdown is derived from it rather than configured beside it — so the
/// property that matters is that any delay produces a sensible ladder without
/// anybody having to check.
/// </summary>
public class BdsUpdateCountdownTests
{
    [Fact]
    public void The_first_notice_is_always_the_whole_window()
    {
        // Not the nearest rung below it. Told "in 25 minutes", a player can
        // plan; told "in 15 minutes" a third of the way through, they have been
        // given a smaller number than they actually have.
        var points = BdsUpdateCountdown.PointsFor(TimeSpan.FromMinutes(25));

        Assert.Equal(TimeSpan.FromMinutes(25), points[0]);
    }

    [Fact]
    public void The_default_half_hour_is_five_minutes_then_every_minute()
    {
        Assert.Equal(
            [
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(25),
                TimeSpan.FromMinutes(20),
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(10),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(4),
                TimeSpan.FromMinutes(3),
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(1),
            ],
            BdsUpdateCountdown.PointsFor(TimeSpan.FromMinutes(30)));
    }

    // Inside the last five minutes there is nothing left to plan, only somewhere
    // safe to stand — so the ladder counts in the unit players are counting in.
    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(90)]
    public void The_last_five_minutes_are_counted_one_at_a_time(int minutes)
    {
        var points = BdsUpdateCountdown.PointsFor(TimeSpan.FromMinutes(minutes));

        Assert.Equal(
            [
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(4),
                TimeSpan.FromMinutes(3),
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(1),
            ],
            points.Where(p => p <= TimeSpan.FromMinutes(5)));
    }

    /// <summary>
    /// The property behind the change: whenever a player joins inside the last
    /// hour, the next notice is never more than five minutes away.
    /// </summary>
    /// <remarks>
    /// The old ladder went 30 → 15 → 5, so somebody arriving twenty minutes into
    /// a half-hour window heard nothing for a quarter of an hour. Asserted as a
    /// gap rather than as a list, because the list is the implementation and the
    /// gap is the promise.
    /// </remarks>
    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    public void Nobody_inside_the_last_hour_waits_more_than_five_minutes(int minutes)
    {
        var points = BdsUpdateCountdown.PointsFor(TimeSpan.FromMinutes(minutes));

        foreach (var (from, to) in points.Zip(points.Skip(1)))
        {
            Assert.True(
                from - to <= TimeSpan.FromMinutes(5),
                $"{from} to {to} leaves a player unheard from for {from - to}");
        }

        Assert.Equal(TimeSpan.FromMinutes(1), points[^1]);
    }

    // Above an hour the notices go back to being sparse: at that range a warning
    // is information to plan around rather than a reason to stop, and five-minute
    // chatter for two hours is how players learn to ignore the line.
    [Fact]
    public void Above_an_hour_the_ladder_stays_coarse()
    {
        var points = BdsUpdateCountdown.PointsFor(TimeSpan.FromHours(4));

        Assert.Equal(
            [TimeSpan.FromHours(4), TimeSpan.FromHours(2), TimeSpan.FromHours(1)],
            points.Where(p => p >= TimeSpan.FromHours(1)));
    }

    // The opening notice states the window, so a rung a few seconds under it
    // would repeat that notice with a smaller number.
    [Fact]
    public void A_rung_just_under_the_window_is_not_announced_as_well()
    {
        var points = BdsUpdateCountdown.PointsFor(TimeSpan.FromSeconds(30 * 60 + 20));

        Assert.DoesNotContain(TimeSpan.FromMinutes(30), points);
        Assert.Equal(TimeSpan.FromMinutes(25), points[1]);
    }

    // The opening notice and the 30-minute rung are the same instant here.
    // Saying it twice in one second reads as a stutter.
    [Fact]
    public void A_delay_landing_exactly_on_a_rung_does_not_announce_it_twice()
    {
        var points = BdsUpdateCountdown.PointsFor(TimeSpan.FromMinutes(30));

        Assert.Equal(points.Count, points.Distinct().Count());
    }

    [Fact]
    public void Every_point_is_strictly_inside_the_window_and_ordered()
    {
        foreach (var minutes in new[] { 1, 2, 5, 7, 15, 30, 45, 90, 240, 1440, 10080 })
        {
            var delay = TimeSpan.FromMinutes(minutes);
            var points = BdsUpdateCountdown.PointsFor(delay);

            Assert.NotEmpty(points);
            Assert.All(points, p => Assert.InRange(p, TimeSpan.Zero, delay));
            Assert.Equal(points.OrderByDescending(p => p), points);
        }
    }

    // Zero is a supported answer meaning "as soon as it is seen". There is no
    // window, so there is nothing to count down — the stop's own short warning
    // is all anybody gets, and that is the documented behaviour.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void No_window_means_no_countdown(int minutes)
    {
        Assert.Empty(BdsUpdateCountdown.PointsFor(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void A_very_short_delay_still_gets_its_one_notice()
    {
        Assert.Equal([TimeSpan.FromMinutes(1)], BdsUpdateCountdown.PointsFor(TimeSpan.FromMinutes(1)));
    }

    // The notice names the build. A scheduled restart can only say "restarting";
    // here the wrapper knows why, and a player seeing the line for the fourth
    // time is better served by one that says what is changing.
    [Fact]
    public void The_notice_names_the_version_and_the_time_left()
    {
        var line = BdsUpdateCountdown.Notice("1.26.44.3", TimeSpan.FromMinutes(15));

        Assert.StartsWith("say ", line);
        Assert.Contains("1.26.44.3", line);
        Assert.Contains("15 minutes", line);
    }

    [Fact]
    public void A_called_off_update_says_so()
    {
        Assert.Contains("1.26.44.3", BdsUpdateCountdown.Cancelled("1.26.44.3"));
        Assert.StartsWith("say ", BdsUpdateCountdown.Cancelled("1.26.44.3"));
    }

    [Fact]
    public void The_delay_defaults_to_half_an_hour_and_never_goes_negative()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), new BdsUpdateSettings().AutoApplyDelay);

        Assert.Equal(
            TimeSpan.Zero,
            new BdsUpdateSettings { AutoApplyDelayMinutes = -10 }.AutoApplyDelay);
    }
}
