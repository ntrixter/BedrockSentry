using BedrockSentry.Core.Commands;
using BedrockSentry.Core.Instances;

namespace BedrockSentry.Services.Commands;

/// <summary>The console of one throwaway run: commands in, each one's reply out.</summary>
/// <remarks>
/// <para>Commands go in batches, each followed by its own <see cref="ConsoleMarker"/>,
/// and a reply is every line between two markers. The server runs console commands one
/// after another and prints each reply before starting the next, so the order of lines
/// is the order of commands; nothing is timed.</para>
///
/// <para>A line printed between batches, such as a script's own log line, lands in the
/// reply of the next batch's first command. That is harmless to every reader here: a
/// help page keeps its form lines, a description its head, and a probe looks only for
/// a syntax error naming its value.</para>
/// </remarks>
public sealed class ThrowawaySession
{
    /// <summary>The most commands sent before waiting for their replies.</summary>
    public const int BatchSize = 40;

    /// <summary>How long one batch may take.</summary>
    public static readonly TimeSpan BatchBudget = TimeSpan.FromSeconds(60);

    private readonly IBedrockProcess _process;
    private readonly object _gate = new();
    private readonly List<string> _lines = [];
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _consumed;
    private int _nextMarker = 1;
    private int _awaitedMarker;
    private TaskCompletionSource? _batchDone;

    private readonly TimeSpan _batchBudget;

    /// <param name="batchBudget">How long one batch may take; <see cref="BatchBudget"/> when null.</param>
    public ThrowawaySession(IBedrockProcess process, TimeSpan? batchBudget = null)
    {
        _process = process;
        _batchBudget = batchBudget ?? BatchBudget;
        _process.OutputReceived += OnOutput;
    }

    /// <summary>The server, for a caller that drives its console line by line (<see cref="ThrowawayConsole"/>).</summary>
    internal IBedrockProcess Process => _process;

    /// <summary>What the server called itself on its <c>Version:</c> line.</summary>
    public string? Announced { get; private set; }

    /// <summary>Every line up to <c>Server started.</c>, which no command asked for.</summary>
    public IReadOnlyList<string> StartLines { get; private set; } = [];

    /// <summary>
    /// Waits for <c>Server started.</c>; null when it came, otherwise why it did not.
    /// </summary>
    public async Task<string?> WaitUntilStartedAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exited = _process.WaitForExitAsync(waiting.Token);
        var first = await Task.WhenAny(_started.Task, exited, Task.Delay(budget, waiting.Token)).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await waiting.CancelAsync().ConfigureAwait(false);

        if (first == _started.Task)
        {
            // The start is nobody's reply: without this, every line of it would be read
            // as the first command's.
            lock (_gate)
            {
                StartLines = _lines.GetRange(0, _lines.Count);
                _consumed = _lines.Count;
            }

            return null;
        }

        if (first == exited)
        {
            var code = exited.IsCompletedSuccessfully ? exited.Result.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
            return $"the server exited before it started, with exit code {code}{LastWords()}";
        }

        return $"the server did not start within {budget.TotalMinutes:0} minutes{LastWords()}";
    }

    /// <summary>Sends every command and returns each one's reply, in order.</summary>
    /// <exception cref="ThrowawayFailure">A batch did not finish within <see cref="BatchBudget"/>.</exception>
    public async Task<IReadOnlyList<ConsoleExchange>> ExchangeAsync(
        IReadOnlyList<string> commands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var exchanges = new List<ConsoleExchange>(commands.Count);
        for (var offset = 0; offset < commands.Count; offset += BatchSize)
        {
            var batch = commands.Skip(offset).Take(BatchSize).ToList();
            exchanges.AddRange(await BatchAsync(batch, cancellationToken).ConfigureAwait(false));
        }

        return exchanges;
    }

    private async Task<IReadOnlyList<ConsoleExchange>> BatchAsync(
        IReadOnlyList<string> commands, CancellationToken cancellationToken)
    {
        int first;
        TaskCompletionSource done;
        lock (_gate)
        {
            first = _nextMarker;
            _nextMarker += commands.Count;
            _awaitedMarker = first + commands.Count - 1;
            _batchDone = done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        for (var i = 0; i < commands.Count; i++)
        {
            await _process.SendAsync(commands[i], cancellationToken).ConfigureAwait(false);
            await _process.SendAsync(ConsoleMarker.Command(first + i), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await done.Task.WaitAsync(_batchBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var why = _process.HasExited ? "the server exited" : "the server stopped answering";
            throw new ThrowawayFailure(
                $"{why}: no reply to {commands[0]} or the {commands.Count - 1} commands after it within "
                + $"{_batchBudget.TotalSeconds:0} seconds");
        }

        List<string> lines;
        lock (_gate)
        {
            lines = _lines.GetRange(_consumed, _lines.Count - _consumed);
        }

        var exchanges = new List<ConsoleExchange>(commands.Count);
        var reply = new List<string>();
        var read = 0;
        foreach (var line in lines)
        {
            read++;
            if (ConsoleMarker.NumberIn(HelpPages.Message(line)) is { } n
                && n >= first && n < first + commands.Count)
            {
                // A marker can only close its own command and any before it whose
                // marker never came, which then reply with nothing.
                while (first + exchanges.Count <= n)
                {
                    exchanges.Add(new ConsoleExchange(commands[exchanges.Count], reply));
                    reply = [];
                }

                if (exchanges.Count == commands.Count)
                {
                    break;
                }

                continue;
            }

            reply.Add(line);
        }

        lock (_gate)
        {
            _consumed += read;
        }

        return exchanges;
    }

    private void OnOutput(ConsoleOrigin origin, string line)
    {
        var message = HelpPages.Message(line);

        lock (_gate)
        {
            _lines.Add(line);

            if (_batchDone is { } done && ConsoleMarker.NumberIn(message) == _awaitedMarker)
            {
                done.TrySetResult();
            }
        }

        if (message.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
        {
            Announced = message["Version:".Length..].Trim();
        }
        else if (message.StartsWith("Server started", StringComparison.OrdinalIgnoreCase))
        {
            _started.TrySetResult();
        }
    }

    // The last few lines a server printed say more than any sentence written here.
    private string LastWords()
    {
        lock (_gate)
        {
            var last = _lines
                .Select(HelpPages.Message)
                .Where(l => l.Trim().Length > 0)
                .TakeLast(1)
                .ToList();

            return last.Count == 0 ? string.Empty : $"; its last line was \"{last[0].Trim()}\"";
        }
    }
}
