using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Players;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// <c>D27</c>'s pre-migration copy. The thing under test is that a copy can be
/// taken <em>while a connection to the database is open</em>, which is the only
/// situation it is ever taken in — <c>MigrateAsync</c> reads the schema version
/// through a connection it holds for the whole method, and copies before it
/// starts changing anything.
/// </summary>
/// <remarks>
/// This used to be <c>File.Copy</c>, which opens the source with
/// <c>FILE_SHARE_READ</c> and is therefore refused by Windows whenever another
/// handle has the file open for writing. The failure was caught and logged, so
/// the safety net silently did not exist on the only platform that ships — and
/// could not be noticed, because the copy is gated on a schema version above
/// zero and only version 1 has ever existed.
///
/// Like the ZIP strategy's sharing fix, the assertion has teeth on Windows,
/// where CI runs; on Linux the old code passed too.
/// </remarks>
public sealed class PlayerDatabaseBackupTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-dbcopy-{Guid.NewGuid():N}");

    private readonly PlayerDatabase _database;

    public PlayerDatabaseBackupTests()
    {
        Directory.CreateDirectory(_directory);
        _database = new PlayerDatabase(
            Path.Combine(_directory, "players.db"), NullLogger<PlayerDatabase>.Instance);
        _database.MigrateAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string Destination => Path.Combine(_directory, "players.db.v1.bak");

    [Fact]
    public async Task Copies_the_database_while_a_connection_holds_it_open()
    {
        var store = new SqlitePlayerStore(_database);
        await store.RecordAsync(
            "survival",
            new PlayerEvent(PlayerEventKind.Connected, "SteveExample", "2535000000000001"),
            new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));

        // Exactly the shape MigrateAsync is in when it calls this: a live
        // read/write handle on the file being copied.
        await using var held = await _database.OpenAsync();

        await _database.CopyToAsync(Destination, held);

        Assert.True(File.Exists(Destination));

        // A real database, not merely a file of the right size — the other half
        // of why this is VACUUM INTO rather than a byte copy, which can tear.
        await using var copy = new SqliteConnection($"Data Source={Destination}");
        await copy.OpenAsync();

        await using var command = copy.CreateCommand();
        command.CommandText = "SELECT gamertag FROM players;";
        Assert.Equal("SteveExample", (string?)await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Overwrites_a_copy_from_a_previous_attempt()
    {
        await File.WriteAllTextAsync(Destination, "a stale backup from last time");

        await _database.CopyToAsync(Destination);

        await using var copy = new SqliteConnection($"Data Source={Destination}");
        await copy.OpenAsync();

        await using var command = copy.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(PlayerDatabase.TargetVersion, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }
}
