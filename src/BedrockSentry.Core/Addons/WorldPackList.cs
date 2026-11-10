using System.Text.Json;
using System.Text.Json.Nodes;

namespace BedrockSentry.Core.Addons;

/// <summary>How reading a world's pack list went.</summary>
public enum WorldPackListState
{
    /// <summary>Read.</summary>
    Read,

    /// <summary>
    /// No such file. A world with no packs has none, so this is ordinary and
    /// yields an empty list rather than a fault.
    /// </summary>
    Absent,

    /// <summary>
    /// There and not JSON. <strong>Never quietly empty</strong>: writing a fresh
    /// list over a file nobody could parse would delete every registration in
    /// it, which is the loudest possible version of losing somebody's data
    /// while reporting success.
    /// </summary>
    Malformed,

    /// <summary>There and unreadable — an ACL, a lock. Also never empty.</summary>
    Unreadable,
}

/// <summary>One registration in a world's pack list.</summary>
/// <param name="PackId">The pack's header uuid, as written.</param>
/// <param name="Version">
/// The version, in whichever spelling the file used. The registration matches
/// on <strong>uuid AND version</strong>, measured on BDS 1.26.51.1 — a pack
/// whose manifest moved on without this moving with it is reported by the
/// server as <em>not found</em>, identically to one that is genuinely absent.
/// </param>
/// <param name="Raw">
/// The entry exactly as it was read. <strong>This is what makes the writer
/// safe.</strong> A list may hold keys this version knows nothing about, and an
/// entry rewritten from only the fields understood here would silently drop
/// them from a file the wrapper does not own.
/// </param>
public sealed record WorldPackEntry(string? PackId, PackVersionRead Version, JsonNode Raw)
{
    /// <summary>Could this entry be understood well enough to act on?</summary>
    public bool IsUnderstood => PackId is { Length: > 0 };
}

/// <summary>A world's pack list, read and written without losing what it held.</summary>
/// <remarks>
/// <para><strong>Order is meaning.</strong> Measured on BDS 1.26.51.1 with two
/// packs defining one <c>.mcfunction</c>, swapped, each carrying a unique
/// function as a control that both really loaded: <strong>the entry at index 0
/// wins</strong>, and the server's own Pack Stack index order is the file
/// order. Alphabetical, uuid order and on-disk order were each excluded
/// separately. So this type preserves order exactly and a reorder moves the
/// winner toward the front.</para>
///
/// <para><strong>Every operation is pure.</strong> The file is the world's, a
/// server may be running beside it, and BDS rewrites a world's files on
/// shutdown — so deciding what the list should say and writing it are separate
/// acts, and only the first one lives here.</para>
/// </remarks>
public sealed record WorldPackList(IReadOnlyList<WorldPackEntry> Entries)
{
    /// <summary>An empty list, which is what a world with no packs has.</summary>
    public static WorldPackList Empty { get; } = new([]);

    /// <summary>Read a world's pack list.</summary>
    /// <param name="json">The file's text, or null when there is no file.</param>
    public static WorldPackListRead Read(string? json)
    {
        if (json is null)
        {
            return new WorldPackListRead(WorldPackListState.Absent, Empty, null);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return new WorldPackListRead(WorldPackListState.Malformed, Empty, ex.Message);
        }

        if (node is not JsonArray array)
        {
            return new WorldPackListRead(
                WorldPackListState.Malformed, Empty, "The pack list is not a JSON array.");
        }

        var entries = new List<WorldPackEntry>(array.Count);

        foreach (var item in array)
        {
            // An entry this cannot understand is KEPT rather than dropped. It is
            // somebody else's registration and the server may well resolve it.
            if (item is null)
            {
                continue;
            }

            var packId = item is JsonObject obj && obj.TryGetPropertyValue("pack_id", out var id)
                ? id?.GetValue<string>()
                : null;

            // The null check is explicit rather than left to Deserialize's
            // tolerance of a null receiver. A key written `"version": null`
            // yields a null node, and this reading correctly today depends on an
            // extension method accepting one — which a later tidy to `v!` or to
            // a different API would take away, throwing on a hand-edited file
            // and taking the whole tab down over one entry.
            var version = item is JsonObject o && o.TryGetPropertyValue("version", out var v)
                && v is not null
                    ? PackVersion.Read(v.Deserialize<JsonElement>())
                    : PackVersionRead.OfAbsent();

            entries.Add(new WorldPackEntry(packId, version, item.DeepClone()));
        }

        return new WorldPackListRead(WorldPackListState.Read, new WorldPackList(entries), null);
    }

