using System.Collections.Concurrent;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Players;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>What a caller asks for: why, and optionally a note for the history.</summary>
public sealed record BackupRequest(BackupReason Reason, string? Note = null);

/// <summary>
/// Runs one backup: <c>save hold</c> → poll <c>save query</c> → wait for the write the
/// hold sets off, and when one lands resume and hold again (<c>T356</c>) → every enabled
/// strategy in parallel → <c>save resume</c>, unconditionally
/// (<c>docs/design.md</c> § 8.1).
/// </summary>
/// <remarks>
/// The resume is the invariant this class exists to protect. Whatever happens
/// between hold and resume — a failed strategy, a thrown exception, a full
/// disk, a cancelled token — the resume still fires, on its own timeout and
/// its own token. A live server left with saves paused writes nothing to disk
/// from that moment on, and loses everything since the hold on its next crash;
/// it is the worst failure mode in this codebase.
///
/// Timeouts are constructor arguments with defaults rather than configuration,
/// for the <c>D22</c> reason: the only correct value is "long enough", and a
/// configurable one set too short quietly converts working backups into failed
/// ones.
/// </remarks>
public sealed class BackupOrchestrator
{
    private readonly IReadOnlyList<IBackupStrategy> _strategies;
    private readonly BackupHistory _history;
    private readonly BackupArtifactStore _artifacts;
    private readonly IShadowCopyService _shadows;
    private readonly IPlayerStore? _players;
    private readonly TimeProvider _time;
    private readonly ILogger<BackupOrchestrator> _log;
    private readonly BackupRuns _runs;

    private readonly TimeSpan _queryTimeout;
    private readonly TimeSpan _resumeTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _saveLandsWithin;

