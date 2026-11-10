namespace BedrockSentry.Core.Structures;

/// <summary>One of Bedrock's three dimensions: its id, the name commands take, and its height.</summary>
/// <param name="Id">The id the Script API and the configuration use, such as <c>minecraft:nether</c>.</param>
/// <param name="CommandName">The name <c>execute in</c> takes, such as <c>nether</c>.</param>
/// <param name="Title">What a page calls it, such as <c>Nether</c>.</param>
public sealed record BedrockDimension(string Id, string CommandName, string Title, WorldHeight Height)
{
    /// <summary>The overworld, −64 to 319.</summary>
    public static BedrockDimension Overworld { get; } = new("minecraft:overworld", "overworld", "Overworld", WorldHeight.Overworld);

    /// <summary>The Nether, 0 to 127.</summary>
    public static BedrockDimension Nether { get; } = new("minecraft:nether", "nether", "Nether", WorldHeight.Nether);

    /// <summary>The End, 0 to 255.</summary>
    public static BedrockDimension End { get; } = new("minecraft:the_end", "the_end", "End", WorldHeight.End);

    /// <summary>All three, in the order a page lists them.</summary>
    public static IReadOnlyList<BedrockDimension> All { get; } = [Overworld, Nether, End];

    /// <summary>
    /// The dimension an id or a command name names, ignoring case; empty is the overworld,
    /// which every server has; anything else is null.
    /// </summary>
    public static BedrockDimension? Find(string? name)
    {
        var wanted = name?.Trim() ?? string.Empty;
        if (wanted.Length == 0)
        {
            return Overworld;
        }

        return All.FirstOrDefault(d =>
            string.Equals(d.Id, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.CommandName, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <paramref name="command"/> as the console runs it in this dimension. The console runs
    /// in the overworld, so the overworld's commands go as they are, and the others go
    /// through <c>execute in</c>, measured to carry <c>tickingarea</c>, <c>testforblocks</c>,
    /// <c>structure</c> and <c>kill</c> on BDS 1.26.52.3 (<c>D106</c>).
    /// </summary>
    public string Run(string command) =>
        this == Overworld ? command : $"execute in {CommandName} run {command}";

    public override string ToString() => Title;
}
