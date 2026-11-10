using BedrockSentry.Core.Backups;

namespace BedrockSentry.Core.Instances;

/// <summary>
/// What a line of server output is <em>about</em>, as distinct from where it
/// came from (<see cref="ConsoleOrigin"/>) or how bad it is
/// (<see cref="BdsLogLevel"/>).
/// </summary>
/// <remarks>
/// Origin and level between them already colour the wrapper's own voice, the
/// commands it sent, chat, warnings and errors. Everything else — the large
/// majority of a running server's output — arrived as one undifferentiated
/// colour, which is what made a startup something to read rather than scan.
/// This is the third axis, and it only ever applies to plain server output.
/// </remarks>
public enum ConsoleLineKind
{
    /// <summary>Unrecognised. Full text colour: a classifier that knows nothing must still leave a readable console.</summary>
    Ordinary,

    /// <summary>The server starting, stopping, or naming its version.</summary>
    Lifecycle,

    /// <summary>Somebody arrived.</summary>
    PlayerJoined,

    /// <summary>Somebody left.</summary>
    PlayerLeft,

    /// <summary>The save handshake: <c>Saving...</c>, <c>Data saved</c>, the resume.</summary>
    Save,

    /// <summary>
    /// The file list <c>save query</c> prints after <c>Data saved</c>. Its own kind so the
    /// console can fold it under that line (<see cref="ConsoleSaveFold"/>); coloured and
    /// filtered as <see cref="Save"/>.
    /// </summary>
    SaveManifest,

    /// <summary>Real, routine, and not worth reading — dimmed rather than hidden.</summary>
    Housekeeping,

    /// <summary>A player died, as the companion pack relayed it.</summary>
    Death,

    /// <summary>
    /// An operator ran one of the companion pack's commands in game, as the pack relayed it
    /// (<c>cmd|…</c>). Vanilla commands a player types print nothing on BDS and have no
    /// script event, so they cannot be shown.
    /// </summary>
    OperatorCommand,

    /// <summary>
    /// A line of the server's output with nothing in it. Recorded, and never shown:
    /// a row with a time and nothing after it says nothing (<c>T311</c>). The
    /// wrapper reads one after every script line from Linux BDS 1.26.51.1 and two on
    /// Windows (<c>D93</c>'s spike, <c>T295</c>), and two on Windows after
    /// <c>Content logging to console is enabled.</c> at each start.
    /// </summary>
    Blank,
}

/// <summary>
/// Sorts server output into <see cref="ConsoleLineKind"/> by shape.
/// </summary>
/// <remarks>
/// Every literal here was measured off a real server (<c>docs/design.md</c> § 5.5, and
/// the connect/disconnect shapes <c>D25</c> settled) rather than taken from
/// documentation. That distinction has already cost this project twice: a chat
/// recogniser was written against a line BDS turns out never to print, and
/// <c>D21</c> rested on a premise measurement did not support.
///
/// Recognising nothing is a supported answer. An unmatched line is
/// <see cref="ConsoleLineKind.Ordinary"/> and renders exactly as it did before
/// this existed, so a future BDS that renames everything degrades to the old
/// console rather than to a broken one.
/// </remarks>
public static class ConsoleLineClassifier
{
    /// <summary>
    /// Lines that say the server is working and are never worth reading. The
    /// three <c>Player </c> entries matter most: they follow a connect within
    /// about two seconds and restate an arrival the operator has already seen.
    /// </summary>
    private static readonly string[] Housekeeping =
    [
        "Running AutoCompaction",
        "Player Spawned:",
        "Player PartyIdUpdate:",
        "setting up server logging",
        "opening worlds/",
    ];

    /// <summary>
    /// Boxed notices Bedrock prints as several lines between two rules of
    /// <c>=</c>, which are advice rather than events and want dimming whole.
    /// </summary>
    /// <remarks>
    /// Matched on the banner's subject, not the rule: the rules themselves are
    /// identical between one notice and the next, so opening a block on a bare
    /// row of <c>=</c> would swallow whatever followed the next one.
    /// </remarks>
    private static readonly string[] DimBanners =
    [
        "TELEMETRY MESSAGE",
    ];

