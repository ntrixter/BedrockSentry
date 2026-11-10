using System.Text.Json.Serialization;

namespace BedrockSentry.Core.Commands;

/// <summary>The three shapes a word in one of the server's command forms takes.</summary>
public enum ParamKind
{
    /// <summary>A fixed word the command takes as written: <c>clear</c> in <c>/camera &lt;players: target&gt; clear</c>.</summary>
    Literal,

    /// <summary>One of a few fixed words, written <c>&lt;eyes|feet&gt;</c>.</summary>
    Choice,

    /// <summary>A labelled value of a type: <c>&lt;players: target&gt;</c>, <c>[amount: int]</c>.</summary>
    Typed,
}

/// <summary>One word of a command form, as the server's <c>help</c> prints it.</summary>
/// <param name="Word">The literal word, for <see cref="ParamKind.Literal"/>; null otherwise.</param>
/// <param name="Label">The name the server gives the value, for <see cref="ParamKind.Typed"/>.</param>
/// <param name="Type">
/// The value's type, for <see cref="ParamKind.Typed"/>: a built-in such as <c>int</c>,
/// <c>target</c> or <c>x y z</c>, or the name of one of the server's option lists such
/// as <c>GameMode</c>.
/// </param>
/// <param name="Choices">The words of a <see cref="ParamKind.Choice"/>.</param>
/// <param name="Optional">Written in square brackets.</param>
/// <param name="Suffix">
/// Text the server glues to the value: the <c>L</c> of <c>&lt;amount: int&gt;L</c>, which
/// <c>xp 5L</c> takes.
/// </param>
public sealed record CommandParam(
    ParamKind Kind,
    string? Word,
    string? Label,
    string? Type,
    IReadOnlyList<string>? Choices,
    bool Optional,
    string? Suffix)
{
    /// <summary>A fixed word.</summary>
    public static CommandParam Literal(string word) =>
        new(ParamKind.Literal, word, null, null, null, false, null);

    /// <summary>One of a few fixed words.</summary>
    public static CommandParam Choice(IReadOnlyList<string> choices, bool optional) =>
        new(ParamKind.Choice, null, null, null, choices, optional, null);

    /// <summary>A labelled value of a type.</summary>
    public static CommandParam Typed(string label, string type, bool optional, string? suffix = null) =>
        new(ParamKind.Typed, null, label, type, null, optional, suffix is { Length: > 0 } ? suffix : null);

    /// <summary>The word as the server writes it in a form.</summary>
    [JsonIgnore]
    public string Text => Kind switch
    {
        ParamKind.Literal => Word ?? string.Empty,
        ParamKind.Choice => Bracket(string.Join('|', Choices ?? [])),
        _ => Bracket($"{Label}: {Type}") + Suffix,
    };

    private string Bracket(string inner) => Optional ? $"[{inner}]" : $"<{inner}>";
}

/// <summary>One way to write a command: one line of the server's <c>help</c>.</summary>
/// <param name="Line">
/// The line as the server printed it, leading <c>/</c> included. The Commands view shows
/// this rather than a line rebuilt from <paramref name="Params"/>, so what an
/// administrator reads there is the server's own text.
/// </param>
/// <param name="Params">The words after the command's name.</param>
public sealed record CommandForm(string Line, IReadOnlyList<CommandParam> Params);
