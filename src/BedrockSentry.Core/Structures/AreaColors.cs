using System.Text.RegularExpressions;

namespace BedrockSentry.Core.Structures;

/// <summary>The colours an area's outline is drawn in, in game (<c>D106</c>).</summary>
/// <remarks>
/// White first, as the outline was asked for, then Minecraft's own bright text colours, so
/// each new area gets a colour of its own until all eight are taken. An area keeps the
/// colour it was given; one saved without a colour takes its place's colour in the list.
/// </remarks>
public static partial class AreaColors
{
    /// <summary>The palette, in the order new areas take it.</summary>
    public static IReadOnlyList<string> Palette { get; } =
    [
        "#ffffff", "#ffaa00", "#55ffff", "#ff55ff", "#55ff55", "#ffff55", "#ff5555", "#5555ff",
    ];

    /// <summary>Whether <paramref name="color"/> is written <c>#rrggbb</c>.</summary>
    public static bool IsValid(string? color) => color is not null && Hex().IsMatch(color);

    /// <summary>The colour an area at <paramref name="index"/> (from 0) is drawn in when it names none.</summary>
    public static string ForPlace(int index) => Palette[((index % Palette.Count) + Palette.Count) % Palette.Count];

    /// <summary>The colour an area is drawn in: its own when valid, otherwise its place's.</summary>
    public static string Of(string? color, int index) => IsValid(color) ? color!.ToLowerInvariant() : ForPlace(index);

    /// <summary>The first palette colour none of <paramref name="used"/> has, or the next in turn when all are.</summary>
    public static string Next(IEnumerable<string> used)
    {
        ArgumentNullException.ThrowIfNull(used);

        var taken = new HashSet<string>(used.Where(IsValid), StringComparer.OrdinalIgnoreCase);
        return Palette.FirstOrDefault(c => !taken.Contains(c)) ?? ForPlace(taken.Count);
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Hex();
}
