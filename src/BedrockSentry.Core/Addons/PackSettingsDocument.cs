using System.Text.Json;
using System.Text.Json.Nodes;

namespace BedrockSentry.Core.Addons;

/// <summary>A value an override may carry.</summary>
/// <remarks>
/// <para><strong>The writable shapes are the only ones this type can hold, and
/// that is the guard.</strong> Measured on BDS 1.26.51.1: an <em>array</em>
/// value is rejected and the rejection takes <strong>every pack's overrides in
/// that world</strong> with it — loud on stdout, invisible in the file, which
/// keeps its bad content. A manager writing one bad value silently
/// un-configures every add-on in the world. So rather than validating arrays
/// away, the write path cannot express one.</para>
///
/// <para><see cref="Unsupported"/> exists only from a <em>read</em>: a file may
/// already hold something this cannot write, and it has to be describable to be
/// reported. A document holding one refuses to be written.</para>
/// </remarks>
public abstract record PackSettingValue
{
    private PackSettingValue()
    {
    }

    /// <summary>A boolean, for a toggle.</summary>
    public sealed record Toggle(bool Value) : PackSettingValue;

    /// <summary>A number, for a slider.</summary>
    /// <remarks>
    /// <strong>A slider is a 32-bit float on the way back.</strong> Measured:
    /// <c>7.3</c> reaches a script as <c>7.300000190734863</c>, which is
    /// <c>(double)(float)7.3</c> — the widening happens before the value gets
    /// there. Nothing here rounds it, and a panel must not promise that what was
    /// written is what a script reads.
    /// </remarks>
    public sealed record Number(double Value) : PackSettingValue;

    /// <summary>A string, for a dropdown.</summary>
    public sealed record Text(string Value) : PackSettingValue;

    /// <summary>Something read from a file that this cannot write back.</summary>
    public sealed record Unsupported(string Raw) : PackSettingValue;
}

/// <summary>How reading a world's pack-settings file went.</summary>
public enum PackSettingsDocumentState
{
    Read,

    /// <summary>
    /// No file. Ordinary and the commonest case — it is created lazily and most
    /// worlds never have one.
    /// </summary>
    Absent,

    /// <summary>There and not readable as this shape. Never treated as empty.</summary>
    Malformed,

    /// <summary>
    /// There and it could not be opened — an ACL, a lock. Separate from
    /// <see cref="Malformed"/> because the remedies differ: a broken file can be
    /// rewritten and a denied one cannot, so collapsing them offers a repair
    /// that will fail for a second reason nobody has been told about.
    /// </summary>
    Unreadable,
}

/// <summary>One pack's overrides.</summary>
public sealed record PackSettingsEntry(
    string PackId, IReadOnlyDictionary<string, PackSettingValue> Values);

