using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Players;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 7.2, against a real file rather than an in-memory fake — the
/// behaviour that matters here is cascade deletes, transactions and a pragma,
/// none of which a substitute would exercise.
/// </summary>
public sealed class SqlitePlayerStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-players-{Guid.NewGuid():N}");

    private readonly PlayerDatabase _database;
    private readonly SqlitePlayerStore _store;

    private static readonly DateTimeOffset Noon =
        new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static PlayerEvent Connect(string xuid = "2535000000000001", string tag = "SteveExample") =>
        new(PlayerEventKind.Connected, tag, xuid);

    private static PlayerEvent Disconnect(string xuid = "2535000000000001", string tag = "SteveExample") =>
        new(PlayerEventKind.Disconnected, tag, xuid);

    public SqlitePlayerStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _database = new PlayerDatabase(
            Path.Combine(_directory, "players.db"), NullLogger<PlayerDatabase>.Instance);
        _database.MigrateAsync().GetAwaiter().GetResult();
        _store = new SqlitePlayerStore(_database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await _database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, typeof(T));
    }

    [Fact]
    public async Task Migrating_an_already_current_file_changes_nothing()
    {
        Assert.Equal(PlayerDatabase.TargetVersion, await ScalarAsync<int>("PRAGMA user_version;"));

        await _database.MigrateAsync();

        Assert.Equal(PlayerDatabase.TargetVersion, await ScalarAsync<int>("PRAGMA user_version;"));
        Assert.False(File.Exists($"{_database.FilePath}.v1.bak"));
    }

    [Fact]
    public async Task A_connect_creates_the_profile_and_opens_a_session()
    {
        await _store.RecordAsync("survival", Connect(), Noon);

        var online = await _store.OnlineAsync();
        var player = Assert.Single(online);

        Assert.Equal("2535000000000001", player.Xuid);
        Assert.Equal("SteveExample", player.Gamertag);
        Assert.Equal("survival", player.InstanceId);
        Assert.Equal(Noon, player.ConnectedUtc);
    }

    [Fact]
    public async Task A_disconnect_closes_the_session_and_takes_them_off_the_roster()
    {
        await _store.RecordAsync("survival", Connect(), Noon);
        await _store.RecordAsync("survival", Disconnect(), Noon.AddMinutes(30));

        Assert.Empty(await _store.OnlineAsync());
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM player_sessions WHERE disconnected_utc IS NOT NULL;"));
    }

    // D30. The gamertag is most-recently-seen and moves; first_seen_utc is the one
    // date nothing may move, including a later connect under a changed name.
    [Fact]
    public async Task A_later_connect_refreshes_the_gamertag_but_never_first_seen()
    {
        await _store.RecordAsync("survival", Connect(tag: "OldName"), Noon);
        await _store.RecordAsync("survival", Disconnect(tag: "OldName"), Noon.AddMinutes(5));
        await _store.RecordAsync("survival", Connect(tag: "NewName"), Noon.AddDays(30));

        Assert.Equal("NewName", await ScalarAsync<string>("SELECT gamertag FROM players;"));
        Assert.Equal(
            Noon,
            DateTimeOffset.Parse(await ScalarAsync<string>("SELECT first_seen_utc FROM players;")));
    }

    // D26. A second connect with one still open means the first ended at a time
    // nobody recorded. Closing it at a guess would count the gap as playtime.
    [Fact]
    public async Task A_connect_over_an_open_session_drops_the_orphan_rather_than_guessing()
    {
        await _store.RecordAsync("survival", Connect(), Noon);
        await _store.RecordAsync("survival", Connect(), Noon.AddHours(3));

        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM player_sessions;"));
        Assert.Single(await _store.OnlineAsync());
        Assert.Equal(Noon.AddHours(3), (await _store.OnlineAsync())[0].ConnectedUtc);
    }

    // Already online when the wrapper started watching: there is no honest
    // connected_utc, so there is no row rather than an invented one.
    [Fact]
    public async Task A_disconnect_with_no_open_session_records_nothing()
    {
        await _store.RecordAsync("survival", Disconnect(), Noon);

        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM player_sessions;"));
    }

    // D26, the case where the wrapper saw the process go.
    [Fact]
    public async Task Closing_open_sessions_ends_them_at_the_exit_rather_than_now()
    {
        await _store.RecordAsync("survival", Connect(), Noon);
        await _store.RecordAsync("creative", Connect("999"), Noon);

        Assert.Equal(1, await _store.CloseOpenSessionsAsync("survival", Noon.AddHours(2)));

        var stillOn = Assert.Single(await _store.OnlineAsync());
        Assert.Equal("creative", stillOn.InstanceId);

        Assert.Equal(
            Noon.AddHours(2),
            DateTimeOffset.Parse(await ScalarAsync<string>(
                "SELECT disconnected_utc FROM player_sessions WHERE instance_id = 'survival';")));
    }

    // D26, the case where the wrapper itself died: no end time exists, so the row
    // goes rather than silently counting the outage.
    [Fact]
    public async Task Dropping_open_sessions_removes_them_entirely()
    {
        await _store.RecordAsync("survival", Connect(), Noon);
        await _store.RecordAsync("survival", Disconnect(), Noon.AddMinutes(10));
        await _store.RecordAsync("survival", Connect(), Noon.AddHours(1));

        Assert.Equal(1, await _store.DropOpenSessionsAsync("survival"));

        // The closed one survives: it is a real session with a real end.
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM player_sessions;"));
        Assert.Empty(await _store.OnlineAsync());
    }

    [Fact]
    public async Task The_list_includes_players_who_are_not_online()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);
        await _store.RecordAsync("survival", Disconnect("111", "Ann"), Noon.AddHours(1));
        await _store.RecordAsync("survival", Connect("222", "Bob"), Noon.AddHours(2));

        var rows = await _store.ListAsync(null, Noon.AddHours(3));

        Assert.Equal(2, rows.Count);
        Assert.False(rows.Single(r => r.Xuid == "111").IsOnline);
        Assert.True(rows.Single(r => r.Xuid == "222").IsOnline);
    }

    // Playtime is summed over sessions, and an open one counts up to the moment
    // asked rather than to nothing — a page left open must not freeze somebody's
    // total at the instant it loaded.
    [Fact]
    public async Task Playtime_sums_closed_sessions_and_counts_the_open_one_up_to_now()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);
        await _store.RecordAsync("survival", Disconnect("111", "Ann"), Noon.AddMinutes(90));
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon.AddHours(4));

        var row = Assert.Single(await _store.ListAsync(null, Noon.AddHours(5)));

        Assert.Equal(TimeSpan.FromMinutes(150), row.Playtime);
        Assert.Equal(TimeSpan.FromHours(1), row.CurrentSession(Noon.AddHours(5)));
        Assert.Equal(2, row.SessionCount);
    }

    // § 7.1. First-seen anywhere and first-seen on one server are different facts,
    // so the per-instance view derives its own rather than reusing D30's value.
    [Fact]
    public async Task Per_instance_first_seen_is_this_servers_own_not_the_global_one()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);
        await _store.RecordAsync("survival", Disconnect("111", "Ann"), Noon.AddHours(1));
        await _store.RecordAsync("creative", Connect("111", "Ann"), Noon.AddDays(10));

        var global = Assert.Single(await _store.ListAsync(null, Noon.AddDays(11)));
        var creative = Assert.Single(await _store.ListAsync("creative", Noon.AddDays(11)));

        Assert.Equal(Noon, global.FirstSeenUtc);
        Assert.Equal(Noon.AddDays(10), creative.FirstSeenUtc);
    }

    // Somebody playing elsewhere is offline as far as this server is concerned,
    // which is the truthful answer to the question this page asks.
    [Fact]
    public async Task A_player_online_elsewhere_reads_as_offline_on_this_instance()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);
        await _store.RecordAsync("survival", Disconnect("111", "Ann"), Noon.AddHours(1));
        await _store.RecordAsync("creative", Connect("111", "Ann"), Noon.AddHours(2));

        Assert.False(Assert.Single(await _store.ListAsync("survival", Noon.AddHours(3))).IsOnline);
        Assert.True(Assert.Single(await _store.ListAsync("creative", Noon.AddHours(3))).IsOnline);
    }

    [Fact]
    public async Task A_player_who_never_joined_this_server_is_not_on_its_page()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);
        await _store.RecordAsync("creative", Connect("222", "Bob"), Noon);

        Assert.Equal("111", Assert.Single(await _store.ListAsync("survival", Noon)).Xuid);
        Assert.Equal(2, (await _store.ListAsync(null, Noon)).Count);
    }

    [Fact]
    public async Task The_roster_can_be_scoped_to_one_instance()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);
        await _store.RecordAsync("creative", Connect("222", "Bob"), Noon);

        Assert.Equal(2, (await _store.OnlineAsync()).Count);
        Assert.Equal("Ann", Assert.Single(await _store.OnlineAsync("survival")).Gamertag);
    }

    // D29. One delete, and the cascade takes everything with it -- which only
    // works because the connection turns foreign keys on.
    [Fact]
    public async Task Forgetting_a_player_takes_their_sessions_and_state_too()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);
        await _store.RecordAsync("survival", Disconnect("111", "Ann"), Noon.AddHours(1));
        await _store.RecordAsync("creative", Connect("111", "Ann"), Noon.AddHours(2));
        await _store.RecordAsync("survival", Connect("222", "Bob"), Noon);

        await using (var connection = await _database.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO player_instance_state (xuid, instance_id, is_allowlisted, op_level) " +
                "VALUES ('111', 'survival', 1, 2);";
            await command.ExecuteNonQueryAsync();
        }

        // 1 profile + 2 sessions + 1 state row.
        Assert.Equal(4, await _store.ForgetPlayerAsync("111"));

        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM player_sessions WHERE xuid = '111';"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM player_instance_state WHERE xuid = '111';"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM players;"));
    }

    // Removing an instance takes its history, not the people. Someone known to
    // this install does not stop being known because a server went away.
    [Fact]
    public async Task Forgetting_an_instance_keeps_the_profiles()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);
        await _store.RecordAsync("survival", Disconnect("111", "Ann"), Noon.AddHours(1));
        await _store.RecordAsync("creative", Connect("111", "Ann"), Noon.AddHours(2));

        await using (var connection = await _database.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO player_instance_state (xuid, instance_id, is_allowlisted, op_level) " +
                "VALUES ('111', 'survival', 1, 2);";
            await command.ExecuteNonQueryAsync();
        }

        // One closed session plus the allowlist/OP row — both scoped to this
        // instance, and both go.
        Assert.Equal(2, await _store.CountAsync("survival"));
        Assert.Equal(2, await _store.ForgetAsync("survival"));

        Assert.Equal(0, await _store.CountAsync("survival"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM players;"));
        Assert.Single(await _store.OnlineAsync("creative"));
    }
}
