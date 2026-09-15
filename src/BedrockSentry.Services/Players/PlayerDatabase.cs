using BedrockSentry.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Players;

/// <summary>
/// Owns the player database file and the migrations that shape it
/// (<c>docs/design.md</c> § 7.2).
/// </summary>
/// <remarks>
/// Migrations are additive only, and a copy is taken before any of them run
/// (<c>D27</c>). Both halves are needed and neither is sufficient. The wrapper's
/// self-update can put an older binary back after a failed health check, and that
/// binary would then be reading a file a newer one migrated — the exact failure
/// rollback exists to prevent, arriving through the door rollback opened.
/// Additive-only keeps the old binary able to read the file at all; the copy is
/// what a rollback restores when the newer version wrote data the older one would
/// otherwise mangle.
///
/// Schema version lives in SQLite's own <c>user_version</c> pragma rather than a
/// table of our own, so a migration cannot fail halfway and leave the version
/// claiming something the tables do not match — the pragma is set inside the same
/// transaction as the statements it describes.
/// </remarks>
public sealed class PlayerDatabase
{
    /// <summary>
    /// One entry per schema version, in order. Index 0 produces version 1.
    /// Append only — editing one that has shipped changes a file somebody
    /// already has, which is the thing <c>D27</c> forbids.
    /// </summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE players (
            xuid             TEXT    NOT NULL PRIMARY KEY,
            gamertag         TEXT    NOT NULL,
            nickname         TEXT    NULL,
            notes            TEXT    NULL,
            first_seen_utc   TEXT    NOT NULL,
            is_wrapper_admin INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE player_sessions (
            id               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            xuid             TEXT    NOT NULL REFERENCES players(xuid) ON DELETE CASCADE,
            instance_id      TEXT    NOT NULL,
            connected_utc    TEXT    NOT NULL,
            disconnected_utc TEXT    NULL
        );

        CREATE TABLE player_instance_state (
            xuid           TEXT    NOT NULL REFERENCES players(xuid) ON DELETE CASCADE,
            instance_id    TEXT    NOT NULL,
            is_allowlisted INTEGER NOT NULL DEFAULT 0,
            op_level       INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (xuid, instance_id)
        );

        -- Playtime is a sum over sessions rather than a stored counter, so the
        -- aggregate runs on every page load and this index is what keeps that
        -- honest rather than merely principled.
        CREATE INDEX ix_player_sessions_xuid_instance
            ON player_sessions (xuid, instance_id);

        -- The roster, and D26's cleanup, both ask the same question: which rows
        -- for this instance are still open.
        CREATE INDEX ix_player_sessions_open
            ON player_sessions (instance_id) WHERE disconnected_utc IS NULL;
        """,
    ];

    private readonly ILogger<PlayerDatabase> _log;

    public PlayerDatabase(ISentryPaths paths, ILogger<PlayerDatabase> log)
        : this(System.IO.Path.Combine(paths.Data, "players.db"), log)
    {
    }

    public PlayerDatabase(string filePath, ILogger<PlayerDatabase> log)
    {
        FilePath = filePath;
        _log = log;
    }

    public string FilePath { get; }

    /// <summary>The version this build expects the file to be at.</summary>
    public static int TargetVersion => Migrations.Length;

    public string ConnectionString =>
        new SqliteConnectionStringBuilder { DataSource = FilePath }.ToString();

    /// <summary>Opens a connection with foreign keys on — they are off by default.</summary>
    /// <remarks>
    /// Off by default is a real trap here: <c>D29</c> erases a player by deleting
    /// one row and relying on the cascade to take the sessions and per-instance
    /// state with it. Without this pragma that delete silently leaves both behind,
    /// and "remove my data" quietly does not.
    /// </remarks>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    /// <summary>Brings the file up to <see cref="TargetVersion"/>, copying it first.</summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        var directory = System.IO.Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var current = await ReadVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (current >= Migrations.Length)
        {
            return;
        }

        // D27. Only for a file that already holds something: a fresh database has
        // nothing worth restoring, and a backup of an empty file would just be
        // noise in the data directory.
        if (current > 0)
        {
            await BackUpAsync(connection, current, cancellationToken).ConfigureAwait(false);
        }

        for (var version = current; version < Migrations.Length; version++)
        {
            await using var transaction =
                await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;

                // The pragma cannot be parameterised. The value is a loop index
                // over a private array, never anything from outside.
                command.CommandText = $"{Migrations[version]}\nPRAGMA user_version = {version + 1};";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _log.LogInformation("Player database migrated to version {Version}", version + 1);
        }
    }

    private async Task BackUpAsync(
        SqliteConnection connection, int fromVersion, CancellationToken cancellationToken)
    {
        try
        {
            await CopyToAsync($"{FilePath}.v{fromVersion}.bak", connection, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            // Worth continuing: refusing to migrate would leave the wrapper unable
            // to start at all, which is a worse outcome than a rollback that has
            // to fall back on the operator's own backup. Logged as an error rather
            // than a warning because what just failed is D27's safety net, and the
            // next thing this method does is the irreversible part.
            _log.LogError(ex, "Could not copy the player database before migrating");
        }
    }

    /// <summary>
    /// Writes a consistent copy of the database to <paramref name="destination"/>,
    /// overwriting it.
    /// </summary>
    /// <remarks>
    /// SQLite's own <c>VACUUM INTO</c> rather than <see cref="File.Copy(string, string, bool)"/>,
    /// for two reasons that both bite on Windows alone.
    ///
    /// <c>File.Copy</c> opens the source with <c>FILE_SHARE_READ</c>, which does
    /// not permit the write access an open connection to this database already
    /// holds — so the copy failed with a sharing violation every time, the
    /// <c>catch</c> above swallowed it, and <c>D27</c>'s pre-migration safety
    /// copy silently did not exist on the only platform this ships to. Nobody
    /// noticed because it is gated on a schema version above zero and only
    /// version 1 has ever existed: the first time it would have mattered is the
    /// first real migration, which is also the first time it would have run.
    ///
    /// And a byte-for-byte copy of a live SQLite file can be torn even when it
    /// does succeed. <c>VACUUM INTO</c> writes a transactionally consistent
    /// database through the connection that is already open, so there is no
    /// second handle to be refused and no half-written page to restore from.
    /// </remarks>
    public async Task CopyToAsync(
        string destination,
        SqliteConnection? connection = null,
        CancellationToken cancellationToken = default)
    {
        var owned = connection is null
            ? await OpenAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            // VACUUM INTO refuses a destination that exists, where File.Copy was
            // being told to overwrite one.
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            await using var command = (connection ?? owned)!.CreateCommand();
            command.CommandText = "VACUUM INTO $destination;";
            command.Parameters.AddWithValue("$destination", destination);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _log.LogInformation("Copied the player database to {Backup}", destination);
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> ReadVersionAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? 0 : Convert.ToInt32(value);
    }
}
