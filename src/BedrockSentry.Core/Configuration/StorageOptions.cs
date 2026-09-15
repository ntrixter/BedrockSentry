namespace BedrockSentry.Core.Configuration;

/// <summary>
/// Where the space-hungry folders live, bound from <c>Sentry:Storage</c>
/// (<c>docs/design.md</c> § 4.3).
/// </summary>
/// <remarks>
/// Only the directories that grow without bound are relocatable: backups and
/// downloaded server packages here, plus the instances root, which was already
/// <c>Sentry:Instances:Root</c> and stays there. Everything else the wrapper
/// keeps — configuration, certificates, key material, the player database,
/// logs, update staging — is small, and stays at the data root on purpose: the
/// config file's own location derives from the data root, so the anchor cannot
/// follow a setting written inside it.
/// </remarks>
public sealed class StorageOptions
{
    public const string SectionName = "Sentry:Storage";

    /// <summary>Where backup artifacts go. Blank means the default under the data root.</summary>
    public string BackupsRoot { get; set; } = "";

    /// <summary>Where downloaded Bedrock zips go. Blank means the default.</summary>
    public string ServerPackagesRoot { get; set; } = "";
}
