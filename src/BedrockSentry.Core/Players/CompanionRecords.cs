namespace BedrockSentry.Core.Players;

/// <summary>A whole-block position and the dimension it is in, as the companion pack relays it.</summary>
/// <param name="Dimension">
/// The id the Script API returns — <c>minecraft:overworld</c>, <c>minecraft:nether</c>,
/// <c>minecraft:the_end</c> — stored as given and named for a person only where it
/// is rendered, so a future dimension or a locale cannot break a match.
/// </param>
/// <remarks>
/// The dimension travels with the coordinates because a bare x, y, z is ambiguous
/// across the Nether's eighth-scale grid: the same three numbers name different
/// places.
/// </remarks>
public readonly record struct BlockPosition(int X, int Y, int Z, string Dimension);

/// <summary>A player's death, as the companion pack relays it.</summary>
/// <param name="Gamertag">
/// <c>Player.name</c>, exactly as the Script API returns it. The pack cannot read
/// an XUID, so this is resolved against a session (<see cref="CompanionAttribution"/>).
/// </param>
/// <param name="Cause">The damage cause enum value, such as <c>entityAttack</c>.</param>
/// <param name="KillerType">The killing entity's <c>typeId</c>, when there was one.</param>
/// <param name="KillerName">
/// The killing player's name, when a player did it. Stored for display and never
/// resolved to an XUID.
/// </param>
/// <param name="Message">The sentence the pack built, shown to the player and on the console.</param>
public sealed record CompanionDeath(
    string Gamertag,
    string Cause,
    string? KillerType,
    string? KillerName,
    BlockPosition Position,
    string Message);

/// <summary>Which relay a position at the end of a session came from.</summary>
public enum ExitPositionSource
{
    /// <summary>The pack's <c>leave</c> line, printed as the player left.</summary>
    Leave,

    /// <summary>
    /// The pack's answer to the wrapper's stop exchange (<c>D35</c>), printed while
    /// the player is still online, just before the server is stopped.
    /// </summary>
    Where,
}

/// <summary>What a relay line naming a player was attributed to.</summary>
/// <remarks>
/// Three states, because the world has three: exactly one session matches, none
/// does, or more than one does and they cannot be told apart. The last two write
/// nothing, because a position or a death put on the wrong session is believed.
/// </remarks>
public enum RelayAttribution
{
    /// <summary>Exactly one match, and the write was made.</summary>
    Recorded,

    /// <summary>No session of this run on this instance carries that gamertag.</summary>
    NoSession,

    /// <summary>More than one did, and nothing says which.</summary>
    Ambiguous,
}

/// <summary>
/// The rules that turn a relayed gamertag into a session, stated once.
/// </summary>
/// <remarks>
/// <para>A candidate is a session of this instance, opened during this run, whose
/// own connect line printed exactly the relayed name. The session's own gamertag is
/// used rather than the player's current one, which is only each XUID's latest
/// name.</para>
///
/// <para>A <c>leave</c> and a death can arrive either side of BDS's own disconnect
/// line, so a session closed within <see cref="RecentlyClosed"/> still counts. A
/// <c>where</c> is answered while everybody is online, so only an open session
/// counts for it.</para>
/// </remarks>
public static class CompanionAttribution
{
    /// <summary>How long after its disconnect a session still takes a leave or a death.</summary>
    public static readonly TimeSpan RecentlyClosed = TimeSpan.FromSeconds(10);
}
