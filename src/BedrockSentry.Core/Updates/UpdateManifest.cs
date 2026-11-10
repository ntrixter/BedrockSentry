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

    /// <summary>
    /// The install epoch the build was cut at (<c>D114</c>). A build above the running
    /// one's epoch cannot be installed by self-update and needs <c>uninstall.ps1</c>
    /// then <c>install.ps1</c> instead.
    /// </summary>
    /// <remarks>
    /// An epoch rather than a flag because the manifest is a rolling pointer: a flag
    /// set on the first build that needs a reinstall would have to stay set on every
    /// build after it, and then no install on the new side could update either.
    /// Absent from every manifest published before the field existed, which reads as
    /// <c>0</c>. A build that predates this property ignores it, so the field only
    /// protects installs that already run a build that reads it.
    /// </remarks>
    [JsonPropertyName("installEpoch")]
    public int InstallEpoch { get; set; }

    /// <summary>True when this build is above the running build's install epoch.</summary>
    public bool NeedsReinstall() => NeedsReinstallFrom(BuildInfo.InstallEpoch);

    /// <summary>True when this build is above <paramref name="runningEpoch"/>.</summary>
    public bool NeedsReinstallFrom(int runningEpoch) => InstallEpoch > runningEpoch;

    /// <summary>True when this manifest describes a build other than the running one.</summary>
    public bool IsNewerThanRunning() =>
        !string.IsNullOrWhiteSpace(CommitSha)
        && !string.Equals(CommitSha, BuildInfo.CommitSha, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the Settings page says in place of <em>Install now</em> for a build that
    /// <see cref="NeedsReinstall"/>.
    /// </summary>
    public string ReinstallNotice() =>
        $"{Identity} cannot be installed from here. Run uninstall.ps1 and then install.ps1 "
        + "from its release; worlds, backups and settings are kept.";
}
