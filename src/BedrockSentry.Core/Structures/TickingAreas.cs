using System.Globalization;
using System.Text.RegularExpressions;
using BedrockSentry.Core.Instances;

namespace BedrockSentry.Core.Structures;

/// <summary>A ticking area's shape, as <c>tickingarea add</c> and <c>add circle</c> make them.</summary>
public enum TickingAreaShape
{
    Box,
    Circle,
}

/// <summary>One of the world's ticking areas, as <c>tickingarea list all-dimensions</c> lists it (<c>D107</c>).</summary>
/// <param name="From">A box's first corner, or a circle's centre, as listed.</param>
/// <param name="To">A box's second corner; a circle's centre again.</param>
/// <param name="Radius">A circle's radius in chunks; 0 for a box.</param>
/// <remarks>
/// BDS 1.26.52.3 snaps an area to whole chunks and lists y as 0: a box added from 100 to 115
/// is listed from 96 to 127, and a circle's centre is the middle of its chunk (300 is listed
/// as 296).
/// </remarks>
public sealed record TickingArea(
    string Name, BedrockDimension Dimension, TickingAreaShape Shape, BlockVector From, BlockVector To, int Radius, bool Preload)
{
    /// <summary>Whether this is the area a sync adds while it runs (<see cref="StructureCommands.TickingAreaName"/>).</summary>
    public bool IsSyncs => Name == StructureCommands.TickingAreaName;

    /// <summary>The block columns it keeps loaded, whole chunks, both ends included; a circle's is its bounding square.</summary>
    public (int MinX, int MinZ, int MaxX, int MaxZ) Columns
    {
        get
        {
            if (Shape == TickingAreaShape.Circle)
            {
                var half = (Radius * 16) + 8;
                var x = ChunkStart(From.X) + 8;
                var z = ChunkStart(From.Z) + 8;
                return (x - half, z - half, x + half - 1, z + half - 1);
            }

            return (
                ChunkStart(Math.Min(From.X, To.X)), ChunkStart(Math.Min(From.Z, To.Z)),
                ChunkStart(Math.Max(From.X, To.X)) + 15, ChunkStart(Math.Max(From.Z, To.Z)) + 15);
        }
    }

    /// <summary>What the panel shows for where it is.</summary>
    public string Extent => Shape == TickingAreaShape.Circle
        ? $"circle at {From.X} {From.Z}, radius {Radius} {(Radius == 1 ? "chunk" : "chunks")}"
        : $"{From.X} {From.Z} to {To.X} {To.Z}";

    private static int ChunkStart(int block) => (int)Math.Floor(block / 16.0) * 16;
}

/// <summary>The world's ticking areas and how many of its ten are in use (<c>D107</c>).</summary>
/// <param name="Unread">Lines of the answer this could not read, kept rather than dropped.</param>
public sealed partial record TickingAreaList(IReadOnlyList<TickingArea> Areas, int InUse, int Max, IReadOnlyList<string> Unread)
{
    /// <summary>Ticking areas a world may have, in all its dimensions together (measured on BDS 1.26.52.3).</summary>
    public const int MaxPerWorld = 10;

    public static TickingAreaList Empty { get; } = new([], 0, MaxPerWorld, []);

    /// <summary>
    /// Reads the answer to <c>tickingarea list all-dimensions</c>: each line's message, the
    /// first as BDS printed it after its timestamp, the rest bare. Null when the first line
    /// is neither the list's header nor the answer for none.
    /// </summary>
    public static TickingAreaList? Parse(IReadOnlyList<string> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return null;
        }

        var first = TickingAreaAnswers.Plain(messages[0]);
        if (first == TickingAreaAnswers.NoneInAnyDimension)
        {
            return Empty;
        }

        if (!first.Contains(TickingAreaAnswers.ListHeader, StringComparison.Ordinal))
        {
            return null;
        }

        var areas = new List<TickingArea>();
        var unread = new List<string>();
        BedrockDimension? dimension = null;
        int? inUse = null, max = null;

        foreach (var raw in messages.Skip(1))
        {
            var line = TickingAreaAnswers.Plain(raw).Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (Count().Match(line) is { Success: true } count)
            {
                inUse = Number(count.Groups["used"].Value);
                max = Number(count.Groups["max"].Value);
                continue;
            }

            if (line.EndsWith(':') && Heading(line[..^1]) is { } heading)
            {
                dimension = heading;
                continue;
            }

            if (dimension is not null && Read(line, dimension) is { } area)
            {
                areas.Add(area);
                continue;
            }

            unread.Add(raw);
        }

        return new TickingAreaList(areas, inUse ?? areas.Count, max ?? MaxPerWorld, unread);
    }

    /// <summary>One <c>- name: …</c> line, as <c>list</c>, <c>remove</c> and <c>preload</c> print them.</summary>
    internal static TickingArea? Read(string line, BedrockDimension dimension)
    {
        if (Box().Match(line) is { Success: true } box)
        {
            return new TickingArea(
                box.Groups["name"].Value, dimension, TickingAreaShape.Box,
                Vector(box, "x1", "y1", "z1"), Vector(box, "x2", "y2", "z2"), 0, box.Groups["preload"].Success);
        }

        if (Circle().Match(line) is { Success: true } circle)
        {
            var centre = Vector(circle, "x", "y", "z");
            return new TickingArea(
                circle.Groups["name"].Value, dimension, TickingAreaShape.Circle,
                centre, centre, Number(circle.Groups["r"].Value), circle.Groups["preload"].Success);
        }

        return null;
    }

    private static BedrockDimension? Heading(string title) => title switch
    {
        "Overworld" => BedrockDimension.Overworld,
        "Nether" => BedrockDimension.Nether,
        "The End" => BedrockDimension.End,
        _ => null,
    };

    private static BlockVector Vector(Match m, string x, string y, string z) =>
        new(Number(m.Groups[x].Value), Number(m.Groups[y].Value), Number(m.Groups[z].Value));

    private static int Number(string text) => int.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?<used>\d+)/(?<max>\d+) ticking areas in use\.$")]
    private static partial Regex Count();

    [GeneratedRegex(@"^- (?<name>.+): (?<x1>-?\d+) (?<y1>-?\d+) (?<z1>-?\d+) to (?<x2>-?\d+) (?<y2>-?\d+) (?<z2>-?\d+)(?<preload> Preload)?$")]
    private static partial Regex Box();

    [GeneratedRegex(@"^- (?<name>.+) \(Circle\): (?<x>-?\d+) (?<y>-?\d+) (?<z>-?\d+) Radius: (?<r>\d+) chunks?(?<preload> Preload)?$")]
    private static partial Regex Circle();
}
