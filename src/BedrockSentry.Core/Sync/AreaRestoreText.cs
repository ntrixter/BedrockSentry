using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Structures;

namespace BedrockSentry.Core.Sync;

/// <summary>The four steps of an area restore, as the page and the console number them.</summary>
public enum AreaRestoreStep
{
    /// <summary>Reading the area out of the backup on a throwaway server, while this one runs.</summary>
    Reading = 1,

    /// <summary>Stopping this server, with the countdown's warning.</summary>
    Stopping = 2,

    /// <summary>Taking the safety backup and putting the pieces in the world's pack.</summary>
    Placing = 3,

    /// <summary>Starting this server and putting the pieces back.</summary>
    PuttingBack = 4,
}

/// <summary>How an area restore ended, or that it has not.</summary>
public enum AreaRestoreOutcome
{
    /// <summary>Still going.</summary>
    Running,

    /// <summary>Every piece was put back.</summary>
    Finished,

    /// <summary>Stopped before this server's world changed; it is as it was.</summary>
    CalledOff,

    /// <summary>
    /// Stopped after the server was stopped for it, with not every piece put back. The
    /// world from before is in the safety backup.
    /// </summary>
    Incomplete,
}

/// <summary>Where an area restore is, for the page, as of the moment it was read.</summary>
/// <param name="Title">The area, as <see cref="AreaRestorePlan.Title"/> names it.</param>
/// <param name="Message">The last notice, or how it ended.</param>
public sealed record AreaRestoreProgress(
    string InstanceId,
    string Title,
    string BackupName,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    AreaRestoreStep Step,
    AreaRestoreOutcome Outcome,
    string? Message);

/// <summary>What an area restore says, in the console, in game and on the page (<c>D108</c>).</summary>
/// <remarks>Here rather than in the page or the service, so a test can read every sentence.</remarks>
public static class AreaRestoreText
{
    /// <summary>How long the countdown before the stop warns players.</summary>
    public static readonly TimeSpan Warning = TimeSpan.FromSeconds(30);

    /// <summary>What a restore of corners calls its area.</summary>
    public static string CornersTitle(BlockBox box, BedrockDimension dimension) =>
        dimension == BedrockDimension.Overworld
            ? $"the area from {box}"
            : $"the area from {box} in the {dimension.Title}";

    /// <summary>What the Backups tab's panel says a restore of an area does.</summary>
    /// <param name="server">The server whose world changes, which may not be the one the backup is of (<c>D111</c>).</param>
    public static string PanelLead(string server) =>
        $"The area is read out of this backup on a copy of Bedrock while {server} keeps running. Then {server} "
        + $"restarts with {SyncText.Elapsed(Warning)}' warning, a safety backup of its world is taken, and the area is "
        + "put back. Inside the area, what the backup holds replaces what is there, its entities too; the rest of the "
        + "world is untouched.";

    /// <summary>How big a planned area is, for the panel.</summary>
    public static string Size(AreaRestorePlan plan) =>
        $"{plan.Title}: {SyncText.Count(plan.Pieces.Count, "piece")}, about {ByteSize.Format(plan.EstimatedBytes)}."
        + (plan.Plan is { HeightClamped: true, Box: { } box }
            ? $" It stops at the world's height, from {box.Min.Y} to {box.Max.Y}."
            : "");

    /// <summary>The answer a command gets at once, before anything is read.</summary>
    public static string Accepted(string title, string backupName) =>
        $"Restoring {title} from {backupName}. The server restarts with {SyncText.Elapsed(Warning)}' warning "
        + "once the backup is read.";

    public static string Started(AreaRestorePlan plan, string backupName) =>
        $"Restoring {plan.Title} from {backupName}. Step 1 of 4: reading {SyncText.Count(plan.Pieces.Count, "piece")}, "
        + $"about {ByteSize.Format(plan.EstimatedBytes)}, out of the backup on a copy of Bedrock nobody plays on. "
        + "This server keeps running meanwhile.";

    public static string Read(int pieces, long bytes) =>
        $"Step 1 of 4: read the {SyncText.Count(pieces, "piece")} out of the backup, {ByteSize.Format(bytes)}.";

    public static string Stopping(bool running) => running
        ? $"Step 2 of 4: stopping this server, with {SyncText.Elapsed(Warning)}' warning."
        : "Step 2 of 4: this server is stopped already, so nobody is warned.";

    public static string Placed(string? safetyBackup) =>
        "Step 3 of 4: put the pieces in the world's structures pack"
        + (safetyBackup is null
            ? "; there was no world here before, so no safety backup was taken."
            : $"; the world from before is in the safety backup {safetyBackup}.");

