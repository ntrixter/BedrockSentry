using System.Collections.Concurrent;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Sync;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// A restore of a whole world, run in the background with its steps kept for the strip under
/// the server's controls (<c>D111</c>).
/// </summary>
/// <remarks>
/// <para>The restore itself is <see cref="WorldRestorer.RestoreAsync"/>, unchanged in what it
/// does; this keeps where it is, so a page that is reloaded or left still shows it, and says
/// how it ended until it is dismissed or another begins.</para>
///
/// <para>One per server at a time. A restore that is refused before anything changes ends
/// <see cref="SyncOutcome.CalledOff"/> with the refusal as its sentence.</para>
/// </remarks>
public sealed class WorldRestoreRuns(WorldRestorer restorer, TimeProvider time, ILogger<WorldRestoreRuns> log)
{
    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.Ordinal);

    /// <summary>Raised with the server's id whenever its restore moves on.</summary>
    public event Action<string>? Changed;

    /// <summary>The server's restore now, or its last since the wrapper started and before it was dismissed.</summary>
    public WorldRestoreProgress? Progress(string instanceId) =>
        _runs.TryGetValue(instanceId, out var run) ? run.Progress : null;

    /// <summary>Whether a restore runs on the server.</summary>
    public bool IsRunning(string instanceId) =>
        _runs.TryGetValue(instanceId, out var run) && run.Progress.Outcome == SyncOutcome.Running;

    /// <summary>The background run, for a test to wait on.</summary>
    internal Task? Running(string instanceId) => _runs.TryGetValue(instanceId, out var run) ? run.Task : null;

    /// <summary>Takes an ended restore's progress away, so the strip it draws goes.</summary>
    /// <returns>False while it runs, when there is nothing to take away.</returns>
    public bool Dismiss(string instanceId)
    {
        if (_runs.TryGetValue(instanceId, out var run)
            && run.Progress.Outcome != SyncOutcome.Running
            && _runs.TryRemove(new KeyValuePair<string, Run>(instanceId, run)))
        {
            Publish(instanceId);
            return true;
        }

        return false;
    }

    /// <summary>Starts a restore of <paramref name="archivePath"/> over the server's world, and says how it begins.</summary>
    /// <exception cref="InvalidOperationException">A restore already runs on the server.</exception>
    public WorldRestoreProgress Start(
        InstanceSupervisor supervisor, string archivePath, bool restoreConfig, bool restoreAddons)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentException.ThrowIfNullOrEmpty(archivePath);

        var id = supervisor.InstanceId;
        var wasRunning = supervisor.Status.State is Core.Instances.InstanceState.Running;

        var run = new Run(new WorldRestoreProgress(
            id, Path.GetFileName(archivePath), wasRunning, time.GetUtcNow(), null,
            wasRunning ? WorldRestoreStep.Stopping : WorldRestoreStep.SafetyBackup,
            WorldReplaced: false, SyncOutcome.Running, null));

        if (!_runs.TryAdd(id, run)
            && !(_runs.TryGetValue(id, out var last)
                 && last.Progress.Outcome != SyncOutcome.Running
                 && _runs.TryUpdate(id, run, last)))
        {
            throw new InvalidOperationException("A restore already runs on this server; try again once it ends.");
        }

        run.Task = Task.Run(() => RunAsync(run, supervisor, archivePath, restoreConfig, restoreAddons), CancellationToken.None);
        Publish(id);
        return run.Progress;
    }

    private async Task RunAsync(
        Run run, InstanceSupervisor supervisor, string archivePath, bool restoreConfig, bool restoreAddons)
    {
        var id = supervisor.InstanceId;

        try
        {
            var result = await restorer
                .RestoreAsync(
                    supervisor, archivePath, restoreConfig, restoreAddons, CancellationToken.None,
                    new Steps(this, run, id))
                .ConfigureAwait(false);

            End(run, id, SyncOutcome.Finished, result.Summary());
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Restoring {Archive} onto {InstanceId} failed", Path.GetFileName(archivePath), id);

            var replaced = run.Progress.WorldReplaced;
            End(run, id, replaced ? SyncOutcome.Incomplete : SyncOutcome.CalledOff, WorldRestoreText.Failed(ex.Message, replaced));
        }
    }

    private void End(Run run, string instanceId, SyncOutcome outcome, string detail)
    {
        run.Progress = run.Progress with { Outcome = outcome, Detail = detail, FinishedUtc = time.GetUtcNow() };
        Publish(instanceId);
    }

    private void Publish(string instanceId)
    {
        try
        {
            Changed?.Invoke(instanceId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "A restore progress handler for {InstanceId} threw", instanceId);
        }
    }

    private sealed class Run(WorldRestoreProgress progress)
    {
        private WorldRestoreProgress _progress = progress;

        public WorldRestoreProgress Progress
        {
            get => Volatile.Read(ref _progress);
            set => Volatile.Write(ref _progress, value);
        }

        public Task? Task { get; set; }
    }

    /// <summary>Each step the restorer reports, kept on the run as it is said.</summary>
    /// <remarks>Synchronous, unlike <see cref="System.Progress{T}"/>, so the step a failure lands in is the step it failed in.</remarks>
    private sealed class Steps(WorldRestoreRuns owner, Run run, string instanceId) : IProgress<WorldRestoreReport>
    {
        public void Report(WorldRestoreReport value)
        {
            run.Progress = run.Progress with
            {
                Step = value.Step,
                WorldReplaced = run.Progress.WorldReplaced || value.WorldReplaced,
            };

            owner.Publish(instanceId);
        }
    }
}
