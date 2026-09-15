using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;
using Microsoft.Extensions.Options;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// Whether the custom-command backup strategy is permitted to run on this
/// install (<c>D45</c>).
/// </summary>
/// <remarks>
/// <para>One reader for the strategy, the orchestrator's report, the Backups tab
/// and the startup warning, so the rule is stated once. Stated in each of those
/// it would be stated four times and enforced once, which is this codebase's
/// recurring defect — and here it would mean a panel that says blocked over a
/// command that runs, or the reverse.</para>
///
/// <para><strong>The direction that must be live is the opposite of
/// <c>DestructiveConfirmPolicy</c>'s, and that is worth noticing rather than
/// copying past.</strong> There the dangerous state is <em>off</em>, so turning
/// the check back on must not need a restart. Here the dangerous state is
/// <em>on</em>, so turning it off is the direction that matters: an administrator
/// who decides the command box was a mistake must be able to close it without
/// waiting for a restart window. <see cref="IOptionsMonitor{T}"/> gives both, and
/// the config file is registered with <c>reloadOnChange</c>.</para>
/// </remarks>
public sealed class CustomCommandPolicy(IOptionsMonitor<BackupPolicyOptions> options)
{
    /// <summary>Why it is off, in the words the operator reads.</summary>
    public const string Reason =
        "The custom backup command is switched off for this install. A command typed into the "
        + "panel runs as the account the service runs as, and the panel is reachable from the "
        + "network once setup is done — so it stays off until an administrator turns it on from "
        + "an elevated prompt.";

    /// <summary>
    /// What to do about it. Built from the option's own names so the text cannot
    /// drift from the setting it describes.
    /// </summary>
    public static readonly string HowToEnable =
        "Re-run install.ps1 with -AllowCustomBackupCommand (it still takes -ZipPath), or set "
        + $"{BackupPolicyOptions.SectionName}:{nameof(BackupPolicyOptions.AllowCustomCommand)} "
        + "to true in the data root's config\\bedrocksentry.json, which only an administrator "
        + "can write.";

    /// <summary>The banner the Backups tab shows in place of a working control.</summary>
    public static readonly string Waiver = $"{Reason} {HowToEnable}";

    /// <summary>Whether a custom command may run. Read live, never captured.</summary>
    public bool Allowed => options.CurrentValue.AllowCustomCommand;

    /// <summary>
    /// The block for <paramref name="strategyName"/>, or null when it may run.
    /// </summary>
    public BlockedStrategy? Blocks(string strategyName) =>
        Allowed ? null : new BlockedStrategy(strategyName, Reason, HowToEnable);
}
