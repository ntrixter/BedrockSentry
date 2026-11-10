using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Structures;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Worlds;

/// <summary>A running server whose console a pass of <see cref="StructureConsole"/> drives.</summary>
/// <remarks>
/// An instance (<see cref="InstanceSupervisor"/>), or a throwaway reading a backup's world
/// (<see cref="ThrowawayConsole"/>, <c>D108</c>).
/// </remarks>
public interface IStructureServer
{
    /// <summary>What a notice calls it.</summary>
    string Name { get; }

    /// <summary>What a log line calls it.</summary>
    string InstanceId { get; }

    /// <summary>Whether its console takes commands now.</summary>
    bool IsRunning { get; }

    /// <summary>Its output, where every answer is watched for and every notice goes.</summary>
    ConsoleBuffer Console { get; }

    /// <summary>
    /// Sends one command. Throws <see cref="InvalidOperationException"/>, <see cref="IOException"/>
    /// or <see cref="ObjectDisposedException"/> when it was not sent (<c>D87</c>).
    /// </summary>
    Task SendCommandAsync(string command, CancellationToken cancellationToken = default);
}

/// <summary>What became of one piece.</summary>
public enum StructurePieceState
{
    /// <summary>Saved, or swept and put back.</summary>
    Done,

    /// <summary>Tried and not done; the reason says why.</summary>
    Failed,

    /// <summary>Not tried, because an earlier piece stopped the pass.</summary>
    NotRun,
}

/// <summary>One piece's outcome.</summary>
/// <param name="Sweeps">
/// What each sweep before a load answered, in order, such as <c>Killed Cow, Pig</c>
/// then <c>Killed Raw Beef, Raw Porkchop</c> then <c>No targets matched selector</c>;
/// empty when none ran.
/// </param>
public sealed record StructurePieceOutcome(
    LabelledPiece Piece, StructurePieceState State, string? Reason, IReadOnlyList<string> Sweeps)
{
    /// <summary>A piece that was not tried.</summary>
    public static StructurePieceOutcome NotRun(LabelledPiece piece) => new(piece, StructurePieceState.NotRun, null, []);
}

/// <summary>How a pass over the pieces ended.</summary>
public enum StructurePassState
{
    /// <summary>Every piece is done.</summary>
    Finished,

    /// <summary>A piece failed, or a ticking area could not be given back, and the pieces after it were not tried.</summary>
    Stopped,

    /// <summary>Nothing was sent: the server was not running.</summary>
    Refused,
}

/// <summary>A pass over the pieces.</summary>
/// <param name="Reason">Why it stopped or was refused; null when it finished.</param>
/// <param name="TickingAreaLeft">
/// Why the ticking area <see cref="StructureCommands.TickingAreaName"/> may still
/// be in the world, or null when every one this pass added was given back.
/// </param>
public sealed record StructurePassResult(
    StructurePassState State,
    IReadOnlyList<StructurePieceOutcome> Pieces,
    string? Reason,
    string? TickingAreaLeft);

/// <summary>How long a pass waits for each thing it waits for.</summary>
/// <param name="Answer">For a command's answer, where nothing below says otherwise.</param>
/// <param name="SaveAnswer">For <c>structure save</c>'s; a full piece answered in 0.3 s on Linux.</param>
/// <param name="LoadAnswer">For <c>structure load</c>'s; a full piece answered in 3.4 to 4.4 s on Linux.</param>
/// <param name="ProbeEvery">Between probes while the area loads.</param>
/// <param name="ProbeFor">
/// From a ticking area's answer to the last probe; a 5 × 5 chunk area of terrain
/// never generated before loaded in 2.0 s on Linux.
/// </param>
public sealed record StructureConsoleTimings(
    TimeSpan Answer,
    TimeSpan SaveAnswer,
    TimeSpan LoadAnswer,
    TimeSpan ProbeEvery,
    TimeSpan ProbeFor)
{
    /// <summary>Several times what was measured, since a busy server answers late.</summary>
    public static StructureConsoleTimings Default { get; } = new(
        Answer: TimeSpan.FromSeconds(15),
        SaveAnswer: TimeSpan.FromSeconds(30),
        LoadAnswer: TimeSpan.FromSeconds(60),
        ProbeEvery: TimeSpan.FromMilliseconds(250),
        ProbeFor: TimeSpan.FromSeconds(60));
}

