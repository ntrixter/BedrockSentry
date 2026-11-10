using System.Text.Json.Nodes;
using BedrockSentry.Core.Broker;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Services.Broker;
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
    private readonly OutsideFolders? _outside;

    public StorageLocations(SentryPaths paths, ILogger<StorageLocations> log, OutsideFolders? outside = null)
    {
        _paths = paths;
        _log = log;
        _outside = outside;
    }

    private string ConfigPath => Path.Combine(_paths.Config, "bedrocksentry.json");

    public string BackupsRoot => _paths.Backups;

    public string ServerPackagesRoot => _paths.ServerPackages;

    public string DefaultBackupsRoot => Path.Combine(_paths.DataRoot, "backups");

    public string DefaultServerPackagesRoot => Path.Combine(_paths.DataRoot, "server-packages");

    public Task SetBackupsRootAsync(string? root, CancellationToken cancellationToken = default) =>
        SetAsync("BackupsRoot", BrokerGrantTarget.BackupsRoot, CurrentOverride(_paths.Backups, DefaultBackupsRoot), root, apply: value =>
            _paths.ApplyStorageOverrides(value, CurrentOverride(_paths.ServerPackages, DefaultServerPackagesRoot)),
            cancellationToken);

    public Task SetServerPackagesRootAsync(string? root, CancellationToken cancellationToken = default) =>
        SetAsync("ServerPackagesRoot", BrokerGrantTarget.ServerPackagesRoot, CurrentOverride(_paths.ServerPackages, DefaultServerPackagesRoot), root, apply: value =>
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
        string key, BrokerGrantTarget target, string? previous, string? root, Action<string?> apply, CancellationToken cancellationToken)
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

            try
            {
                await ProveAsync(value, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex) when (_outside is { } outside && outside.IsOutside(value))
            {
                await HandOverAsync(outside, key, target, previous, value, ex, cancellationToken).ConfigureAwait(false);
            }
        }

        // The file first, then the live paths: if the write fails, what the
        // wrapper is doing still matches what the config says.
        await WriteAsync(key, value, cancellationToken).ConfigureAwait(false);

        apply(value);

        _log.LogInformation(
            "Storage location {Key} set to {Value}", key, value ?? "(the default)");
    }

    /// <summary>
    /// Proves the location works before it is written anywhere: created if missing, one
    /// probe file written and removed. Failing now is a message beside the field; failing
    /// later is a backup that dies at three in the morning.
    /// </summary>
    private static async Task ProveAsync(string folder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var probe = Path.Combine(folder, $".bedrocksentry-probe-{Guid.NewGuid():n}");
        await File.WriteAllTextAsync(probe, "probe", cancellationToken).ConfigureAwait(false);
        File.Delete(probe);
    }

    private Task WriteAsync(string key, string? value, CancellationToken cancellationToken) =>
        JsonConfigWriter.MergeAsync(
            ConfigPath,
            new Dictionary<string, JsonNode?>
            {
                [$"{StorageOptions.SectionName}:{key}"] = JsonValue.Create(value ?? ""),
            },
            cancellationToken);

    /// <summary>
    /// A folder outside the data root the service may not use (<c>D120</c>): the command
    /// that hands it over, or, where the install lets the panel ask, the broker's grant.
    /// </summary>
    /// <remarks>
    /// The broker grants the root it reads from the configuration, so the file names the
    /// folder before it is asked, and names the previous one again if the grant or the
    /// proof after it fails. The live paths change only once the proof passes.
    /// </remarks>
    private async Task HandOverAsync(
        OutsideFolders outside, string key, BrokerGrantTarget target, string? previous, string value,
        UnauthorizedAccessException denied, CancellationToken cancellationToken)
    {
        if (!outside.PanelMayGrant)
        {
            throw new InvalidOperationException(outside.Refusal(value), denied);
        }

        await WriteAsync(key, value, cancellationToken).ConfigureAwait(false);

        var refused = await outside.GrantAsync(target, null, cancellationToken).ConfigureAwait(false);

        if (refused is null)
        {
            try
            {
                await ProveAsync(value, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                refused = $"the folder was granted, but the service still could not write there ({ex.Message})";
            }
        }

        if (refused is not null)
        {
            await WriteAsync(key, previous, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(outside.Refusal(value, refused), denied);
        }
    }
}
