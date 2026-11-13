using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Players;
using BedrockSentry.Services.Players;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Ties instance lifetime to the wrapper's own: starts what is configured to
/// auto-start, re-checks anything that refused to start while coming up, and
/// shuts everything down gracefully when the service stops.
///
/// The shutdown half is the load-bearing one. Windows is waiting on the service
/// control manager, so this has a budget rather than a best effort, and the
/// invariant from <c>docs/design.md</c> § 5.2 has to hold around it — the updater's
/// stop timeout exceeds the host's shutdown timeout, which exceeds the total
/// instance drain. Inverting any pair force-kills a server that is legitimately
/// mid-save.
/// <para>
/// The drain runs in <see cref="StoppingAsync"/>, which the host calls before any
/// hosted service's <c>StopAsync</c>, and not in its turn among them (<c>T112</c>).
/// Services stop in reverse registration order and the web server is registered
/// last, so it stops first, and it waits for its open connections until
/// <c>HostOptions.ShutdownTimeout</c>. A panel tab that reconnects as the stop begins
/// holds it that long, and a drain queued behind it began at the moment Windows was
/// told the service had stopped.
/// </para>
/// </summary>
public sealed class InstanceHostedService(
    InstanceRegistry registry,
    ILogger<InstanceHostedService> logger,
    PlayerDatabase? playerDatabase = null,
    IPlayerStore? players = null,
    IStartPreflight? startPreflight = null,
    IOrphanInspector? orphans = null,
    TimeProvider? time = null,
    IBeforeLaunch? beforeLaunch = null,
    ServersToStartAgain? startAgain = null) : IHostedLifecycleService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// Lifetime of the refused-start re-check (<c>T167</c>). Separate from the
    /// host's own token because the re-check has to be stopped <em>before</em> the
    /// drain in <see cref="StopAsync"/>, not alongside it.
    /// </summary>
    private CancellationTokenSource? _recheck;

    private Task? _recheckLoop;

    /// <summary>
    /// The re-check loop, once one has been started, so a caller can wait for it
    /// to finish rather than for a length of time (<c>T211</c>).
    /// </summary>
    /// <remarks>
    /// <para><strong>Exposed for the test that asserts the loop STOPS.</strong> A
    /// negative assertion — nothing more was asked — cannot poll for an absence,
    /// so it has to settle against a positive event, and the only honest one here
    /// is the loop completing. What it replaced was a fixed <c>Task.Delay(60)</c>,
    /// which is the `T194` shape: a wall-clock sleep that happens not to have
    /// failed yet is not evidence, and it went red on a loaded CI runner where the
    /// same test took fifteen times as long as it does locally.</para>
    ///
    /// <para>Null until something refuses to start, and it never faults: every
    /// exit from the loop is a return.</para>
    /// </remarks>
    internal Task? RecheckLoop => _recheckLoop;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (startPreflight is not null)
        {
            // Wired here, first, and not in the container: the inspector can only
            // be resolved lazily from a page otherwise, which would leave every
            // auto-start on boot unchecked — the case T149 was found in. Taking
            // it as a constructor parameter is what makes the container build it
            // before this method runs. The interface rather than PortInspector so
            // a test can push a fake through the same two lines.
            registry.StartPreflight = startPreflight;
        }
        else
        {
            // Loud rather than silent: with nothing wired, every launch skips the
            // port check and a held port is back to a crash loop with no mention
            // of ports. In the real host this means the DI registration went.
            logger.LogWarning(
                "No start pre-flight is wired; servers will not be checked for a held port before launch");
        }

        if (orphans is not null)
        {
            // Same wiring as the pre-flight above, and for the same reason: taken
            // as a constructor parameter so the container builds it before this
            // method runs, rather than lazily when a page first asks.
            registry.Orphans = orphans;
        }
        else
        {
            logger.LogWarning(
                "No orphan inspector is wired; a Bedrock server left over from a previous "
                + "wrapper will not be detected before a launch");
        }

        // Wired the same way, before any auto-start: a launch on boot is a launch
        // after a sync too, and must take its structures pack off (D103).
        registry.BeforeLaunch = beforeLaunch;

        if (playerDatabase is not null)
        {
            // Before anything starts: a server coming up and writing sessions
            // against a file that has not been migrated is the one ordering that
            // cannot be recovered from.
            await playerDatabase.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        if (players is not null)
        {
            // D26, the half the wrapper cannot observe as it happens. Any session
            // still open now belongs to a server that stopped while this process
            // was not running, so nothing knows when it ended. Dropping the row
            // loses a session; keeping it would count every minute of the outage,
            // and every minute until someone noticed, as playtime.
            foreach (var supervisor in registry.All)
            {
                var dropped = await players
                    .DropOpenSessionsAsync(supervisor.InstanceId, cancellationToken)
                    .ConfigureAwait(false);

                if (dropped > 0)
                {
                    logger.LogWarning(
                        "Dropped {Count} session(s) left open on {InstanceId} by a shutdown this "
                        + "wrapper did not see; their playtime is lost rather than guessed at",
                        dropped, supervisor.InstanceId);
                }
            }
        }

        var refused = new List<InstanceSupervisor>();

        // Milestone 13.5: the servers the wrapper's update stopped come back whether or
        // not they auto-start, and each launch moves its companion to this build's.
        var again = startAgain?.Take().ToHashSet(StringComparer.Ordinal) ?? [];

        foreach (var supervisor in registry.All)
        {
            var startingAgain = again.Contains(supervisor.InstanceId);

            if (!supervisor.Definition.AutoStart && !startingAgain)
            {
                logger.LogInformation(
                    "Instance {InstanceId} is configured not to auto-start", supervisor.InstanceId);
                continue;
            }

            if (startingAgain && !supervisor.Definition.AutoStart)
            {
                logger.LogInformation(
                    "Instance {InstanceId} was running when BedrockSentry updated itself; starting it again",
                    supervisor.InstanceId);
                Notice(supervisor, "Starting again: it was running when BedrockSentry updated itself.");
            }

            try
            {
                // Launch, deliberately without waiting for readiness. BDS took
                // 1.6 seconds to report ready on a warm world and much longer on
                // a cold one; multiplying that by the instance count and doing it
                // before the service reports started is how a wrapper gets killed
                // by the SCM for being slow to start.
                await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One instance failing to launch must not stop the others, and
                // must not stop the web interface — which is where an
                // administrator would go to find out what happened.
                logger.LogError(ex, "Instance {InstanceId} failed to start", supervisor.InstanceId);
            }

            // A refusal does not throw. The supervisor faults and returns, so this
            // loop used to carry on to the next instance and the refusal was
            // permanent for the life of the process (`T167`).
            if (WorthRechecking(supervisor))
            {
                refused.Add(supervisor);
            }
        }

        if (refused.Count > 0)
        {
            _recheck = new CancellationTokenSource();

            // Deliberately not awaited. StartAsync is on the SCM's clock, and the
            // first re-check is ten seconds away.
            _recheckLoop = Task.Run(
                () => RecheckRefusedAsync(refused, _recheck.Token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Re-checks instances that refused to start, on
    /// <see cref="RefusedStartRetry.Schedule"/>, until each one starts or the
    /// schedule runs out (<c>T167</c>).
    /// </summary>
    /// <remarks>
    /// <para>Scoped to the instances <em>this pass</em> refused, rather than
    /// sweeping the registry for refused faults on a timer. The defect is a
    /// service coming up against its own predecessor; a refusal a person caused by
    /// pressing <strong>Start</strong> has that person in front of the panel, and
    /// starting their server again from behind them is not this method's business.
    /// </para>
    ///
    /// <para>Every exit is quiet except the last: a re-check that works logs one
    /// line saying the refusal was transient, and a schedule that runs out logs a
    /// warning per instance, because a server that never came back is the state the
    /// machine has been left in.</para>
    /// </remarks>
    private async Task RecheckRefusedAsync(
        List<InstanceSupervisor> refused, CancellationToken cancellationToken)
    {
        var minutes = (int)Math.Round(RefusedStartRetry.Window.TotalMinutes);

        logger.LogInformation(
            "{Count} instance(s) refused to start; re-checking {Attempts} time(s) over the next "
            + "{Minutes} minute(s) in case what is holding them is this service's own predecessor",
            refused.Count, RefusedStartRetry.Schedule.Length, minutes);

        // And on each instance's own console, beside the ALERT the refusal just
        // wrote there. A re-check that only reaches the Windows event log leaves the
        // panel saying "refused" for two minutes with no hint that anything is
        // coming, which is the same silence in miniature.
        foreach (var supervisor in refused)
        {
            Notice(
                supervisor,
                $"re-checking up to {RefusedStartRetry.Schedule.Length} more time(s) over the next "
                + $"{minutes} minute(s), in case what is holding this server is the previous "
                + "service still shutting down");
        }

        var attempt = 0;

        foreach (var delay in RefusedStartRetry.Schedule)
        {
            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            attempt++;

            // Over a copy: the list is pruned inside the loop.
            foreach (var supervisor in refused.ToList())
            {
                if (!WorthRechecking(supervisor))
                {
                    // Something else decided. StillWorthRetrying says what each
                    // of those somethings is and why none of them may be
                    // overridden here.
                    refused.Remove(supervisor);
                    continue;
                }

                // Quiet when it meets the refusal the console already shows: the ALERT
                // was written once at boot, and repeating it on each fast re-check put
                // five copies of one sentence on the console (`T441` clause 1).
                try
                {
                    await supervisor.RecheckStartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex, "Instance {InstanceId} failed on re-check {Attempt}",
                        supervisor.InstanceId, attempt);
                }

                if (WorthRechecking(supervisor))
                {
                    continue;
                }

                // Not "started": a launch that happened can still have crashed
                // before this line. What is true is that the refusal is gone,
                // which is the thing being re-checked.
                refused.Remove(supervisor);
                logger.LogInformation(
                    "Instance {InstanceId} is no longer refused on re-check {Attempt}; whatever "
                    + "was holding it has gone", supervisor.InstanceId, attempt);
            }

            if (refused.Count == 0)
            {
                return;
            }
        }

        await RecheckSlowlyAsync(refused, attempt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// After the schedule: says once that each instance is still refused, then
    /// re-checks it every <see cref="RefusedStartRetry.SlowInterval"/> until it starts
    /// or something else decides for it (<c>T441</c>).
    /// </summary>
    private async Task RecheckSlowlyAsync(
        List<InstanceSupervisor> refused, int attempt, CancellationToken cancellationToken)
    {
        foreach (var supervisor in refused)
        {
            var outcome = RefusedStartRetry.GaveUpMessage(attempt, RefusedStartRetry.Window);

            logger.LogWarning(
                "Instance {InstanceId} {Outcome}: {Reason}",
                supervisor.InstanceId, outcome, supervisor.Status.FaultReason);

            // ALERT, the same prefix the refusal and the watchdog's give-up use,
            // because this is the same kind of sentence: a server that is down and
            // is not going to come back without somebody.
            Notice(supervisor, $"ALERT: {outcome}");
        }

        while (refused.Count > 0)
        {
            try
            {
                await Task.Delay(RefusedStartRetry.SlowInterval, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (var supervisor in refused.ToList())
            {
                if (!WorthRechecking(supervisor))
                {
                    refused.Remove(supervisor);
                    continue;
                }

                try
                {
                    await supervisor.RecheckStartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Instance {InstanceId} failed on a slow re-check", supervisor.InstanceId);
                }

                if (!WorthRechecking(supervisor))
                {
                    refused.Remove(supervisor);
                    logger.LogInformation(
                        "Instance {InstanceId} is no longer refused; whatever was holding it has gone",
                        supervisor.InstanceId);
                    Notice(supervisor, "no longer refused to start; starting it now");
                }
            }
        }
    }

    /// <summary>
    /// Writes to one instance's own console, the surface the refusal itself used.
    /// </summary>
    private void Notice(InstanceSupervisor supervisor, string message) =>
        supervisor.Console.Append(ConsoleOrigin.Notice, message, _time.GetUtcNow());

    private static bool WorthRechecking(InstanceSupervisor supervisor) =>
        RefusedStartRetry.StillWorthRetrying(
            supervisor.Status, supervisor.Definition.AutoStart, supervisor.Definition.Retired);

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The drain, before anything else in the host stops (<c>T112</c>).
    /// </summary>
    public Task StoppingAsync(CancellationToken cancellationToken) => DrainAsync();

    /// <summary>
    /// A second pass, which finds nothing running unless something started a server
    /// after <see cref="StoppingAsync"/> drained them.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => DrainAsync();

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Stops the refused-start re-check, then every running instance in parallel.
    /// </summary>
    private async Task DrainAsync()
    {
        // First, and before the list below is taken. A re-check that fired during
        // the drain would start a server the host is shutting down, and it would
        // not appear in `running` because it was stopped when that was computed —
        // so it would be left behind, unsupervised, holding the world open for the
        // next service start to refuse. That is the defect this retry exists to
        // fix, recreated by the fix.
        if (_recheck is not null)
        {
            await _recheck.CancelAsync().ConfigureAwait(false);

            if (_recheckLoop is not null)
            {
                try
                {
                    await _recheckLoop.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "The refused-start re-check did not stop cleanly");
                }
            }

            _recheck.Dispose();
            _recheck = null;
        }

        var running = registry.All
            .Where(s => s.Status.State is not (InstanceState.Stopped or InstanceState.Faulted))
            .ToList();

        if (running.Count == 0)
        {
            return;
        }

        var options = registry.Options;
        var request = GracefulStopRequest.ServiceStop(
            options.ServiceStopWarning, options.ShutdownDrainBudget);

        logger.LogInformation(
            "Stopping {Count} instance(s): {Warning}s warning, {Budget}s drain",
            running.Count,
            options.ServiceStopWarning.TotalSeconds,
            options.ShutdownDrainBudget.TotalSeconds);

        // In parallel, so the budget is per-instance rather than cumulative.
        // Stopping serially would make the drain time scale with the number of
        // servers, and the SCM's patience does not.
        await Task.WhenAll(running.Select(async supervisor =>
        {
            try
            {
                // Deliberately not passing the host's cancellation token. It is
                // already cancelled by the time a forced shutdown reaches here,
                // and threading it through would abort the warning and the save
                // this method exists to perform.
                await supervisor.StopAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Instance {InstanceId} did not stop cleanly", supervisor.InstanceId);
            }
        })).ConfigureAwait(false);
    }
}
