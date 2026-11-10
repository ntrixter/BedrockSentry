using System.Text.Json;

namespace BedrockSentry.Core.Addons;

/// <summary>One of the five kinds of control a pack may declare.</summary>
/// <remarks>
/// Five, not four. A table measured from two real packs had four and the schema
/// has five — <see cref="Multiselect"/> was the missing one, and it is where a
/// reader written against the other four drops a value silently, because its
/// default key is <c>defaults</c>, plural and an array.
/// </remarks>
public enum PackSettingKind
{
    /// <summary>Text on the panel. Not a control, and nothing can be written to it.</summary>
    Label,

    Toggle,
    Slider,
    Dropdown,
    Multiselect,
}

/// <summary>One choice in a dropdown or multiselect.</summary>
public sealed record PackSettingOption(string Name, string? Text);

/// <summary>One control a pack declares.</summary>
/// <param name="Kind">Which of the five.</param>
/// <param name="Name">
/// The namespaced key the script reads it by, and the key an override is written
/// under. Null only for <see cref="PackSettingKind.Label"/>, which has none.
/// </param>
public sealed record PackSetting(
    PackSettingKind Kind,
    string? Name,
    string? Text,
    bool? DefaultToggle,
    double? DefaultNumber,
    string? DefaultText,
    IReadOnlyList<string> DefaultSelection,
    double? Min,
    double? Max,
    double? Step,
    IReadOnlyList<PackSettingOption> Options)
{
    /// <summary>Can a value be written for this at all?</summary>
    /// <remarks>
    /// A label is not a control, and <see cref="PackSettingKind.Multiselect"/>
    /// is refused for a measured reason rather than an aesthetic one — see
    /// <c>PackSettingsWrite</c>.
    /// </remarks>
    public bool IsWritable => Kind is not PackSettingKind.Label and not PackSettingKind.Multiselect;
}

/// <summary>Whether a pack has a gear.</summary>
public enum PackSettingsState
{
    /// <summary>It declares controls and they read.</summary>
    Read,

    /// <summary>It declares none. Ordinary — most packs have no gear.</summary>
    NoSettings,

    /// <summary>
    /// It declares something this cannot read as a settings array. No gear, and
    /// a reason — <strong>only a schema-valid array gets one at all</strong>,
    /// so half-rendering a malformed declaration would offer controls whose
    /// writes the game may not accept.
    /// </summary>
    Unreadable,
}

/// <summary>A pack's declared controls, or why there are none.</summary>
public readonly record struct PackSettingsSchema(
    PackSettingsState State, IReadOnlyList<PackSetting> Settings, string? Reason)
{
    /// <summary>Is there a gear to show?</summary>
    public bool HasGear => State == PackSettingsState.Read && Settings.Count > 0;

    /// <summary>How many settings a person can see a control for; labels are not counted.</summary>
    public int ControlCount => Settings.Count(s => s.Kind != PackSettingKind.Label);

    /// <summary>The control with this name, or null.</summary>
    public PackSetting? Find(string name) =>
        Settings.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
}

/// <summary>Reads a pack's <c>settings</c> array out of its manifest.</summary>
/// <remarks>
/// <para><strong>Tolerant on read and strict on write, deliberately.</strong>
/// The option shape here was not measured from a real pack carrying every kind,
/// so a shape this does not recognise yields no gear rather than a guess — while
/// what may be <em>written</em> is decided against the schema by
/// <c>PackSettingsWrite</c>, where being wrong is expensive.</para>
/// </remarks>
public static class PackSettingsReader
{
    /// <summary>The most controls a pack may declare.</summary>
    public const int MaxSettings = 50;

