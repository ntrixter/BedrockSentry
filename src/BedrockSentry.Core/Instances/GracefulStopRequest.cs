namespace BedrockSentry.Core.Instances;

/// <summary>
/// Why an instance is being stopped. Recorded on the console line so an
/// administrator reading back a log can tell a scheduled restart from a service
/// stop from someone pressing a button.
/// </summary>
public enum StopReason
{
    Manual,
    ServiceStop,
    ScheduledRestart,
    WrapperUpdate,
    ServerUpdate,

    /// <summary>Coming down so a backup can be applied over the world.</summary>
    Restore,
}

/// <summary>
/// A request to shut an instance down gracefully.
///
/// Graceful shutdown is shared infrastructure: scheduled restarts, BDS
/// auto-update and wrapper self-update all warn players, stop, and optionally
/// back up the same way. That is one method with several callers passing
/// different toggles, not three implementations that drift — so everything that
/// varies between callers lives in this record rather than in the code that
/// performs the stop.
///
/// The variation that matters most is time. A scheduled restart may warn for as
/// long as the administrator likes, because the wrapper stays up throughout. A
/// service stop gets seconds, because Windows is waiting on the service control
/// manager and a long countdown risks the service being killed mid-warning —
/// exactly the outcome the graceful path exists to prevent (<c>docs/design.md</c> § 5.2).
/// </summary>
/// <param name="WarningLead">
/// Total time between the first warning and the stop. Zero stops immediately.
/// </param>
/// <param name="Warnings">
/// Points before the stop at which to warn players. Entries longer than
/// <paramref name="WarningLead"/> are ignored rather than treated as an error;
/// shortening the lead should not require also editing the list.
/// </param>
/// <param name="DrainBudget">
/// How long BDS gets to exit after <c>stop</c> before it is killed. The spike
/// measured a real stop at 422 ms, so this is generous by design — the cost of
/// being wrong is a killed server mid-save.
/// </param>
public sealed record GracefulStopRequest(
    StopReason Reason,
    TimeSpan WarningLead,
    IReadOnlyList<TimeSpan> Warnings,
    TimeSpan DrainBudget)
{
    /// <summary>
    /// The Bedrock build this stop is for, when the reason is
    /// <see cref="StopReason.ServerUpdate"/>. Null everywhere else.
    /// </summary>
    /// <remarks>
    /// Here rather than in the countdown because the countdown is shared: the
    /// caller is the only thing that knows which build it is applying, and the
    /// notices are produced several layers below it. Without it the closing
    /// notice read "Server restarting to update Minecraft in 10 seconds" while
    /// every earlier rung named the version — and on the **Update now** path,
    /// which sends only that closing notice, players never saw the version at
    /// all (`T134`).
    /// <para>
    /// Init-only with a default rather than a fifth positional parameter, so the
    /// callers that have no version to give are unchanged.
    /// </para>
    /// </remarks>
    public string? Version { get; init; }

    /// <summary>Stop now, no warning. For a server with nobody on it, or an operator in a hurry.</summary>
    public static GracefulStopRequest Immediate(StopReason reason, TimeSpan drainBudget) =>
        new(reason, TimeSpan.Zero, [], drainBudget);

    /// <summary>
    /// The short warning a service stop gets. One notice, then down — see the
    /// class remarks for why this is not the administrator's countdown.
    /// </summary>
    public static GracefulStopRequest ServiceStop(TimeSpan warning, TimeSpan drainBudget) =>
        new(StopReason.ServiceStop, warning, warning > TimeSpan.Zero ? [warning] : [], drainBudget);

    /// <summary>The administrator's full countdown, used by scheduled restarts.</summary>
    public static GracefulStopRequest Scheduled(RestartSchedule schedule, TimeSpan drainBudget)
    {
        var warnings = schedule.WarningsOrDefault;
        var lead = warnings.Count > 0 ? warnings.Max() : TimeSpan.Zero;
        return new GracefulStopRequest(StopReason.ScheduledRestart, lead, warnings, drainBudget);
    }

    /// <summary>
    /// The warnings that will actually fire, latest-first, with anything past the
    /// lead time or duplicated removed.
    /// </summary>
    public IReadOnlyList<TimeSpan> EffectiveWarnings() =>
        Warnings
            .Where(w => w > TimeSpan.Zero && w <= WarningLead)
            .Distinct()
            .OrderByDescending(w => w)
            .ToList();
}

/// <summary>
/// Turns a countdown into something worth reading in chat.
/// </summary>
public static class CountdownMessage
{
    /// <summary>"15 minutes", "1 minute", "30 seconds" — singular where it matters.</summary>
    public static string Describe(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        // Rounding rather than truncating: a warning fired a few milliseconds
        // late must not announce "4 minutes" for a five-minute mark.
        if (remaining.TotalSeconds < 60)
        {
            var seconds = Math.Max(1, (int)Math.Round(remaining.TotalSeconds));
            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }

        if (remaining.TotalMinutes < 60)
        {
            var minutes = (int)Math.Round(remaining.TotalMinutes);
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        // Hours *and* minutes, not hours rounded. Rounding here overstated the
        // time left in the one direction that matters: 90 minutes announced
        // itself as "2 hours", giving players half an hour they did not have —
        // and 75 minutes said "1 hour", which the 1-hour rung then said again a
        // quarter of an hour later.
        var wholeHours = (int)remaining.TotalHours;
        var spareMinutes = (int)Math.Round(remaining.TotalMinutes) - (wholeHours * 60);

        // A remainder that rounds up to a full hour belongs to the hour.
        if (spareMinutes >= 60)
        {
            wholeHours++;
            spareMinutes = 0;
        }

        var hoursText = wholeHours == 1 ? "1 hour" : $"{wholeHours} hours";

        return spareMinutes == 0
            ? hoursText
            : $"{hoursText} {(spareMinutes == 1 ? "1 minute" : $"{spareMinutes} minutes")}";
    }

    /// <summary>The full <c>say</c> line for a pending stop.</summary>
    /// <param name="version">
    /// The Bedrock build being applied, for <see cref="StopReason.ServerUpdate"/>.
    /// Named when it is known, so this line matches the rungs above it rather
    /// than being the one notice in the ladder that omits what is changing
    /// (`T134`). Optional because every other reason has no version to name.
    /// </param>
    public static string ForStop(StopReason reason, TimeSpan remaining, string? version = null)
    {
        var what = reason switch
        {
            StopReason.ScheduledRestart => "restarting",
            StopReason.ServerUpdate => string.IsNullOrWhiteSpace(version)
                ? "restarting to update Minecraft"
                : $"restarting to update Minecraft {version.Trim()}",
            StopReason.WrapperUpdate => "restarting to update BedrockSentry",
            StopReason.Restore => "restarting to restore a backup",
            _ => "shutting down",
        };

        return $"say Server {what} in {Describe(remaining)}";
    }
}
