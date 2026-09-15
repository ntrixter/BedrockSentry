using BedrockSentry.Core.Instances;

namespace BedrockSentry.Tests;

public class ConsoleBufferTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 3, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sequences_start_at_one_and_increase()
    {
        var buffer = new ConsoleBuffer();

        Assert.Equal(1, buffer.Append(ConsoleOrigin.Stdout, "first", When).Sequence);
        Assert.Equal(2, buffer.Append(ConsoleOrigin.Stdout, "second", When).Sequence);
        Assert.Equal(2, buffer.LatestSequence);
    }

    [Fact]
    public void Appending_parses_the_line()
    {
        var buffer = new ConsoleBuffer();

        var line = buffer.Append(
            ConsoleOrigin.Stdout, "[2026-08-03 07:00:16:314 INFO] Server started.", When);

        Assert.Equal(BdsLogLevel.Info, line.Level);
        Assert.Equal("Server started.", line.Message);
        Assert.Equal("[2026-08-03 07:00:16:314 INFO] Server started.", line.Raw);
    }

    [Fact]
    public void Reads_only_what_is_new()
    {
        var buffer = new ConsoleBuffer();
        buffer.Append(ConsoleOrigin.Stdout, "one", When);
        buffer.Append(ConsoleOrigin.Stdout, "two", When);

        var first = buffer.Read(0);
        Assert.Equal(["one", "two"], first.Lines.Select(l => l.Raw));

        buffer.Append(ConsoleOrigin.Stdout, "three", When);

        var second = buffer.Read(first.Cursor);
        Assert.Equal(["three"], second.Lines.Select(l => l.Raw));
    }

    [Fact]
    public void An_empty_read_returns_a_usable_cursor()
    {
        var buffer = new ConsoleBuffer();

        var empty = buffer.Read(0);
        Assert.Empty(empty.Lines);
        Assert.Equal(0, empty.Cursor);
        Assert.False(empty.Gap);

        buffer.Append(ConsoleOrigin.Stdout, "after", When);
        Assert.Equal(["after"], buffer.Read(empty.Cursor).Lines.Select(l => l.Raw));
    }

    [Fact]
    public void Reading_again_with_the_same_cursor_yields_nothing_new()
    {
        var buffer = new ConsoleBuffer();
        buffer.Append(ConsoleOrigin.Stdout, "one", When);

        var read = buffer.Read(0);
        Assert.Single(read.Lines);
        Assert.Empty(buffer.Read(read.Cursor).Lines);
    }

    [Fact]
    public void Oldest_lines_are_evicted_at_capacity()
    {
        var buffer = new ConsoleBuffer(capacity: 3);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Append(ConsoleOrigin.Stdout, $"line {i}", When);
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal(["line 3", "line 4", "line 5"], buffer.Read(0).Lines.Select(l => l.Raw));
    }

    // The point of tracking a gap: a page that was disconnected while the server
    // was noisy must be told it missed output rather than shown a seamless lie.
    [Fact]
    public void A_cursor_older_than_the_buffer_reports_a_gap()
    {
        var buffer = new ConsoleBuffer(capacity: 3);
        for (var i = 1; i <= 6; i++)
        {
            buffer.Append(ConsoleOrigin.Stdout, $"line {i}", When);
        }

        Assert.True(buffer.Read(1).Gap);
    }

    [Fact]
    public void A_first_read_is_never_a_gap()
    {
        var buffer = new ConsoleBuffer(capacity: 2);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Append(ConsoleOrigin.Stdout, $"line {i}", When);
        }

        Assert.False(buffer.Read(0).Gap);
    }

    [Fact]
    public void A_cursor_exactly_at_the_boundary_is_not_a_gap()
    {
        var buffer = new ConsoleBuffer(capacity: 3);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Append(ConsoleOrigin.Stdout, $"line {i}", When);
        }

        // Retained: 3, 4, 5. A caller holding cursor 2 has seen everything
        // that was evicted, so nothing was missed.
        var read = buffer.Read(2);
        Assert.False(read.Gap);
        Assert.Equal(["line 3", "line 4", "line 5"], read.Lines.Select(l => l.Raw));
    }

    // A capped read that advanced the cursor past what it returned would drop
    // the remainder on the floor — invisible, and only under load.
    [Fact]
    public void A_capped_read_resumes_where_it_stopped()
    {
        var buffer = new ConsoleBuffer();
        for (var i = 1; i <= 5; i++)
        {
            buffer.Append(ConsoleOrigin.Stdout, $"line {i}", When);
        }

        var first = buffer.Read(0, maxLines: 2);
        Assert.Equal(["line 1", "line 2"], first.Lines.Select(l => l.Raw));

        var second = buffer.Read(first.Cursor, maxLines: 2);
        Assert.Equal(["line 3", "line 4"], second.Lines.Select(l => l.Raw));
    }

    [Fact]
    public void Clearing_keeps_sequences_monotonic()
    {
        var buffer = new ConsoleBuffer();
        buffer.Append(ConsoleOrigin.Stdout, "before", When);
        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(2, buffer.Append(ConsoleOrigin.Notice, "after", When).Sequence);
    }

    [Fact]
    public void Commands_and_notices_share_the_stream_with_output()
    {
        var buffer = new ConsoleBuffer();
        buffer.Append(ConsoleOrigin.Notice, "starting instance", When);
        buffer.Append(ConsoleOrigin.Command, "save hold", When);
        buffer.Append(ConsoleOrigin.Stdout, "[2026-08-03 07:00:16:654 INFO] Saving...", When);

        var origins = buffer.Read(0).Lines.Select(l => l.Origin);
        Assert.Equal(
            [ConsoleOrigin.Notice, ConsoleOrigin.Command, ConsoleOrigin.Stdout],
            origins);
    }

    [Fact]
    public async Task Concurrent_appends_do_not_lose_or_duplicate_sequences()
    {
        var buffer = new ConsoleBuffer(capacity: 10_000);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++)
            {
                buffer.Append(ConsoleOrigin.Stdout, "x", When);
            }
        })));

        var sequences = buffer.Read(0).Lines.Select(l => l.Sequence).ToList();
        Assert.Equal(2000, sequences.Count);
        Assert.Equal(2000, sequences.Distinct().Count());
        Assert.Equal(sequences.OrderBy(s => s), sequences);
    }

    [Fact]
    public void Capacity_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConsoleBuffer(0));
    }
}
