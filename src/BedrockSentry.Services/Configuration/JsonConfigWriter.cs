using System.Text.Json;
using System.Text.Json.Nodes;

namespace BedrockSentry.Services.Configuration;

/// <summary>
/// Merges values into a JSON configuration file without disturbing anything else in it.
/// </summary>
/// <remarks>
/// <para>
/// The obvious implementation — bind the file to a POCO, mutate it, serialise it
/// back — is wrong in two ways that only show up later. It writes out every
/// property at its default value, so settings the operator never touched become
/// explicit and stop tracking future default changes. And it silently discards any
/// key the POCO does not model, including comments-as-keys and hand-added settings
/// from a newer or older version of the wrapper.
/// </para>
/// <para>
/// So this walks the existing <see cref="JsonNode"/> tree and sets only the paths
/// it was asked to set.
/// </para>
/// </remarks>
public static class JsonConfigWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Sets each <paramref name="values"/> entry at its colon-delimited configuration
    /// path (e.g. <c>"Sentry:Admin:Username"</c>), creating intermediate objects as
    /// needed, and writes the result back atomically.
    /// </summary>
    public static async Task MergeAsync(
        string path,
        IReadOnlyDictionary<string, JsonNode?> values,
        CancellationToken ct = default)
    {
        var root = await ReadRootAsync(path, ct).ConfigureAwait(false);

        foreach (var (configPath, value) in values)
        {
            SetPath(root, configPath, value);
        }

        await WriteAtomicAsync(path, root, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the key at each colon-delimited path, leaving everything else in
    /// the file alone. A path that is not there is not an error.
    /// </summary>
    /// <remarks>
    /// Distinct from setting null, which writes <c>"Key": null</c> — a key that
    /// exists and binds to a default. Removing an instance has to actually take
    /// its entry out, or the next start reads it back and recreates it.
    /// </remarks>
    public static async Task RemoveAsync(
        string path,
        IEnumerable<string> configPaths,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var root = await ReadRootAsync(path, ct).ConfigureAwait(false);
        var removedAny = false;

        foreach (var configPath in configPaths)
        {
            removedAny |= RemovePath(root, configPath);
        }

        if (removedAny)
        {
            await WriteAtomicAsync(path, root, ct).ConfigureAwait(false);
        }
    }

    private static bool RemovePath(JsonObject root, string configPath)
    {
        var segments = configPath.Split(':', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            throw new ArgumentException("Configuration path must not be empty.", nameof(configPath));
        }

        var current = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            // A missing parent means the key was never there. Nothing to do, and
            // nothing to create on the way to not doing it.
            if (current[segments[i]] is not JsonObject next)
            {
                return false;
            }

            current = next;
        }

        return current.Remove(segments[^1]);
    }

    private static async Task<JsonObject> ReadRootAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        // A malformed file is not something to paper over by starting fresh — that
        // would silently discard whatever the operator was in the middle of editing.
        var node = JsonNode.Parse(text, documentOptions: ReadOptions)
            ?? throw new InvalidOperationException($"'{path}' does not contain a JSON object.");

        return node as JsonObject
            ?? throw new InvalidOperationException($"'{path}' must contain a JSON object at its root.");
    }

    private static void SetPath(JsonObject root, string configPath, JsonNode? value)
    {
        var segments = configPath.Split(':', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            throw new ArgumentException("Configuration path must not be empty.", nameof(configPath));
        }

        var current = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is JsonObject existing)
            {
                current = existing;
            }
            else
            {
                var created = new JsonObject();
                current[segments[i]] = created;
                current = created;
            }
        }

        current[segments[^1]] = value;
    }

    private static async Task WriteAtomicAsync(string path, JsonObject root, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException($"Cannot determine a directory for '{path}'.");

        Directory.CreateDirectory(directory);

        // Same directory as the target, so the move is a rename within one volume
        // rather than a copy that can fail halfway.
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(temp, root.ToJsonString(WriteOptions), ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Leaving a stray temp file behind is strictly better than masking the
            // original failure with a cleanup error.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
