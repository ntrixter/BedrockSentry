using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Sync;

namespace BedrockSentry.Core.Backups;

/// <summary>The steps of a backup (<c>D113</c>); which of them a run lists depends on how it reads the world.</summary>
public enum BackupStep
{
    /// <summary><c>save hold</c>, the wait for the save's file list and for the write the hold sets off; a running server only.</summary>
    Pausing = 1,

    /// <summary>Taking the shadow copy the acquisition toggle asked for (<c>D66</c>).</summary>
    Snapshot = 2,

    /// <summary>Every enabled strategy copying the world.</summary>
    Copying = 3,

    /// <summary><c>save resume</c> and its confirmation; a running server only.</summary>
    Resuming = 4,
}

/// <summary>Where a backup is, as of the moment it was read (<c>D113</c>).</summary>
/// <param name="Live">Whether the server ran when the backup began, which decides whether saves are paused.</param>
/// <param name="Snapshot">
/// Whether the run reads a shadow copy: asked for and not fallen back from. With one, saves resume
/// before the copy rather than after it, so the steps come in another order.
/// </param>
/// <param name="FilesDone">World files every copying strategy has passed, summed across them.</param>
/// <param name="FilesOf">World files every copying strategy will pass, summed; zero until the list is known.</param>
/// <param name="Copiers">How many strategies copy the files themselves, which <see cref="FilesOf"/> counts per file.</param>
/// <param name="Severity">How it ended, from its record; null while it runs or when it was cancelled.</param>
/// <param name="Label">The record's label, such as <c>OK</c>; null while it runs.</param>
/// <param name="Detail">How it ended, in a sentence; null while it runs.</param>
/// <param name="EndedAt">
/// The step an ended backup is marked against, or null when every step is done: a warning about
/// the resume is the resume's, a failure is the copy's or, before any copy, the pause's.
/// </param>
public sealed record BackupProgress(
    string InstanceId,
    BackupReason Reason,
    bool Live,
    bool Snapshot,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    BackupStep Step,
    int FilesDone,
    int FilesOf,
    int Copiers,
    SyncOutcome Outcome,
    BackupSeverity? Severity,
    string? Label,
    string? Detail,
    BackupStep? EndedAt = null)
{
    /// <summary>The step an ended backup with <paramref name="record"/> is marked against.</summary>
    public static BackupStep? EndedAtFor(BackupRecord record, bool live) => record.Outcome().Severity switch
    {
        BackupSeverity.Failed when record.Strategies.Count > 0 || !live => BackupStep.Copying,
        BackupSeverity.Failed => BackupStep.Pausing,
        BackupSeverity.Warning when record.SavesResumed is SaveResumeOutcome.Unconfirmed or SaveResumeOutcome.NotSent
            => BackupStep.Resuming,
        _ => null,
    };

    /// <summary>
    /// Whether the strip stays once the backup has ended: one somebody asked for, and any that
    /// did not work, stay until dismissed; an automatic one that worked goes when it ends, since
    /// its row in the table says so.
    /// </summary>
    public bool KeptWhenEnded => Reason is BackupReason.Manual || Severity is not BackupSeverity.Ok;
}

/// <summary>What the strip under a server's controls says about a backup (<c>D113</c>).</summary>
/// <remarks>
/// Here rather than in a component, so a test reads every arm. The strip is the sync's
/// (<see cref="SyncStrip"/>), the one place under the controls that says what is being done to
/// the server. Its colour is the outcome's: <see cref="SyncOutcome.Finished"/> for a backup that
/// worked, <see cref="SyncOutcome.CalledOff"/> for one with a warning or one cancelled, and
/// <see cref="SyncOutcome.Incomplete"/> for one that failed.
/// </remarks>
public static class BackupProgressText
{
    /// <summary>The steps a backup lists, in the order it takes them.</summary>
    /// <remarks>
    /// A stopped server's files are static, so its backup is the copy alone. With a snapshot the
    /// saves resume before the copy (<c>D66</c>), which is the toggle's whole point.
    /// </remarks>
    public static IReadOnlyList<BackupStep> Steps(bool live, bool snapshot) => (live, snapshot) switch
    {
        (false, _) => [BackupStep.Copying],
        (true, false) => [BackupStep.Pausing, BackupStep.Copying, BackupStep.Resuming],
        (true, true) => [BackupStep.Pausing, BackupStep.Snapshot, BackupStep.Resuming, BackupStep.Copying],
    };

    /// <summary>A step's name as the strip lists it.</summary>
    public static string Name(BackupStep step) => step switch
    {
        BackupStep.Pausing => "Pause saves",
        BackupStep.Snapshot => "Take a snapshot",
        BackupStep.Copying => "Copy the world",
        BackupStep.Resuming => "Resume saves",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unhandled step."),
    };

