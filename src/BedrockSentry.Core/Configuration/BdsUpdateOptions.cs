using BedrockSentry.Core.Instances;

namespace BedrockSentry.Core.Configuration;

/// <summary>
/// Where the wrapper looks for Bedrock server builds, bound from
/// <c>Sentry:BdsUpdate</c> (<c>docs/design.md</c> § 11).
/// </summary>
/// <remarks>
/// Install-wide rather than per-instance, because it describes where Mojang
/// publishes rather than what any one server wants. What each server does with
/// the answer — follow release or preview, update by itself or wait to be told,
/// or stay pinned — is on the instance, since the appetite for a new build on
/// a survival world people have played for a year is not the appetite on a
/// creative test server.
/// </remarks>
public sealed class BdsUpdateOptions
{
    public const string SectionName = "Sentry:BdsUpdate";

    /// <summary>Whether to ask Mojang what it is offering at all.</summary>
    /// <remarks>
    /// Named so it can be turned off rather than discovered, the same way
    /// <c>D20</c>'s public-address lookup is: this is an outbound request to a
    /// third party on a schedule, and somebody running an air-gapped or
    /// deliberately pinned install should be able to say no in one place.
    /// </remarks>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Mojang's download-links feed.
    /// </summary>
    /// <remarks>
    /// Configurable rather than hard-coded for the reason <c>D13</c> made the
    /// wrapper's own feed configurable: this is somebody else's endpoint, and
    /// when it moves — which it has before — an administrator should be able to
    /// point at the new one without waiting for a wrapper release. The measured
    /// shape it must return is documented on
    /// <see cref="Packages.BdsDownloadLinks"/>.
    /// </remarks>
    public string LinksUrl { get; set; } =
        "https://net-secondary.web.minecraft-services.net/api/v1.0/download/links";

    /// <summary>When to ask.</summary>
    /// <remarks>
    /// The same <see cref="RestartSchedule"/> scheduled restarts and scheduled
    /// backups already use, rather than a third way of writing "every so often".
    /// It carries an interval mode and a time-of-day mode, and the time-of-day
    /// one has the daylight-saving handling of <c>D18</c> already solved and
    /// already tested — which a bare <c>TimeSpan</c> would have had to grow the
    /// first time somebody asked for "check at 4am", as somebody now has.
    ///
    /// <see cref="RestartScheduleMode.None"/> means no timed check: the check on
    /// startup still runs, and <b>Check now</b> still works. To stop asking
    /// Mojang anything at all, use <see cref="CheckForUpdates"/>.
    ///
    /// Nullable and defaulted through <see cref="ScheduleOrDefault"/> for the
    /// reason <see cref="RestartSchedule.Warnings"/> is: a pre-populated default
    /// would merge with whatever the administrator wrote instead of replacing it.
    /// </remarks>
    public RestartSchedule? CheckSchedule { get; set; }

    /// <summary>
    /// The pre-<c>CheckSchedule</c> spelling, honoured so a hand-edited config
    /// does not quietly stop working.
    /// </summary>
    /// <remarks>
    /// Six hours, matching the wrapper's own check. Bedrock ships every few
    /// weeks, so anything faster is polling a third party to learn nothing.
    /// Setting <see cref="CheckSchedule"/> — which is what the Settings page
    /// writes — supersedes this entirely.
    /// </remarks>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>The schedule actually in force.</summary>
    public RestartSchedule ScheduleOrDefault => CheckSchedule ?? new RestartSchedule
    {
        Mode = RestartScheduleMode.EveryNHours,
        IntervalHours = CheckInterval > TimeSpan.Zero
            ? Math.Max(1, (int)Math.Round(CheckInterval.TotalHours))
            : 6,
    };
}
