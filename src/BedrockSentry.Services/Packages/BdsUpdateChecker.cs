using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BedrockSentry.Services.Packages;

/// <summary>
/// Asks Mojang what it is offering, on a schedule (<c>docs/design.md</c> § 11).
/// </summary>
/// <remarks>
/// The clock and nothing else. What any instance does with the answer is
/// decided when a page asks, so this service holds no per-instance state and
/// applying an update is not its job — which keeps "somebody else's endpoint is
/// slow" and "this server needs restarting" as separate problems.
///
/// The schedule is re-read every time round the loop rather than captured once.
/// It is editable from the Settings page now, and a setting that needs the
/// Windows Service restarted to take effect is a setting that will be reported
/// as broken.
/// </remarks>
public sealed class BdsUpdateChecker(
    BdsUpdateService updates,
    IOptionsMonitor<BdsUpdateOptions> options,
    TimeProvider timeProvider,
    ILogger<BdsUpdateChecker> logger) : BackgroundService
{
    /// <summary>
    /// How often to wake and re-read the schedule while waiting for the next due
    /// time.
    /// </summary>
    /// <remarks>
    /// Sleeping straight through to the next due instant would mean a schedule
    /// changed from "daily at 4am" to "every hour" did nothing until 4am. Waking
    /// on a short ceiling and re-deciding costs nothing measurable and makes the
    /// setting behave the way the page implies it does.
    /// </remarks>
    private static readonly TimeSpan LongestSleep = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Once at startup so the first page load has something to say, rather
        // than an empty panel until the first interval elapses.
        await SafelyRefreshAsync(stoppingToken).ConfigureAwait(false);

        // The anchor for an interval schedule. Startup rather than the epoch, so
        // an install that is restarted often does not re-check on every boot and
        // then again on the interval.
        var lastCheck = timeProvider.GetUtcNow();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = timeProvider.GetUtcNow();
                var due = NextDue(lastCheck);

                if (due is null)
                {
                    // No timed check configured. Still worth staying alive: the
                    // schedule can be turned back on from the Settings page
                    // without restarting the service, and this is the loop that
                    // has to notice.
                    await Task.Delay(LongestSleep, timeProvider, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var wait = due.Value - now;

                if (wait > TimeSpan.Zero)
                {
                    await Task
                        .Delay(wait < LongestSleep ? wait : LongestSleep, timeProvider, stoppingToken)
                        .ConfigureAwait(false);

                    // Re-decide rather than assuming the sleep landed on the due
                    // time: it may have been the ceiling, and the schedule may
                    // have changed underneath it.
                    continue;
                }

                lastCheck = timeProvider.GetUtcNow();
                await SafelyRefreshAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
    }

    /// <summary>
    /// When the check after <paramref name="lastCheck"/> was due, or null when
    /// nothing is scheduled.
    /// </summary>
    /// <remarks>
    /// Measured from the last check rather than from now, and the difference
    /// matters. Asking for the next occurrence after <em>now</em> means a due
    /// time the wrapper slept through — a paused VM, a laptop lid, a machine
    /// that was off — is not late, it is simply gone, and the answer rolls
    /// forward to the following one. This way a missed check fires as soon as
    /// the wrapper notices, which for a poll is the only sensible reading of
    /// "every six hours".
    /// </remarks>
    private DateTimeOffset? NextDue(DateTimeOffset lastCheck)
    {
        var current = options.CurrentValue;

        if (!current.CheckForUpdates)
        {
            return null;
        }

        // Local time, like every other schedule the administrator sets: "check
        // at 4am" means 4am where the box is.
        return current.ScheduleOrDefault.NextOccurrence(lastCheck, lastCheck, TimeZoneInfo.Local);
    }

    private async Task SafelyRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await updates.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // RefreshAsync already swallows the expected network failures, so
            // anything reaching here is unexpected — and still not worth faulting
            // a background service the whole wrapper's health is judged by.
            logger.LogError(ex, "The Bedrock update check failed unexpectedly");
        }
    }
}
