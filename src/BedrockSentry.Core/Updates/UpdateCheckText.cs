using BedrockSentry.Core.Diagnostics;

namespace BedrockSentry.Core.Updates;

/// <summary>A check of a feed that could not read it (<c>T196</c>, <c>T52</c>).</summary>
/// <param name="AtUtc">When the check was made.</param>
/// <param name="Reason">Why nothing usable came back, as a clause: <c>it answered 404</c>.</param>
public sealed record UpdateCheckFailure(DateTimeOffset AtUtc, string Reason);

/// <summary>
/// What a page says about the last check of a feed: the wrapper's update channel or
/// Mojang's download feed.
/// </summary>
/// <remarks>
/// Here rather than in the pages, because the suite sees a <c>.razor</c> file only
/// as text and this has four states to tell apart (<c>T196</c>).
/// </remarks>
public static class UpdateCheckText
{
    /// <summary>The <em>Last checked</em> row for the wrapper's own update channel.</summary>
    public static string LastChecked(DateTimeOffset? readUtc, UpdateCheckFailure? failed) =>
        LastChecked(readUtc, failed, "the channel");

    /// <summary>
    /// The <em>Last checked</em> row: never asked, read at a time, or asked and
    /// unreadable, with the last read beside it when there was one.
    /// </summary>
    /// <param name="readUtc">When the feed was last read, or null if never.</param>
    /// <param name="failed">The last check that could not read it, or null.</param>
    /// <param name="feed">What was read, as the sentence names it: <c>the channel</c>.</param>
    public static string LastChecked(DateTimeOffset? readUtc, UpdateCheckFailure? failed, string feed)
    {
        if (failed is null)
        {
            return readUtc is { } read ? When(read) : "never";
        }

        var unreadable = $"the check at {When(failed.AtUtc)} could not read {feed}: {failed.Reason}";

        return readUtc is { } last
            ? $"{When(last)}; {unreadable}"
            : $"never read; {unreadable}";
    }

    private static string When(DateTimeOffset utc) => ClockText.DateAndTime(utc.ToLocalTime());
}