    public static string PuttingBack(int pieces) =>
        $"Step 4 of 4: putting back the {SyncText.Count(pieces, "piece")}. Inside each, what the backup holds "
        + "replaces what is there, its entities too.";

    public static string Finished(AreaRestorePlan plan, string backupName, TimeSpan took) =>
        $"Restored {plan.Title} from {backupName} in {SyncText.Elapsed(took)}.";

    public static string CalledOff(string title, string reason) =>
        $"The restore of {title} was called off before this server's world changed: {reason}";

    public static string Incomplete(string title, string reason) =>
        $"The restore of {title} stopped after this server was stopped for it: {reason} The world from before "
        + "is in the safety backup.";

    /// <summary>What the controls say while an area restore runs; they are disabled until it ends.</summary>
    public const string ControlsWait = "Start, Stop and Restart wait until the restore finishes.";

    /// <summary>The four steps by name, as the strip under the controls lists them (<c>D111</c>).</summary>
    public static IReadOnlyList<string> StepNames { get; } =
        ["Read the area from the backup", "Stop", "Safety backup", "Put the area back"];

    /// <summary>The strip under the server's controls for <paramref name="progress"/> (<c>D111</c>).</summary>
    /// <remarks>The sync's strip (<see cref="SyncStrip"/>), so every job that stops a server shows in one place.</remarks>
    public static SyncStrip Strip(AreaRestoreProgress progress, Func<DateTimeOffset, DateTimeOffset> toLocal)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(toLocal);

        var step = (int)progress.Step;
        var of = StepNames.Count;
        var outcome = OutcomeOf(progress.Outcome);
        var started = ClockText.TimeWithSeconds(toLocal(progress.StartedUtc));

        var steps = StepNames
            .Select((name, i) => new SyncStepView(i + 1, name, SyncStrip.StateOf(i + 1, step, outcome)))
            .ToList();

        if (outcome == SyncOutcome.Running)
        {
            return new(
                $"Restoring {progress.Title} from {progress.BackupName}",
                $"started {started}",
                $"Step {step} of {of}",
                null,
                steps,
                $"{Doing(progress.Step)} {ControlsWait}",
                outcome);
        }

        var ended = progress.FinishedUtc ?? progress.StartedUtc;
        var when = $"{started} to {ClockText.TimeWithSeconds(toLocal(ended))}, {SyncText.Elapsed(ended - progress.StartedUtc)}";

        var (title, position) = progress.Outcome switch
        {
            AreaRestoreOutcome.Finished => ($"Restored {progress.Title}", "Done"),
            AreaRestoreOutcome.CalledOff => ($"Restore of {progress.Title} called off", "Nothing changed"),
            AreaRestoreOutcome.Incomplete => ($"Restore of {progress.Title} stopped at step {step} of {of}", "Not every piece put back"),
            _ => throw new ArgumentOutOfRangeException(nameof(progress), progress.Outcome, "Unhandled outcome."),
        };

