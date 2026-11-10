using System.Text.RegularExpressions;

namespace BedrockSentry.Core.Commands;

/// <summary>Reads one form line of the server's <c>help</c>.</summary>
/// <remarks>
/// <para>A line is <c>/name</c> and then its words: <c>&lt;label: type&gt;</c> for a
/// value, <c>[label: type]</c> for an optional one, <c>&lt;a|b&gt;</c> for a choice of
/// words, and bare words the command takes as written. Measured on BDS 1.26.52.3, whose
/// 304 lines are the fixture.</para>
///
/// <para><strong>Brackets nest.</strong> <c>loot</c> writes its last word
/// <c>[&lt;tool&gt;|mainhand|offhand: string]</c>, so a reader that ends a word at the
/// first closing bracket cuts it in two. A suffix glued to a bracket stays with it:
/// <c>&lt;time: int&gt;D</c> is one word.</para>
/// </remarks>
public static partial class HelpLine
{
    // The last ": " splits a label from its type, because a label may hold spaces
    // ("x angle") and a type may too ("x y z"), but neither holds ": ".
    [GeneratedRegex(@"^(?<label>.*): (?<type>[^:]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelAndType { get; }

    /// <summary>
    /// The one word whose label is itself a choice: an item, or the hand holding one.
    /// </summary>
    /// <remarks>
    /// The server types it <c>string</c>, which would complete nothing. It is read as
    /// its own type so completion can offer <c>mainhand</c>, <c>offhand</c> and the items.
    /// </remarks>
    public const string ToolOrHand = "ToolOrHand";

    private const string ToolOrHandLabel = "<tool>|mainhand|offhand";

    /// <summary>The command's name and the form, or null when the line is not a form.</summary>
    public static (string Name, CommandForm Form)? Parse(string? line)
    {
        if (line is not { Length: > 1 } || line[0] != '/' || line[1] == ' ')
        {
            return null;
        }

        var space = line.IndexOf(' ', StringComparison.Ordinal);
        var name = space < 0 ? line[1..] : line[1..space];
        var rest = space < 0 ? string.Empty : line[(space + 1)..];

        var words = new List<CommandParam>();
        foreach (var (word, suffix) in Split(rest))
        {
            words.Add(Read(word, suffix));
        }

        return (name, new CommandForm(line, words));
    }

    /// <summary>The words of a form, a bracketed one kept whole with any suffix glued to it.</summary>
    internal static IEnumerable<(string Word, string Suffix)> Split(string rest)
    {
        var i = 0;
        while (i < rest.Length)
        {
            if (rest[i] == ' ')
            {
                i++;
                continue;
            }

            if (rest[i] is '<' or '[')
            {
                var depth = 0;
                var j = i;
                for (; j < rest.Length; j++)
                {
                    if (rest[j] is '<' or '[')
                    {
                        depth++;
                    }
                    else if (rest[j] is '>' or ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            break;
                        }
                    }
                }

                // An unclosed bracket runs to the end of the line rather than being
                // dropped: a word the server printed is shown, even mangled.
                var close = Math.Min(j + 1, rest.Length);
                var k = close;
                while (k < rest.Length && rest[k] != ' ')
                {
                    k++;
                }

                yield return (rest[i..close], rest[close..k]);
                i = k;
            }
            else
            {
                var j = i;
                while (j < rest.Length && rest[j] != ' ')
                {
                    j++;
                }

                yield return (rest[i..j], string.Empty);
                i = j;
            }
        }
    }

    private static CommandParam Read(string word, string suffix)
    {
        if (word[0] is not ('<' or '['))
        {
            return CommandParam.Literal(word);
        }

        var optional = word[0] == '[';
        var closed = word.Length >= 2 && word[^1] is '>' or ']';
        var inner = word[1..(closed ? word.Length - 1 : word.Length)];

        var match = LabelAndType.Match(inner);
        if (!match.Success)
        {
            return CommandParam.Choice(inner.Split('|'), optional);
        }

        var label = match.Groups["label"].Value;
        var type = match.Groups["type"].Value;

        return label == ToolOrHandLabel
            ? CommandParam.Typed("tool", ToolOrHand, optional, suffix)
            : CommandParam.Typed(label, type, optional, suffix);
    }
}
