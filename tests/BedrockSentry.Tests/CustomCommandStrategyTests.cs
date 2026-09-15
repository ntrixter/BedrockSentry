using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Services.Backups;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 8.2. Commands go through the real platform shell — cmd on Windows,
/// sh elsewhere — so each test writes its command for the shell it will get.
/// </summary>
public sealed class CustomCommandStrategyTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-cmd-{Guid.NewGuid():N}");

    public CustomCommandStrategyTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private BackupContext Context(string command)
    {
        var artifacts = Path.Combine(_directory, "artifacts");
        Directory.CreateDirectory(artifacts);

        return new BackupContext(
            "survival",
            "Survival",
            _directory,
            Path.Combine(_directory, "worlds"),
            [new SaveManifestEntry("Bedrock level/level.dat", 2543)],
            artifacts,
            new BackupSettings { CustomCommand = new() { Enabled = true, Command = command } },
            BackupReason.Manual,
            "a note",
            new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// A strategy on an install where the command is permitted, which is what every
    /// test below this line except the gate ones is about.
    /// </summary>
    private static CustomCommandStrategy Strategy(TimeSpan? timeout = null) =>
        new(Policy(allowed: true), NullLogger<CustomCommandStrategy>.Instance, timeout);

    private static CustomCommandPolicy Policy(bool allowed) =>
        new(new ChangeableOptionsMonitor<BackupPolicyOptions>(
            new BackupPolicyOptions { AllowCustomCommand = allowed }));

    [Fact]
    public void Enabled_requires_a_command_not_just_the_toggle()
    {
        var strategy = Strategy();

        Assert.False(strategy.IsEnabled(new BackupSettings()));
        Assert.False(strategy.IsEnabled(
            new BackupSettings { CustomCommand = new() { Enabled = true, Command = "  " } }));
        Assert.True(strategy.IsEnabled(
            new BackupSettings { CustomCommand = new() { Enabled = true, Command = "backup.cmd" } }));
    }

    [Fact]
    public async Task Runs_the_command_with_the_context_in_its_environment()
    {
        var command = OperatingSystem.IsWindows()
            ? "echo instance=%BEDROCKSENTRY_INSTANCE_ID% reason=%BEDROCKSENTRY_REASON%"
            : "echo instance=$BEDROCKSENTRY_INSTANCE_ID reason=$BEDROCKSENTRY_REASON";

        var result = await Strategy().RunAsync(Context(command), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("instance=survival", result.Detail);
        Assert.Contains("reason=Manual", result.Detail);
    }

    // The manifest file is the part a script cannot reconstruct: the byte counts
    // are only real while saves are held, and a script copying live files
    // without them has the same corruption problem every strategy has.
    [Fact]
    public async Task Writes_the_manifest_where_the_command_can_read_it()
    {
        var command = OperatingSystem.IsWindows()
            ? "type \"%BEDROCKSENTRY_MANIFEST_FILE%\""
            : "cat \"$BEDROCKSENTRY_MANIFEST_FILE\"";

        var result = await Strategy().RunAsync(Context(command), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("Bedrock level/level.dat:2543", result.Detail);

        // And it stays beside the artifacts afterwards, documenting what the
        // snapshot contained.
        Assert.NotNull(result.ArtifactPath);
        Assert.True(File.Exists(result.ArtifactPath));
    }

    [Fact]
    public async Task A_nonzero_exit_is_a_failure_that_names_the_code()
    {
        var result = await Strategy().RunAsync(Context("exit 3"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Exit code 3", result.Detail);
    }

    // Saves are held while this runs; a hung script does not get to hold a live
    // server's writes with it.
    [Fact]
    public async Task A_command_that_outlives_the_timeout_is_killed_and_reported()
    {
        var command = OperatingSystem.IsWindows()
            ? "ping -n 60 127.0.0.1 > nul"
            : "sleep 60";

        var result = await Strategy(TimeSpan.FromMilliseconds(500))
            .RunAsync(Context(command), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("still running", result.Detail);
    }
}
