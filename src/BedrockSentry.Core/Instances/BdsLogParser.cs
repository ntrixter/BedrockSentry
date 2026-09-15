using System.Globalization;

namespace BedrockSentry.Core.Instances;

/// <summary>
/// Splits a raw BDS output line into its timestamp, level and message.
///
/// Written against real 1.26.36.1 output captured by <c>scripts/stdin-spike.ps1</c>
/// (see <c>docs/design.md</c> § 5.5), which settled the one thing worth knowing here:
/// <b>most lines carry a <c>[timestamp LEVEL]</c> prefix, and the ones that
/// matter most do not.</b> The save manifest, the startup banner, wrapped
/// continuation lines inside a warning block, the names line after a
/// <c>list</c> header, and <c>Quit correctly</c> all arrive bare. A parser that
/// requires the prefix throws away precisely the payload.
///
/// So an unparseable line is not an error and is never dropped — it comes back
/// with <see cref="BdsLogLevel.Unknown"/> and its text intact.
/// </summary>
public static class BdsLogParser
{
    /// <summary>
    /// BDS writes local time with millisecond precision and no offset, e.g.
    /// <c>2026-08-03 07:00:14:765</c>. Note the colon before the milliseconds
    /// where the rest of the world uses a period — a standard round-trip parse
    /// fails on it, which is why the format is spelled out.
    /// </summary>
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss:fff";

    /// <summary>
    /// What BDS puts in front of <em>every</em> line when it has not opened a log
    /// file of its own.
    /// </summary>
    /// <remarks>
    /// It is normal — the vanilla dedicated server writes no log file unless it
    /// is told to, and says so on each line rather than once. But it sits ahead
    /// of the <c>[timestamp LEVEL]</c> prefix, so a parser that does not know
    /// about it reads every line as unstructured text. That is not a cosmetic
    /// loss: readiness is detected by matching "Server started" against the
    /// parsed message, so an instance whose server is up and taking players sits
    /// on Starting forever, and the watchdog's stability window never closes.
    /// </remarks>
    public const string NoLogFilePrefix = "NO LOG FILE! - ";

    /// <summary>
    /// How far past the start of a line the opening <c>[</c> may sit and still be
    /// treated as a prefix that something got in front of.
    /// </summary>
    /// <remarks>
    /// <c>T160</c>: one NUL byte in 4,811 lines of a real transcript landed in
    /// front of a <c>save resume</c> confirmation. The parser bailed on its first
    /// check, so the whole <c>[timestamp INFO] </c> prefix stayed in
    /// <c>Message</c>, no matcher could match it, and a backup reported a
    /// critical thirty seconds later for a resume that had in fact arrived. The
    /// same byte on a <c>Server started</c> line leaves the instance in
    /// <c>Starting</c> for ever — the watchdog's stability window never closes
    /// and the update health gate never sees ready, which rolls back a build that
    /// was fine.
    /// <para>
    /// Deliberately tiny, and the length bound is the lesser of the two guards.
    /// The real one is that the remainder must parse as a <em>strict</em> prefix —
    /// a timestamp, a recognised level, a <c>]</c> — so a bare content line that
    /// merely contains a bracket is untouched. The bound stops the search
    /// wandering into somebody's chat message looking for one.
    /// </para>
    /// </remarks>
    private const int MaxStrayPrefixBytes = 4;

    /// <summary>
    /// Parses <paramref name="raw"/> into its parts. Never throws and never
    /// returns null: a line that matches nothing is still a line.
    /// </summary>
    public static (BdsLogLevel Level, string Message, DateTime? Timestamp) Parse(string? raw)
    {
        var text = raw ?? string.Empty;

        // Stripped rather than tolerated in place, so everything downstream —
        // matching, display, the transcript — sees the line BDS would have
        // written if it had a log file. The fact that it does not is worth
        // saying once, not on all six thousand lines of a startup.
        if (text.StartsWith(NoLogFilePrefix, StringComparison.Ordinal))
        {
            text = text[NoLogFilePrefix.Length..];
        }

        if (TryParsePrefixed(text, out var line))
        {
            return line;
        }

        // Nothing parsed at the start of the line. Before giving up, look for a
        // prefix that something short got in front of — see MaxStrayPrefixBytes.
        // Only a strict parse is accepted, so this cannot reinterpret content.
        var bracket = text.IndexOf('[', StringComparison.Ordinal);
        if (bracket > 0
            && bracket <= MaxStrayPrefixBytes
            && TryParsePrefixed(text[bracket..], out var salvaged))
        {
            return salvaged;
        }

        return (BdsLogLevel.Unknown, text, null);
    }

    /// <summary>
    /// One strict attempt at <c>[timestamp LEVEL] message</c>, anchored at the
    /// start of <paramref name="text"/>. False means "this is content", and the
    /// caller keeps the line whole.
    /// </summary>
    private static bool TryParsePrefixed(
        string text,
        out (BdsLogLevel Level, string Message, DateTime? Timestamp) result)
    {
        result = (BdsLogLevel.Unknown, text, null);

        // A prefix is "[", then a timestamp, a space, a level, then "] ". Anything
        // else — including a line that merely starts with "[" — is content.
        if (text.Length < 3 || text[0] != '[')
        {
            return false;
        }

        var close = text.IndexOf(']');
        if (close < 0)
        {
            return false;
        }

        var inside = text.AsSpan(1, close - 1);
        var split = inside.LastIndexOf(' ');
        if (split <= 0)
        {
            return false;
        }

        var levelSpan = inside[(split + 1)..];
        var level = levelSpan switch
        {
            "INFO" => BdsLogLevel.Info,
            "WARN" => BdsLogLevel.Warn,
            "ERROR" => BdsLogLevel.Error,
            _ => BdsLogLevel.Unknown,
        };

        // An unrecognised level means this bracket was not a log prefix at all —
        // a chat message or a pack name could open with one. Keep the line whole
        // rather than guessing at a split point inside someone's text.
        if (level == BdsLogLevel.Unknown)
        {
            return false;
        }

        DateTime? timestamp = null;
        if (DateTime.TryParseExact(
                inside[..split],
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            timestamp = parsed;
        }

        // Exactly one space follows "]"; TrimStart would eat the indentation that
        // makes the startup banner and the allow-list block readable.
        var messageStart = close + 1;
        if (messageStart < text.Length && text[messageStart] == ' ')
        {
            messageStart++;
        }

        result = (level, text[messageStart..], timestamp);
        return true;
    }
}
