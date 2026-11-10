namespace BedrockSentry.Core.Instances;

/// <summary>
/// Collects one answer a command prints over several lines, by subscribing as
/// <see cref="ConsoleWatch"/> does: from the line that starts it to the line that ends it.
/// </summary>
/// <remarks>
/// <para>BDS prints the first line of a multi-line answer with its timestamp and level and
/// the rest bare, as <c>tickingarea list all-dimensions</c> does on 1.26.52.3 (<c>D107</c>).
/// Between the start and the end only bare lines (<see cref="BdsLogLevel.Unknown"/>) are
/// kept, so a player joining while the answer prints does not land inside it.</para>
///
/// <para>Create it before sending the command, for the reason <see cref="ConsoleWatch"/>
/// gives. A start line that is also an end, such as a one-line refusal, completes it.</para>
/// </remarks>
public sealed class ConsoleCollect : IDisposable
{
    private readonly ConsoleBuffer _console;
    private readonly Func<ConsoleLine, bool> _starts;
    private readonly Func<ConsoleLine, bool> _ends;
    private readonly List<ConsoleLine> _lines = [];
    private readonly object _gate = new();

    private readonly TaskCompletionSource<IReadOnlyList<ConsoleLine>> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _disposed;

    private ConsoleCollect(ConsoleBuffer console, Func<ConsoleLine, bool> starts, Func<ConsoleLine, bool> ends)
    {
        _console = console;
        _starts = starts;
        _ends = ends;
        _console.LineAppended += OnLine;
    }

    /// <summary>Starts listening. Send the command <em>after</em> this returns.</summary>
    public static ConsoleCollect For(ConsoleBuffer console, Func<ConsoleLine, bool> starts, Func<ConsoleLine, bool> ends)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(starts);
        ArgumentNullException.ThrowIfNull(ends);

        return new ConsoleCollect(console, starts, ends);
    }

    /// <summary>The answer's lines, start and end included, or null if <paramref name="timeout"/> passed first.</summary>
    public async Task<IReadOnlyList<ConsoleLine>?> WaitAsync(TimeSpan timeout, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        using var expiry = new CancellationTokenSource();

        try
        {
            var elapsed = Task.Delay(timeout, time, expiry.Token);
            var first = await Task.WhenAny(_done.Task, elapsed).ConfigureAwait(false);
            return first == _done.Task ? _done.Task.Result : null;
        }
        finally
        {
            expiry.Cancel();
        }
    }

    private void OnLine(ConsoleLine line)
    {
        if (line.Origin is not (ConsoleOrigin.Stdout or ConsoleOrigin.Stderr))
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                if (_done.Task.IsCompleted)
                {
                    return;
                }

                if (_lines.Count == 0)
                {
                    if (!_starts(line))
                    {
                        return;
                    }
                }
                else if (line.Level != BdsLogLevel.Unknown && !_ends(line))
                {
                    return;
                }

                _lines.Add(line);

                if (_ends(line))
                {
                    _done.TrySetResult([.. _lines]);
                }
            }
        }
        catch (Exception)
        {
            // A predicate that throws is the caller's bug; the wait times out and says so,
            // rather than the throw taking out the reader that produced the line.
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
