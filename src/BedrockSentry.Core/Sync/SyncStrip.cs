using BedrockSentry.Core.Diagnostics;

namespace BedrockSentry.Core.Sync;

/// <summary>How far one of the five steps has got, as the strip draws it.</summary>
public enum SyncStepState
{
    /// <summary>Not reached.</summary>
    Pending,

    /// <summary>Under way now.</summary>
    Current,

    /// <summary>Done, or passed with nothing to do.</summary>
    Done,

    /// <summary>The step the sync ended in without finishing.</summary>
    EndedHere,
}

/// <summary>One of the five steps, numbered and named as the strip lists them.</summary>
public sealed record SyncStepView(int Number, string Name, SyncStepState State);

/// <summary>Pieces done out of the pass under way, for the strip's bar.</summary>
public readonly record struct SyncBar(int Done, int Of);

/// <summary>What the strip above a server's controls says about its sync (<c>D103</c>).</summary>
/// <param name="Title">What is happening or how it ended, such as <c>Syncing from Survival</c>.</param>
/// <param name="When">When it started, or when it ran and for how long.</param>
/// <param name="Position">Where it is (<c>Step 5 of 5 · piece 2 of 5</c>), or what it came to.</param>
/// <param name="Bar">The pass under way, or null when none is.</param>
/// <param name="Line">The sentence under the steps.</param>
/// <remarks>
/// Here rather than in the component, so a test can read every arm: the page is
/// reachable by the suite only as text.
/// </remarks>
public sealed record SyncStrip(
    string Title,
    string When,
    string Position,
    SyncBar? Bar,
    IReadOnlyList<SyncStepView> Steps,
    string Line,
    SyncOutcome Outcome)
{
    /// <summary>What the controls say while a sync runs; they are disabled until it ends.</summary>
    public const string ControlsWait = "Start, Stop and Restart wait until the sync finishes.";

    /// <summary>The five steps by name, as the strip lists them.</summary>
    public static IReadOnlyList<string> StepNames(string source) =>
    [
        "Save the Sync Areas here",
        "Stop",
        "Read the pieces",
        $"Copy in {source}'s world",
        "Put the Sync Areas back",
    ];

    /// <summary>The strip for <paramref name="progress"/>, its times in the zone <paramref name="toLocal"/> converts to.</summary>
    public static SyncStrip For(SyncProgress progress, Func<DateTimeOffset, DateTimeOffset> toLocal)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(toLocal);

        var source = progress.SourceName;
        var step = (int)progress.Step;
        var started = ClockText.TimeWithSeconds(toLocal(progress.StartedUtc));

        var steps = StepNames(source)
            .Select((name, i) => new SyncStepView(i + 1, name, StateOf(i + 1, step, progress.Outcome)))
            .ToList();

        if (progress.Outcome == SyncOutcome.Running)
        {
            // No bar while the server starts: no piece is under way until it is up.
            var bar = progress.Pieces > 0 && !progress.Starting
                ? progress.Step switch
                {
                    SyncStep.Saving => new SyncBar(progress.PiecesSaved, progress.Pieces),
                    SyncStep.PuttingBack => new SyncBar(progress.PiecesPutBack, progress.Pieces),
                    _ => (SyncBar?)null,
                }
                : null;

            var position = bar is { } b
                ? $"Step {step} of 5 · piece {Math.Min(b.Done + 1, b.Of)} of {b.Of}"
                : $"Step {step} of 5";

            return new(
                $"Syncing from {source}",
                $"started {started}",
                position,
                bar,
                steps,
                $"{Doing(progress)} {ControlsWait}",
                progress.Outcome);
        }

        var ended = progress.FinishedUtc ?? progress.StartedUtc;
        var when = $"{started} to {ClockText.TimeWithSeconds(toLocal(ended))}, {SyncText.Elapsed(ended - progress.StartedUtc)}";

        return progress.Outcome switch
        {
            SyncOutcome.Finished => new(
                $"Sync from {source} finished",
                when,
                progress.Pieces == 0 ? "No areas kept" : $"{progress.PiecesPutBack} of {progress.Pieces} pieces put back",
                null,
                steps,
                progress.Detail ?? string.Empty,
                progress.Outcome),

            SyncOutcome.CalledOff => new(
                $"Sync from {source} called off at step {step} of 5",
                when,
                "Nothing changed",
                null,
                steps,
                progress.Detail ?? string.Empty,
                progress.Outcome),

            SyncOutcome.Incomplete => new(
                $"Sync from {source} stopped at step {step} of 5",
                when,
                progress.Pieces == 0
                    ? "World replaced"
                    : $"World replaced; {progress.PiecesPutBack} of {progress.Pieces} pieces put back",
                null,
                steps,
                progress.Detail ?? string.Empty,
                progress.Outcome),

            _ => throw new ArgumentOutOfRangeException(nameof(progress), progress.Outcome, "Unhandled outcome."),
        };
    }

    /// <summary>
    /// How step <paramref name="number"/> is drawn when the job is at <paramref name="step"/>: shared
    /// by the sync, a restore of a world and a restore of an area (<c>D111</c>), whose strips are one.
    /// </summary>
    public static SyncStepState StateOf(int number, int step, SyncOutcome outcome) => outcome switch
    {
        SyncOutcome.Finished => SyncStepState.Done,
        _ when number < step => SyncStepState.Done,
        _ when number > step => SyncStepState.Pending,
        SyncOutcome.Running => SyncStepState.Current,
        _ => SyncStepState.EndedHere,
    };

    /// <summary>What the step under way is doing, in a sentence.</summary>
    private static string Doing(SyncProgress progress) => (progress.Step, progress.Starting, progress.CurrentPiece) switch
    {
        (SyncStep.Saving, true, _) => "Starting this server to save its Sync Areas.",
        (SyncStep.Saving, _, { } piece) => $"Saving {piece}.",
        (SyncStep.Saving, _, null) => "Saving the Sync Areas.",
        (SyncStep.Stopping, _, _) => "Stopping this server.",
        (SyncStep.Reading, _, _) => "Reading the saved pieces out of the stopped world.",
        (SyncStep.Replacing, _, _) => $"Taking a safety backup and copying in {progress.SourceName}'s world.",
        (SyncStep.PuttingBack, true, _) => "Starting this server with the saved pieces in its structures pack.",
        (SyncStep.PuttingBack, _, { } piece) => $"Putting back {piece}.",
        (SyncStep.PuttingBack, _, null) => "Putting the Sync Areas back.",
        _ => throw new ArgumentOutOfRangeException(nameof(progress), progress.Step, "Unhandled step."),
    };
}
