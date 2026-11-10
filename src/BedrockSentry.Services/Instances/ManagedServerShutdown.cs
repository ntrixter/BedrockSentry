using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Backups;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>What a planned outage did to the servers before it began.</summary>
/// <param name="Stopped">
/// Instance ids that were running and were stopped, so a caller whose outage
/// then falls through can put back exactly what it took down.
/// </param>
/// <param name="Failed">Instance ids that would not stop cleanly.</param>
/// <param name="CalledOff">
/// The countdown was called off before the stops began, so nothing stopped (<c>D100</c>).
/// </param>
public sealed record ManagedShutdownResult(
    IReadOnlyList<string> Stopped,
    IReadOnlyList<string> Failed,
    bool CalledOff = false)
{
    public static readonly ManagedShutdownResult Nothing = new([], []);

    public static readonly ManagedShutdownResult CalledOffBeforeTheStop = new([], [], CalledOff: true);

    public int Drained => Stopped.Count;
}

/// <summary>
/// Takes every managed server down for something the wrapper is about to do to
/// itself (<c>docs/design.md</c> § 12.3).
/// </summary>
/// <remarks>
/// A wrapper self-update stops the Windows Service, which takes every Bedrock
/// server with it. Left to the service stop, those servers get the short
/// countdown of § 5.2 — deliberately short, because Windows is waiting on the
/// service control manager. That budget is right for a stop somebody else
/// initiated and wrong for this one: an update the wrapper chose to apply is a
/// planned outage, planned <em>before</em> any clock is running.
/// </remarks>
public interface IManagedServerShutdown
{
    /// <param name="cancellationToken">
    /// Shortens the countdown; never abandons the stop. See the implementation.
    /// </param>
    /// <param name="countdown">
    /// Lets the caller call the countdown off, leaving every server running (<c>D100</c>).
    /// </param>
    Task<ManagedShutdownResult> DrainAsync(
        StopReason reason, TimeSpan warning, CancellationToken cancellationToken = default,
        DrainCountdown? countdown = null);

