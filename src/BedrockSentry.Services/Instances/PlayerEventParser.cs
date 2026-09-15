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
/// The pattern is deliberately unanchored: BDS prefixes its own timestamp and log
/// level, and the wrapper's console strips or keeps that depending on where the
/// line came from. Anchoring at the start would make correctness depend on which.
/// </remarks>
public static partial class PlayerEventParser
{
    [GeneratedRegex(
        @"Player (?<kind>connected|disconnected):\s*(?<name>.*?),\s*xuid:\s*(?<xuid>\d+)",
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

        var match = PlayerLine().Match(line);
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
