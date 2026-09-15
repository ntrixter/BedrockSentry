using BedrockSentry.Core.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// Recognising player chat.
///
/// The companion pack's relay format is definitive — BedrockSentry defines both
/// ends of it. The bare <c>&lt;name&gt; text</c> form is matched defensively and
/// has not been confirmed against a real BDS console, so the tests that matter
/// most here are the ones proving ordinary server output does not trip it.
/// </summary>
public class ChatParserTests
{
    private static ChatMessage Parse(string message)
    {
        Assert.True(ChatParser.TryParse(message, out var chat), $"'{message}' should be chat");
        return chat;
    }

    private static void NotChat(string message) =>
        Assert.False(ChatParser.TryParse(message, out _), $"'{message}' should not be chat");

    [Fact]
    public void Reads_the_companion_relay()
    {
        var chat = Parse($"{ChatParser.RelayMarker}Steve|hello everyone");

        Assert.Equal("Steve", chat.Player);
        Assert.Equal("hello everyone", chat.Text);
    }

    // The delimiters are pipes precisely so a message containing one still
    // parses into two fields rather than three.
    [Fact]
    public void A_message_containing_the_delimiter_survives()
    {
        var chat = Parse($"{ChatParser.RelayMarker}Steve|left | right | middle");

        Assert.Equal("Steve", chat.Player);
        Assert.Equal("left | right | middle", chat.Text);
    }

    // Player names are UTF-8 and frequently not ASCII — the spike checked the
    // decoding for exactly this reason.
    [Fact]
    public void A_non_ascii_name_is_read_whole()
    {
        var chat = Parse($"{ChatParser.RelayMarker}Ná†e_日本|こんにちは");

        Assert.Equal("Ná†e_日本", chat.Player);
        Assert.Equal("こんにちは", chat.Text);
    }

    [Fact]
    public void Reads_the_classic_angle_bracket_form()
    {
        var chat = Parse("<Steve> hello everyone");

        Assert.Equal("Steve", chat.Player);
        Assert.Equal("hello everyone", chat.Text);
    }

    // A false positive here is worse than a miss: it would take a line of real
    // server output, restyle it as conversation, and then hide it the moment
    // somebody turned chat off — losing operational output to a chat filter.
    [Theory]
    [InlineData("Server started.")]
    [InlineData("Version: 1.26.36.1")]
    [InlineData("<--- something --->")]
    [InlineData("< spaced > not a name")]
    [InlineData("<>")]
    [InlineData("<Steve>")]
    [InlineData("<Steve>no space after")]
    [InlineData("<xml attr=\"1\"/> in a pack error")]
    [InlineData("")]
    public void Ordinary_output_is_not_chat(string message) => NotChat(message);

    [Fact]
    public void A_relay_line_with_no_delimiter_is_not_chat() =>
        NotChat($"{ChatParser.RelayMarker}malformed");
}

/// <summary>
/// Chat is classified where it is recorded, so every reader agrees about what
/// it is — and so hiding it can be a view decision rather than a capture one.
/// </summary>
public class ConsoleBufferChatTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Chat_from_the_server_is_recorded_as_chat()
    {
        var buffer = new ConsoleBuffer();

        var line = buffer.Append(
            ConsoleOrigin.Stdout, "[2026-08-04 12:00:00:001 INFO] <Steve> hello", When);

        Assert.Equal(ConsoleOrigin.Chat, line.Origin);
        Assert.Equal("Steve: hello", line.Message);

        // The raw line is kept exactly as it arrived, so the transcript and any
        // later re-reading still have what the server actually printed.
        Assert.Equal("[2026-08-04 12:00:00:001 INFO] <Steve> hello", line.Raw);
    }

    [Fact]
    public void Ordinary_output_keeps_its_origin()
    {
        var buffer = new ConsoleBuffer();

        var line = buffer.Append(
            ConsoleOrigin.Stdout, "[2026-08-04 12:00:00:001 INFO] Server started.", When);

        Assert.Equal(ConsoleOrigin.Stdout, line.Origin);
    }

    // A command the wrapper sent is not chat, whatever it happens to contain.
    // Without the origin guard, sending `say <Steve> hi` would classify our own
    // command as a player talking.
    [Fact]
    public void A_command_the_wrapper_sent_is_never_reclassified()
    {
        var buffer = new ConsoleBuffer();

        var line = buffer.Append(ConsoleOrigin.Command, "say <Steve> hi", When);

        Assert.Equal(ConsoleOrigin.Command, line.Origin);
    }

    /// <summary>
    /// The promise the toggle makes: hiding chat costs nothing, because the
    /// buffer never stops recording it.
    /// </summary>
    /// <remarks>
    /// The console filters at render time for the same reason. Filtering as
    /// lines are read from the buffer would advance the cursor past them, and
    /// turning the toggle back on would show only chat from that moment on —
    /// which looks like it works and quietly loses the conversation somebody
    /// turned it back on to read.
    /// </remarks>
    [Fact]
    public void Chat_is_recorded_whether_or_not_anyone_is_watching_it()
    {
        var buffer = new ConsoleBuffer();

        buffer.Append(ConsoleOrigin.Stdout, "<Steve> first", When);
        buffer.Append(ConsoleOrigin.Stdout, "[2026-08-04 12:00:01:000 INFO] Server started.", When);
        buffer.Append(ConsoleOrigin.Stdout, "<Steve> second", When);

        var everything = buffer.Read(0).Lines;

        Assert.Equal(3, everything.Count);
        Assert.Equal(2, everything.Count(l => l.Origin == ConsoleOrigin.Chat));

        // A reader hiding chat shows one line of the three and still holds the
        // other two, which is what makes turning it back on free.
        var shown = everything.Where(l => l.Origin != ConsoleOrigin.Chat).ToList();

        Assert.Single(shown);
        Assert.Equal(3, everything.Count);
    }
}