    /// <summary>Starts instances a drain stopped, for an outage that then fell through.</summary>
    Task RestoreAsync(IReadOnlyList<string> instanceIds, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IManagedServerShutdown"/>
public sealed class ManagedServerShutdown : IManagedServerShutdown
{
    private readonly InstanceRegistry _registry;
    private readonly ShutdownBackups _backups;
    private readonly TimeProvider _time;
    private readonly ILogger<ManagedServerShutdown> _log;

    public ManagedServerShutdown(
        InstanceRegistry registry,
        ShutdownBackups backups,
        TimeProvider time,
        ILogger<ManagedServerShutdown> log,
        ServersToStartAgain? startAgain = null)
    {
        _registry = registry;
        _backups = backups;
        _time = time;
        _log = log;
        _startAgain = startAgain;
    }

    /// <summary>Where the servers a drain stopped are kept for the start after the update (milestone 13.5).</summary>
    private readonly ServersToStartAgain? _startAgain;

    /// <remarks>
    /// <b>The countdown runs here, not inside the stop.</b> The obvious
    /// implementation hands <see cref="InstanceSupervisor.StopAsync"/> a long
    /// warning lead and lets its existing countdown do the work — but that
    /// countdown runs while holding the instance's gate, so an hour's warning
    /// would be an hour in which that instance could not be stopped, started or
    /// restarted by anything. Including, fatally, by
    /// <see cref="InstanceHostedService"/>: a Windows Update reboot landing
    /// mid-countdown would block the host's own shutdown on the same gate and
    /// blow through the budget § 5.2's invariant depends on.
    ///
    /// So the notices are sent from out here through the ordinary console, and
    /// the supervisor is only entered at the end — with no warning left to give,
    /// so it holds its gate for the seconds a stop actually takes. It is the same
    /// separation § 11.3 makes for a Bedrock build's window, and it should have
    /// been made here first.
    ///
    /// <paramref name="cancellationToken"/> therefore means "stop waiting", not
    /// "stop draining": a cancelled countdown falls straight through to the stop
    /// rather than abandoning servers half-warned. Cancelling is how a host
    /// shutdown says it has run out of patience for the courtesy, and it still
    /// gets the graceful stop underneath.
    ///
    /// Calling off is the other ending, and only through <paramref name="countdown"/>:
    /// the servers keep running and players are told (<c>D100</c>).
    /// </remarks>
    public async Task<ManagedShutdownResult> DrainAsync(
        StopReason reason, TimeSpan warning, CancellationToken cancellationToken = default,
        DrainCountdown? countdown = null)
    {
        var running = _registry.All
            .Where(s => s.Status.State is not (InstanceState.Stopped or InstanceState.Faulted))
            .ToList();

        if (running.Count == 0)
        {
            return ManagedShutdownResult.Nothing;
        }

        _log.LogInformation(
            "Draining {Count} instance(s) for a {Reason}: {Warning}s warning",
            running.Count, reason, warning.TotalSeconds);

        using (var waiting = countdown is null
                   ? null
                   : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, countdown.Token))
        {
            await CountDownAsync(running, reason, warning, waiting?.Token ?? cancellationToken)
                .ConfigureAwait(false);
        }

        if (countdown is not null && !countdown.TryBeginStopping())
        {
            _log.LogInformation("The countdown was called off; {Count} instance(s) stay up", running.Count);

            await Task
                .WhenAll(running.Select(s => SayAsync(s, CountdownMessage.CalledOff())))
                .ConfigureAwait(false);

            return ManagedShutdownResult.CalledOffBeforeTheStop;
        }

        var failed = new List<string>();
        var stopped = new List<string>();

        // In parallel, so the budget is per-instance rather than cumulative —
        // the same reasoning the service stop uses, for the same reason.
        await Task.WhenAll(running.Select(async supervisor =>
        {
            try
            {
                // No warning left: it has already been given, out here, without
                // holding this gate.
                await supervisor
                    .StopAsync(
                        GracefulStopRequest.Immediate(reason, _registry.Options.ShutdownDrainBudget),
                        CancellationToken.None)
                    .ConfigureAwait(false);

                // After the stop, not before: the files are static now, and this
                // is the toggle those instances have had in config since
                // milestone 7 with nothing calling it.
                await _backups
                    .RunIfEnabledAsync(supervisor, reason, CancellationToken.None)
                    .ConfigureAwait(false);

                lock (stopped)
                {
                    stopped.Add(supervisor.InstanceId);
                }
            }
            catch (Exception ex)
            {
                // Counted, logged, not rethrown. A server that will not stop
                // cleanly is a reason to look at it, not a reason to abandon an
                // update that is already staged — and the service stop that
                // follows will deal with it the blunt way regardless.
                lock (failed)
                {
                    failed.Add(supervisor.InstanceId);
                }

                _log.LogError(ex, "Instance {InstanceId} did not stop cleanly", supervisor.InstanceId);
            }
        })).ConfigureAwait(false);

        // Before the caller hands over to the update, so the wrapper that starts
        // after it, new build or rolled back, starts these again.
        _startAgain?.Record(stopped);

        return new ManagedShutdownResult(stopped, failed);
    }

    public async Task RestoreAsync(
        IReadOnlyList<string> instanceIds, CancellationToken cancellationToken = default)
    {
        // Started here, so the next wrapper start has nothing to start again for them.
        _startAgain?.Forget(instanceIds);

        foreach (var id in instanceIds)
        {
            try
            {
                await _registry.Get(id).StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One that will not come back must not stop the others coming
                // back. This path already exists because something else went
                // wrong; making it throw as well helps nobody.
                _log.LogError(ex, "Could not restart {InstanceId} after an abandoned outage", id);
            }
        }
    }

    /// <summary>Tells everyone online, at the points the warning implies.</summary>
    private async Task CountDownAsync(
        IReadOnlyList<InstanceSupervisor> running,
        StopReason reason,
        TimeSpan warning,
        CancellationToken cancellationToken)
    {
        if (warning <= TimeSpan.Zero)
        {
            return;
        }

        var points = CountdownLadder.PointsFor(warning).ToList();

        if (warning > CountdownLadder.LastCall)
        {
            points.Add(CountdownLadder.LastCall);
        }

        var deadline = _time.GetUtcNow() + warning;

        try
        {
            foreach (var point in points)
            {
                var wait = deadline - point - _time.GetUtcNow();

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                }

                await Task
                    .WhenAll(running.Select(s => SayAsync(s, CountdownMessage.ForStop(reason, point))))
                    .ConfigureAwait(false);
            }

            var remaining = deadline - _time.GetUtcNow();

            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Somebody has run out of patience for the courtesy — a host
            // shutdown, most likely, and the servers get their graceful stop now —
            // or the countdown was called off, which DrainAsync reads next.
            _log.LogInformation("The player countdown was cut short");
        }
    }

    /// <summary>
    /// Says something to whoever is online, if anyone can hear it.
    /// </summary>
    /// <remarks>
    /// A server that is not running has nobody to tell, and that is not an
    /// error. The outage still happens on time.
    /// </remarks>
    private async Task SayAsync(InstanceSupervisor supervisor, string command)
    {
        if (supervisor.Status.State is not InstanceState.Running)
        {
            return;
        }

        try
        {
            await supervisor.SendCommandAsync(command).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // Raced the server stopping between the check and the write.
            _log.LogDebug(ex, "Could not announce to {InstanceId}", supervisor.InstanceId);
        }
    }
}
