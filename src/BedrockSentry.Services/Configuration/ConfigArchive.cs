using System.Text.Json;
using System.Text.Json.Nodes;
using BedrockSentry.Core;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Configuration;

/// <summary>What an import changed.</summary>
/// <param name="Sections">Top-level sections written, in the order they were written.</param>
/// <param name="Instances">Instance ids the file carried.</param>
/// <param name="NewInstances">
/// Those that were not already configured here. The distinction matters because
/// only these need a service restart to appear.
/// </param>
public sealed record ConfigImportResult(
    IReadOnlyList<string> Sections,
    IReadOnlyList<string> Instances,
    IReadOnlyList<string> NewInstances)
{
    public bool NeedsRestart => Instances.Count > 0;
}

/// <summary>
/// Export the wrapper's own settings to JSON and import them back
/// (<c>docs/design.md</c> § 13).
/// </summary>
/// <remarks>
/// The same shape as the player export (§ 7.4) pointed at configuration instead:
/// a file somebody can read, keep, diff, hand-edit and put back. It is the
/// answer to "I am moving this to a new box" and to "I broke something, what did
/// it used to say".
///
/// <b>It works on the configuration file's own JSON, not on the options
/// objects.</b> Binding to POCOs and serialising them back would write out every
/// property at its default — turning settings nobody chose into explicit ones
/// that stop tracking future defaults — and would silently drop every key the
/// POCOs do not model, including hand-added ones and settings from a newer
/// wrapper. That is the same reasoning <see cref="JsonConfigWriter"/> exists for,
/// and it applies at least as strongly to a file whose whole purpose is to be
/// complete.
/// </remarks>
public sealed class ConfigArchive
{
    /// <summary>Bumped when the file's shape changes in a way an old reader would misread.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// The sections that travel, named rather than excluded.
    /// </summary>
    /// <remarks>
    /// <b>An allow-list, deliberately.</b> A deny-list is one section away from
    /// leaking: somebody adds <c>Sentry:SomeApi</c> with a token in it, nobody
    /// remembers this file, and it ends up in an export people email around.
    /// With an allow-list a new section is simply absent until somebody decides
    /// it belongs, which is the failure that costs an afternoon rather than an
    /// incident.
    ///
    /// Two sections are absent on purpose:
    /// <list type="bullet">
    /// <item><c>Sentry:Admin</c> — the credentials. § 13 excludes them even
    /// hashed, so a restore re-sets the login rather than carrying it over. A
    /// hash is not a password, but it is offline-attackable, and this file is
    /// meant to be copied around.</item>
    /// <item><c>Sentry:Tls</c> — it holds <c>PfxPassword</c> in plain text, and
    /// the rest of it (a certificate path, a port) describes this machine rather
    /// than this installation. Carrying the section without the password would
    /// be worse than not carrying it: importing would replace a working
    /// certificate configuration with one missing the password to open it.</item>
    /// </list>
    /// </remarks>
    public static readonly string[] Sections =
    [
        InstanceOptions.SectionName,
        BdsUpdateOptions.SectionName,
        UpdateOptions.SectionName,
        HostDetailsOptions.SectionName,

        // Machine-specific paths, like the per-instance WorkingDirectory the
        // Instances section already carries. A file imported onto a box whose
        // volumes differ points these somewhere that does not exist; that fails
        // loudly at the next backup or download, not silently, and matches how
        // instance folders behave under the same import.
        Core.Configuration.StorageOptions.SectionName,
    ];

    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true };

    private static readonly JsonDocumentOptions Read = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ISentryPaths _paths;
    private readonly ILogger<ConfigArchive> _log;

    public ConfigArchive(ISentryPaths paths, ILogger<ConfigArchive> log)
    {
        _paths = paths;
        _log = log;
    }

    private string ConfigPath => Path.Combine(_paths.Config, "bedrocksentry.json");

    /// <summary>Everything in <see cref="Sections"/>, as it is on disk.</summary>
    public async Task<string> ExportAsync(
        DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        var root = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);

        var carried = new JsonObject();

        foreach (var section in Sections)
        {
            if (Find(root, section) is { } node)
            {
                // Deep-cloned because a node belongs to one parent, and this one
                // is still attached to the tree it was read from.
                carried[section] = node.DeepClone();
            }
        }

        var archive = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["exportedUtc"] = atUtc.ToString("o"),
            ["wrapperVersion"] = BuildInfo.Identity,

            // Keyed by full configuration path — "Sentry:Instances" rather than
            // a nested Sentry object — so what a section is called here is
            // exactly what it is called in the file it came from, and somebody
            // reading the export can find it.
            ["config"] = carried,
        };

        return archive.ToJsonString(Write);
    }

    /// <summary>
    /// Writes an exported file back.
    /// </summary>
    /// <remarks>
    /// <b>Merge, never replace wholesale.</b> Each instance the file carries is
    /// written exactly as it was exported — so no stale setting survives inside
    /// it — while an instance configured here and absent from the file is left
    /// alone. A file that is older or narrower than this machine is far more
    /// likely than somebody deleting a server by leaving it out, which is the
    /// same call the player import makes (§ 7.4) and for the same reason.
    ///
    /// Anything outside <see cref="Sections"/> in the incoming file is ignored
    /// rather than trusted. An import is a file from somewhere else, and the
    /// allow-list has to hold on the way in as well as on the way out — otherwise
    /// an edited export could write <c>Sentry:Admin</c> and hand somebody a login.
    /// </remarks>
    public async Task<ConfigImportResult> ImportAsync(
        string json, CancellationToken cancellationToken = default)
    {
        var archive = Parse(json);

        var version = archive["schemaVersion"]?.GetValue<int>() ?? 0;

        if (version is < 1 or > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"This file says it is schema version {version}; this wrapper reads 1 to "
                + $"{SchemaVersion}.");
        }

        if (archive["config"] is not JsonObject carried)
        {
            throw new InvalidOperationException("This file has no 'config' section to import.");
        }

        var values = new Dictionary<string, JsonNode?>();
        var written = new List<string>();
        var instances = new List<string>();

        foreach (var section in Sections)
        {
            if (carried[section] is not JsonObject body)
            {
                continue;
            }

            written.Add(section);

            foreach (var (key, value) in body)
            {
                // Instances get one level deeper, so each server is replaced as a
                // whole and servers the file does not mention survive. Setting
                // "Servers" itself would delete every one of them.
                if (section == InstanceOptions.SectionName
                    && key == "Servers"
                    && value is JsonObject servers)
                {
                    foreach (var (id, definition) in servers)
                    {
                        values[$"{section}:Servers:{id}"] = definition?.DeepClone();
                        instances.Add(id);
                    }

                    continue;
                }

                values[$"{section}:{key}"] = value?.DeepClone();
            }
        }

        if (values.Count == 0)
        {
            throw new InvalidOperationException(
                "This file carries none of the sections BedrockSentry imports.");
        }

        // Read before writing, so "new" means new here rather than new to the
        // file. Only these need a service restart to appear.
        var existing = await ExistingInstanceIdsAsync(cancellationToken).ConfigureAwait(false);

        var added = instances
            .Where(id => !existing.Contains(id))
            .ToList();

        await JsonConfigWriter.MergeAsync(ConfigPath, values, cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "Imported configuration: {Sections} sections, {Instances} instances ({New} new)",
            written.Count, instances.Count, added.Count);

        return new ConfigImportResult(written, instances, added);
    }

    /// <summary>Reads a file without importing it, so a page can say what it would do.</summary>
    public async Task<ConfigImportResult> PreviewAsync(
        string json, CancellationToken cancellationToken = default)
    {
        var archive = Parse(json);

        if (archive["config"] is not JsonObject carried)
        {
            throw new InvalidOperationException("This file has no 'config' section to import.");
        }

        var existing = await ExistingInstanceIdsAsync(cancellationToken).ConfigureAwait(false);

        var written = Sections.Where(s => carried[s] is JsonObject).ToList();

        var instances = carried[InstanceOptions.SectionName] is JsonObject instanceSection
            && instanceSection["Servers"] is JsonObject servers
                ? servers.Select(s => s.Key).ToList()
                : [];

        return new ConfigImportResult(
            written, instances, instances.Where(id => !existing.Contains(id)).ToList());
    }

    private async Task<HashSet<string>> ExistingInstanceIdsAsync(CancellationToken cancellationToken)
    {
        var root = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);

        return Find(root, $"{InstanceOptions.SectionName}:Servers") is JsonObject servers
            ? new HashSet<string>(servers.Select(s => s.Key), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<JsonObject> ReadConfigAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ConfigPath))
        {
            return [];
        }

        var text = await File.ReadAllTextAsync(ConfigPath, cancellationToken).ConfigureAwait(false);

        return Parse(text);
    }

    private static JsonObject Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(text, Read);

            return document.RootElement.ValueKind is JsonValueKind.Object
                ? JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject ?? []
                : throw new InvalidOperationException("This is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"This is not readable JSON. {ex.Message}", ex);
        }
    }

    /// <summary>Walks a colon-delimited configuration path.</summary>
    private static JsonNode? Find(JsonObject root, string configPath)
    {
        JsonNode? node = root;

        foreach (var segment in configPath.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is not JsonObject current || !current.TryGetPropertyValue(segment, out node))
            {
                return null;
            }
        }

        return node;
    }
}
