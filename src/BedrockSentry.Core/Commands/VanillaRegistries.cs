using System.Text.RegularExpressions;

namespace BedrockSentry.Core.Commands;

/// <summary>The block, item, entity and other names in Mojang's <c>@minecraft/vanilla-data</c>.</summary>
/// <remarks>
/// <para>Each <c>lib/mojang-*.d.ts</c> declares one TypeScript enum whose members are
/// the names, <c>Diamond = "minecraft:diamond"</c>. Only those assignments are read:
/// <c>mojang-block.d.ts</c> also names block states in single quotes,
/// <c>'minecraft:cardinal_direction'</c>, and they are not blocks.</para>
///
/// <para>Measured on release <c>1.26.52</c>: 1,463 blocks, 1,623 items, 129 entities,
/// 89 biomes, 37 effects, 42 enchantments and 7 camera presets.</para>
///
/// <para><strong><c>mojang-feature.d.ts</c> is not read.</strong> Its 25 names are
/// structure features, and BDS 1.26.52.3 refused each of the six tried as
/// <c>place feature</c>'s <c>features</c> while taking
/// <c>minecraft:oak_tree_feature</c>, which the file does not list. Offering them would
/// complete words the server answers with a syntax error.</para>
/// </remarks>
public static partial class VanillaRegistries
{
    /// <summary>The namespace every vanilla name carries.</summary>
    public const string Namespace = "minecraft:";

    [GeneratedRegex("=\\s*\"minecraft:(?<name>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex MemberPattern { get; }

    /// <summary>Which file fills which list, by the type name the help forms use.</summary>
    public static IReadOnlyDictionary<string, string> Files { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mojang-block.d.ts"] = "Block",
            ["mojang-item.d.ts"] = "Item",
            ["mojang-entity.d.ts"] = "EntityType",
            ["mojang-biome.d.ts"] = "Biome",
            ["mojang-effect.d.ts"] = "Effect",
            ["mojang-enchantment.d.ts"] = "Enchant",
            ["mojang-cameraPresets.d.ts"] = "CameraPreset",
        };

    /// <summary>The names one declaration file assigns, without the namespace, sorted.</summary>
    public static IReadOnlyList<string> Names(string declarations) =>
        [.. MemberPattern.Matches(declarations ?? string.Empty)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>Every list the files fill, keyed by type name.</summary>
    /// <param name="files">Each file's name, without its folder, and its text.</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Read(
        IEnumerable<(string FileName, string Text)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var lists = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (fileName, text) in files)
        {
            if (Files.TryGetValue(fileName, out var type))
            {
                lists[type] = Names(text);
            }
        }

        return lists;
    }
}
