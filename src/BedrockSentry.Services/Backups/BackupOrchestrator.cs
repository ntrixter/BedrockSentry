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
/// Runs one backup: <c>save hold</c> → poll <c>save query</c> → every enabled
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
    private readonly IPlayerStore? _players;
    private readonly TimeProvider _time;
    private readonly ILogger<BackupOrchestrator> _log;

    private readonly TimeSpan _queryTimeout;
    private readonly TimeSpan _resumeTimeout;
    private readonly TimeSpan _pollInterval;

    /// <summary>One backup per instance at a time; a second ask is refused, not queued.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public BackupOrchestrator(
        IEnumerable<IBackupStrategy> strategies,
        BackupHistory history,
        TimeProvider time,
        ILogger<BackupOrchestrator> log,
        IPlayerStore? players = null,
        TimeSpan? queryTimeout = null,
        TimeSpan? resumeTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _strategies = strategies.ToList();
        _history = history;
        _players = players;
        _time = time;
        _log = log;
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(60);
        _resumeTimeout = resumeTimeout ?? TimeSpan.FromSeconds(30);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>The reply that proves saves are running again (<c>docs/design.md</c> § 5.5).</summary>
    /// <remarks>
    /// "world", not "level". Community wrappers and older documentation carry
    /// the "level" wording, and a literal match on it would wait out its
    /// timeout on every backup while the real reply sat already read.
    /// </remarks>
    internal const string ResumeReply = "Changes to the world are resumed";

    public async Task<BackupRecord> RunAsync(
        InstanceSupervisor supervisor, BackupRequest request, CancellationToken cancellationToken = default)
    {
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
            return await FinishAsync(supervisor.InstanceId, new BackupRecord(
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
            return await FinishAsync(supervisor.InstanceId, new BackupRecord(
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

            return await FinishAsync(supervisor.InstanceId, new BackupRecord(
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

        var artifactDirectory = _history.DirectoryFor(supervisor.InstanceId);
        Directory.CreateDirectory(artifactDirectory);

        var worldsDirectory = Path.Combine(supervisor.WorkingDirectory, "worlds");

        // A stopped server's files are static: there is nothing to hold and
        // nobody mid-write, so the whole console exchange is skipped and the
        // manifest is a walk of what is on disk (docs/design.md § 8.1). This is also
        // what backup-on-shutdown uses, which is why the reason is not the test.
        var live = supervisor.Status.State is InstanceState.Running;

        IReadOnlyList<BackupStrategyResult> results;

        // Declared out here on purpose (`T158`). The resume runs in the finally
        // below, so a variable inside the try cannot carry its outcome out — and
        // the !live branch never resumes at all, so it has to stay null rather
        // than unset. Null means "there was no exchange to have an outcome".
        SaveResumeOutcome? resumed = null;

        if (!live)
        {
            var context = new BackupContext(
                supervisor.InstanceId, supervisor.Definition.Name,
                supervisor.WorkingDirectory, worldsDirectory,
                WalkStaticWorld(worldsDirectory), artifactDirectory,
                supervisor.Definition.Backup,
                request.Reason, request.Note, startedUtc);

            results = await RunStrategiesAsync(enabled, context, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            supervisor.Console.Append(
                ConsoleOrigin.Notice, "backup starting; pausing world saves", _time.GetUtcNow());

            await supervisor.SendCommandAsync("save hold", cancellationToken).ConfigureAwait(false);

            try
            {
                var manifest = await AwaitManifestAsync(supervisor, cancellationToken).ConfigureAwait(false);

                var context = new BackupContext(
                    supervisor.InstanceId, supervisor.Definition.Name,
                    supervisor.WorkingDirectory, worldsDirectory,
                    manifest, artifactDirectory,
                    supervisor.Definition.Backup,
                    request.Reason, request.Note, startedUtc);

                results = await RunStrategiesAsync(enabled, context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // The invariant. Not the caller's token: a cancelled backup is
                // precisely a case where saves must still be resumed.
                resumed = await ResumeSavesAsync(supervisor).ConfigureAwait(false);
            }
        }

        // Succeeded still means "every strategy succeeded" — the archive is either
        // good or it is not, and an unconfirmed resume does not change that. It
        // would also hide a perfectly good archive from LastSuccessfulUtcAsync,
        // blanking "last backup" and making the skip rule back up on every tick.
        // The resume outcome is its own field and its own sentence (`T158`).
        return await FinishAsync(supervisor.InstanceId, new BackupRecord(
            startedUtc, _time.GetUtcNow(), request.Reason, request.Note,
            Skipped: false, SkipReason: null,
            Succeeded: results.All(r => r.Succeeded),
            Strategies: results,
            SavesResumed: resumed,

            // Null rather than an empty list, so the key stays out of history.json
            // altogether on every install that never trips the gate.
            BlockedStrategies: blocked.Count == 0 ? null : blocked),
            cancellationToken).ConfigureAwait(false);
    }

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
                throw new TimeoutException(
                    $"The server did not produce a save manifest within {_queryTimeout.TotalSeconds:0}s. "
                    + "Saves were being held and will be resumed.");
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
    private async Task<SaveResumeOutcome> ResumeSavesAsync(InstanceSupervisor supervisor)
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

        var confirmation =
            await watch.WaitAsync(_resumeTimeout, _time, CancellationToken.None).ConfigureAwait(false);

        if (confirmation is not null)
        {
            supervisor.Console.Append(
                ConsoleOrigin.Notice, "backup finished; world saves resumed", _time.GetUtcNow());
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
    /// The manifest for a stopped server: everything under <c>worlds</c>, full
    /// lengths, because nothing is mid-write and there is nobody to ask.
    /// </summary>
    private static IReadOnlyList<SaveManifestEntry> WalkStaticWorld(string worldsDirectory)
    {
        if (!Directory.Exists(worldsDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(worldsDirectory, "*", SearchOption.AllDirectories)
            .Select(file => new SaveManifestEntry(
                Path.GetRelativePath(worldsDirectory, file).Replace('\\', '/'),
                new FileInfo(file).Length))
            .ToList();
    }

    private async Task<BackupRecord> FinishAsync(
        string instanceId, BackupRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await _history.AppendAsync(instanceId, record, cancellationToken).ConfigureAwait(false);
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