        return new(title, when, position, null, steps, progress.Message ?? string.Empty, outcome);
    }

    /// <summary>What the step under way is doing, in a sentence.</summary>
    public static string Doing(AreaRestoreStep step) => step switch
    {
        AreaRestoreStep.Reading => "Reading the area out of the backup on a copy of Bedrock; this server keeps running.",
        AreaRestoreStep.Stopping => $"Stopping this server, with {SyncText.Elapsed(Warning)}' warning.",
        AreaRestoreStep.Placing => "Taking a safety backup and putting the pieces in the world's structures pack.",
        AreaRestoreStep.PuttingBack => "Starting this server and putting the area back.",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unhandled step."),
    };

    private static SyncOutcome OutcomeOf(AreaRestoreOutcome outcome) => outcome switch
    {
        AreaRestoreOutcome.Running => SyncOutcome.Running,
        AreaRestoreOutcome.Finished => SyncOutcome.Finished,
        AreaRestoreOutcome.CalledOff => SyncOutcome.CalledOff,
        AreaRestoreOutcome.Incomplete => SyncOutcome.Incomplete,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unhandled outcome."),
    };

    /// <summary>Why a name matched none of the server's Sync Areas.</summary>
    public static string NoSuchArea(string name, IReadOnlyList<KeptAreaSettings> saved)
    {
        var names = saved.Select(a => (a.Name ?? string.Empty).Trim()).Where(n => n.Length > 0).ToList();

        return names.Count == 0
            ? $"No Sync Area is named {name}, and this server has none. Give two corners instead."
            : $"No Sync Area is named {name}. This server's are {SyncText.Names(names)}.";
    }

    public const string NoUsableBackup =
        "This server has no backup an area can be restored from. Take one on the Backups tab first.";

    /// <summary>Why another server's backups give an area nothing to read (<c>D111</c>).</summary>
    public static string NoUsableBackupOf(string owner) =>
        $"{owner} has no backup an area can be restored from. Its Backups tab takes one.";

    /// <summary>
    /// Why backups that exist give an area nothing to read: how many there are and why
    /// the newest cannot be used (<c>T440</c>). A server with only a safety backup was
    /// told it had no backups.
    /// </summary>
    /// <param name="owner">The server the backups are of, when it is not this one; null for this one.</param>
    public static string NoneUsable(int count, BackupCandidate newest, string? owner = null)
    {
        ArgumentNullException.ThrowIfNull(newest);

        var whose = owner ?? "This server";
        var backups = count == 1 ? "1 backup" : $"{count} backups";
        var fix = owner is null ? "Take one on the Backups tab first." : "Its Backups tab takes one.";

        return $"{whose} has {backups}, and none can restore an area: the newest, {newest.Name}, {newest.Unusable}. {fix}";
    }

    /// <summary>Why a backup's name matched none on disk.</summary>
    /// <param name="owner">The server the backups are of, when it is not this one; null for this one.</param>
    public static string NoSuchBackup(string asked, IReadOnlyList<string> newest, string? owner = null) => newest.Count == 0
        ? $"No backup is named {asked}, and none of {(owner is null ? "this server's" : $"{owner}'s")} can be read."
        : $"No backup is named {asked}. The newest {(newest.Count == 1 ? "is" : "are")} {SyncText.Names(newest)}.";

    /// <summary>What every sentence calls a backup of another server (<c>D111</c>); a backup of this one keeps its name.</summary>
    public static string BackupLabel(string backupName, string? owner) =>
        owner is null ? backupName : $"{owner}'s {backupName}";

    /// <summary>What the Sync tab says Sync one area does (<c>D111</c>).</summary>
    /// <param name="source">The server this one takes its world from, or null when none is chosen.</param>
    public static string SyncOneLead(string? source) =>
        $"Copies one area from {source ?? "the other server"}'s newest backup into this server and leaves the rest "
        + $"of this world as it is. This server restarts with {SyncText.Elapsed(Warning)}' warning, after a safety "
        + "backup. A Sync Area chosen here is taken from the other server this time, not kept.";

    /// <summary>Why Sync this area is off before a server to copy from is chosen (<c>T362</c> clause 3).</summary>
    public const string SyncOneNoSource = "Choose a server under Takes its world from first.";

    /// <summary>Which backup Sync one area would read, after the area's size.</summary>
    public static string SyncOneFrom(string backupLabel) => $"From {backupLabel}.";

    /// <summary>What the Backups tab says once an area has gone onto another server, whose page shows its steps.</summary>
    public static string RunsElsewhere(string target) =>
        $"Restoring the area on {target}. Its page shows the steps under its controls.";

    /// <summary>Why the server the backup is from cannot be read from.</summary>
    public const string SourceGone = "The server the backup is from is no longer here; choose another.";

    /// <summary>
    /// Why an area of a newer build's world is not put into a server on an older one (<c>D111</c>,
    /// <c>D103</c>'s rule): what an older build does with a newer world's blocks is unmeasured.
    /// </summary>
    public static string OlderBuild(string target, string targetBuild, string source, string sourceBuild) =>
        $"{target} runs Bedrock {targetBuild}, older than {source}'s {sourceBuild}. Update {target} first, "
        + "or restore onto a server on the same build or a newer one.";

    public const string SyncRunning = "A sync involves this server; restore an area once it ends.";

    public const string AlreadyRunning = "An area restore already runs on this server; try again once it ends.";

    /// <summary>Why a server in <paramref name="state"/> cannot start a restore now; null when it can.</summary>
    /// <remarks>
    /// A restore leaves a server stopped when it was stopped before, so a server on its way up or
    /// down is neither and is refused until it arrives. A faulted server counts as stopped.
    /// </remarks>
    public static string? Busy(InstanceState state) => state switch
    {
        InstanceState.Stopped or InstanceState.Running or InstanceState.Faulted => null,
        InstanceState.Starting => "This server is starting; restore an area once it is running or stopped.",
        InstanceState.Stopping => "This server is stopping; restore an area once it has stopped.",
        InstanceState.Backoff =>
            "This server exited and is waiting to start again; restore an area once it is running, or stop it first.",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unhandled state."),
    };
}
