using System.Globalization;

namespace BedrockSentry.Core.Players;

/// <summary>
/// How a server's players-online count is drawn: the bar, its fill, the number beside
/// it and the operators line. The instance page's control row and the Status tab's
/// Players card both draw it, so both read these rules rather than carrying a copy.
/// </summary>
/// <remarks>
/// The fill clamps at 100% and the number does not. Bedrock's allow list has a "join
/// if full" flag whose purpose is to let somebody past <c>max-players</c>, so over the
/// maximum is a real state rather than an error: the bar says full, the label says
/// 12 / 10, and neither lies.
/// </remarks>
public static class OnlineMeter
{
    /// <summary>What the Operators line says when the files that decide it could not be read.</summary>
    public const string Unknown = "unknown";

    /// <summary>A bar needs a maximum to be a fraction of; without one only the count is shown.</summary>
    public static bool HasBar(int? maxPlayers) => maxPlayers is > 0;

    /// <summary>At or over the maximum, which the bar shows in its own colour.</summary>
    public static bool IsFull(int online, int? maxPlayers) => maxPlayers is > 0 && online >= maxPlayers;

    /// <summary>The bar's fill as a CSS width, clamped at 100%; <c>0%</c> when there is no maximum.</summary>
    public static string FillWidth(int online, int? maxPlayers) =>
        maxPlayers is > 0 and var max
            ? Math.Min(100, online * 100.0 / max).ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : "0%";

    /// <summary><c>3 / 10</c> against a known maximum; the bare count otherwise.</summary>
    public static string Label(int online, int? maxPlayers) =>
        maxPlayers is > 0 and var max ? $"{online} / {max}" : online.ToString(CultureInfo.InvariantCulture);

    /// <summary>What a screen reader is told about the bar.</summary>
    public static string AriaLabel(int online, int? maxPlayers) =>
        maxPlayers is > 0 and var max ? $"{online} of {max} players online" : $"{online} players online";

    /// <summary>
    /// The Status tab's Operators line: <c>Offline</c> when no operator is online,
    /// <c>2 Online</c> otherwise, with their names listed under it by the page.
    /// </summary>
    /// <param name="operatorsOnline">How many operators are online; null when that could not be read.</param>
    public static string OperatorsLabel(int? operatorsOnline) => operatorsOnline switch
    {
        null => Unknown,
        0 => "Offline",
        var count => $"{count} Online",
    };
}
