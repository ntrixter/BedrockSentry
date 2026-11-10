using System.Text.Json.Nodes;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Configuration;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Packages;

/// <summary>
/// Writes the install-wide half of the Bedrock update settings
/// (<c>docs/design.md</c> § 11) back to the configuration file.
/// </summary>
/// <remarks>
/// Install-wide, so it deliberately does not live on the instance registry
/// beside the per-instance mode, channel and pin: when the feed is checked
/// describes Mojang, not any one server, and having the two writers in one
/// place would invite exactly the mix-up the split exists to prevent.
///
/// Only the keys it is asked for are written, through
/// <see cref="JsonConfigWriter"/> — a hand-edited <c>LinksUrl</c> or a
/// <c>CheckForUpdates: false</c> survives a schedule change untouched.
/// </remarks>
public sealed class BdsUpdateSettingsStore(ISentryPaths paths, ILogger<BdsUpdateSettingsStore> logger)
{
    private string ConfigPath => Path.Combine(paths.Config, "bedrocksentry.json");

    /// <summary>Persists when the wrapper asks Mojang what it is offering.</summary>
    /// <remarks>
    /// The legacy <c>CheckInterval</c> key is cleared at the same time. Leaving
    /// it would make the file state two different answers to one question, and
    /// the one it states louder — a bare interval sitting above the schedule —
    /// would be the one that no longer applies.
    /// </remarks>
    public async Task SetScheduleAsync(
        RestartSchedule schedule, bool checkForUpdates, CancellationToken cancellationToken = default)
    {
        var prefix = $"{BdsUpdateOptions.SectionName}:CheckSchedule";

        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{BdsUpdateOptions.SectionName}:CheckForUpdates"] = JsonValue.Create(checkForUpdates),
                    [$"{prefix}:Mode"] = JsonValue.Create(schedule.Mode.ToString()),
                    [$"{prefix}:IntervalHours"] = JsonValue.Create(schedule.IntervalHours),
                    [$"{prefix}:IntervalMinutes"] = JsonValue.Create(schedule.IntervalMinutes),

                    // "HH:mm" rather than the round-trip form: this file gets
                    // hand-edited, and the binder reads back what a person would
                    // have written anyway.
                    [$"{prefix}:TimeOfDay"] = JsonValue.Create(schedule.TimeOfDay.ToString("HH\\:mm")),
                },
                cancellationToken)
            .ConfigureAwait(false);

        await JsonConfigWriter
            .RemoveAsync(ConfigPath, [$"{BdsUpdateOptions.SectionName}:CheckInterval"], cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Bedrock update checks are now {Schedule}",
            checkForUpdates ? Describe(schedule) : "turned off");
    }

    /// <summary>One phrase for a log line or a page, in the administrator's terms.</summary>
    /// <remarks>
    /// The schedule's own words wherever it runs (<see cref="RestartSchedule.Describe"/>),
    /// rather than a copy of them the twelve-hour clock (<c>T304</c>) would have to reach
    /// twice. Only the no-schedule answer differs, and on purpose.
    /// </remarks>
    public static string Describe(RestartSchedule schedule) => schedule.Mode switch
    {
        RestartScheduleMode.EveryNHours or RestartScheduleMode.EveryNMinutes or RestartScheduleMode.DailyAt =>
            schedule.Describe(),

        // Not "never": the check on service startup still runs, and so does the
        // button. Saying never would be wrong in the way that matters.
        _ => "only on startup and when asked",
    };
}
