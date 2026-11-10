namespace BedrockSentry.Core.Instances;

/// <summary>How the console marks a line: its glyph and its classes.</summary>
/// <remarks>
/// Here rather than in the console component so a test can walk every
/// <see cref="ConsoleLineKind"/>: the suite does not reference the Host, and a kind
/// the page did not know would render as ordinary output without a word.
/// </remarks>
public static class ConsoleLineStyle
{
    /// <summary>
    /// The one-character mark for a line, or a space.
    /// </summary>
    /// <remarks>
    /// Severity outranks origin, which outranks subject — a warning is a warning
    /// whoever raised it and whatever it is about. Chat's mark is a quote rather
    /// than an emoji, and a death's a dagger: emoji are double-width, and one on a
    /// line knocks it a column out of true with the rest of the console.
    /// </remarks>
    public static string Glyph(ConsoleLine line) => line switch
    {
        { Level: BdsLogLevel.Error } => "✖",
        { Level: BdsLogLevel.Warn } => "▲",
        { Origin: ConsoleOrigin.Stderr } => "✖",
        { Origin: ConsoleOrigin.Command } => "›",
        { Origin: ConsoleOrigin.Notice } => "❯",
        { Origin: ConsoleOrigin.Chat } => "\"",
        _ => line.Kind switch
        {
            ConsoleLineKind.Lifecycle => "●",
            ConsoleLineKind.PlayerJoined => "→",
            ConsoleLineKind.PlayerLeft => "←",
            ConsoleLineKind.Save or ConsoleLineKind.SaveManifest => "◆",
            ConsoleLineKind.Death => "†",
            ConsoleLineKind.OperatorCommand => "/",
            _ => " ",
        },
    };

    /// <summary>The class for a line's severity, or null.</summary>
    public static string? LevelClass(ConsoleLine line) => line.Level switch
    {
        BdsLogLevel.Warn => "console-warn",
        BdsLogLevel.Error => "console-error",
        _ => null,
    };

    /// <summary>
    /// The class for what a line is about. Null for an ordinary line, so an
    /// unrecognised line carries no class at all and renders exactly as it did
    /// before any of this existed; null for a blank line, which is never rendered.
    /// </summary>
    public static string? KindClass(ConsoleLine line) => line.Kind switch
    {
        ConsoleLineKind.Lifecycle => "console-lifecycle",
        ConsoleLineKind.PlayerJoined => "console-join",
        ConsoleLineKind.PlayerLeft => "console-leave",
        ConsoleLineKind.Save or ConsoleLineKind.SaveManifest => "console-save",
        ConsoleLineKind.Housekeeping => "console-noise",
        ConsoleLineKind.Death => "console-death",
        ConsoleLineKind.OperatorCommand => "console-opcommand",
        _ => null,
    };
}
