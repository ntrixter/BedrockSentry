using System.Text.Json;
using System.Text.Json.Serialization;

namespace BedrockSentry.Core.Commands;

/// <summary>The blocks, items, entities and functions an add-on's files declare.</summary>
public sealed record AddonIds(
    IReadOnlyList<string> Blocks,
    IReadOnlyList<string> Items,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> Functions)
{
    /// <summary>Nothing declared.</summary>
    public static AddonIds None { get; } = new([], [], [], []);

    /// <summary>Everything declared, across the four lists.</summary>
    [JsonIgnore]
    public int Count => Blocks.Count + Items.Count + Entities.Count + Functions.Count;

    /// <summary>Both sets in one, each list sorted and without repeats.</summary>
    public AddonIds With(AddonIds other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new AddonIds(
            Merge(Blocks, other.Blocks), Merge(Items, other.Items),
            Merge(Entities, other.Entities), Merge(Functions, other.Functions));
    }

    private static IReadOnlyList<string> Merge(IEnumerable<string> a, IEnumerable<string> b) =>
        [.. a.Concat(b).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}

/// <summary>Reads what a behavior pack declares from its files, without running it.</summary>
/// <remarks>
/// <para>A pack's <c>entities/</c>, <c>items/</c> and <c>blocks/</c> JSON names each thing
/// at <c>minecraft:entity.description.identifier</c> and its siblings, and every
/// <c>functions/**/*.mcfunction</c> is a function named by its path:
/// <c>functions/example/reset_warps.mcfunction</c> is <c>example/reset_warps</c>. These
/// complete the moment the pack is switched on, before any server has loaded it.</para>
///
/// <para>A name in the <c>minecraft:</c> namespace is the pack changing a vanilla thing,
/// which Mojang's data already lists, so it is left out. A file that does not parse is
/// skipped: the server will say what it thinks of it, and this list is a convenience.</para>
/// </remarks>
public static class AddonDeclarations
{
    private static readonly JsonDocumentOptions Tolerant = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly (string Folder, string Component, int List)[] Kinds =
    [
        ("blocks", "minecraft:block", 0),
        ("items", "minecraft:item", 1),
        ("entities", "minecraft:entity", 2),
    ];

    private const string FunctionsFolder = "functions";
    private const string FunctionExtension = ".mcfunction";

    /// <summary>Whether a file, by its path in the pack, is one this reads.</summary>
    /// <param name="relativePath">Relative to the pack folder, separated by <c>/</c>.</param>
    public static bool Wants(string relativePath) =>
        KindOf(relativePath) is not null || FunctionName(relativePath) is not null;

    /// <summary>Everything the files declare.</summary>
    /// <param name="files">
    /// Each file's path relative to the pack folder, separated by <c>/</c>, and its text;
    /// null text for a function file, whose name is all that is read.
    /// </param>
    public static AddonIds Read(IEnumerable<(string RelativePath, string? Text)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var lists = new[] { new List<string>(), new List<string>(), new List<string>() };
        var functions = new List<string>();

        foreach (var (path, text) in files)
        {
            if (FunctionName(path) is { } function)
            {
                functions.Add(function);
                continue;
            }

            if (KindOf(path) is not { } kind || text is null || Identifier(text, kind.Component) is not { } id)
            {
                continue;
            }

            if (!id.StartsWith(VanillaRegistries.Namespace, StringComparison.OrdinalIgnoreCase))
            {
                lists[kind.List].Add(id);
            }
        }

        return new AddonIds(Sorted(lists[0]), Sorted(lists[1]), Sorted(lists[2]), Sorted(functions));
    }

    private static (string Folder, string Component, int List)? KindOf(string relativePath)
    {
        foreach (var kind in Kinds)
        {
            if (relativePath.StartsWith(kind.Folder + "/", StringComparison.OrdinalIgnoreCase)
                && relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return null;
    }

    private static string? FunctionName(string relativePath)
    {
        if (!relativePath.StartsWith(FunctionsFolder + "/", StringComparison.OrdinalIgnoreCase)
            || !relativePath.EndsWith(FunctionExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = relativePath[(FunctionsFolder.Length + 1)..^FunctionExtension.Length];
        return name.Length > 0 ? name : null;
    }

    private static string? Identifier(string text, string component)
    {
        try
        {
            using var document = JsonDocument.Parse(text, Tolerant);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(component, out var body)
                && body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("description", out var description)
                && description.ValueKind == JsonValueKind.Object
                && description.TryGetProperty("identifier", out var identifier)
                && identifier.ValueKind == JsonValueKind.String
                && identifier.GetString() is { Length: > 0 } id
                && !id.Contains(' ', StringComparison.Ordinal)
                    ? id
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Sorted(IEnumerable<string> names) =>
        [.. names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