    /// <summary>Serialise the list back, keeping every entry's own shape.</summary>
    public string Write()
    {
        var array = new JsonArray();

        foreach (var entry in Entries)
        {
            array.Add(entry.Raw.DeepClone());
        }

        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Is this uuid registered, at any version?</summary>
    public WorldPackEntry? EntryFor(string packId) =>
        Entries.FirstOrDefault(e =>
            e.IsUnderstood && string.Equals(e.PackId, packId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Register a pack, or say why not.</summary>
    /// <remarks>
    /// A second version of one uuid is <strong>refused</strong> rather than
    /// warned about (<c>D79</c>): BDS resolves a uuid to one pack, so the second
    /// entry is not an addition but an ambiguity, and which of them the server
    /// picks is not this wrapper's to promise.
    /// </remarks>
    public WorldPackChange Enable(string packId, PackVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);

        if (EntryFor(packId) is { } existing)
        {
            return existing.Version.Version == version
                ? WorldPackChange.NoChange(this, WorldPackChangeKind.AlreadyEnabled)
                : WorldPackChange.Refused(this, existing.Version);
        }

        // Appended, so an existing entry keeps the precedence it had. Index 0
        // wins, and quietly promoting a new pack over one already in place would
        // change which of them takes effect.
        var entry = new WorldPackEntry(packId, PackVersionRead.Of(version), NodeFor(packId, version));

        return WorldPackChange.Changed(
            new WorldPackList([.. Entries, entry]), WorldPackChangeKind.Enabled);
    }

    /// <summary>Remove a pack's registration.</summary>
    public WorldPackChange Disable(string packId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);

        if (EntryFor(packId) is null)
        {
            return WorldPackChange.NoChange(this, WorldPackChangeKind.NotEnabled);
        }

        var kept = Entries
            .Where(e => !(e.IsUnderstood
                && string.Equals(e.PackId, packId, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return WorldPackChange.Changed(new WorldPackList(kept), WorldPackChangeKind.Disabled);
    }

    /// <summary>
    /// Point an existing registration at a different version of the same pack.
    /// </summary>
    /// <remarks>
    /// <strong>This is the second of the two writes an update takes.</strong>
    /// Replacing a pack's files without this leaves the world asking for a
    /// version that is no longer there, and BDS reports that with the same
    /// <em>not found</em> sentence a missing pack gets — so the pack vanishes
    /// and the message points at the wrong cause.
    /// </remarks>
    public WorldPackChange SetVersion(string packId, PackVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);

        if (EntryFor(packId) is not { } existing)
        {
            return WorldPackChange.NoChange(this, WorldPackChangeKind.NotEnabled);
        }

        if (existing.Version.Version == version)
        {
            return WorldPackChange.NoChange(this, WorldPackChangeKind.AlreadyEnabled);
        }

        // The entry's own node is edited rather than rebuilt, so any key this
        // version does not know about survives the update.
        var node = existing.Raw.DeepClone();
        node["version"] = VersionNode(version);

        var replaced = Entries
            .Select(e => ReferenceEquals(e, existing)
                ? new WorldPackEntry(packId, PackVersionRead.Of(version), node)
                : e)
            .ToList();

        return WorldPackChange.Changed(new WorldPackList(replaced), WorldPackChangeKind.VersionChanged);
    }

    /// <summary>Move a registration to a new position, 0 being the one that wins.</summary>
    public WorldPackChange MoveTo(string packId, int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        if (EntryFor(packId) is not { } existing)
        {
            return WorldPackChange.NoChange(this, WorldPackChangeKind.NotEnabled);
        }

        var moved = Entries.Where(e => !ReferenceEquals(e, existing)).ToList();
        moved.Insert(Math.Min(index, moved.Count), existing);

        return WorldPackChange.Changed(new WorldPackList(moved), WorldPackChangeKind.Reordered);
    }

    /// <summary>
    /// Puts the packs <paramref name="order"/> names in that order, in the places they hold
    /// now (<c>D85</c>): every other entry, understood or not, stays where it is.
    /// </summary>
    /// <remarks>
    /// A pack <paramref name="order"/> names that this list does not hold, or holds twice, is
    /// left out of it, since there is no one place to put it. Already in that order is
    /// <see cref="WorldPackChangeKind.AlreadyEnabled"/>, nothing to write.
    /// </remarks>
    public WorldPackChange Arrange(IReadOnlyList<string> order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var once = Entries
            .Where(e => e.IsUnderstood)
            .GroupBy(e => e.PackId!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var wanted = order.Where(once.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var slots = Entries
            .Select((e, i) => (e, i))
            .Where(x => x.e.PackId is { } id && wanted.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Select(x => x.i)
            .ToList();

        var arranged = Entries.ToList();

        for (var k = 0; k < slots.Count; k++)
        {
            arranged[slots[k]] = EntryFor(wanted[k])!;
        }

        return arranged.SequenceEqual(Entries, ReferenceEqualityComparer.Instance)
            ? WorldPackChange.NoChange(this, WorldPackChangeKind.AlreadyEnabled)
            : WorldPackChange.Changed(new WorldPackList(arranged), WorldPackChangeKind.Reordered);
    }

    private static JsonNode NodeFor(string packId, PackVersion version) => new JsonObject
    {
        ["pack_id"] = packId,
        ["version"] = VersionNode(version),
    };

    /// <summary>
    /// Always the array (<c>D84</c>), never the string a modern manifest uses.
    /// </summary>
    /// <remarks>
    /// The two spellings are both read, and mirroring each pack's own would put
    /// two shapes in one list — so if either turns out to be wrong some entries
    /// load and some do not, which is far harder to see than all of them failing.
    /// The components are written exactly as they were read, because the
    /// registration matches on the version and a tidied one is a different key.
    /// </remarks>
    private static JsonArray VersionNode(PackVersion version)
    {
        var array = new JsonArray();

        foreach (var component in version.Components)
        {
            array.Add(component);
        }

        return array;
    }
}

/// <summary>What a read of a world's pack list returned.</summary>
public readonly record struct WorldPackListRead(
    WorldPackListState State, WorldPackList List, string? Reason);

/// <summary>What an attempted change did.</summary>
public enum WorldPackChangeKind
{
    Enabled,
    Disabled,
    VersionChanged,
    Reordered,

    /// <summary>Asked for what is already true. Nothing to write.</summary>
    AlreadyEnabled,

    /// <summary>Asked to change a pack this world does not register.</summary>
    NotEnabled,

    /// <summary>
    /// This uuid is already registered at another version. Refused rather than
    /// added, because a uuid resolves to one pack and two entries is an
    /// ambiguity rather than a choice (<c>D79</c>).
    /// </summary>
    RefusedAlreadyAtAnotherVersion,
}

/// <summary>The outcome of a change, and the list it produced.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="List">The list afterwards — unchanged when nothing happened.</param>
/// <param name="Conflict">
/// On a refusal, the version already registered, so the refusal can name it
/// rather than saying only that something was in the way.
/// </param>
public readonly record struct WorldPackChange(
    WorldPackChangeKind Kind, WorldPackList List, PackVersionRead? Conflict)
{
    /// <summary>Is there anything to write?</summary>
    public bool Writes => Kind
        is WorldPackChangeKind.Enabled
        or WorldPackChangeKind.Disabled
        or WorldPackChangeKind.VersionChanged
        or WorldPackChangeKind.Reordered;

    internal static WorldPackChange Changed(WorldPackList list, WorldPackChangeKind kind) =>
        new(kind, list, null);

    internal static WorldPackChange NoChange(WorldPackList list, WorldPackChangeKind kind) =>
        new(kind, list, null);

    internal static WorldPackChange Refused(WorldPackList list, PackVersionRead conflict) =>
        new(WorldPackChangeKind.RefusedAlreadyAtAnotherVersion, list, conflict);
}
