namespace BedrockSentry.Core.Instances;

/// <summary>
/// Waits for one line matching a predicate to arrive on a console, by
/// subscribing rather than polling.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because polling for a reply is not safe on a bounded buffer.</b>
/// The backup orchestrator used to take a cursor, send its command, and re-read
/// <see cref="ConsoleBuffer.Read"/> every 250ms looking for the answer.
/// <see cref="ConsoleBuffer"/> is a ring, and <see cref="ConsoleSnapshot.Gap"/>
/// exists to say that lines were evicted before the reader got to them — its own
/// summary warns that <em>"the moment output is fastest is exactly when this
/// happens"</em> (<c>D16</c>). Neither backup loop looked at that flag. So a
/// confirmation that arrived during a burst — a Bedrock startup after an update
/// being the fastest output this wrapper ever sees — was appended, evicted, and
/// never matched, and the wrapper reported a resume it had in fact been told
/// about (<c>T170</c>).
/// </para>
/// <para>
/// Subscribing removes the whole family rather than tuning it. There is no
/// cursor to be stale, no window to be blind in, and no interaction with the
/// buffer's capacity at all: <see cref="ConsoleBuffer.LineAppended"/> is
/// documented as the fan-out point for exactly this, and the line is examined as
/// it is recorded.
/// </para>
/// <para>
/// <b>Create it before sending the command.</b> That ordering is the point of the
/// type: a watch that is constructed, then used to send, then awaited cannot miss
/// a reply that comes back faster than the caller can start listening. The shape
/// makes it awkward to get wrong, which a "wait for this line" method taking a
/// send callback would not.
/// </para>
/// </remarks>
public sealed class ConsoleWatch : IDisposable
{
    private readonly ConsoleBuffer _console;
    private readonly Func<ConsoleLine, bool> _matches;

    private readonly TaskCompletionSource<ConsoleLine> _matched =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _disposed;

    private ConsoleWatch(ConsoleBuffer console, Func<ConsoleLine, bool> matches)
    {
        _console = console;
        _matches = matches;

        _console.LineAppended += OnLine;
    }

    /// <summary>Starts listening. Send the command <em>after</em> this returns.</summary>
    public static ConsoleWatch For(ConsoleBuffer console, Func<ConsoleLine, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(matches);

        return new ConsoleWatch(console, matches);
    }

    /// <summary>
    /// The matching line, or null if <paramref name="timeout"/> passed first.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception because every caller so far has something
    /// specific to say about not being answered — the resume has to report an
    /// outcome and carry on regardless, since it runs inside the finally that
    /// protects the save invariant and must never throw.
    /// </remarks>
    public async Task<ConsoleLine?> WaitAsync(
        TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(time);

        if (_matched.Task.IsCompleted)
        {
            return _matched.Task.Result;
        }

        using var expiry = new CancellationTokenSource();

        try
        {
            var elapsed = Task.Delay(timeout, time, expiry.Token);
            var first = await Task.WhenAny(_matched.Task, elapsed).ConfigureAwait(false);

            if (first == _matched.Task)
            {
                return _matched.Task.Result;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        finally
        {
            // Otherwise a short wait against a long timeout leaves a timer
            // holding this object for the rest of the timeout — and the manifest
            // loop waits in slices, so it would leave one per slice.
            expiry.Cancel();
        }
    }

    private void OnLine(ConsoleLine line)
    {
        // The predicate is somebody else's code running on the pipe reader's
        // thread. A throw here would travel back into ConsoleBuffer's fan-out and
        // take out the reader that produced the line, which is a far worse
        // outcome than failing to match one.
        try
        {
            if (_matches(line))
            {
                _matched.TrySetResult(line);
            }
        }
        catch (Exception)
        {
            // Deliberately swallowed: a predicate that throws is a bug in the
            // caller, and the wait will time out and be reported as unanswered.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _console.LineAppended -= OnLine;
        }
    }
}
