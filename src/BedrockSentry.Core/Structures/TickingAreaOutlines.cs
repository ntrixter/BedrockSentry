namespace BedrockSentry.Core.Structures;

/// <summary>
/// How the panel shows one of the world's ticking areas in game (<c>D107</c>): kept by its
/// dimension and name, since one name may be used in two dimensions (measured on BDS 1.26.52.3).
/// </summary>
public sealed class TickingAreaSettings
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The dimension's id, such as <c>minecraft:nether</c>.</summary>
    public string Dimension { get; set; } = string.Empty;

    public bool ShowInGame { get; set; }

    /// <summary>The outline's colour, <c>#rrggbb</c>; empty takes its place in the palette.</summary>
    public string Color { get; set; } = string.Empty;

    /// <summary>Whether these settings are for <paramref name="area"/>.</summary>
    public bool IsFor(TickingArea area)
    {
        ArgumentNullException.ThrowIfNull(area);
        return Name == area.Name && BedrockDimension.Find(Dimension) == area.Dimension;
    }
}

/// <summary>The ticking areas the companion pack outlines, and in what colour (<c>D107</c>).</summary>
/// <remarks>
/// Each is a full-height box over the whole chunks it keeps loaded, the dimension's bottom to its
/// top, as the operator chose on 2026-10-08; a circle is drawn as a circle inside that box.
/// An area the panel has settings for but the world no longer lists is not drawn.
/// </remarks>
public static class TickingAreaOutlines
{
    public static IReadOnlyList<AreaOutline> For(TickingAreaList list, IReadOnlyList<TickingAreaSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(settings);

        var outlines = new List<AreaOutline>();

        foreach (var area in list.Areas)
        {
            var index = Index(settings, area);
            if (index < 0 || !settings[index].ShowInGame)
            {
                continue;
            }

            var (minX, minZ, maxX, maxZ) = area.Columns;
            var height = area.Dimension.Height;
            outlines.Add(new AreaOutline(
                area.Dimension,
                BlockBox.Between(new BlockVector(minX, height.Bottom, minZ), new BlockVector(maxX, height.Top, maxZ)),
                AreaColors.Of(settings[index].Color, index),
                area.Shape == TickingAreaShape.Circle));
        }

        return outlines;
    }

    /// <summary>The commands that hand the pack the shown ticking areas, clearing the rest.</summary>
    public static IReadOnlyList<string> Commands(TickingAreaList list, IReadOnlyList<TickingAreaSettings> settings) =>
        AreaOutlines.Commands(AreaOutlines.TickGroup, For(list, settings));

    /// <summary>Where <paramref name="area"/>'s settings are, or -1.</summary>
    public static int Index(IReadOnlyList<TickingAreaSettings> settings, TickingArea area)
    {
        ArgumentNullException.ThrowIfNull(settings);

        for (var i = 0; i < settings.Count; i++)
        {
            if (settings[i].IsFor(area))
            {
                return i;
            }
        }

        return -1;
    }
}
