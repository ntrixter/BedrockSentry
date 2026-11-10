using System.Text.Json;
using System.Text.Json.Nodes;

namespace BedrockSentry.Core.Addons;

/// <summary>What removing one pack from a world's history file came to.</summary>
/// <param name="Understood">
/// The file has the shape this reads: an object whose <c>packs</c> array holds
/// objects with a <c>uuid</c>. False means nothing here can safely rewrite it.
/// </param>
/// <param name="Json">The file's new text, or null when nothing changed.</param>
/// <param name="Reason">Why it was not understood.</param>
public readonly record struct PackHistoryEdit(bool Understood, string? Json, string? Reason)
{
    /// <summary>Whether the file needs writing.</summary>
    public bool Changed => Json is not null;
}

/// <summary>
/// A world's <c>world_behavior_pack_history.json</c> or
/// <c>world_resource_pack_history.json</c> (<c>D83</c>).
/// </summary>
/// <remarks>
/// <para>BDS never writes either (measured for <c>D83</c>). A world made in the game
/// and copied to a server can carry them, and a pack removed from such a world while
/// its history entry stays shows in the game as a pack that is missing.</para>
///
/// <para>The shape is the game's: <c>{"packs": [{"uuid": …, "version": […]}, …]}</c>.
/// Everything this does not recognise is refused rather than rewritten, because a
/// removal that half-applies is worse than one that does not happen (<c>D83</c>).</para>
/// </remarks>
public static class PackHistoryFile
{
    /// <summary>The behaviour packs' history file.</summary>
    public const string BehaviorFileName = "world_behavior_pack_history.json";

    /// <summary>The resource packs' history file.</summary>
    public const string ResourceFileName = "world_resource_pack_history.json";

    /// <summary>Both, in the order they are handled.</summary>
    public static IReadOnlyList<string> FileNames { get; } = [BehaviorFileName, ResourceFileName];

    /// <summary>The file's text with every entry for one pack removed.</summary>
    /// <param name="json">The file's text.</param>
    /// <param name="uuid">The pack's uuid, matched ignoring case.</param>
    public static PackHistoryEdit Without(string json, string uuid)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return new PackHistoryEdit(false, null, ex.Message);
        }

        if (root is not JsonObject document)
        {
            return new PackHistoryEdit(false, null, "it is not a JSON object");
        }

        if (!document.TryGetPropertyValue("packs", out var packsNode) || packsNode is null)
        {
            return new PackHistoryEdit(true, null, null);
        }

        if (packsNode is not JsonArray packs)
        {
            return new PackHistoryEdit(false, null, "its packs entry is not a list");
        }

        var doomed = packs
            .Where(p => p is JsonObject entry
                        && entry["uuid"] is JsonValue value
                        && value.TryGetValue<string>(out var id)
                        && string.Equals(id, uuid, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (doomed.Count == 0)
        {
            return new PackHistoryEdit(true, null, null);
        }

        foreach (var entry in doomed)
        {
            packs.Remove(entry);
        }

        return new PackHistoryEdit(
            true, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), null);
    }
}
