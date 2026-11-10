using System.Text.Json.Nodes;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Services.Configuration;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Players;

/// <summary>
/// Writes the player-faces switch (<c>D47</c>) back to the configuration file.
/// </summary>
/// <remarks>
/// Only the one key, through <see cref="JsonConfigWriter"/>, so a hand-edited
/// <c>CacheFor</c> survives the switch being flipped.
/// </remarks>
public sealed class PlayerFacesSettingsStore(ISentryPaths paths, ILogger<PlayerFacesSettingsStore> logger)
{
    private string ConfigPath => Path.Combine(paths.Config, "bedrocksentry.json");

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{PlayerFacesOptions.SectionName}:{nameof(PlayerFacesOptions.Enabled)}"] = JsonValue.Create(enabled),
                },
                cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Player faces from {Source} are now {State}",
            PlayerFacesOptions.SourceName,
            enabled ? "on" : "off");
    }
}