    /// <summary>True for a line that is nothing but a rule of <c>=</c>.</summary>
    private static bool IsRule(ReadOnlySpan<char> text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 4)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            if (c != '=')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The boxed notices the console folds under their opening line
    /// (<see cref="ConsoleNoticeFold"/>), by the subject their banner carries, and whether
    /// a box starts folded. The telemetry message is advice and starts folded (<c>ON2</c>);
    /// the allow list warning and the transport error are printed at <c>WARN</c> and
    /// <c>ERROR</c> and start open, so hiding them is the viewer's choice.
    /// </summary>
    /// <remarks>
    /// <c>TRANSPORT TYPE ERROR</c> is matched without the two spaces BDS 1.26.50.1 prints
    /// after it, so a banner that drops them still matches.
    /// </remarks>
    public static readonly IReadOnlyList<(string Banner, bool FoldedAtFirst)> Notices =
    [
        ("TELEMETRY MESSAGE", true),
        ("ALLOW LIST WARNING", false),
        ("TRANSPORT TYPE ERROR", false),
    ];

    /// <summary>
    /// The subject from <see cref="Notices"/> that <paramref name="message"/> opens, or
    /// null; the console folds the box under the line (<see cref="ConsoleNoticeFold"/>).
    /// </summary>
    public static string? NoticeOpenedBy(string? message)
    {
        if (message is null || !message.Contains('='))
        {
            return null;
        }

        foreach (var (banner, _) in Notices)
        {
            if (message.Contains(banner, StringComparison.Ordinal))
            {
                return banner;
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="message"/> opens one of <see cref="Notices"/>.</summary>
    public static bool OpensANotice(string? message) => NoticeOpenedBy(message) is not null;

    /// <summary>True when <paramref name="message"/> is nothing but a rule of <c>=</c>, as a boxed notice closes.</summary>
    public static bool IsNoticeRule(string? message) => message is not null && IsRule(message);

    /// <summary>True when this line opens one of <see cref="DimBanners"/>.</summary>
    private static bool OpensADimBanner(ReadOnlySpan<char> text)
    {
        if (!text.Contains('='))
        {
            return false;
        }

        foreach (var banner in DimBanners)
        {
            if (text.Contains(banner, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] Lifecycle =
    [
        "Starting Server",
        "Version:",
        "Server started",
        "Server stop requested",
        "Stopping server",
        "Quit correctly",
    ];

    private static readonly string[] Save =
    [
        "Saving...",
        "Data saved",
        "Changes to the world are resumed",
    ];

    /// <summary>
    /// What the classifier has to remember between lines.
    /// </summary>
    /// <remarks>
    /// Two of Bedrock's shapes span lines. A warning wraps onto continuation
    /// rows that carry no <c>[timestamp LEVEL]</c> prefix of their own, so a
    /// per-line reading paints half a warning block amber and leaves the rest
    /// looking like ordinary output. And its boxed notices run from one rule of
    /// <c>=</c> to the next. Neither can be read from one line alone.
    /// </remarks>
    public struct State
    {
        /// <summary>The level of the last line that carried one explicitly.</summary>
        public BdsLogLevel CarriedLevel;

        /// <summary>Inside a boxed notice that should be dimmed whole.</summary>
        public bool InDimBanner;
    }

    /// <summary>Nothing but whitespace and control characters, or nothing at all.</summary>
    private static bool IsBlank(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c) && !char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Classifies a line, advancing <paramref name="state"/> past it.
    /// </summary>
    /// <param name="relay">The companion pack's relay the line carries, if any.</param>
    /// <param name="echoPositions">
    /// Whether the pack's join and leave lines count as arrivals and departures
    /// (<see cref="CompanionSettings.EchoPositions"/>); off, they are dimmed.
    /// </param>
    /// <returns>
    /// The kind, and the level the line should be treated as having — which is
    /// the parsed level for a prefixed line, and the carried one for a wrapped
    /// continuation.
    /// </returns>
    public static (ConsoleLineKind Kind, BdsLogLevel Level) Classify(
        ConsoleOrigin origin,
        BdsLogLevel level,
        string? message,
        CompanionRelay? relay,
        bool echoPositions,
        ref State state)
    {
        if (origin is not ConsoleOrigin.Stdout)
        {
            // Chat, the wrapper's own voice and the commands it sent are neither
            // continuations of the server's warnings nor inside its boxes.
            return (ConsoleLineKind.Ordinary, level);
        }

        var text = (message ?? string.Empty).AsSpan();

        if (level is not BdsLogLevel.Unknown)
        {
            // A line that states its own level ends whatever was being carried.
            state.CarriedLevel = level;
        }

        // Wherever it falls (T311): after a script line, after the content log's
        // notice, inside a boxed notice, under a warning. It closes no box and
        // carries no level worth painting, and a timestamp prefix, whitespace or a
        // control character does not make it something to read.
        if (IsBlank(text))
        {
            return (ConsoleLineKind.Blank, level);
        }

        if (level is BdsLogLevel.Unknown
            && state.CarriedLevel is BdsLogLevel.Warn or BdsLogLevel.Error
            && Classify(message) is ConsoleLineKind.Ordinary)
        {
            // A bare line under a warning, and not something the classifier
            // recognises in its own right — the save manifest arrives bare too
            // and must keep its own colour rather than inheriting an alarm.
            level = state.CarriedLevel;
        }

        // Before the banners: a relay says what it is, wherever it lands.
        if (relay is not null)
        {
            return (KindOf(relay, echoPositions), level);
        }

        if (state.InDimBanner)
        {
            // The closing rule belongs to the box it closes.
            if (IsRule(text))
            {
                state.InDimBanner = false;
            }

            return (ConsoleLineKind.Housekeeping, level);
        }

        if (OpensADimBanner(text))
        {
            state.InDimBanner = true;
            return (ConsoleLineKind.Housekeeping, level);
        }

        return (Classify(message), level);
    }

    /// <summary>
    /// What a relay line is about. The lines the pack prints for the wrapper rather
    /// than for a person (hello, the probe, the stop exchange, a newer pack's kinds)
    /// are dimmed, and so are its join and leave lines when they are not wanted as
    /// arrivals and departures.
    /// </summary>
    private static ConsoleLineKind KindOf(CompanionRelay relay, bool echoPositions) => relay switch
    {
        DeathRelay => ConsoleLineKind.Death,
        CommandRelay => ConsoleLineKind.OperatorCommand,
        JoinRelay => echoPositions ? ConsoleLineKind.PlayerJoined : ConsoleLineKind.Housekeeping,
        LeaveRelay => echoPositions ? ConsoleLineKind.PlayerLeft : ConsoleLineKind.Housekeeping,
        _ => ConsoleLineKind.Housekeeping,
    };

    /// <summary>
    /// Classifies a console line, or returns <see cref="ConsoleLineKind.Ordinary"/>
    /// for anything already coloured by another axis.
    /// </summary>
    public static ConsoleLineKind Classify(ConsoleLine line)
    {
        // The wrapper's own notices, the commands it sent, chat and the run
        // boundary each have their own treatment already; a content class on top
        // would be two colours arguing over one line. Warnings and errors keep
        // their severity, which outranks their subject.
        if (line.Origin is not ConsoleOrigin.Stdout)
        {
            return ConsoleLineKind.Ordinary;
        }

        return line.Level is BdsLogLevel.Warn or BdsLogLevel.Error
            ? ConsoleLineKind.Ordinary
            : Classify(line.Message);
    }

    /// <summary>The shape test itself, exposed for tests and reuse.</summary>
    public static ConsoleLineKind Classify(string? message)
    {
        if (message is null || IsBlank(message))
        {
            return ConsoleLineKind.Blank;
        }

        var text = message.AsSpan().TrimStart();

        // Before the generic Player checks: "Player Spawned:" and
        // "Player PartyIdUpdate:" also begin with "Player ", and both are noise
        // around an arrival rather than one.
        if (StartsWithAny(text, Housekeeping))
        {
            return ConsoleLineKind.Housekeeping;
        }

        if (text.StartsWith("Player connected:", StringComparison.Ordinal))
        {
            return ConsoleLineKind.PlayerJoined;
        }

        if (text.StartsWith("Player disconnected:", StringComparison.Ordinal))
        {
            return ConsoleLineKind.PlayerLeft;
        }

        if (StartsWithAny(text, Lifecycle))
        {
            return ConsoleLineKind.Lifecycle;
        }

        if (StartsWithAny(text, Save))
        {
            return ConsoleLineKind.Save;
        }

        return LooksLikeAManifest(message) ? ConsoleLineKind.SaveManifest : ConsoleLineKind.Ordinary;
    }

    /// <summary>
    /// The <c>save query</c> manifest, recognised by the same parser the backup
    /// uses.
    /// </summary>
    /// <remarks>
    /// Deliberately shared rather than reimplemented: two recognisers would
    /// eventually disagree, and a console calling something a manifest that the
    /// backup does not — or the reverse — would be a genuinely confusing lie
    /// during an incident.
    ///
    /// The guard in front of it is for cost, not correctness. Classification
    /// runs per visible line per render, and the parser splits and allocates;
    /// almost every real line fails "ends in a digit and contains a path
    /// separator" without touching it.
    /// </remarks>
    private static bool LooksLikeAManifest(string message) =>
        char.IsAsciiDigit(message[^1])
        && (message.Contains('/') || message.Contains('\\'))
        && SaveManifest.TryParse(message, out _);

    private static bool StartsWithAny(ReadOnlySpan<char> text, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