    /// <summary>Read the controls from a manifest's text.</summary>
    public static PackSettingsSchema Read(string manifestJson)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifestJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return new PackSettingsSchema(PackSettingsState.Unreadable, [], ex.Message);
        }

        using (document)
        {
            if (PackJson.Field(document.RootElement, "settings") is not { } declared
                || declared.ValueKind == JsonValueKind.Null)
            {
                return new PackSettingsSchema(PackSettingsState.NoSettings, [], null);
            }

            if (declared.ValueKind != JsonValueKind.Array)
            {
                return new PackSettingsSchema(
                    PackSettingsState.Unreadable, [], "Its settings are not a list.");
            }

            var settings = new List<PackSetting>();

            foreach (var item in declared.EnumerateArray())
            {
                if (Parse(item) is not { } setting)
                {
                    return new PackSettingsSchema(
                        PackSettingsState.Unreadable, [],
                        "One of its settings is written in a way this cannot read, "
                        + "so none of them are offered.");
                }

                settings.Add(setting);
            }

            if (settings.Count > MaxSettings)
            {
                return new PackSettingsSchema(
                    PackSettingsState.Unreadable, [],
                    $"It declares {settings.Count} settings and the format allows {MaxSettings}.");
            }

            // A duplicate name means two controls writing one key, so which of
            // them an override belongs to cannot be told. Refused rather than
            // resolved toward the first, which would silently make the second
            // control do nothing.
            var duplicate = settings
                .Where(s => s.Name is not null)
                .GroupBy(s => s.Name, StringComparer.Ordinal)
                .FirstOrDefault(g => g.Count() > 1);

            if (duplicate is not null)
            {
                return new PackSettingsSchema(
                    PackSettingsState.Unreadable, [],
                    $"Two of its settings are both named '{duplicate.Key}'.");
            }

            return settings.Count == 0
                ? new PackSettingsSchema(PackSettingsState.NoSettings, [], null)
                : new PackSettingsSchema(PackSettingsState.Read, settings, null);
        }
    }

    private static PackSetting? Parse(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var kind = PackJson.Text(item, "type")?.ToLowerInvariant() switch
        {
            "label" => PackSettingKind.Label,
            "toggle" => PackSettingKind.Toggle,
            "slider" => PackSettingKind.Slider,
            "dropdown" => PackSettingKind.Dropdown,
            "multiselect" => PackSettingKind.Multiselect,
            _ => (PackSettingKind?)null,
        };

        if (kind is not { } type)
        {
            return null;
        }

        var name = PackJson.Text(item, "name");
        var text = PackJson.Text(item, "text");

        // A label needs no name and everything else does: without one there is
        // no key to write an override under, so the control could be rendered
        // and never do anything.
        if (type != PackSettingKind.Label && name is null)
        {
            return null;
        }

        return new PackSetting(
            type,
            name,
            text,
            Boolean(item, "default"),
            Number(item, "default"),
            type is PackSettingKind.Dropdown ? PackJson.Text(item, "default") : null,

            // The plural key, and the one a reader written against the other
            // four kinds misses — leaving a multiselect with no defaults at all
            // and nothing to say so.
            type is PackSettingKind.Multiselect ? Strings(item, "defaults") : [],

            Number(item, "min"),
            Number(item, "max"),
            Number(item, "step"),
            Options(item));
    }

    private static bool? Boolean(JsonElement parent, string name) =>
        PackJson.Field(parent, name) is { } value && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static double? Number(JsonElement parent, string name) =>
        PackJson.Field(parent, name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetDouble(out var number)
            ? number
            : null;

    private static IReadOnlyList<string> Strings(JsonElement parent, string name) =>
        PackJson.Field(parent, name) is { ValueKind: JsonValueKind.Array } array
            ? [.. array.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => s.Length > 0)]
            : [];

    private static IReadOnlyList<PackSettingOption> Options(JsonElement parent)
    {
        if (PackJson.Field(parent, "options") is not { ValueKind: JsonValueKind.Array } options)
        {
            return [];
        }

        var parsed = new List<PackSettingOption>();

        foreach (var option in options.EnumerateArray())
        {
            // Both shapes accepted, because which one a real pack uses was not
            // measured and refusing the wrong guess costs a gear that works.
            if (option.ValueKind == JsonValueKind.String && option.GetString() is { Length: > 0 } plain)
            {
                parsed.Add(new PackSettingOption(plain, null));
            }
            else if (PackJson.Text(option, "name") is { } named)
            {
                parsed.Add(new PackSettingOption(named, PackJson.Text(option, "text")));
            }
        }

        return parsed;
    }
}
