using System.Text;
using System.Threading.Channels;
using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Persists one instance's console output to disk with size-based rotation.
///
/// The point of persisting at all is that the output an administrator most wants
/// is the output from just before a crash (<c>docs/design.md</c> § 5.4) — which is
/// exactly what the in-memory ring loses when the wrapper restarts.
///
/// Writes go through a channel and a single background task rather than straight
/// to the file. The alternative is writing on the thread that read the pipe,
/// which puts a disk stall directly in the path of the server's own stdout: the
/// pipe fills, BDS blocks on its next write, and a slow disk turns into a hung
/// Minecraft server.
/// </summary>
public sealed class ConsoleLogWriter : IAsyncDisposable
{
    private readonly ConsoleBuffer _buffer;
    private readonly string _directory;
    private readonly ConsoleLogOptions _options;
    private readonly ILogger _log;
    private readonly Channel<ConsoleLine> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;

    /// <summary>
    /// How long to keep trying to open the transcript, how often, and how long to
    /// leave it alone after a whole window has been spent without success.
    /// </summary>
    /// <remarks>
    /// Sized for the thing that actually holds it: an outgoing wrapper process that
    /// has not finished exiting, which is seconds rather than minutes. The cooldown
    /// keeps a genuinely unwritable transcript — a full disk, a permission change —
    /// from spending the whole window again on every line that arrives.
    /// </remarks>
    public static readonly TimeSpan DefaultOpenRetryFor = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultOpenRetryEvery = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan DefaultOpenCooldown = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _openRetryFor;
    private readonly TimeSpan _openRetryEvery;
    private readonly TimeSpan _openCooldown;

    private StreamWriter? _writer;
    private long _written;
    private bool _disposed;

    /// <summary>When an open may next be attempted, after a window was spent failing.</summary>
    private DateTimeOffset _retryOpenAfter = DateTimeOffset.MinValue;

    /// <summary>Whether the "cannot write" error has already been reported.</summary>
    private bool _reportedUnavailable;

    public ConsoleLogWriter(
        string instanceId,
        ConsoleBuffer buffer,
        string directory,
        ConsoleLogOptions options,
        ILogger logger,
        TimeSpan? openRetryFor = null,
        TimeSpan? openRetryEvery = null,
        TimeSpan? openCooldown = null)
    {
        _buffer = buffer;
        _directory = directory;
        _options = options;
        _log = logger;
        _openRetryFor = openRetryFor ?? DefaultOpenRetryFor;
        _openRetryEvery = openRetryEvery ?? DefaultOpenRetryEvery;
        _openCooldown = openCooldown ?? DefaultOpenCooldown;

        // Unbounded, but drained by a dedicated task that only ever appends to a
        // file. A bounded channel would have to choose between blocking the pipe
        // reader and dropping the lines nearest a crash, and both are worse than
        // a briefly large queue.
        _queue = Channel.CreateUnbounded<ConsoleLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "console.log");

        _buffer.LineAppended += OnLine;
        _pump = Task.Run(() => PumpAsync(_shutdown.Token));

