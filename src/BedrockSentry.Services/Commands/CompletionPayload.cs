using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BedrockSentry.Core.Commands;

namespace BedrockSentry.Services.Commands;

/// <summary>What the console's script reads: one instance's completion data as JSON (<c>D105</c>).</summary>
/// <remarks>
/// <para><strong>The shape is the browser's, and terse on purpose</strong>: every name
/// Mojang lists goes to the page, 3,390 in its 1.26.52, and the whole
/// payload for BDS 1.26.52.3 is 123 KB, 18 KB compressed. So a form's words are
/// <c>{"k":"p","l":label,"t":type,"o":optional}</c> rather than spelled out.
/// <c>tests/js/console-completion.test.js</c> reads a payload written by this class, so a
/// change here that the script does not follow fails there.</para>
///
/// <para><strong>Names keep the spelling the server takes.</strong> Blocks, items and
/// entities go without their <c>minecraft:</c> namespace, which the server takes either
/// way; biomes keep it, because <c>locate biome</c> refused <c>plains</c> and took
/// <c>minecraft:plains</c> (BDS 1.26.52.3). The script treats a name with a colon as whole.</para>
/// </remarks>
public static class CompletionPayload
{
    /// <summary>The payload's own version, so a page left open over an update reloads.</summary>
    public const int Schema = 1;

    /// <summary>Where the console's script fetches it.</summary>
    public const string Route = "/console-completion/{instanceId}";

    /// <summary>The route for one instance.</summary>
    public static string PathFor(string instanceId) => "/console-completion/" + Uri.EscapeDataString(instanceId);

    /// <summary>The lists whose names the server takes only with their namespace.</summary>
    private static readonly HashSet<string> Namespaced = new(StringComparer.Ordinal) { "Biome" };

    private static readonly JsonWriterOptions Options = new()
    {
        // Command words are full of < > and &, and the payload is served as JSON, not
        // put into markup, so escaping them only makes it larger.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The JSON for <paramref name="view"/>.</summary>
    public static string Write(CompletionView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Options))
        {
            json.WriteStartObject();
            json.WriteNumber("schema", Schema);
            json.WriteString("tag", view.Tag);
            json.WriteString("bds", view.Commands.Grammar.BdsVersion);
            json.WriteString("vanillaData", view.Vanilla?.Version);

            json.WriteStartObject("commands");
            foreach (var command in view.Commands.Grammar.Commands)
            {
                WriteCommand(json, command, addon: false);
            }

            foreach (var command in view.AddonCommands)
            {
                WriteCommand(json, command, addon: true);
            }

            json.WriteEndObject();

            json.WriteStartObject("enums");
            foreach (var list in view.Commands.Grammar.Options)
            {
                WriteList(json, list.Type, list.Values);
            }

            var rules = GameRuleLists(view.Commands.Grammar);
            WriteList(json, "BoolGameRule", rules.Bool);
            WriteList(json, "IntGameRule", rules.Int);
            json.WriteEndObject();

            json.WriteStartObject("registries");
            foreach (var type in new[] { "Block", "Item", "EntityType", "Biome", "Effect", "Enchant" })
            {
                var names = Usable(type, view.Vanilla?.Of(type) ?? []);
                WriteList(json, type, Namespaced.Contains(type) ? [.. names.Select(n => VanillaRegistries.Namespace + n)] : names);
            }

            json.WriteEndObject();
            WriteList(json, "summonRefused", [.. Refused("summon").Order(StringComparer.Ordinal)]);

            WriteList(json, "cameraPresets", [.. (view.Vanilla?.Of("CameraPreset") ?? []).Select(n => VanillaRegistries.Namespace + n)]);
            WriteList(json, "structures", StructureNames.All);

            json.WriteStartObject("addon");
            WriteList(json, "names", view.Addons.Packs);
            json.WriteBoolean("loaded", view.AddonsLoaded);
            json.WriteBoolean("read", view.Addons.ReadUtc is not null);
            json.WriteStartObject("ids");
            WriteList(json, "Block", view.Addons.Ids.Blocks);
            WriteList(json, "Item", view.Addons.Ids.Items);
            WriteList(json, "EntityType", view.Addons.Ids.Entities);
            json.WriteEndObject();
            WriteList(json, "functions", view.Addons.Ids.Functions);
            json.WriteEndObject();

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The gamerules as the forms take them: lowercase, and without the rules that have
    /// forms of their own, such as <c>gamerule playerwaypoints &lt;value&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The server lists them camelCased (<c>doDayLightCycle</c>) and takes either case;
    /// lowercase is how the game's own completion shows them.
    /// </remarks>
    public static (IReadOnlyList<string> Bool, IReadOnlyList<string> Int) GameRuleLists(CommandGrammar grammar)
    {
        ArgumentNullException.ThrowIfNull(grammar);

        var ownForms = new HashSet<string>(
            grammar.Commands.Where(c => c.Name == "gamerule")
                .SelectMany(c => c.Forms)
                .Where(f => f.Params.Count > 0 && f.Params[0].Kind is ParamKind.Literal)
                .Select(f => f.Params[0].Word!),
            StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<string> Of(GameRuleKind kind) =>
            [.. grammar.GameRules
                .Where(r => r.Kind == kind && !ownForms.Contains(r.Name))
                .Select(r => r.Name.ToLowerInvariant())
                .Order(StringComparer.Ordinal)];

        return (Of(GameRuleKind.Boolean), Of(GameRuleKind.Integer));
    }

    // Mojang's names less the ones the server refused where the forms take them.
    private static IReadOnlyList<string> Usable(string type, IReadOnlyList<string> names)
    {
        var refused = Refused(type);
        return refused.Count == 0 ? names : [.. names.Where(n => !refused.Contains(n))];
    }

    private static IReadOnlySet<string> Refused(string section) =>
        ShippedGrammar.Refusals.TryGetValue(section, out var names) ? names : new HashSet<string>();

    private static void WriteCommand(Utf8JsonWriter json, GrammarCommand command, bool addon)
    {
        Entry(command.Name, alias: null);
        foreach (var alias in command.Aliases)
        {
            Entry(alias, command.Name);
        }

        void Entry(string name, string? alias)
        {
            json.WriteStartObject(name);
            json.WriteStartArray("o");
            foreach (var form in command.Forms)
            {
                json.WriteStartArray();
                foreach (var word in form.Params)
                {
                    WriteParam(json, word);
                }

                json.WriteEndArray();
            }

            json.WriteEndArray();
            json.WriteString("d", command.Description);
            if (alias is not null)
            {
                json.WriteString("a", alias);
            }

            if (addon)
            {
                json.WriteBoolean("addon", true);
            }

            json.WriteEndObject();
        }
    }

    private static void WriteParam(Utf8JsonWriter json, CommandParam word)
    {
        json.WriteStartObject();
        switch (word.Kind)
        {
            case ParamKind.Literal:
                json.WriteString("k", "lit");
                json.WriteString("t", word.Word);
                break;

            case ParamKind.Choice:
                json.WriteString("k", "alt");
                WriteList(json, "v", word.Choices ?? []);
                json.WriteBoolean("o", word.Optional);
                break;

            default:
                json.WriteString("k", "p");
                json.WriteString("l", word.Label);
                json.WriteString("t", word.Type);
                json.WriteBoolean("o", word.Optional);
                if (word.Suffix is { } suffix)
                {
                    json.WriteString("s", suffix);
                }

                break;
        }

        json.WriteEndObject();
    }

    private static void WriteList(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
    }
}
