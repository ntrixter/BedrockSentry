namespace BedrockSentry.Core.Commands;

/// <summary>Names in Mojang's data that the server refused where its forms take them.</summary>
/// <remarks>
/// <para>Measured by sending every name to BDS 1.26.52.3 with Mojang's 1.26.52 data: every
/// item, effect and enchantment was taken; 198 of 1,463 blocks were refused by
/// <c>setblock</c>, most of them Education Edition's chemistry blocks; 2 of 129 entities by
/// a <c>type=</c> selector and 20 by <c>summon</c>; and one biome. Offering them would
/// complete a word the server answers with a syntax error, so completion leaves them
/// out.</para>
///
/// <para>The list is that version's and is not measured again: a name a later version
/// starts to take stays out, and a new name it refuses is offered. Sending every name on
/// every version would add about three minutes to each run.</para>
///
/// <para>The text is sections: a <c>[Name]</c> line, then one name per line; <c>#</c>
/// starts a comment and anything after the heading's <c>]</c> says how it was tried.</para>
/// </remarks>
public static class MeasuredRefusals
{
    /// <summary>The version the list was measured on.</summary>
    public const string MeasuredOn = "1.26.52.3";

    /// <summary>Each section's names.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Parse(string text)
    {
        var sections = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        HashSet<string>? current = null;

        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.IndexOf(']', StringComparison.Ordinal) is var close and > 1)
            {
                current = new HashSet<string>(StringComparer.Ordinal);
                sections[line[1..close]] = current;
                continue;
            }

            current?.Add(line);
        }

        return sections;
    }
}
