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
        var schedule = supervisor.Definition.RestartSchedule;
        if (schedule.Mode == RestartScheduleMode.None)
        {
            return;
        }

        var status = supervisor.Status;

        // Only a healthy, running server. Restarting one that is already
        // starting, stopping, backing off or faulted would either duplicate work
        // already in progress or kick something that is trying to recover.
        if (status.State != InstanceState.Running)
        {
            return;
        }

        lock (_inFlight)
        {
            if (_inFlight.Contains(supervisor.InstanceId))
            {
                return;
            }
        }

        var warnings = schedule.WarningsOrDefault;
        var lead = warnings.Count > 0 ? warnings.Max() : TimeSpan.Zero;

        var now = timeProvider.GetUtcNow();
        var due = schedule.NextOccurrence(now, status.StartedUtc, TimeZone);
        if (due is null)
        {
            return;
        }

        var remaining = due.Value - now;

        // One tick of slack. The due moment almost never lands exactly on a tick,
        // so without it the longest warning is always a few seconds too late to
        // qualify and silently never fires — the one warning most likely to be
        // the reason an administrator configured warnings at all.
        if (remaining > lead + TickInterval)
        {
            return;
        }

        lock (_inFlight)
        {
            if (!_inFlight.Add(supervisor.InstanceId))
            {
                return;
            }
        }

        logger.LogInformation(
            "Scheduled restart for {InstanceId} is due at {Due}; warning players now",
            supervisor.InstanceId, due.Value);

        _ = RunAsync(supervisor, new GracefulStopRequest(
            StopReason.ScheduledRestart, lead, warnings, registry.Options.ShutdownDrainBudget),
            cancellationToken);
    }

    /// <summary>
    /// Runs the restart off the tick loop, so a fifteen-minute countdown on one
    /// instance does not hold up every other instance's schedule.
    /// </summary>
    private async Task RunAsync(
        InstanceSupervisor supervisor, GracefulStopRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // The backup rides the stop-start gap, while the files are static
            // (docs/design.md § 8.1). Its own toggle decides whether it runs, and its
            // failure does not hold the server down.
            await supervisor
                .RestartAsync(
                    request,
                    cancellationToken,
                    whileStopped: shutdownBackups is null
                        ? null
                        : ct => shutdownBackups.RunIfEnabledAsync(supervisor, request.Reason, ct))
                .ConfigureAwait(false);

            logger.LogInformation("Scheduled restart of {InstanceId} completed", supervisor.InstanceId);
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
            }
        }
    }
}
