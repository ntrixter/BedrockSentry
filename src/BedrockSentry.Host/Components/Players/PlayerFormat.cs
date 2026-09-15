namespace BedrockSentry.Host.Components.Players;

/// <summary>
/// How durations and dates are written wherever players are listed.
/// </summary>
/// <remarks>
/// Shared rather than duplicated: the global roster and each instance's own
/// Players tab are two views over one set of rows (<c>docs/design.md</c> § 7.3), and a
/// second copy of these would be free to drift into saying "3h 5m" on one page
/// and "3.1 hours" on the other.
/// </remarks>
public static class PlayerFormat
{
    /// <summary>
    /// A span at the coarsest unit that still says something useful.
    /// </summary>
    /// <remarks>
    /// Never zero: a session that has just started is "&lt;1m" rather than "0m",
    /// which would read as a row that is not counting.
    /// </remarks>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return "<1m";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{(int)span.TotalMinutes}m";
        }

        return span.TotalHours < 24
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{(int)span.TotalDays}d {span.Hours}h";
    }

    /// <summary>A local calendar date, or an em dash when there is nothing to show.</summary>
    public static string Date(DateTimeOffset? value) =>
        value is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd") : "—";
}
