namespace BedrockSentry.Core.Instances;

/// <summary>
/// When to speak up while counting down to a planned outage.
/// </summary>
/// <remarks>
/// Derived from the length of the window rather than configured beside it. A
/// separate list of warning points lets somebody set a five-minute window with a
/// fifteen-minute warning in it, which cannot be honoured and has to be silently
/// dropped — one number to set, and the notices follow from it.
///
/// Shared by every planned outage that has a window to count down: a Bedrock
/// build waiting out its instance's delay (<c>docs/design.md</c> § 11.3) and the
/// wrapper updating itself (§ 12). The alternative is the same ladder written
/// twice in two namespaces, drifting apart the first time somebody tunes one.
/// </remarks>
public static class CountdownLadder
{
    /// <summary>How often to speak inside the last hour.</summary>
    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(5);

    /// <summary>Where that cadence starts.</summary>
    private static readonly TimeSpan CadenceFrom = TimeSpan.FromHours(1);

    /// <summary>
    /// How often to speak once <see cref="Cadence"/> has been reached.
    /// </summary>
    /// <remarks>
    /// Inside the last five minutes there is no longer anything to plan — there
    /// is only finding somewhere safe to stand and logging off. A minute is the
    /// smallest unit worth naming at that range, and it is the unit players are
    /// already counting in.
    /// </remarks>
    private static readonly TimeSpan FinalCadence = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A rung this close under the window is dropped.
    /// </summary>
    /// <remarks>
    /// The opening notice states the window itself, so a thirty-one minute
    /// window would otherwise say "in 31 minutes" and then "in 30 minutes" a
    /// minute later — twice, for no new information. With rungs five minutes
    /// apart rather than fifteen, a window landing just above one is the common
    /// case rather than the odd one.
    /// </remarks>
    private static readonly TimeSpan Stutter = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The rungs, longest first. Points past the window are never reached.
    /// </summary>
    /// <remarks>
    /// Three scales, each matching what a warning is for at that range. Sparse
    /// above an hour, where a notice is something to plan a session around.
    /// Every five minutes through the last hour. Every minute inside the last
    /// five, where it is a reason to stop what you are doing.
    ///
    /// The earlier ladder went 30 → 15 → 5 to keep the channel quiet, on the
    /// reasoning that a notice every few minutes is spam players tune out
    /// exactly when the last warning matters. That reasoning only holds for
    /// somebody who was already online when the window opened. Anybody who joins
    /// part-way through hears nothing at all until five minutes out — which, on
    /// the default half-hour delay, is most of the window — and the first they
    /// know of it is a server about to go down. Reported from a real server: "it
    /// only starts at 5 minutes".
    /// </remarks>
    private static readonly TimeSpan[] Rungs = BuildRungs();

    private static TimeSpan[] BuildRungs()
    {
        var rungs = new List<TimeSpan>
        {
            TimeSpan.FromHours(12),
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(2),
        };

        for (var at = CadenceFrom; at >= Cadence; at -= Cadence)
        {
            rungs.Add(at);
        }

        // Then every minute for the last five. Starts one step below the
        // five-minute mark, which the loop above has already placed.
        for (var at = Cadence - FinalCadence; at >= FinalCadence; at -= FinalCadence)
        {
            rungs.Add(at);
        }

        return [.. rungs];
    }

    /// <summary>
    /// The last call, for a countdown whose end is timed precisely enough to hit
    /// it.
    /// </summary>
    /// <remarks>
    /// Not a rung, deliberately. The Bedrock auto-updater speaks from a sweep
    /// that runs every fifteen seconds, so a ten-second point there would be
    /// missed or announced with five seconds left. A stop's own countdown sleeps
    /// to each point exactly and can honour it, so that caller adds this and the
    /// swept one does not.
    /// </remarks>
    public static readonly TimeSpan LastCall = TimeSpan.FromSeconds(10);

    /// <summary>
    /// When to speak, as time remaining, longest first.
    /// </summary>
    /// <remarks>
    /// Always opens with the window itself, so the first thing players hear is
    /// how long they actually have rather than whichever rung happens to sit
    /// just below it — told "in 22 minutes", not "in 20 minutes" two minutes
    /// later. A zero or negative window yields nothing at all: there is nothing
    /// to count down.
    /// </remarks>
    public static IReadOnlyList<TimeSpan> PointsFor(TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            return [];
        }

        var points = new List<TimeSpan> { window };

        foreach (var rung in Rungs)
        {
            // Strictly inside the window, and far enough inside to be worth
            // saying: a rung equal to it is the opening notice, already there,
            // and one just under it repeats that notice with a smaller number.
            if (window - rung >= Stutter)
            {
                points.Add(rung);
            }
        }

        return points;
    }
}
