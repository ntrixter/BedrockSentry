using BedrockSentry.Core.Diagnostics;

namespace BedrockSentry.Tests;

public sealed class UptimeFormatterTests
{
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(1, "1s")]
    [InlineData(59, "59s")]
    public void Under_a_minute_shows_seconds_only(int seconds, string expected) =>
        Assert.Equal(expected, UptimeFormatter.Format(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Under_an_hour_shows_minutes_and_seconds() =>
        Assert.Equal("5m 22s", UptimeFormatter.Format(new TimeSpan(0, 5, 22)));

    [Fact]
    public void Under_a_day_shows_hours_minutes_and_seconds() =>
        Assert.Equal("3h 14m 22s", UptimeFormatter.Format(new TimeSpan(3, 14, 22)));

    [Fact]
    public void A_day_or_more_shows_days_first() =>
        Assert.Equal("2d 3h 14m 22s", UptimeFormatter.Format(new TimeSpan(2, 3, 14, 22)));

    [Fact]
    public void Days_accumulate_rather_than_rolling_into_months()
    {
        // TotalDays, not Day-of-something: a wrapper left running for a year should
        // read "400d", not silently wrap.
        Assert.Equal("400d 0h 0m 0s", UptimeFormatter.Format(TimeSpan.FromDays(400)));
    }

    [Fact]
    public void Seconds_are_always_shown()
    {
        // The display ticks once a second. A format whose largest visible unit only
        // changes every minute reads as a frozen page rather than a live counter.
        Assert.EndsWith("s", UptimeFormatter.Format(TimeSpan.FromDays(3)), StringComparison.Ordinal);
        Assert.Contains("0s", UptimeFormatter.Format(TimeSpan.FromHours(5)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_span_clamps_to_zero()
    {
        // NTP correcting a drifted server clock moves it backwards. "-3s" of uptime
        // is worse than admitting nothing.
        Assert.Equal("0s", UptimeFormatter.Format(TimeSpan.FromSeconds(-3)));
    }
}
