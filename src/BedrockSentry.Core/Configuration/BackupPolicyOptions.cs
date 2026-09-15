namespace BedrockSentry.Core.Configuration;

/// <summary>
/// Install-wide policy for what the backup strategies are permitted to do, bound
/// from <c>Sentry:Backups</c>.
/// </summary>
/// <remarks>
/// <para>Separate from <c>BackupSettings</c>, which is per-instance and is written
/// by the Backups tab. <strong>Nothing here may be settable from the panel</strong>
/// — that is the whole point of it, and a setting the panel can write is a gate
/// that gates nothing.</para>
///
/// <para>It lives in the config file under the data root, which
/// <c>install.ps1</c> and <c>SentryPaths</c> both ACL to LocalSystem and
/// Administrators only. So editing it by hand already requires an administrator;
/// the file permissions are the enforcement, not a check in this process.</para>
///
/// <para>Deliberately absent from the settings export as well —
/// <c>ConfigArchive</c> carries an <em>allow</em>-list of sections, so a new one
/// is excluded until somebody decides otherwise. A config people email around
/// must not be able to switch this on at the far end.</para>
/// </remarks>
public sealed class BackupPolicyOptions
{
    public const string SectionName = "Sentry:Backups";

    /// <summary>
    /// Whether the custom-command backup strategy may run at all (<c>D45</c>).
    /// </summary>
    /// <remarks>
    /// <para>Off unless somebody turned it on, which is what a bare auto-property
    /// with no initializer gives: a fresh install, an upgrade, and a config file
    /// that has never heard of this section all land on <c>false</c>.</para>
    ///
    /// <para><strong>Why it needs a gate at all.</strong> The strategy runs
    /// <c>cmd.exe /d /s /c "&lt;command&gt;"</c> with the command taken from the
    /// Backups tab, as whatever account the service runs as — LocalSystem today —
    /// and the panel binds every interface once setup is done. So without this,
    /// "somebody got into the panel" and "somebody owns the machine" are the same
    /// event.</para>
    ///
    /// <para><strong>What it does not do.</strong> It does not hide the feature:
    /// the checkbox and any command already saved stay visible, disabled, with the
    /// reason and the way to enable it beside them. It does not clear a command
    /// somebody had configured before the gate existed — turning this on starts it
    /// running again with nothing to retype. And it is not a boundary against an
    /// attacker who already has code execution: running as LocalSystem the service
    /// could rewrite this flag. The point is that a default install is not a
    /// remote shell, and turning it into one is a deliberate act by somebody at an
    /// elevated prompt.</para>
    /// </remarks>
    public bool AllowCustomCommand { get; set; }
}
