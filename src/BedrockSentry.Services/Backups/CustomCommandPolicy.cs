using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Services.Configuration;

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
/// <para>Read from <see cref="IInstallPolicy"/>, never from the data root's config:
/// the service's own account can write that file, and a leftover <c>true</c> there
/// would keep the gate open. <see cref="InstallPolicyFile"/> reads on every call, so
/// turning it off takes effect at once.</para>
/// </remarks>
public sealed class CustomCommandPolicy(IInstallPolicy policy)
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
        "Re-run install.ps1 with -AllowCustomBackupCommand (it still takes -ZipPath). It writes "
        + $"{InstallPolicyFile.AllowCustomBackupCommandKey} into {InstallPolicyFile.FileName} in the "
        + "install folder, which only an administrator can write.";

    /// <summary>The banner the Backups tab shows in place of a working control.</summary>
    public static readonly string Waiver = $"{Reason} {HowToEnable}";

    /// <summary>Whether a custom command may run. Read live, never captured.</summary>
    public bool Allowed => policy.AllowCustomBackupCommand;

    /// <summary>
    /// The block for <paramref name="strategyName"/>, or null when it may run.
    /// </summary>
    public BlockedStrategy? Blocks(string strategyName) =>
        Allowed ? null : new BlockedStrategy(strategyName, Reason, HowToEnable);
}
