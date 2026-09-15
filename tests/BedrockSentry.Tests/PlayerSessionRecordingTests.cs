using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Players;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BedrockSentry.Tests;

/// <summary>
/// The whole path, end to end: a line off the server's console becomes a row in
/// the database (<c>D25</c>), and the process going away ends what it left open
/// (<c>D26</c>). Wired to a real store rather than a substitute, because the part
/// worth proving is that these two halves agree about what a session is.
/// </summary>
public sealed class PlayerSessionRecordingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-sessions-{Guid.NewGuid():N}");

    private readonly PlayerDatabase _database;
    private readonly SqlitePlayerStore _store;
    private readonly FakeProcessFactory _factory = new();
    private readonly FakeTimeProvider _time;
    private readonly InstanceSupervisor _supervisor;

    private static readonly DateTimeOffset Origin = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private const string Connected = "Player connected: SteveExample, xuid: 2535000000000001";
    private const string Spawned = "Player Spawned: SteveExample xuid: 2535000000000001, pfid: 0000000000000000";
    private const string Disconnected =
        "Player disconnected: SteveExample, xuid: 2535000000000001, pfid: 0000000000000000";

    public PlayerSessionRecordingTests()
    {
        Directory.CreateDirectory(_directory);

        _database = new PlayerDatabase(
            Path.Combine(_directory, "players.db"), NullLogger<PlayerDatabase>.Instance);
        _database.MigrateAsync().GetAwaiter().GetResult();
        _store = new SqlitePlayerStore(_database);

        _time = new FakeTimeProvider(Origin);
        _supervisor = new InstanceSupervisor(
            "survival",
            new InstanceDefinition { Name = "Survival" },
            _directory,
            _factory,
            _time,
            NullLogger<InstanceSupervisor>.Instance,
            players: _store);
    }

    public void Dispose()
    {
        _supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// Session writes are queued off the console pump, so the assertion has to
    /// wait for the queue rather than assume it has drained.
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.Fail(because);
    }

    [Fact]
    public async Task A_connect_on_the_console_opens_a_session()
    {
        await _supervisor.StartAsync();
        _factory.Latest.Emit(Connected);

        await WaitUntilAsync(
            async () => (await _store.OnlineAsync()).Count == 1,
            "the connect line never became a session");

        var player = Assert.Single(await _store.OnlineAsync());
        Assert.Equal("SteveExample", player.Gamertag);
        Assert.Equal("2535000000000001", player.Xuid);
        Assert.Equal("survival", player.InstanceId);
        Assert.Equal(Origin, player.ConnectedUtc);
    }

    // The line that follows every connect, carrying the same XUID in a shape the
    // parser must not accept. Taken as an arrival it would open a second session
    // and orphan the first.
    [Fact]
    public async Task The_spawn_line_does_not_open_a_second_session()
    {
        await _supervisor.StartAsync();
        _factory.Latest.Emit(Connected);
        _time.Advance(TimeSpan.FromSeconds(2));
        _factory.Latest.Emit(Spawned);

        await WaitUntilAsync(
            async () => (await _store.OnlineAsync()).Count == 1,
            "the connect line never became a session");

        await Task.Delay(100);
        Assert.Single(await _store.OnlineAsync());
    }

    [Fact]
    public async Task A_disconnect_on_the_console_closes_it()
    {
        await _supervisor.StartAsync();
        _factory.Latest.Emit(Connected);
        _time.Advance(TimeSpan.FromMinutes(21));
        _factory.Latest.Emit(Disconnected);

        await WaitUntilAsync(
            async () => (await _store.OnlineAsync()).Count == 0,
            "the disconnect line never closed the session");
    }

    // D26, the half the wrapper can observe. The exit is a real end time, so the
    // rows are closed at it rather than deleted.
    [Fact]
    public async Task An_exit_closes_whatever_the_server_left_open()
    {
        await _supervisor.StartAsync();
        _factory.Latest.Emit(Connected);

        await WaitUntilAsync(
            async () => (await _store.OnlineAsync()).Count == 1,
            "the connect line never became a session");

        _time.Advance(TimeSpan.FromHours(2));
        _factory.Latest.Exit(0);

        await WaitUntilAsync(
            async () => (await _store.OnlineAsync()).Count == 0,
            "the exit never closed the open session");

        // Closed, not dropped: the session happened and its length is known.
        await using var connection = await _database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT disconnected_utc FROM player_sessions;";

        var ended = (string?)await command.ExecuteScalarAsync();
        Assert.NotNull(ended);
        Assert.Equal(Origin.AddHours(2), DateTimeOffset.Parse(ended));
    }
}
