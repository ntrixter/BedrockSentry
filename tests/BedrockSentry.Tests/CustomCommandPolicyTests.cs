using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// <c>D45</c>. The gate that keeps a default install from being a remote shell.
/// </summary>
/// <remarks>
/// The strategy runs <c>cmd.exe /d /s /c "&lt;command from the panel&gt;"</c> as the
/// service account, and the panel binds every interface once setup is done. So the
/// default matters more than anything else here, and it is the first thing asserted.
/// </remarks>
public class CustomCommandPolicyTests
{
    private static CustomCommandPolicy Policy(bool allowed) =>
        new(new ChangeableOptionsMonitor<BackupPolicyOptions>(
            new BackupPolicyOptions { AllowCustomCommand = allowed }));

    /// <summary>
    /// THE ONE THAT MATTERS. A fresh install, an upgrade, and a config file that has
    /// never heard of the section all land here.
    /// </summary>
    [Fact]
    public void The_command_is_blocked_unless_somebody_turned_it_on()
    {
        var untouched = new CustomCommandPolicy(
            new ChangeableOptionsMonitor<BackupPolicyOptions>(new BackupPolicyOptions()));

        Assert.False(untouched.Allowed);
        Assert.NotNull(untouched.Blocks("custom-command"));

        Assert.False(Policy(allowed: false).Allowed);
    }

    [Fact]
    public void Turning_it_on_lets_it_run()
    {
        Assert.True(Policy(allowed: true).Allowed);
        Assert.Null(Policy(allowed: true).Blocks("custom-command"));
    }

    /// <remarks>
    /// Both directions, and the one that matters is turning it <em>off</em> — the
    /// mirror of <c>DestructiveConfirmPolicy</c>, where the dangerous state is the
    /// other one. An administrator who decides the command box was a mistake must be
    /// able to close it without waiting for a restart window.
    /// </remarks>
    [Fact]
    public void Turning_it_off_again_takes_effect_without_a_restart()
    {
        var options = new ChangeableOptionsMonitor<BackupPolicyOptions>(
            new BackupPolicyOptions { AllowCustomCommand = true });

        var policy = new CustomCommandPolicy(options);
        Assert.True(policy.Allowed);

        options.Set(new BackupPolicyOptions());

        Assert.False(policy.Allowed);
    }

    [Fact]
    public void The_block_names_the_strategy_it_came_from()
    {
        var block = Policy(allowed: false).Blocks("custom-command");

        Assert.Equal("custom-command", block!.Strategy);
    }

    /// <remarks>
    /// The person reading this is reading it in a history row or on a panel, days
    /// later, without the startup log in front of them — so the remedy has to travel
    /// with the reason.
    /// </remarks>
    [Fact]
    public void The_block_names_the_setting_and_the_installer_switch()
    {
        var detail = Policy(allowed: false).Blocks("custom-command")!.Detail;

        Assert.Contains(BackupPolicyOptions.SectionName, detail);
        Assert.Contains(nameof(BackupPolicyOptions.AllowCustomCommand), detail);
        Assert.Contains("-AllowCustomBackupCommand", detail);
        Assert.Contains("bedrocksentry.json", detail);
    }

    [Fact]
    public void The_waiver_the_panel_shows_carries_both_halves()
    {
        Assert.Contains(CustomCommandPolicy.Reason, CustomCommandPolicy.Waiver);
        Assert.Contains(CustomCommandPolicy.HowToEnable, CustomCommandPolicy.Waiver);
    }

    /// <summary>
    /// It must not travel in a settings export.
    /// </summary>
    /// <remarks>
    /// Free, because <c>ConfigArchive</c> carries an <em>allow</em>-list of sections
    /// rather than a deny-list. Asserted anyway: "we get it for free" is exactly the
    /// sentence that stops being true when the list is next edited, and this is the
    /// one that would let somebody enable a remote shell by emailing a config file.
    /// </remarks>
    [Fact]
    public void The_backups_policy_section_is_not_something_an_export_can_carry()
    {
        Assert.DoesNotContain(
            BackupPolicyOptions.SectionName,
            ConfigArchive.Sections,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// THE GATE ITSELF, pointed straight at the strategy rather than through the
    /// orchestrator.
    /// </summary>
    /// <remarks>
    /// The orchestrator drops a blocked strategy before calling it, so this arm is
    /// unreachable in production today. It exists because "the gate covers any
    /// caller" is a claim, and a caller that builds a <c>BackupContext</c> itself is
    /// the one it has to be true for. The sentinel is the assertion that matters:
    /// a refusal that still ran the command would pass every other check here.
    /// </remarks>
    [Fact]
    public async Task A_blocked_command_is_refused_by_the_strategy_itself_and_never_runs()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bs-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var sentinel = Path.Combine(directory, "it-ran.txt");
            var command = OperatingSystem.IsWindows()
                ? $"echo ran > \"{sentinel}\""
                : $"touch '{sentinel}'";

            var strategy = new CustomCommandStrategy(
                Policy(allowed: false), NullLogger<CustomCommandStrategy>.Instance);

            var context = new BackupContext(
                "survival", "Survival", directory, Path.Combine(directory, "worlds"),
                [new SaveManifestEntry("Bedrock level/level.dat", 2543)],
                directory,
                new BackupSettings { CustomCommand = new() { Enabled = true, Command = command } },
                BackupReason.Manual, null, DateTimeOffset.UtcNow);

            var result = await strategy.RunAsync(context, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("-AllowCustomBackupCommand", result.Detail);

            // The command did not run, and nothing was written on its behalf.
            Assert.False(File.Exists(sentinel));
            Assert.Empty(Directory.GetFiles(directory, "manifest-*.txt"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
