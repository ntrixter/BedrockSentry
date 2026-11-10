using BedrockSentry.Core.Addons;

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
/// two fields. <see cref="CompanionRelayReader"/> reads it, as it reads every
/// kind the pack prints, so chat takes the same tags before the marker.
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
    /// A chat relay's start: the pack's marker and the <c>chat</c> kind
    /// (<see cref="ChatRelay"/>).
    /// </summary>
    public const string RelayMarker = CompanionPack.Marker + "chat|";

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

        if (!CompanionRelayReader.TryRead(message, out var relay) || relay is not ChatRelay said)
        {
            return false;
        }

        chat = new ChatMessage(said.Gamertag, said.Text);
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
