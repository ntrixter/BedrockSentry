using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Sync;

namespace BedrockSentry.Core.Backups;

/// <summary>The steps of a restore of a whole world, in the order it takes them (<c>D111</c>).</summary>
public enum WorldRestoreStep
{
    /// <summary>Stopping the server; passed over when it was stopped already.</summary>
    Stopping = 1,

    /// <summary>Taking the safety backup of the world being replaced.</summary>
    SafetyBackup = 2,

    /// <summary>Putting the backup's world, and its configuration when asked, in place.</summary>
    Replacing = 3,

    /// <summary>Starting the server again; passed over when it was stopped before.</summary>
    Starting = 4,
}

/// <summary>What a restore says as it moves on.</summary>
/// <param name="WorldReplaced">
/// True once the backup's world is in place: a failure before it leaves the world as it was, and
/// one after it does not.
/// </param>
public readonly record struct WorldRestoreReport(WorldRestoreStep Step, bool WorldReplaced = false);

/// <summary>Where a restore of a whole world is, as of the moment it was read (<c>D111</c>).</summary>
/// <param name="WasRunning">Whether the server ran when the restore began, which decides its steps.</param>
/// <param name="Detail">How it ended, in a sentence; null while it runs.</param>
public sealed record WorldRestoreProgress(
    string InstanceId,
    string BackupName,
    bool WasRunning,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    WorldRestoreStep Step,
    bool WorldReplaced,
    SyncOutcome Outcome,
    string? Detail);

/// <summary>What the strip under a server's controls says about a restore of its world (<c>D111</c>).</summary>
/// <remarks>
/// Here rather than in a component, so a test reads every arm. The strip is the sync's
/// (<see cref="SyncStrip"/>): one place under the controls says what is being done to the server.
/// </remarks>
public static class WorldRestoreText
{
    /// <summary>What the controls say while a restore runs; they are disabled until it ends.</summary>
    public const string ControlsWait = "Start, Stop and Restart wait until the restore finishes.";

    /// <summary>The steps a restore lists: four for a running server, two for a stopped one.</summary>
    public static IReadOnlyList<WorldRestoreStep> Steps(bool wasRunning) => wasRunning
        ? [WorldRestoreStep.Stopping, WorldRestoreStep.SafetyBackup, WorldRestoreStep.Replacing, WorldRestoreStep.Starting]
        : [WorldRestoreStep.SafetyBackup, WorldRestoreStep.Replacing];

    /// <summary>A step's name as the strip lists it.</summary>
    public static string Name(WorldRestoreStep step) => step switch
    {
        WorldRestoreStep.Stopping => "Stop",
        WorldRestoreStep.SafetyBackup => "Safety backup",
        WorldRestoreStep.Replacing => "Put back the world",
        WorldRestoreStep.Starting => "Start",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unhandled step."),
    };

    /// <summary>What the step under way is doing, in a sentence.</summary>
    public static string Doing(WorldRestoreStep step, string backupName) => step switch
    {
        WorldRestoreStep.Stopping => "Stopping the server. Players online are disconnected.",
        WorldRestoreStep.SafetyBackup => "Taking a safety backup of the world being replaced.",
        WorldRestoreStep.Replacing => $"Putting back the world from {backupName}.",
        WorldRestoreStep.Starting => "Starting the server again.",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unhandled step."),
    };

    /// <summary>The strip for <paramref name="progress"/>, its times in the zone <paramref name="toLocal"/> converts to.</summary>
    public static SyncStrip Strip(WorldRestoreProgress progress, Func<DateTimeOffset, DateTimeOffset> toLocal)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(toLocal);

        var steps = Steps(progress.WasRunning);

        // A step the list passes over, such as a stop on a stopped server, counts as the one after it.
        var index = steps.ToList().FindIndex(s => s >= progress.Step);
        var at = (index < 0 ? steps.Count - 1 : index) + 1;
        var of = steps.Count;
        var started = ClockText.TimeWithSeconds(toLocal(progress.StartedUtc));

        var views = steps
            .Select((s, i) => new SyncStepView(i + 1, Name(s), SyncStrip.StateOf(i + 1, at, progress.Outcome)))
            .ToList();

        if (progress.Outcome == SyncOutcome.Running)
        {
            return new(
                $"Restoring {progress.BackupName}",
                $"started {started}",
                $"Step {at} of {of}",
                null,
                views,
                $"{Doing(steps[at - 1], progress.BackupName)} {ControlsWait}",
                progress.Outcome);
        }

        var ended = progress.FinishedUtc ?? progress.StartedUtc;
        var when = $"{started} to {ClockText.TimeWithSeconds(toLocal(ended))}, {SyncText.Elapsed(ended - progress.StartedUtc)}";

        return progress.Outcome switch
        {
            SyncOutcome.Finished => new(
                $"Restored {progress.BackupName}",
                when,
                "Done",
                null,
                views,
                progress.Detail ?? string.Empty,
                progress.Outcome),

            SyncOutcome.CalledOff => new(
                $"Restore of {progress.BackupName} called off",
                when,
                "Nothing changed",
                null,
                views,
                progress.Detail ?? string.Empty,
                progress.Outcome),

            SyncOutcome.Incomplete => new(
                $"Restore of {progress.BackupName} stopped at step {at} of {of}",
                when,
                "World replaced",
                null,
                views,
                progress.Detail ?? string.Empty,
                progress.Outcome),

            _ => throw new ArgumentOutOfRangeException(nameof(progress), progress.Outcome, "Unhandled outcome."),
        };
    }

    /// <summary>The sentence a restore that failed ends on.</summary>
    /// <param name="worldReplaced">Whether the backup's world was in place when it failed.</param>
    public static string Failed(string reason, bool worldReplaced)
    {
        var sentence = reason.Trim() is { Length: > 0 } r ? (r.EndsWith('.') ? r : r + ".") : "It failed.";

        return worldReplaced
            ? $"{sentence} The backup's world is in place; the world from before is in the safety backup."
            : $"{sentence} The world was left as it was.";
    }
}
