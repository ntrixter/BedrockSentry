using System.Collections.Concurrent;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// Exercises <see cref="BedrockProcess"/> against a real child process with real
/// pipes. Encoding, blank-line handling, stdin flushing and exit codes are
/// exactly the things a mock would agree with and an operating system would not,
/// so the stand-in in tests/BedrockSentry.FakeServer speaks the protocol the
/// milestone 4 spike recorded from BDS 1.26.36.1.
/// </summary>
public sealed class BedrockProcessTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly string _workingDirectory;

    public BedrockProcessTests()
    {
        _workingDirectory = Path.Combine(
            Path.GetTempPath(), "bedrocksentry-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_workingDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A test that left the process running briefly can hold the folder;
            // a leaked temp directory is not worth failing a green run over.
        }
    }

    private static string FakeServerPath => Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "fake-bedrock-server.exe" : "fake-bedrock-server");

    private void Configure(params string[] settings) =>
        File.WriteAllLines(Path.Combine(_workingDirectory, "fake-server.conf"), settings);

    private (BedrockProcess Process, ConcurrentQueue<(ConsoleOrigin Origin, string Line)> Lines) Create()
    {
        var lines = new ConcurrentQueue<(ConsoleOrigin, string)>();
        var process = new BedrockProcess(
            "test", FakeServerPath, _workingDirectory, NullLogger<BedrockProcess>.Instance);
        process.OutputReceived += (origin, line) => lines.Enqueue((origin, line));
        return (process, lines);
    }

    private static async Task<bool> WaitFor(
        Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Patience);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    [Fact]
    public async Task Reports_startup_output_and_readiness()
    {
        var (process, lines) = Create();
        await using var _ = process;

        await process.StartAsync();

        Assert.True(await WaitFor(() => lines.Any(l => l.Line.Contains("Server started."))));
        Assert.NotNull(process.ProcessId);
        Assert.False(process.HasExited);

        await process.SendAsync("stop");
        Assert.Equal(0, await process.WaitForExitAsync());
    }

    // The whole product rests on this one round trip.
    [Fact]
    public async Task Round_trips_a_command_over_stdin()
    {
        var (process, lines) = Create();
        await using var _ = process;

        await process.StartAsync();
        Assert.True(await WaitFor(() => lines.Any(l => l.Line.Contains("Server started."))));

        await process.SendAsync("list");

        Assert.True(await WaitFor(() => lines.Any(l => l.Line.Contains("players online"))));

        await process.SendAsync("stop");
        await process.WaitForExitAsync();
    }

    /// <summary>
    /// The save handshake against the real double, including the reply `save resume`
    /// gives when nothing is held (`T174`).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The double's save path had no test at all, in either direction, while its own
    /// header claimed its replies were copied from a real transcript. One of them was
    /// not: `save resume` answered the confirmation whether or not a hold was
    /// outstanding, where BDS answers "A previous save has not been completed." if
    /// none is — verified by hand on 1.26.45.1.
    /// </para>
    /// <para>
    /// That is worth a test rather than a corrected comment, because of what the
    /// unfaithful double cost. A reviewer reasoned <em>from</em> it, found the
    /// orchestrator's note about the real reply unsupported, and concluded a correct
    /// comment was unverified prose. A double nobody checks is an authority nobody
    /// can appeal.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_save_handshake_answers_the_way_a_real_server_does()
    {
        var (process, lines) = Create();
        await using var _ = process;

        await process.StartAsync();
        Assert.True(await WaitFor(() => lines.Any(l => l.Line.Contains("Server started."))));

        // Nothing held yet, so this is the not-ready reply and NOT a confirmation.
        await process.SendAsync("save resume");
        Assert.True(await WaitFor(
            () => lines.Any(l => l.Line.Contains("A previous save has not been completed."))));
        Assert.DoesNotContain(lines, l => l.Line.Contains("Changes to the world are resumed."));

        await process.SendAsync("save hold");
        Assert.True(await WaitFor(() => lines.Any(l => l.Line.Contains("Saving..."))));

        await process.SendAsync("save query");
        Assert.True(await WaitFor(
            () => lines.Any(l => l.Line.Contains("Data saved. Files are now ready to be copied."))));

        // The manifest arrives on its own line, unprefixed, after "Data saved" - which
        // is the detail the spike recorded and a mock would not have guessed.
        Assert.True(await WaitFor(() => lines.Any(l => l.Line.Contains("Bedrock level/level.dat:"))));

        // Now a hold IS outstanding, so the same command confirms.
        await process.SendAsync("save resume");
        Assert.True(await WaitFor(
            () => lines.Any(l => l.Line.Contains("Changes to the world are resumed."))));

        await process.SendAsync("stop");
        await process.WaitForExitAsync();
    }

    // A code page mismatch silently mangles non-ASCII player names, and only
    // once a real player joins — long after anyone would connect it to the pipe.
    [Fact]
    public async Task Decodes_non_ascii_output_as_utf8()
    {
        var (process, lines) = Create();
        await using var _ = process;

        await process.StartAsync();
        Assert.True(await WaitFor(() => lines.Any(l => l.Line.Contains("Server started."))));

        await process.SendAsync("list");
        Assert.True(await WaitFor(() => lines.Any(l => l.Line.Contains("Aleš", StringComparison.Ordinal))));
        Assert.DoesNotContain(lines, l => l.Line.Contains('�'));

        await process.SendAsync("stop");
        await process.WaitForExitAsync();
    }

    // A null from OutputDataReceived means the stream closed; an empty string is
    // a real blank line. BDS emits real blank lines, so conflating them would
    // drop content from the transcript.
    [Fact]
    public async Task Preserves_genuinely_empty_lines()
    {
        var (process, lines) = Create();
        await using var _ = process;

        await process.StartAsync();
        Assert.True(await WaitFor(() => lines.Any(l => l.Line.Contains("Server started."))));

        await process.SendAsync("list");
        Assert.True(await WaitFor(() => lines.Any(l => l.Line.Length == 0)));

        await process.SendAsync("stop");
        await process.WaitForExitAsync();
    }

    [Fact]
    public async Task Reports_a_non_zero_exit_code()
    {
        Configure("exit_after_ms=100", "exit_code=7");

        var (process, _) = Create();
        await using var __ = process;

        await process.StartAsync();

        Assert.Equal(7, await process.WaitForExitAsync());
        Assert.True(process.HasExited);
    }

    // A crash must surface the last lines before it, since that is the output
    // an administrator actually wants. WaitForExitAsync draining the readers is
    // what makes that true.
    [Fact]
    public async Task Output_written_before_an_exit_is_not_lost()
    {
        Configure("exit_after_ms=400");

        var (process, lines) = Create();
        await using var __ = process;

        await process.StartAsync();
        await process.WaitForExitAsync();

        Assert.Contains(lines, l => l.Line.Contains("Starting Server"));
        Assert.Contains(lines, l => l.Line.Contains("LOADING VANILLA WORLD"));
    }

    // File.Exists answers "false" both for a file that is not there and for one
    // that cannot be examined, so "not found" on its own sends an administrator
    // hunting for a missing file when the real problem may be access. These
    // messages have to carry enough to tell the cases apart.
    [Fact]
    public async Task A_missing_executable_says_the_directory_is_empty()
    {
        await using var process = new BedrockProcess(
            "survival",
            Path.Combine(_workingDirectory, "bedrock_server.exe"),
            _workingDirectory,
            NullLogger<BedrockProcess>.Instance);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => process.StartAsync());

        Assert.Contains("bedrock_server.exe", ex.Message);
        Assert.Contains("survival", ex.Message);
        Assert.Contains("empty", ex.Message);
        Assert.Contains(_workingDirectory, ex.Message);
    }

    // "Wrong folder" and "the empty folder BedrockSentry just created" look
    // identical without this, and they are different mistakes.
    [Fact]
    public async Task A_populated_directory_without_the_executable_says_so()
    {
        File.WriteAllText(Path.Combine(_workingDirectory, "server.properties"), "x");
        File.WriteAllText(Path.Combine(_workingDirectory, "allowlist.json"), "[]");

        await using var process = new BedrockProcess(
            "survival",
            Path.Combine(_workingDirectory, "bedrock_server.exe"),
            _workingDirectory,
            NullLogger<BedrockProcess>.Instance);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => process.StartAsync());

        Assert.Contains("2 file(s)", ex.Message);
        Assert.Contains("no .exe", ex.Message);
    }

    [Fact]
    public async Task A_missing_working_directory_is_reported_as_such()
    {
        var missing = Path.Combine(_workingDirectory, "does-not-exist");

        await using var process = new BedrockProcess(
            "survival",
            Path.Combine(missing, "bedrock_server.exe"),
            missing,
            NullLogger<BedrockProcess>.Instance);

        var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => process.StartAsync());

        Assert.Contains("working directory", ex.Message);
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public async Task Sending_to_an_exited_process_throws()
    {
        Configure("exit_after_ms=100");

        var (process, _) = Create();
        await using var __ = process;

        await process.StartAsync();
        await process.WaitForExitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => process.SendAsync("list"));
    }

    [Fact]
    public async Task A_process_object_cannot_be_restarted()
    {
        var (process, _) = Create();
        await using var __ = process;

        await process.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => process.StartAsync());

        await process.SendAsync("stop");
        await process.WaitForExitAsync();
    }

    [Fact]
    public async Task Kill_terminates_a_server_that_ignores_stop()
    {
        Configure("ignore_stop=1");

        var (process, lines) = Create();
        await using var __ = process;

        await process.StartAsync();
        Assert.True(await WaitFor(() => lines.Any(l => l.Line.Contains("Server started."))));

        await process.SendAsync("stop");
        Assert.True(await WaitFor(() => lines.Any(l => l.Line.Contains("Server stop requested"))));
        Assert.False(process.HasExited);

        process.Kill();

        await process.WaitForExitAsync();
        Assert.True(process.HasExited);
    }

    // A handler that throws must not take the reader down with it: losing one
    // line is survivable, losing the reader means the console goes silent and
    // the supervisor never sees "Server started."
    [Fact]
    public async Task A_throwing_output_handler_does_not_stop_the_reader()
    {
        var received = new ConcurrentQueue<string>();

        await using var process = new BedrockProcess(
            "test", FakeServerPath, _workingDirectory, NullLogger<BedrockProcess>.Instance);

        process.OutputReceived += (_, _) => throw new InvalidOperationException("handler blew up");
        process.OutputReceived += (_, line) => received.Enqueue(line);

        await process.StartAsync();

        Assert.True(await WaitFor(() => received.Any(l => l.Contains("Server started."))));

        await process.SendAsync("stop");
        await process.WaitForExitAsync();
    }
}
