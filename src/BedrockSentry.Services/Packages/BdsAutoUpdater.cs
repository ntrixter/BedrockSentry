using System.Collections.Concurrent;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Packages;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Packages;

/// <summary>An update this instance is counting down to.</summary>
/// <param name="Version">As it will be announced to players.</param>
/// <param name="SeenUtc">When the wrapper first offered it to this instance.</param>
/// <param name="DueUtc">When it will be applied.</param>
/// <param name="PackageFileName">
/// The zip in the store, once it is there. Null while the download is still
/// running — the countdown starts before the bytes are down, so a slow line
/// eats into the delay rather than into the notice players get.
/// </param>
public sealed record PendingBdsUpdate(
    string Version,
    DateTimeOffset SeenUtc,
    DateTimeOffset DueUtc,
    string? PackageFileName,
    string? Failure = null)
{
    public TimeSpan RemainingAt(DateTimeOffset now)
    {
        var left = DueUtc - now;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}

/// <summary>
/// Applies Bedrock builds to the instances that asked to be updated
/// automatically (<c>docs/design.md</c> § 11.3).
/// </summary>
/// <remarks>
/// <b>The delay lives here, not in the stop.</b> The obvious implementation is
/// to hand <see cref="InstanceSupervisor.StopAsync"/> a half-hour warning lead
/// and let its existing countdown do the work — but that countdown runs inside
/// the instance's own gate, so a thirty-minute window would be thirty minutes
/// in which nobody could stop, start or restart that server, and nobody could
/// call the update off either. The one thing an operator most wants during a
/// countdown is the ability to interrupt it.
///
/// So this service holds the pending update, sends the notices itself through
/// the ordinary console, and only enters the supervisor at the end — with a
/// short final countdown, because by then players have had the whole window.
/// The instance stays completely usable throughout, and <see cref="Cancel"/> is
/// a dictionary removal rather than a cancellation racing a semaphore.
/// </remarks>
public sealed class BdsAutoUpdater : BackgroundService
{
    /// <summary>
    /// How often to look. Fine enough that a one-minute countdown point is not
    /// missed, coarse enough to be free.
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    /// <summary>The countdown the stop itself gives, after the window has run.</summary>
    private static readonly IReadOnlyList<TimeSpan> FinalWarnings = [TimeSpan.FromSeconds(10)];

    private readonly InstanceRegistry _registry;
    private readonly BdsUpdateService _updates;
    private readonly BdsUpdateInstaller _installer;
    private readonly TimeProvider _time;
    private readonly ILogger<BdsAutoUpdater> _log;

    private readonly ConcurrentDictionary<string, PendingBdsUpdate> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Versions an operator called off, per instance.
    /// </summary>
    /// <remarks>
    /// Without this, cancelling would be undone by the next tick — the build is
    /// still newer, so it would simply be scheduled again, and the button would
    /// look broken. Deliberately not persisted: a wrapper restart is a fair
    /// place to reconsider, and a declined version that outlived the process
    /// would be a pin nobody could see in the config file.
    ///
    /// <b>Saving the instance's update settings clears it</b>
    /// (<see cref="Reconsider"/>). A decline is scoped to the settings it was
    /// made under, and the only other way out is a newer build appearing —
    /// which, for anyone who cancelled and then went to change the delay,
    /// presents as a schedule that will not come back. That was the bug: mode
    /// off and on again looked like the obvious re-arm and did nothing.
    /// </remarks>
    private readonly ConcurrentDictionary<string, string> _declined =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Instances currently inside <see cref="BdsUpdateInstaller.ApplyAsync"/>.</summary>
    private readonly ConcurrentDictionary<string, byte> _applying =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Countdown points already spoken, so each is said once.</summary>
    private readonly ConcurrentDictionary<string, TimeSpan> _lastSpoken =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What each due update was last said to wait for (<see cref="InstanceClaim"/>),
    /// so it is said once per claim.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _waitingFor =
        new(StringComparer.OrdinalIgnoreCase);

    public BdsAutoUpdater(
        InstanceRegistry registry,
        BdsUpdateService updates,
        BdsUpdateInstaller installer,
        TimeProvider time,
        ILogger<BdsAutoUpdater> log)
    {
        _registry = registry;
        _updates = updates;
        _installer = installer;
        _time = time;
        _log = log;

        _registry.ServerUpdateSettingsChanged += Reconsidered;
    }

    /// <remarks>
    /// Both singletons for the life of the host, so this is tidiness rather
    /// than a leak that could bite — but a <see cref="BackgroundService"/> is
    /// disposable, and a handler still attached to a live registry after its
    /// owner is gone is the shape of bug that is very hard to find later.
    /// </remarks>
    public override void Dispose()
    {
        _registry.ServerUpdateSettingsChanged -= Reconsidered;
        base.Dispose();
    }

    private void Reconsidered(string instanceId) => Reconsider(instanceId);

    /// <summary>
    /// True while any instance is inside
    /// <see cref="BdsUpdateInstaller.ApplyAsync"/>.
    /// </summary>
    /// <remarks>
    /// Read by the wrapper's own self-update before it drains. Stopping a server
    /// that is mid-Bedrock-update is read by that update's health gate as a
    /// build which will not start: it rolls back a build that was fine, declines
    /// that version so it is never retried, and starts the server again — into
    /// the middle of a drain. Deferring the wrapper update by one check interval
    /// costs nothing; the alternative corrupts an unrelated decision.
    /// </remarks>
    public bool IsApplyingAnywhere => !_applying.IsEmpty;

    /// <summary>The instances currently taking a Bedrock build, for a log line.</summary>
    public IReadOnlyList<string> Applying => _applying.Keys.ToList();

    /// <summary>
    /// True while this instance is taking its pending build, which stays pending until
    /// the install returns, health gate and all (`T407`).
    /// </summary>
    public bool IsApplying(string instanceId) => _applying.ContainsKey(instanceId);

    /// <summary>What this instance is counting down to, or null.</summary>
    public PendingBdsUpdate? PendingFor(string instanceId) =>
        _pending.TryGetValue(instanceId, out var pending) ? pending : null;

    /// <summary>
    /// Calls off a pending update. The build is not offered to this instance
    /// again until the wrapper restarts, a newer one appears, or this server's
    /// update settings are saved.
    /// </summary>
    /// <remarks>
    /// Refused once the install has begun (`T407`): the server is already being
    /// stopped, and players would be told the update was called off as it happens.
    /// </remarks>
    public bool Cancel(string instanceId) =>
        !IsApplying(instanceId) && Withdraw(instanceId, decline: true);

    /// <summary>
    /// Forgets the build this instance declined, so the next sweep offers it
    /// again.
    /// </summary>
    /// <remarks>
    /// Wired to <see cref="InstanceRegistry.ServerUpdateSettingsChanged"/>:
    /// changing how a server takes updates is an unambiguous ask to reconsider,
    /// and without this the mode round trip an operator reaches for first —
    /// Automatic to Notify and back — was the one gesture that could not undo a
    /// Cancel. It clears a build that failed its health gate too. That is
    /// deliberate rather than overlooked: retrying is a human action either way,
    /// nothing loops on its own, and the gate that caught it once catches it
    /// again.
    /// </remarks>
    public bool Reconsider(string instanceId) => _declined.TryRemove(instanceId, out _);

    /// <param name="decline">
    /// True when an operator turned this build down, false when the schedule
    /// simply stopped applying — a mode change or a pin. Only the first is a
    /// judgement about the build, and only the first should stop it being
    /// offered again.
    /// </param>
    private bool Withdraw(string instanceId, bool decline)
    {
        if (!_pending.TryRemove(instanceId, out var pending))
        {
            return false;
        }

        if (decline)
        {
            _declined[instanceId] = pending.Version;
        }

        _lastSpoken.TryRemove(instanceId, out _);
        _waitingFor.TryRemove(instanceId, out _);

        _log.LogInformation(
            "The automatic update of {InstanceId} to Bedrock {Version} was called off",
            instanceId, pending.Version);

        if (TryGet(instanceId) is { } supervisor)
        {
            // Best effort and deliberately not awaited into the caller's
            // failure path: the update is already cancelled, and a server that
            // has since stopped must not turn a successful cancel into an error
            // on the page.
            _ = SayAsync(supervisor, BdsUpdateCountdown.Cancelled(pending.Version));
        }

        return true;
    }

    /// <summary>Brings a pending update forward to now.</summary>
    public bool ApplyNow(string instanceId)
    {
        if (IsApplying(instanceId) || !_pending.TryGetValue(instanceId, out var pending))
        {
            return false;
        }

        _pending[instanceId] = pending with { DueUtc = _time.GetUtcNow() };
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(Tick, _time, stoppingToken).ConfigureAwait(false);
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
    }

    /// <summary>
    /// One pass over every instance: schedule what is due to be scheduled,
    /// speak the countdown, apply what has come round.
    /// </summary>
    /// <remarks>
    /// Public so tests drive the decision directly instead of advancing a clock
    /// across half an hour to observe it — the same shape
    /// <see cref="Instances.RestartScheduler.Evaluate"/> uses, and for the same
    /// reason: the loop is a timer and nothing else, and the decision is the
    /// part that can be wrong.
    /// </remarks>
    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        foreach (var supervisor in _registry.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ConsiderAsync(supervisor, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One instance's trouble is not every instance's. A server whose
                // update keeps failing must not stop the others being swept.
                _log.LogError(ex, "The automatic update sweep failed for {InstanceId}", supervisor.InstanceId);
            }
        }
    }

    private async Task ConsiderAsync(InstanceSupervisor supervisor, CancellationToken cancellationToken)
    {
        var id = supervisor.InstanceId;

        if (_applying.ContainsKey(id))
        {
            return;
        }

        var settings = supervisor.Definition.ServerUpdate;

        if (settings.Mode is not BdsUpdateMode.Automatic || settings.IsPinned)
        {
            // Includes a pin arriving mid-countdown, which has to call the
            // update off: a pin outranks the mode, and leaving a scheduled
            // update running under one would make the two settings contradict
            // each other with the quieter one winning.
            // Withdrawn rather than declined: the operator turned the schedule
            // off, which says nothing about the build. Declining here is what
            // made Automatic → Notify → Automatic a one-way trip.
            Withdraw(id, decline: false);

            return;
        }

        if (_pending.TryGetValue(id, out var pending))
        {
            await AdvanceAsync(supervisor, pending, cancellationToken).ConfigureAwait(false);
            return;
        }

        var status = _updates.StatusFor(supervisor);

        if (!status.Actionable || status.Available is not { } offer)
        {
            return;
        }

        var version = offer.Version?.ToString() ?? offer.FileName;

        if (_declined.TryGetValue(id, out var declined)
            && string.Equals(declined, version, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await ScheduleAsync(supervisor, offer, version, cancellationToken).ConfigureAwait(false);
    }

    private async Task ScheduleAsync(
        InstanceSupervisor supervisor,
        BdsDownload offer,
        string version,
        CancellationToken cancellationToken)
    {
        var id = supervisor.InstanceId;
        var now = _time.GetUtcNow();
        var delay = supervisor.Definition.ServerUpdate.AutoApplyDelay;

        var pending = new PendingBdsUpdate(version, now, now + delay, null);

        _pending[id] = pending;
        _lastSpoken.TryRemove(id, out _);

        _log.LogInformation(
            "Instance {InstanceId} will update to Bedrock {Version} in {Delay}",
            id, version, delay);

        supervisor.Console.Append(
            ConsoleOrigin.Notice,
            delay > TimeSpan.Zero
                ? $"Bedrock {version} is available; this server updates automatically in "
                  + $"{CountdownMessage.Describe(delay)}"
                : $"Bedrock {version} is available; this server updates automatically now",
            now);

        // Straight away, not on the next tick. The opening notice is the one
        // that states the whole window, and a tick's delay would have it
        // announce "in 30 minutes" with 29 left — or, on a sweep that lands
        // later still, quietly become the wrong number.
        await AnnounceAsync(supervisor, pending, now, cancellationToken).ConfigureAwait(false);

        // Started here and not awaited: the countdown is already running, and
        // making players wait for a download before they are told anything would
        // spend their warning on a progress bar nobody can see.
        _ = DownloadAsync(id, offer, version);
    }

    private async Task DownloadAsync(string instanceId, BdsDownload offer, string version)
    {
        try
        {
            var package = await _updates.DownloadAsync(offer).ConfigureAwait(false);

            _pending.AddOrUpdate(
                instanceId,
                _ => new PendingBdsUpdate(version, _time.GetUtcNow(), _time.GetUtcNow(), package.FileName),
                (_, current) => current.Version == version
                    ? current with { PackageFileName = package.FileName }
                    : current);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex, "Could not download Bedrock {Version} for {InstanceId}; the update will not run",
                version, instanceId);

            // Recorded on the pending update rather than silently dropped. An
            // update that was announced to players and then simply never
            // happened is the shape of failure nobody investigates, because
            // nothing anywhere says it went wrong.
            _pending.AddOrUpdate(
                instanceId,
                _ => new PendingBdsUpdate(version, _time.GetUtcNow(), _time.GetUtcNow(), null, ex.Message),
                (_, current) => current.Version == version
                    ? current with { Failure = ex.Message }
                    : current);
        }
    }

    private async Task AdvanceAsync(
        InstanceSupervisor supervisor, PendingBdsUpdate pending, CancellationToken cancellationToken)
    {
        var id = supervisor.InstanceId;
        var now = _time.GetUtcNow();

        if (pending.Failure is not null)
        {
            // Left in place for the page to show, but no longer counted down to.
            return;
        }

        if (now < pending.DueUtc)
        {
            await AnnounceAsync(supervisor, pending, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (pending.PackageFileName is null)
        {
            // Due, but the download has not finished. Waiting beats applying
            // nothing, and the failure path above covers the case where it never
            // will.
            return;
        }

        await ApplyAsync(supervisor, pending, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Says once per claim that a due update waits for it.</summary>
    private void Waiting(InstanceSupervisor supervisor, PendingBdsUpdate pending, string purpose)
    {
        var id = supervisor.InstanceId;

        if (_waitingFor.TryGetValue(id, out var said) && said == purpose)
        {
            return;
        }

        _waitingFor[id] = purpose;

        _log.LogInformation(
            "The automatic update of {InstanceId} to Bedrock {Version} waits for {Purpose}",
            id, pending.Version, purpose);

        supervisor.Console.Append(
            ConsoleOrigin.Notice, ClaimText.UpdateWaits(pending.Version, purpose), _time.GetUtcNow());
    }

    /// <summary>
    /// Speaks the next countdown point that has fallen due.
    /// </summary>
    /// <remarks>
    /// Driven off "what is the smallest point still ahead of us" rather than off
    /// a timer per point, so a tick that arrives late — a busy box, a paused VM —
    /// says the right thing once instead of firing every missed point in a burst.
    /// </remarks>
    private async Task AnnounceAsync(
        InstanceSupervisor supervisor,
        PendingBdsUpdate pending,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var id = supervisor.InstanceId;
        var remaining = pending.RemainingAt(now);

        var due = BdsUpdateCountdown
            .PointsFor(pending.DueUtc - pending.SeenUtc)
            .Where(point => point >= remaining)
            .DefaultIfEmpty(TimeSpan.MinValue)
            .Min();

        if (due == TimeSpan.MinValue)
        {
            return;
        }

        if (_lastSpoken.TryGetValue(id, out var spoken) && spoken <= due)
        {
            return;
        }

        // Recorded only if it was actually said (`T142`). This used to mark the
        // rung spoken first, and `SayAsync` returns silently when the instance is
        // not running — so a rung falling inside an outage was booked as
        // delivered and the `spoken <= due` guard above suppressed it for good.
        // The rung that bites is the last one: the server comes back with under a
        // minute left and anybody rejoining gets no final warning at all.
        //
        // Nothing else needs to change. `due` is recomputed from the remaining
        // time on every tick, so a server that returns mid-countdown announces
        // whichever rung is appropriate *now* rather than replaying a stale one.
        if (await SayAsync(supervisor, BdsUpdateCountdown.Notice(pending.Version, due))
                .ConfigureAwait(false))
        {
            _lastSpoken[id] = due;
        }
    }

    private async Task ApplyAsync(
        InstanceSupervisor supervisor, PendingBdsUpdate pending, CancellationToken cancellationToken)
    {
        var id = supervisor.InstanceId;

        if (_updates.PackageFor(pending.PackageFileName!) is not { } package)
        {
            _log.LogError(
                "Bedrock {Version} is no longer in the package store; the automatic update of "
                + "{InstanceId} cannot run", pending.Version, id);

            _pending[id] = pending with
            {
                Failure = $"{pending.PackageFileName} is no longer in the package store.",
            };

            return;
        }

        if (!_applying.TryAdd(id, 0))
        {
            return;
        }

        try
        {
            // A short countdown, not the instance's: players have just had the
            // whole window, and repeating a fifteen-minute warning after a
            // thirty-minute one would be the second countdown in a row.
            var result = await _installer
                .ApplyAsync(supervisor, package, FinalWarnings, cancellationToken)
                .ConfigureAwait(false);

            _log.LogInformation(
                "Automatic update of {InstanceId} to Bedrock {Version}: {Detail}",
                id, pending.Version, result.Detail);

            if (result.RolledBack)
            {
                // Declined so the next sweep does not immediately try the build
                // that just failed to start. Somebody has to look at it.
                _declined[id] = pending.Version;
            }

            _pending.TryRemove(id, out _);
            _lastSpoken.TryRemove(id, out _);
            _waitingFor.TryRemove(id, out _);
        }
        catch (InstanceClaimedException claimed)
        {
            // Another job is part-way through this server, such as a sync. The
            // installer's claim refused before it stopped or installed anything, and
            // the build is not at fault, so it waits rather than being declined: the
            // first tick after the claim ends applies it, with the short final
            // countdown. Found by the installer asking, never by looking first, so
            // there is no moment between a look and the claim for a sync to begin in.
            Waiting(supervisor, pending, claimed.Purpose);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex, "The automatic update of {InstanceId} to Bedrock {Version} failed",
                id, pending.Version);

            _declined[id] = pending.Version;
            _pending[id] = pending with { Failure = ex.Message };
            _waitingFor.TryRemove(id, out _);
        }
        finally
        {
            _applying.TryRemove(id, out _);
        }
    }

    private InstanceSupervisor? TryGet(string instanceId) =>
        _registry.All.FirstOrDefault(s =>
            string.Equals(s.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Says something to whoever is online, if anyone can hear it.
    /// </summary>
    /// <remarks>
    /// A stopped server has no console to talk to, and that is not an error —
    /// the countdown is for players, and there are none. The update still
    /// happens on time.
    /// </remarks>
    /// <summary>
    /// Sends one announcement, returning whether it actually reached the server.
    /// </summary>
    /// <remarks>
    /// The return value exists for `T142`: a caller that records what it has
    /// announced has to be able to tell "said" from "silently dropped because the
    /// server is not running". A stopped server has no console, so not sending is
    /// correct — booking it as sent was not.
    /// </remarks>
    private async Task<bool> SayAsync(InstanceSupervisor supervisor, string command)
    {
        if (supervisor.Status.State is not InstanceState.Running)
        {
            return false;
        }

        try
        {
            await supervisor.SendCommandAsync(command).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // Raced the server stopping between the check and the write.
            _log.LogDebug(ex, "Could not announce to {InstanceId}", supervisor.InstanceId);
            return false;
        }
    }
}
