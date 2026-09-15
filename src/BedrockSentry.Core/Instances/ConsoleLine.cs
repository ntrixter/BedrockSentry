namespace BedrockSentry.Core.Instances;

/// <summary>
/// Where a console line came from. The wrapper's own messages and the commands
/// it sent are kept in the same stream as the server's output rather than in a
/// side channel: when a server misbehaves, "what did we send it, and when" is
/// half the answer, and an operator should not have to correlate two logs by
/// timestamp to get it.
/// </summary>
public enum ConsoleOrigin
{
    /// <summary>The server's standard output.</summary>
    Stdout,

    /// <summary>The server's standard error.</summary>
    Stderr,

    /// <summary>A command the wrapper wrote to the server's standard input.</summary>
    Command,

    /// <summary>The wrapper talking about the server — starting, exited, backing off.</summary>
    Notice,

    /// <summary>
    /// A new run of the server begins here.
    /// </summary>
    /// <remarks>
    /// Its own origin rather than another notice because it is the one line that
    /// is a boundary rather than a remark. A restart puts hundreds of lines of
    /// startup into a console that already held hundreds from the last run, and
    /// they look identical — without a rule across the page it is genuinely hard
    /// to tell which run you are reading, and easy to diagnose a fault from the
    /// previous one.
    /// </remarks>
    Launch,

    /// <summary>Something a player said in game.</summary>
    /// <remarks>
    /// Its own origin so the console can show or hide it without touching what
    /// is recorded. A busy server's chat drowns the operational output an
    /// administrator opened the console to read, and an administrator watching a
    /// restart does not want to scroll past small talk — but chat is also
    /// exactly what you want when moderating, so neither answer is right for
    /// everybody all the time. Classifying it here makes that a view decision
    /// rather than a capture decision.
    /// </remarks>
    Chat,
}

/// <summary>
/// Severity as reported by BDS itself. <see cref="Unknown"/> is not a failure
/// case: plenty of real output carries no level at all (see
/// <see cref="BdsLogParser"/>), and treating that as an error would misreport
/// the majority of a save manifest.
/// </summary>
public enum BdsLogLevel
{
    Unknown,
    Info,
    Warn,
    Error,
}

/// <summary>
/// One line of instance console output, as stored in the ring buffer and shown
/// on the console page.
/// </summary>
/// <param name="Sequence">
/// Monotonic per-instance counter. The UI polls "everything after N" rather
/// than by timestamp — timestamps are not unique at this resolution (the spike
/// transcript has several lines sharing a millisecond), so a timestamp cursor
/// silently drops lines.
/// </param>
/// <param name="ReceivedUtc">When the wrapper read the line, not when BDS wrote it.</param>
/// <param name="Origin">Which stream produced it.</param>
/// <param name="Raw">The line exactly as received, prefix and all. Persisted verbatim.</param>
/// <param name="Level">Severity, if the line carried one.</param>
/// <param name="Message">
/// The line with its <c>[timestamp LEVEL]</c> prefix removed, or the whole line
/// when there was no prefix. This is what the console page displays.
/// </param>
/// <param name="ServerTimestamp">
/// The timestamp BDS stamped on the line, if any. Kept separate from
/// <see cref="ReceivedUtc"/> because it is local time with no offset and cannot
/// be compared across machines or across a DST boundary.
/// </param>
/// <param name="Kind">
/// What the line is about, decided as it enters the buffer.
/// </param>
/// <remarks>
/// Classified here rather than when the console renders because two of the
/// rules need to see the line before it: a wrapped continuation inside a
/// warning block carries no level of its own, and Bedrock's boxed notices run
/// several lines between one banner and the next. A render-time classifier
/// would have to rescan the buffer from the top on every frame to know either.
/// Deciding once also means every reader agrees, the way chat already does.
/// It reaches no further than the view — the transcript on disk is still the
/// raw bytes the server wrote.
/// </remarks>
public sealed record ConsoleLine(
    long Sequence,
    DateTimeOffset ReceivedUtc,
    ConsoleOrigin Origin,
    string Raw,
    BdsLogLevel Level,
    string Message,
    DateTime? ServerTimestamp,
    ConsoleLineKind Kind = ConsoleLineKind.Ordinary);
