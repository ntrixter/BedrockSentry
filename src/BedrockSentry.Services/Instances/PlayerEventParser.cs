using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using BedrockSentry.Core.Instances;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Reads player arrivals and departures out of the BDS console (<c>D25</c>).
/// </summary>
/// <remarks>
/// Measured on 1.26.40 rather than assumed, which is the whole point — the
/// vanilla chat shape in Section 5.4 was guessed at for months and turned out
/// not to be printed at all. The two lines this cares about are:
///
/// <code>
/// Player connected: SteveExample, xuid: 2535000000000001
/// Player disconnected: SteveExample, xuid: 2535000000000001, pfid: 0000000000000000
/// </code>
///
/// The output around them is the reason this matches on the verb rather than on
/// <c>Player </c>. A connect is followed about two seconds later by
/// <c>Player Spawned:</c>, which carries the same XUID in a *different* shape
/// with no comma before <c>xuid:</c>, and <c>Player PartyIdUpdate:</c> carries no
/// XUID at all. Matching the prefix would find all three and mean none of them —
/// and would open a session twice for one arrival.
///
/// <c>pfid</c> is present on the disconnect and absent on the connect, so nothing
/// here may require it.
///
/// The pattern is anchored at the start of the message: the text after BDS's own
/// <c>[timestamp LEVEL]</c> prefix and any <c>NO LOG FILE! - </c> in front of it,
/// both stripped here by <see cref="BdsLogParser.Parse"/> so a raw line and a
/// recorded message read alike. The anchor is what keeps a connect line out of
/// everything else that can print one: a behaviour pack's script output, and a
/// player's own words in a relayed chat message. Unanchored, either opened a
/// session for somebody who never connected. <see cref="ConsoleLineClassifier"/>
/// already matched only at the start, and a test pins the two to one answer.
/// </remarks>
public static partial class PlayerEventParser
{
    [GeneratedRegex(
        @"^\s*Player (?<kind>connected|disconnected):\s*(?<name>.*?),\s*xuid:\s*(?<xuid>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PlayerLine();

    /// <summary>
    /// True when this line is one end of a session, with the pieces the session
    /// table needs.
    /// </summary>
    public static bool TryParse(string? line, [NotNullWhen(true)] out PlayerEvent? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var (_, message, _) = BdsLogParser.Parse(line);
        var match = PlayerLine().Match(message);
        if (!match.Success)
        {
            return false;
        }

        var gamertag = match.Groups["name"].Value.Trim();
        if (gamertag.Length == 0)
        {
            // A session has to belong to someone. Better to ignore the line than
            // to open a row against an empty name.
            return false;
        }

        result = new PlayerEvent(
            match.Groups["kind"].Value == "connected"
                ? PlayerEventKind.Connected
                : PlayerEventKind.Disconnected,
            gamertag,
            match.Groups["xuid"].Value);

        return true;
    }
}
