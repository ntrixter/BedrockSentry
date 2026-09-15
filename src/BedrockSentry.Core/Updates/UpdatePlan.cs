using System.Text.Json.Serialization;

namespace BedrockSentry.Core.Updates;

/// <summary>
/// Everything the updater helper needs, handed over as a file.
/// </summary>
/// <remarks>
/// <para>
/// A file rather than command-line arguments: the helper is launched detached with no
/// inherited handles, paths here routinely contain spaces, and a plan on disk is
/// something an operator can read after the fact when an update went wrong.
/// </para>
/// <para>
/// <strong>This type is duplicated in BedrockSentry.Updater.</strong> That project
/// deliberately has no project references — it exists to replace the very assemblies
/// it would otherwise load, so referencing Core would put a lock on a file it has to
/// overwrite. The duplication is the price of that guarantee. Change one, change both.
/// </para>
/// </remarks>
public sealed class UpdatePlan
{
    /// <summary>Windows service name to stop and start.</summary>
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = "BedrockSentry";

    /// <summary>Install root holding <c>versions\</c> and the <c>current</c> junction.</summary>
    [JsonPropertyName("installRoot")]
    public string InstallRoot { get; set; } = string.Empty;

    /// <summary>Version directory the junction points at now, to roll back to.</summary>
    [JsonPropertyName("previousVersionDirectory")]
    public string PreviousVersionDirectory { get; set; } = string.Empty;

    /// <summary>Already-staged version directory to switch to.</summary>
    [JsonPropertyName("targetVersionDirectory")]
    public string TargetVersionDirectory { get; set; } = string.Empty;

    /// <summary>Composite identity of the build being installed.</summary>
    [JsonPropertyName("targetIdentity")]
    public string TargetIdentity { get; set; } = string.Empty;

    /// <summary>Identity currently running, recorded so a rollback can be verified too.</summary>
    [JsonPropertyName("previousIdentity")]
    public string PreviousIdentity { get; set; } = string.Empty;

    /// <summary>
    /// Loopback health URL to poll after the switch.
    /// </summary>
    /// <remarks>
    /// Written by the build being replaced and consumed against the build replacing
    /// it, so the port and path are a compatibility contract across versions rather
    /// than a private detail. Changing either in a future build breaks updates
    /// <em>from</em> every build that came before it.
    /// </remarks>
    [JsonPropertyName("healthUrl")]
    public string HealthUrl { get; set; } = string.Empty;

    [JsonPropertyName("healthTimeoutSeconds")]
    public int HealthTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// How long to wait for a clean service stop before forcing it.
    /// </summary>
    /// <remarks>
    /// Must exceed the host's own shutdown timeout, which must in turn exceed the
    /// drain budget of every managed server instance. Inverting that ordering means
    /// killing a service that is legitimately mid-save.
    /// </remarks>
    [JsonPropertyName("stopTimeoutSeconds")]
    public int StopTimeoutSeconds { get; set; } = 120;

    /// <summary>Where the helper writes its own progress log.</summary>
    [JsonPropertyName("logPath")]
    public string LogPath { get; set; } = string.Empty;

    /// <summary>Where the helper writes the outcome for the service to read on restart.</summary>
    [JsonPropertyName("resultPath")]
    public string ResultPath { get; set; } = string.Empty;
}

/// <summary>Outcome the helper leaves behind for the service to pick up.</summary>
public sealed class UpdateResult
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
