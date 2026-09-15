using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

public sealed class ConsoleLogWriterTests : IDisposable
{
    private readonly string _directory;

    public ConsoleLogWriterTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "bedrocksentry-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ConsoleLogWriter Create(ConsoleBuffer buffer, ConsoleLogOptions? options = null) =>
        new("test", buffer, _directory, options ?? new ConsoleLogOptions(),
            NullLogger<ConsoleLogWriter>.Instance);

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private ConsoleLogWriter CreateWithRetry(
        ConsoleBuffer buffer,
        TimeSpan? openRetryFor = null,
        TimeSpan? openRetryEvery = null,
        TimeSpan? openCooldown = null) =>
        new("test", buffer, _directory, new ConsoleLogOptions(),
            NullLogger<ConsoleLogWriter>.Instance, openRetryFor, openRetryEvery, openCooldown);

    /// <summary>
    /// Stands in for the outgoing wrapper process: a handle that shares read and
    /// delete but not write, which is exactly what the writer itself asks for and
    /// exactly what refuses a second writer.
    /// </summary>
    /// <remarks>
    /// Windows enforces this; on Unix the sharing mode is not enforced the same way,
    /// so these read as trivially passing there rather than as failures. CI runs on
    /// windows-latest, which is where the behaviour being pinned down lives.
    /// </remarks>
    private FileStream HoldTranscript() =>
        new(Path.Combine(_directory, "console.log"),
            FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);

    /// <summary>
    /// `T106`. On a service restart there really is a second writer: Windows reports
    /// a service stopped before its process has exited (`T109`), so the outgoing
    /// wrapper can still hold this file while the incoming one starts.
    /// </summary>
    /// <remarks>
    /// The lines arriving in that window are a server's startup banner — the version,
    /// the pack list, the ports — and they used to be dropped one at a time, because
    /// a failed open discarded the line and let the next one try again immediately.
    /// Banner lines arrive far faster than a process exits, so the retry never got
    /// ahead of the burst: 48 lines lost and 48 identical errors logged, for one
    /// restart. Waiting on the pump instead keeps them, because the queue behind it
    /// is unbounded.
    /// </remarks>
    [Fact]
    public async Task The_startup_banner_survives_a_transient_lock_on_the_transcript()
    {
        var buffer = new ConsoleBuffer();
        var path = Path.Combine(_directory, "console.log");
        var holder = HoldTranscript();

        await using var writer = CreateWithRetry(
            buffer,
            openRetryFor: TimeSpan.FromSeconds(10),
            openRetryEvery: TimeSpan.FromMilliseconds(25));

        buffer.Append(ConsoleOrigin.Stdout, "[INFO] Starting Server");
        buffer.Append(ConsoleOrigin.Stdout, "[INFO] Version: 1.26.44.3");
        buffer.Append(ConsoleOrigin.Stdout, "[INFO] Server started.");

        // Long enough that the old code would have burned through all three.
        await Task.Delay(200);

        // The process finally exits.
        holder.Dispose();

        Assert.True(
            await WaitFor(() => File.Exists(path) && ReadAll(path).Contains("Server started.")),
            "the banner never reached the transcript");

        var text = ReadAll(path);
        Assert.Contains("Starting Server", text);
        Assert.Contains("Version: 1.26.44.3", text);
    }

    /// <summary>
    /// A transcript that cannot be opened for a whole window is left alone for a
    /// while rather than retried per line — and comes back on its own afterwards.
    /// </summary>
    [Fact]
    public async Task It_comes_back_after_giving_up_once_the_lock_clears()
    {
        var buffer = new ConsoleBuffer();
        var path = Path.Combine(_directory, "console.log");
        var holder = HoldTranscript();

        await using var writer = CreateWithRetry(
            buffer,
            openRetryFor: TimeSpan.FromMilliseconds(150),
            openRetryEvery: TimeSpan.FromMilliseconds(25),
            openCooldown: TimeSpan.FromMilliseconds(150));

        buffer.Append(ConsoleOrigin.Stdout, "arrives while it is locked");
        await Task.Delay(400);

        holder.Dispose();
        await Task.Delay(250);

        buffer.Append(ConsoleOrigin.Stdout, "arrives after it clears");

        Assert.True(
            await WaitFor(() => File.Exists(path) && ReadAll(path).Contains("arrives after it clears")),
            "the writer never recovered after giving up");
    }

    [Fact]
    public async Task Writes_output_to_disk()
    {
        var buffer = new ConsoleBuffer();
        await using var writer = Create(buffer);

        buffer.Append(ConsoleOrigin.Stdout, "[2026-08-03 07:00:16:314 INFO] Server started.");

        Assert.True(await WaitFor(() => File.Exists(writer.Path) && ReadAll(writer.Path).Length > 0));
        Assert.Contains("Server started.", ReadAll(writer.Path));
    }

    // A transcript read months later still has to distinguish what the server
    // said from what the wrapper told it to do.
    [Fact]
    public async Task Marks_commands_and_notices_distinctly()
    {
        var buffer = new ConsoleBuffer();
        await using var writer = Create(buffer);

        buffer.Append(ConsoleOrigin.Launch, "starting (pid 42)");
        buffer.Append(ConsoleOrigin.Notice, "stopping (restart)");
        buffer.Append(ConsoleOrigin.Command, "save hold");
        buffer.Append(ConsoleOrigin.Stdout, "Saving...");
        buffer.Append(ConsoleOrigin.Stderr, "something went wrong");

        Assert.True(await WaitFor(() => ReadAll(writer.Path).Contains("something went wrong")));

        var text = ReadAll(writer.Path);

        // The run boundary is marked differently from an ordinary notice, so a
        // transcript spanning several restarts can be split back into runs.
        Assert.Contains("=== starting (pid 42)", text);
        Assert.Contains("# stopping (restart)", text);
        Assert.Contains("> save hold", text);
        Assert.Contains("! something went wrong", text);
        Assert.Contains("\nSaving...", text);
    }