/// <summary>
/// Saves pieces of a world on a running server, or sweeps them and puts saved
/// ones back, through its console, one piece at a time (<c>D86</c>, <c>D103</c>).
/// </summary>
/// <remarks>
/// <para><strong>Per piece:</strong> borrow the ticking area
/// <see cref="StructureCommands.TickingAreaName"/> over the piece and one chunk
/// around it (<see cref="StructureCommands.Ring"/>), probe until every chunk of
/// it is loaded, then <c>structure save</c>, or a sweep of the piece's entities
/// followed by <c>structure load</c>, then give the ticking area back.
/// A save needs its area loaded, and a load into an area not loaded is queued
/// and never confirmed, so nothing is saved or loaded before the probe
/// answers.</para>
///
/// <para><strong>The ticking area goes back whatever happens</strong> once BDS
/// has said it was added: after a failure, a timeout or a cancellation, on
/// <see cref="CancellationToken.None"/>. One left behind keeps its chunks loaded
/// for good and holds one of the dimension's ten places. If the server will not
/// take it back the result says so, and the next pass that finds one of that
/// name gives it back before adding its own.</para>
///
/// <para><strong>A pass stops at the first piece that fails</strong> and marks
/// the rest not run. A failed save means the copy is incomplete, which ends a
/// sync before its world is touched (<c>D103</c>); a failed load leaves the rest
/// of the pieces in the pack for another try.</para>
///
/// <para>Console commands run in the overworld, so a piece in another dimension
/// sends every command through <c>execute in</c> (<see cref="BedrockDimension.Run"/>,
/// <c>D106</c>), its ticking area included. One pass at a time on a server: two would
/// borrow the same ticking area.</para>
/// </remarks>
public sealed class StructureConsole
{
    private readonly TimeProvider _time;
    private readonly ILogger<StructureConsole> _log;
    private readonly StructureConsoleTimings _timings;

    public StructureConsole(TimeProvider time, ILogger<StructureConsole> logger)
        : this(time, logger, StructureConsoleTimings.Default)
    {
    }

    public StructureConsole(TimeProvider time, ILogger<StructureConsole> logger, StructureConsoleTimings timings)
    {
        _time = time;
        _log = logger;
        _timings = timings;
    }

