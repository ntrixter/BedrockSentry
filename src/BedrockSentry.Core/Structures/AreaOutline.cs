using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BedrockSentry.Core.Structures;

/// <summary>An area the companion pack outlines in game for operators near it (<c>D106</c>).</summary>
/// <param name="Dimension">The dimension the area is in.</param>
/// <param name="Box">The blocks the area holds, both corners included.</param>
/// <param name="Color">The outline's colour, <c>#rrggbb</c>.</param>
/// <param name="Circle">Whether the pack draws the circle <paramref name="Box"/> bounds rather than the box (<c>D107</c>).</param>
public sealed record AreaOutline(BedrockDimension Dimension, BlockBox Box, string Color, bool Circle = false);

/// <summary>
/// The console commands that hand the companion pack a group of outlines:
/// <c>scriptevent bedrocksentry:outline</c> with a JSON message.
/// </summary>
/// <remarks>
/// <para><strong>The message.</strong>
/// <c>{"g":"sync","p":1,"n":2,"a":[["nether",0,60,0,15,80,15,"ffaa00"],…]}</c>: the group,
/// this part's number and how many parts there are, then one array per area of the dimension's
/// command name, the two corners and the colour without its <c>#</c>, and <c>"circle"</c> ninth
/// for a circle drawn inside those corners (from companion 1.4.0, <c>D107</c>). The pack replaces the
/// group's outlines once the last part arrives, so an empty list clears them
/// (<c>companion-pack/scripts/lib.js</c>, <c>OutlineStore</c>).</para>
///
/// <para><strong>Parts.</strong> BDS refused a <c>scriptevent</c> message of 3,000 characters
/// with <em>Message length exceeds (2048) character limit!</em> and passed one of 2,000 whole
/// on 1.26.52.3 (<c>D106</c> step 0), so a message is cut at <see cref="MaxMessageLength"/>
/// between areas.</para>
/// </remarks>
public static class AreaOutlines
{
    /// <summary>The scriptevent id the pack listens on: <c>OUTLINE_EVENT</c> in <c>scripts/main.js</c>.</summary>
    public const string EventId = "bedrocksentry:outline";

    /// <summary>The group the Sync Areas are sent under.</summary>
    public const string SyncGroup = "sync";

    /// <summary>The group the world's ticking areas are sent under (<c>D107</c>).</summary>
    public const string TickGroup = "tick";

    /// <summary>The longest message sent, under BDS's 2,048.</summary>
    public const int MaxMessageLength = 2000;

    /// <summary>The commands that replace <paramref name="group"/>'s outlines with <paramref name="outlines"/>; never empty.</summary>
    public static IReadOnlyList<string> Commands(string group, IReadOnlyList<AreaOutline> outlines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(outlines);

        // The heading of the longest part number this could need, so every part fits once numbered.
        var heading = Heading(group, outlines.Count + 1, outlines.Count + 1).Length + "]}".Length;
        var parts = new List<List<string>> { new() };
        var length = heading;

        foreach (var outline in outlines)
        {
            var area = Area(outline);
            var added = area.Length + (parts[^1].Count > 0 ? 1 : 0);

            if (parts[^1].Count > 0 && length + added > MaxMessageLength)
            {
                parts.Add([]);
                length = heading;
                added = area.Length;
            }

            parts[^1].Add(area);
            length += added;
        }

        return
        [
            .. parts.Select((areas, i) =>
                $"scriptevent {EventId} {Heading(group, i + 1, parts.Count)}{string.Join(',', areas)}]}}"),
        ];
    }

    private static string Heading(string group, int part, int parts) =>
        $"{{\"g\":{JsonSerializer.Serialize(group)},\"p\":{part},\"n\":{parts},\"a\":[";

    private static string Area(AreaOutline outline)
    {
        var (min, max) = (outline.Box.Min, outline.Box.Max);
        var text = new StringBuilder();
        text.Append('[').Append(JsonSerializer.Serialize(outline.Dimension.CommandName));

        foreach (var value in (int[])[min.X, min.Y, min.Z, max.X, max.Y, max.Z])
        {
            text.Append(',').Append(value.ToString(CultureInfo.InvariantCulture));
        }

        var color = AreaColors.IsValid(outline.Color) ? outline.Color[1..].ToLowerInvariant() : "ffffff";
        text.Append(",\"").Append(color).Append('"');
        return (outline.Circle ? text.Append(",\"circle\"") : text).Append(']').ToString();
    }
}
