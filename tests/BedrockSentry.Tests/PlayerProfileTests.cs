using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Players;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// Milestone 6's profile half: the fields an administrator owns, the per-instance
/// breakdown beneath them, and the export/import tool from docs/design.md § 7.4.
/// </summary>
public sealed class PlayerProfileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-profile-{Guid.NewGuid():N}");

    private readonly PlayerDatabase _database;
    private readonly SqlitePlayerStore _store;
    private readonly PlayerArchive _archive;

    private static readonly DateTimeOffset Noon = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static PlayerEvent Connect(string xuid, string tag) =>
        new(PlayerEventKind.Connected, tag, xuid);

    private static PlayerEvent Disconnect(string xuid, string tag) =>
        new(PlayerEventKind.Disconnected, tag, xuid);

    public PlayerProfileTests()
    {
        Directory.CreateDirectory(_directory);
        _database = new PlayerDatabase(
            Path.Combine(_directory, "players.db"), NullLogger<PlayerDatabase>.Instance);
        _database.MigrateAsync().GetAwaiter().GetResult();
        _store = new SqlitePlayerStore(_database);
        _archive = new PlayerArchive(_database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task An_unknown_xuid_has_no_profile()
    {
        Assert.Null(await _store.GetAsync("nobody", Noon));
    }

    [Fact]
    public async Task A_profile_breaks_activity_out_per_instance()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);
        await _store.RecordAsync("survival", Disconnect("111", "Ann"), Noon.AddHours(2));
        await _store.RecordAsync("creative", Connect("111", "Ann"), Noon.AddHours(3));

        var profile = await _store.GetAsync("111", Noon.AddHours(4));

        Assert.NotNull(profile);
        Assert.Equal(TimeSpan.FromHours(3), profile.TotalPlaytime);
        Assert.Equal(2, profile.TotalSessions);
        Assert.Equal("creative", profile.OnlineOn);

        // Ordered by playtime, so the server somebody actually lives on is first.
        Assert.Equal("survival", profile.Instances[0].InstanceId);
        Assert.Equal(TimeSpan.FromHours(2), profile.Instances[0].Playtime);
        Assert.False(profile.Instances[0].IsOnline);
        Assert.True(profile.Instances[1].IsOnline);
    }

    [Fact]
    public async Task Nickname_notes_and_the_admin_flag_are_editable()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);

        Assert.True(await _store.UpdateProfileAsync("111", "Annie", "Builds the spawn", true));

        var profile = await _store.GetAsync("111", Noon);
        Assert.Equal("Annie", profile!.Nickname);
        Assert.Equal("Builds the spawn", profile.Notes);
        Assert.True(profile.IsWrapperAdmin);
        Assert.Equal("Annie", profile.Display);

        // The gamertag is what the server calls them and is not editable here.
        Assert.Equal("Ann", profile.Gamertag);
    }

    // Blank and unset are the same thing: storing "" would make a cleared field
    // behave differently from one never filled in.
    [Fact]
    public async Task Clearing_a_nickname_stores_nothing_rather_than_an_empty_string()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);
        await _store.UpdateProfileAsync("111", "Annie", "note", false);
        await _store.UpdateProfileAsync("111", "   ", "", false);

        var profile = await _store.GetAsync("111", Noon);
        Assert.Null(profile!.Nickname);
        Assert.Null(profile.Notes);
        Assert.Equal("Ann", profile.Display);
    }

    [Fact]
    public async Task Editing_an_unknown_player_reports_that_it_did_nothing()
    {
        Assert.False(await _store.UpdateProfileAsync("nobody", "X", null, false));
    }

    [Fact]
    public async Task An_export_round_trips_into_an_empty_database()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);
        await _store.RecordAsync("survival", Disconnect("111", "Ann"), Noon.AddHours(1));
        await _store.UpdateProfileAsync("111", "Annie", "Builds the spawn", true);

        var json = await _archive.ExportAsync(Noon.AddHours(2));

        var otherDir = Path.Combine(_directory, "second");
        var other = new PlayerDatabase(
            Path.Combine(otherDir, "players.db"), NullLogger<PlayerDatabase>.Instance);
        await other.MigrateAsync();

        var result = await new PlayerArchive(other).ImportAsync(json);

        Assert.Equal(1, result.ProfilesAdded);
        Assert.Equal(1, result.SessionsAdded);
        Assert.Empty(result.Warnings);

        var restored = await new SqlitePlayerStore(other).GetAsync("111", Noon.AddHours(2));
        Assert.Equal("Annie", restored!.Nickname);
        Assert.True(restored.IsWrapperAdmin);
        Assert.Equal(TimeSpan.FromHours(1), restored.TotalPlaytime);
    }

    // § 7.4. A row missing from the file means the file is older or narrower than
    // the database, which is far likelier than somebody meaning to erase a player
    // by leaving them out. D29 is the deliberate way to do that.
    [Fact]
    public async Task Import_never_deletes_what_the_file_omits()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);
        var json = await _archive.ExportAsync(Noon);

        await _store.RecordAsync("survival", Connect("222", "Bob"), Noon.AddHours(1));

        await _archive.ImportAsync(json);

        Assert.NotNull(await _store.GetAsync("222", Noon.AddHours(2)));
    }

    [Fact]
    public async Task Importing_the_same_file_twice_adds_nothing_the_second_time()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);
        await _store.RecordAsync("survival", Disconnect("111", "Ann"), Noon.AddHours(1));

        var json = await _archive.ExportAsync(Noon.AddHours(2));

        await _archive.ImportAsync(json);
        var second = await _archive.ImportAsync(json);

        Assert.Equal(0, second.SessionsAdded);
        Assert.Equal(1, second.ProfilesUpdated);

        var profile = await _store.GetAsync("111", Noon.AddHours(2));
        Assert.Equal(1, profile!.TotalSessions);
    }

    // D30. An import is very much something, and it must not move first-seen.
    [Fact]
    public async Task Import_does_not_move_first_seen_on_a_player_already_here()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);
        var json = (await _archive.ExportAsync(Noon))
            .Replace(Noon.ToString("O"), Noon.AddYears(1).ToString("O"));

        await _archive.ImportAsync(json);

        var profile = await _store.GetAsync("111", Noon.AddYears(2));
        Assert.Equal(Noon, profile!.FirstSeenUtc);
    }

    // The shape a hand-edited export takes when somebody deletes a profile but
    // leaves its session rows behind. The foreign key would reject them anyway;
    // naming the player beats a constraint violation.
    [Fact]
    public async Task A_session_for_an_unknown_player_is_reported_rather_than_dropped_silently()
    {
        await _store.RecordAsync("survival", Connect("111", "Ann"), Noon);

        var json = (await _archive.ExportAsync(Noon))
            .Replace("\"players\"", "\"playersOriginal\"");

        var empty = new PlayerDatabase(
            Path.Combine(_directory, "third", "players.db"), NullLogger<PlayerDatabase>.Instance);
        await empty.MigrateAsync();

        var result = await new PlayerArchive(empty).ImportAsync(json);

        Assert.Equal(0, result.ProfilesAdded);
        Assert.Equal(0, result.SessionsAdded);
        Assert.Contains(result.Warnings, w => w.Contains("111"));
    }
}