    [Fact]
    public async Task Rotates_when_the_file_grows_past_the_limit()
    {
        var buffer = new ConsoleBuffer();
        await using var writer = Create(buffer, new ConsoleLogOptions { MaxFileBytes = 200, MaxFiles = 3 });

        for (var i = 0; i < 40; i++)
        {
            buffer.Append(ConsoleOrigin.Stdout, $"line {i} padded out to force a rotation soon");
        }

        Assert.True(await WaitFor(() => File.Exists(Path.Combine(_directory, "console.1.log"))));
    }

    [Fact]
    public async Task Keeps_no_more_than_the_configured_number_of_files()
    {
        var buffer = new ConsoleBuffer();
        await using var writer = Create(buffer, new ConsoleLogOptions { MaxFileBytes = 100, MaxFiles = 2 });

        for (var i = 0; i < 200; i++)
        {
            buffer.Append(ConsoleOrigin.Stdout, $"line {i} with enough text to roll the file over");
        }

        // Give the pump time to finish rotating before counting.
        await WaitFor(() => Directory.GetFiles(_directory, "console.*.log").Length >= 2);
        await Task.Delay(200);

        var rotated = Directory.GetFiles(_directory, "console.?.log");
        Assert.True(rotated.Length <= 2, $"expected at most 2 rotated files, found {rotated.Length}");
    }

    // The lines queued during a shutdown are the ones describing the shutdown.
    // Losing them defeats the purpose of writing a transcript at all.
    [Fact]
    public async Task Drains_queued_lines_on_dispose()
    {
        var buffer = new ConsoleBuffer();
        var writer = Create(buffer);

        for (var i = 0; i < 50; i++)
        {
            buffer.Append(ConsoleOrigin.Stdout, $"shutdown line {i}");
        }

        await writer.DisposeAsync();

        var text = ReadAll(writer.Path);
        Assert.Contains("shutdown line 0", text);
        Assert.Contains("shutdown line 49", text);
    }

    [Fact]
    public async Task Stops_recording_once_disposed()
    {
        var buffer = new ConsoleBuffer();
        var writer = Create(buffer);

        buffer.Append(ConsoleOrigin.Stdout, "before dispose");
        await writer.DisposeAsync();

        buffer.Append(ConsoleOrigin.Stdout, "after dispose");
        await Task.Delay(150);

        var text = ReadAll(writer.Path);
        Assert.Contains("before dispose", text);
        Assert.DoesNotContain("after dispose", text);
    }

    [Fact]
    public async Task Appends_to_an_existing_transcript_rather_than_truncating_it()
    {
        var path = Path.Combine(_directory, "console.log");
        await File.WriteAllTextAsync(path, "from a previous run\n");

        var buffer = new ConsoleBuffer();
        var writer = Create(buffer);
        buffer.Append(ConsoleOrigin.Stdout, "from this run");
        await writer.DisposeAsync();

        var text = ReadAll(path);
        Assert.Contains("from a previous run", text);
        Assert.Contains("from this run", text);
    }

    // An administrator's first instinct is to tail the file; the writer must not
    // lock them out of it.
    [Fact]
    public async Task The_transcript_can_be_read_while_it_is_being_written()
    {
        var buffer = new ConsoleBuffer();
        await using var writer = Create(buffer);

        buffer.Append(ConsoleOrigin.Stdout, "first");
        Assert.True(await WaitFor(() => ReadAll(writer.Path).Contains("first")));

        buffer.Append(ConsoleOrigin.Stdout, "second");
        Assert.True(await WaitFor(() => ReadAll(writer.Path).Contains("second")));
    }

    private static string ReadAll(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public class ConsoleBufferNotificationTests
{
    [Fact]
    public void Subscribers_receive_every_line()
    {
        var buffer = new ConsoleBuffer();
        var seen = new List<ConsoleLine>();
        buffer.LineAppended += seen.Add;

        buffer.Append(ConsoleOrigin.Stdout, "one");
        buffer.Append(ConsoleOrigin.Stdout, "two");

        Assert.Equal(["one", "two"], seen.Select(l => l.Raw));
    }

    // The same isolation the process pipe needs: a plain multicast invoke stops
    // at the first handler that throws, so a failing log writer would silently
    // cut off every subscriber registered after it.
    [Fact]
    public void A_throwing_subscriber_does_not_starve_the_others()
    {
        var buffer = new ConsoleBuffer();
        var seen = new List<string>();

        buffer.LineAppended += _ => throw new InvalidOperationException("bad subscriber");
        buffer.LineAppended += line => seen.Add(line.Raw);

        buffer.Append(ConsoleOrigin.Stdout, "still delivered");

        Assert.Equal(["still delivered"], seen);
    }

    [Fact]
    public void Unsubscribing_stops_delivery()
    {
        var buffer = new ConsoleBuffer();
        var seen = new List<string>();
        void Handler(ConsoleLine line) => seen.Add(line.Raw);

        buffer.LineAppended += Handler;
        buffer.Append(ConsoleOrigin.Stdout, "before");
        buffer.LineAppended -= Handler;
        buffer.Append(ConsoleOrigin.Stdout, "after");

        Assert.Equal(["before"], seen);
    }
}
