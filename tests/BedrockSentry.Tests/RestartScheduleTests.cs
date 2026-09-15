using BedrockSentry.Core.Instances;

namespace BedrockSentry.Tests;

public class RestartScheduleTests
{
    // A fixed zone with a known DST rule, so the daylight-saving cases below are
    // deterministic on a build agent in any region. Linux and Windows disagree on
    // the id, which is what TryFindSystemTimeZoneById is sorting out.
    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    [Fact]
    public void None_never_schedules()
    {
        var schedule = new RestartSchedule { Mode = RestartScheduleMode.None };

        Assert.Null(schedule.NextOccurrence(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Every_n_hours_counts_from_the_last_start()
    {
        var schedule = new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            IntervalHours = 6,
        };

        var started = new DateTimeOffset(2026, 8, 3, 1, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 8, 3, 2, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 8, 3, 7, 0, 0, TimeSpan.Zero),
            schedule.NextOccurrence(now, started));
    }

    // A server up for days must not produce a restart time in the past, and the
    // arithmetic must not get there by stepping one interval at a time.
    [Fact]
    public void Every_n_hours_skips_forward_past_a_long_uptime()
    {
        var schedule = new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            IntervalHours = 6,
        };

        var started = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 8, 3, 2, 30, 0, TimeSpan.Zero);

        var next = schedule.NextOccurrence(now, started);

        Assert.NotNull(next);
        Assert.True(next > now);
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 6, 0, 0, TimeSpan.Zero), next);
    }

    // Landing exactly on an interval boundary must advance, not return "now" —
    // otherwise the scheduler fires in a tight loop.
    [Fact]
    public void Every_n_hours_advances_when_exactly_on_a_boundary()
    {
        var schedule = new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            IntervalHours = 6,
        };

        var started = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 8, 3, 6, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero),
            schedule.NextOccurrence(now, started));
    }

    [Fact]
    public void Every_n_hours_with_no_anchor_counts_from_now()
    {
        var schedule = new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            IntervalHours = 4,
        };

        var now = new DateTimeOffset(2026, 8, 3, 9, 15, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 8, 3, 13, 15, 0, TimeSpan.Zero),
            schedule.NextOccurrence(now));
    }

    [Fact]
    public void A_non_positive_interval_schedules_nothing_rather_than_looping()
    {
        var schedule = new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            IntervalHours = 0,
        };

        Assert.Null(schedule.NextOccurrence(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Daily_at_picks_today_when_the_time_is_still_ahead()
    {
        var schedule = new RestartSchedule
        {
            Mode = RestartScheduleMode.DailyAt,
            TimeOfDay = new TimeOnly(5, 0),
        };

        // 02:00 Eastern on a summer date, so 05:00 the same day is still to come.
        var now = new DateTimeOffset(2026, 8, 3, 6, 0, 0, TimeSpan.Zero); // 02:00 EDT

        var next = schedule.NextOccurrence(now, null, Eastern);

        Assert.NotNull(next);
        Assert.Equal(new TimeSpan(-4, 0, 0), next.Value.Offset);
        Assert.Equal(new DateTime(2026, 8, 3, 5, 0, 0), next.Value.DateTime);
    }

    [Fact]
    public void Daily_at_rolls_to_tomorrow_once_the_time_has_passed()
    {
        var schedule = new RestartSchedule
        {
            Mode = RestartScheduleMode.DailyAt,
            TimeOfDay = new TimeOnly(5, 0),
        };

        var now = new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero); // 10:00 EDT

        var next = schedule.NextOccurrence(now, null, Eastern);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 8, 4, 5, 0, 0), next.Value.DateTime);
    }

    // Spring forward: 02:30 does not exist on 2026-03-08 in US Eastern. Skipping
    // the day would mean a daily restart that silently does not happen, which is
    // worse than one that runs a little late.
    [Fact]
    public void Daily_at_a_time_that_does_not_exist_runs_when_the_gap_closes()
    {
        var schedule = new RestartSchedule
        {
            Mode = RestartScheduleMode.DailyAt,
            TimeOfDay = new TimeOnly(2, 30),
        };

        var now = new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero); // 01:00 EST

        var next = schedule.NextOccurrence(now, null, Eastern);

        Assert.NotNull(next);
        Assert.False(Eastern.IsInvalidTime(next.Value.DateTime));
        // The gap closes at 03:00 local, still on the same day.
        Assert.Equal(new DateTime(2026, 3, 8, 3, 0, 0), next.Value.DateTime);
    }

    // Fall back: 01:30 happens twice on 2026-11-01. Take the first instant, so
    // the restart runs once rather than being eligible twice in one night.
    [Fact]
    public void Daily_at_an_ambiguous_time_takes_the_earlier_instant()
    {
        var schedule = new RestartSchedule
        {
            Mode = RestartScheduleMode.DailyAt,
            TimeOfDay = new TimeOnly(1, 30),
        };

        var now = new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero); // 00:00 EDT

        var next = schedule.NextOccurrence(now, null, Eastern);

        Assert.NotNull(next);
        Assert.Equal(new TimeSpan(-4, 0, 0), next.Value.Offset); // EDT, the earlier of the two
    }

    [Fact]
    public void Daily_at_always_returns_a_future_instant()
    {
        var schedule = new RestartSchedule
        {
            Mode = RestartScheduleMode.DailyAt,
            TimeOfDay = new TimeOnly(3, 0),
        };

        // Walk a full DST transition day in fifteen-minute steps; every answer
        // must be in the future, whatever the clock is doing.
        var cursor = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.Zero);
        var end = cursor.AddDays(1);

        while (cursor < end)
        {
            var next = schedule.NextOccurrence(cursor, null, Eastern);
            Assert.NotNull(next);
            Assert.True(next > cursor, $"next {next} was not after {cursor}");
            cursor = cursor.AddMinutes(15);
        }
    }

    // `T126`: the Status card described a live EveryNMinutes schedule as "none"
    // because it carried its own copy of this switch and had never been taught
    // the mode. The wording moved onto the schedule so there is one place to be
    // right; this is what makes that place testable, which a .razor file was not.
    [Theory]
    [InlineData(RestartScheduleMode.None, "none")]
    [InlineData(RestartScheduleMode.EveryNHours, "every 12 hours")]
    [InlineData(RestartScheduleMode.EveryNMinutes, "every 30 minutes")]
    [InlineData(RestartScheduleMode.DailyAt, "daily at 05:00")]
    public void Every_mode_describes_itself(RestartScheduleMode mode, string expected)
    {
        // Defaults throughout, so a new mode added without a Describe case shows
        // up here as "none" rather than as a passing test.
        var schedule = new RestartSchedule { Mode = mode };

        Assert.Equal(expected, schedule.Describe());
    }

    [Fact]
    public void An_interval_of_one_is_singular()
    {
        var hourly = new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            IntervalHours = 1,
        };

        var minutely = new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNMinutes,
            IntervalMinutes = 1,
        };

        Assert.Equal("every hour", hourly.Describe());
        Assert.Equal("every minute", minutely.Describe());
    }
}

