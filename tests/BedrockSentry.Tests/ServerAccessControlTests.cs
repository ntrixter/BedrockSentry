using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 7.6. One editor per server state: a stopped server's files are
/// edited directly; a running server is told through its console and the change
/// is confirmed by watching for BDS's own rewrite of the file — never by
/// parsing a reply whose wording has not been measured.
/// </summary>
public sealed class ServerAccessControlTests : IAsyncDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-acl-{Guid.NewGuid():N}");

    private readonly FakeProcessFactory _factory = new();
    private readonly InstanceSupervisor _supervisor;

    public ServerAccessControlTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "instance"));

        _supervisor = new InstanceSupervisor(
            "survival",
            new InstanceDefinition { Name = "Survival" },
            Path.Combine(_directory, "instance"),
            _factory,
            TimeProvider.System,
            NullLogger<InstanceSupervisor>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _supervisor.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private ServerAccessControl Control(TimeSpan? confirmWithin = null) => new(
        TimeProvider.System,
        NullLogger<ServerAccessControl>.Instance,
        confirmWithin ?? TimeSpan.FromSeconds(2));

    private string AllowlistPath => AllowlistFile.PathFor(_supervisor.WorkingDirectory);
    private string PermissionsPath => PermissionsFile.PathFor(_supervisor.WorkingDirectory);

    private async Task StartServerAsync()
    {
        _factory.ProcessesExitOnStop = true;
        await _supervisor.StartAsync();
        _factory.Latest.Ready();
        Assert.True(await _supervisor.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_stopped_server_gets_its_file_edited_directly()
    {
        var result = await Control().AllowAsync(_supervisor, "SteveExample", ignoresPlayerLimit: true);

        Assert.True(result.Changed);
        Assert.True(result.ConfirmedOnDisk);

        var entry = Assert.Single(AllowlistFile.Load(AllowlistPath).Entries);
        Assert.Equal("SteveExample", entry.Name);
        Assert.True(entry.IgnoresPlayerLimit);

        // And nothing was sent to a server that is not there.
        Assert.Empty(_factory.Created);
    }

    [Fact]
    public async Task Allowing_somebody_already_allowed_is_a_no_op_that_says_so()
    {
        await Control().AllowAsync(_supervisor, "Steve");
        var again = await Control().AllowAsync(_supervisor, "steve");

        Assert.False(again.Changed);
        Assert.Single(AllowlistFile.Load(AllowlistPath).Entries);
    }

    [Fact]
    public async Task A_running_server_is_told_through_its_console()
    {
        await StartServerAsync();

        // Simulate BDS rewriting its own file, the way a real server answers the
        // command — the confirmation loop should see it appear. Hung off the send
        // rather than off a timer: this used to answer 300 ms after the test
        // started, which raced the confirmation window and failed build.62 on a
        // loaded Windows runner while passing every time locally.
        _factory.Latest.Answers = command =>
        {
            if (command.StartsWith("allowlist add", StringComparison.Ordinal))
            {
                var file = AllowlistFile.Load(AllowlistPath);
                file.Add("Some Player 99");
                file.Save(AllowlistPath);
            }

            return Task.CompletedTask;
        };

        var result = await Control().AllowAsync(_supervisor, "Some Player 99");

        Assert.True(result.Changed);
        Assert.True(result.ConfirmedOnDisk);

        // The name carries spaces, so the argument must be quoted.
        Assert.Contains("allowlist add \"Some Player 99\"", _factory.Latest.Sent);
    }

    [Fact]
    public async Task A_change_the_server_never_wrote_is_reported_unconfirmed()
    {
        await StartServerAsync();

        var result = await Control(TimeSpan.FromMilliseconds(400))
            .AllowAsync(_supervisor, "Steve");

        Assert.True(result.Changed);
        Assert.False(result.ConfirmedOnDisk);
        Assert.Contains("has not appeared", result.Detail);
    }

    [Fact]
    public async Task Removing_goes_through_the_console_too_when_running()
    {
        var seeded = AllowlistFile.Load(AllowlistPath);
        seeded.Add("Steve");
        seeded.Save(AllowlistPath);

        await StartServerAsync();

        _factory.Latest.Answers = command =>
        {
            if (command.StartsWith("allowlist remove", StringComparison.Ordinal))
            {
                var file = AllowlistFile.Load(AllowlistPath);
                file.Remove("Steve");
                file.Save(AllowlistPath);
            }

            return Task.CompletedTask;
        };

        var result = await Control().DisallowAsync(_supervisor, "Steve");

        Assert.True(result.ConfirmedOnDisk);
        Assert.Contains("allowlist remove \"Steve\"", _factory.Latest.Sent);
    }

    [Fact]
    public async Task Op_on_a_stopped_server_writes_the_permissions_file()
    {
        var result = await Control().SetOperatorAsync(
            _supervisor, "2535000000000001", "SteveExample", makeOperator: true);

        Assert.True(result.Changed);
        Assert.True(result.ConfirmedOnDisk);
        Assert.Equal(
            PermissionTier.Operator,
            PermissionsFile.Load(PermissionsPath).TierOf("2535000000000001"));
    }

    // Deop writes an explicit member rather than removing the override: on a
    // server whose default-player-permission-level is operator, removal would
    // hand the tier straight back.
    [Fact]
    public async Task Deop_writes_member_explicitly_rather_than_removing_the_row()
    {
        var seeded = PermissionsFile.Load(PermissionsPath);
        seeded.Set("111", PermissionTier.Operator);
        seeded.Save(PermissionsPath);

        var result = await Control().SetOperatorAsync(_supervisor, "111", "Steve", makeOperator: false);

        Assert.True(result.Changed);
        Assert.Equal(PermissionTier.Member, PermissionsFile.Load(PermissionsPath).TierOf("111"));
    }

    [Fact]
    public async Task Op_on_a_running_server_sends_the_command_and_watches_the_file()
    {
        await StartServerAsync();

        _factory.Latest.Answers = command =>
        {
            if (command.StartsWith("op ", StringComparison.Ordinal))
            {
                var file = PermissionsFile.Load(PermissionsPath);
                file.Set("111", PermissionTier.Operator);
                file.Save(PermissionsPath);
            }

            return Task.CompletedTask;
        };

        var result = await Control().SetOperatorAsync(_supervisor, "111", "Steve", makeOperator: true);

        Assert.True(result.ConfirmedOnDisk);
        Assert.Contains("op \"Steve\"", _factory.Latest.Sent);
    }

    // op on a player who is offline is refused by the server with no file
    // change. The honest report is "sent, not taken" — not success.
    [Fact]
    public async Task An_op_the_server_refuses_comes_back_unconfirmed()
    {
        await StartServerAsync();

        var result = await Control(TimeSpan.FromMilliseconds(400))
            .SetOperatorAsync(_supervisor, "111", "Offline Person", makeOperator: true);

        Assert.True(result.Changed);
        Assert.False(result.ConfirmedOnDisk);
        Assert.Contains("not online", result.Detail);
    }
}
