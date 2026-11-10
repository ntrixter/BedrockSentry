using BedrockSentry.Core.Structures;

namespace BedrockSentry.Core.Sync;

/// <summary>Which Sync Areas the companion pack outlines in game, and in what colour (<c>D106</c>).</summary>
public static class SyncOutlines
{
    /// <summary>
    /// The areas with Show in game ticked, in their colours. An area in a dimension Bedrock
    /// does not have is left out, since the pack could not place it; the Sync tab names it.
    /// </summary>
    public static IReadOnlyList<AreaOutline> For(IReadOnlyList<KeptAreaSettings> areas)
    {
        ArgumentNullException.ThrowIfNull(areas);

        var outlines = new List<AreaOutline>();

        for (var i = 0; i < areas.Count; i++)
        {
            var area = areas[i];

            if (area.ShowInGame && BedrockDimension.Find(area.Dimension) is { } dimension)
            {
                outlines.Add(new AreaOutline(
                    dimension,
                    BlockBox.Between(new BlockVector(area.X1, area.Y1, area.Z1), new BlockVector(area.X2, area.Y2, area.Z2)),
                    AreaColors.Of(area.Color, i)));
            }
        }

        return outlines;
    }

    /// <summary>The commands that hand the pack every outlined Sync Area, clearing the ones no longer outlined.</summary>
    public static IReadOnlyList<string> Commands(IReadOnlyList<KeptAreaSettings> areas) =>
        AreaOutlines.Commands(AreaOutlines.SyncGroup, For(areas));
}
