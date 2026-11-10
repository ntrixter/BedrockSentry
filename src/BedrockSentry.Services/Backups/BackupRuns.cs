using System.Collections.Concurrent;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// Where each server's backup is, kept for the strip under its controls (<c>D113</c>).
/// </summary>
/// <remarks>
/// <para><see cref="BackupOrchestrator"/> writes here as it moves on, so a backup shows whoever
/// asked for it: the Back up now button, the schedule, a join or a leave, or a stop. One taken
/// inside a job that claims the server, such as a restore's safety backup, is a step of that
/// job's strip and is not kept here.</para>
///
/// <para>One per server at a time, as the orchestrator's gate already makes it. An ended backup
/// is kept until it is dismissed or the next one begins when
/// <see cref="BackupProgress.KeptWhenEnded"/> says so, and dropped as it ends otherwise.</para>
/// </remarks>
public sealed class BackupRuns(TimeProvider time, ILogger<BackupRuns>? log = null)
{
    private readonly ILogger _log = log ?? (ILogger)NullLogger.Instance;

    private DateTimeOffset Now => time.GetUtcNow();

    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised with the server's id whenever its backup moves to another step or ends.</summary>
    public event Action<string>? Changed;

    /// <summary>The server's backup now, or its last kept one since the wrapper started.</summary>
    public BackupProgress? Progress(string instanceId) =>
        _runs.TryGetValue(instanceId, out var run) ? run.Read() : null;

    /// <summary>Whether a backup runs on the server.</summary>
    public bool IsRunning(string instanceId) =>
        Progress(instanceId) is { Outcome: SyncOutcome.Running };

    /// <summary>Takes an ended backup's strip away.</summary>
    /// <returns>False while it runs, when there is nothing to take away.</returns>
    public bool Dismiss(string instanceId)
    {
        if (_runs.TryGetValue(instanceId, out var run)
            && run.Read().Outcome != SyncOutcome.Running
            && _runs.TryRemove(new KeyValuePair<string, Run>(instanceId, run)))
        {
            Publish(instanceId);
            return true;
        }

        return false;
    }

    /// <summary>Starts keeping a backup that begins now, replacing whatever ended before it.</summary>
    internal Run Begin(string instanceId, BackupReason reason, bool live, bool snapshot)
    {
        var run = new Run(this, instanceId, new BackupProgress(
            instanceId, reason, live, live && snapshot, time.GetUtcNow(), null,
            live ? BackupStep.Pausing : BackupStep.Copying,
            FilesDone: 0, FilesOf: 0, Copiers: 0,
            SyncOutcome.Running, null, null, null));

        _runs[instanceId] = run;
        Publish(instanceId);
        return run;
    }

    private void Publish(string instanceId)
    {
        try
        {
            Changed?.Invoke(instanceId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A backup progress handler for {InstanceId} threw", instanceId);
        }
    }

    /// <summary>One backup, as the orchestrator tells it.</summary>
    internal sealed class Run(BackupRuns owner, string instanceId, BackupProgress progress)
    {
        private readonly object _lock = new();
        private BackupProgress _progress = progress;
        private int _filesDone;

        public BackupProgress Read()
        {
            lock (_lock)
            {
                return _progress with { FilesDone = Volatile.Read(ref _filesDone) };
            }
        }

        /// <summary>The step now under way.</summary>
        public void Step(BackupStep step) => _ = Change(p => p with { Step = step });

        /// <summary>The shadow copy could not be taken, so the run reads the live world.</summary>
        public void FellBack() => _ = Change(p => p with { Snapshot = false });

        /// <summary>The copy begins: <paramref name="files"/> world files, each passed by <paramref name="copiers"/> strategies.</summary>
        public void Copying(int files, int copiers) =>
            _ = Change(p => p with { Step = BackupStep.Copying, FilesOf = files * copiers, Copiers = copiers });

        /// <summary>One strategy passed one file; from several threads at once, and published by the page's own tick.</summary>
        public void FileDone() => Interlocked.Increment(ref _filesDone);

        /// <summary>The backup ended with <paramref name="record"/>.</summary>
        public void End(BackupRecord record) => Finish(p =>
        {
            var outcome = record.Outcome();

            return p with
            {
                FinishedUtc = owner.Now,
                Outcome = BackupProgressText.OutcomeOf(outcome.Severity),
                Severity = outcome.Severity,
                Label = outcome.Label,
                Detail = outcome.Detail,
                EndedAt = BackupProgress.EndedAtFor(record, p.Live),
            };
        });

        /// <summary>The backup was cancelled before it ended, and leaves no record.</summary>
        public void Cancelled() => Finish(p => p with
        {
            FinishedUtc = owner.Now,
            Outcome = SyncOutcome.CalledOff,
            Severity = null,
            Label = null,
            Detail = BackupProgressText.Cancelled,
            EndedAt = p.Step,
        });

        /// <summary>The backup threw past its record, which then was not written.</summary>
        public void Failed(string reason) => Finish(p => p with
        {
            FinishedUtc = owner.Now,
            Outcome = SyncOutcome.Incomplete,
            Severity = BackupSeverity.Failed,
            Label = "Failed",
            Detail = string.IsNullOrWhiteSpace(reason) ? "The backup stopped with an error." : reason,
            EndedAt = p.Step,
        });

        private void Finish(Func<BackupProgress, BackupProgress> change)
        {
            // A strip that could not be ended is taken away rather than left reading
            // "under way", which would hold Back up now off until the wrapper restarts.
            if (!Change(change) || !Read().KeptWhenEnded)
            {
                owner._runs.TryRemove(new KeyValuePair<string, Run>(instanceId, this));
                owner.Publish(instanceId);
            }
        }

        /// <remarks>
        /// Never throws, and that is load-bearing: the orchestrator calls <see cref="Step"/> inside
        /// the finally that sends <c>save resume</c>, ahead of the send, and a strip that threw
        /// there would leave the server holding saves for the sake of a sentence.
        /// </remarks>
        private bool Change(Func<BackupProgress, BackupProgress> change)
        {
            try
            {
                lock (_lock)
                {
                    _progress = change(_progress);
                }
            }
            catch (Exception ex)
            {
                owner._log.LogWarning(ex, "The backup strip for {InstanceId} could not be updated", instanceId);
                return false;
            }

            owner.Publish(instanceId);
            return true;
        }
    }
}
