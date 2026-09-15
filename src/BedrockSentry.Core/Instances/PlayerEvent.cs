namespace BedrockSentry.Core.Instances;

/// <summary>Which end of a session a console line marks.</summary>
public enum PlayerEventKind
{
    Connected,
    Disconnected,
}

/// <summary>
/// A player arriving or leaving, read off the server's own console (<c>D25</c>).
/// </summary>
/// <param name="Xuid">
/// The identity everything else hangs off. Kept as a string: it is a 64-bit
/// value used as a key and never as a number, and parsing it into one buys
/// nothing while risking a range that does not fit.
/// </param>
public sealed record PlayerEvent(PlayerEventKind Kind, string Gamertag, string Xuid);
