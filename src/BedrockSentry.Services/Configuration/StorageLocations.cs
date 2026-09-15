using System.Text.Json.Nodes;
using BedrockSentry.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Configuration;

/// <summary>
/// Edits where backups and server packages live, live (<c>docs/design.md</c> § 4.3).
/// </summary>
/// <remarks>
/// No restart, deliberately: <c>BackupHistory</c> and <c>ServerPackageStore</c>
/// both resolve their directory from <see cref="SentryPaths"/> on every call
/// rather than caching it, so a change here is simply where the <em>next</em>
/// backup or download lands. What this class does not do is move anything —
/// files already at the old location stay there, stop appearing in the pickers
/// until they are moved by hand, and the page says so rather than leaving the
/// administrator to wonder where their archives went. A move by hand is safe at
/// any quiet moment because nothing holds these directories open between
/// operations.
/// </remarks>
public sealed class StorageLocations
{
    private readonly SentryPaths _paths;
    private readonly ILogger<StorageLocations> _log;

    public StorageLocations(SentryPaths paths, ILogger<StorageLocations> log)
    {
        _paths = paths;
        _log = log;
    }

    private string ConfigPath => Path.Combine(_paths.Config, "bedrocksentry.json");

    public string BackupsRoot => _paths.Backups;

    public string ServerPackagesRoot => _paths.ServerPackages;

    public string DefaultBackupsRoot => Path.Combine(_paths.DataRoot, "backups");

    public string DefaultServerPackagesRoot => Path.Combine(_paths.DataRoot, "server-packages");

    public Task SetBackupsRootAsync(string? root, CancellationToken cancellationToken = default) =>
        SetAsync("BackupsRoot", root, apply: value =>
            _paths.ApplyStorageOverrides(value, CurrentOverride(_paths.ServerPackages, DefaultServerPackagesRoot)),
            cancellationToken);

    public Task SetServerPackagesRootAsync(string? root, CancellationToken cancellationToken = default) =>
        SetAsync("ServerPackagesRoot", root, apply: value =>
            _paths.ApplyStorageOverrides(CurrentOverride(_paths.Backups, DefaultBackupsRoot), value),
            cancellationToken);

    /// <summary>The current override, or null when the path is simply the default.</summary>
    private static string? CurrentOverride(string current, string @default) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(current),
            Path.TrimEndingDirectorySeparator(@default),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal)
            ? null
            : current;

    private async Task SetAsync(
        string key, string? root, Action<string?> apply, CancellationToken cancellationToken)
    {
        string? value;

        if (string.IsNullOrWhiteSpace(root))
        {
            // Blank is "back to the default", not an error — the page's way of
            // undoing an override.
            value = null;
        }
        else
        {
            if (!Path.IsPathRooted(root))
            {
                throw new InvalidOperationException(
                    "The folder needs a full path, like D:\\\\BedrockSentry\\\\backups.");
            }

            value = Path.GetFullPath(root.Trim());

            // Prove the location works before it is written anywhere: created if
            // missing, one probe file written and removed. Failing now is a
            // message beside the field; failing later is a backup that dies at
            // three in the morning.
            Directory.CreateDirectory(value);
            var probe = Path.Combine(value, $".bedrocksentry-probe-{Guid.NewGuid():n}");
            await File.WriteAllTextAsync(probe, "probe", cancellationToken).ConfigureAwait(false);
            File.Delete(probe);
        }

        // The file first, then the live paths: if the write fails, what the
        // wrapper is doing still matches what the config says.
        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{StorageOptions.SectionName}:{key}"] = JsonValue.Create(value ?? ""),
                },
                cancellationToken)
            .ConfigureAwait(false);

        apply(value);

        _log.LogInformation(
            "Storage location {Key} set to {Value}", key, value ?? "(the default)");
    }
}
