using System.Text.Json.Serialization;

namespace BedrockSentry.Core.Updates;

/// <summary>
/// The <c>version.json</c> published alongside each release, describing the newest
/// build on a channel.
/// </summary>
/// <remarks>
/// Fetched from a fixed per-channel URL that always points at the latest build, so
/// the wrapper polls one stable address rather than enumerating releases and working
/// out which is newest.
/// </remarks>
public sealed class UpdateManifest
{
    /// <summary>Composite build identity, e.g. <c>0.3.0-build.57+f0cb4a6</c>.</summary>
    [JsonPropertyName("identity")]
    public string Identity { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Full commit SHA. This, not <see cref="Version"/>, decides whether an update exists.
    /// </summary>
    /// <remarks>
    /// On the build channel the version prefix never moves — only the run number and
    /// the commit advance — so comparing versions would miss every update.
    /// </remarks>
    [JsonPropertyName("commitSha")]
    public string CommitSha { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset PublishedAt { get; set; }

    /// <summary>Lowercase hex SHA-256 of the release archive.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>True when this manifest describes a build other than the running one.</summary>
    public bool IsNewerThanRunning() =>
        !string.IsNullOrWhiteSpace(CommitSha)
        && !string.Equals(CommitSha, BuildInfo.CommitSha, StringComparison.OrdinalIgnoreCase);
}