    /// <summary>What the step under way is doing, in a sentence.</summary>
    public static string Doing(BackupStep step, bool snapshot) => step switch
    {
        BackupStep.Pausing => "Pausing world saves and asking the server which files make up the save. Players stay on.",
        BackupStep.Snapshot => "Taking a shadow copy of the drive, so saves can resume before the copy.",
        BackupStep.Copying when snapshot => "Copying the world from the snapshot. Saves are running again.",
        BackupStep.Copying => "Copying the world. Players stay on; saves resume once the copy is done.",
        BackupStep.Resuming => "Resuming world saves.",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unhandled step."),
    };

    /// <summary>What a backup of <paramref name="reason"/> is called in the strip's title.</summary>
    public static string Called(BackupReason reason) => reason switch
    {
        BackupReason.Manual => "Backup",
        BackupReason.Scheduled => "Scheduled backup",
        BackupReason.PlayerActivity => "Backup after a join or leave",
        BackupReason.Shutdown => "Backup on stop",
        BackupReason.Safety => "Safety backup",
        BackupReason.Imported => "Import",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unhandled backup reason."),
    };

    /// <summary>The files counted once each, whatever the number of strategies copying them.</summary>
    public static SyncBar? Bar(BackupProgress progress) =>
        progress is { Step: BackupStep.Copying, FilesOf: > 0, Copiers: > 0 }
            ? new SyncBar(
                Math.Min(progress.FilesDone, progress.FilesOf) / progress.Copiers,
                progress.FilesOf / progress.Copiers)
            : null;

    /// <summary>The strip for <paramref name="progress"/>, its times in the zone <paramref name="toLocal"/> converts to.</summary>
    public static SyncStrip Strip(BackupProgress progress, Func<DateTimeOffset, DateTimeOffset> toLocal)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(toLocal);

        var steps = Steps(progress.Live, progress.Snapshot);

        // A step this run does not list, such as a snapshot it fell back from, counts as the one
        // it lists next.
        var index = steps.ToList().IndexOf(progress.Step);
        var at = (index < 0 ? 1 : index + 1);
        var of = steps.Count;
        var called = Called(progress.Reason);
        var started = ClockText.TimeWithSeconds(toLocal(progress.StartedUtc));

        var views = steps
            .Select((s, i) => new SyncStepView(i + 1, Name(s), SyncStrip.StateOf(i + 1, at, progress.Outcome)))
            .ToList();

        if (progress.Outcome == SyncOutcome.Running)
        {
            var bar = Bar(progress);

            var position = bar is { } b
                ? $"Step {at} of {of} · {b.Done} of {Files(b.Of)}"
                : $"Step {at} of {of}";

            return new(
                $"{called} under way",
                $"started {started}",
                position,
                bar,
                views,
                Doing(steps[at - 1], progress.Snapshot),
                progress.Outcome);
        }

        // Marked against the step it ended in, or all done when none is to blame.
        if (progress.EndedAt is { } endedAt)
        {
            var endedIndex = steps.ToList().IndexOf(endedAt);
            var endedStep = endedIndex < 0 ? 1 : endedIndex + 1;

            views = steps
                .Select((s, i) => new SyncStepView(i + 1, Name(s), SyncStrip.StateOf(i + 1, endedStep, progress.Outcome)))
                .ToList();
        }
        else
        {
            views = steps
                .Select((s, i) => new SyncStepView(i + 1, Name(s), SyncStepState.Done))
                .ToList();
        }

        var ended = progress.FinishedUtc ?? progress.StartedUtc;
        var when = $"{started} to {ClockText.TimeWithSeconds(toLocal(ended))}, {SyncText.Elapsed(ended - progress.StartedUtc)}";

        var title = progress.Severity switch
        {
            BackupSeverity.Ok => $"{called} finished",
            BackupSeverity.Warning => $"{called} finished with a warning",
            BackupSeverity.Failed => $"{called} failed",
            BackupSeverity.Skipped => $"{called} skipped",
            null => $"{called} cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(progress), progress.Severity, "Unhandled severity."),
        };

        return new(
            title,
            when,
            progress.Label ?? "Nothing kept",
            null,
            views,
            progress.Detail ?? string.Empty,
            progress.Outcome);
    }

    /// <summary>The outcome an ended backup's strip is coloured by.</summary>
    public static SyncOutcome OutcomeOf(BackupSeverity severity) => severity switch
    {
        BackupSeverity.Ok or BackupSeverity.Skipped => SyncOutcome.Finished,
        BackupSeverity.Warning => SyncOutcome.CalledOff,
        BackupSeverity.Failed => SyncOutcome.Incomplete,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unhandled severity."),
    };

    /// <summary>The sentence a cancelled backup ends on.</summary>
    public const string Cancelled = "The backup was cancelled before it finished.";

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";
}