    /// <summary>One backup per instance at a time; a second ask is refused, not queued.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public BackupOrchestrator(
        IEnumerable<IBackupStrategy> strategies,
        BackupHistory history,
        BackupArtifactStore artifacts,
        IShadowCopyService shadows,
        TimeProvider time,
        ILogger<BackupOrchestrator> log,
        IPlayerStore? players = null,
        TimeSpan? queryTimeout = null,
        TimeSpan? resumeTimeout = null,
        TimeSpan? pollInterval = null,
        BackupRuns? runs = null,
        TimeSpan? saveLandsWithin = null)
    {
        _strategies = strategies.ToList();
        _history = history;
        _artifacts = artifacts;
        _shadows = shadows;
        _players = players;
        _time = time;
        _log = log;
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(60);
        _resumeTimeout = resumeTimeout ?? TimeSpan.FromSeconds(30);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        _saveLandsWithin = saveLandsWithin ?? TimeSpan.FromSeconds(10);
        _runs = runs ?? new BackupRuns(time);
    }

    /// <summary>Where each server's backup is, for the strip under its controls (<c>D113</c>).</summary>
    public BackupRuns Runs => _runs;

    /// <summary>The reply that proves saves are running again (<c>docs/design.md</c> § 5.5).</summary>
    /// <remarks>
    /// "world", not "level". Community wrappers and older documentation carry
    /// the "level" wording, and a literal match on it would wait out its
    /// timeout on every backup while the real reply sat already read.
    /// </remarks>
    internal const string ResumeReply = "Changes to the world are resumed";

    /// <summary>The console notice when the resume follows the whole copy.</summary>
    internal const string BackupFinishedNotice = "backup finished; world saves resumed";

    /// <summary>
    /// The console notice when the resume follows the snapshot, with the copy
    /// still to come (<c>D66</c>).
    /// </summary>
    /// <remarks>
    /// A separate sentence rather than the one above, because the one above would
    /// be false: the backup has not finished, and somebody reading the console to
    /// decide whether the server is busy would be told the wrong thing. This is the
    /// label-states-something-the-code-does-not-do class that `D66` exists for,
    /// arriving one line inside the fix for it.
    /// </remarks>
    internal const string SnapshotTakenNotice =
        "snapshot taken; world saves resumed, backup still copying";

    /// <summary>The console notice when the save landed past its own list and is paused again (<c>T356</c>).</summary>
    internal const string SaveLandedNotice =
        "the save wrote past its own list; pausing world saves again to list it whole";

    /// <summary>How long a write must stay still before it counts as landed (<c>T356</c>).</summary>
    private static readonly TimeSpan SaveSettle = TimeSpan.FromSeconds(1);

    /// <exception cref="InstanceClaimedException">
    /// The server is claimed by a job, such as a sync, and this backup is not one the
    /// job takes itself (<see cref="BackupClaimRule"/>).
    /// </exception>
    public async Task<BackupRecord> RunAsync(
        InstanceSupervisor supervisor, BackupRequest request, CancellationToken cancellationToken = default)
    {
        if (BackupClaimRule.WaitsForAClaim(request.Reason) && supervisor.Status.ClaimedFor is { } purpose)
        {
            throw new InstanceClaimedException(supervisor.Definition.Name, purpose);
        }

        var gate = _gates.GetOrAdd(supervisor.InstanceId, _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"A backup of '{supervisor.InstanceId}' is already running. Backups do not queue: "
                + "by the time this one finished, the queued one would archive the same state again.");
        }

        try
        {
            return await RunLockedAsync(supervisor, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<BackupRecord> RunLockedAsync(
        InstanceSupervisor supervisor, BackupRequest request, CancellationToken cancellationToken)
    {
        var startedUtc = _time.GetUtcNow();

        if (request.Reason == BackupReason.Scheduled
            && await ShouldSkipAsync(supervisor.InstanceId, cancellationToken).ConfigureAwait(false))
        {
            return await FinishAsync(supervisor, new BackupRecord(
                startedUtc, _time.GetUtcNow(), request.Reason, request.Note,
                Skipped: true, SkipReason: "Nobody has played on this server since the last backup.",
                Succeeded: false, Strategies: []), cancellationToken).ConfigureAwait(false);
        }

        // Asked once per strategy and reused, not asked once per list (`D45`).
        // Blocked() reads an options monitor live, so two calls either side of a
        // config reload can legitimately disagree — and a strategy would then land
        // in both lists or in neither.
        var considered = _strategies
            .Where(s => s.IsEnabled(supervisor.Definition.Backup))
            .Select(s => (Strategy: s, Block: s.Blocked(supervisor.Definition.Backup)))
            .ToList();

        var blocked = considered.Where(c => c.Block is not null).Select(c => c.Block!).ToList();
        var enabled = considered.Where(c => c.Block is null).Select(c => c.Strategy).ToList();

        if (enabled.Count == 0 && blocked.Count == 0)
        {
            return await FinishAsync(supervisor, new BackupRecord(
                startedUtc, _time.GetUtcNow(), request.Reason, request.Note,
                Skipped: true, SkipReason: "No backup strategy is enabled for this instance.",
                Succeeded: false, Strategies: []), cancellationToken).ConfigureAwait(false);
        }

        if (enabled.Count == 0)
        {
            // Not a skip, and the distinction is the point. A skip is the absence of
            // a request — nobody played, nothing is switched on — and it renders as
            // a neutral badge. This is a request that was refused: the operator
            // asked for a backup, has none, and can fix it. Reporting it as "No
            // backup strategy is enabled for this instance" would be a straight lie
            // about a ticked box, and they would conclude the wrapper is broken.
            _log.LogError(
                "No backup of {InstanceId} was taken: every strategy it has switched on is "
                + "blocked. {Why}",
                supervisor.InstanceId, string.Join(" ", blocked.Select(b => b.Detail)));

            return await FinishAsync(supervisor, new BackupRecord(
                startedUtc, _time.GetUtcNow(), request.Reason, request.Note,
                Skipped: false, SkipReason: null,
                Succeeded: false, Strategies: [],
                BlockedStrategies: blocked), cancellationToken).ConfigureAwait(false);
        }

        if (blocked.Count > 0)
        {
            _log.LogWarning(
                "Backup of {InstanceId} is running without {Count} strategy it has switched on. "
                + "{Why}",
                supervisor.InstanceId, blocked.Count,
                string.Join(" ", blocked.Select(b => b.Detail)));
        }

        // Read once, before the strip is begun, so the steps it lists and the path taken
        // below are one answer (`D113`). A stopped server's files are static: there is
        // nothing to hold and nobody mid-write, so the whole console exchange is skipped
        // and the manifest is a walk of what is on disk (docs/design.md § 8.1). This is
        // also what backup-on-shutdown uses, which is why the reason is not the test.
        var live = supervisor.Status.State is InstanceState.Running;
        var wantsShadowCopy = supervisor.Definition.Backup.Acquisition.UseShadowCopy;

        // A backup inside a job that claims the server, such as a restore's safety
        // backup, is a step of that job's strip and draws none of its own.
        var run = supervisor.Status.ClaimedFor is null
            ? _runs.Begin(supervisor.InstanceId, request.Reason, live, wantsShadowCopy)
            : null;

        try
        {
            var record = await TakeAsync(
                supervisor, request, startedUtc, enabled, blocked, live, wantsShadowCopy, run,
                cancellationToken).ConfigureAwait(false);

            run?.End(record);
            return record;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run?.Cancelled();
            throw;
        }
        catch (Exception ex)
        {
            run?.Failed(ex.Message);
            throw;
        }
    }

    private async Task<BackupRecord> TakeAsync(
        InstanceSupervisor supervisor,
        BackupRequest request,
        DateTimeOffset startedUtc,
        IReadOnlyList<IBackupStrategy> enabled,
        IReadOnlyList<BlockedStrategy> blocked,
        bool live,
        bool wantsShadowCopy,
        BackupRuns.Run? run,
        CancellationToken cancellationToken)
    {
        // Declared out here, before the try that records a run which never reached a
        // strategy (`T267`), because both the success record and the failure record
        // are built from them. Null results is what tells the two apart: it is only
        // assigned once every strategy has come back.
        IReadOnlyList<BackupStrategyResult>? results = null;

        // Declared out here on purpose (`T158`). The resume runs in the finally
        // below, so a variable inside the try cannot carry its outcome out — and
        // the !live branch never resumes at all, so it has to stay null rather
        // than unset. Null means "there was no exchange to have an outcome".
        SaveResumeOutcome? resumed = null;

        // Null until something has decided it. A run that stops before the shadow
        // copy was attempted has no acquisition to report, and recording the
        // NotRequested this used to start from would claim the toggle was off on an
        // install where it is ticked (`D66`).
        BackupAcquisition? acquisition = null;
        string? acquisitionDetail = null;

        try
        {
            var artifactDirectory = _history.DirectoryFor(supervisor.InstanceId);
            Directory.CreateDirectory(artifactDirectory);

            var worldsDirectory = Path.Combine(supervisor.WorkingDirectory, "worlds");

            // Once per run, not once per strategy (`D68`). Two formats enabled
            // together have to prune against the same answer, and a second read could
            // legitimately differ if somebody ticks a lock while this is running.
            var locked = await _artifacts
                .LockedAsync(supervisor.InstanceId, cancellationToken).ConfigureAwait(false);

            if (!live)
            {
                // A stopped server is holding nothing, so a snapshot would buy
                // nothing — the toggle's entire promise is that saves are held for
                // less time. Recorded as NotApplicable rather than NotRequested,
                // because the toggle IS on and a row saying otherwise is a small lie
                // about a setting the reader can see is ticked (`D66`).
                acquisition = wantsShadowCopy
                    ? BackupAcquisition.NotApplicable
                    : BackupAcquisition.NotRequested;

                var walked = WalkStaticWorld(worldsDirectory);
                run?.Copying(walked.Count, Copiers(enabled));

                var context = new BackupContext(
                    supervisor.InstanceId, supervisor.Definition.Name,
                    supervisor.WorkingDirectory, worldsDirectory, worldsDirectory,
                    walked, artifactDirectory,
                    supervisor.Definition.Backup,
                    request.Reason, request.Note, startedUtc, locked,
                    ServerVersion: supervisor.Status.ServerVersion,
                    OnFileDone: run is null ? null : run.FileDone);

                results = await RunStrategiesAsync(enabled, context, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!wantsShadowCopy)
                {
                    acquisition = BackupAcquisition.NotRequested;
                }

                supervisor.Console.Append(
                    ConsoleOrigin.Notice, "backup starting; pausing world saves", _time.GetUtcNow());

                // OUTSIDE the try below, and that is load-bearing rather than
                // incidental: a hold that was never sent must not be followed by a
                // resume, which BDS answers "A previous save has not been completed."
                // and which would be recorded as a warning about a backup that had
                // not started.
                //
                // It is sound only because a throw from SendAsync means the command
                // was not sent -- three arms, all before any write, and pinned by
                // A_send_that_throws_has_not_reached_the_server rather than asserted
                // here. `D87` is whether to keep depending on that or record the hold
                // pessimistically and pay for a resume nothing was holding.
                await supervisor.SendCommandAsync("save hold", cancellationToken).ConfigureAwait(false);

                ShadowCopy? shadow = null;

                // Whether a hold is outstanding, so whether the finally owes a resume. Set
                // false BEFORE each early resume is awaited, for `T226`'s reason: what it
                // records is that a resume was sent, and a resume that threw partway has
                // still been sent.
                var holding = true;

                try
                {
                    var manifest = await AwaitManifestAsync(supervisor, cancellationToken).ConfigureAwait(false);

                    // `T356`. The manifest lists the database as it stood at the hold, and
                    // BDS writes the changes the hold flushed past those lengths a second
                    // or more later; the manifest does not move while the hold lasts. So a
                    // copy at these lengths lacks whatever changed in the seconds before
                    // the backup. When that write lands, resume and hold again: the
                    // second list takes it in, and the copy still stops at lengths the
                    // server named (`D97`). Quiet means the first list is the whole save.
                    // A zero window watches nothing, for a caller whose world is not a save.
                    var landing = _saveLandsWithin <= TimeSpan.Zero
                        ? SaveLanding.Quiet
                        : await SaveLandingWatch.WaitAsync(
                            worldsDirectory, manifest, _saveLandsWithin, SaveSettle, _pollInterval, _time,
                            cancellationToken).ConfigureAwait(false);

                    if (landing is not SaveLanding.Quiet)
                    {
                        holding = false;
                        var between = await ResumeSavesAsync(supervisor, SaveLandedNotice)
                            .ConfigureAwait(false);

                        if (between is not SaveResumeOutcome.Confirmed)
                        {
                            // Not held, or not known to be; a second hold on top of either
                            // is a copy nobody can vouch for. The record carries this
                            // resume's outcome, since the finally sends none.
                            resumed = between;
                            throw new InvalidOperationException(
                                "The save wrote past its own list, and saves could not be "
                                + "paused again to list it whole, so nothing was copied.");
                        }

                        // A throw here means the hold was not sent (`D87`), and nothing is
                        // held, which is what holding already says.
                        await supervisor.SendCommandAsync("save hold", cancellationToken).ConfigureAwait(false);
                        holding = true;

                        manifest = await AwaitManifestAsync(supervisor, cancellationToken).ConfigureAwait(false);
                    }

                    var source = worldsDirectory;

                    if (wantsShadowCopy)
                    {
                        run?.Step(BackupStep.Snapshot);

                        (shadow, source, acquisition, acquisitionDetail) =
                            await AcquireAsync(supervisor, worldsDirectory, cancellationToken)
                                .ConfigureAwait(false);

                        if (acquisition is not BackupAcquisition.ShadowCopy)
                        {
                            run?.FellBack();
                        }
                    }

                    if (acquisition is BackupAcquisition.ShadowCopy)
                    {
                        // THE POINT OF THE TOGGLE, and the reason `D66` is a contract
                        // change rather than a rename. The world is frozen in the
                        // snapshot, so nothing the server does from here can reach
                        // the bytes being copied — which means saves can run again
                        // now rather than after the copy. Without this the toggle
                        // costs a snapshot and buys nothing, and the label would
                        // state something the code does not do for the second time.
                        // BEFORE the await, and that ordering is the whole of `T226`.
                        // ResumeSavesAsync catches every failure of the SEND and turns
                        // it into an outcome -- but the console watch, the wait and
                        // the append all sit outside that try, so the method can
                        // still throw. Set afterwards, such a throw left the flag
                        // false and the finally sent a SECOND save resume: the one
                        // thing `T213` clause 2 exists to forbid, on the arm the
                        // happy path never reaches.
                        //
                        // What the flag has to record is "a resume was attempted
                        // early", not "an early resume returned" -- a first resume
                        // that threw partway has still been sent, and sending another
                        // is not a recovery.
                        holding = false;
                        run?.Step(BackupStep.Resuming);
                        resumed = await ResumeSavesAsync(supervisor, SnapshotTakenNotice)
                            .ConfigureAwait(false);
                    }

                    // Listed from wherever the bytes will be read, so the list and
                    // the copy describe one folder (`D97`).
                    var (others, unlisted) = ListBeyondTheSave(supervisor.InstanceId, source, manifest);

                    run?.Copying(manifest.Count + (others?.Files.Count ?? 0), Copiers(enabled));

                    var context = new BackupContext(
                        supervisor.InstanceId, supervisor.Definition.Name,
                        supervisor.WorkingDirectory, worldsDirectory, source,
                        manifest, artifactDirectory,
                        supervisor.Definition.Backup,
                        request.Reason, request.Note, startedUtc, locked,
                        others, unlisted, supervisor.Status.ServerVersion,
                        OnFileDone: run is null ? null : run.FileDone);

                    results = await RunStrategiesAsync(enabled, context, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    // The invariant, and the guard is what already happened rather
                    // than what was configured: asking the settings whether a
                    // snapshot let saves resume early would be the same fact stated
                    // twice.
                    if (holding)
                    {
                        // Not the caller's token: a cancelled backup is precisely a
                        // case where saves must still be resumed.
                        run?.Step(BackupStep.Resuming);
                        resumed = await ResumeSavesAsync(supervisor, BackupFinishedNotice)
                            .ConfigureAwait(false);
                    }

                    if (shadow is not null)
                    {
                        // Unconditional, in the same spirit as the resume. A shadow
                        // copy left behind holds volume space until somebody notices,
                        // and on a schedule it is left behind every time.
                        await _shadows.ReleaseAsync(shadow, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex) when (results is null
            && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // `T267`. Everything above the first strategy — the artifact directory,
            // the locks file, the world walk, the save hold, the manifest wait — used
            // to throw straight past the only call that appends to the history. So a
            // run refused there left NOTHING: the standing card read "No backups
            // yet" or "Backed up" over a server whose every backup was failing, and a
            // scheduled run's only witness was a line in the Windows Application log.
            // The record is where `D67` says such a sentence belongs; the log line
            // stays, so that witness is not lost either.
            //
            // A caller's cancellation still propagates, for RunStrategiesAsync's
            // reason: a backup somebody cancelled is not a failed one. And a throw
            // after the strategies have come back is not this case — results is set
            // by then, and the archives it names are real. On the live path the
            // finally above has already run by the time this does, so the resume's
            // outcome is known and goes on the record with the failure.
            _log.LogError(
                ex, "No backup of {InstanceId} was taken: the run stopped before anything was copied",
                supervisor.InstanceId);

            return await FinishAsync(supervisor, new BackupRecord(
                startedUtc, _time.GetUtcNow(), request.Reason, request.Note,
                Skipped: false, SkipReason: null,
                Succeeded: false,
                Strategies: [],
                SavesResumed: resumed,
                BlockedStrategies: blocked.Count == 0 ? null : blocked,
                Acquisition: acquisition,
                AcquisitionDetail: acquisitionDetail,

                // Never blank: a failed row with no sentence is the case `D45`'s
                // composing exists to prevent.
                Failure: string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message),
                cancellationToken).ConfigureAwait(false);
        }

        // Succeeded still means "every strategy succeeded" — the archive is either
        // good or it is not, and an unconfirmed resume does not change that. It
        // would also hide a perfectly good archive from LastSuccessfulUtcAsync,
        // blanking "last backup" and making the skip rule back up on every tick.
        // The resume outcome is its own field and its own sentence (`T158`).
        return await FinishAsync(supervisor, new BackupRecord(
            startedUtc, _time.GetUtcNow(), request.Reason, request.Note,
            Skipped: false, SkipReason: null,
            Succeeded: results.All(r => r.Succeeded),
            Strategies: results,
            SavesResumed: resumed,

            // Null rather than an empty list, so the key stays out of history.json
            // altogether on every install that never trips the gate.
            BlockedStrategies: blocked.Count == 0 ? null : blocked,

            Acquisition: acquisition,
            AcquisitionDetail: acquisitionDetail),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How many of <paramref name="enabled"/> pass each file themselves, which the strip's bar counts.</summary>
    private static int Copiers(IReadOnlyList<IBackupStrategy> enabled) => enabled.Count(s => s.CountsFiles);

    private async Task<bool> ShouldSkipAsync(string instanceId, CancellationToken cancellationToken)
    {
        if (_players is null)
        {
            return false;
        }

        var last = await _history.LastSuccessfulUtcAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (last is not { } since)
        {
            // Never backed up: the first one always runs, whatever the roster
            // says — "no activity since the last backup" is not a sentence that
            // means anything yet.
            return false;
        }

        return !await _players.AnyActivitySinceAsync(instanceId, since, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends <c>save query</c> until the manifest appears. Ready on the first
    /// poll against a real server (<c>docs/design.md</c> § 5.5); the loop is for the
    /// slow-disk day that measurement does not cover.
    /// </summary>
    private async Task<IReadOnlyList<SaveManifestEntry>> AwaitManifestAsync(
        InstanceSupervisor supervisor, CancellationToken cancellationToken)
    {
        // Subscribed before the first send, and held across every retry. This
        // used to re-read a cursor on a timer, which loses the answer whenever
        // the buffer wraps between polls — and losing it here is worse than
        // losing the resume's confirmation, because the backup would go on to
        // copy a save it never confirmed was flushed (`T170`).
        using var watch = ConsoleWatch.For(
            supervisor.Console,
            // The manifest arrives bare — no [timestamp LEVEL] prefix — so it is
            // recognised by shape, from the parsed message (docs/design.md § 5.5).
            // Requiring the prefix here would discard exactly the line carrying
            // the payload.
            line => line.Origin == ConsoleOrigin.Stdout
                && SaveManifest.TryParse(line.Message, out _));

        var deadline = _time.GetUtcNow() + _queryTimeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await supervisor.SendCommandAsync("save query", cancellationToken).ConfigureAwait(false);

            // A slice, not the whole budget: BDS answers "not ready" by saying
            // nothing this loop matches, so the retry is the ask itself.
            var reply = await watch
                .WaitAsync(_pollInterval, _time, cancellationToken)
                .ConfigureAwait(false);

            if (reply is not null && SaveManifest.TryParse(reply.Message, out var entries))
            {
                return entries;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                // No word here about the resume. This message is kept on the record
                // (`T267`), which is written after the finally has sent it, so a
                // promise that saves "will be resumed" would be stored beside an
                // outcome that may say they were not — the record's own resume
                // sentence says what actually happened.
                throw new TimeoutException(
                    $"The server did not answer 'save query' with a save manifest within "
                    + $"{_queryTimeout.TotalSeconds:0}s.");
            }
        }
    }

    private async Task<IReadOnlyList<BackupStrategyResult>> RunStrategiesAsync(
        IReadOnlyList<IBackupStrategy> enabled, BackupContext context, CancellationToken cancellationToken)
    {
        // All at once, each failure its own result: one strategy failing is not
        // a reason to deny the others their copy of the same snapshot.
        var runs = enabled.Select(async strategy =>
        {
            try
            {
                return await strategy.RunAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller cancelled the backup; that is not a strategy
                // failure, and recording it as one would put a red mark in the
                // history for something somebody did on purpose. It propagates —
                // through the finally that resumes saves — as the cancellation
                // it is.
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex, "Backup strategy {Strategy} failed for {InstanceId}",
                    strategy.Name, context.InstanceId);

                return new BackupStrategyResult(strategy.Name, Succeeded: false, ex.Message, null);
            }
        });

        return await Task.WhenAll(runs).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the shadow copy the acquisition toggle asked for, or falls back to
    /// the live world and says why (<c>D66</c>, <c>D67</c>).
    /// </summary>
    /// <remarks>
    /// <para><strong>A failed acquisition is never a failed backup.</strong> It is
    /// a slower one: saves are held for the whole copy instead of for a moment,
    /// which is a cost an administrator can weigh rather than a loss. Reporting it
    /// as a failure would blank <c>LastSuccessfulUtcAsync</c>, empty "last backup"
    /// and make the skip rule back up on every tick — the `D45` and `T173`
    /// precedent, and the reason this returns a state rather than throwing.</para>
    ///
    /// <para>Cancellation is the one exception that still propagates: a cancelled
    /// backup is not a volume that cannot be snapshotted, and swallowing it here
    /// would turn a cancelled run into a slow one.</para>
    /// </remarks>
    private async Task<(ShadowCopy? Shadow, string Source, BackupAcquisition State, string? Detail)>
        AcquireAsync(
            InstanceSupervisor supervisor, string worldsDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var volumeRoot = ShadowCopyPaths.VolumeRootOf(worldsDirectory);
            var shadow = await _shadows.CreateAsync(supervisor.InstanceId, volumeRoot, cancellationToken)
                .ConfigureAwait(false);

            return (
                shadow,
                ShadowCopyPaths.Inside(shadow.DeviceObject, worldsDirectory),
                BackupAcquisition.ShadowCopy,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex, "Could not take a shadow copy for the backup of {InstanceId}. The backup will "
                + "read the live world instead, so saves stay held for the whole copy.",
                supervisor.InstanceId);

            supervisor.Console.Append(
                ConsoleOrigin.Notice,
                "no shadow copy could be taken; saves stay held for the whole backup",
                _time.GetUtcNow());

            return (null, worldsDirectory, BackupAcquisition.LiveFallback, ex.Message);
        }
    }

    /// <summary>
    /// Fires <c>save resume</c>, confirms the measured reply, and reports which of
    /// the four things happened. Never throws: this runs inside the finally that
    /// protects the invariant, and an exception here would mask whatever the
    /// backup itself threw.
    /// </summary>
    /// <remarks>
    /// It returned <c>Task</c> until `T158`, which is why a resume that was never
    /// confirmed could be stored and displayed as a clean backup — the outcome had
    /// nowhere to go, and the only trace was a <c>LogCritical</c> nobody read.
    /// </remarks>
    /// <param name="notice">
    /// What the instance console says when the resume is confirmed. There are two,
    /// because a resume that follows a snapshot has not finished a backup
    /// (<c>D66</c>).
    /// </param>
    private async Task<SaveResumeOutcome> ResumeSavesAsync(
        InstanceSupervisor supervisor, string notice)
    {
        // Before the send, so a confirmation that comes straight back cannot
        // arrive before anybody is listening for it.
        using var watch = ConsoleWatch.For(
            supervisor.Console,
            line => line.Message.StartsWith(ResumeReply, StringComparison.OrdinalIgnoreCase));

        try
        {
            await supervisor.SendCommandAsync("save resume", CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process is gone. A dead server holds no saves, so there is
            // nothing left to resume — this is fine, and saying otherwise in
            // the log would send someone hunting a problem that does not exist.
            _log.LogInformation(
                "Instance {InstanceId} exited before save resume; nothing was left held",
                supervisor.InstanceId);
            return SaveResumeOutcome.ServerGone;
        }
        catch (Exception ex)
        {
            _log.LogCritical(
                ex, "Could not send save resume to {InstanceId} — the server may still be "
                + "holding saves. Send 'save resume' at its console now.", supervisor.InstanceId);
            return SaveResumeOutcome.NotSent;
        }

        AfterResumeSent();

        var confirmation =
            await watch.WaitAsync(_resumeTimeout, _time, CancellationToken.None).ConfigureAwait(false);

        if (confirmation is not null)
        {
            supervisor.Console.Append(ConsoleOrigin.Notice, notice, _time.GetUtcNow());
            return SaveResumeOutcome.Confirmed;
        }

        // Loud, and in both places somebody might look. The command was sent
        // and the confirmation never came, which is either a very slow server
        // or one that no longer has saves held for a reason we cannot see.
        _log.LogCritical(
            "save resume was sent to {InstanceId} and no confirmation arrived within {Timeout}. "
            + "A server left holding saves writes nothing to disk, so check the instance console "
            + "for \"Changes to the world are resumed\" — if it is there the resume worked and "
            + "this is a false alarm.", supervisor.InstanceId, _resumeTimeout);

        // At Warn, not as a plain notice. The console's own rule is that severity
        // outranks origin, so this now carries the warning glyph and colour
        // instead of the same accent green as "backup finished" three lines above
        // it — which is how the operator came to read a warning as a success
        // and then as a contradiction (`T170`).
        //
        // And it no longer tells anybody to type 'save resume'. That advice sent
        // the operator to a console where BDS answers "A previous save has not
        // been completed." — which is its ordinary reply when there is no hold
        // outstanding, i.e. when the resume already worked. Following the
        // wrapper's own advice produced a frightening line about a world that
        // was fine. Say what is unknown and what it would look like either way.
        supervisor.Console.Append(
            ConsoleOrigin.Notice,
            "save resume was sent and no confirmation came back within "
            + $"{_resumeTimeout.TotalSeconds:0}s. The world may still be holding saves, or the "
            + "confirmation may simply have been missed. Check the lines above for \"Changes to "
            + "the world are resumed\" — if it is there, this was a false alarm and the world is "
            + "fine.",
            _time.GetUtcNow(),
            BdsLogLevel.Warn);

        return SaveResumeOutcome.Unconfirmed;
    }

    /// <summary>
    /// Runs between sending <c>save resume</c> and waiting for its confirmation, so a
    /// test can make the resume throw after its command has gone (<c>T226</c>).
    /// </summary>
    /// <remarks>
    /// No console subscriber can make that stretch throw: <see cref="ConsoleBuffer"/>
    /// isolates handler exceptions, and the wait is given
    /// <see cref="CancellationToken.None"/>. Without this the ordering of
    /// <c>holding</c> rested on reading, and reversing it left the suite green.
    /// </remarks>
    internal Action AfterResumeSent { get; set; } = static () => { };

    /// <summary>
    /// How a stopped server's world is listed, so the refused arm can be exercised
    /// (<c>T267</c>).
    /// </summary>
    /// <remarks>
    /// The state an operator meets is a <c>worlds</c> directory this service may not
    /// read, and a build running as root cannot arrange one — a rig that denies
    /// itself a folder and then lists it succeeds (<c>T176</c>). Only this one call
    /// is substituted; the existence check before it is the real one, so a test
    /// drives the same shape a denied folder produces rather than a story about it
    /// (<c>T174</c>).
    /// </remarks>
    internal Func<string, IEnumerable<string>> WorldLister { get; set; } =
        worlds => Directory.EnumerateFiles(worlds, "*", SearchOption.AllDirectories);

    /// <summary>
    /// The files under <c>worlds</c> that a running server's backup copies whole
    /// beside the save (<c>D97</c>).
    /// </summary>
    /// <remarks>
    /// A folder that cannot be listed costs those files and not the backup: the
    /// save's own files are copied either way, and the reason goes into each
    /// strategy's result, which is stored, rather than only into the log (<c>D67</c>).
    /// Through <see cref="WorldLister"/>, the seam the stopped walk uses, so a test
    /// reaches the refused arm the same way.
    /// </remarks>
    private (BeyondTheSave? Others, string? Unlisted) ListBeyondTheSave(
        string instanceId, string source, IReadOnlyList<SaveManifestEntry> manifest)
    {
        try
        {
            return (WorldFolder.BeyondTheSave(
                WorldLister(source).Select(file => Path.GetRelativePath(source, file)),
                manifest), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(
                ex, "Could not list the worlds folder of {InstanceId}; the backup holds the "
                + "files the save named and nothing beside them", instanceId);

            return (null, ex.Message);
        }
    }

    /// <summary>
    /// The manifest for a stopped server: everything under <c>worlds</c>, full
    /// lengths, because nothing is mid-write and there is nobody to ask.
    /// </summary>
    private IReadOnlyList<SaveManifestEntry> WalkStaticWorld(string worldsDirectory)
    {
        if (!Directory.Exists(worldsDirectory))
        {
            return [];
        }

        return WorldLister(worldsDirectory)
            .Select(file => new SaveManifestEntry(
                Path.GetRelativePath(worldsDirectory, file).Replace('\\', '/'),
                new FileInfo(file).Length))
            .ToList();
    }

    private async Task<BackupRecord> FinishAsync(
        InstanceSupervisor supervisor, BackupRecord record, CancellationToken cancellationToken)
    {
        var instanceId = supervisor.InstanceId;

        try
        {
            await _history.AppendAsync(instanceId, record, cancellationToken).ConfigureAwait(false);

            // After the append, so the row just written is the newest success when
            // it is one, and every run keeps the history to this server's limit
            // rather than a job of its own having to (`D98`).
            await _history
                .TrimAsync(
                    instanceId, supervisor.Definition.Backup.History.KeepDays, _time.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The backup itself may have succeeded; failing the whole run
            // because its diary entry could not be written inverts what matters.
            _log.LogError(ex, "Could not record backup history for {InstanceId}", instanceId);
        }

        return record;
    }
}
