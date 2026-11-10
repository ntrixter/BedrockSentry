using System.Text.Json;
using System.Text.Json.Nodes;
using BedrockSentry.Core.Addons;

namespace BedrockSentry.Core.Structures;

/// <summary>
/// The behaviour pack this wrapper writes structures into, so a server's own
/// <c>/structure load</c> can place them (<c>D86</c>, <c>D103</c>).
/// </summary>
/// <remarks>
/// <para><strong>Why a pack.</strong> Measured on BDS 1.26.52.3: a <c>.mcstructure</c>
/// under a world behaviour pack's <c>structures</c> folder loads by name, so the
/// wrapper never writes into a world's database. <c>structures/sentry/x.mcstructure</c>
/// loaded as <c>sentry:x</c>, so the folder under <c>structures</c> is the
/// namespace, and every file here is in <see cref="Namespace"/>.</para>
///
/// <para><strong>Read when the world loads, and only then.</strong> A file added while
/// the server ran was not found, nor after <c>reload</c> or <c>reload all</c>; after a
/// restart it loaded. So the pack is written while the server is stopped, which a
/// registration has to be anyway (<c>D85</c>).</para>
///
/// <para><strong>A structure saved in the world shadows a file of the same
/// name</strong> (measured), so nothing this wrapper places uses a name the world
/// itself saves under; <see cref="Namespace"/> is the wrapper's own.</para>
///
/// <para>The manifest is the shape measured to load: <c>format_version</c> 2, one
/// data module, versions as arrays, plus <c>metadata.product_type</c> <c>addon</c>
/// (<c>T428</c>). Its version never changes, because the pack's
/// contents are per world and per operation rather than released.</para>
/// </remarks>
public static class StructurePack
{
    /// <summary>The header uuid, which is the pack's identity (<c>D79</c>).</summary>
    public const string Uuid = "213876f6-e34c-444c-ae24-97848023329b";

    /// <summary>The data module's uuid.</summary>
    public const string ModuleUuid = "7c0a1b24-128f-4526-b9c8-035e2887526d";

    /// <summary>What the Add-ons tab calls it while a world holds it.</summary>
    public const string Name = "BedrockSentry structures";

    /// <summary>Its folder in a world's <c>behavior_packs</c>.</summary>
    public const string FolderName = "bedrocksentry_structures";

    /// <summary>The namespace every structure in the pack loads under.</summary>
    public const string Namespace = "bedrocksentry";

    /// <summary>The folder a behaviour pack keeps its structures in.</summary>
    public const string StructuresFolder = "structures";

    /// <summary>The pack's one version: <c>[1, 0, 0]</c>.</summary>
    public static PackVersion Version { get; } = PackVersion.Of([1, 0, 0])!.Value;

    /// <summary>Whether a uuid is this pack's, ignoring case as the library does.</summary>
    public static bool Is(string? uuid) =>
        string.Equals(uuid, Uuid, StringComparison.OrdinalIgnoreCase);

    /// <summary>The pack's <c>manifest.json</c>.</summary>
    public static string Manifest()
    {
        static JsonArray Array(PackVersion v) => [.. v.Components.Select(c => (JsonNode)c)];

        var manifest = new JsonObject
        {
            ["format_version"] = 2,
            ["header"] = new JsonObject
            {
                ["name"] = Name,
                ["description"] = "Structures BedrockSentry places with /structure load. "
                    + "BedrockSentry writes and removes this pack itself.",
                ["uuid"] = Uuid,
                ["version"] = Array(Version),
                ["min_engine_version"] = new JsonArray(1, 21, 0),
            },
            // Without it Bedrock shows an "Achievements disabled" warning for a
            // world holding the pack (T428).
            ["metadata"] = new JsonObject { ["product_type"] = "addon" },
            ["modules"] = new JsonArray(new JsonObject
            {
                ["type"] = "data",
                ["uuid"] = ModuleUuid,
                ["version"] = Array(Version),
            }),
        };

        return manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// The file a structure goes in, relative to the pack's folder, or null when
    /// the name cannot be one of this pack's: another namespace, or a part that is
    /// not <see cref="StructureName.IsPlain"/>.
    /// </summary>
    public static string? RelativePathFor(StructureName name) =>
        name.Namespace == Namespace && name.IsPlain
            ? Path.Combine(StructuresFolder, Namespace, name.Name + StructureFile.Extension)
            : null;
}
