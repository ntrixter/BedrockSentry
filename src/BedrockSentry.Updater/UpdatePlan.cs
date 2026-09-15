using System.Text.Json.Serialization;

namespace BedrockSentry.Updater;

/// <summary>
/// Everything the helper needs, handed over as a file by the service.
/// </summary>
/// <remarks>
/// <strong>Duplicated from <c>BedrockSentry.Core.Updates.UpdatePlan</c> on purpose.</strong>
/// This project has no project references because it exists to replace the assemblies
/// it would otherwise load — referencing Core would put a file lock on something it
/// has to overwrite, which is the one thing it must never do. Duplicating a small
/// data contract is the price. Change one, change both; the JSON property names are
/// what actually has to match.
/// </remarks>
internal sealed class UpdatePlan
{
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = "BedrockSentry";

    [JsonPropertyName("installRoot")]
    public string InstallRoot { get; set; } = string.Empty;

    [JsonPropertyName("previousVersionDirectory")]
    public string PreviousVersionDirectory { get; set; } = string.Empty;

    [JsonPropertyName("targetVersionDirectory")]
    public string TargetVersionDirectory { get; set; } = string.Empty;

    [JsonPropertyName("targetIdentity")]
    public string TargetIdentity { get; set; } = string.Empty;

    [JsonPropertyName("previousIdentity")]
    public string PreviousIdentity { get; set; } = string.Empty;

    [JsonPropertyName("healthUrl")]
    public string HealthUrl { get; set; } = string.Empty;

    [JsonPropertyName("healthTimeoutSeconds")]
    public int HealthTimeoutSeconds { get; set; } = 90;

    [JsonPropertyName("stopTimeoutSeconds")]
    public int StopTimeoutSeconds { get; set; } = 120;

    [JsonPropertyName("logPath")]
    public string LogPath { get; set; } = string.Empty;

    [JsonPropertyName("resultPath")]
    public string ResultPath { get; set; } = string.Empty;
}

/// <summary>Outcome left behind for the service to read once it is back up.</summary>
internal sealed class UpdateResult
{
    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; set; }

    [JsonPropertyName("identity")]
    public string Identity { get; set; } = string.Empty;

    [JsonPropertyName("rolledBack")]
    public bool RolledBack { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("completedUtc")]
    public DateTimeOffset CompletedUtc { get; set; }
}
