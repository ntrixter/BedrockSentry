namespace BedrockSentry.Core.Instances;

/// <summary>
/// What a consumer got back from <see cref="ConsoleBuffer.Read"/>.
/// </summary>
/// <param name="Lines">The lines after the requested cursor, oldest first.</param>
/// <param name="Cursor">
/// The sequence to pass to the next call. Always advance to this rather than to
/// the last line's own sequence — they differ when the buffer was empty.
/// </param>
/// <param name="Gap">
/// True when lines between the caller's cursor and the oldest retained line were
/// evicted before the caller read them. Surfaced rather than swallowed
/// (<c>D16</c>): a console page that quietly stitches a gap shut shows a
/// plausible, wrong history, and the moment output is fastest is exactly when
/// this happens.
/// </param>
public readonly record struct ConsoleSnapshot(
    IReadOnlyList<ConsoleLine> Lines,
    long Cursor,
    bool Gap);

/// <summary>
/// A bounded, thread-safe ring of recent console output for one instance.
///
/// Bounded because a busy server produces output indefinitely and the wrapper is
/// a long-running service; the durable copy is the rotated log file on disk
/// (<c>docs/design.md</c> § 5.4), and this exists only to answer "what happened just
/// now" for the web view.
///
/// Reads are by sequence cursor, not by timestamp (<c>D16</c>, <c>docs/design.md</c>
/// § 5.4). The spike transcript has several lines sharing a millisecond, so a
/// timestamp cursor drops whichever lines tie with the last one seen — and only
/// under load.
/// </summary>
public sealed class ConsoleBuffer
{
    public const int DefaultCapacity = 2000;

    private readonly object _gate = new();
    private readonly Queue<ConsoleLine> _lines;
    private readonly int _capacity;

    private long _nextSequence = 1;

    /// <summary>
    /// What the classifier remembers between lines. Guarded by the same lock as
    /// the sequence number — see <see cref="Append(ConsoleOrigin, string, DateTimeOffset)"/>.
    /// </summary>
    private ConsoleLineClassifier.State _classifier;

    public ConsoleBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _capacity = capacity;
        _lines = new Queue<ConsoleLine>(capacity);
    }

    /// <summary>
    /// Raised for every line recorded, after the buffer has accepted it.
    /// The fan-out point for anything that wants a live feed rather than a poll —
    /// disk persistence today, the console page later.
    /// </summary>
    /// <remarks>
    /// Raised outside the buffer's lock, so a slow subscriber cannot back up the
    /// pipe reader that produced the line. The cost is that two threads appending
    /// at once may raise out of sequence; subscribers that care read
    /// <see cref="ConsoleLine.Sequence"/> rather than trusting arrival order.
    /// </remarks>
    public event Action<ConsoleLine>? LineAppended;

    /// <summary>The sequence of the most recent line, or 0 if nothing has been written.</summary>
    public long LatestSequence
    {
        get { lock (_gate) { return _nextSequence - 1; } }
    }

    public int Count
    {
        get { lock (_gate) { return _lines.Count; } }
    }

    /// <summary>
    /// Records a line and returns it. <paramref name="receivedUtc"/> is a
    /// parameter rather than read from the clock here so that ordering can be
    /// asserted in tests without sleeping.
    /// </summary>
    /// <param name="level">
    /// Overrides the level the text parses to. For the wrapper's own lines only:
    /// they are not BDS log lines, so nothing in the words says how serious they
    /// are, and without this a notice saying the world may be unflushed rendered
    /// in the same accent colour as "backup finished; world saves resumed"
    /// (<c>T170</c>). Severity outranks origin in the console's own rules, so
    /// stating it here is all that is needed — the glyph and the colour follow.
    /// </param>
    public ConsoleLine Append(
        ConsoleOrigin origin, string raw, DateTimeOffset receivedUtc, BdsLogLevel? level = null)
    {
        var (parsedLevel, message, timestamp) = BdsLogParser.Parse(raw);

        // Classified once, here, where the line is recorded — not at render
        // time. Every reader then agrees about what is chat: the console page,
        // the transcript on disk, and anything later that wants to count it.
        // Deciding per-viewer would let two views of one buffer disagree.
        if (origin == ConsoleOrigin.Stdout && ChatParser.TryParse(message, out var chat))
        {
            origin = ConsoleOrigin.Chat;
            message = $"{chat.Player}: {chat.Text}";
        }

        ConsoleLine line;
        lock (_gate)
        {
            // Under the same lock as the sequence number, because it carries
            // state from the previous line and two threads interleaving here
            // would attribute a warning's continuation to the wrong block.
            var (kind, classifiedLevel) =
                ConsoleLineClassifier.Classify(origin, parsedLevel, message, ref _classifier);

            // A stated level wins. The classifier reads the server's words to
            // decide what they mean; a wrapper line's severity is not in its
            // words, it is known by whoever wrote it.
            var effectiveLevel = level ?? classifiedLevel;

            line = new ConsoleLine(
                _nextSequence++,
                receivedUtc,
                origin,
                raw ?? string.Empty,
                effectiveLevel,
                message,
                timestamp,
                kind);

            _lines.Enqueue(line);
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
            }
        }

        Notify(line);
        return line;
    }

    /// <summary>
    /// Invokes subscribers one at a time so a throwing one cannot stop the
    /// others. A plain multicast invoke stops at the first exception, which would
    /// let a failing log writer silently cut off every other subscriber.
    /// </summary>
    private void Notify(ConsoleLine line)
    {
        var handlers = LineAppended;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<ConsoleLine>)handler)(line);
            }
            catch
            {
                // No logger here by design — Core takes no dependencies. The
                // subscriber is the only party that can meaningfully report its
                // own failure, and dropping a console line must never propagate
                // back into the pipe reader.
            }
        }
    }

    public ConsoleLine Append(ConsoleOrigin origin, string raw) =>
        Append(origin, raw, DateTimeOffset.UtcNow);

    /// <summary>
    /// Returns everything recorded after <paramref name="afterSequence"/>. Pass 0
    /// for a first read, then the previous call's <see cref="ConsoleSnapshot.Cursor"/>.
    /// </summary>
    public ConsoleSnapshot Read(long afterSequence, int maxLines = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);

        lock (_gate)
        {
            if (_lines.Count == 0)
            {
                return new ConsoleSnapshot([], _nextSequence - 1, false);
            }

            var oldest = _lines.Peek().Sequence;

            // A first read (cursor 0) is not a gap even when older lines have
            // already been evicted — the caller never had them to lose.
            var gap = afterSequence > 0 && afterSequence < oldest - 1;

            var selected = new List<ConsoleLine>();
            foreach (var line in _lines)
            {
                if (line.Sequence <= afterSequence)
                {
                    continue;
                }

                selected.Add(line);
                if (selected.Count == maxLines)
                {
                    break;
                }
            }

            // Advance only as far as was actually handed over, or a capped read
            // would skip everything it did not return.
            var cursor = selected.Count > 0 ? selected[^1].Sequence : _nextSequence - 1;
            return new ConsoleSnapshot(selected, cursor, gap);
        }
    }

    /// <summary>
    /// Drops retained lines without resetting the sequence. Sequences stay
    /// monotonic across a clear so a page holding an old cursor sees a gap
    /// rather than silently replaying the next server's output as its own.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
    }
}
