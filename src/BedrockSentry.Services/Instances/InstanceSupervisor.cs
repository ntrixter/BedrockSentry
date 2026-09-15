using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Players;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Owns the lifecycle of one Bedrock instance: launching it, watching it, and
/// deciding what to do when it goes away.
///
/// The interesting behaviour is entirely in "goes away". An exit the wrapper
/// asked for is a stop; an exit it did not is a failure, and failures are
/// bounded — back off, and after a configurable number of consecutive ones,
/// stop trying and raise an alert (<c>D15</c>). Both halves matter: retrying
/// forever turns one bad start into permanent world-file churn, and giving up
/// quietly turns it into a server nobody notices is down.
///
/// Everything time-dependent goes through <see cref="TimeProvider"/> so the
/// watchdog can be tested at speed. A backoff ladder that is only ever exercised
/// by waiting out real minutes is a backoff ladder that is never exercised.
/// </summary>
public sealed class InstanceSupervisor : IAsyncDisposable
{
    private readonly string _instanceId;
    private readonly InstanceDefinition _definition;
    private readonly string _workingDirectory;
    private readonly IBedrockProcessFactory _factory;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    /// <summary>
    /// Keeps the LAN discovery pair for whichever instance configured it, across
    /// this one's startup (<c>D24</c>). Null leaves the ports alone, which is
    /// what tests and any single-instance caller want.
    /// </summary>
    private readonly LanDiscoveryPortGuard? _discovery;

    /// <summary>
    /// Where session boundaries go (<c>D25</c>). Null records nothing, which is
    /// what every test that does not care about players wants.
    /// </summary>
    private readonly IPlayerStore? _players;

    /// <summary>
    /// Notes which Bedrock version this server reports, so "last updated" is
    /// answerable even for a build somebody unzipped over it by hand.
    /// </summary>
    private readonly ServerVersionLog? _versions;

    /// <summary>
    /// Session writes, chained rather than fired off independently.
    /// </summary>
    /// <remarks>
    /// Console output arrives on a synchronous event from the read pump, and a
    /// database write must not run on it — a stalled disk would stop the wrapper
    /// reading the server's output at all. But letting the writes race is worse
    /// than the block would have been: a disconnect landing before the connect it
    /// follows leaves a session open forever, which is exactly the state
    /// <c>D26</c> can only clean up by throwing the row away. So they queue, in
    /// arrival order, off the pump.
    /// </remarks>
    private readonly Lock _playerWriteGate = new();
    private Task _playerWrites = Task.CompletedTask;

    /// <summary>Serialises the public start/stop/restart calls against each other.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Resolved at each launch rather than captured, because the thing that
    /// answers it needs the registry and the registry builds this supervisor —
    /// so it cannot exist yet when the constructor runs. Null means the check is
    /// off, which is every test that is not about ports.
    /// </summary>
    private readonly Func<IStartPreflight?>? _startPreflight;

    /// <summary>
    /// Asked at each launch whether a Bedrock server this wrapper did not start
    /// is already on this instance's world (<c>D56</c>). Resolved lazily for the
    /// same reason as the pre-flight above; null means the check is off.
    /// </summary>
    private readonly Func<IOrphanInspector?>? _orphans;

    private readonly Lock _statusGate = new();

    private IBedrockProcess? _process;
    private Task? _monitor;
    private CancellationTokenSource? _lifetime;
    private TaskCompletionSource? _ready;
    private volatile bool _stopRequested;

    /// Said once per launch, not on every line that carries the prefix.
    private bool _notedMissingLogFile;
    private int _consecutiveFailures;
    private InstanceStatus _status;
    private bool _disposed;

