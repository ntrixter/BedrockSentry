using System.Collections.Concurrent;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Structures;
using BedrockSentry.Core.Sync;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Worlds;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Sync;

/// <summary>Whether an area restore can start, and what it would read.</summary>
/// <param name="Server">The server whose world changes.</param>
/// <param name="Backup">The backup chosen; null when none could be.</param>
/// <param name="Problems">Why it cannot start; empty when it can.</param>
/// <param name="Source">
/// The server the backup is of, when it is another (<c>D111</c>); null when it is <paramref name="Server"/>.
/// </param>
public sealed record AreaRestoreCheck(
    InstanceSupervisor Server,
    AreaRestorePlan Plan,
    RestorableBackup? Backup,
    IReadOnlyList<string> Problems,
    InstanceSupervisor? Source = null)
{
    public bool CanStart => Problems.Count == 0;

    /// <summary>What every sentence calls the backup: its name, after its server's when that is another.</summary>
    public string BackupLabel => AreaRestoreText.BackupLabel(Backup?.Name ?? string.Empty, Source?.Definition.Name);
}

/// <summary>The Bedrock build a server runs, as far as the wrapper knows it (<c>D111</c>).</summary>
public interface IInstanceBuilds
{
    /// <summary>The build, or null when nothing recorded or announced one.</summary>
    Version? BuildOf(InstanceSupervisor server);
}

/// <summary><see cref="IInstanceBuilds"/> over a function, for the container.</summary>
public sealed class InstanceBuilds(Func<InstanceSupervisor, Version?> buildOf) : IInstanceBuilds
{
    public Version? BuildOf(InstanceSupervisor server) => buildOf(server);
}

