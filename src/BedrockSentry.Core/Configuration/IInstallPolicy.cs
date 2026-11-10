namespace BedrockSentry.Core.Configuration;

/// <summary>
/// Switches only an administrator sets, read from a file the service's account can read
/// and never write (milestone 18.5).
/// </summary>
/// <remarks>
/// <para>Not configuration. The data root's config file is the service account's to
/// write once the service no longer runs as LocalSystem, so a gate kept there gates
/// nothing: whatever reached the account could open it. This file lives in the install
/// folder, which only administrators and LocalSystem can write, and only an elevated
/// <c>install.ps1</c> writes it.</para>
/// <para>Absent from the panel and from the settings export by construction: neither
/// can reach the install folder.</para>
/// </remarks>
public interface IInstallPolicy
{
    /// <summary>
    /// Whether the custom-command backup strategy may run at all (<c>D45</c>). False
    /// unless the file says true, so a missing or unreadable file keeps it shut.
    /// </summary>
    bool AllowCustomBackupCommand { get; }

    /// <summary>
    /// The update feed the privilege broker reads, or null for the built-in one.
    /// </summary>
    /// <remarks>
    /// Here and not in <c>Sentry:Update:FeedUrlTemplate</c>, which the service's account
    /// can write: the feed names the build and its checksum, and the broker installs
    /// that build as LocalSystem. Only an <c>https</c> address counts.
    /// </remarks>
    string? UpdateFeedUrlTemplate { get; }

    /// <summary>
    /// Whether the first-run wizard may be reached from the network before an
    /// administrator account exists: <c>install.ps1 -AllowRemoteSetup</c>.
    /// </summary>
    /// <remarks>
    /// Here so the elevated installer never writes into the data root, which the
    /// service's account can change, links included. <c>Sentry:AllowRemoteSetup</c> in
    /// the service's own configuration is read beside it.
    /// </remarks>
    bool AllowRemoteSetup => false;

    /// <summary>
    /// Whether the panel may ask the broker to grant a folder outside the data root
    /// (<c>D120</c>): <c>install.ps1 -AllowPanelFolderGrants</c>. False by default, when
    /// only <c>install.ps1 -GrantFolder</c> grants one.
    /// </summary>
    bool AllowPanelFolderGrants => false;
}
