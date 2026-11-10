using System.Text.Json;

namespace BedrockSentry.Core.Addons;

/// <summary>Turns a pack's own files into a <see cref="PackManifest"/>.</summary>
/// <remarks>
/// <para>Pure: it takes text and returns a result, so every shape a real pack
/// can carry is an ordinary unit test rather than a folder somebody has to
/// build. The file reading is a seam above this.</para>
///
/// <para><strong>It never throws on somebody else's file.</strong> A library is
/// filled by dropping archives into a folder, so malformed input is the
/// ordinary case and not an exceptional one — every failure comes back as a
/// state with a reason to show.</para>
/// </remarks>
public static class PackManifestReader
{
    /// <summary>Read a <c>manifest.json</c>.</summary>
    public static PackManifestRead Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                // Both appear in packs people actually ship, and refusing either
                // would reject a pack the game itself loads.
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return PackManifestRead.Failed(PackManifestState.NotJson, ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return PackManifestRead.Failed(
                    PackManifestState.NotAPack, "The manifest's top level is not an object.");
            }

            if (PackJson.Field(root, "header") is not { ValueKind: JsonValueKind.Object } header)
            {
                return PackManifestRead.Failed(
                    PackManifestState.NotAPack, "The manifest has no header.");
            }

            // The uuid is the identity: the library groups by it, the world's
            // pack list names it, and a pack without one cannot be referred to
            // at all. Everything else below is allowed to be missing.
            if (PackJson.Text(header, "uuid") is not { } uuid)
            {
                return PackManifestRead.Failed(
                    PackManifestState.NotAPack, "The manifest's header has no uuid.");
            }

            return PackManifestRead.Of(new PackManifest(
                uuid,
                PackJson.Text(header, "name") ?? uuid,
                PackJson.Text(header, "description"),
                PackVersion.Read(PackJson.Field(header, "version")),
                PackVersion.Read(PackJson.Field(header, "min_engine_version")),
                PackJson.Integer(root, "format_version"),
                ModuleTypes(root),
                Dependencies(root),
                Authors(root)));
        }
    }

    /// <summary>
    /// Read a pack's <c>texts/en_US.lang</c> into the keys a manifest can override.
    /// </summary>
    /// <remarks>
    /// Bedrock's <c>.lang</c> is <c>key=value</c> a line at a time, with <c>##</c>
    /// starting a comment. The value keeps every character after the first
    /// <c>=</c>, because a description legitimately contains one and splitting
    /// on all of them truncates the sentence at the first.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ReadLanguage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var comment = line.IndexOf("##", StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line[..comment].TrimEnd();
            }

            var split = line.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            entries[line[..split].Trim()] = line[(split + 1)..].Trim();
        }

        return entries;
    }

    private static IReadOnlyList<string> ModuleTypes(JsonElement root)
    {
        if (PackJson.Field(root, "modules") is not { ValueKind: JsonValueKind.Array } modules)
        {
            return [];
        }

        return modules
            .EnumerateArray()
            .Where(m => m.ValueKind == JsonValueKind.Object)
            .Select(m => PackJson.Text(m, "type"))
            .OfType<string>()
            .ToList();
    }

    private static IReadOnlyList<PackDependency> Dependencies(JsonElement root)
    {
        if (PackJson.Field(root, "dependencies") is not { ValueKind: JsonValueKind.Array } dependencies)
        {
            return [];
        }

        return dependencies.EnumerateArray().Select(PackDependency.Read).ToList();
    }

    private static IReadOnlyList<string> Authors(JsonElement root)
    {
        // metadata.authors, not header.author. Reading the header here returns
        // nothing at all for every pack in the wild, which looks like a pack
        // that did not name its author rather than a reader looking in the
        // wrong place (`D81`).
        if (PackJson.Field(root, "metadata") is not { ValueKind: JsonValueKind.Object } metadata
            || PackJson.Field(metadata, "authors") is not { ValueKind: JsonValueKind.Array } authors)
        {
            return [];
        }

        return authors
            .EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.String)
            .Select(a => a.GetString())
            .OfType<string>()
            .Where(a => a.Length > 0)
            .ToList();
    }
}