public class WatchdogOptionsTests
{
    [Fact]
    public void First_retry_uses_the_initial_backoff()
    {
        var options = new WatchdogOptions
        {
            InitialBackoff = TimeSpan.FromSeconds(10),
        };

        Assert.Equal(TimeSpan.FromSeconds(10), options.BackoffFor(1));
    }

    [Fact]
    public void Backoff_grows_by_the_multiplier()
    {
        var options = new WatchdogOptions
        {
            InitialBackoff = TimeSpan.FromSeconds(10),
            BackoffMultiplier = 3,
            MaxBackoff = TimeSpan.FromMinutes(10),
        };

        Assert.Equal(TimeSpan.FromSeconds(30), options.BackoffFor(2));
        Assert.Equal(TimeSpan.FromSeconds(90), options.BackoffFor(3));
    }

    [Fact]
    public void Backoff_is_capped()
    {
        var options = new WatchdogOptions
        {
            InitialBackoff = TimeSpan.FromSeconds(10),
            BackoffMultiplier = 3,
            MaxBackoff = TimeSpan.FromMinutes(1),
        };

        Assert.Equal(TimeSpan.FromMinutes(1), options.BackoffFor(9));
    }

    // A huge failure count overflows TimeSpan long before it exceeds MaxBackoff,
    // and TimeSpan.FromMilliseconds throws rather than saturating — so the cap
    // has to be applied in double arithmetic.
    [Fact]
    public void An_absurd_failure_count_still_returns_the_cap()
    {
        var options = new WatchdogOptions
        {
            InitialBackoff = TimeSpan.FromSeconds(10),
            BackoffMultiplier = 3,
            MaxBackoff = TimeSpan.FromMinutes(5),
        };

        Assert.Equal(TimeSpan.FromMinutes(5), options.BackoffFor(500));
    }

    [Fact]
    public void A_multiplier_below_one_does_not_shrink_the_backoff()
    {
        var options = new WatchdogOptions
        {
            InitialBackoff = TimeSpan.FromSeconds(10),
            BackoffMultiplier = 0.1,
        };

        Assert.Equal(TimeSpan.FromSeconds(10), options.BackoffFor(4));
    }
}
