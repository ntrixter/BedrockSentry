using System.Globalization;
using BedrockSentry.Core.Structures;

namespace BedrockSentry.Core.Sync;

/// <summary>One Sync Area, split into the pieces a sync saves and puts back.</summary>
/// <param name="Number">Its place in the list, from 1; its pieces are named <c>keep_&lt;Number&gt;_&lt;n&gt;</c>.</param>
/// <param name="Dimension">The dimension it is in, or null when its dimension is not one Bedrock has.</param>
public sealed record KeptAreaPlan(int Number, string Name, StructurePlan Plan, BedrockDimension? Dimension = null)
{
    /// <summary>Its pieces, each labelled as the console names it.</summary>
    public IReadOnlyList<LabelledPiece> Pieces =>
    [
        .. Plan.Pieces.Select(p => new LabelledPiece(p, $"{Name}, piece {p.Index + 1} of {Plan.Pieces.Count}", Dimension)),
    ];
}

/// <summary>The Sync Areas of one server, split and checked (<c>D103</c>, <c>D106</c>).</summary>
/// <param name="Problems">Why a sync would be refused; empty when it would not.</param>
public sealed record SyncPlan(IReadOnlyList<KeptAreaPlan> Areas, IReadOnlyList<string> Problems)
{
    /// <summary>The longest name a Sync Area may have; it is said in every notice about its pieces.</summary>
    public const int MaxNameLength = 40;

    /// <summary>Whether a sync may run with these areas.</summary>
    public bool CanSync => Problems.Count == 0;

    /// <summary>Every piece of every area, in the order they are saved and put back.</summary>
    public IReadOnlyList<LabelledPiece> Pieces => [.. Areas.SelectMany(a => a.Pieces)];

    /// <summary>About how many bytes the pieces' files take, eight a block as measured.</summary>
    public long EstimatedBytes => Areas.Sum(a => a.Plan.EstimatedBytes);

    /// <summary>Splits and checks the Sync Areas.</summary>
    /// <remarks>
    /// <para>Each area is split on its own (<see cref="StructurePieces.Plan"/>) within its
    /// dimension's height, so a problem names the area it is in. Two areas in one
    /// dimension that share a block are refused, since the later would be put back over
    /// the earlier and what the earlier kept there would be lost without a word; the same
    /// coordinates in two dimensions are two places. Together they take at most
    /// <see cref="StructurePieces.MaxPieces"/> pieces, the bound one area has, for the same
    /// reason: a mistyped corner is caught before anything runs.</para>
    /// </remarks>
    public static SyncPlan For(IReadOnlyList<KeptAreaSettings> areas)
    {
        ArgumentNullException.ThrowIfNull(areas);

        var plans = new List<KeptAreaPlan>(areas.Count);
        var problems = new List<string>();

        for (var i = 0; i < areas.Count; i++)
        {
            var area = areas[i];
            var number = i + 1;
            var name = (area.Name ?? string.Empty).Trim();

            if (name.Length == 0)
            {
                problems.Add($"Sync Area {number} has no name.");
                name = $"Sync Area {number}";
            }
            else if (name.Length > MaxNameLength)
            {
                problems.Add($"\"{name}\" is longer than {MaxNameLength} characters, the most a Sync Area's name may be.");
            }

            var dimension = BedrockDimension.Find(area.Dimension);
            if (dimension is null)
            {
                problems.Add($"{name}: {area.Dimension} is not a dimension Bedrock has.");
            }

            var plan = StructurePieces.Plan(
                new BlockVector(area.X1, area.Y1, area.Z1),
                new BlockVector(area.X2, area.Y2, area.Z2),
                $"keep_{number}",
                (dimension ?? BedrockDimension.Overworld).Height);

            if (plan.State != StructurePlanState.Planned)
            {
                problems.Add($"{name}: {plan.Reason}");
            }

            plans.Add(new KeptAreaPlan(number, name, plan, dimension));
        }

        foreach (var group in plans.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"{group.Count()} Sync Areas are named {group.Key}; each needs a name of its own.");
        }

        var planned = plans.Where(p => p.Plan.Box is not null && p.Dimension is not null).ToList();

        for (var i = 0; i < planned.Count; i++)
        {
            for (var j = i + 1; j < planned.Count; j++)
            {
                if (planned[i].Dimension == planned[j].Dimension
                    && Overlap(planned[i].Plan.Box!.Value, planned[j].Plan.Box!.Value) is { } shared)
                {
                    var where = planned[i].Dimension == BedrockDimension.Overworld ? "" : $" in the {planned[i].Dimension}";
                    problems.Add(
                        $"{planned[i].Name} and {planned[j].Name} share the blocks from {shared}{where}. Sync Areas cannot "
                        + "overlap, or the later would be put back over what the earlier kept.");
                }
            }
        }

        var total = plans.Sum(p => p.Plan.Pieces.Count);

        if (total > StructurePieces.MaxPieces)
        {
            problems.Add(
                $"The Sync Areas need {total.ToString("N0", CultureInfo.InvariantCulture)} pieces together, and one "
                + $"sync takes at most {StructurePieces.MaxPieces}.");
        }

        return new SyncPlan(plans, problems);
    }

    private static BlockBox? Overlap(BlockBox a, BlockBox b)
    {
        var min = new BlockVector(Math.Max(a.Min.X, b.Min.X), Math.Max(a.Min.Y, b.Min.Y), Math.Max(a.Min.Z, b.Min.Z));
        var max = new BlockVector(Math.Min(a.Max.X, b.Max.X), Math.Min(a.Max.Y, b.Max.Y), Math.Min(a.Max.Z, b.Max.Z));

        return min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z ? BlockBox.Between(min, max) : null;
    }
}
