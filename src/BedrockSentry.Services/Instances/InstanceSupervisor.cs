using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Players;
using BedrockSentry.Core.Structures;
using BedrockSentry.Core.Sync;
using BedrockSentry.Services.Worlds;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// How a wait for an instance to report itself started ended (<c>T209</c>).
/// </summary>
/// <remarks>
/// Three states because there are three, and the type that had two was made to
/// lie: a caller wrote <em>"did not report itself started within the budget"</em>
/// on an outcome that had nothing to do with the budget.
/// </remarks>
public enum ReadyWait
{
    /// <summary>The server reported <c>Server started.</c> inside the budget.</summary>
    Ready,

    /// <summary>
    /// This instance is not trying to start any more — it was refused, stopped,
    /// disposed, or the watchdog gave up. Nothing is going to complete the wait,
    /// so waiting longer cannot help, and however much budget is left is beside
    /// the point.
    /// </summary>
    NotStarting,

    /// <summary>The budget ran out while the instance was still starting.</summary>
    BudgetExpired,
}

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
public sealed class InstanceSupervisor : IAsyncDisposable, IStructureServer
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
    private readonly Func<IBeforeLaunch?>? _beforeLaunch;

    private readonly Lock _statusGate = new();

    /// <summary>The job that alone may start and stop this server, under <see cref="_statusGate"/>.</summary>
    private InstanceClaim? _claim;

    private IBedrockProcess? _process;
    private Task? _monitor;
    private CancellationTokenSource? _lifetime;
    private TaskCompletionSource? _ready;

    /// <summary>
    /// Completed whenever <see cref="_ready"/> is replaced or cleared, so a
    /// waiter holding the previous attempt's source wakes and looks again.
    /// </summary>
    /// <remarks>
    /// `T201`. A crashed attempt's readiness source is never completed - the
    /// watchdog's relaunch installs a NEW one and that is the one
    /// <c>Server started.</c> completes - so a waiter that captured the old
    /// reference sat out its entire budget while the server it was waiting for
    /// was already serving. That waiter holds the install-wide start slot, so
    /// every other instance queued behind a wait for an event that could no
    /// longer happen: measured at 120.00 seconds, and the warning logged at the
    /// end of it said the server had not reported itself started 104 seconds
    /// after it had.
    ///
    /// A signal rather than a poll on purpose. Polling would wake the waiter on
    /// a timer, which under a test's controlled clock means waiting on an
    /// advance nobody makes; this wakes on the event itself, so the only timer
    /// left in <see cref="WaitForReadyAsync"/> is the budget it was given.
    /// </remarks>
    private TaskCompletionSource _readyReplaced =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile bool _stopRequested;

    /// <summary>
    /// How long a start waits for a predecessor of this wrapper to release the
    /// ports it is about to bind, and how often it looks (<c>D74</c>).
    /// </summary>
    /// <remarks>
    /// Fifteen seconds is above both measurements of the thing being waited on —
    /// 6.4 seconds past the SCM's <c>Stopped</c> in <c>T82</c>, 8.8 in
    /// <c>T167</c> — with room for a machine slower than either. It is a ceiling
    /// rather than a delay: the ordinary restart clears in well under a second
    /// and pays one poll interval for the privilege, and the wait is not entered
    /// at all unless something is actually holding a port.
    /// </remarks>
    public static readonly TimeSpan PredecessorWaitFor = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PredecessorWaitEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The ports a predecessor was still holding when this attempt launched, and
    /// empty from the moment the server reports itself started.
    /// </summary>
    /// <remarks>
    /// <para>Non-empty means the wait above expired with the port still held, so
    /// this attempt was launched into one — the case <c>D74</c> chose to allow
    /// rather than refuse. It is cleared on <c>Server started.</c>, which makes
    /// it a precise discriminator rather than a timing guess: a server that
    /// reported started bound its port, so anything that happens to it
    /// afterwards is unrelated to the hold and belongs on the ladder like any
    /// other crash.</para>
    ///
    /// <para>That is what lets the exit path keep <c>T200</c>'s spurious failure
    /// off the watchdog's count. At <strong>Give up after = 1</strong> — a value
    /// the panel offers — counting it would permanently fault the pair's owner on
    /// a routine service restart.</para>
    /// </remarks>
    private IReadOnlyList<int> _launchedOverHold = [];

    /// <summary>
    /// Whether the single uncounted retry above has already been spent on this
    /// run. Cleared when the server reports itself started, or when it is stopped.
    /// </summary>
    private bool _holdRetryUsed;

    /// Said once per launch, not on every line that carries the prefix.
    private bool _notedMissingLogFile;

    /// <summary>
    /// What this run's companion pack has said, null before its hello. Relayed
    /// deaths and positions are recorded only under <see cref="CompanionPack.Protocol"/>,
    /// since fields are read by position.
    /// </summary>
    private CompanionRun? _companionRun;

    /// <summary>
    /// Completed by the pack's <c>where-end</c> while a stop is asking where
    /// everybody is (<c>D35</c>); null the rest of the time, when a <c>where</c>
    /// line is ignored.
    /// </summary>
    private TaskCompletionSource? _positionsAsked;

    /// <summary>What the pack's probe command is: <c>scriptevent</c> reaches its handler with cheats off (<c>D93</c>'s spike).</summary>
    internal const string ProbeCommand = "scriptevent bedrocksentry:probe";

    /// <summary>The command asking the pack where every online player is (<c>D35</c>).</summary>
    internal const string WhereCommand = "scriptevent bedrocksentry:where";

    /// <summary>
    /// The longest a stop waits for the pack's positions, taken from the drain
    /// budget and never more than a quarter of it. The spike measured the answer
    /// within a millisecond of the command, and half a second for the first
    /// command after a start.
    /// </summary>
    public static readonly TimeSpan PositionExchangeLimit = TimeSpan.FromSeconds(2);

    /// <summary>What the companion pack has said on this run, null before its hello.</summary>
    public CompanionRun? Companion => _companionRun;

    /// <summary>
    /// One set of outlines at a time: the pack takes a group in parts, and two sets sent
    /// together would interleave them (<c>D106</c>).
    /// </summary>
    private readonly SemaphoreSlim _outlineGate = new(1, 1);

    /// <summary>
    /// The world's ticking areas as last read from the console, and when (<c>D107</c>); null
    /// until <see cref="Worlds.TickingAreas"/> reads them. The outlines it sends come from here.
    /// </summary>
    public TickingAreaRead? TickingAreas { get; set; }

    /// <summary>When this run launched: only sessions opened since can take a relayed line.</summary>
    private DateTimeOffset _runStartedUtc;
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
        Func<IOrphanInspector?>? orphans = null,
        Func<IBeforeLaunch?>? beforeLaunch = null)
    {
        _players = players;
        _versions = versions;
        _startPreflight = startPreflight;
        _orphans = orphans;
        _beforeLaunch = beforeLaunch;
        _instanceId = instanceId;
        _definition = definition;
        _workingDirectory = workingDirectory;
        _factory = factory;
        _time = timeProvider;
        _log = logger;
        _discovery = discoveryGuard;

        Console = new ConsoleBuffer(consoleCapacity);
        _status = InstanceStatus.Stopped(instanceId, definition.Name);
        UnexpectedExits = new ExitTally(timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Every exit this supervisor saw that no stop asked for, crash-loop retries included
    /// (<c>D48</c>).
    /// </summary>
    public ExitTally UnexpectedExits { get; }

    /// <summary>Recent console output, including the wrapper's own notices.</summary>
    public ConsoleBuffer Console { get; }

    public string InstanceId => _instanceId;

    string IStructureServer.Name => _definition.Name;

    bool IStructureServer.IsRunning => Status.State == InstanceState.Running;

    public InstanceDefinition Definition => _definition;

    /// <summary>Where this instance actually runs, which may be outside the instance root.</summary>
    public string WorkingDirectory => _workingDirectory;

    /// <summary>
    /// Whether a Bedrock server is actually installed here. Checked from disk
    /// each time rather than cached: an administrator can unpack one by hand at
    /// any moment, and a cached "no" would leave the UI insisting the instance
    /// is empty while the server sits in it.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> what the launch guard reads, although in an
    /// install the two always agree. This one is a question about the disk, asked
    /// by the panel so it can offer the Install button; the guard's is a question
    /// about whether <see cref="IBedrockProcessFactory"/> can produce a running
    /// server here, which a factory that launches nothing answers for itself.
    /// Making them share an answer would be <c>T151</c> clause 6 again: the same
    /// method, and the wrong answer for one of the two callers.
    /// </remarks>
    public bool IsProvisioned => Presence.CanLaunch;

    /// <summary>
    /// What the disk says about this instance's executable, in full (<c>T246</c>).
    /// </summary>
    /// <remarks>
    /// <see cref="IsProvisioned"/> collapses this to the one question its callers
    /// ask — may the Start button act — and a surface that RENDERS a reason needs
    /// the rest, because "there is no server here" and "there is one and I am not
    /// allowed to read it" are different sentences with different repairs.
    /// </remarks>
    public ServerPresence Presence => InstanceLayout.PresenceOf(_workingDirectory, _definition);

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
    /// Claims this server's starts and stops for one job, such as a sync or an
    /// update, until the claim is disposed (<see cref="InstanceClaim"/>).
    /// </summary>
    /// <param name="purpose">What the server is busy with, as <see cref="ClaimText"/> names it.</param>
    /// <exception cref="InstanceClaimedException">Another job claims it.</exception>
    /// <remarks>
    /// <para>Waits for a start or stop already under way, and is set while the gate
    /// they take is held. A start or stop that takes the gate afterwards finds the
    /// claim, so nothing lands between the holder's own steps.</para>
    ///
    /// <para>Refused at once when another job holds a claim, rather than after the
    /// gate: a claim can last minutes, and whatever waits for it decides for itself
    /// whether to wait (<see cref="RestartScheduler"/>) or say so.</para>
    /// </remarks>
    public async Task<InstanceClaim> ClaimAsync(string purpose, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        RefuseUnlessHolder(holder: null);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var claim = new InstanceClaim(this, purpose);
            InstanceStatus updated;

            lock (_statusGate)
            {
                if (_claim is { } held)
                {
                    throw new InstanceClaimedException(_definition.Name, held.Purpose);
                }

                _claim = claim;
                updated = _status = _status with { ClaimedFor = purpose };
            }

            StatusChanged?.Invoke(updated);
            return claim;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends <paramref name="claim"/>, if it is still the one held.</summary>
    internal void Release(InstanceClaim claim)
    {
        InstanceStatus updated;

        lock (_statusGate)
        {
            if (!ReferenceEquals(_claim, claim))
            {
                return;
            }

            _claim = null;
            updated = _status = _status with { ClaimedFor = null };
        }

        StatusChanged?.Invoke(updated);
    }

    /// <summary>Throws when a job other than <paramref name="holder"/> claims this server.</summary>
    private void RefuseUnlessHolder(InstanceClaim? holder)
    {
        InstanceClaim? claim;

        lock (_statusGate)
        {
            claim = _claim;
        }

        if (claim is not null && !ReferenceEquals(claim, holder))
        {
            throw new InstanceClaimedException(_definition.Name, claim.Purpose);
        }
    }

    /// <summary>
    /// Launches the instance and begins supervising it. Returns once the process
    /// has been launched, not once the server is ready — those are 1.6 seconds
    /// apart on a warm world, and conflating them makes the status page lie.
    /// Use <see cref="WaitForReadyAsync"/> when readiness is what you need.
    /// </summary>
    /// <exception cref="InstanceClaimedException">A job claims this server; it starts it itself.</exception>
    public Task StartAsync(CancellationToken cancellationToken = default) =>
        StartAsync(holder: null, cancellationToken);

    /// <summary>Set while <see cref="RecheckStartAsync"/> runs (<c>T441</c>).</summary>
    private volatile bool _quietRepeatedRefusal;

    /// <summary>
    /// A start from the boot re-check of a refused instance, fast or slow (<c>T441</c>): the same as
    /// <see cref="StartAsync(CancellationToken)"/>, except a refusal that repeats the
    /// reason already shown writes no console line.
    /// </summary>
    internal async Task RecheckStartAsync(CancellationToken cancellationToken)
    {
        _quietRepeatedRefusal = true;
        try
        {
            await StartAsync(holder: null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _quietRepeatedRefusal = false;
        }
    }

    /// <summary>A start by <paramref name="holder"/>, or by anyone while nothing claims the server.</summary>
    internal async Task StartAsync(InstanceClaim? holder, CancellationToken cancellationToken)
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
            RefuseUnlessHolder(holder);

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
                    // nothing to wait on before the start slot can go. The
                    // status and the console already say why (T149).
                    if (hold is not null)
                    {
                        hold.NothingStarted();
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
                _ = ReleaseStartSlotWhenReadyAsync(hold);
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
    private async Task<LanDiscoveryPortGuard.IStartSlot?> AcquireDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (_discovery is null)
        {
            return null;
        }

        var owns = _discovery.OwnsDiscoveryPair(PortInspector.ModelOf(_workingDirectory));
        return await _discovery.HoldAsync(_instanceId, owns, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lets the install-wide start slot go once BDS is past its own bind, or once
    /// the budget runs out. Nothing here releases the discovery pair — disposing
    /// the hold never does, in any arm; see
    /// <see cref="LanDiscoveryPortGuard.HoldAsync"/>.
    /// </summary>
    /// <remarks>
    /// The pair is protected by the <em>wait</em> rather than by the release. BDS
    /// binds discovery after it reports started, so the slot has to be held past
    /// that line or the next start races the bind.
    /// Detached from <see cref="StartAsync"/> on purpose: that call returns once
    /// the process has launched, not once the server is ready, and awaiting the
    /// release here would quietly change what starting an instance means to every
    /// caller. The release itself is unconditional — ready, timeout, cancellation
    /// and a throw all arrive at the same finally. A leaked slot stops every
    /// instance from starting until the service restarts, which locks the owning
    /// instance out of its own port too, since only its own start frees the pair.
    /// </remarks>
    private async Task ReleaseStartSlotWhenReadyAsync(LanDiscoveryPortGuard.IStartSlot hold)
    {
        try
        {
            // Three outcomes, three sentences (`T209`). This said the budget had
            // run out whatever came back, and wrote that at Warning into the
            // operator's event log on every ordinary start-then-stop.
            //
            // AND BOTH REPLACEMENTS CLAIMED THE WRONG EFFECT (`T223`). They said
            // the discovery ports were being released; disposing this hold releases
            // the install-wide START SLOT. For a non-owner -- which is exactly the
            // instance that takes this path -- the sockets are a STANDING hold that
            // only the owner's own start gives up, so nothing about the pair
            // changes here. An administrator reading "releasing the discovery
            // ports" concludes the pair is free and looks somewhere else.
            //
            // `T209`'s own class inside `T209`'s fix: that entry exists because a
            // message asserted a cause it had not established, and its replacement
            // asserted an effect that does not happen.
            //
            // THE FIX FOR THAT THEN CARRIED IT. Two of the sentences were
            // corrected and two more were not: the catch arm below, and this
            // method's own name and summary. The catch arm is a Warning, so of the
            // three sentences this path can log it is one of only two that clear
            // the event sink -- the surviving copy was the readable one. The claim
            // began upstream, in `HoldAsync`'s summary, which promised that
            // disposing released the pair as well; correcting the callers without
            // correcting that line leaves the next caller to write it again.
            switch (await WaitForReadyOutcomeAsync(LanDiscoveryPortGuard.DefaultHoldTimeout)
                .ConfigureAwait(false))
            {
                case ReadyWait.BudgetExpired:
                    _log.LogWarning(
                        "{InstanceId} did not report itself started within the LAN discovery hold "
                        + "budget; releasing the start slot anyway", _instanceId);
                    break;

                case ReadyWait.NotStarting:
                    // No server is about to bind the pair, so the slot goes back
                    // without the discovery margin.
                    hold.NothingStarted();

                    // Not a warning. A stop, a refusal and a watchdog give-up are
                    // all things somebody or something decided, and the slot is
                    // released early rather than late.
                    _log.LogInformation(
                        "{InstanceId} stopped starting before it reported itself started; "
                        + "releasing the start slot", _instanceId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex, "Waiting on {InstanceId} before releasing the start slot failed",
                _instanceId);
        }
        finally
        {
            await hold.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for BDS to report <c>Server started.</c>, across relaunches and
    /// inside one budget. False on timeout — which is not the same as a failure,
    /// and the caller decides which it is.
    /// </summary>
    /// <remarks>
    /// <para><strong>Across relaunches is the whole of `T201`.</strong> This used
    /// to capture <see cref="_ready"/> once and wait on that one source. A start
    /// that crashes never completes its source; the watchdog's retry installs a
    /// new one, and the new one is what <c>Server started.</c> completes. So the
    /// waiter outlived the thing it was waiting for and ran to its deadline with
    /// the server up — while holding the start slot every other instance needs.
    /// The question a caller is asking is whether <em>this instance</em> is
    /// serving, not whether one particular process is, so the wait follows the
    /// instance.</para>
    ///
    /// <para>A null source still answers false immediately, and that is
    /// load-bearing rather than incidental: <see cref="Refuse"/> nulls it so a
    /// refused start cannot read as ready, which is the answer the update
    /// installer's health gate must never get wrong because it deletes the
    /// rollback build on the strength of it.</para>
    /// </remarks>
    public async Task<bool> WaitForReadyAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default) =>
        await WaitForReadyOutcomeAsync(timeout, cancellationToken).ConfigureAwait(false)
            is ReadyWait.Ready;

    /// <summary>
    /// The same wait, saying <em>which</em> of the three things happened
    /// (<c>T209</c>).
    /// </summary>
    /// <remarks>
    /// <para><strong>A <c>bool</c> here had two states for three world states, and
    /// a caller wrote a sentence naming the wrong one.</strong> The hold-release
    /// path logged <em>"did not report itself started within the LAN discovery hold
    /// budget"</em> on every false — including with 79.6 seconds of a 120-second
    /// budget still to run, because supervision had ended and the readiness source
    /// was cleared. `T176`'s thesis, in the method `T201`'s own fix had just
    /// taught to follow state changes.</para>
    ///
    /// <para>Wider than the crash case, which is why it is not cosmetic:
    /// <see cref="StopAsync"/> cancels the lifetime, so an ordinary start-then-stop
    /// before the server reports started writes the same sentence at
    /// <c>Warning</c> into the operator's event log.</para>
    ///
    /// <para><see cref="WaitForReadyAsync"/> stays as the answer to <em>is it
    /// ready</em>, which is all the health gate and the tests ever asked — and it
    /// is one expression over this, so the two cannot disagree.</para>
    /// </remarks>
    public async Task<ReadyWait> WaitForReadyOutcomeAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = _time.GetUtcNow() + timeout;

        while (true)
        {
            // The signal first, then the source. Read the other way round, a
            // replacement landing between the two reads would leave this holding
            // the old source and the NEW signal - waiting on an event that has
            // already happened, which is the defect again with an extra step.
            var replaced = _readyReplaced.Task;
            var ready = _ready;

            if (ready is null)
            {
                return ReadyWait.NotStarting;
            }

            if (ready.Task.IsCompleted)
            {
                return ReadyWait.Ready;
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return ReadyWait.BudgetExpired;
            }

            var delay = Task.Delay(remaining, _time, cancellationToken);
            var first = await Task.WhenAny(ready.Task, replaced, delay).ConfigureAwait(false);

            if (first == ready.Task)
            {
                return ReadyWait.Ready;
            }

            if (first == delay)
            {
                return ReadyWait.BudgetExpired;
            }

            // The attempt being waited on ended and another began. Round again
            // on the new one, inside the same budget.
        }
    }

    /// <summary>
    /// Warns players, stops the server, and waits for it to go. Kills it only if
    /// the drain budget runs out.
    ///
    /// This is the single graceful-shutdown path: scheduled restarts, service
    /// stop, BDS update and wrapper self-update all arrive here with a different
    /// <see cref="GracefulStopRequest"/> rather than a different implementation.
    /// </summary>
    /// <exception cref="InstanceClaimedException">
    /// A job claims this server and the stop is not the wrapper's own
    /// (<see cref="StopReason.ServiceStop"/>, <see cref="StopReason.WrapperUpdate"/>).
    /// </exception>
    public Task StopAsync(GracefulStopRequest request, CancellationToken cancellationToken = default) =>
        StopAsync(holder: null, request, cancellationToken);

    /// <summary>A stop by <paramref name="holder"/>, or by anyone while nothing claims the server.</summary>
    internal Task StopAsync(
        InstanceClaim? holder, GracefulStopRequest request, CancellationToken cancellationToken) =>
        StopAsync(holder, request, cancellationToken, countdown: null);

    /// <returns>False when <paramref name="countdown"/> was called off before the stop began.</returns>
    /// <remarks>
    /// <paramref name="cancellationToken"/> means "stop waiting" and falls through to the
    /// stop, as <see cref="ManagedServerShutdown.DrainAsync"/>'s does; calling off is the
    /// other ending, and the server stays up with its players told (<c>T425</c>).
    /// </remarks>
    private async Task<bool> StopAsync(
        InstanceClaim? holder, GracefulStopRequest request, CancellationToken cancellationToken,
        DrainCountdown? countdown)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The wrapper going away stops every server, claimed or not: Windows
            // waits for nobody (docs/design.md § 5.2).
            if (request.Reason is not (StopReason.ServiceStop or StopReason.WrapperUpdate))
            {
                RefuseUnlessHolder(holder);
            }

            _stopRequested = true;

            var process = _process;
            if (process is null || process.HasExited)
            {
                _lifetime?.Cancel();
                await AwaitMonitorAsync().ConfigureAwait(false);
                UpdateStatus(s => s with { State = InstanceState.Stopped, ProcessId = null, NextRetryUtc = null });
                return true;
            }

            UpdateStatus(s => s with { State = InstanceState.Stopping, NextRetryUtc = null });

            if (countdown is null)
            {
                await WarnAndWaitAsync(request, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, countdown.Token))
                {
                    try
                    {
                        await WarnAndWaitAsync(request, waiting.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Called off; decided below, under the countdown's own lock.
                    }
                }

                if (!countdown.TryBeginStopping())
                {
                    _stopRequested = false;
                    Note("the countdown was called off; the server stays up");
                    await SendInternalAsync(CountdownMessage.CalledOff(), CancellationToken.None).ConfigureAwait(false);
                    UpdateStatus(s => s with { State = InstanceState.Running });
                    return false;
                }
            }

            // D35, inside the drain budget and never after it: the budget starts
            // here and asking the pack where everybody is spends part of it.
            var drainEnds = _time.GetUtcNow() + request.DrainBudget;
            await AskWhereEveryoneIsAsync(request.DrainBudget, cancellationToken).ConfigureAwait(false);

            Note($"stopping ({request.Reason})");
            await SendInternalAsync("stop", cancellationToken).ConfigureAwait(false);

            var exit = process.WaitForExitAsync(CancellationToken.None);
            var left = drainEnds - _time.GetUtcNow();
            var budget = Task.Delay(left > TimeSpan.Zero ? left : TimeSpan.Zero, _time, cancellationToken);
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
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Asks the companion pack where every online player is and records each
    /// answer as that session's exit position (<c>D35</c>), before the stop that
    /// would end the sessions without one.
    /// </summary>
    /// <remarks>
    /// Only when this run's pack speaks this build's protocol, has answered the
    /// probe, and the instance keeps last positions. A pack that does not answer
    /// costs at most <see cref="PositionExchangeLimit"/>, or a quarter of the
    /// drain budget if that is less, out of the budget the stop already had.
    /// </remarks>
    private async Task AskWhereEveryoneIsAsync(TimeSpan drainBudget, CancellationToken cancellationToken)
    {
        if (_companionRun is not { Readable: true, ProbeAnswered: true }
            || !_definition.Companion.RecordLastLocation)
        {
            return;
        }

        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _positionsAsked, answered);

        try
        {
            var limit = drainBudget / 4 < PositionExchangeLimit ? drainBudget / 4 : PositionExchangeLimit;
            var timeout = Task.Delay(limit, _time, cancellationToken);

            await SendInternalAsync(WhereCommand, cancellationToken).ConfigureAwait(false);

            if (await Task.WhenAny(answered.Task, timeout).ConfigureAwait(false) != answered.Task)
            {
                Note($"the companion pack did not say where everyone is within {limit.TotalSeconds:0.#}s; "
                    + "stopping without their positions");
            }
        }
        finally
        {
            Volatile.Write(ref _positionsAsked, null);
        }
    }

    /// <param name="whileStopped">
    /// Runs between the stop and the start, while the world files are static —
    /// backup-on-shutdown lives here (<c>docs/design.md</c> § 8.1), and the update
    /// flows will use the same seam. A throw from it still starts the server:
    /// whatever went wrong in the gap, a server left down is not the answer.
    /// </param>
    /// <remarks>
    /// Claimed for its length (<see cref="ClaimText.Restart"/>), so no other job's
    /// start or stop lands between its own.
    /// </remarks>
    /// <exception cref="InstanceClaimedException">Another job claims this server.</exception>
    public async Task RestartAsync(
        GracefulStopRequest request,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? whileStopped = null)
    {
        using var claim = await ClaimAsync(ClaimText.Restart, cancellationToken).ConfigureAwait(false);
        await RestartAsync(claim, request, cancellationToken, whileStopped).ConfigureAwait(false);
    }

    /// <summary>A restart by the job holding <paramref name="holder"/>.</summary>
    /// <param name="countdown">Calls the restart off until its stop begins; the server then stays up.</param>
    internal async Task RestartAsync(
        InstanceClaim holder,
        GracefulStopRequest request,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? whileStopped,
        DrainCountdown? countdown = null)
    {
        if (!await StopAsync(holder, request, cancellationToken, countdown).ConfigureAwait(false))
        {
            return;
        }

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

        await StartAsync(holder, cancellationToken).ConfigureAwait(false);
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
            NoteThenPublish(
                "stop typed at the console; the watchdog will not restart this instance",
                s => s with { State = InstanceState.Stopping, NextRetryUtc = null });
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
    /// Installs the readiness source for a new attempt, or clears it, and wakes
    /// anybody waiting on the attempt that just ended.
    /// </summary>
    /// <remarks>
    /// Both writes go through here so they cannot drift (`T201`). A waiter woken
    /// by the signal re-reads the pair and carries on against the new attempt, or
    /// answers false if there is no longer one - which is what a refusal means
    /// and is why <see cref="Refuse"/> uses this rather than assigning directly.
    /// </remarks>
    private void SetReady(TaskCompletionSource? next)
    {
        _ready = next;

        // The fresh signal goes in before the old one is completed, so a waiter
        // that wakes on it never reads a completed signal beside a stale source.
        var waking = _readyReplaced;
        _readyReplaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waking.TrySetResult();
    }

    /// <summary>
    /// Faults the instance with <paramref name="reason"/> and launches nothing.
    /// </summary>
    /// <remarks>
    /// Shared by both refusals — a held port and an orphan on the world — so the
    /// state they leave behind cannot drift apart. Clearing the readiness source
    /// is the load-bearing line: it is replaced further down, *after* this
    /// returns, so without it a refusal leaves the previous run's completed task
    /// in place and <c>WaitForReadyAsync</c> answers true for a server that was
    /// never launched — the one answer the update installer's health gate must
    /// never get, since it deletes the rollback build on the strength of it.
    /// Through <see cref="SetReady"/> rather than by assignment, so anybody
    /// waiting on the attempt this refusal replaces is told rather than left to
    /// time out (`T201`).
    /// </remarks>
    private LaunchOutcome Refuse(string reason)
    {
        _process = null;
        SetReady(null);

        // `T441`. On the boot re-check, a refusal repeating the one this instance
        // already shows says nothing new, which keeps it quiet against a port held
        // for good. A person's Start still gets its ALERT. Only the console and the log are spared: the state is set
        // again below either way.
        var repeat = _quietRepeatedRefusal
                     && Status is { State: InstanceState.Faulted, FaultKind: InstanceFault.RefusedToStart }
                     && string.Equals(Status.FaultReason, reason, StringComparison.Ordinal);

        if (repeat)
        {
            UpdateStatus(s => s with
            {
                State = InstanceState.Faulted,
                ProcessId = null,
                ReadyUtc = null,
                NextRetryUtc = null,
                FaultReason = reason,
                FaultKind = InstanceFault.RefusedToStart,
            });
            _log.LogDebug("Instance {InstanceId} still {Reason}", _instanceId, reason);
            return LaunchOutcome.Refused;
        }

        // Loud for the same reason the watchdog's give-up is: a server that is
        // quietly not running is worse than one that says why.
        NoteThenPublish($"ALERT: {reason}", s => s with
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
    /// <summary>
    /// Waits, briefly, for a predecessor of this wrapper to let go of the ports
    /// this instance is about to bind. Returns whatever it is still holding when
    /// the wait ends — empty in the ordinary case, which is also what a wait that
    /// worked returns.
    /// </summary>
    /// <remarks>
    /// <para><c>D74</c>, and the third answer <c>T200</c> showed was missing. The
    /// pre-flight is right not to refuse over a predecessor's hold: it is the same
    /// reservation this instance is about to inherit, and refusing would fault the
    /// pair's owner on every service restart. But declining to refuse was the only
    /// alternative to refusing, so the launch went ahead into a port that was
    /// genuinely still held — and BDS opens the world <em>before</em> it binds, so
    /// what that costs is an unclean close on a live world, roughly sixteen
    /// seconds of outage, and a spurious failure on the watchdog ladder.</para>
    ///
    /// <para><strong>Bounded by construction rather than by choice.</strong> The
    /// thing being waited on is another process finishing its exit, and that has
    /// been measured twice: 6.4 seconds past the SCM's <c>Stopped</c> in
    /// <c>T82</c>, 8.8 in <c>T167</c>. The ceiling is set above both with room to
    /// spare, and the start proceeds at expiry rather than refusing, because a
    /// predecessor that is unusually slow is still not a reason to leave a
    /// server down.</para>
    ///
    /// <para>The shape is <c>LanDiscoveryPortGuard</c>'s own bounded retry one
    /// layer up: same reason, same kind of thing waited on, same refusal to wait
    /// forever.</para>
    /// </remarks>
    private async Task<IReadOnlyList<int>> WaitOutPredecessorAsync(
        IStartPreflight preflight, CancellationToken cancellationToken)
    {
        IReadOnlyList<int> held;

        try
        {
            held = preflight.PredecessorHolds(_workingDirectory);
        }
        catch (Exception ex)
        {
            // Same posture as the conflict check above: not being able to ask is
            // not a reason to hold up a start that would otherwise work.
            _log.LogWarning(
                ex, "Could not check whether an outgoing {InstanceId} host still holds its ports",
                _instanceId);
            return [];
        }

        if (held.Count == 0)
        {
            return held;
        }

        var deadline = _time.GetUtcNow() + PredecessorWaitFor;

        Note($"waiting up to {PredecessorWaitFor.TotalSeconds:0}s for the outgoing wrapper to "
             + $"release {PortConflictText.PortList(held)}");
        _log.LogInformation(
            "Instance {InstanceId} is waiting for a predecessor host to release {Ports}",
            _instanceId, string.Join(", ", held));

        while (_time.GetUtcNow() < deadline)
        {
            await Task.Delay(PredecessorWaitEvery, _time, cancellationToken).ConfigureAwait(false);

            try
            {
                held = preflight.PredecessorHolds(_workingDirectory);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex, "Could not re-check {InstanceId}'s ports while waiting; launching anyway",
                    _instanceId);
                return [];
            }

            if (held.Count == 0)
            {
                return held;
            }
        }

        // Expired, and the start goes ahead anyway. What it must not do is carry
        // on as though nothing happened: the caller records what was still held,
        // so an exit before this attempt ever reported started can say why and
        // can stay off the watchdog ladder.
        _log.LogWarning(
            "A predecessor host still holds {Ports} for {InstanceId} after {Waited}s; starting anyway",
            string.Join(", ", held), _instanceId, PredecessorWaitFor.TotalSeconds);

        return held;
    }

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
                found = await orphans.FindAsync(InstanceId, _workingDirectory, Ours(), cancellationToken)
                    .ConfigureAwait(false);
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

        // After the orphan question and before the port one, which is the order
        // the sentences are worth reading in. An orphan can be on this world with
        // the executable since deleted, and that is the more urgent thing to say;
        // a held port, by contrast, does not matter at all when there is nothing
        // here to bind it.
        //
        // A refusal rather than a failed launch, because the two are different
        // events and the watchdog's accounting is built for the other one
        // (`T245`). Launching anyway threw, counted as a crash, and gave up three
        // attempts later under "no further restarts will be attempted" — a
        // sentence about a crash loop, on a server that had never started. This
        // states what is missing, stays off the ladder, and is re-checked on
        // `RefusedStartRetry.Schedule` like the other two refusals.
        var presence = _factory.PresenceOf(_definition, _workingDirectory);

        if (!presence.CanLaunch)
        {
            return Refuse(MissingServerText.RefusalReason(
                _workingDirectory, _definition.Executable, presence));
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

            // WHAT THIS WAIT COSTS, said here rather than left to be rediscovered.
            // On the StartAsync path the install-wide discovery start slot is
            // already held when this runs, so a wait delays every other
            // instance's start by as long as it lasts. That is a real cost and it
            // is still the better trade: the ordinary handover clears in one poll
            // interval, the ceiling is fifteen seconds, and what it replaces is a
            // crash on a live world followed by a watchdog backoff and a slot held
            // for the whole readiness budget. The watchdog's own retries call
            // LaunchAsync directly and take no slot, so they pay nobody else.
            _launchedOverHold = await WaitOutPredecessorAsync(preflight, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_beforeLaunch?.Invoke() is { } before)
        {
            // Under the start gate and with no process running, which is the
            // condition IBeforeLaunch promises. It must not start or stop this
            // supervisor: that would wait on the gate this launch holds.
            try
            {
                await before.BeforeLaunchAsync(this, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Work before launching {InstanceId} failed; launching anyway", _instanceId);
            }
        }

        var process = _factory.Create(_instanceId, _definition, _workingDirectory);
        process.OutputReceived += OnOutput;

        _notedMissingLogFile = false;
        _companionRun = null;
        SetReady(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var startedUtc = _time.GetUtcNow();
        _runStartedUtc = startedUtc;

        try
        {
            await process.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ServerNotLaunchableException ex)
        {
            // `T250`. The executable check answers a question about the
            // installation, and no relaunch changes any of its answers — so this is
            // the fourth refusal rather than a fourth way to fail, and it goes to
            // the same place as the other three.
            //
            // It exists at all because the guard above cannot reach the state this
            // one does: PresenceOf reads the parent directory's index, which a Deny
            // ACE on the file does not change, so it calls a denied executable
            // Installed and is right to. Opening the file is the only check that
            // separates them, and it can only be done here (`T246`). Measured
            // before this: 0 launch lines beside 3 "failed to launch:", then "exited
            // unexpectedly after 0s" and a permanent give-up, about a process that
            // was never started.
            //
            // The state travels on the exception rather than being read off it.
            // Deciding from the framework exception type would be composing a second
            // rule about what a denied file looks like, which is the defect this
            // whole line of work is made of — and a FileNotFoundException out of a
            // process start can mean a missing dependency rather than a missing
            // server, so the type does not carry the answer anyway.
            //
            // No second log line: Refuse logs the sentence, and for the one state
            // that has a Windows message to add, RefusalReason already composes it
            // in.
            process.OutputReceived -= OnOutput;
            await process.DisposeAsync().ConfigureAwait(false);

            return Refuse(MissingServerText.RefusalReason(
                _workingDirectory, _definition.Executable, ex.Presence));
        }
        catch (ServerFolderNotGrantedException ex)
        {
            // Milestone 18.5: the folder could not be given to the server's
            // folder identity, or lacks its low label (D124, D126). A relaunch would meet the same list, so it is a
            // refusal for the same reason as the one above, not a crash.
            process.OutputReceived -= OnOutput;
            await process.DisposeAsync().ConfigureAwait(false);

            return Refuse(ex.Message);
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

    /// <summary>
    /// Supervises this instance until it stops, gives up or is cancelled, and
    /// clears the readiness source on the way out whichever of those it was.
    /// </summary>
    /// <remarks>
    /// <para><strong>The clearing is the second half of `T201` and it is here
    /// rather than at each exit.</strong> The first half taught
    /// <see cref="WaitForReadyAsync"/> to follow the instance across relaunches;
    /// this is what happens when there is no next relaunch. A watchdog give-up
    /// ends them, and the source the last crashed attempt installed is never
    /// completed by anything — so a caller sits inside its whole budget waiting
    /// for a server that can no longer report started, <em>while holding the
    /// install-wide start slot</em>. Measured at 79.8 seconds past the give-up,
    /// with two healthy servers down behind it and released only when the LAN
    /// discovery hold timed out at 120s.</para>
    ///
    /// <para>A give-up is only the exit that was measured. The loop also returns
    /// on a stop, on a stop noticed after a backoff, on a refused relaunch, on a
    /// cancelled wait and on the token between iterations — six of them, every one
    /// ending supervision, none completing the source. Clearing it at each return
    /// is the arrangement where the seventh is the one somebody forgets, so it
    /// goes in a <c>finally</c> and the returns stay as they are.</para>
    ///
    /// <para><see cref="SetReady"/> rather than an assignment, so a waiter is
    /// woken to re-read rather than left to its deadline — the same reason
    /// <see cref="Refuse"/> uses it, which is why the refusal path was the one
    /// arm of this already covered.</para>
    /// </remarks>
    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SuperviseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SetReady(null);
        }
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
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
                _launchedOverHold = [];
                _holdRetryUsed = false;

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

            // D48. Past the stop just above, so every exit from here on is one nobody
            // asked for, the uncounted handover retry below included: it is still a
            // server that went down.
            UnexpectedExits.Add(exitedUtc);

            var watchdog = _definition.Watchdog;

            // `T200`/`D74`. This attempt was launched into a port a predecessor of
            // this wrapper had still not released when the wait ran out, and it
            // never got as far as reporting itself started - so what killed it is
            // almost certainly the bind, and BDS opens the world before it gets
            // there. It is this wrapper's own handover rather than a fault of the
            // server, so it is retried without being counted.
            //
            // NOT COUNTING IT IS THE POINT, not a nicety. At Give up after = 1, a
            // value the panel offers, one spurious failure permanently faults the
            // pair's owner on an ordinary service restart. And the operator-facing
            // line names the port, which is the whole of what `T151`'s opening
            // paragraph exists to prevent going unsaid: what this looked like was
            // "exited unexpectedly (code -1073741819)".
            // ONE FREE PASS, NOT A LOOP. `_holdRetryUsed` is what bounds this,
            // and leaving it out was a hole rather than a simplification: a
            // holder that never goes away - a second install's host, which
            // matches by name and holds 19132 for as long as it runs (`D49`,
            // `D50`) - would otherwise retry uncounted forever, and the watchdog
            // could never give up and say so. A handover is one event. A second
            // crash over the same hold is not one, so the ladder takes it.
            if (_launchedOverHold.Count > 0 && !_holdRetryUsed)
            {
                var held = PortConflictText.PortList(_launchedOverHold);
                var heldVerb = _launchedOverHold.Count == 1 ? "was" : "were";
                var retryIn = watchdog.BackoffFor(1);

                _launchedOverHold = [];
                _holdRetryUsed = true;

                NoteThenPublish(
                    $"exited after {ranFor.TotalSeconds:0.#}s without reporting started, and "
                    + $"{held} {heldVerb} still held by the outgoing wrapper when it launched. "
                    + $"Not counted against the watchdog; retrying in {retryIn.TotalSeconds:0.#}s",
                    s => s with
                    {
                        State = InstanceState.Backoff,
                        ProcessId = null,
                        LastExitCode = exitCode,
                        LastExitUtc = exitedUtc,
                        NextRetryUtc = exitedUtc + retryIn,
                    });
                _log.LogWarning(
                    "Instance {InstanceId} exited (code {ExitCode}) after launching over a "
                    + "predecessor's hold on {Ports}; retrying in {Backoff} without counting it",
                    _instanceId, exitCode, held, retryIn);

                try
                {
                    await Task.Delay(retryIn, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                // The same three steps the counted path below takes, rather than
                // looping round: a `continue` here re-enters the loop with no
                // process to wait on and falls straight back into this accounting
                // with a null exit code, which is a spin rather than a retry.
                if (_stopRequested)
                {
                    UpdateStatus(s => s with { State = InstanceState.Stopped, NextRetryUtc = null });
                    return;
                }

                if (await LaunchAsync(cancellationToken).ConfigureAwait(false) == LaunchOutcome.Refused)
                {
                    return;
                }

                continue;
            }

            // A hold this attempt launched over that is NOT getting the free pass:
            // cleared here so it cannot be spent by some later crash, and the
            // exit falls through to the ordinary accounting below.
            _launchedOverHold = [];

            // D17: only a run that lasted clears the counter. Resetting on any
            // successful launch would let a server that dies ten seconds in retry
            // forever, which is precisely the loop D15 exists to bound.
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

                // Loud on purpose. A watchdog that gives up silently converts a
                // crash into an outage nobody is told about.
                NoteThenPublish($"ALERT: {reason}", s => s with
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
                _log.LogError(
                    "Instance {InstanceId} {Reason}; no further restarts will be attempted",
                    _instanceId, reason);
                return;
            }

            var backoff = watchdog.BackoffFor(_consecutiveFailures);
            NoteThenPublish(
                $"exited unexpectedly after {ranFor.TotalSeconds:0.#}s "
                + $"({WatchdogLadder.FailureCount(_consecutiveFailures, watchdog)}); "
                + $"retrying in {backoff.TotalSeconds:0.#}s",
                s => s with
                {
                    State = InstanceState.Backoff,
                    ProcessId = null,
                    LastExitCode = exitCode,
                    LastExitUtc = exitedUtc,
                    ConsecutiveFailures = _consecutiveFailures,
                    NextRetryUtc = exitedUtc + backoff,
                });
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

        var recorded = Console.Append(
            origin, line, _time.GetUtcNow(), echoPositions: _definition.Companion.EchoPositions);

        // A chat line is a player's own words, so nothing below may read it,
        // whatever its message says: a player called "Stopping server" must not
        // switch the watchdog off, one called "Version" must not record a build,
        // and a message quoting a connect line must not open a session.
        if (recorded.Origin == ConsoleOrigin.Chat)
        {
            return;
        }

        if (recorded.Relay is { } relay)
        {
            RecordRelay(relay, recorded.ReceivedUtc);
            return;
        }

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

            // It bound its ports, so whatever a predecessor was holding at launch
            // is no longer this run's story (`T200`), and the free retry is
            // available again for the next handover.
            _launchedOverHold = [];
            _holdRetryUsed = false;

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
            NoteThenPublish(
                "the server reported it is shutting down; the watchdog will not restart it",
                s => s with { State = InstanceState.Stopping, NextRetryUtc = null });
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

    /// <summary>
    /// Records what the companion pack relayed (milestone 12 step 7): its hello as
    /// the start of counting, a death, and where a player was as they left.
    /// </summary>
    /// <remarks>
    /// Through the session write queue, so a death or a leave lands after the
    /// connect that opened its session and before an exit closes it. Each write is
    /// behind its own <see cref="CompanionSettings"/> toggle, read as the line
    /// arrives. The hello is recorded only while deaths are, because the Players
    /// pages read the earliest one as the day counting began.
    /// </remarks>
    private void RecordRelay(CompanionRelay relay, DateTimeOffset atUtc)
    {
        var companion = _definition.Companion;

        switch (relay)
        {
            case HelloRelay hello:
                var run = new CompanionRun(hello.Protocol, hello.PackVersion, atUtc, ProbeAnswered: false);
                _companionRun = run;

                if (!run.Readable)
                {
                    Note($"the companion pack speaks protocol {run.Protocol} and this build reads "
                        + $"{CompanionPack.Protocol}, so its deaths and positions are not recorded");
                    return;
                }

                if (companion.RecordDeaths)
                {
                    QueuePlayerWrite(
                        store => store.RecordCompanionReportAsync(_instanceId, hello.PackVersion, atUtc), "hello");
                }

                // Once per hello, off the console pump: a write to the server's
                // input must never hold up reading its output.
                _ = Task.Run(SendProbeAsync);

                // The pack keeps outlines in memory only, so each run is handed them again (D106).
                _ = Task.Run(() => SendOutlinesAsync(CancellationToken.None));
                RaiseCompanionHello();
                return;

            case ProbeRelay:
                if (_companionRun is { } current)
                {
                    _companionRun = current with { ProbeAnswered = true };
                }

                return;
        }

        if (_companionRun is not { Readable: true })
        {
            return;
        }

        var runStarted = _runStartedUtc;

        switch (relay)
        {
            case DeathRelay { Death: var death } when companion.RecordDeaths:
                QueuePlayerWrite(
                    async store => LogAttribution(
                        "death", death.Gamertag,
                        await store.RecordDeathAsync(_instanceId, runStarted, death, atUtc).ConfigureAwait(false)),
                    "death");
                break;

            case LeaveRelay leave when companion.RecordLastLocation:
                QueuePlayerWrite(
                    async store => LogAttribution(
                        "leave position", leave.Gamertag,
                        await store.RecordExitAsync(
                            _instanceId, runStarted, leave.Gamertag, leave.Position,
                            ExitPositionSource.Leave, atUtc).ConfigureAwait(false)),
                    "leave position");
                break;

            // Only while a stop is asking: a where typed at the console at any
            // other moment would put a position on a session that is not ending.
            case WhereRelay where when Volatile.Read(ref _positionsAsked) is not null:
                QueuePlayerWrite(
                    async store => LogAttribution(
                        "position before a stop", where.Gamertag,
                        await store.RecordExitAsync(
                            _instanceId, runStarted, where.Gamertag, where.Position,
                            ExitPositionSource.Where, atUtc).ConfigureAwait(false)),
                    "position before a stop");
                break;

            case WhereEndRelay:
                Volatile.Read(ref _positionsAsked)?.TrySetResult();
                break;

            case AreaRelay area:
                RaiseAreaCommand(area.Command);
                break;

            case TickShowRelay tick:
                RaiseTickShow(tick.Command);
                break;

            case RestoreRelay restore:
                RaiseRestoreCommand(restore.Command);
                break;
        }
    }

    /// <summary>
    /// A <c>syncarea</c> an operator typed in game or on the console (<c>D106</c>). Raised on
    /// the console pump, so a subscriber hands the work to a task of its own;
    /// <see cref="Sync.InGameAreaCommands"/> is the one that answers.
    /// </summary>
    public event Action<InstanceSupervisor, SyncAreaCommand>? AreaCommand;

    /// <summary>A <c>showtickingarea</c> an operator typed (<c>D107</c>), raised on the console pump.</summary>
    public event Action<InstanceSupervisor, TickShowCommand>? TickShow;

    /// <summary>A <c>restorearea</c> an operator typed (<c>D108</c>), raised on the console pump.</summary>
    public event Action<InstanceSupervisor, AreaRestoreCommand>? RestoreCommand;

    /// <summary>The companion pack said hello in a protocol this build reads, raised on the console pump.</summary>
    public event Action<InstanceSupervisor>? CompanionHello;

    private void RaiseTickShow(TickShowCommand command)
    {
        try
        {
            TickShow?.Invoke(this, command);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "A tick-show subscriber threw for {InstanceId}", _instanceId);
        }
    }

    private void RaiseRestoreCommand(AreaRestoreCommand command)
    {
        try
        {
            RestoreCommand?.Invoke(this, command);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "A restore-command subscriber threw for {InstanceId}", _instanceId);
        }
    }

    private void RaiseCompanionHello()
    {
        try
        {
            CompanionHello?.Invoke(this);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "A companion-hello subscriber threw for {InstanceId}", _instanceId);
        }
    }

    private void RaiseAreaCommand(SyncAreaCommand command)
    {
        try
        {
            AreaCommand?.Invoke(this, command);
        }
        catch (Exception ex)
        {
            // The console pump: a subscriber that throws must not stop the output being read.
            _log.LogError(ex, "An area-command subscriber threw for {InstanceId}", _instanceId);
        }
    }

    private async Task SendProbeAsync()
    {
        try
        {
            await SendInternalAsync(ProbeCommand, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A probe that could not be sent leaves the run unprobed, which only
            // means the stop does not ask for positions.
            _log.LogWarning(ex, "Could not send the companion probe to {InstanceId}", _instanceId);
        }
    }

    /// <summary>
    /// Hands the companion pack the Sync Areas it outlines in game for operators, replacing what
    /// it had (<c>D106</c>). Sent at each hello and after every change to the areas; nothing is
    /// sent while the server is stopped, before the pack's hello, or to a pack too old to draw.
    /// </summary>
    /// <returns>Whether the commands were sent.</returns>
    public async Task<bool> SendOutlinesAsync(CancellationToken cancellationToken = default)
    {
        if (_companionRun is not { Readable: true } run || !CompanionPack.DrawsOutlines(run.PackVersion))
        {
            return false;
        }

        await _outlineGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_process is not { HasExited: false })
            {
                return false;
            }

            var commands = SyncOutlines.Commands(_definition.Sync.KeptAreas).ToList();

            // The ticking areas only once they have been read: settings alone say nothing of
            // where an area is (D107).
            if (TickingAreas is { } read)
            {
                commands.AddRange(TickingAreaOutlines.Commands(read.List, _definition.TickingAreas));
            }

            foreach (var command in commands)
            {
                await SendInternalAsync(command, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Outlines that could not be sent only leave the pack drawing the last set.
            _log.LogWarning(ex, "Could not send the Sync Area outlines to {InstanceId}", _instanceId);
            return false;
        }
        finally
        {
            _outlineGate.Release();
        }
    }

    private void LogAttribution(string what, string gamertag, RelayAttribution attribution)
    {
        if (attribution != RelayAttribution.Recorded)
        {
            _log.LogInformation(
                "Did not record the companion pack's {What} for {Gamertag} on {InstanceId}: {Attribution}",
                what, gamertag, _instanceId, attribution);
        }
    }

    /// <summary>Queues a store write behind the session writes already queued.</summary>
    private void QueuePlayerWrite(Func<IPlayerStore, Task> write, string what)
    {
        if (_players is null)
        {
            return;
        }

        lock (_playerWriteGate)
        {
            _playerWrites = _playerWrites.ContinueWith(
                _ => WriteAsync(write, what),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task WriteAsync(Func<IPlayerStore, Task> write, string what)
    {
        try
        {
            await write(_players!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never rethrown, for the reason WritePlayerEventAsync gives.
            _log.LogError(ex, "Could not record the companion pack's {What} on {InstanceId}", what, _instanceId);
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

    /// <summary>
    /// Writes the line that explains a state, then publishes the state (`T313`).
    /// </summary>
    /// <remarks>
    /// In this order so that whoever the state wakes - the panel, a caller waiting
    /// on <see cref="StatusChanged"/> or polling <see cref="Status"/> - finds the
    /// reason already in the console. Published first, a test that waited for
    /// Backoff read the console before the hold's line was in it, once in twelve
    /// loaded Windows suites.
    /// </remarks>
    private void NoteThenPublish(string note, Func<InstanceStatus, InstanceStatus> mutate)
    {
        Note(note);
        UpdateStatus(mutate);
    }

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
