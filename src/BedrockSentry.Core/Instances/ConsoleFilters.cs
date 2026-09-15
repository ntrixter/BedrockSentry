namespace BedrockSentry.Core.Instances;

/// <summary>
/// Which categories of console line the operator wants to see.
/// </summary>
/// <remarks>
/// The question this answers is "what is going on in the server" — the people
/// on it, rather than the machine underneath them. A busy server's output is
/// overwhelmingly the machine, and the arrivals and departures that actually
/// say what is happening are three lines in a thousand.
///
/// A record rather than four parameters threaded through the component tree,
/// and the predicate lives here rather than in the page so it can be tested:
/// there is no bUnit in this solution, so logic left in a <c>.razor</c> file
/// has no cover at all.
///
/// Filtering is applied at <em>render</em> time by the caller, never when lines
/// are read from the buffer. Reading past hidden lines would advance the cursor
/// over them, and turning a category back on would show only what arrived after
/// that moment — which looks like it works and quietly loses the history you
/// turned it on for (<c>D16</c>, <c>docs/design.md</c> § 5.4).
/// </remarks>
/// <param name="ServerOutput">
/// Everything that is not one of the categories below: startup and shutdown,
/// saves, housekeeping, warnings and errors, the wrapper's own notices, and the
/// commands it sent. Off is the setting that turns the console into a feed of
/// what players are doing.
/// </param>
/// <param name="Joins">Players arriving.</param>
/// <param name="Leaves">Players departing.</param>
/// <param name="Chat">
/// What players said. Nothing is ever classified as chat until the companion
/// pack ships (milestone 12) — BDS prints no player chat of its own
/// (<c>docs/design.md</c> § 5.4, measured).
/// </param>
public sealed record ConsoleFilters(
    bool ServerOutput = true,
    bool Joins = true,
    bool Leaves = true,
    bool Chat = true)
{
    /// <summary>Everything, which is how the console opens.</summary>
    public static ConsoleFilters All { get; } = new();

    /// <summary>Just the people: who arrived, who left, and what they said.</summary>
    public static ConsoleFilters PlayersOnly { get; } = new(ServerOutput: false);

    /// <summary>True when nothing is filtered out.</summary>
    public bool ShowingEverything => ServerOutput && Joins && Leaves && Chat;

    /// <summary>
    /// Whether this line belongs on screen.
    /// </summary>
    /// <remarks>
    /// Run boundaries are never hidden. They are structural rather than
    /// conversational — a restart in the middle of a filtered feed would
    /// otherwise be invisible, and "why did everyone leave and rejoin" has no
    /// answer without it.
    /// </remarks>
    public bool Shows(ConsoleLine line)
    {
        if (line.Origin is ConsoleOrigin.Launch)
        {
            return true;
        }

        if (line.Origin is ConsoleOrigin.Chat)
        {
            return Chat;
        }

        return line.Kind switch
        {
            ConsoleLineKind.PlayerJoined => Joins,
            ConsoleLineKind.PlayerLeft => Leaves,
            _ => ServerOutput,
        };
    }

    /// <summary>How many of <paramref name="lines"/> this would hide.</summary>
    public int CountHidden(IEnumerable<ConsoleLine> lines)
    {
        var hidden = 0;

        foreach (var line in lines)
        {
            if (!Shows(line))
            {
                hidden++;
            }
        }

        return hidden;
    }
}