/// <summary>
/// A world's <c>world_behavior_pack_settings.json</c>.
/// </summary>
/// <remarks>
/// <para><strong>BDS reads this file and never writes it</strong> — measured
/// across 22 runs, including ones where it held a stale entry the server could
/// have pruned and one it rejected outright; its bytes were always still the
/// ones written by hand. So the manager is the only writer and there is nothing
/// to reconcile, which is the good news; the bad news is that nothing repairs a
/// mistake either.</para>
///
/// <para>Keyed by the pack's <strong>header</strong> uuid. Measured: a version
/// bump keeps a pack's stored values and a uuid change drops them, and the
/// orphaned entry for the old uuid is never cleaned up by the server.</para>
/// </remarks>
public sealed record PackSettingsDocument(
    string? FormatVersion, IReadOnlyList<PackSettingsEntry> Entries)
{
    private const string SettingsKey = "minecraft:pack_settings";

    /// <summary>
    /// The <c>format_version</c> a working file carries, for a caller creating
    /// one where there was none.
    /// </summary>
    /// <remarks>
    /// <strong>It is a version STRING, not a number</strong>, read from the
    /// documented shape of a file BDS accepted rather than recalled. A caller
    /// that already has a document echoes <see cref="FormatVersion"/> instead:
    /// this constant answers only the create-from-nothing case, and
    /// <see cref="Write"/> still takes the value rather than reaching for it,
    /// so a wrong one is a caller's decision rather than a default nobody chose.
    /// </remarks>
    public const string KnownFormatVersion = "1.21.100";

    /// <summary>One pack's overrides, or null when it has none.</summary>
    public PackSettingsEntry? For(string packId) =>
        Entries.FirstOrDefault(e => string.Equals(e.PackId, packId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Read a world's settings file.</summary>
    /// <param name="json">Its text, or null when there is no file.</param>
    public static PackSettingsDocumentRead Read(string? json)
    {
        if (json is null)
        {
            return new PackSettingsDocumentRead(
                PackSettingsDocumentState.Absent, new PackSettingsDocument(null, []), null);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return new PackSettingsDocumentRead(
                PackSettingsDocumentState.Malformed, new PackSettingsDocument(null, []), ex.Message);
        }

        if (root is not JsonObject document
            || document[SettingsKey] is not JsonObject block
            || block["settings"] is not JsonArray entries)
        {
            return new PackSettingsDocumentRead(
                PackSettingsDocumentState.Malformed,
                new PackSettingsDocument(null, []),
                $"It has no {SettingsKey} settings list.");
        }

        // A version STRING. Asking for an int here threw
        // InvalidOperationException — not JsonException, so not caught above —
        // on every real file, because that is not the shape one holds.
        // A present value of any other shape is refused rather than read: this
        // file cannot be written back without echoing the field, and a document
        // that reads cleanly and cannot be written is the refusal one layer too
        // late.
        string? format = null;

        if (document["format_version"] is { } version)
        {
            if (version is JsonValue value && value.TryGetValue<string>(out var text))
            {
                format = text;
            }
            else
            {
                return new PackSettingsDocumentRead(
                    PackSettingsDocumentState.Malformed,
                    new PackSettingsDocument(null, []),
                    "Its format_version is not a version string.");
            }
        }

        var read = new List<PackSettingsEntry>();

        foreach (var entry in entries)
        {
            if (entry is not JsonObject obj
                || obj["pack_id"]?.GetValue<string>() is not { Length: > 0 } packId)
            {
                continue;
            }

            var values = new Dictionary<string, PackSettingValue>(StringComparer.Ordinal);

            if (obj["values"] is JsonObject declared)
            {
                foreach (var (key, value) in declared)
                {
                    values[key] = ValueOf(value);
                }
            }

            read.Add(new PackSettingsEntry(packId, values));
        }

        return new PackSettingsDocumentRead(
            PackSettingsDocumentState.Read, new PackSettingsDocument(format, read), null);
    }

    /// <summary>Replace one pack's overrides, leaving every other pack's alone.</summary>
    public PackSettingsDocument With(
        string packId, IReadOnlyDictionary<string, PackSettingValue> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentNullException.ThrowIfNull(values);

        var others = Entries
            .Where(e => !string.Equals(e.PackId, packId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // An empty set of overrides removes the entry rather than writing an
        // empty one: the file holds only what differs from the manifest, so an
        // entry with nothing in it says nothing and is one more thing to parse.
        if (values.Count > 0)
        {
            others.Add(new PackSettingsEntry(packId, values));
        }

        return this with { Entries = others };
    }

    /// <summary>Drop one pack's overrides entirely.</summary>
    public PackSettingsDocument Without(string packId) =>
        With(packId, new Dictionary<string, PackSettingValue>());

    /// <summary>Serialise the whole file.</summary>
    /// <param name="formatVersion">
    /// The document's own when it had one, and <see cref="KnownFormatVersion"/>
    /// when creating the file from nothing. <strong>There is no default</strong>:
    /// a wrong <c>format_version</c> is rejected by the server, and a rejection
    /// discards every pack's overrides in the world — so a guessed value here
    /// would be the exact catastrophe this type is shaped to avoid. A caller
    /// supplies one it has a reason to believe.
    /// </param>
    public string Write(string formatVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatVersion);

        var settings = new JsonArray();

        foreach (var entry in Entries)
        {
            var values = new JsonObject();

            foreach (var (key, value) in entry.Values)
            {
                values[key] = value switch
                {
                    PackSettingValue.Toggle toggle => JsonValue.Create(toggle.Value),
                    PackSettingValue.Number number => JsonValue.Create(number.Value),
                    PackSettingValue.Text text => JsonValue.Create(text.Value),

                    // Unreachable through a validated write, and a throw rather
                    // than a skip: silently dropping an override is how somebody
                    // loses a setting they had, and this file is the only copy.
                    _ => throw new InvalidOperationException(
                        $"'{key}' holds a value that cannot be written. "
                        + "Validate the document before writing it."),
                };
            }

            settings.Add(new JsonObject
            {
                ["pack_id"] = entry.PackId,
                ["values"] = values,
            });
        }

        var document = new JsonObject
        {
            ["format_version"] = formatVersion,
            [SettingsKey] = new JsonObject { ["settings"] = settings },
        };

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static PackSettingValue ValueOf(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<bool>(out var flag) => new PackSettingValue.Toggle(flag),
        JsonValue value when value.TryGetValue<double>(out var number) => new PackSettingValue.Number(number),
        JsonValue value when value.TryGetValue<string>(out var text) => new PackSettingValue.Text(text),
        _ => new PackSettingValue.Unsupported(node?.ToJsonString() ?? "null"),
    };
}

/// <summary>What a read of a world's settings file returned.</summary>
public readonly record struct PackSettingsDocumentRead(
    PackSettingsDocumentState State, PackSettingsDocument Document, string? Reason);