/// <summary>Puts one area of a server back from a backup, its own (<c>D108</c>) or another server's (<c>D111</c>).</summary>
/// <remarks>
/// <para><strong>Nothing of the server changes until every piece is a file outside it.</strong>
/// The area is read out of the backup on a throwaway copy (<see cref="IBackupAreaReader"/>)
/// while the server runs; a piece that cannot be read calls the restore off before the
/// countdown starts.</para>
///
/// <para>Then the server stops with <see cref="AreaRestoreText.Warning"/>'s warning, as a sync
/// stops, a safety backup is taken under its ZIP rule (<c>D104</c>), the pieces go into the
/// world's structures pack, and the start that follows puts them back through the console.
/// The pack comes off at the launch after (<see cref="WorldSync.KeepPackForLaunch"/>). A server
/// stopped before is stopped again after.</para>
///
/// <para>The restore claims the server for its whole length (<see cref="InstanceClaim"/>), so a
/// sync on it is refused meanwhile, and it is refused while a sync holds it. A sync reads
/// another server only through its backups, so a restore on a sync's source does not meet
/// it.</para>
///
/// <para><strong>The backup may be another server's</strong> (<c>D111</c>): a sync of one area,
/// or an area put onto another server from the Backups tab. The server whose world changes is
/// the one that is claimed, warned, stopped and backed up, and whose build reads the backup;
/// the other is only read through its backup. A server on an older build than the other is
/// refused before anything stops, as <c>D103</c> requires of a sync.</para>
/// </remarks>
/// <param name="builds">Each server's build, for that refusal; null knows none, and refuses nothing.</param>
public sealed class AreaRestore(
    InstanceRegistry registry,
    WorldSync sync,
    WorldRestorer restorer,
    BackupHistory history,
    BackupArtifactStore artifacts,
    IBackupAreaReader reader,
    StructureConsole console,
    StructurePackWriter packs,
    TimeProvider time,
    ILogger<AreaRestore> log,
    IInstanceBuilds? builds = null)
{
    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.Ordinal);

    /// <summary>The warning before the stop.</summary>
    internal TimeSpan StopWarning { get; init; } = AreaRestoreText.Warning;

    /// <summary>How long a stop may take before the server is ended.</summary>
    internal TimeSpan DrainBudget { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a start may take to be ready.</summary>
    internal TimeSpan ReadyWithin { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Raised with the server's id whenever its restore moves on.</summary>
    public event Action<string>? Changed;

    /// <summary>The server's restore now, or its last since the wrapper started.</summary>
    public AreaRestoreProgress? Progress(string instanceId) =>
        _runs.TryGetValue(instanceId, out var run) ? run.Progress : null;

    /// <summary>Whether a restore runs on the server.</summary>
    public bool IsRunning(string instanceId) =>
        _runs.TryGetValue(instanceId, out var run) && run.Progress.Outcome == AreaRestoreOutcome.Running;

    /// <summary>Takes an ended restore's progress away, so the strip it draws goes (<c>D111</c>).</summary>
    /// <returns>False while it runs, when there is nothing to take away.</returns>
    public bool Dismiss(string instanceId)
    {
        if (_runs.TryGetValue(instanceId, out var run)
            && run.Progress.Outcome != AreaRestoreOutcome.Running
            && _runs.TryRemove(new KeyValuePair<string, Run>(instanceId, run)))
        {
            Publish(instanceId);
            return true;
        }

        return false;
    }

    /// <summary>The background run, for a test to wait on.</summary>
    internal Task? Running(string instanceId) => _runs.TryGetValue(instanceId, out var run) ? run.Task : null;

    /// <summary>Whether a restore of <paramref name="target"/> from <paramref name="backup"/> can start now.</summary>
    /// <param name="instanceId">The server whose world changes.</param>
    /// <param name="backup">A backup's name on disk, <see cref="BackupChoice.Latest"/> or null.</param>
    /// <param name="from">The server whose backups are read; null or <paramref name="instanceId"/> for its own.</param>
    public Task<AreaRestoreCheck> CheckAsync(
        string instanceId, AreaRestoreTarget target, string? backup, string? from = null, CancellationToken cancellationToken = default) =>
        CheckAsync(instanceId, target, backup, from, ours: false, cancellationToken);

    private async Task<AreaRestoreCheck> CheckAsync(
        string instanceId, AreaRestoreTarget target, string? backup, string? from, bool ours, CancellationToken cancellationToken)
    {
        var server = registry.Get(instanceId);
        var definition = server.Definition;
        var plan = AreaRestorePlan.For(target, definition.Sync.KeptAreas);
        var problems = new List<string>(plan.Problems);
        InstanceSupervisor? source = null;
        RestorableBackup? found = null;

        if (!string.IsNullOrEmpty(from) && !string.Equals(from, instanceId, StringComparison.Ordinal)
            && !(registry.TryGet(from, out source) && !source.Definition.Retired))
        {
            source = null;
            problems.Insert(0, AreaRestoreText.SourceGone);
        }
        else
        {
            var owner = source?.InstanceId ?? instanceId;
            var candidates = (await new UsableBackups(restorer, history).ListAsync(owner, cancellationToken).ConfigureAwait(false)).ToList();
            var chosen = BackupChoice.Pick([.. candidates.Select(c => c.Candidate)], backup, source?.Definition.Name);
            found = chosen.Name is { } name ? candidates.First(c => c.Candidate.Name == name).Backup : null;

            if (chosen.Refusal is { } refusal)
            {
                problems.Insert(0, refusal);
            }
        }

        // `D103`'s rule for a sync, asked before anything stops: an older build is not handed a
        // newer world's blocks. Unknown on either side refuses nothing, as a sync refuses nothing.
        if (source is not null
            && builds?.BuildOf(server) is { } serverBuild
            && builds.BuildOf(source) is { } sourceBuild
            && serverBuild < sourceBuild)
        {
            problems.Insert(0, AreaRestoreText.OlderBuild(
                definition.Name, serverBuild.ToString(), source.Definition.Name, sourceBuild.ToString()));
        }

        // Before anything stops, for `T168`'s reason: the answer costs no uptime here.
        if (restorer.NeedFor(server, restoringConfig: false) is SafetyBackupNeed.WorldUnreadable)
        {
            problems.Insert(0, SyncText.WorldUnreadable);
        }

        if (AreaRestoreText.Busy(server.Status.State) is { } busy)
        {
            problems.Insert(0, busy);
        }

        if (definition.Retired)
        {
            problems.Insert(0, "This server is retired, so it cannot be started to restore an area.");
        }

        if (IsRunning(instanceId) && !ours)
        {
            problems.Insert(0, AreaRestoreText.AlreadyRunning);
        }
        else if (sync.IsRunning(instanceId))
        {
            problems.Insert(0, AreaRestoreText.SyncRunning);
        }
        else if (!ours && server.Status.ClaimedFor is { } purpose)
        {
            problems.Insert(0, ClaimText.Refusal(definition.Name, purpose));
        }

        return new AreaRestoreCheck(server, plan, found, problems, source);
    }

    /// <summary>Starts a restore in the background, and says how it begins.</summary>
    /// <exception cref="InvalidOperationException">It cannot start; the message says why.</exception>
    /// <param name="from">The server whose backups are read; null or <paramref name="instanceId"/> for its own.</param>
    public async Task<AreaRestoreProgress> StartAsync(
        string instanceId, AreaRestoreTarget target, string? backup, string? from = null, CancellationToken cancellationToken = default)
    {
        var asked = await CheckAsync(instanceId, target, backup, from, cancellationToken).ConfigureAwait(false);

        if (!asked.CanStart)
        {
            throw new InvalidOperationException(string.Join(" ", asked.Problems));
        }

        var claim = await asked.Server
            .ClaimAsync(ClaimText.AreaRestore(asked.Plan.Title), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var run = new Run(new AreaRestoreProgress(
                instanceId, asked.Plan.Title, asked.BackupLabel, time.GetUtcNow(), null,
                AreaRestoreStep.Reading, AreaRestoreOutcome.Running, null));

            if (!_runs.TryAdd(instanceId, run)
                && !(_runs.TryGetValue(instanceId, out var last)
                     && last.Progress.Outcome != AreaRestoreOutcome.Running
                     && _runs.TryUpdate(instanceId, run, last)))
            {
                throw new InvalidOperationException(AreaRestoreText.AlreadyRunning);
            }

            // Asked again under the claim, the answer the restore runs on: what the first saw
            // could move before the claim was taken, and nothing can move it now.
            var ready = await CheckAsync(instanceId, target, backup, from, ours: true, cancellationToken).ConfigureAwait(false);

            if (!ready.CanStart)
            {
                _runs.TryRemove(new KeyValuePair<string, Run>(instanceId, run));
                throw new InvalidOperationException(string.Join(" ", ready.Problems));
            }

            run.Task = Task.Run(async () =>
            {
                using (claim)
                {
                    await RunAsync(run, ready, claim).ConfigureAwait(false);
                }
            });

            return run.Progress;
        }
        catch
        {
            claim.Dispose();
            throw;
        }
    }

    private async Task RunAsync(Run run, AreaRestoreCheck ready, InstanceClaim held)
    {
        var server = ready.Server;
        var plan = ready.Plan;
        var backup = ready.Backup!;
        var label = ready.BackupLabel;
        var pieces = plan.Pieces;
        var wasRunning = server.Status.State == InstanceState.Running;
        var stopped = false;

        try
        {
            // 1. Read the area out of the backup on a copy, while this server runs.
            Say(run, server, AreaRestoreStep.Reading, AreaRestoreText.Started(plan, label));

            var read = await reader
                .ReadAsync(server, backup, pieces, new Notices(this, server), CancellationToken.None)
                .ConfigureAwait(false);

            if (read.Files is not { } files)
            {
                CallOff(run, server, plan, Sentence(read.Failure));
                return;
            }

            Say(run, server, AreaRestoreStep.Reading, AreaRestoreText.Read(files.Count, files.Sum(f => (long)f.Bytes.Length)));

            // 2. Stop, with the countdown's warning.
            Say(run, server, AreaRestoreStep.Stopping, AreaRestoreText.Stopping(wasRunning));
            await StopIfRunningAsync(server, held).ConfigureAwait(false);
            stopped = true;

            if (server.Status.State != InstanceState.Stopped)
            {
                CallOff(run, server, plan, $"{server.Definition.Name} did not stop.");
                return;
            }

            // 3. A safety backup, then the pieces into the world's structures pack.
            Enter(run, server, AreaRestoreStep.Placing);
            var world = WorldSync.WorldOf(server.WorkingDirectory);

            if (world is not { Directory: { } worldDirectory })
            {
                await RestartIfAsync(wasRunning, server, held).ConfigureAwait(false);
                CallOff(run, server, plan, world.Reason!);
                return;
            }

            var need = restorer.NeedFor(server, restoringConfig: false);

            if (need is not (SafetyBackupNeed.Required or SafetyBackupNeed.NotNeeded))
            {
                await RestartIfAsync(wasRunning, server, held).ConfigureAwait(false);
                CallOff(run, server, plan, SyncText.WorldUnreadable);
                return;
            }

            SafetyBackup safety;

            try
            {
                // Under this server's own ZIP rule, as its other backups are (`D104`).
                safety = need is SafetyBackupNeed.Required
                    ? await restorer
                        .TakeSafetyBackupAsync(
                            server, Path.Combine(server.WorkingDirectory, "worlds"), $"Before restoring {plan.Title}",
                            CancellationToken.None, server.Definition.Backup.Zip,
                            await artifacts.LockedAsync(server.InstanceId).ConfigureAwait(false))
                        .ConfigureAwait(false)
                    : SafetyBackup.NothingToProtect;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                log.LogError(ex, "The safety backup before restoring an area of {InstanceId} failed", server.InstanceId);
                await RestartIfAsync(wasRunning, server, held).ConfigureAwait(false);
                CallOff(run, server, plan, $"the safety backup could not be taken: {ex.Message}");
                return;
            }

            var placed = await packs.PlaceAsync(worldDirectory, files).ConfigureAwait(false);

            if (placed.State != StructurePackState.Placed)
            {
                // The pack comes off at the start that follows, so the world is as it was.
                await RestartIfAsync(wasRunning, server, held).ConfigureAwait(false);
                CallOff(run, server, plan, placed.Reason ?? "the pieces could not be put in the world's structures pack.");
                return;
            }

            Say(run, server, AreaRestoreStep.Placing,
                AreaRestoreText.Placed(safety.Exists ? Path.GetFileName(safety.ArchivePath) : null));

            // 4. Start with the pack, and put the pieces back.
            Enter(run, server, AreaRestoreStep.PuttingBack);
            string? startFailure;

            // From the dispose on, a launch is not this one: a later start takes the pack off.
            using (sync.KeepPackForLaunch(server.InstanceId))
            {
                startFailure = await StartAndWaitAsync(server, held).ConfigureAwait(false);
            }

            if (startFailure is not null)
            {
                Incomplete(run, server, plan, startFailure);
                return;
            }

            Say(run, server, AreaRestoreStep.PuttingBack, AreaRestoreText.PuttingBack(pieces.Count));

            var back = await console.PutBackAsync(server, pieces).ConfigureAwait(false);

            if (back.State != StructurePassState.Finished)
            {
                Incomplete(run, server, plan, back.Reason ?? "Not every piece was put back.");
                return;
            }

            if (!wasRunning)
            {
                // Stopped before the restore, so stopped after it; the pack comes off at its next start.
                await StopIfRunningAsync(server, held).ConfigureAwait(false);
            }

            End(run, server, AreaRestoreOutcome.Finished,
                AreaRestoreText.Finished(plan, label, time.GetUtcNow() - run.Progress.StartedUtc));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Restoring {Title} on {InstanceId} failed", plan.Title, server.InstanceId);

            // Whatever went wrong, a server that was running is not left down for it.
            await RestartIfAsync(wasRunning, server, held).ConfigureAwait(false);

            if (stopped)
            {
                Incomplete(run, server, plan, Sentence(ex.Message));
            }
            else
            {
                CallOff(run, server, plan, Sentence(ex.Message));
            }
        }

    }

    private static string Sentence(string? text)
    {
        var trimmed = (text ?? "it failed").Trim();
        return trimmed.EndsWith('.') ? trimmed : trimmed + ".";
    }

    private async Task<string?> StartAndWaitAsync(InstanceSupervisor server, InstanceClaim claim)
    {
        await claim.StartAsync(CancellationToken.None).ConfigureAwait(false);

        return await server.WaitForReadyOutcomeAsync(ReadyWithin).ConfigureAwait(false) switch
        {
            ReadyWait.Ready => null,
            ReadyWait.BudgetExpired => SyncText.NotReady(server.Definition.Name, ReadyWithin),
            ReadyWait.NotStarting => server.Status.FaultReason is { } reason
                ? $"{server.Definition.Name} did not start: {reason}"
                : $"{server.Definition.Name} stopped before it was ready.",
            var other => throw new ArgumentOutOfRangeException(nameof(server), other, "Unhandled wait outcome."),
        };
    }

    private async Task StopIfRunningAsync(InstanceSupervisor server, InstanceClaim claim)
    {
        if (server.Status.State is InstanceState.Stopped)
        {
            return;
        }

        var warnings = StopWarning > TimeSpan.Zero
            ? new[] { StopWarning, TimeSpan.FromSeconds(10) }.Where(w => w <= StopWarning).Distinct().ToArray()
            : [];

        await claim
            .StopAsync(new GracefulStopRequest(StopReason.AreaRestore, StopWarning, warnings, DrainBudget), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task RestartIfAsync(bool wasRunning, InstanceSupervisor server, InstanceClaim claim)
    {
        if (wasRunning && server.Status.State != InstanceState.Running)
        {
            try
            {
                await claim.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Could not start {InstanceId} again after restoring an area", server.InstanceId);
            }
        }
    }

    private void Enter(Run run, InstanceSupervisor server, AreaRestoreStep step)
    {
        run.Progress = run.Progress with { Step = step };
        Publish(server.InstanceId);
    }

    private void Say(Run run, InstanceSupervisor server, AreaRestoreStep step, string notice)
    {
        run.Progress = run.Progress with { Step = step, Message = notice };
        Notice(server, notice);
        Publish(server.InstanceId);
    }

    private void CallOff(Run run, InstanceSupervisor server, AreaRestorePlan plan, string reason) =>
        End(run, server, AreaRestoreOutcome.CalledOff, AreaRestoreText.CalledOff(plan.Title, reason));

    private void Incomplete(Run run, InstanceSupervisor server, AreaRestorePlan plan, string reason) =>
        End(run, server, AreaRestoreOutcome.Incomplete, AreaRestoreText.Incomplete(plan.Title, Sentence(reason)));

    private void End(Run run, InstanceSupervisor server, AreaRestoreOutcome outcome, string notice)
    {
        run.Progress = run.Progress with { Outcome = outcome, Message = notice, FinishedUtc = time.GetUtcNow() };
        Notice(server, notice);

        if (outcome == AreaRestoreOutcome.Finished)
        {
            log.LogInformation("{Notice} ({InstanceId})", notice, server.InstanceId);
        }
        else
        {
            log.LogWarning("{Notice} ({InstanceId})", notice, server.InstanceId);
        }

        Publish(server.InstanceId);
    }

    private void Notice(InstanceSupervisor server, string text) =>
        server.Console.Append(ConsoleOrigin.Notice, text, time.GetUtcNow());

    private void Publish(string instanceId)
    {
        try
        {
            Changed?.Invoke(instanceId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "An area restore progress handler for {InstanceId} threw", instanceId);
        }
    }

    private sealed class Run(AreaRestoreProgress progress)
    {
        private AreaRestoreProgress _progress = progress;

        public AreaRestoreProgress Progress
        {
            get => Volatile.Read(ref _progress);
            set => Volatile.Write(ref _progress, value);
        }

        public Task? Task { get; set; }
    }

    /// <summary>Each piece read on the copy, said on the server's own console.</summary>
    private sealed class Notices(AreaRestore owner, InstanceSupervisor server) : IProgress<string>
    {
        public void Report(string value) => owner.Notice(server, $"Read {value} from the backup.");
    }
}
