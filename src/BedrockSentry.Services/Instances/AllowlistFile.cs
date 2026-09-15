using System.Text.Json;
using System.Text.Json.Nodes;

using BedrockSentry.Core.Configuration;

namespace BedrockSentry.Services.Instances;

/// <summary>One allowlist entry, as far as the wrapper understands it.</summary>
/// <param name="Name">The gamertag. The key Bedrock actually uses.</param>
/// <param name="IgnoresPlayerLimit">May join a full server.</param>
/// <param name="Xuid">
/// Absent until this player first connects — Bedrock fills it in, not us. Which
/// is why the allowlist can name somebody the wrapper holds no profile for, and
/// why <c>D31</c> settled that this file, not a table, is the source of truth.
/// </param>
public sealed record AllowlistEntry(string Name, bool IgnoresPlayerLimit, string? Xuid);

/// <summary>
/// Reads and edits a BDS <c>allowlist.json</c> (<c>docs/design.md</c> § 7.5).
/// </summary>
/// <remarks>
/// Node-based rather than typed on purpose: entries carry fields this class was
/// not written for (<c>xuid</c> arrives only after a first join, and future BDS
/// versions may add more), and a typed round trip would silently drop them —
/// the exact failure this file's measurements were taken to prevent. Editing
/// mutates the nodes it read, so everything not understood survives untouched.
///
/// The measured file is minified onto a single CRLF-terminated line, and the
/// writer keeps that shape. Byte-for-byte fidelity is not the goal — BDS
/// rewrites this file itself whenever the list changes in-game — but writing
/// the shape BDS writes means a diff between our edit and its next rewrite
/// shows the change, not a reformat.
/// </remarks>
public sealed class AllowlistFile
{
    public const string FileName = "allowlist.json";

    private readonly JsonArray _entries;

    private AllowlistFile(JsonArray entries) => _entries = entries;

    public static string PathFor(string instanceDirectory) =>
        Path.Combine(instanceDirectory, FileName);

    /// <summary>
    /// Loads the file. Missing means an empty list — a server that has never
    /// had an allowlist edit may not have written one yet. Malformed throws:
    /// guessing at a broken access-control file is worse than saying so.
    /// </summary>
    public static AllowlistFile Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AllowlistFile([]);
        }

        var parsed = JsonNode.Parse(File.ReadAllText(path));

        return parsed is JsonArray entries
            ? new AllowlistFile(entries)
            : throw new JsonException($"{Path.GetFileName(path)} does not contain a JSON array.");
    }

    public IReadOnlyList<AllowlistEntry> Entries =>
        _entries
            .OfType<JsonObject>()
            .Select(e => new AllowlistEntry(
                e["name"]?.GetValue<string>() ?? string.Empty,
                e["ignoresPlayerLimit"]?.GetValue<bool>() ?? false,
                e["xuid"]?.GetValue<string>()))
            .Where(e => e.Name.Length > 0)
            .ToList();

    /// <summary>
    /// Case-insensitive, because gamertags are: the same person must not end up
    /// listed twice as <c>Steve</c> and <c>steve</c>.
    /// </summary>
    public bool Contains(string name) =>
        Entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether <paramref name="name"/>'s entry may join a full server. False for
    /// a name that is not listed at all, which is the same answer for the same
    /// reason: no entry, no exemption.
    /// </summary>
    /// <remarks>
    /// Exists so a caller can read the flag <em>before</em> removing the entry
    /// that carries it (`T153`). Case-insensitive, matching <see cref="Contains"/>.
    /// </remarks>
    public bool IgnoresPlayerLimit(string name) =>
        Entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
            && e.IgnoresPlayerLimit);

    /// <summary>False when already present — adding twice is not an edit.</summary>
    public bool Add(string name, bool ignoresPlayerLimit = false)
    {
        if (Contains(name))
        {
            return false;
        }

        // Key order matches the measured file (ignoresPlayerLimit first), so an
        // entry we write looks like an entry BDS wrote.
        _entries.Add(new JsonObject
        {
            ["ignoresPlayerLimit"] = ignoresPlayerLimit,
            ["name"] = name,
        });

        return true;
    }

    public bool Remove(string name)
    {
        var doomed = _entries
            .OfType<JsonObject>()
            .FirstOrDefault(e => string.Equals(
                e["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));

        return doomed is not null && _entries.Remove(doomed);
    }

    /// <summary>Only meaningful while the server is stopped — see § 7.6.</summary>
    public bool SetIgnoresPlayerLimit(string name, bool value)
    {
        var entry = _entries
            .OfType<JsonObject>()
            .FirstOrDefault(e => string.Equals(
                e["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return false;
        }

        entry["ignoresPlayerLimit"] = value;
        return true;
    }

    public void Save(string path) =>
        // Write-then-move, like every other file the wrapper owns mid-write, so
        // a crash leaves a droppable temp beside an intact allowlist rather than
        // half of one.
        AtomicFile.WriteAllText(path, _entries.ToJsonString() + "\r\n");
}
