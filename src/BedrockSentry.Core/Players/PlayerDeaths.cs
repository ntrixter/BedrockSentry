using System.Globalization;

namespace BedrockSentry.Core.Players;

/// <summary>Deaths recorded for one player on one server, and since when.</summary>
/// <param name="SinceUtc">
/// The companion pack's first report on that server. Nothing before it was seen,
/// so the count is deaths since then, never deaths ever. A server it has never
/// reported on has no tally at all rather than a believable zero.
/// </param>
public readonly record struct DeathTally(int Count, DateTimeOffset SinceUtc)
{
    /// <summary>When a server's Deaths column starts, or null when it has none.</summary>
    /// <param name="tallies">Every row's tally on one server's roster.</param>
    /// <remarks>
    /// Every tally on one server shares its start, the pack's first report there,
    /// so the first one found answers for the column. Null when the pack has never
    /// reported there, and then the page shows no column rather than dashes.
    /// </remarks>
    public static DateTimeOffset? CountedSince(IEnumerable<DeathTally?> tallies)
    {
        ArgumentNullException.ThrowIfNull(tallies);

        return tallies.FirstOrDefault(t => t is not null)?.SinceUtc;
    }
}

/// <summary>One recorded death, as a player's page lists it.</summary>
/// <param name="Message">The sentence the pack built, as the console showed it.</param>
public sealed record RecordedDeath(string InstanceId, DateTimeOffset DiedUtc, string Message);

/// <summary>How the Players pages write deaths.</summary>
public static class DeathText
{
    /// <summary>A table cell: the count, or an em dash where nothing has been counted.</summary>
    public static string Count(DeathTally? tally) =>
        tally is { } counted ? counted.Count.ToString(CultureInfo.InvariantCulture) : "—";

    /// <summary>The sentence under a server's roster, saying what its Deaths column covers.</summary>
    /// <param name="sinceDate">The date of the pack's first report there, as the page writes dates.</param>
    public static string Coverage(string sinceDate) =>
        $"Deaths are counted from {sinceDate}, when the companion pack first reported on this server.";
}
