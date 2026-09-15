using System.Text.Json;
using System.Text.Json.Nodes;

using BedrockSentry.Core.Configuration;

namespace BedrockSentry.Services.Instances;

/// <summary>The three Bedrock permission tiers, as the file spells them.</summary>
/// <remarks>
/// Strings rather than an enum end to end: an unrecognised value in somebody's
/// file must be displayed and preserved, not mapped to a default — see
/// <see cref="PermissionsFile"/> on why nothing here is typed tighter than the
/// file itself.
/// </remarks>
public static class PermissionTier
{
    public const string Visitor = "visitor";
    public const string Member = "member";
    public const string Operator = "operator";
}

/// <summary>One override in <c>permissions.json</c>.</summary>
/// <param name="Permission">A tier — <c>visitor</c>, <c>member</c> or <c>operator</c>.</param>
/// <param name="Xuid">The player, by the only identity Bedrock trusts here.</param>
public sealed record PermissionEntry(string Permission, string Xuid);

/// <summary>
/// Reads and edits a BDS <c>permissions.json</c> (<c>docs/design.md</c> § 7.5).
/// </summary>
/// <remarks>
/// This is an <em>override</em> list, not a roster. A player absent from it
/// gets <c>default-player-permission-level</c> from <c>server.properties</c> —
/// which is why <see cref="Remove"/> means "back to the default" and is a
/// different act from setting <see cref="PermissionTier.Member"/>. Callers that
/// render absence as "not an operator" are lying whenever the default says
/// otherwise; the UI reads the default alongside this file for exactly that
/// reason.
///
/// Node-based like <see cref="AllowlistFile"/>, and for the same reason: fields
/// this class does not understand must survive a round trip. The measured file
/// is pretty-printed with a three-space indent; the writer matches that. BDS
/// puts a space before each colon too, which <c>System.Text.Json</c> cannot,
/// and § 7.5 settles that chasing byte equality is not the job — BDS rewrites
/// the file itself on its next in-game change.
/// </remarks>
public sealed class PermissionsFile
{
    public const string FileName = "permissions.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentSize = 3,
        NewLine = "\r\n",
    };

    private readonly JsonArray _entries;

    private PermissionsFile(JsonArray entries) => _entries = entries;

    public static string PathFor(string instanceDirectory) =>
        Path.Combine(instanceDirectory, FileName);

    public static PermissionsFile Load(string path)
    {
        if (!File.Exists(path))
        {
            return new PermissionsFile([]);
        }

        var parsed = JsonNode.Parse(File.ReadAllText(path));

        return parsed is JsonArray entries
            ? new PermissionsFile(entries)
            : throw new JsonException($"{Path.GetFileName(path)} does not contain a JSON array.");
    }

    public IReadOnlyList<PermissionEntry> Entries =>
        _entries
            .OfType<JsonObject>()
            .Select(e => new PermissionEntry(
                e["permission"]?.GetValue<string>() ?? string.Empty,
                e["xuid"]?.GetValue<string>() ?? string.Empty))
            .Where(e => e.Xuid.Length > 0)
            .ToList();

    public string? TierOf(string xuid) =>
        Entries.FirstOrDefault(e => e.Xuid == xuid)?.Permission;

    /// <summary>Sets this player's tier, adding the override if it is new.</summary>
    public void Set(string xuid, string tier)
    {
        var entry = _entries
            .OfType<JsonObject>()
            .FirstOrDefault(e => e["xuid"]?.GetValue<string>() == xuid);

        if (entry is null)
        {
            // Key order matches the measured file: permission first.
            _entries.Add(new JsonObject
            {
                ["permission"] = tier,
                ["xuid"] = xuid,
            });
            return;
        }

        entry["permission"] = tier;
    }

    /// <summary>
    /// Drops the override, returning the player to whatever
    /// <c>default-player-permission-level</c> says. Not the same act as setting
    /// <see cref="PermissionTier.Member"/> — see the class remarks.
    /// </summary>
    public bool Remove(string xuid)
    {
        var doomed = _entries
            .OfType<JsonObject>()
            .FirstOrDefault(e => e["xuid"]?.GetValue<string>() == xuid);

        return doomed is not null && _entries.Remove(doomed);
    }

    public void Save(string path) =>
        AtomicFile.WriteAllText(path, _entries.ToJsonString(WriteOptions) + "\r\n");
}
