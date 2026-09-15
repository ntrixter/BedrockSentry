namespace BedrockSentry.Core.Diagnostics;

/// <summary>
/// Renders a duration as a compact human-readable string.
/// </summary>
/// <remarks>
/// Seconds are always included. This drives a counter that ticks once a second,
/// and a display whose largest visible unit only changes every minute reads as
/// frozen rather than live.
/// </remarks>
public static class UptimeFormatter
{
    /// <summary>Formats a duration, e.g. <c>2d 3h 14m 22s</c>.</summary>
    public static string Format(TimeSpan elapsed)
    {
        // A negative span means the clock moved backwards under us — NTP correcting
        // a drifted server clock does exactly this. Showing "-3s" is worse than
        // showing nothing meaningful, so clamp.
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var days = (int)elapsed.TotalDays;

        return days > 0
            ? $"{days}d {elapsed.Hours}h {elapsed.Minutes}m {elapsed.Seconds}s"
            : elapsed.Hours > 0
                ? $"{elapsed.Hours}h {elapsed.Minutes}m {elapsed.Seconds}s"
                : elapsed.Minutes > 0
                    ? $"{elapsed.Minutes}m {elapsed.Seconds}s"
                    : $"{elapsed.Seconds}s";
    }
}
