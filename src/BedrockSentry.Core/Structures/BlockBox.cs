namespace BedrockSentry.Core.Structures;

/// <summary>A box of blocks, both corners included.</summary>
public readonly record struct BlockBox
{
    private BlockBox(BlockVector min, BlockVector max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>The corner with the lowest coordinate on every axis.</summary>
    public BlockVector Min { get; }

    /// <summary>The corner with the highest coordinate on every axis.</summary>
    public BlockVector Max { get; }

    /// <summary>The block box two corners span, whichever way round they are given.</summary>
    public static BlockBox Between(BlockVector a, BlockVector b) => new(
        new BlockVector(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
        new BlockVector(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));

    /// <summary>How many blocks it spans along each axis.</summary>
    public BlockVector Size => new(Max.X - Min.X + 1, Max.Y - Min.Y + 1, Max.Z - Min.Z + 1);

    /// <summary>How many block positions it holds.</summary>
    public long Volume => (long)Size.X * Size.Y * Size.Z;

    public override string ToString() => $"{Min} to {Max}";
}

/// <summary>The lowest and highest block a dimension holds, both included.</summary>
public readonly record struct WorldHeight(int Bottom, int Top)
{
    /// <summary>
    /// The overworld: <c>-64</c> to <c>319</c>. Measured on BDS 1.26.52.3, a
    /// structure saved from <c>-64</c> to <c>319</c> in one column spans 384, the
    /// height <c>/structure</c> caps a structure at.
    /// </summary>
    public static WorldHeight Overworld { get; } = new(-64, 319);

    /// <summary>
    /// The Nether: <c>0</c> to <c>127</c>. Measured on BDS 1.26.52.3: <c>setblock</c> placed at
    /// 0 and 127 and refused −1 and 128 (<c>D106</c>).
    /// </summary>
    public static WorldHeight Nether { get; } = new(0, 127);

    /// <summary>The End: <c>0</c> to <c>255</c>, measured the same way.</summary>
    public static WorldHeight End { get; } = new(0, 255);
}
