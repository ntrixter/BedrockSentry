using System.Globalization;
using System.Text.RegularExpressions;

namespace BedrockSentry.Core.Commands;

/// <summary>A command sent after another one so the end of its reply can be recognised.</summary>
/// <remarks>
/// <para>The server does not say when it has finished answering, and some commands answer
/// nothing at all: <c>alwaysday true</c> prints no line. Waiting a fixed time either
/// wastes it or cuts a slow reply short. So each command is followed by this one, and its
/// reply is every line before the marker's.</para>
///
/// <para><strong>Each marker names itself.</strong> <c>scoreboard players list
/// bsmark7</c> answers <c>Player bsmark7 has no scores recorded</c> and does nothing else
/// (BDS 1.26.52.3). A fixed marker such as <c>list</c> cannot tell its own reply from a
/// command's: <c>execute in overworld run list</c> prints the same line, and every reply
/// after it would be read one command late.</para>
/// </remarks>
public static partial class ConsoleMarker
{
    private const string Prefix = "bsmark";

    [GeneratedRegex(@"\bbsmark(?<n>\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern { get; }

    /// <summary>The marker command with sequence number <paramref name="n"/>.</summary>
    public static string Command(int n) =>
        string.Create(CultureInfo.InvariantCulture, $"scoreboard players list {Prefix}{n}");

    /// <summary>The sequence number a line names, or null when it is no marker's reply.</summary>
    public static int? NumberIn(string message)
    {
        var match = TokenPattern.Match(message ?? string.Empty);
        return match.Success
            && int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }
}
