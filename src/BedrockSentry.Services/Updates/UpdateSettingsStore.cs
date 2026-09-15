using System.Text.Json.Nodes;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Services.Configuration;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Updates;

/// <summary>
/// Writes the wrapper's own update settings back to the configuration file
/// (<c>docs/design.md</c> § 12).
/// </summary>
/// <remarks>
/// Only the four an administrator should be choosing from a page. The feed
/// address, the health port and the failure limits stay hand-edited on purpose:
/// they are the parts of the self-update contract that can strand an install
/// between versions if they are wrong, and none of them is something somebody
/// changes on a Tuesday.
/// </remarks>
public sealed class UpdateSettingsStore(ISentryPaths paths, ILogger<UpdateSettingsStore> logger)
{
    private string ConfigPath => Path.Combine(paths.Config, "bedrocksentry.json");

    public async Task SaveAsync(
        UpdateChannel channel,
        UpdateMode mode,
        TimeSpan checkInterval,
        TimeSpan playerWarning,
        CancellationToken cancellationToken = default)
    {
        var prefix = UpdateOptions.SectionName;

        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{prefix}:Channel"] = JsonValue.Create(channel.ToString()),
                    [$"{prefix}:Mode"] = JsonValue.Create(mode.ToString()),

                    // "hh:mm:ss", which is what the binder reads back and what a
                    // person hand-editing this file would have written.
                    [$"{prefix}:CheckInterval"] = JsonValue.Create(checkInterval.ToString("c")),
                    [$"{prefix}:PlayerWarning"] = JsonValue.Create(playerWarning.ToString("c")),
                },
                cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Wrapper update settings changed: {Channel} channel, {Mode}, checking every {Interval}, "
            + "{Warning} warning",
            channel, mode, checkInterval, playerWarning);
    }
}