    public InstanceSupervisor(
        string instanceId,
        InstanceDefinition definition,
        string workingDirectory,
        IBedrockProcessFactory factory,
        TimeProvider timeProvider,
        ILogger<InstanceSupervisor> logger,
        int consoleCapacity = ConsoleBuffer.DefaultCapacity,
        LanDiscoveryPortGuard? discoveryGuard = null,
        IPlayerStore? players = null,
        ServerVersionLog? versions = null,
        Func<IStartPreflight?>? startPreflight = null,
        Func<IOrphanInspector?>? orphans = null)
    {
        _players = players;
        _versions = versions;
        _startPreflight = startPreflight;
        _orphans = orphans;
        _instanceId = instanceId;
        _definition = definition;
        _workingDirectory = workingDirectory;
        _factory = factory;
        _time = timeProvider;
        _log = logger;
        _discovery = discoveryGuard;

        Console = new ConsoleBuffer(consoleCapacity);
        _status = InstanceStatus.Stopped(instanceId, definition.Name);
    }

    /// <summary>Recent console output, including the wrapper's own notices.</summary>
    public ConsoleBuffer Console { get; }

    public string InstanceId => _instanceId;

    public InstanceDefinition Definition => _definition;

    /// <summary>Where this instance actually runs, which may be outside the instance root.</summary>
    public string WorkingDirectory => _workingDirectory;

    /// <summary>
    /// Whether a Bedrock server is actually installed here. Checked from disk
    /// each time rather than cached: an administrator can unpack one by hand at
    /// any moment, and a cached "no" would leave the UI insisting the instance
    /// is empty while the server sits in it.
    /// </summary>
    public bool IsProvisioned =>
        File.Exists(Path.Combine(_workingDirectory, _definition.Executable));

    public InstanceStatus Status
    {
        get { lock (_statusGate) { return _status; } }
    }

    public event Action<InstanceStatus>? StatusChanged;

    /// <summary>
    /// Drops the version the last run announced, because the files it described
    /// have been replaced.
    /// </summary>
    /// <remarks>
    /// <see cref="InstanceStatus.ServerVersion"/> is the running server's own
    /// answer and outlives the process on purpose — a stopped instance should still
    /// say what it was last on. It stops being true only when something rewrites
    /// the binaries underneath it, which is an update applied while stopped, and
    /// nothing else calls this.
    /// </remarks>
    public void ForgetReportedVersion() => UpdateStatus(s => s with { ServerVersion = null });

    /// <summary>Points the published status at a new display name (`T89`).</summary>
    /// <remarks>
    /// <see cref="InstanceStatus"/> is immutable and carries its own copy of the
    /// name, taken from the definition when this supervisor was built. Renaming
    /// only the definition left that copy stale for the supervisor's whole
    /// lifetime, so the Instances tab strip and the Status page table — both fed
    /// by <c>Registry.Statuses()</c> — went on showing the old name while the page
    /// title showed the new one. Two names for one server in a single page load.
    /// Called by <c>InstanceRegistry.RenameAsync</c> after the config write
    /// succeeds, so a rename that fails to persist does not move the UI.
    /// </remarks>
    public void RenameTo(string name) => UpdateStatus(s => s with { Name = name });

