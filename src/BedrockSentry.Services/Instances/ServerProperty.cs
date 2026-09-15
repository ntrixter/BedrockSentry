using System.Globalization;
using System.Text.RegularExpressions;

namespace BedrockSentry.Services.Instances;

/// <summary>How a setting should be presented for editing.</summary>
public enum ServerPropertyEditor
{
    /// <summary>A free-text box. The fallback, and the honest one.</summary>
    Text,

    /// <summary>Exactly true or false.</summary>
    Boolean,

    /// <summary>One of a fixed set the file itself lists.</summary>
    Choice,

    /// <summary>A number, possibly bounded.</summary>
    Number,
}

/// <summary>
/// One setting out of a <c>server.properties</c> file, together with the
/// documentation Mojang ships beside it.
/// </summary>
/// <remarks>
/// Nothing here is a wrapper-maintained list. Bedrock's own file carries a
/// description and an "Allowed values" line for every setting, so BedrockSentry
/// reads those instead of keeping a copy that would go stale the first time a
/// default changed — and a setting added in a future BDS release arrives
/// documented on day one rather than as an unlabelled text box.
/// </remarks>
/// <param name="Key">The setting name, e.g. <c>server-port</c>.</param>
/// <param name="Value">
/// Its current value. For a setting that is commented out this is the value on
/// the commented line, which is how Mojang writes the documented default.
/// </param>
/// <param name="IsSet">
/// False when the line is commented out. The server is using its built-in
/// default, which may or may not be the value shown.
/// </param>
/// <param name="Description">The prose above the allowed-values line, if any.</param>
/// <param name="AllowedValues">The file's own "Allowed values" text, verbatim.</param>
/// <param name="DocumentedDefault">The file's own "Default:" text, when it states one.</param>
public sealed record ServerProperty(
    string Key,
    string Value,
    bool IsSet,
    string? Description,
    string? AllowedValues,
    string? DocumentedDefault,
    ServerPropertyEditor Editor,
    IReadOnlyList<string> Choices,
    decimal? Minimum,
    decimal? Maximum,
    bool IsInteger)
{
    /// <summary>
    /// Whether <paramref name="candidate"/> is something this setting could hold.
    /// </summary>
    /// <remarks>
    /// Deliberately split from range checking. A value of the wrong shape — text
    /// in a number, a choice that is not on the list — cannot be what anyone
    /// meant, so it is refused. A number outside the documented range only earns
    /// a warning: Mojang's own documentation contradicts the implementation in at
    /// least one place (<c>max-threads</c> says "any positive integer" and then
    /// says 0 means "as many as possible"), and a wrapper that refuses a value
    /// the server accepts is worse than one that says it looks wrong.
    /// </remarks>
    public string? Reject(string candidate)
    {
        switch (Editor)
        {
            case ServerPropertyEditor.Boolean:
            case ServerPropertyEditor.Choice:
                return Choices.Contains(candidate, StringComparer.OrdinalIgnoreCase)
                    ? null
                    : $"must be one of {string.Join(", ", Choices)}";

            case ServerPropertyEditor.Number:
                if (candidate.Length == 0)
                {
                    return "needs a number";
                }

                if (!decimal.TryParse(
                        candidate, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    return "is not a number";
                }

                return IsInteger && number != decimal.Truncate(number)
                    ? "must be a whole number"
                    : null;

            default:
                // The one thing every value has to survive is being written back
                // out: a newline would turn one setting into two, and the second
                // would be nonsense the server refuses to parse.
                return candidate.AsSpan().IndexOfAny('\r', '\n') >= 0
                    ? "cannot contain a line break"
                    : null;
        }
    }

    /// <summary>
    /// A note about <paramref name="candidate"/> that is worth showing but not
    /// worth blocking a save over. Null when there is nothing to say.
    /// </summary>
    public string? Warn(string candidate)
    {
        if (Editor is not ServerPropertyEditor.Number
            || !decimal.TryParse(
                candidate, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        if (Minimum is { } min && number < min)
        {
            return $"the file documents a minimum of {min}";
        }

        return Maximum is { } max && number > max
            ? $"the file documents a maximum of {max}"
            : null;
    }
}

/// <summary>
/// Turns an "Allowed values" line into something the UI can render as a control.
/// </summary>
public static partial class AllowedValues
{
    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex QuotedToken();

    /// <summary>Matches <c>[1, 65535]</c> and <c>[0.0, 1.0]</c>.</summary>
    [GeneratedRegex(@"\[\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\]")]
    private static partial Regex BracketRange();

    /// <summary>Matches a bare <c>0-65535</c>, and nothing else on the line.</summary>
    [GeneratedRegex(@"^(-?\d+)\s*-\s*(-?\d+)$")]
    private static partial Regex DashRange();

    /// <summary>Matches "equal to 5 or greater".</summary>
    [GeneratedRegex(@"(-?\d+)\s+or\s+greater", RegexOptions.IgnoreCase)]
    private static partial Regex OrGreater();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]*$")]
    private static partial Regex BareToken();

    /// <summary>
    /// Matches a description that gives zero a meaning of its own — "if set to
    /// 0", "0 or removed", "0 means unlimited", "0 disables this".
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. A bare <c>\b0\b</c> would match the "0" inside
    /// "0.85" — the shipped default of
    /// <c>player-movement-action-direction-threshold</c> — and every bracket
    /// range that starts at zero, so the phrasings are named instead. Each is
    /// taken from how Mojang actually writes it in the file.
    /// </remarks>
    [GeneratedRegex(
        @"(?:set\s+to|value\s+of|using|use)\s+0(?![.\d])"
        + @"|(?<![.\d])0(?![.\d])\s+(?:or\s+removed|means|disables|indicates|is\s+unlimited|for\s+unlimited)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ZeroIsMeaningful();

    /// <summary>
    /// Reads <paramref name="allowed"/> and, failing that, falls back to what
    /// <paramref name="currentValue"/> looks like.
    /// </summary>
    /// <param name="description">
    /// The prose above the allowed-values line. Consulted because Mojang's two
    /// halves disagree: <c>max-threads</c> says "Any positive integer" and then,
    /// a sentence earlier, "If set to 0 or removed then it will use as many as
    /// possible". Reading only the allowed-values line produced a minimum of 1
    /// and warned about a value the file itself documents — reported from a real
    /// install, with the contradiction visible in the same row of the editor.
    /// </param>
    public static (ServerPropertyEditor Editor, IReadOnlyList<string> Choices,
        decimal? Minimum, decimal? Maximum, bool IsInteger) Interpret(
        string? allowed, string currentValue, string? description = null)
    {
        var text = Collapse(allowed);

        if (text.Length == 0)
        {
            // No documentation at all. Several of the movement-strictness
            // settings are like this. A value of exactly true or false is
            // unambiguous enough to offer as a toggle; anything else stays a text
            // box rather than being guessed at.
            return IsBoolean(currentValue)
                ? (ServerPropertyEditor.Boolean, BooleanChoices, null, null, false)
                : (ServerPropertyEditor.Text, [], null, null, false);
        }

        if (Choices(text) is { Count: >= 2 } choices)
        {
            return choices.Count == 2
                   && choices.Any(c => IsTrue(c))
                   && choices.Any(c => IsFalse(c))
                ? (ServerPropertyEditor.Boolean, BooleanChoices, null, null, false)
                : (ServerPropertyEditor.Choice, choices, null, null, false);
        }

        // A quoted value that did not resolve into a list means the setting
        // accepts more than one kind of thing — server-build-radius-ratio takes
        // the word "Disabled" *or* a fraction. A number field would refuse the
        // shipped default; a dropdown would hide every number. Text takes both.
        if (QuotedToken().IsMatch(text))
        {
            return (ServerPropertyEditor.Text, [], null, null, false);
        }

        var range = Range(text);

        // Lowered rather than removed. "Any positive integer" plus a documented
        // zero means the real range is 0 and up, not that the range is unknown —
        // and a negative thread count is still worth flagging.
        return range.Item3 is > 0 && ZeroIsMeaningful().IsMatch(Collapse(description))
            ? (range.Item1, range.Item2, 0, range.Item4, range.Item5)
            : range;
    }

    private static readonly string[] BooleanChoices = ["true", "false"];

    private static bool IsBoolean(string value) => IsTrue(value) || IsFalse(value);

    private static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static bool IsFalse(string value) => value.Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whitespace-collapsed, so a wrapped line reads as one sentence.</summary>
    private static string Collapse(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// The fixed set of values the text lists, or null when it is describing
    /// something else.
    /// </summary>
    private static IReadOnlyList<string>? Choices(string text)
    {
        // Mojang quotes the members of a set: "survival", "creative", or
        // "adventure". Requiring that what is left over after removing them is
        // only punctuation is what keeps '"Disabled" or any value in range
        // [0.0, 1.0]' from being mistaken for a one-item list.
        var quoted = QuotedToken().Matches(text);
        if (quoted.Count >= 2)
        {
            var remainder = QuotedToken().Replace(text, string.Empty);
            return IsOnlySeparators(remainder)
                ? quoted.Select(m => m.Groups[1].Value).ToList()
                : null;
        }

        if (quoted.Count > 0)
        {
            return null;
        }

        // A few settings list their values unquoted — "Allowed values: true,
        // false". Every part has to be a single bare word for this to be a list
        // rather than a sentence that happens to contain a comma.
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 && parts.All(p => BareToken().IsMatch(p)) ? parts : null;
    }

    private static bool IsOnlySeparators(string remainder)
    {
        foreach (var word in remainder.Split(
                     (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var stripped = word.Trim(',', '.', ';', ':');
            if (stripped.Length != 0
                && !stripped.Equals("or", StringComparison.OrdinalIgnoreCase)
                && !stripped.Equals("and", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static (ServerPropertyEditor, IReadOnlyList<string>, decimal?, decimal?, bool) Range(string text)
    {
        decimal? minimum = null;
        decimal? maximum = null;
        var numeric = false;

        // "Any value in the range of [0, 1]" is a fraction; "Integers in the
        // range [4, 12]" is not. The bounds look identical, so the word is the
        // only thing that separates them — and getting it wrong turns 0.85, the
        // shipped default of player-movement-action-direction-threshold, into a
        // value the editor refuses.
        var isInteger = Says(text, "integer");

        if (BracketRange().Match(text) is { Success: true } bracket)
        {
            minimum = Parse(bracket.Groups[1].Value);
            maximum = Parse(bracket.Groups[2].Value);
            numeric = true;
        }
        else if (DashRange().Match(text) is { Success: true } dash)
        {
            // The whole line is "0-65535" and nothing else, so there is no word
            // to go on — but there is no decimal point either.
            minimum = Parse(dash.Groups[1].Value);
            maximum = Parse(dash.Groups[2].Value);
            numeric = true;
            isInteger = true;
        }
        else if (OrGreater().Match(text) is { Success: true } floor)
        {
            minimum = Parse(floor.Groups[1].Value);
            numeric = true;
        }
        else if (Says(text, "non-negative integer"))
        {
            minimum = 0;
            numeric = true;
        }
        else if (Says(text, "positive integer"))
        {
            minimum = 1;
            numeric = true;
        }
        else if (isInteger)
        {
            numeric = true;
        }

        return numeric
            ? (ServerPropertyEditor.Number, [], minimum, maximum, isInteger)
            : (ServerPropertyEditor.Text, [], null, null, false);
    }

    private static bool Says(string text, string phrase) =>
        text.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    private static decimal? Parse(string text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