    /// <summary>Saves each piece into the server's world as a structure of the piece's name.</summary>
    /// <param name="progress">Told each piece's outcome as it is decided, done or failed.</param>
    /// <remarks>
    /// Cancelled, it sends nothing more, gives back a ticking area it holds, and
    /// throws; <paramref name="progress"/> has been told every piece decided before.
    /// </remarks>
    public Task<StructurePassResult> SaveAsync(
        IStructureServer server,
        IReadOnlyList<LabelledPiece> pieces,
        IProgress<StructurePieceOutcome>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(Pass.Save, server, pieces, progress, cancellationToken);

    /// <summary>
    /// For each piece, removes every entity but players from its box and loads
    /// the structure of its name at its lowest corner.
    /// </summary>
    /// <inheritdoc cref="SaveAsync" path="/param[@name='progress']"/>
    /// <inheritdoc cref="SaveAsync" path="/remarks"/>
    public Task<StructurePassResult> PutBackAsync(
        IStructureServer server,
        IReadOnlyList<LabelledPiece> pieces,
        IProgress<StructurePieceOutcome>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(Pass.PutBack, server, pieces, progress, cancellationToken);

    private enum Pass
    {
        Save,
        PutBack,
    }

    private async Task<StructurePassResult> RunAsync(
        Pass pass,
        IStructureServer server,
        IReadOnlyList<LabelledPiece> pieces,
        IProgress<StructurePieceOutcome>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(pieces);

        if (!server.IsRunning)
        {
            return new StructurePassResult(
                StructurePassState.Refused,
                [.. pieces.Select(StructurePieceOutcome.NotRun)],
                $"{server.Name} is not running, and its console is how pieces are "
                + (pass == Pass.Save ? "saved." : "put back."),
                null);
        }

        var outcomes = new List<StructurePieceOutcome>(pieces.Count);

        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            var (outcome, left) = await RunPieceAsync(pass, server, piece, cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);
            progress?.Report(outcome);

            Notice(server, outcome.State == StructurePieceState.Done
                ? $"{Did(pass)} {piece.Label}."
                : $"Could not {Verb(pass)} {piece.Label}: {outcome.Reason}");

            if (left is not null)
            {
                _log.LogWarning(
                    "The ticking area {Name} may still be in {InstanceId}'s world: {Reason}",
                    StructureCommands.TickingAreaName, server.InstanceId, left);

                Notice(server,
                    $"The ticking area {StructureCommands.TickingAreaName} may still be in the world: {left} "
                    + $"Remove it with {piece.In.Run($"tickingarea remove {StructureCommands.TickingAreaName}")}.");
            }

            if (outcome.State == StructurePieceState.Failed)
            {
                return Finish(outcomes, pieces, outcome.Reason, left);
            }

            if (left is not null)
            {
                // The piece is done. Going on would borrow a ticking area of the
                // same name, which is not this pass's to take while it may still
                // be there, so a later piece stops the pass and the last does not.
                var isLast = i == pieces.Count - 1;

                return Finish(
                    outcomes,
                    pieces,
                    isLast ? null : $"The ticking area {StructureCommands.TickingAreaName} could not be given back: {left}",
                    left);
            }
        }

        return Finish(outcomes, pieces, null, null);
    }

    private static StructurePassResult Finish(
        List<StructurePieceOutcome> outcomes, IReadOnlyList<LabelledPiece> pieces, string? stopped, string? left)
    {
        foreach (var piece in pieces.Skip(outcomes.Count))
        {
            outcomes.Add(StructurePieceOutcome.NotRun(piece));
        }

        return new StructurePassResult(
            stopped is null ? StructurePassState.Finished : StructurePassState.Stopped, outcomes, stopped, left);
    }

    /// <summary>One piece: the ticking area, the probe, the work, and the ticking area back.</summary>
    /// <returns>The outcome, and why the ticking area may still be there, or null.</returns>
    private async Task<(StructurePieceOutcome Outcome, string? TickingAreaLeft)> RunPieceAsync(
        Pass pass, IStructureServer server, LabelledPiece labelled, CancellationToken cancellationToken)
    {
        var piece = labelled.Piece;
        var dimension = labelled.In;
        var added = await AddTickingAreaAsync(server, piece.Box, dimension, cancellationToken).ConfigureAwait(false);

        if (added.Failure is { } refused)
        {
            // Unanswered is not refused: BDS may have added it and been slow to
            // say so, and one left behind holds a place for good.
            var unsure = added.MayBeThere
                ? await RemoveTickingAreaAsync(server, dimension).ConfigureAwait(false)
                : null;

            return (new StructurePieceOutcome(labelled, StructurePieceState.Failed, refused, []), unsure);
        }

        string? failure = null;
        IReadOnlyList<string> sweeps = [];
        string? left;

        try
        {
            failure = await ProbeAsync(server, piece.Box, dimension, cancellationToken).ConfigureAwait(false);

            if (failure is null && pass == Pass.Save)
            {
                failure = await SaveAsync(server, piece, dimension, cancellationToken).ConfigureAwait(false);
            }
            else if (failure is null)
            {
                (failure, sweeps) = await SweepAndLoadAsync(server, piece, dimension, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            left = await RemoveTickingAreaAsync(server, dimension).ConfigureAwait(false);
        }

        if (failure is not null)
        {
            _log.LogWarning(
                "Could not {Verb} {Piece} on {InstanceId}: {Reason}", Verb(pass), piece.Name, server.InstanceId, failure);
        }

        return (new StructurePieceOutcome(
                labelled,
                failure is null ? StructurePieceState.Done : StructurePieceState.Failed,
                failure,
                sweeps),
            left);
    }

    /// <summary>
    /// How many more times a ticking area is asked for while BDS says its name is
    /// taken, a probe interval apart.
    /// </summary>
    /// <remarks>
    /// Measured on BDS 1.26.52.3: a piece's ticking area answered <em>"Removed
    /// ticking area(s)"</em>, the next piece's add of the same name straight after
    /// answered <em>"A ticking area with the name bedrocksentry already
    /// exists."</em>, a remove then answered that none of that name existed, and
    /// the add after that was taken. BDS lets go of a name a little after it
    /// says so.
    /// </remarks>
    internal const int NameTakenRetries = 8;

    /// <summary>
    /// Borrows the ticking area: no failure when BDS added it, else why not and
    /// whether one of its name may be there anyway.
    /// </summary>
    private async Task<(string? Failure, bool MayBeThere)> AddTickingAreaAsync(
        IStructureServer server, BlockBox box, BedrockDimension dimension, CancellationToken cancellationToken)
    {
        var add = StructureCommands.AddTickingArea(box, dimension);
        var answer = await AskAsync(server, add, cancellationToken).ConfigureAwait(false);

        for (var retry = 0; retry < NameTakenRetries && answer.Answer?.Kind == StructureAnswerKind.TickingAreaNameTaken; retry++)
        {
            if (retry == 0)
            {
                // Left by a pass that was cut off, or given back a moment ago and
                // not yet let go of. The name is this wrapper's and unique in a
                // dimension, so it is given back either way.
                var remove = StructureCommands.RemoveTickingArea(dimension);
                var removed = await AskAsync(server, remove, cancellationToken).ConfigureAwait(false);

                if (removed.Answer?.Kind is not (StructureAnswerKind.Done or StructureAnswerKind.NoTickingArea))
                {
                    return ($"A ticking area named {StructureCommands.TickingAreaName} was already there and could not be "
                        + $"removed. {Unanswered(removed, remove)}", true);
                }
            }

            await Task.Delay(_timings.ProbeEvery, _time, cancellationToken).ConfigureAwait(false);
            answer = await AskAsync(server, add, cancellationToken).ConfigureAwait(false);
        }

        return answer switch
        {
            { Answer.Kind: StructureAnswerKind.Done } => (null, true),
            { Answer: { Kind: StructureAnswerKind.TickingAreasFull } full } =>
                ($"BDS answered \"{full.Text}\" A piece needs a ticking area of its own while it is "
                    + "saved or put back; remove one with tickingarea remove and its name, which "
                    + "tickingarea list all-dimensions shows.", false),
            { Answer.Kind: StructureAnswerKind.TickingAreaNameTaken } => (Unanswered(answer, add), true),
            { Answer: null, NotSent: null } => (Unanswered(answer, add), true),
            _ => (Unanswered(answer, add), false),
        };
    }

    /// <summary>Probes until every chunk the ticking area holds is loaded; null when it is, else why not.</summary>
    private async Task<string?> ProbeAsync(
        IStructureServer server, BlockBox box, BedrockDimension dimension, CancellationToken cancellationToken)
    {
        var probe = StructureCommands.Probe(box, dimension);
        var giveUpAt = _time.GetUtcNow() + _timings.ProbeFor;

        while (true)
        {
            var answer = await AskAsync(server, probe, cancellationToken).ConfigureAwait(false);

            switch (answer.Answer?.Kind)
            {
                case StructureAnswerKind.Done:
                    return null;

                case StructureAnswerKind.NotLoaded when _time.GetUtcNow() >= giveUpAt:
                    return $"The area from {box} was still not loaded {_timings.ProbeFor.TotalSeconds:0} seconds "
                        + "after its ticking area was added.";

                case StructureAnswerKind.NotLoaded:
                    await Task.Delay(_timings.ProbeEvery, _time, cancellationToken).ConfigureAwait(false);
                    continue;

                default:
                    return Unanswered(answer, probe);
            }
        }
    }

    private async Task<string?> SaveAsync(
        IStructureServer server, StructurePiece piece, BedrockDimension dimension, CancellationToken cancellationToken)
    {
        var save = StructureCommands.Save(piece.Name, piece.Box, dimension);
        var answer = await AskAsync(server, save, cancellationToken).ConfigureAwait(false);

        return answer.Answer?.Kind == StructureAnswerKind.Done ? null : Unanswered(answer, save);
    }

    /// <summary>
    /// The most sweeps before a load. Measured: a sweep that killed six mobs left
    /// six drops, a second straight after killed those, and a third answered
    /// <em>"No targets matched selector"</em>. A box something keeps spawning into
    /// is still loaded after this many, with whatever the last sweep left.
    /// </summary>
    internal const int MaxSweeps = 4;

    /// <returns>Why not, or null; and what each sweep answered.</returns>
    private async Task<(string? Failure, IReadOnlyList<string> Sweeps)> SweepAndLoadAsync(
        IStructureServer server, StructurePiece piece, BedrockDimension dimension, CancellationToken cancellationToken)
    {
        var sweep = StructureCommands.Sweep(piece.Box, dimension);
        var sweeps = new List<string>();

        for (var round = 0; round < MaxSweeps; round++)
        {
            var swept = await AskAsync(server, sweep, cancellationToken).ConfigureAwait(false);

            if (swept.Answer?.Kind is not (StructureAnswerKind.Done or StructureAnswerKind.NothingToRemove))
            {
                return (Unanswered(swept, sweep), sweeps);
            }

            sweeps.Add(swept.Answer.Text);

            if (swept.Answer.Kind == StructureAnswerKind.NothingToRemove)
            {
                break;
            }
        }

        var load = StructureCommands.Load(piece.Name, piece.Box.Min, dimension);
        var loaded = await AskAsync(server, load, cancellationToken).ConfigureAwait(false);

        var failure = loaded.Answer?.Kind switch
        {
            StructureAnswerKind.Done => null,
            StructureAnswerKind.LoadQueued =>
                $"BDS queued the load until the area is loaded and does not say when it runs, so whether "
                + $"{piece.Name} was placed is not known (it answered \"{loaded.Answer.Text}\").",
            _ => Unanswered(loaded, load),
        };

        return (failure, sweeps);
    }

    /// <summary>Gives the ticking area back; null when it is gone, else why it may not be.</summary>
    private async Task<string?> RemoveTickingAreaAsync(IStructureServer server, BedrockDimension dimension)
    {
        var remove = StructureCommands.RemoveTickingArea(dimension);
        var answer = await AskAsync(server, remove, CancellationToken.None).ConfigureAwait(false);

        return answer.Answer?.Kind is StructureAnswerKind.Done or StructureAnswerKind.NoTickingArea
            ? null
            : Unanswered(answer, remove);
    }

    /// <summary>What a command came to: its answer, or why there is none.</summary>
    private readonly record struct Asked(StructureAnswer? Answer, string? NotSent);

    /// <summary>Sends one command and waits for its answer, as long as its step allows.</summary>
    /// <remarks>
    /// <para>Listening starts before the send (<see cref="ConsoleWatch"/>), so an
    /// answer faster than the caller cannot be missed.</para>
    ///
    /// <para>A cancelled pass sends nothing more, checked here rather than left to
    /// the send: once a command is written its answer is waited for, so that
    /// whether the ticking area was added is known when the pass gives it
    /// back.</para>
    /// </remarks>
    private async Task<Asked> AskAsync(
        IStructureServer server, StructureCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var watch = ConsoleWatch.For(server.Console, line => command.Read(line) is not null);

        try
        {
            await server.SendCommandAsync(command.Text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // Not running any more, or its input closed under the write: the
            // command was not sent (D87), and nothing will answer it.
            return new Asked(null, $"{server.Name} stopped before \"{command.Text}\" could be sent.");
        }

        var line = await watch.WaitAsync(Within(command), _time, CancellationToken.None).ConfigureAwait(false);

        return new Asked(line is null ? null : command.Read(line), null);
    }

    /// <summary>The sentence for an answer that is not the one the step needs.</summary>
    private string Unanswered(Asked asked, StructureCommand command) => asked switch
    {
        { NotSent: { } why } => why,
        { Answer: null } =>
            $"BDS did not answer \"{command.Text}\" within {Within(command).TotalSeconds:0} seconds.",
        { Answer: { } answer } => $"BDS answered \"{command.Text}\" with \"{answer.Text}\".",
    };

    private TimeSpan Within(StructureCommand command) => command.Step switch
    {
        StructureStep.Save => _timings.SaveAnswer,
        StructureStep.Load => _timings.LoadAnswer,
        _ => _timings.Answer,
    };

    private void Notice(IStructureServer server, string text) =>
        server.Console.Append(ConsoleOrigin.Notice, text, _time.GetUtcNow());

    private static string Verb(Pass pass) => pass == Pass.Save ? "save" : "put back";

    private static string Did(Pass pass) => pass == Pass.Save ? "Saved" : "Put back";
}
