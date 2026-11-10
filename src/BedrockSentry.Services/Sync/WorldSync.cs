using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Structures;
using BedrockSentry.Core.Sync;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Worlds;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Sync;

/// <summary>Whether a sync can start on a server now, and with what.</summary>
/// <param name="Backup">The source's newest backup a sync can copy in, or null.</param>
/// <param name="Problems">Why it cannot start; empty when it can.</param>
public sealed record SyncReadiness(
    InstanceSupervisor Target,
    InstanceSupervisor? Source,
    RestorableBackup? Backup,
    SyncPlan Plan,
    IReadOnlyList<string> Problems)
{
    public bool CanStart => Problems.Count == 0;
}

/// <summary>
/// Copies another server's latest backup into a server and puts its Sync Areas
/// back over it (<c>D103</c>).
/// </summary>
/// <remarks>
/// <para><strong>The five steps.</strong> Save each kept piece on this server
/// while it runs (<see cref="StructureConsole.SaveAsync"/>); stop it, which
/// writes the saves to disk; read the pieces out of the stopped world into this
/// server's sync folder; take a safety backup and replace the world with the
/// source's backup, under this server's own level name, and put the pieces in
/// its structures pack; start it and put the pieces back
/// (<see cref="StructureConsole.PutBackAsync"/>).</para>
///
/// <para><strong>Nothing of this server's world changes until every piece is a
/// file outside it.</strong> A save that fails, a stop that does not happen or a
/// piece missing from the stopped world calls the sync off with the world as it
/// was, and a server that was running is started again. After the world is
/// replaced, a failure leaves it replaced and says what was not put back; the
/// pieces stay in the sync folder and the world from before in the safety
/// backup.</para>
///
/// <para><strong>The structures pack comes off at the next launch</strong>,
/// answered 2026-10-04: this is the server's <see cref="IBeforeLaunch"/>, so every
/// launch takes the pack off first, except the one the sync makes to put the
/// pieces back.</para>
///
/// <para>The source is only ever read through its backups, and the sync runs in
/// the background so a page closing does not cut it off. One sync at a time on a
/// server.</para>
/// </remarks>
public sealed class WorldSync : IBeforeLaunch
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly InstanceRegistry _registry;
    private readonly WorldRestorer _restorer;
    private readonly BackupHistory _history;
    private readonly BackupArtifactStore _artifacts;
    private readonly StructureConsole _console;
    private readonly StructurePackWriter _packs;
    private readonly ISentryPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<WorldSync> _log;

    private readonly ConcurrentDictionary<string, SyncRun> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _keepPackForLaunch = new(StringComparer.Ordinal);

    public WorldSync(
        InstanceRegistry registry,
        WorldRestorer restorer,
        BackupHistory history,
        BackupArtifactStore artifacts,
        StructureConsole console,
        StructurePackWriter packs,
        ISentryPaths paths,
        TimeProvider time,
        ILogger<WorldSync> log)
    {
        _registry = registry;
        _restorer = restorer;
        _history = history;
        _artifacts = artifacts;
        _console = console;
        _packs = packs;
        _paths = paths;
        _time = time;
        _log = log;
    }

    /// <summary>How long players are warned before the sync stops this server.</summary>
    internal TimeSpan StopWarning { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a stop may take before the server is ended.</summary>
    internal TimeSpan DrainBudget { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a start may take to be ready.</summary>
    internal TimeSpan ReadyWithin { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Runs after the safety backup and before the world is replaced.</summary>
    /// <remarks>
    /// A seam for the states a sync meets between its check and its copy, such as a
    /// source backup pruned in between, which are races rather than states a test
    /// can arrange (<c>T176</c>).
    /// </remarks>
    internal Action? BeforeReplacing { get; set; }

    /// <summary>Raised with the server's id whenever its sync moves on.</summary>
    public event Action<string>? Changed;

    /// <summary>Where a server's saved pieces are kept between the stop and the put-back.</summary>
    public string PiecesDirectory(string instanceId) => Path.Combine(_paths.Data, "sync", instanceId);

    /// <summary>Where a sync that stopped before its pieces were put back is recorded, beside its pieces.</summary>
    public string WaitingFile(string instanceId) => Path.Combine(_paths.Data, "sync", $"{instanceId}.waiting.json");

    /// <summary>
    /// The sync that replaced this server's world and stopped before its Sync Areas were put
    /// back, or null when none waits.
    /// </summary>
    /// <remarks>
    /// On disk rather than in the run, so a wrapper restart does not lose the way back. A file
    /// that cannot be read is logged and treated as none: refusing every sync over it would
    /// leave nothing on the page to clear it with.
    /// </remarks>
    public UnfinishedSync? Waiting(string instanceId)
    {
        var path = WaitingFile(instanceId);

        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UnfinishedSync>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.LogWarning(ex, "Could not read {Path}", path);
            return null;
        }
    }

    /// <summary>Whether the waiting pieces can be put back now, and if not, why.</summary>
    public IReadOnlyList<string> CheckPutBack(string instanceId) => CheckPutBack(instanceId, ours: null);

    /// <param name="ours">The claim the put-back asking holds, which is no reason to refuse it.</param>
    private List<string> CheckPutBack(string instanceId, InstanceClaim? ours)
    {
        var target = _registry.Get(instanceId);

        if (Waiting(instanceId) is not { } waiting)
        {
            return [UnfinishedSyncText.NothingWaiting];
        }

        var problems = new List<string>();

        if (target.Definition.Retired)
        {
            problems.Add("This server is retired, so it cannot be started to put its Sync Areas back.");
        }

        if (IsRunning(instanceId))
        {
            problems.Add("A sync is already running on this server.");
        }
        else if (ours is null && target.Status.ClaimedFor is { } purpose)
        {
            problems.Add(ClaimText.Refusal(target.Definition.Name, purpose));
        }

        if (SyncText.Busy(target.Status.State) is { } busy)
        {
            problems.Add(busy);
        }

        if (WorldOf(target.WorkingDirectory).Reason is { } unknown)
        {
            problems.Add(unknown);
        }

        var folder = PiecesDirectory(instanceId);
        problems.AddRange(waiting.Plan.Problems);
        problems.AddRange(waiting.Plan.Pieces
            .Select(p => (p.Label, File: p.Piece.Name.Name + StructureFile.Extension))
            .Where(p => !File.Exists(Path.Combine(folder, p.File)))
            .Select(p => UnfinishedSyncText.Missing(p.Label, p.File, folder)));

        return problems;
    }

    /// <summary>
    /// Puts back the pieces a sync saved before it stopped, in the background: the sync's
    /// fifth step again, after stopping the server if it runs (<c>D103</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">It cannot start; the message says why.</exception>
    public async Task<SyncProgress> PutBackAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (CheckPutBack(instanceId) is { Count: > 0 } refused)
        {
            throw new InvalidOperationException(string.Join(" ", refused));
        }

        var target = _registry.Get(instanceId);
        var waiting = Waiting(instanceId)!;
        var claim = await target.ClaimAsync(ClaimText.Sync(waiting.SourceName), cancellationToken).ConfigureAwait(false);

        try
        {
            // Asked again under the claim, which nothing else can move now.
            if (CheckPutBack(instanceId, claim) is { Count: > 0 } now)
            {
                throw new InvalidOperationException(string.Join(" ", now));
            }

            var plan = waiting.Plan;
            var run = new SyncRun(
                instanceId, waiting.SourceName, waiting.BackupName, _time.GetUtcNow(),
                [.. plan.Pieces.Select(p => p.Label)], waiting.Areas);

            if (!_runs.TryAdd(instanceId, run)
                && !(_runs.TryGetValue(instanceId, out var last)
                     && last.Snapshot().Outcome != SyncOutcome.Running
                     && _runs.TryUpdate(instanceId, run, last)))
            {
                throw new InvalidOperationException("A sync is already running on this server.");
            }

            run.Task = Task.Run(() => PutBackClaimedAsync(run, target, plan, claim), CancellationToken.None);
            Publish(instanceId);
            return run.Snapshot();
        }
        catch
        {
            claim.Dispose();
            throw;
        }
    }

    /// <summary>Forgets the waiting pieces, so a new sync can run; the world stays as it is.</summary>
    /// <returns>False while a sync runs here, or when nothing waits.</returns>
    public bool DiscardWaiting(string instanceId)
    {
        if (IsRunning(instanceId) || Waiting(instanceId) is not { } waiting)
        {
            return false;
        }

        File.Delete(WaitingFile(instanceId));
        Notice(_registry.Get(instanceId), UnfinishedSyncText.Discarded(waiting.SourceName));
        Publish(instanceId);
        return true;
    }

    private async Task PutBackClaimedAsync(SyncRun run, InstanceSupervisor target, SyncPlan plan, InstanceClaim claim)
    {
        try
        {
            await PutBackWaitingAsync(run, target, plan, claim).ConfigureAwait(false);
        }
        finally
        {
            claim.Dispose();
            Publish(target.InstanceId);
        }
    }

    private async Task PutBackWaitingAsync(SyncRun run, InstanceSupervisor target, SyncPlan plan, InstanceClaim claim)
    {
        var pieces = plan.Pieces;
        var wasRunning = target.Status.State == InstanceState.Running;

        try
        {
            Notice(target, UnfinishedSyncText.Started(run.SourceName, pieces.Count));

            // The pack is only read at a start, so the server stops first.
            Enter(run, target, SyncStep.Stopping);

            if (target.Status.State != InstanceState.Stopped)
            {
                await StopIfRunningAsync(target, claim).ConfigureAwait(false);
            }

            if (target.Status.State != InstanceState.Stopped)
            {
                Incomplete(run, target, $"{target.Definition.Name} did not stop, so its pieces were not put in its pack.");
                return;
            }

            Enter(run, target, SyncStep.PuttingBack);
            var folder = PiecesDirectory(target.InstanceId);
            var files = pieces
                .Select(p => new StructurePackFile(
                    p.Piece.Name, File.ReadAllBytes(Path.Combine(folder, p.Piece.Name.Name + StructureFile.Extension))))
                .ToList();

            if (WorldOf(target.WorkingDirectory).Directory is not { } world)
            {
                await RestartIfAsync(wasRunning, target, claim).ConfigureAwait(false);
                Incomplete(run, target, WorldOf(target.WorkingDirectory).Reason!);
                return;
            }

            var placed = await _packs.PlaceAsync(world, files).ConfigureAwait(false);

            if (placed.State != StructurePackState.Placed)
            {
                await RestartIfAsync(wasRunning, target, claim).ConfigureAwait(false);
                Incomplete(run, target, placed.Reason ?? "The pieces could not be put in its structures pack.");
                return;
            }

            await StartAndPutBackAsync(run, target, plan, claim, wasRunning).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Putting back the Sync Areas of {InstanceId} failed", target.InstanceId);
            await RestartIfAsync(wasRunning, target, claim).ConfigureAwait(false);
            Incomplete(run, target, ex.Message);
        }
    }

    /// <summary>
    /// Step 5: starts the server with its structures pack, puts every piece back, and stops
    /// it again when it was stopped before. Shared by a sync and a put-back after one.
    /// </summary>
    private async Task StartAndPutBackAsync(
        SyncRun run, InstanceSupervisor target, SyncPlan plan, InstanceClaim claim, bool wasRunning)
    {
        var pieces = plan.Pieces;
        run.Starting(true);
        Publish(target.InstanceId);
        string? startFailure;

        try
        {
            // From the dispose on, a launch is not this one: a later start takes the pack off.
            using (KeepPackForLaunch(target.InstanceId))
            {
                startFailure = await StartAndWaitAsync(target, claim).ConfigureAwait(false);
            }
        }
        finally
        {
            run.Starting(false);
        }

        if (startFailure is not null)
        {
            Incomplete(run, target, startFailure);
            return;
        }

        run.BeginPass();
        Say(run, target, SyncStep.PuttingBack, SyncText.PuttingBack(run.SourceName, pieces.Count));

        var back = await _console
            .PutBackAsync(target, pieces, new Counter(run, saving: false, () => Publish(target.InstanceId)))
            .ConfigureAwait(false);

        if (back.State != StructurePassState.Finished)
        {
            Incomplete(run, target, Why(back, "put back", "Not every piece was put back."));
            return;
        }

        if (!wasRunning)
        {
            // Stopped before, so stopped after; the pack comes off at its next start.
            await StopIfRunningAsync(target, claim).ConfigureAwait(false);
        }

        Finished(run, target, plan);
    }

    /// <summary>The server's sync now, or its last; null when it has never synced since the wrapper started.</summary>
    public SyncProgress? Progress(string instanceId) =>
        _runs.TryGetValue(instanceId, out var run) ? run.Snapshot() : null;

    /// <summary>Whether a sync is running on the server.</summary>
    public bool IsRunning(string instanceId) =>
        _runs.TryGetValue(instanceId, out var run) && run.Snapshot().Outcome == SyncOutcome.Running;

    /// <summary>The background run, for a test to wait on.</summary>
    internal Task? Running(string instanceId) => _runs.TryGetValue(instanceId, out var run) ? run.Task : null;

    /// <summary>Whether a sync can start on <paramref name="instanceId"/>, and if not, why.</summary>
    public Task<SyncReadiness> CheckAsync(string instanceId, CancellationToken cancellationToken = default) =>
        CheckAsync(instanceId, ours: null, cancellationToken);

    /// <param name="ours">The claim the sync asking holds, which is no reason to refuse it.</param>
    private async Task<SyncReadiness> CheckAsync(
        string instanceId, InstanceClaim? ours, CancellationToken cancellationToken)
    {
        var target = _registry.Get(instanceId);
        var settings = target.Definition.Sync;
        var definition = target.Definition;
        var plan = SyncPlan.For(settings.KeptAreas);
        var problems = new List<string>(plan.Problems);
        InstanceSupervisor? source = null;
        RestorableBackup? backup = null;

        if (string.IsNullOrEmpty(settings.SourceInstanceId))
        {
            problems.Insert(0, "Choose the server this one takes its world from.");
        }
        else if (settings.SourceInstanceId == instanceId)
        {
            problems.Insert(0, "A server cannot take its world from itself.");
        }
        else if (!_registry.TryGet(settings.SourceInstanceId, out source) || source.Definition.Retired)
        {
            source = null;
            problems.Insert(0, "The server this one takes its world from is no longer here; choose another.");
        }
        else
        {
            backup = await NewestUsableBackupAsync(source, cancellationToken).ConfigureAwait(false);

            if (backup is null)
            {
                problems.Insert(0,
                    $"{source.Definition.Name} has no backup a sync can copy in: a ZIP archive or a folder copy "
                    + "holding its world. Its Backups tab takes one.");
            }
        }

        // Before anything stops, for `T168`'s reason: the answer costs no uptime here.
        if (_restorer.NeedFor(target, restoringConfig: false) is SafetyBackupNeed.WorldUnreadable)
        {
            problems.Insert(0, SyncText.WorldUnreadable);
        }

        if (SyncText.Busy(target.Status.State) is { } busy)
        {
            problems.Insert(0, busy);
        }

        if (definition.Retired)
        {
            problems.Insert(0, "This server is retired, so it cannot be started to sync.");
        }

        if (Waiting(instanceId) is { } waiting)
        {
            problems.Insert(0, UnfinishedSyncText.SyncRefused(waiting));
        }

        if (IsRunning(instanceId))
        {
            problems.Insert(0, "A sync is already running on this server.");
        }
        else if (ours is null && target.Status.ClaimedFor is { } purpose)
        {
            problems.Insert(0, ClaimText.Refusal(definition.Name, purpose));
        }

        return new SyncReadiness(target, source, backup, plan, problems);
    }

    /// <summary>Starts a sync in the background, and says how it begins.</summary>
    /// <exception cref="InvalidOperationException">It cannot start; the message says why.</exception>
    /// <remarks>
    /// The sync claims the server for its whole length (<see cref="InstanceClaim"/>):
    /// a scheduled restart, an automatic update or a scheduled backup that comes due
    /// meanwhile waits for it to end, and a start, stop, restore or update asked for
    /// meanwhile is refused.
    /// </remarks>
    public async Task<SyncProgress> StartAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var asked = await CheckAsync(instanceId, cancellationToken).ConfigureAwait(false);

        if (!asked.CanStart)
        {
            throw new InvalidOperationException(string.Join(" ", asked.Problems));
        }

        var claim = await asked.Target
            .ClaimAsync(ClaimText.Sync(asked.Source!.Definition.Name), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // Asked again under the claim, the answer the sync runs on: what the first
            // saw could move before the claim was taken, and nothing can move it now.
            var ready = await CheckAsync(instanceId, claim, cancellationToken).ConfigureAwait(false);

            if (!ready.CanStart)
            {
                throw new InvalidOperationException(string.Join(" ", ready.Problems));
            }

            var run = new SyncRun(
                instanceId, ready.Source!.Definition.Name, ready.Backup!.Name, _time.GetUtcNow(),
                [.. ready.Plan.Pieces.Select(p => p.Label)],
                [.. ready.Target.Definition.Sync.KeptAreas.Select(Copy)]);

            if (!_runs.TryAdd(instanceId, run)
                && !(_runs.TryGetValue(instanceId, out var last)
                     && last.Snapshot().Outcome != SyncOutcome.Running
                     && _runs.TryUpdate(instanceId, run, last)))
            {
                throw new InvalidOperationException("A sync is already running on this server.");
            }

            run.Task = Task.Run(() => RunClaimedAsync(run, ready, claim), CancellationToken.None);
            Publish(instanceId);
            return run.Snapshot();
        }
        catch
        {
            claim.Dispose();
            throw;
        }
    }

    /// <summary>Runs the sync and ends its claim, whatever the outcome.</summary>
    private async Task RunClaimedAsync(SyncRun run, SyncReadiness ready, InstanceClaim claim)
    {
        try
        {
            await RunAsync(run, ready, claim).ConfigureAwait(false);
        }
        finally
        {
            claim.Dispose();

            // Again after the claim ends, so a page that asked whether a sync can
            // start while it was still held asks once more.
            Publish(ready.Target.InstanceId);
        }
    }

    /// <summary>Takes an ended sync's progress away, so the strip it draws goes.</summary>
    /// <returns>False while it runs, when there is nothing to take away.</returns>
    public bool Dismiss(string instanceId)
    {
        if (!_runs.TryGetValue(instanceId, out var run) || run.Snapshot().Outcome == SyncOutcome.Running)
        {
            return false;
        }

        var dismissed = _runs.TryRemove(new KeyValuePair<string, SyncRun>(instanceId, run));
        Publish(instanceId);
        return dismissed;
    }

    /// <summary>
    /// Keeps the structures pack on the server's world for the launches made until the
    /// result is disposed: the start that puts a sync's or an area restore's pieces back
    /// (<c>D103</c>, <c>D108</c>).
    /// </summary>
    public IDisposable KeepPackForLaunch(string instanceId)
    {
        _keepPackForLaunch[instanceId] = 0;
        return new Kept(() => _keepPackForLaunch.TryRemove(instanceId, out _));
    }

    private sealed class Kept(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                release();
            }
        }
    }

    /// <summary>Takes the structures pack off a world before a launch, except while a sync or a restore waits for its own (<c>D103</c>).</summary>
    /// <remarks>
    /// The sync marks the server before its start and unmarks it once the start is
    /// ready, so a watchdog relaunch inside that wait keeps the pack too: it is the
    /// same start, made again.
    /// </remarks>
    public async Task BeforeLaunchAsync(InstanceSupervisor supervisor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(supervisor);

        if (_keepPackForLaunch.ContainsKey(supervisor.InstanceId))
        {
            return;
        }

        if (WorldOf(supervisor.WorkingDirectory).Directory is not { } world || !HoldsThePack(world))
        {
            return;
        }

        var cleared = await _packs.ClearAsync(world, cancellationToken).ConfigureAwait(false);

        Notice(supervisor, cleared.State == StructurePackState.Cleared
            ? $"Took {StructurePack.Name} off this world before starting: it only carries the pieces of a sync or "
              + "an area restore to the start that puts them back."
            : $"{StructurePack.Name} could not be taken off this world before starting: {cleared.Reason}");
    }

    private async Task RunAsync(SyncRun run, SyncReadiness ready, InstanceClaim claim)
    {
        var target = ready.Target;
        var source = ready.Source!.Definition.Name;
        var backup = ready.Backup!;
        var plan = ready.Plan;
        var pieces = plan.Pieces;
        var wasRunning = target.Status.State == InstanceState.Running;
        var worldReplaced = false;

        try
        {
            // 1. Save the kept pieces while the server runs.
            Say(run, target, SyncStep.Saving, SyncText.Started(source, plan));

            if (pieces.Count > 0)
            {
                if (target.Status.State != InstanceState.Running)
                {
                    run.Starting(true);
                    Publish(target.InstanceId);
                    var notStarted = await StartAndWaitAsync(target, claim).ConfigureAwait(false);
                    run.Starting(false);

                    if (notStarted is not null)
                    {
                        await StopIfRunningAsync(target, claim).ConfigureAwait(false);
                        CallOff(run, target, notStarted);
                        return;
                    }
                }

                run.BeginPass();
                Publish(target.InstanceId);
                var saved = await _console
                    .SaveAsync(target, pieces, new Counter(run, saving: true, () => Publish(target.InstanceId)))
                    .ConfigureAwait(false);

                if (saved.State != StructurePassState.Finished)
                {
                    if (!wasRunning)
                    {
                        await StopIfRunningAsync(target, claim).ConfigureAwait(false);
                    }

                    CallOff(run, target, Why(saved, "save", "Not every piece was saved."));
                    return;
                }
            }

            // 2. Stop, which lands every save on disk.
            Enter(run, target, SyncStep.Stopping);

            if (target.Status.State != InstanceState.Stopped)
            {
                Say(run, target, SyncStep.Stopping, SyncText.Stopping(pieces.Count));
                await StopIfRunningAsync(target, claim).ConfigureAwait(false);
            }

            if (target.Status.State != InstanceState.Stopped)
            {
                CallOff(run, target, $"{target.Definition.Name} did not stop, so its world was not read.");
                return;
            }

            // 3. Read the pieces out of the stopped world, into files outside it.
            Enter(run, target, SyncStep.Reading);
            var world = WorldOf(target.WorkingDirectory);

            if (world is not { Level: { } level, Directory: { } worldDirectory })
            {
                await RestartIfAsync(wasRunning, target, claim).ConfigureAwait(false);
                CallOff(run, target, world.Reason!);
                return;
            }

            var files = new List<StructurePackFile>(pieces.Count);

            if (pieces.Count > 0)
            {
                var read = ReadPieces(worldDirectory, pieces, PiecesDirectory(target.InstanceId));

                if (read.Failure is { } unread)
                {
                    await RestartIfAsync(wasRunning, target, claim).ConfigureAwait(false);
                    CallOff(run, target, unread);
                    return;
                }

                files = read.Files;
                Say(run, target, SyncStep.Reading, SyncText.Read(files.Count, files.Sum(f => (long)f.Bytes.Length)));
            }

            // 4. A safety backup, then the source's world under this server's level name.
            Enter(run, target, SyncStep.Replacing);
            var worlds = Path.Combine(target.WorkingDirectory, "worlds");
            var need = _restorer.NeedFor(target, restoringConfig: false);

            if (need is not (SafetyBackupNeed.Required or SafetyBackupNeed.NotNeeded))
            {
                await RestartIfAsync(wasRunning, target, claim).ConfigureAwait(false);
                CallOff(run, target, SyncText.WorldUnreadable);
                return;
            }

            SafetyBackup safety;

            try
            {
                // Under this server's own ZIP rule, as its other backups are (`D104`):
                // its compression, and its retention sparing what is locked.
                safety = need is SafetyBackupNeed.Required
                    ? await _restorer
                        .TakeSafetyBackupAsync(
                            target, worlds, $"Before syncing from {source}", CancellationToken.None,
                            target.Definition.Backup.Zip,
                            await _artifacts.LockedAsync(target.InstanceId).ConfigureAwait(false))
                        .ConfigureAwait(false)
                    : SafetyBackup.NothingToProtect;

                BeforeReplacing?.Invoke();
                ReplaceWorld(backup, worlds, level);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                _log.LogError(ex, "Sync of {InstanceId} from {Source} could not replace its world", target.InstanceId, source);
                await RestartIfAsync(wasRunning, target, claim).ConfigureAwait(false);
                CallOff(run, target, ex.Message);
                return;
            }

            worldReplaced = true;
            Say(run, target, SyncStep.Replacing,
                SyncText.Replaced(source, backup.Name, safety.Exists ? Path.GetFileName(safety.ArchivePath) : null));

            if (pieces.Count == 0)
            {
                await RestartIfAsync(wasRunning, target, claim).ConfigureAwait(false);
                Finished(run, target, plan);
                return;
            }

            var placed = await _packs.PlaceAsync(Path.Combine(worlds, level), files).ConfigureAwait(false);

            if (placed.State != StructurePackState.Placed)
            {
                await RestartIfAsync(wasRunning, target, claim).ConfigureAwait(false);
                Incomplete(run, target, placed.Reason ?? "The pieces could not be put in its structures pack.");
                return;
            }

            // 5. Start with the pack, and put the pieces back.
            Enter(run, target, SyncStep.PuttingBack);
            await StartAndPutBackAsync(run, target, plan, claim, wasRunning).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sync of {InstanceId} from {Source} failed", target.InstanceId, source);

            // Whatever went wrong, a server that was running is not left down for it.
            await RestartIfAsync(wasRunning, target, claim).ConfigureAwait(false);

            if (worldReplaced)
            {
                Incomplete(run, target, ex.Message);
            }
            else
            {
                CallOff(run, target, ex.Message);
            }
        }
    }

    /// <summary>Why a pass over the pieces did not finish, naming the piece it stopped at.</summary>
    private static string Why(StructurePassResult pass, string verb, string otherwise) =>
        pass.Pieces.FirstOrDefault(p => p.State == StructurePieceState.Failed) is { Reason: { } reason } failed
            ? $"Could not {verb} {failed.Piece.Label}: {reason}"
            : pass.Reason ?? otherwise;

    /// <summary>The source's newest backup whose world a sync can copy in.</summary>
    /// <remarks>
    /// The restore's own questions, in its order (<c>T251</c>, <c>T229</c>,
    /// <c>T236</c>): it can be opened, a folder copy holds a world, and the history
    /// does not record it as damaged; and it is not a safety backup
    /// (<see cref="SyncSource.IsSafetyBackup"/>). A newer one that fails any of
    /// them is passed over for an older one rather than refusing the sync.
    /// </remarks>
    private Task<RestorableBackup?> NewestUsableBackupAsync(InstanceSupervisor source, CancellationToken cancellationToken) =>
        new UsableBackups(_restorer, _history).NewestAsync(source.InstanceId, cancellationToken);

    /// <summary>Reads each piece out of a stopped world and keeps a copy in <paramref name="folder"/>, when one is given.</summary>
    internal static (List<StructurePackFile> Files, string? Failure) ReadPieces(
        string worldDirectory, IReadOnlyList<LabelledPiece> pieces, string? folder)
    {
        var open = WorldStructures.Open(worldDirectory);

        if (open.Database is not { } database)
        {
            return ([], $"the stopped world's database could not be read: {open.Reason}");
        }

        var files = new List<StructurePackFile>(pieces.Count);

        foreach (var piece in pieces)
        {
            var read = WorldStructures.Read(database, piece.Piece.Name);

            if (read.State != WorldStructureState.Found || read.Bytes is null)
            {
                // Absent from a stopped world means the save never happened (`D86`).
                return ([], read.State == WorldStructureState.Absent
                    ? $"{piece.Label} ({piece.Piece.Name}) is not in the stopped world, so its save never happened."
                    : $"{piece.Label} ({piece.Piece.Name}) could not be read out of the stopped world: {read.Reason}");
            }

            files.Add(new StructurePackFile(piece.Piece.Name, read.Bytes));
        }

        if (folder is null)
        {
            return (files, null);
        }

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);

        foreach (var file in files)
        {
            File.WriteAllBytes(Path.Combine(folder, file.Name.Name + StructureFile.Extension), file.Bytes);
        }

        return (files, null);
    }

    /// <summary>
    /// Puts the world a backup holds where <paramref name="level"/> is, leaving any
    /// other world in the folder alone.
    /// </summary>
    /// <remarks>
    /// <para>Extracted beside the server's <c>worlds</c> folder first, and swapped
    /// in by moves, so the world being replaced is untouched until the new one is
    /// whole. The backup's world is <see cref="WorldRestorer.WorldToTake"/>'s.</para>
    ///
    /// <para>The world moved aside is deleted once the new one is in place: the
    /// safety backup taken before this holds it.</para>
    /// </remarks>
    private void ReplaceWorld(RestorableBackup backup, string worldsDirectory, string level)
    {
        var stamp = _time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var staging = $"{worldsDirectory}.syncing-{stamp}";
        var aside = $"{worldsDirectory}.before-sync-{stamp}";

        try
        {
            WorldRestorer.Extract(backup, staging, EmptySnapshotStage.BesideTheWorld);

            var (from, refusal) = WorldRestorer.WorldToTake(backup, NewServerLevel.WorldsIn(staging));
            if (from is null)
            {
                throw new InvalidOperationException(refusal);
            }

            var target = Path.Combine(worldsDirectory, level);
            var setAside = Path.Combine(aside, level);
            var moved = false;

            if (Directory.Exists(target))
            {
                Directory.CreateDirectory(aside);
                Directory.Move(target, setAside);
                moved = true;
            }

            try
            {
                Directory.CreateDirectory(worldsDirectory);
                Directory.Move(Path.Combine(staging, from), target);
            }
            catch
            {
                if (moved && !Directory.Exists(target))
                {
                    Directory.Move(setAside, target);
                }

                throw;
            }
        }
        finally
        {
            TryDelete(staging);
        }

        TryDelete(aside);
    }

    private void RecordWaiting(string instanceId, UnfinishedSync waiting)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WaitingFile(instanceId))!);
            AtomicFile.WriteAllText(WaitingFile(instanceId), JsonSerializer.Serialize(waiting, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The pieces are still in the folder the notice names; only the button is lost.
            _log.LogError(ex, "Could not record the Sync Areas of {InstanceId} as waiting", instanceId);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    /// <summary>A copy of an area, so an edit made while the sync runs does not reach what it records.</summary>
    private static KeptAreaSettings Copy(KeptAreaSettings area) => new()
    {
        Name = area.Name,
        Dimension = area.Dimension,
        Color = area.Color,
        ShowInGame = area.ShowInGame,
        X1 = area.X1,
        Y1 = area.Y1,
        Z1 = area.Z1,
        X2 = area.X2,
        Y2 = area.Y2,
        Z2 = area.Z2,
    };

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not delete {Directory} after a sync", directory);
        }
    }

    /// <summary>The world a server's <c>server.properties</c> names, or why it cannot be told.</summary>
    /// <remarks>
    /// A missing file or a missing <c>level-name</c> is the world BDS makes by
    /// default, which is the one it would load.
    /// </remarks>
    internal static ServerWorld WorldOf(string workingDirectory)
    {
        var file = ServerProperties.ReadFile(ServerProperties.PathFor(workingDirectory), File.ReadAllLines);

        if (file.State == PropertiesReadState.Unreadable)
        {
            return new(null, null,
                $"{ServerProperties.FileName} could not be read, so which world is this server's is not known: {file.Why}");
        }

        var level = file.Values.TryGetValue("level-name", out var name) && name.Length > 0
            ? name
            : NewServerWorld.DefaultLevelName;

        return new(level, Path.Combine(workingDirectory, "worlds", level), null);
    }

    /// <summary>A server's world folder, or why it is not known.</summary>
    internal sealed record ServerWorld(string? Level, string? Directory, string? Reason);

    private static bool HoldsThePack(string world)
    {
        if (Directory.Exists(StructurePackWriter.PackDirectory(world)))
        {
            return true;
        }

        var list = Path.Combine(world, "world_behavior_packs.json");

        try
        {
            return File.Exists(list) && File.ReadAllText(list).Contains(StructurePack.Uuid, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts the server and waits for it; null when it is ready, else why not.</summary>
    private async Task<string?> StartAndWaitAsync(InstanceSupervisor target, InstanceClaim claim)
    {
        await claim.StartAsync(CancellationToken.None).ConfigureAwait(false);

        return await target.WaitForReadyOutcomeAsync(ReadyWithin).ConfigureAwait(false) switch
        {
            ReadyWait.Ready => null,
            ReadyWait.BudgetExpired => SyncText.NotReady(target.Definition.Name, ReadyWithin),
            ReadyWait.NotStarting => target.Status.FaultReason is { } reason
                ? $"{target.Definition.Name} did not start: {reason}"
                : $"{target.Definition.Name} stopped before it was ready.",
            var other => throw new ArgumentOutOfRangeException(nameof(target), other, "Unhandled wait outcome."),
        };
    }

    private async Task StopIfRunningAsync(InstanceSupervisor target, InstanceClaim claim)
    {
        if (target.Status.State is InstanceState.Stopped)
        {
            return;
        }

        var warnings = StopWarning > TimeSpan.Zero
            ? new[] { StopWarning, TimeSpan.FromSeconds(10) }.Where(w => w <= StopWarning).Distinct().ToArray()
            : [];

        await claim
            .StopAsync(new GracefulStopRequest(StopReason.Sync, StopWarning, warnings, DrainBudget), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task RestartIfAsync(bool wasRunning, InstanceSupervisor target, InstanceClaim claim)
    {
        if (wasRunning && target.Status.State != InstanceState.Running)
        {
            try
            {
                await claim.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Could not start {InstanceId} again after its sync", target.InstanceId);
            }
        }
    }

    private TimeSpan Took(SyncRun run) => _time.GetUtcNow() - run.StartedUtc;

    /// <summary>Moves the sync to <paramref name="step"/> before its work, so a failure in it is placed there.</summary>
    private void Enter(SyncRun run, InstanceSupervisor target, SyncStep step)
    {
        run.Enter(step);
        Publish(target.InstanceId);
    }

    private void Say(SyncRun run, InstanceSupervisor target, SyncStep step, string notice)
    {
        run.Update(step, notice);
        Notice(target, notice);
        Publish(target.InstanceId);
    }

    private void Finished(SyncRun run, InstanceSupervisor target, SyncPlan plan)
    {
        // Nothing waits any more, whether this was a sync or a put-back after one.
        TryDeleteFile(WaitingFile(target.InstanceId));

        var took = Took(run);
        End(run, target, SyncOutcome.Finished,
            SyncText.Finished(run.SourceName, run.BackupName, took, plan),
            SyncText.Outcome(run.SourceName, run.BackupName, plan));
    }

    private void CallOff(SyncRun run, InstanceSupervisor target, string reason) =>
        End(run, target, SyncOutcome.CalledOff, SyncText.CalledOff(run.SourceName, reason), reason);

    private void Incomplete(SyncRun run, InstanceSupervisor target, string reason)
    {
        var folder = PiecesDirectory(target.InstanceId);

        if (run.Areas.Count > 0)
        {
            RecordWaiting(target.InstanceId, new UnfinishedSync(
                run.SourceName, run.BackupName, _time.GetUtcNow(), reason, run.Areas));
        }

        End(run, target, SyncOutcome.Incomplete,
            SyncText.Incomplete(run.SourceName, reason, folder),
            SyncText.IncompleteDetail(reason, folder));
    }

    private void End(SyncRun run, InstanceSupervisor target, SyncOutcome outcome, string notice, string detail)
    {
        run.End(outcome, notice, detail, _time.GetUtcNow());
        Notice(target, notice);

        if (outcome == SyncOutcome.Finished)
        {
            _log.LogInformation("Sync of {InstanceId} from {Source} finished", target.InstanceId, run.SourceName);
        }
        else
        {
            _log.LogWarning("Sync of {InstanceId} from {Source} ended {Outcome}: {Notice}", target.InstanceId, run.SourceName, outcome, notice);
        }

        Publish(target.InstanceId);
    }

    private void Notice(InstanceSupervisor target, string text) =>
        target.Console.Append(ConsoleOrigin.Notice, text, _time.GetUtcNow());

    private void Publish(string instanceId)
    {
        try
        {
            Changed?.Invoke(instanceId);
        }
        catch (Exception ex)
        {
            // A page's handler failing must not take the sync down with it.
            _log.LogWarning(ex, "A sync progress handler for {InstanceId} threw", instanceId);
        }
    }

    /// <summary>Counts pieces as the console decides them.</summary>
    private sealed class Counter(SyncRun run, bool saving, Action publish) : IProgress<StructurePieceOutcome>
    {
        public void Report(StructurePieceOutcome value)
        {
            run.Count(value, saving);
            publish();
        }
    }

    /// <summary>One sync's progress, read whole by the page.</summary>
    private sealed class SyncRun(
        string instanceId,
        string sourceName,
        string backupName,
        DateTimeOffset startedUtc,
        IReadOnlyList<string> labels,
        IReadOnlyList<KeptAreaSettings> areas)
    {
        private readonly Lock _gate = new();
        private SyncStep _step = SyncStep.Saving;
        private SyncOutcome _outcome = SyncOutcome.Running;
        private int _saved;
        private int _putBack;
        private bool _inPass;
        private bool _starting;
        private string? _message;
        private string? _detail;
        private DateTimeOffset? _finishedUtc;

        public string SourceName { get; } = sourceName;

        public string BackupName { get; } = backupName;

        public DateTimeOffset StartedUtc { get; } = startedUtc;

        /// <summary>The Sync Areas as the run saved them, recorded if it stops before they are put back.</summary>
        public IReadOnlyList<KeptAreaSettings> Areas { get; } = areas;

        public Task? Task { get; set; }

        public void Enter(SyncStep step)
        {
            lock (_gate)
            {
                _step = step;
                _inPass = false;
            }
        }

        public void Update(SyncStep step, string message)
        {
            lock (_gate)
            {
                if (_step != step)
                {
                    _inPass = false;
                }

                _step = step;
                _message = message;
            }
        }

        public void Starting(bool starting)
        {
            lock (_gate)
            {
                _starting = starting;
            }
        }

        public void BeginPass()
        {
            lock (_gate)
            {
                _inPass = true;
            }
        }

        public void Count(StructurePieceOutcome piece, bool saving)
        {
            lock (_gate)
            {
                if (piece.State != StructurePieceState.Done)
                {
                    return;
                }

                if (saving)
                {
                    _saved++;
                }
                else
                {
                    _putBack++;
                }
            }
        }

        public void End(SyncOutcome outcome, string message, string detail, DateTimeOffset finishedUtc)
        {
            lock (_gate)
            {
                _outcome = outcome;
                _message = message;
                _detail = detail;
                _finishedUtc = finishedUtc;
                _inPass = false;
                _starting = false;
            }
        }

        public SyncProgress Snapshot()
        {
            lock (_gate)
            {
                // The piece under way: the first not yet done in the pass going on.
                var done = _step == SyncStep.Saving ? _saved : _putBack;
                var current = _inPass && _outcome == SyncOutcome.Running && done < labels.Count
                    ? labels[done]
                    : null;

                return new SyncProgress(
                    instanceId, SourceName, BackupName, StartedUtc, _finishedUtc, _step, _outcome,
                    labels.Count, _saved, _putBack, current, _starting, _message, _detail);
            }
        }
    }
}
