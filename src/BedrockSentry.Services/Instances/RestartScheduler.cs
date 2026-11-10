using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Fires each instance's configured restart at the time it is due.
///
/// The schedule arithmetic lives in <see cref="RestartSchedule"/>, including
/// both daylight-saving cases (<c>D18</c>). This class is only the clock: it
/// decides when a restart is close enough to begin warning players, and hands
/// off to the same graceful-stop path everything else uses.
///
/// A restart that comes due while its server is claimed (<see cref="InstanceClaim"/>),
/// such as by a sync, waits for the claim to end. It then runs if the server is
/// running and has not started since it came due.
/// </summary>
public sealed class RestartScheduler(
    InstanceRegistry registry,
    TimeProvider timeProvider,
    ILogger<RestartScheduler> logger,
    Backups.ShutdownBackups? shutdownBackups = null) : BackgroundService
{
    /// <summary>
    /// How often the schedule is re-examined. Short enough that a warning
    /// countdown starts within seconds of its due moment, long enough to be
    /// nothing on a machine also running Minecraft.
    /// </summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The zone <see cref="RestartScheduleMode.DailyAt"/> times are read in.
    /// Settable so the daylight-saving behaviour can be tested against a known
    /// zone rather than whatever the build agent happens to be in.
    /// </summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>
    /// Instances whose restart is already under way. A restart spends its whole
    /// warning countdown before the process state changes, so without this the
    /// next tick would start a second countdown on top of the first.
    /// </summary>
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The countdown of each restart under way, under the same lock as <see cref="_inFlight"/>,
    /// so a schedule set to none calls it off (<c>T425</c>).
    /// </summary>
    private readonly Dictionary<string, DrainCountdown> _countdowns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Restarts that came due while their server was claimed (<see cref="InstanceClaim"/>),
    /// by instance, under the same lock as <see cref="_inFlight"/>.
    /// </summary>
    /// <remarks>
    /// Kept here because the schedule cannot say it again once the claim ends: a
    /// daily time that has passed answers with tomorrow's.
    /// </remarks>
    private readonly Dictionary<string, WaitingRestart> _waiting = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="Due">When the restart came due.</param>
    /// <param name="Purpose">What it waited for, as <see cref="ClaimText"/> names it.</param>
    private sealed record WaitingRestart(DateTimeOffset Due, string Purpose);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Evaluate(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
    }

    /// <summary>
    /// Examines every instance once and starts any restart that has come due.
    ///
    /// Public because this is the part worth testing: the loop above is a
    /// <see cref="PeriodicTimer"/> and nothing else, and driving one from a fake
    /// clock across a span of hours fires it thousands of times to observe a
    /// single decision. Calling this directly tests the decision.
    /// </summary>
    public void Evaluate(CancellationToken cancellationToken = default)
    {
        foreach (var supervisor in registry.All)
        {
            try
            {
                Consider(supervisor, cancellationToken);
            }
            catch (Exception ex)
            {
                // One instance's schedule must not stop the others being checked.
                logger.LogError(
                    ex, "Failed to evaluate the restart schedule for {InstanceId}",
                    supervisor.InstanceId);
            }
        }
    }

    private void Consider(InstanceSupervisor supervisor, CancellationToken cancellationToken)
    {
        var id = supervisor.InstanceId;
        var schedule = supervisor.Definition.RestartSchedule;

        if (schedule.Mode == RestartScheduleMode.None)
        {
            DrainCountdown? counting;

            // A restart waiting for a claim goes with the schedule that made it, and so
            // does one counting down (T425): its stop has not begun, so nothing is lost.
            lock (_inFlight)
            {
                _waiting.Remove(id);
                counting = _countdowns.GetValueOrDefault(id);
                _countdowns.Remove(id);
            }

            if (counting is not null && counting.TryCallOff())
            {
                logger.LogInformation("Scheduled restart for {InstanceId} called off: the schedule is now none", id);
            }

            return;
        }

        var status = supervisor.Status;
        WaitingRestart? waiting;

        lock (_inFlight)
        {
            if (_inFlight.Contains(id))
            {
                return;
            }

            waiting = _waiting.GetValueOrDefault(id);
        }

        var warnings = schedule.WarningsOrDefault;
        var lead = warnings.Count > 0 ? warnings.Max() : TimeSpan.Zero;
        DateTimeOffset due;

        if (waiting is not null)
        {
            // Still claimed, or settling from whatever the claim did to it.
            if (status.ClaimedFor is not null
                || status.State is InstanceState.Starting or InstanceState.Stopping or InstanceState.Backoff)
            {
                return;
            }

            lock (_inFlight)
            {
                _waiting.Remove(id);
            }

            if (status.State != InstanceState.Running)
            {
                Notice(supervisor, ClaimText.RestartDropped(waiting.Purpose));
                return;
            }

            // A start since the restart came due, such as a sync's, is the fresh
            // process it was for. Another would warn and drop everybody again for
            // nothing, and an interval schedule already counts from that start.
            if (status.StartedUtc >= waiting.Due)
            {
                Notice(supervisor, ClaimText.RestartNotNeeded(waiting.Purpose));
                return;
            }

            Notice(supervisor, ClaimText.RestartAfterWait(waiting.Purpose));
            due = waiting.Due;
        }
        else
        {
            // Only a healthy, running server is restarted. Restarting one that is
            // already starting, stopping, backing off or faulted would either duplicate
            // work already in progress or kick something that is trying to recover.
            var now = timeProvider.GetUtcNow();
            var next = schedule.NextOccurrence(now, status.StartedUtc, TimeZone);
            if (next is null)
            {
                return;
            }

            if (status.State != InstanceState.Running)
            {
                // A job that stopped the server for its claim, such as a sync, holds it
                // stopped or starting when the countdown would begin: the restart waits
                // for the claim as it does on a running server, rather than passing
                // unremarked (T338 clause 9). Unclaimed, a stopped server is left alone.
                if (status.ClaimedFor is { } purpose && next.Value - now <= lead + TickInterval)
                {
                    Wait(supervisor, next.Value, purpose);
                }

                return;
            }

            // One tick of slack. The due moment almost never lands exactly on a tick,
            // so without it the longest warning is always a few seconds too late to
            // qualify and silently never fires — the one warning most likely to be
            // the reason an administrator configured warnings at all.
            if (next.Value - now > lead + TickInterval)
            {
                return;
            }

            due = next.Value;
        }

        var countdown = new DrainCountdown();

        lock (_inFlight)
        {
            if (!_inFlight.Add(id))
            {
                return;
            }

            _countdowns[id] = countdown;
        }

        // A claim is found by asking for one, never by looking first: the look and
        // the claim would be two moments, and a sync could begin between them.
        _ = RunAsync(supervisor, due, new GracefulStopRequest(
            StopReason.ScheduledRestart, lead, warnings, registry.Options.ShutdownDrainBudget),
            countdown, cancellationToken);
    }

    /// <summary>Holds a restart that came due while its server was claimed, and says so once.</summary>
    private void Wait(InstanceSupervisor supervisor, DateTimeOffset due, string purpose)
    {
        lock (_inFlight)
        {
            if (!_waiting.TryAdd(supervisor.InstanceId, new WaitingRestart(due, purpose)))
            {
                return;
            }
        }

        logger.LogInformation(
            "Scheduled restart for {InstanceId} due at {Due} waits for {Purpose}",
            supervisor.InstanceId, due, purpose);

        Notice(supervisor, ClaimText.RestartWaits(purpose));
    }

    private void Notice(InstanceSupervisor supervisor, string text) =>
        supervisor.Console.Append(ConsoleOrigin.Notice, text, timeProvider.GetUtcNow());

    /// <summary>
    /// Runs the restart off the tick loop, so a fifteen-minute countdown on one
    /// instance does not hold up every other instance's schedule.
    /// </summary>
    /// <remarks>
    /// Claimed for its length (<see cref="ClaimText.ScheduledRestart"/>), so a sync
    /// or an update cannot begin inside its countdown or its stop-start gap.
    /// </remarks>
    private async Task RunAsync(
        InstanceSupervisor supervisor,
        DateTimeOffset due,
        GracefulStopRequest request,
        DrainCountdown countdown,
        CancellationToken cancellationToken)
    {
        try
        {
            InstanceClaim claim;

            try
            {
                claim = await supervisor
                    .ClaimAsync(ClaimText.ScheduledRestart, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InstanceClaimedException claimed)
            {
                Wait(supervisor, due, claimed.Purpose);
                return;
            }

            logger.LogInformation(
                "Scheduled restart for {InstanceId} is due at {Due}; warning players now",
                supervisor.InstanceId, due);

            using (claim)
            {
                // The backup rides the stop-start gap, while the files are static
                // (docs/design.md § 8.1). Its own toggle decides whether it runs, and its
                // failure does not hold the server down.
                await claim
                    .RestartAsync(
                        request,
                        cancellationToken,
                        whileStopped: shutdownBackups is null
                            ? null
                            : ct => shutdownBackups.RunIfEnabledAsync(supervisor, request.Reason, ct),
                        countdown)
                    .ConfigureAwait(false);
            }

            logger.LogInformation("Scheduled restart of {InstanceId} ended", supervisor.InstanceId);
        }
        catch (OperationCanceledException)
        {
            // The host is stopping; its own shutdown path takes over from here.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled restart of {InstanceId} failed", supervisor.InstanceId);
        }
        finally
        {
            lock (_inFlight)
            {
                _inFlight.Remove(supervisor.InstanceId);

                if (_countdowns.GetValueOrDefault(supervisor.InstanceId) == countdown)
                {
                    _countdowns.Remove(supervisor.InstanceId);
                }
            }
        }
    }
}
