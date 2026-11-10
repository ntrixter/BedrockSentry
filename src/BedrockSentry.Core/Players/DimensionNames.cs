namespace BedrockSentry.Core.Players;

/// <summary>What a person reads for a dimension id.</summary>
/// <remarks>
/// The id is what is stored, and a name is made only where something is shown,
/// so a locale or a dimension added later cannot break a match. An id not listed
/// is shown as it is: named badly rather than wrongly. The companion pack keeps
/// the same list for the sentences it builds, and a test holds the two together.
/// </remarks>
public static class DimensionNames
{
    /// <summary>The ids this knows, and what each reads as.</summary>
    public static IReadOnlyDictionary<string, string> Known { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["minecraft:overworld"] = "the Overworld",
            ["minecraft:nether"] = "the Nether",
            ["minecraft:the_end"] = "the End",
        };

    /// <summary>The name for <paramref name="id"/>, or the id itself.</summary>
    public static string For(string id) =>
        Known.TryGetValue(id, out var name) ? name : id;

    /// <summary>
    /// "at 10, 64, -20 in the Overworld" — the wording the pack uses in its own
    /// sentences, so a page and the console say a place the same way.
    /// </summary>
    public static string Describe(BlockPosition position) =>
        $"at {position.X}, {position.Y}, {position.Z} in {For(position.Dimension)}";
}
