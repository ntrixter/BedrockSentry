using BedrockSentry.Core.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// `T170`. Waiting for a console reply by subscribing rather than by re-reading
/// a cursor.
/// </summary>
public sealed class ConsoleWatchTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static ConsoleLine Append(ConsoleBuffer console, string text) =>
        console.Append(ConsoleOrigin.Stdout, text, DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_matching_line_completes_the_wait()
    {
        var console = new ConsoleBuffer();
        using var watch = ConsoleWatch.For(console, line => line.Message.StartsWith("ready"));

        Append(console, "ready to go");

        var line = await watch.WaitAsync(Patience, TimeProvider.System);

        Assert.NotNull(line);
        Assert.Equal("ready to go", line.Message);
    }

    /// <summary>
    /// The whole point of the type, and the defect it replaced.
    /// </summary>
    /// <remarks>
    /// The buffer is a ring. A cursor-and-poll reader loses any reply that is
    /// evicted between two polls, which is what happened on a real box during the
    /// output burst that follows an update — the world had been resumed and the
    /// wrapper said it might not have been. A subscriber sees the line as it is
    /// recorded, so the capacity stops being part of the question.
    /// </remarks>
    [Fact]
    public async Task A_match_that_is_evicted_before_anybody_reads_is_still_seen()
    {
        var console = new ConsoleBuffer(capacity: 8);
        using var watch = ConsoleWatch.For(console, line => line.Message.StartsWith("resumed"));

        Append(console, "resumed the world");

        // Push it out of the ring entirely before waiting.
        for (var i = 0; i < 40; i++)
        {
            Append(console, $"noise {i}");
        }

        Assert.Equal(8, console.Count);

        var line = await watch.WaitAsync(Patience, TimeProvider.System);

        Assert.NotNull(line);
        Assert.Equal("resumed the world", line.Message);
    }

    [Fact]
    public async Task Nothing_matching_times_out_as_null()
    {
        var console = new ConsoleBuffer();
        using var watch = ConsoleWatch.For(console, line => line.Message.StartsWith("never"));

        Append(console, "something else");

        Assert.Null(await watch.WaitAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System));
    }

    /// <remarks>
    /// Lines already recorded when the watch is created are deliberately not
    /// matched: every caller creates the watch and then sends the command it is
    /// waiting on, so an earlier line is somebody else's reply.
    /// </remarks>
    [Fact]
    public async Task Lines_from_before_the_watch_are_not_matched()
    {
        var console = new ConsoleBuffer();
        Append(console, "resumed the world");

        using var watch = ConsoleWatch.For(console, line => line.Message.StartsWith("resumed"));

        Assert.Null(await watch.WaitAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System));
    }

    /// <remarks>
    /// The predicate runs on the pipe reader's thread. A throw travelling back
    /// into the buffer's fan-out would take out the reader that produced the
    /// line, which is a far worse outcome than one unmatched reply.
    /// </remarks>
    [Fact]
    public async Task A_predicate_that_throws_does_not_break_the_console()
    {
        var console = new ConsoleBuffer();
        using var watch = ConsoleWatch.For(console, _ => throw new InvalidOperationException("bad"));

        var recorded = Append(console, "still recorded");

        Assert.Equal("still recorded", recorded.Message);
        Assert.Equal(1, console.Count);
        Assert.Null(await watch.WaitAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System));
    }

    [Fact]
    public async Task Disposing_stops_the_watch_matching()
    {
        var console = new ConsoleBuffer();
        var watch = ConsoleWatch.For(console, line => line.Message.StartsWith("late"));
        watch.Dispose();

        Append(console, "late arrival");

        Assert.Null(await watch.WaitAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System));
    }

    /// <remarks>
    /// The manifest loop waits in slices against one long-lived watch, so a
    /// second wait after a slice expired has to still be able to succeed.
    /// </remarks>
    [Fact]
    public async Task A_watch_can_be_waited_on_again_after_a_slice_expires()
    {
        var console = new ConsoleBuffer();
        using var watch = ConsoleWatch.For(console, line => line.Message.StartsWith("slow"));

        Assert.Null(await watch.WaitAsync(TimeSpan.FromMilliseconds(20), TimeProvider.System));

        Append(console, "slow answer");

        var line = await watch.WaitAsync(Patience, TimeProvider.System);
        Assert.NotNull(line);
        Assert.Equal("slow answer", line.Message);
    }
}
