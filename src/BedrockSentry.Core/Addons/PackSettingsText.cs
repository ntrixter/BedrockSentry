namespace BedrockSentry.Core.Addons;

/// <summary>Turning a pack's own text into something a web panel can show.</summary>
/// <remarks>
/// Pack authors write labels for the Bedrock client, which renders
/// <c>§</c> formatting codes as colour and style. A browser renders them as
/// literal characters, so a label written for the game reads as
/// <c>§eHard mode</c> on this panel unless they are removed.
/// </remarks>
public static class PackSettingsText
{
    private const char FormattingCode = '§';

    /// <summary>A pack's label with its formatting codes removed.</summary>
    /// <remarks>
    /// <para>A code is <c>§</c> plus <strong>one character, whatever it is</strong>
    /// — not a validated set. An unknown code is still consumed by the client's
    /// parser, so keeping the character after an unrecognised <c>§</c> would put
    /// a stray letter in the middle of a word.</para>
    ///
    /// <para>A trailing <c>§</c> with nothing after it is dropped: it can show
    /// nothing in the client either.</para>
    /// </remarks>
    public static string? Plain(string? text)
    {
        if (text is null || text.IndexOf(FormattingCode, StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var plain = new System.Text.StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == FormattingCode)
            {
                i++;
                continue;
            }

            plain.Append(text[i]);
        }

        return plain.ToString();
    }

    /// <summary>
    /// Whether a label reads as a heading over the controls after it, rather than
    /// as a note about the one before it.
    /// </summary>
    /// <remarks>
    /// Bedrock draws a label as a line of text in the pack's settings screen and
    /// says nothing about which it is. Pack authors use short ones to group
    /// controls ("Explosions", "Advanced") and long ones to explain a control, so a
    /// label is a heading when it is short and is not a sentence: 32 characters
    /// or fewer, with no full stop, colon, question or exclamation mark.
    /// </remarks>
    public static bool IsHeading(string? text)
    {
        if (Plain(text)?.Trim() is not { Length: > 0 and <= 32 } plain)
        {
            return false;
        }

        return plain.IndexOfAny(['.', ':', '?', '!']) < 0;
    }
}