    /// <summary>
    /// Launches the instance and begins supervising it. Returns once the process
    /// has been launched, not once the server is ready — those are 1.6 seconds
    /// apart on a warm world, and conflating them makes the status page lie.
    /// Use <see cref="WaitForReadyAsync"/> when readiness is what you need.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_definition.Retired)
        {
            throw new InvalidOperationException(
                $"Instance '{_instanceId}' is retired and cannot be started.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            _stopRequested = false;
            _consecutiveFailures = 0;

            // A monitor from the previous run may still be asleep in its backoff.
            // Disposing its token without cancelling leaves it running on a token
            // that can never fire: it wakes, launches a second process, and — now
            // that a launch can be refused — refuses that launch against the
            // wrapper's own child and forgets the live one, which is T149's seed
            // made by the wrapper itself. Cancel it and wait, the way StopAsync
            // does; under the gate with no live process this is quick. A monitor
            // caught in the instant between its delay ending and its launch is
            // still a race, narrower than before but not closed here.
            _lifetime?.Cancel();
            await AwaitMonitorAsync().ConfigureAwait(false);
            _lifetime?.Dispose();
            _lifetime = new CancellationTokenSource();

            // D24. The discovery pair is held across this instance's own bind
            // unless the pair is its own, so that a server which does not need
            // 19132 cannot take it from one that does.
            var hold = await AcquireDiscoveryAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var outcome = await LaunchAsync(_lifetime.Token).ConfigureAwait(false);

                if (outcome == LaunchOutcome.Refused)
                {
                    // Nothing was started, so there is nothing to supervise and
                    // nothing to wait on before the discovery pair can go. The
                    // status and the console already say why (T149).
                    if (hold is not null)
                    {
                        await hold.DisposeAsync().ConfigureAwait(false);
                    }

                    return;
                }

                _monitor = Task.Run(() => MonitorAsync(_lifetime.Token), CancellationToken.None);
            }
            catch
            {
                if (hold is not null)
                {
                    await hold.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }

            if (hold is not null)
            {
                _ = ReleaseDiscoveryWhenReadyAsync(hold);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Takes the discovery pair, unless this is the instance that configured it.
    /// Null when there is no guard at all, which is the single-instance case and
    /// every test that does not care.
    /// </summary>
    private async Task<IAsyncDisposable?> AcquireDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (_discovery is null)
        {
            return null;
        }

        var owns = _discovery.OwnsDiscoveryPair(PortInspector.PortsOf(_workingDirectory));
        return await _discovery.HoldAsync(_instanceId, owns, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lets the discovery pair go once BDS is past its own bind, or once the
    /// budget runs out.
    /// </summary>
    /// <remarks>
    /// Detached from <see cref="StartAsync"/> on purpose: that call returns once
    /// the process has launched, not once the server is ready, and awaiting the
    /// release here would quietly change what starting an instance means to every
    /// caller. The release itself is unconditional — ready, timeout, cancellation
    /// and a throw all arrive at the same finally. A leaked hold would lock the
    /// owning instance out of its own port until the service restarts, which is
    /// the failure this whole path exists to prevent.
    /// </remarks>
    private async Task ReleaseDiscoveryWhenReadyAsync(IAsyncDisposable hold)
    {
        try
        {
            if (!await WaitForReadyAsync(LanDiscoveryPortGuard.DefaultHoldTimeout).ConfigureAwait(false))
            {
                _log.LogWarning(
                    "{InstanceId} did not report itself started within the LAN discovery hold "
                    + "budget; releasing the discovery ports anyway", _instanceId);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex, "Waiting on {InstanceId} before releasing the LAN discovery ports failed",
                _instanceId);
        }
        finally
        {
            await hold.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for BDS to report <c>Server started.</c> False on timeout — which is
    /// not the same as a failure, and the caller decides which it is.
    /// </summary>
    public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var ready = _ready;
        if (ready is null)
        {
            return false;
        }

        if (ready.Task.IsCompleted)
        {
            return true;
        }

        var delay = Task.Delay(timeout, _time, cancellationToken);
        return await Task.WhenAny(ready.Task, delay).ConfigureAwait(false) != delay;
    }

    /// <summary>
    /// Warns players, stops the server, and waits for it to go. Kills it only if
    /// the drain budget runs out.
    ///
    /// This is the single graceful-shutdown path: scheduled restarts, service
    /// stop, BDS update and wrapper self-update all arrive here with a different
    /// <see cref="GracefulStopRequest"/> rather than a different implementation.
    /// </summary>
    public async Task StopAsync(GracefulStopRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stopRequested = true;

            var process = _process;
            if (process is null || process.HasExited)
            {
                _lifetime?.Cancel();
                await AwaitMonitorAsync().ConfigureAwait(false);
                UpdateStatus(s => s with { State = InstanceState.Stopped, ProcessId = null, NextRetryUtc = null });
                return;
            }

            UpdateStatus(s => s with { State = InstanceState.Stopping, NextRetryUtc = null });
            await WarnAndWaitAsync(request, cancellationToken).ConfigureAwait(false);

            Note($"stopping ({request.Reason})");
            await SendInternalAsync("stop", cancellationToken).ConfigureAwait(false);

            var exit = process.WaitForExitAsync(CancellationToken.None);
            var budget = Task.Delay(request.DrainBudget, _time, cancellationToken);
            if (await Task.WhenAny(exit, budget).ConfigureAwait(false) == budget)
            {
                // The failure being reported, not a tidy-up. A server we can no
                // longer talk to must not be left running and unmanaged.
                Note($"did not exit within {request.DrainBudget.TotalSeconds:0.#}s; killing");
                _log.LogWarning(
                    "Instance {InstanceId} did not exit within its drain budget; killing", _instanceId);
                process.Kill();
            }

            await AwaitMonitorAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <param name="whileStopped">
    /// Runs between the stop and the start, while the world files are static —
    /// backup-on-shutdown lives here (<c>docs/design.md</c> § 8.1), and the update
    /// flows will use the same seam. A throw from it still starts the server:
    /// whatever went wrong in the gap, a server left down is not the answer.
    /// </param>
    public async Task RestartAsync(
        GracefulStopRequest request,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? whileStopped = null)
    {
        await StopAsync(request, cancellationToken).ConfigureAwait(false);

        if (whileStopped is not null)
        {
            try
            {
                await whileStopped(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(
                    ex, "The between-stop-and-start step failed for {InstanceId}; starting anyway",
                    _instanceId);
            }
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a command as if it had been typed into the console, echoing it into
    /// the buffer so the log shows what the wrapper asked for as well as what the
    /// server answered.
    /// </summary>
    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var process = _process;
        if (process is null || process.HasExited)
        {
            throw new InvalidOperationException($"Instance '{_instanceId}' is not running.");
        }

        // An operator typing `stop` means it. BDS also announces its own
        // shutdown, and OnOutput catches that for every route including in-game
        // `/stop` — but the announcement arrives after the command, and a server
        // that exits between the two would still be read as a crash. Recording
        // the intent here closes that window for the one path where the wrapper
        // knows in advance.
        //
        // The command itself is still passed through verbatim. All that changes
        // is that the exit which follows is recorded as asked-for.
        if (IsStopCommand(command))
        {
            _stopRequested = true;
            UpdateStatus(s => s with { State = InstanceState.Stopping, NextRetryUtc = null });
            Note("stop typed at the console; the watchdog will not restart this instance");
        }

        await SendInternalAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a console command shuts the server down. Deliberately an exact
    /// match on the whole line: <c>stop</c> is the only BDS command that ends the
    /// process, and treating anything that merely starts with it — a hypothetical
    /// <c>stopsound</c>, or a player named in a longer command — as a shutdown
    /// would suppress the watchdog when it is genuinely needed.
    /// </summary>
    private static bool IsStopCommand(string command) =>
        command.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase);

    private async Task SendInternalAsync(string command, CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            return;
        }

        Console.Append(ConsoleOrigin.Command, command, _time.GetUtcNow());
        await process.SendAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Plays out the countdown. Warnings are scheduled against a fixed deadline
    /// rather than by sleeping between them, so a slow <c>say</c> does not push
    /// every later warning — and the stop itself — further out.
    /// </summary>
    private async Task WarnAndWaitAsync(GracefulStopRequest request, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + request.WarningLead;

        foreach (var warning in request.EffectiveWarnings())
        {
            var wait = deadline - warning - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }

            await SendInternalAsync(
                CountdownMessage.ForStop(request.Reason, warning, request.Version),
                cancellationToken).ConfigureAwait(false);
        }

        var remaining = deadline - _time.GetUtcNow();
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The process ids this supervisor is responsible for, so the orphan search
    /// cannot report our own child back to us.
    /// </summary>
    private int[] Ours() => _process?.ProcessId is { } pid ? [pid] : [];

    /// <summary>
    /// Faults the instance with <paramref name="reason"/> and launches nothing.
    /// </summary>
    /// <remarks>
    /// Shared by both refusals — a held port and an orphan on the world — so the
    /// state they leave behind cannot drift apart. Setting <c>_ready</c> to null
    /// is the load-bearing line: it is replaced further down, *after* this
    /// returns, so without it a refusal leaves the previous run's completed task
    /// in place and <c>WaitForReadyAsync</c> answers true for a server that was
    /// never launched — the one answer the update installer's health gate must
    /// never get, since it deletes the rollback build on the strength of it.
    /// </remarks>
    private LaunchOutcome Refuse(string reason)
    {
        _process = null;
        _ready = null;

        UpdateStatus(s => s with
        {
            State = InstanceState.Faulted,
            ProcessId = null,
            ReadyUtc = null,
            NextRetryUtc = null,
            FaultReason = reason,

            // Moves with the reason, never without it (`T167`). This is what lets
            // the boot retry re-check a refusal and leave a give-up alone without
            // pattern-matching the sentence above.
            FaultKind = InstanceFault.RefusedToStart,
        });

        // Loud for the same reason the watchdog's give-up is: a server that is
        // quietly not running is worse than one that says why.
        Note($"ALERT: {reason}");
        _log.LogError("Instance {InstanceId} {Reason}", _instanceId, reason);

        return LaunchOutcome.Refused;
    }

    /// <summary>
    /// How a launch attempt ended. Only <see cref="Refused"/> changes what the
    /// caller does: a failed launch goes through the watchdog's accounting like
    /// any other crash, a refused one has already faulted and must not.
    /// </summary>
    private enum LaunchOutcome
    {
        Launched,
        Failed,
        Refused,
    }

    /// <summary>
    /// Starts a process. A launch that throws is not propagated: it is the same
    /// kind of event as a crash two seconds in — a failed attempt — and routing
    /// it through the same accounting is what stops a mistyped executable path
    /// from being retried forever.
    /// </summary>
    /// <remarks>
    /// A launch that is <em>refused</em> is a different thing and is kept out of
    /// that accounting on purpose (<c>T149</c>). Something else holds this
    /// server's port, and BDS would open — and repair — the world before it
    /// found that out. Retrying would be the crash loop this check exists to
    /// prevent, and the fault reason would say "gave up after 3 consecutive
    /// failures" where the useful sentence names a process and a PID. So a
    /// refusal faults immediately, with that sentence, and launches nothing.
    /// </remarks>
    private async Task<LaunchOutcome> LaunchAsync(CancellationToken cancellationToken)
    {
        // Asked before the port check, because it is the more specific question
        // and its sentence is the more useful one. A held port is a symptom; a
        // process already on this world is the thing `T149` is about, and an
        // orphan whose configured port has since changed would pass the port
        // check and still destroy the world.
        if (_orphans?.Invoke() is { } orphans)
        {
            IReadOnlyList<OrphanedServer> found;

            try
            {
                found = orphans.Find(_workingDirectory, Ours());
            }
            catch (Exception ex)
            {
                // Same posture as the port pre-flight: not being able to ask is
                // not a reason to refuse a start that would otherwise work.
                _log.LogWarning(
                    ex, "Could not check {InstanceId} for orphaned servers; launching anyway",
                    _instanceId);
                found = [];
            }

            if (found.Count > 0)
            {
                return Refuse(OrphanText.RefusalReason(found));
            }
        }

        if (_startPreflight?.Invoke() is { } preflight)
        {
            IReadOnlyList<PortConflict> conflicts;

            try
            {
                conflicts = preflight.ConflictsForStart(_instanceId, _workingDirectory);
            }
            catch (Exception ex)
            {
                // Best effort, the same posture as the installer's own guard:
                // not being able to ask the question is not a reason to refuse
                // a start that would otherwise work.
                _log.LogWarning(
                    ex, "Could not check {InstanceId}'s ports before starting it; launching anyway",
                    _instanceId);
                conflicts = [];
            }

            if (conflicts.Count > 0)
            {
                return Refuse(PortConflictText.RefusalReason(conflicts));
            }
        }

        var process = _factory.Create(_instanceId, _definition, _workingDirectory);
        process.OutputReceived += OnOutput;

        _notedMissingLogFile = false;
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedUtc = _time.GetUtcNow();

        try
        {
            await process.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            process.OutputReceived -= OnOutput;
            await process.DisposeAsync().ConfigureAwait(false);

            _process = null;
            UpdateStatus(s => s with
            {
                State = InstanceState.Starting,
                ProcessId = null,
                StartedUtc = startedUtc,
                ReadyUtc = null,
            });

            Note($"failed to launch: {ex.Message}");
            _log.LogError(ex, "Instance {InstanceId} failed to launch", _instanceId);
            return LaunchOutcome.Failed;
        }

        _process = process;
        UpdateStatus(s => s with
        {
            State = InstanceState.Starting,
            ProcessId = process.ProcessId,
            StartedUtc = startedUtc,
            ReadyUtc = null,
            FaultReason = null,
            FaultKind = InstanceFault.None,
            NextRetryUtc = null,
        });

        // Launch, not Notice: this is where one run of the server ends and the
        // next begins, and the console draws it as a rule across the page.
        Console.Append(
            ConsoleOrigin.Launch,
            $"starting (pid {process.ProcessId})",
            _time.GetUtcNow());

        _log.LogInformation(
            "Instance {InstanceId} launched as pid {Pid} in {Directory}",
            _instanceId, process.ProcessId, _workingDirectory);

        return LaunchOutcome.Launched;
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            int? exitCode = null;
            var process = _process;

            if (process is not null)
            {
                try
                {
                    exitCode = await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                process.OutputReceived -= OnOutput;
                await process.DisposeAsync().ConfigureAwait(false);
                _process = null;
            }

            var exitedUtc = _time.GetUtcNow();
            var ranFor = exitedUtc - (Status.StartedUtc ?? exitedUtc);

            // D26. Before either branch below: a crash and a clean stop both end
            // every session on this instance, and both end them here, because the
            // wrapper saw the process go and so knows when.
            await ClosePlayerSessionsAsync(exitedUtc).ConfigureAwait(false);

            if (_stopRequested)
            {
                UpdateStatus(s => s with
                {
                    State = InstanceState.Stopped,
                    ProcessId = null,
                    LastExitCode = exitCode,
                    LastExitUtc = exitedUtc,
                    ConsecutiveFailures = 0,
                    NextRetryUtc = null,
                });

                Note(exitCode is { } code ? $"stopped (exit code {code})" : "stopped");
                return;
            }

            // D17: only a run that lasted clears the counter. Resetting on any
            // successful launch would let a server that dies ten seconds in retry
            // forever, which is precisely the loop D15 exists to bound.
            var watchdog = _definition.Watchdog;
            if (ranFor >= watchdog.StableAfter)
            {
                _consecutiveFailures = 0;
            }

            _consecutiveFailures++;

            if (watchdog.MaxConsecutiveFailures > 0
                && _consecutiveFailures >= watchdog.MaxConsecutiveFailures)
            {
                var reason = exitCode is { } code
                    ? $"gave up after {_consecutiveFailures} consecutive failures (last exit code {code})"
                    : $"gave up after {_consecutiveFailures} consecutive failures";

                UpdateStatus(s => s with
                {
                    State = InstanceState.Faulted,
                    ProcessId = null,
                    LastExitCode = exitCode,
                    LastExitUtc = exitedUtc,
                    ConsecutiveFailures = _consecutiveFailures,
                    NextRetryUtc = null,
                    FaultReason = reason,
                    FaultKind = InstanceFault.GaveUp,
                });

                // Loud on purpose. A watchdog that gives up silently converts a
                // crash into an outage nobody is told about.
                Note($"ALERT: {reason}");
                _log.LogError(
                    "Instance {InstanceId} {Reason}; no further restarts will be attempted",
                    _instanceId, reason);
                return;
            }

            var backoff = watchdog.BackoffFor(_consecutiveFailures);
            UpdateStatus(s => s with
            {
                State = InstanceState.Backoff,
                ProcessId = null,
                LastExitCode = exitCode,
                LastExitUtc = exitedUtc,
                ConsecutiveFailures = _consecutiveFailures,
                NextRetryUtc = exitedUtc + backoff,
            });

            Note($"exited unexpectedly after {ranFor.TotalSeconds:0.#}s "
                 + $"({WatchdogLadder.FailureCount(_consecutiveFailures, watchdog)}); "
                 + $"retrying in {backoff.TotalSeconds:0.#}s");
            _log.LogWarning(
                "Instance {InstanceId} exited unexpectedly (code {ExitCode}); retry {Failure} in {Backoff}",
                _instanceId, exitCode, _consecutiveFailures, backoff);

            try
            {
                await Task.Delay(backoff, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Re-checked after the wait: a stop issued during a long backoff must
            // not be overtaken by the restart it was trying to prevent.
            if (_stopRequested)
            {
                UpdateStatus(s => s with { State = InstanceState.Stopped, NextRetryUtc = null });
                return;
            }

            if (await LaunchAsync(cancellationToken).ConfigureAwait(false) == LaunchOutcome.Refused)
            {
                // Faulted with the reason already. Looping round would count it
                // as one more crash and try again, which is the exact sequence
                // T149 records opening a live world three times.
                return;
            }
        }
    }

    /// <summary>
    /// Lines BDS emits when it is shutting down on purpose, whoever asked.
    /// </summary>
    /// <remarks>
    /// The server announcing its own shutdown is better evidence than anything
    /// the wrapper can infer, because it covers routes the wrapper never sees —
    /// an operator running <c>/stop</c> in-game being the one that matters. The
    /// alternative is enumerating every way a shutdown can be triggered and
    /// missing one, which shows up as the wrapper restarting a server somebody
    /// deliberately stopped.
    /// </remarks>
    private static readonly string[] ShutdownAnnouncements =
    [
        "Server stop requested",
        "Stopping server",
    ];

    private void OnOutput(ConsoleOrigin origin, string line)
    {
        // Explained once because BDS repeats it on every line, and because a
        // prefix reading "NO LOG FILE!" looks like a fault when it is not one.
        if (!_notedMissingLogFile && line.StartsWith(BdsLogParser.NoLogFilePrefix, StringComparison.Ordinal))
        {
            _notedMissingLogFile = true;
            Note(
                "this server is not writing a log file of its own, so it prefixes every line "
                + "with \"NO LOG FILE!\". That is normal, and BedrockSentry is keeping the "
                + "transcript either way — the prefix is stripped from the lines below.");
        }

        var recorded = Console.Append(origin, line, _time.GetUtcNow());

        // D25. A session boundary, read off the server's own console. Timestamped
        // with when the line arrived rather than when the write lands, so a slow
        // database cannot stretch somebody's playtime.
        if (PlayerEventParser.TryParse(recorded.Message, out var playerEvent))
        {
            RecordPlayerEvent(playerEvent, recorded.ReceivedUtc);
            RaisePlayerActivity(playerEvent);
            return;
        }

        // "Version: 1.26.36.1", emitted a few lines into startup. Captured here
        // because it is the running server's own answer; the package it was
        // installed from stops being the truth the moment anyone updates by hand.
        if (recorded.Message.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
        {
            var version = recorded.Message["Version:".Length..].Trim();
            if (version.Length > 0)
            {
                UpdateStatus(s => s with { ServerVersion = version });

                // Writes nothing when the version has not changed, which is the
                // common case on every ordinary restart.
                var change = _versions?.Record(_instanceId, version, recorded.ReceivedUtc);

                if (change?.IsAnUpdate == true)
                {
                    Note($"Bedrock updated from {change.PreviousVersion} to {change.Version}");
                }
            }

            return;
        }

        if (recorded.Message.StartsWith("Server started", StringComparison.OrdinalIgnoreCase)
            && Status.State == InstanceState.Starting)
        {
            UpdateStatus(s => s with { State = InstanceState.Running, ReadyUtc = recorded.ReceivedUtc });
            _ready?.TrySetResult();
            _log.LogInformation("Instance {InstanceId} reported ready", _instanceId);
            return;
        }

        // Checked only when the wrapper did not already know: a stop it started
        // has set this flag long before the announcement arrives, and re-running
        // the notice would put a duplicate line in every transcript.
        if (!_stopRequested && IsShutdownAnnouncement(recorded.Message))
        {
            _stopRequested = true;
            UpdateStatus(s => s with { State = InstanceState.Stopping, NextRetryUtc = null });

            Note("the server reported it is shutting down; the watchdog will not restart it");
            _log.LogInformation(
                "Instance {InstanceId} announced its own shutdown; treating the exit as intentional",
                _instanceId);
        }
    }

    /// <summary>
    /// Raised when a player arrives or leaves, after the session write is
    /// queued.
    /// </summary>
    /// <remarks>
    /// Separate from the session write rather than folded into it: that write is
    /// the record and must not be delayed or skipped by anything watching, while
    /// a subscriber here is free to be slow. Handlers run on the console pump,
    /// so anything that blocks belongs on a task of the subscriber's own —
    /// <see cref="Backups.BackupScheduler"/> is the one that does this.
    /// </remarks>
    public event Action<InstanceSupervisor, PlayerEvent>? PlayerActivity;

    private void RaisePlayerActivity(PlayerEvent playerEvent)
    {
        try
        {
            PlayerActivity?.Invoke(this, playerEvent);
        }
        catch (Exception ex)
        {
            // This is the console pump. A subscriber that throws must not be
            // able to stop the wrapper reading its server's output.
            _log.LogError(
                ex, "A player-activity subscriber threw for {InstanceId}", _instanceId);
        }
    }

    /// <summary>Queues a session write behind whatever is already queued.</summary>
    private void RecordPlayerEvent(PlayerEvent playerEvent, DateTimeOffset atUtc)
    {
        if (_players is null)
        {
            return;
        }

        lock (_playerWriteGate)
        {
            _playerWrites = _playerWrites.ContinueWith(
                _ => WritePlayerEventAsync(playerEvent, atUtc),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task WritePlayerEventAsync(PlayerEvent playerEvent, DateTimeOffset atUtc)
    {
        try
        {
            await _players!.RecordAsync(_instanceId, playerEvent, atUtc).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never rethrown: this is chained off the console pump, and a faulted
            // link would silently stop every later write on this instance. Losing
            // one session beats losing all of them without a word.
            _log.LogError(
                ex, "Could not record {Kind} for {Gamertag} on {InstanceId}",
                playerEvent.Kind, playerEvent.Gamertag, _instanceId);
        }
    }

    /// <summary>
    /// Ends whatever is still open now that the process has gone (<c>D26</c>).
    /// </summary>
    /// <remarks>
    /// Queued writes are drained first, or a disconnect still in flight would land
    /// after the close and reopen nothing while overwriting a correct end time
    /// with its own. The exit timestamp is the honest end: the wrapper watched it
    /// happen, so nothing here is a guess — which is what separates this from the
    /// startup sweep that deletes rather than closes.
    /// </remarks>
    private async Task ClosePlayerSessionsAsync(DateTimeOffset exitedUtc)
    {
        if (_players is null)
        {
            return;
        }

        Task pending;
        lock (_playerWriteGate)
        {
            pending = _playerWrites;
        }

        try
        {
            await pending.ConfigureAwait(false);
            await _players.CloseOpenSessionsAsync(_instanceId, exitedUtc).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex, "Could not close the open sessions for {InstanceId} after it exited", _instanceId);
        }
    }

    private static bool IsShutdownAnnouncement(string message) =>
        ShutdownAnnouncements.Any(a => message.StartsWith(a, StringComparison.OrdinalIgnoreCase));

    private void Note(string message) =>
        Console.Append(ConsoleOrigin.Notice, message, _time.GetUtcNow());

    private void UpdateStatus(Func<InstanceStatus, InstanceStatus> mutate)
    {
        InstanceStatus updated;
        lock (_statusGate)
        {
            updated = mutate(_status);
            _status = updated;
        }

        // Raised outside the lock: a handler that touches this supervisor would
        // otherwise deadlock against whatever is already holding it.
        StatusChanged?.Invoke(updated);
    }

    private async Task AwaitMonitorAsync()
    {
        var monitor = _monitor;
        if (monitor is null)
        {
            return;
        }

        try
        {
            await monitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the lifetime token cancelled the loop.
        }

        _monitor = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopRequested = true;
        _lifetime?.Cancel();

        await AwaitMonitorAsync().ConfigureAwait(false);

        if (_process is { } process)
        {
            process.OutputReceived -= OnOutput;
            if (!process.HasExited)
            {
                // Dispose is not a graceful stop and must not pretend to be one.
                // Callers wanting a clean shutdown call StopAsync first.
                process.Kill();
            }

            await process.DisposeAsync().ConfigureAwait(false);
            _process = null;
        }

        _lifetime?.Dispose();
        _gate.Dispose();
    }
}
