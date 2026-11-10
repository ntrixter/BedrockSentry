using System.Globalization;

namespace BedrockSentry.Core.Structures;

/// <summary>One piece of an area: a box no larger than one structure holds, and its name.</summary>
/// <param name="Index">Its place in <see cref="StructurePlan.Pieces"/>, from 0.</param>
public sealed record StructurePiece(int Index, BlockBox Box, StructureName Name);

/// <summary>A piece and how the console names it, such as <c>Spawn town, piece 1 of 4</c>.</summary>
/// <param name="Dimension">The dimension the piece is in; null is the overworld.</param>
public sealed record LabelledPiece(StructurePiece Piece, string Label, BedrockDimension? Dimension = null)
{
    /// <summary>The dimension the piece is in.</summary>
    public BedrockDimension In => Dimension ?? BedrockDimension.Overworld;
}

/// <summary>What splitting an area came to.</summary>
public enum StructurePlanState
{
    /// <summary>Split; the pieces cover the area exactly.</summary>
    Planned,

    /// <summary>Every block of the area is above or below the world.</summary>
    OutsideTheWorld,

    /// <summary>The area needs more pieces than one change takes.</summary>
    TooManyPieces,

    /// <summary>The name the pieces were to share cannot name a structure the pack holds.</summary>
    BadName,
}

/// <summary>An area split into pieces, or why it was not.</summary>
/// <param name="Box">The area after it was brought inside the world's height; null when refused.</param>
/// <param name="HeightClamped">
/// The corners reached above or below the world and the area stops at its top or
/// bottom, which a page says rather than leaves to be noticed.
/// </param>
public sealed record StructurePlan(
    StructurePlanState State,
    BlockBox? Box,
    IReadOnlyList<StructurePiece> Pieces,
    bool HeightClamped,
    string? Reason)
{
    /// <summary>
    /// About how many bytes the pieces' files take: eight a block. Measured on BDS
    /// 1.26.52.3, a 64 × 384 × 64 structure (1,572,864 blocks) was 12,589,511 bytes,
    /// two lists of one integer per block; a chest's contents or a few entities add
    /// little beside them.
    /// </summary>
    public long EstimatedBytes => Pieces.Sum(p => p.Box.Volume * StructurePieces.BytesPerBlock);
}

/// <summary>
/// Splits an area into pieces <c>/structure</c> can save and load one at a time
/// (<c>D86</c>, <c>D103</c>).
/// </summary>
/// <remarks>
/// <para><strong>One structure holds at most 64 × 384 × 64</strong> (measured on
/// BDS 1.26.52.3: <em>"A structure's size cannot be larger than (64, 384, 64)"</em>),
/// so a larger area is several. Pieces start at the area's own lowest corner rather
/// than at a 64-block grid, which keeps the count at its least: an area 64 wide is
/// one piece wherever it lies, and on a grid it could be two.</para>
///
/// <para><strong>One ticking area per piece fits.</strong> Each piece is loaded by
/// a ticking area of its own while it is saved or placed, reaching one chunk past
/// it (<see cref="StructureCommands.Ring"/>). A ticking area covers at most 100
/// chunks (measured: <em>"Ticking area contains more than 100 chunks, ticking
/// area is too large and cannot be created"</em>), and a piece 64 blocks across
/// with its ring touches at most seven chunks a side, 49 in all.</para>
///
/// <para><strong>Pieces run west to east, then north to south</strong>, then up:
/// x fastest, then z, then y. The overworld is 384 tall, so an area never needs a
/// second piece upward there.</para>
/// </remarks>
public static class StructurePieces
{
    /// <summary>The most pieces one change takes: an area about 1,024 blocks square at full height.</summary>
    /// <remarks>
    /// A bound on a slip rather than a policy. A corner typed with an extra digit
    /// asks for thousands of pieces, gigabytes of files and an afternoon of the
    /// server loading them; this refuses it with the count, before anything runs.
    /// </remarks>
    public const int MaxPieces = 256;

    /// <summary>The bytes a structure file takes per block, measured: see <see cref="StructurePlan.EstimatedBytes"/>.</summary>
    public const long BytesPerBlock = 8;

    /// <summary>Splits the area between two corners.</summary>
    /// <param name="a">One corner, included.</param>
    /// <param name="b">The other corner, included.</param>
    /// <param name="prefix">
    /// What every piece's name starts with; piece <c>n</c> is
    /// <c><see cref="StructurePack.Namespace"/>:prefix_n</c>.
    /// </param>
    /// <param name="height">The heights the world holds.</param>
    public static StructurePlan Plan(BlockVector a, BlockVector b, string prefix, WorldHeight height)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var asked = BlockBox.Between(a, b);

        if (asked.Min.Y > height.Top || asked.Max.Y < height.Bottom)
        {
            return Refused(StructurePlanState.OutsideTheWorld,
                $"Every block from {asked} is outside the world, which runs from height "
                + $"{height.Bottom} to {height.Top}.");
        }

        var bottom = Math.Max(asked.Min.Y, height.Bottom);
        var top = Math.Min(asked.Max.Y, height.Top);
        var clamped = bottom != asked.Min.Y || top != asked.Max.Y;
        var box = BlockBox.Between(
            asked.Min with { Y = bottom },
            asked.Max with { Y = top });

        var max = StructureFile.MaxSize;
        var across = Count(box.Size.X, max.X);
        var up = Count(box.Size.Y, max.Y);
        var down = Count(box.Size.Z, max.Z);
        var total = (long)across * up * down;

        if (total > MaxPieces)
        {
            return Refused(StructurePlanState.TooManyPieces,
                $"The area from {box} needs {total.ToString("N0", CultureInfo.InvariantCulture)} pieces, and one change takes at most "
                + $"{MaxPieces}. Check the corners, or split it into smaller areas.");
        }

        var last = $"{prefix}_{total - 1}";

        if (!StructureName.IsPlainPart(prefix) || !StructureName.IsPlainPart(last))
        {
            return Refused(StructurePlanState.BadName,
                $"\"{prefix}\" cannot start a piece's name: names are lower-case letters, digits and "
                + "underscores, at most 64 characters with the piece's number.");
        }

        var pieces = new List<StructurePiece>((int)total);

        for (var y = 0; y < up; y++)
        {
            for (var z = 0; z < down; z++)
            {
                for (var x = 0; x < across; x++)
                {
                    var min = new BlockVector(
                        box.Min.X + (x * max.X), box.Min.Y + (y * max.Y), box.Min.Z + (z * max.Z));
                    var far = new BlockVector(
                        Math.Min(min.X + max.X - 1, box.Max.X),
                        Math.Min(min.Y + max.Y - 1, box.Max.Y),
                        Math.Min(min.Z + max.Z - 1, box.Max.Z));

                    var index = pieces.Count;
                    pieces.Add(new StructurePiece(
                        index,
                        BlockBox.Between(min, far),
                        StructureName.Of(StructurePack.Namespace, $"{prefix}_{index}")));
                }
            }
        }

        return new StructurePlan(StructurePlanState.Planned, box, pieces, clamped, null);
    }

    private static int Count(int length, int most) => (length + most - 1) / most;

    private static StructurePlan Refused(StructurePlanState state, string reason) =>
        new(state, null, [], false, reason);
}