        _log.LogInformation("Console transcript for {InstanceId} at {Path}", instanceId, Path);
    }

    /// <summary>The current (unrotated) transcript file.</summary>
    public string Path { get; }

    private void OnLine(ConsoleLine line) => _queue.Writer.TryWrite(line);

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await WriteAsync(line).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the drain in DisposeAsync has already run.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Console transcript writer for {Path} stopped", Path);
        }
    }

    private async Task WriteAsync(ConsoleLine line)
    {
        try
        {
            if (_writer is null)
            {
                if (DateTimeOffset.UtcNow < _retryOpenAfter)
                {
                    // Inside the cooldown from an open that already failed its whole
                    // window. Dropping this line beats spending that window again on
                    // every line of a burst.
                    return;
                }

                _writer = await OpenWriterAsync().ConfigureAwait(false);

                if (_writer is null)
                {
                    return;
                }
            }

            // The wrapper's own notices and the commands it sent are marked, so a
            // transcript read months later still distinguishes what the server
            // said from what we told it to do.
            var prefix = line.Origin switch
            {
                ConsoleOrigin.Command => "> ",
                ConsoleOrigin.Notice => "# ",
                ConsoleOrigin.Stderr => "! ",
                ConsoleOrigin.Launch => "=== ",

                // Marked, and always written. Hiding chat is a console setting;
                // the transcript is the record, and a record with the
                // conversation removed is no use to whoever reads it after an
                // incident.
                ConsoleOrigin.Chat => "chat ",
                _ => string.Empty,
            };

            var text = prefix + line.Raw;
            await _writer.WriteLineAsync(text).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);

            _written += Encoding.UTF8.GetByteCount(text) + Environment.NewLine.Length;
            if (_written >= _options.MaxFileBytes)
            {
                Rotate();
            }
        }
        catch (Exception ex)
        {
            // A transcript that cannot be written is a degraded log, not a
            // degraded server. Drop the writer so the next line reopens it —
            // that recovers on its own from a transient lock or a full disk.
            _log.LogError(ex, "Failed to write the console transcript at {Path}", Path);
            TryCloseWriter();
        }
    }

    /// <summary>
    /// Opens the transcript, waiting a little for whatever holds it to let go.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OpenWriter"/> asks for write access while sharing only read and
    /// delete, so a second <em>writer</em> is refused — and on a service restart
    /// there is one. Windows reports a service stopped before its process has exited
    /// (measured in <c>T109</c>), so the outgoing wrapper can still hold this file
    /// while the incoming one opens it.
    /// </para>
    /// <para>
    /// The previous behaviour dropped the line and let the next one try again
    /// immediately. The lines of a server's startup banner arrive orders of magnitude
    /// faster than a process exits, so the retry never got ahead of the burst: the
    /// whole banner went missing from the transcript and 48 identical errors went
    /// into the event log for one restart (<c>T106</c>).
    /// </para>
    /// <para>
    /// Waiting here is safe, and this is the one place it is. It runs on the pump
    /// rather than the thread reading the server's stdout — the distinction this
    /// class exists for — and the queue behind it is unbounded, so the lines that
    /// arrive while it waits are kept rather than lost. That is what turns a dropped
    /// banner into a delayed one.
    /// </para>
    /// </remarks>
    private async Task<StreamWriter?> OpenWriterAsync()
    {
        var deadline = DateTimeOffset.UtcNow + _openRetryFor;
        Exception? failure = null;

        while (true)
        {
            try
            {
                var opened = OpenWriter();

                if (_reportedUnavailable)
                {
                    _reportedUnavailable = false;
                    _log.LogInformation("Console transcript at {Path} can be written again", Path);
                }

                return opened;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }

            try
            {
                await Task.Delay(_openRetryEvery, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        _retryOpenAfter = DateTimeOffset.UtcNow + _openCooldown;

        // Once per outage, not once per line. Logging every dropped line is why a
        // single restart put 48 identical events in the Application log, which buried
        // the one fact worth knowing: the transcript is not being written.
        if (!_reportedUnavailable)
        {
            _reportedUnavailable = true;
            _log.LogError(
                failure,
                "Could not open the console transcript at {Path} within {Seconds} seconds. "
                + "Lines are being dropped until it can be opened again.",
                Path,
                _openRetryFor.TotalSeconds);
        }

        return null;
    }

    private StreamWriter OpenWriter()
    {
        var info = new FileInfo(Path);
        _written = info.Exists ? info.Length : 0;

        // FileShare.Read so an administrator can tail the file while it is being
        // written; without it the obvious thing to do with a log is locked out.
        var stream = new FileStream(
            Path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);

        return new StreamWriter(stream, new UTF8Encoding(false));
    }

    private void Rotate()
    {
        TryCloseWriter();

        try
        {
            // Oldest first, so nothing is overwritten before it has been shifted.
            var oldest = System.IO.Path.Combine(_directory, $"console.{_options.MaxFiles}.log");
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var index = _options.MaxFiles - 1; index >= 1; index--)
            {
                var from = System.IO.Path.Combine(_directory, $"console.{index}.log");
                if (File.Exists(from))
                {
                    File.Move(from, System.IO.Path.Combine(_directory, $"console.{index + 1}.log"), overwrite: true);
                }
            }

            if (File.Exists(Path))
            {
                File.Move(Path, System.IO.Path.Combine(_directory, "console.1.log"), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to rotate the console transcript at {Path}", Path);
        }

        _written = 0;
    }

    private void TryCloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (IOException)
        {
            // Already broken; nothing useful to do with a failure to close.
        }

        _writer = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _buffer.LineAppended -= OnLine;
        _queue.Writer.TryComplete();

        // Drain before cancelling. The lines queued during a shutdown are the
        // ones describing the shutdown, and losing those defeats the purpose of
        // writing a transcript at all.
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogWarning("Console transcript writer for {Path} did not drain in time", Path);
        }
        catch (OperationCanceledException)
        {
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        TryCloseWriter();
    }
}
