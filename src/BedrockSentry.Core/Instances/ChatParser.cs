namespace BedrockSentry.Core.Instances;

/// <summary>Something a player said, and who said it.</summary>
public sealed record ChatMessage(string Player, string Text);

/// <summary>
/// Recognises player chat in a line of server output.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes, for two sources, and they are not equally certain.
/// </para>
/// <para>
/// The <b>companion pack relay</b> is definitive, because BedrockSentry defines
/// it. Section 9's pack reaches the wrapper by calling <c>console.log()</c>, so
/// the format is ours to choose, and it is chosen to be unambiguous rather than
/// pretty: a fixed marker and pipe delimiters, so a player called
/// <c>&lt;Steve&gt;</c> saying <c>hello | goodbye</c> still parses into exactly
/// two fields.
/// </para>
/// <para>
/// The <b>bare</b> <c>&lt;name&gt; text</c> shape is the classic Minecraft form
/// and is matched defensively, tightly enough that ordinary server output cannot
/// trip it. Whether current BDS prints chat to the console at all has not been
/// measured on a real server — the wrapper's own spike never tested it, and the
/// companion pack exists in part because it may not. Until a real transcript
/// settles that, this recogniser is written to be right when the line does
/// appear and silent when it does not, rather than to a format nobody has seen.
/// </para>
/// </remarks>
public static class ChatParser
{
    /// <summary>
    /// What the companion pack prints. Changing this changes the pack too — they
    /// are two ends of one wire.
    /// </summary>
    public const string RelayMarker = "[BedrockSentry] chat|";

    /// <summary>
    /// Reads <paramref name="message"/> — the text after any
    /// <c>[timestamp LEVEL]</c> prefix — as chat, or returns false.
    /// </summary>
    public static bool TryParse(string? message, out ChatMessage chat)
    {
        chat = default!;

        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return TryRelay(message, out chat) || TryAngleBrackets(message, out chat);
    }

    private static bool TryRelay(string message, out ChatMessage chat)
    {
        chat = default!;

        if (!message.StartsWith(RelayMarker, StringComparison.Ordinal))
        {
            return false;
        }

        var body = message[RelayMarker.Length..];

        // Split once. Everything after the first delimiter is the message,
        // including any further pipes the player typed.
        var separator = body.IndexOf('|');
        if (separator <= 0)
        {
            return false;
        }

        chat = new ChatMessage(body[..separator], body[(separator + 1)..]);
        return true;
    }

    private static bool TryAngleBrackets(string message, out ChatMessage chat)
    {
        chat = default!;

        if (message.Length < 4 || message[0] != '<')
        {
            return false;
        }

        var close = message.IndexOf('>');

        // The name must be non-empty, must not contain a space, and must be
        // followed by a space. Without all three, a line like
        // "<--- something --->" or an XML fragment in a pack error would read as
        // somebody called "---" talking.
        if (close < 2
            || close + 1 >= message.Length
            || message[close + 1] != ' '
            || message.AsSpan(1, close - 1).ContainsAny(' ', '<'))
        {
            return false;
        }

        var text = message[(close + 2)..];
        if (text.Length == 0)
        {
            return false;
        }

        chat = new ChatMessage(message[1..close], text);
        return true;
    }
}
