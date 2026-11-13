using BedrockSentry.Core.Structures;

namespace BedrockSentry.Core.Sync;

/// <summary>One ticking area a sync adds once its Sync Areas are back (<c>T439</c>).</summary>
/// <param name="Name">The Sync Area's name, which the ticking area takes.</param>
/// <param name="Remove">Sent first, so a ticking area of that name in the copied world is replaced.</param>
/// <param name="Add">The ticking area over the Sync Area's two corners, or null when it cannot have one.</param>
/// <param name="Problem">Why it cannot, when <paramref name="Add"/> is null.</param>
public sealed record SyncTickingArea(string Name, TickingAreaCommand Remove, TickingAreaCommand? Add, string? Problem);

/// <summary>
/// The ticking areas a sync adds over the Sync Areas ticked <see cref="KeptAreaSettings.KeepTicking"/>
/// (<c>T439</c>).
/// </summary>
/// <remarks>
/// The world a sync copies in carries the source's ticking areas, so one this server kept
/// loaded over a Sync Area was gone after every sync. A ticking area covers columns, so
/// only the corners' x and z are sent. Bedrock refuses an area larger than it allows and
/// names the limit itself; that refusal is reported, and the sync is still finished, since
/// every block is back.
/// </remarks>
public static class SyncTicking
{
    public static IReadOnlyList<SyncTickingArea> For(SyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var list = new List<SyncTickingArea>();

        foreach (var area in plan.Areas.Where(a => a.KeepTicking))
        {
            var dimension = area.Dimension ?? BedrockDimension.Overworld;
            var remove = TickingAreaCommands.Remove(area.Name, dimension);

            if (TickingAreaCommands.NameProblem(area.Name) is { } problem)
            {
                list.Add(new SyncTickingArea(area.Name, remove, null, problem));
            }
            else if (area.Plan.Box is not { } box)
            {
                list.Add(new SyncTickingArea(area.Name, remove, null, $"{area.Name} has no corners to cover."));
            }
            else
            {
                list.Add(new SyncTickingArea(
                    area.Name,
                    remove,
                    TickingAreaCommands.AddBox(area.Name, dimension, box.Min.X, box.Min.Z, box.Max.X, box.Max.Z, preload: false),
                    null));
            }
        }

        return list;
    }

    /// <summary>What the console says about one ticking area once BDS has answered.</summary>
    public static string Said(string name, bool ok, string answer) =>
        ok ? $"Kept a ticking area over Sync Area {name}."
           : $"Could not keep a ticking area over Sync Area {name}: {answer}";
}
