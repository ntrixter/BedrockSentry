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
/// <param name="Leaves">Players departing; the console's Disconnects box.</param>
/// <param name="Chat">
/// What players said, once chat is relayed (<see cref="ChatRelayed"/>). Until
/// then the console has no Chat box and this governs nothing.
/// </param>
/// <param name="Deaths">Players dying, as the companion pack relays it.</param>
/// <param name="OperatorCommands">
/// The companion pack's commands an operator ran in game, as the pack relays them.
/// </param>
public sealed record ConsoleFilters(
    bool ServerOutput = true,
    bool Joins = true,
    bool Leaves = true,
    bool Chat = true,
    bool Deaths = true,
    bool OperatorCommands = true)
{
    /// <summary>Everything, which is how the console opens.</summary>
    public static ConsoleFilters All { get; } = new();

    /// <summary>Just the people: who arrived, who left, who died, and what they said.</summary>
    public static ConsoleFilters PlayersOnly { get; } = new(ServerOutput: false);

    /// <summary>
    /// Whether any line can be classified as chat (<c>D95</c>). False: BDS prints no
    /// player chat of its own (<c>docs/design.md</c> § 5.4, measured), and relaying
    /// it needs Bedrock's pre-release <c>chatSend</c> event, which the companion pack
    /// does not use. Its default is the one line to change once the pack relays chat.
    /// </summary>
    /// <remarks>
    /// While false the console has no Chat box and <see cref="Chat"/> governs nothing:
    /// a chat line that arrives anyway, from some pack other than the companion, is
    /// shown rather than hidden behind a box nobody can see.
    /// </remarks>
    public bool ChatRelayed { get; init; } = false;

    /// <summary>True when nothing is filtered out.</summary>
    public bool ShowingEverything =>
        ServerOutput && Joins && Leaves && Deaths && OperatorCommands && (Chat || !ChatRelayed);

    /// <summary>
    /// Whether this line belongs on screen.
    /// </summary>
    /// <remarks>
    /// Run boundaries are never hidden. They are structural rather than
    /// conversational — a restart in the middle of a filtered feed would
    /// otherwise be invisible, and "why did everyone leave and rejoin" has no
    /// answer without it. A blank line is never shown: a row with a time and
    /// nothing after it says nothing (<c>T311</c>).
    /// </remarks>
    public bool Shows(ConsoleLine line)
    {
        if (line.Origin is ConsoleOrigin.Launch)
        {
            return true;
        }

        if (line.Origin is ConsoleOrigin.Chat)
        {
            return Chat || !ChatRelayed;
        }

        return line.Kind switch
        {
            ConsoleLineKind.PlayerJoined => Joins,
            ConsoleLineKind.PlayerLeft => Leaves,
            ConsoleLineKind.Death => Deaths,
            ConsoleLineKind.OperatorCommand => OperatorCommands,
            ConsoleLineKind.Blank => false,
            _ => ServerOutput,
        };
    }

    /// <summary>How many of <paramref name="lines"/> this would hide.</summary>
    /// <remarks>
    /// A blank line is never shown, whatever is ticked, so it is not counted:
    /// the count is what ticking a box would bring back.
    /// </remarks>
    public int CountHidden(IEnumerable<ConsoleLine> lines)
    {
        var hidden = 0;

        foreach (var line in lines)
        {
            if (line.Kind is not ConsoleLineKind.Blank && !Shows(line))
            {
                hidden++;
            }
        }

        return hidden;
    }

    /// <summary>What the console says about the lines these boxes hide, or null when they hide none.</summary>
    /// <remarks>
    /// Asked at every render rather than kept (<c>T308</c>): a count kept beside the
    /// lines was updated only when a new line arrived, so unticking a box showed no
    /// count and ticking it again left the old one.
    /// </remarks>
    public string? HiddenSentence(IEnumerable<ConsoleLine> lines) => CountHidden(lines) switch
    {
        0 => null,
        1 => "1 line hidden.",
        var hidden => $"{hidden} lines hidden.",
    };
}
