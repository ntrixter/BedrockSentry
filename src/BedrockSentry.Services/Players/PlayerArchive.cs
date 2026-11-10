using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BedrockSentry.Core.Players;
using Microsoft.Data.Sqlite;

namespace BedrockSentry.Services.Players;

/// <summary>
/// Export to JSON and import back (<c>docs/design.md</c> § 7.4). The manual-editing
/// path, so SQLite can stay the only backend without anyone having to open it
/// with a database tool to fix a typo in a nickname.
/// </summary>
/// <remarks>
/// Import is upsert-only and never deletes: a row missing from the file means
/// the file is older or narrower than the database, which is overwhelmingly more
/// likely than somebody intending to erase a player by leaving them out. D29's
/// erase is the deliberate way to do that, and it asks first.
///
/// § 7.4 calls this a stop-the-service operation, and it means it. The export is
/// a snapshot; re-importing a stale one while servers are running writes old
/// session rows back over a database that has moved on.
/// </remarks>
public sealed partial class PlayerArchive
{
    private readonly PlayerDatabase _database;

    public PlayerArchive(PlayerDatabase database) => _database = database;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed record ArchiveProfile(
        string Xuid,
        string Gamertag,
        string? Nickname,
        string? Notes,
        bool IsWrapperAdmin,
        string FirstSeenUtc);

    /// <summary>One session. The fields after the first four arrived with schema 2 and are absent from older files.</summary>
    public sealed record ArchiveSession(
        string Xuid,
        string InstanceId,
        string ConnectedUtc,
        string? DisconnectedUtc,
        string? Gamertag = null,
        int? ExitX = null,
        int? ExitY = null,
        int? ExitZ = null,
        string? ExitDimension = null);

    /// <summary>One death the companion pack relayed. Schema 2.</summary>
    public sealed record ArchiveDeath(
        string Xuid,
        string InstanceId,
        string DiedUtc,
        string Cause,
        string? KillerType,
        string? KillerName,
        int X,
        int Y,
        int Z,
        string Dimension,
        string Message);

    /// <summary>One hello from the companion pack. Schema 2.</summary>
    public sealed record ArchiveCompanionReport(string InstanceId, string ReportedUtc, string PackVersion);

    public sealed record ArchiveInstanceState(
        string Xuid, string InstanceId, bool IsAllowlisted, int OpLevel);

    public sealed record Archive(
        int SchemaVersion,
        string ExportedUtc,
        IReadOnlyList<ArchiveProfile> Players,
        IReadOnlyList<ArchiveSession> Sessions,
        IReadOnlyList<ArchiveInstanceState> InstanceState,
        IReadOnlyList<ArchiveDeath>? Deaths = null,
        IReadOnlyList<ArchiveCompanionReport>? CompanionReports = null);

    public async Task<string> ExportAsync(
        DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        var players = new List<ArchiveProfile>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT xuid, gamertag, nickname, notes, is_wrapper_admin, first_seen_utc
                FROM players ORDER BY xuid;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                players.Add(new ArchiveProfile(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4) != 0,
                    reader.GetString(5)));
            }
        }

        var sessions = new List<ArchiveSession>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT xuid, instance_id, connected_utc, disconnected_utc,
                       gamertag, exit_x, exit_y, exit_z, exit_dimension
                FROM player_sessions ORDER BY xuid, connected_utc;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sessions.Add(new ArchiveSession(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : (int)reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : (int)reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : (int)reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }

        var state = new List<ArchiveInstanceState>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT xuid, instance_id, is_allowlisted, op_level
                FROM player_instance_state ORDER BY xuid, instance_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                state.Add(new ArchiveInstanceState(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2) != 0,
                    (int)reader.GetInt64(3)));
            }
        }

        var deaths = new List<ArchiveDeath>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT xuid, instance_id, died_utc, cause, killer_type, killer_name,
                       x, y, z, dimension, message
                FROM player_deaths ORDER BY xuid, instance_id, died_utc;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                deaths.Add(new ArchiveDeath(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    (int)reader.GetInt64(6),
                    (int)reader.GetInt64(7),
                    (int)reader.GetInt64(8),
                    reader.GetString(9),
                    reader.GetString(10)));
            }
        }

        var reports = new List<ArchiveCompanionReport>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT instance_id, reported_utc, pack_version
                FROM companion_reports ORDER BY instance_id, reported_utc;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                reports.Add(new ArchiveCompanionReport(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        return JsonSerializer.Serialize(
            new Archive(
                PlayerDatabase.TargetVersion,
                atUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                players,
                sessions,
                state,
                deaths,
                reports),
            Json);
    }

    /// <summary>A timestamp from a file, in the spelling the store writes, or null when it is not one.</summary>
    /// <remarks>
    /// <para><strong>An instant, not a spelling</strong> (<c>T301</c>). The import
    /// matched a session on the text of its <c>connectedUtc</c>, so a file an
    /// editor had rewritten, <c>2026-09-22T18:00:00.0000000+00:00</c> as
    /// <c>2026-09-22T20:00:00+02:00</c>, added every session a second time. The
    /// store also compares and orders these columns as text, which holds only
    /// while every row is in its one spelling, so the rewrite happens before the
    /// match and before the write.</para>
    ///
    /// <para><strong>ISO 8601 only</strong>, which is what the export writes and
    /// what an editor writing it back writes. <c>09/10/2026</c> reads as either
    /// month depending on who typed it, so it is refused rather than guessed. No
    /// offset is read as UTC, the zone the field names.</para>
    /// </remarks>
    internal static string? Instant(string? text) =>
        text is not null
        && IsoDateAndTime().IsMatch(text)
        && DateTimeOffset.TryParse(
            text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? SqlitePlayerStore.Stamp(at)
            : null;

    [GeneratedRegex(@"^\s*\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}")]
    private static partial Regex IsoDateAndTime();

    public async Task<PlayerImportResult> ImportAsync(
        string json, CancellationToken cancellationToken = default)
    {
        var archive = JsonSerializer.Deserialize<Archive>(json, Json)
            ?? throw new InvalidOperationException("The file is not a player export.");

        var warnings = new List<string>();

        if (archive.SchemaVersion > PlayerDatabase.TargetVersion)
        {
            // Forward-compatible only as far as it can be: additive migrations
            // (D27) mean a newer file's extra fields are simply absent here, and
            // saying so beats importing a subset in silence.
            warnings.Add(
                $"The file was written by schema version {archive.SchemaVersion}; this build "
                + $"understands {PlayerDatabase.TargetVersion}. Anything newer was ignored.");
        }

        var added = 0;
        var updated = 0;
        var sessionsAdded = 0;
        var stateRows = 0;
        var deathsAdded = 0;

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var tx = (SqliteTransaction)transaction;

        foreach (var profile in archive.Players ?? [])
        {
            if (string.IsNullOrWhiteSpace(profile.Xuid))
            {
                warnings.Add("Skipped a player with no XUID — it is the key everything hangs off.");
                continue;
            }

            var existed = await ScalarAsync(
                connection, tx, "SELECT COUNT(*) FROM players WHERE xuid = $xuid;",
                cancellationToken, ("$xuid", profile.Xuid)).ConfigureAwait(false);

            var firstSeen = Instant(profile.FirstSeenUtc);
            if (firstSeen is null && profile.FirstSeenUtc is not null)
            {
                warnings.Add(
                    $"{profile.Xuid}'s firstSeenUtc, '{profile.FirstSeenUtc}', is not a date and time, "
                    + "so it was read as absent.");
            }

            // first_seen_utc is written on insert and left alone on conflict.
            // D30 again: it is the one date nothing may move, and an import is
            // very much something.
            await ExecuteAsync(
                connection, tx,
                """
                INSERT INTO players (xuid, gamertag, nickname, notes, first_seen_utc, is_wrapper_admin)
                VALUES ($xuid, $gamertag, $nickname, $notes, $first, $admin)
                ON CONFLICT(xuid) DO UPDATE SET
                    gamertag = excluded.gamertag,
                    nickname = excluded.nickname,
                    notes = excluded.notes,
                    is_wrapper_admin = excluded.is_wrapper_admin;
                """,
                cancellationToken,
                ("$xuid", profile.Xuid),
                ("$gamertag", profile.Gamertag ?? profile.Xuid),
                ("$nickname", (object?)profile.Nickname ?? DBNull.Value),
                ("$notes", (object?)profile.Notes ?? DBNull.Value),
                ("$first", firstSeen ?? SqlitePlayerStore.Stamp(DateTimeOffset.UnixEpoch)),
                ("$admin", profile.IsWrapperAdmin ? 1 : 0))
                .ConfigureAwait(false);

            if (existed > 0) { updated++; } else { added++; }
        }

        foreach (var session in archive.Sessions ?? [])
        {
            if (string.IsNullOrWhiteSpace(session.Xuid) || string.IsNullOrWhiteSpace(session.InstanceId))
            {
                warnings.Add("Skipped a session with no player or no instance.");
                continue;
            }

            // An unreadable end is refused with the session rather than stored as
            // none, which the store reads as a player still online.
            if (Instant(session.ConnectedUtc) is not { } from)
            {
                warnings.Add(
                    $"Skipped a session for {session.Xuid} — its connectedUtc, '{session.ConnectedUtc}', "
                    + "is not a date and time.");
                continue;
            }

            string? to = null;
            if (session.DisconnectedUtc is not null && (to = Instant(session.DisconnectedUtc)) is null)
            {
                warnings.Add(
                    $"Skipped a session for {session.Xuid} — its disconnectedUtc, '{session.DisconnectedUtc}', "
                    + "is not a date and time.");
                continue;
            }

            // Matched on (xuid, instance, connected_utc) rather than on the row
            // id, which is an autoincrement local to whichever database wrote the
            // file and means nothing in this one. A player cannot connect to the
            // same server twice in the same instant, so the triple is a real key
            // — as an instant, which is why the file's text is respelled first
            // (`T301`).
            var already = await ScalarAsync(
                connection, tx,
                """
                SELECT COUNT(*) FROM player_sessions
                WHERE xuid = $xuid AND instance_id = $instance AND connected_utc = $from;
                """,
                cancellationToken,
                ("$xuid", session.Xuid),
                ("$instance", session.InstanceId),
                ("$from", from)).ConfigureAwait(false);

            // An exit position is taken whole or not at all: three coordinates
            // with no dimension name no place.
            var exit = session is { ExitX: { } x, ExitY: { } y, ExitZ: { } z, ExitDimension: { Length: > 0 } dimension }
                ? (x, y, z, dimension)
                : ((int, int, int, string)?)null;

            if (already > 0)
            {
                // Import never overwrites, but a session this database holds
                // without its schema-2 fields may take them from the file: filling
                // a blank is not replacing an answer.
                await ExecuteAsync(
                    connection, tx,
                    """
                    UPDATE player_sessions SET gamertag = $gamertag
                    WHERE xuid = $xuid AND instance_id = $instance AND connected_utc = $from
                      AND gamertag IS NULL;
                    """,
                    cancellationToken,
                    ("$gamertag", (object?)session.Gamertag ?? DBNull.Value),
                    ("$xuid", session.Xuid),
                    ("$instance", session.InstanceId),
                    ("$from", from))
                    .ConfigureAwait(false);

                if (exit is { } fill)
                {
                    await ExecuteAsync(
                        connection, tx,
                        """
                        UPDATE player_sessions
                        SET exit_x = $x, exit_y = $y, exit_z = $z, exit_dimension = $dimension
                        WHERE xuid = $xuid AND instance_id = $instance AND connected_utc = $from
                          AND exit_dimension IS NULL;
                        """,
                        cancellationToken,
                        ("$x", fill.Item1), ("$y", fill.Item2), ("$z", fill.Item3), ("$dimension", fill.Item4),
                        ("$xuid", session.Xuid),
                        ("$instance", session.InstanceId),
                        ("$from", from))
                        .ConfigureAwait(false);
                }

                continue;
            }

            var owner = await ScalarAsync(
                connection, tx, "SELECT COUNT(*) FROM players WHERE xuid = $xuid;",
                cancellationToken, ("$xuid", session.Xuid)).ConfigureAwait(false);

            if (owner == 0)
            {
                // The foreign key would reject it anyway; saying which player is
                // missing is more use than a constraint violation.
                warnings.Add($"Skipped sessions for {session.Xuid} — no such player in the file or the database.");
                continue;
            }

            await ExecuteAsync(
                connection, tx,
                """
                INSERT INTO player_sessions
                    (xuid, instance_id, connected_utc, disconnected_utc,
                     gamertag, exit_x, exit_y, exit_z, exit_dimension)
                VALUES ($xuid, $instance, $from, $to, $gamertag, $x, $y, $z, $dimension);
                """,
                cancellationToken,
                ("$xuid", session.Xuid),
                ("$instance", session.InstanceId),
                ("$from", from),
                ("$to", (object?)to ?? DBNull.Value),
                ("$gamertag", (object?)session.Gamertag ?? DBNull.Value),
                ("$x", (object?)exit?.Item1 ?? DBNull.Value),
                ("$y", (object?)exit?.Item2 ?? DBNull.Value),
                ("$z", (object?)exit?.Item3 ?? DBNull.Value),
                ("$dimension", (object?)exit?.Item4 ?? DBNull.Value))
                .ConfigureAwait(false);

            sessionsAdded++;
        }

        foreach (var death in archive.Deaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(death.Xuid) || string.IsNullOrWhiteSpace(death.InstanceId))
            {
                warnings.Add("Skipped a death with no player or no instance.");
                continue;
            }

            var owner = await ScalarAsync(
                connection, tx, "SELECT COUNT(*) FROM players WHERE xuid = $xuid;",
                cancellationToken, ("$xuid", death.Xuid)).ConfigureAwait(false);

            if (owner == 0)
            {
                warnings.Add($"Skipped a death for {death.Xuid} — no such player.");
                continue;
            }

            if (Instant(death.DiedUtc) is not { } diedAt)
            {
                warnings.Add(
                    $"Skipped a death for {death.Xuid} — its diedUtc, '{death.DiedUtc}', is not a date and time.");
                continue;
            }

            // (xuid, instance, died_utc) is the table's unique key, so a death
            // already here is skipped rather than counted twice — as an instant,
            // so the file's text is respelled first (`T301`).
            deathsAdded += await ExecuteAsync(
                connection, tx,
                """
                INSERT OR IGNORE INTO player_deaths
                    (xuid, instance_id, died_utc, cause, killer_type, killer_name, x, y, z, dimension, message)
                VALUES ($xuid, $instance, $at, $cause, $killerType, $killerName, $x, $y, $z, $dimension, $message);
                """,
                cancellationToken,
                ("$xuid", death.Xuid),
                ("$instance", death.InstanceId),
                ("$at", diedAt),
                ("$cause", death.Cause ?? string.Empty),
                ("$killerType", (object?)death.KillerType ?? DBNull.Value),
                ("$killerName", (object?)death.KillerName ?? DBNull.Value),
                ("$x", death.X),
                ("$y", death.Y),
                ("$z", death.Z),
                ("$dimension", death.Dimension ?? string.Empty),
                ("$message", death.Message ?? string.Empty))
                .ConfigureAwait(false);
        }

        foreach (var report in archive.CompanionReports ?? [])
        {
            if (string.IsNullOrWhiteSpace(report.InstanceId) || string.IsNullOrWhiteSpace(report.ReportedUtc))
            {
                continue;
            }

            // (instance, reported_utc) is the table's key, compared as an instant (`T301`).
            if (Instant(report.ReportedUtc) is not { } reportedAt)
            {
                warnings.Add(
                    $"Skipped a companion report for {report.InstanceId} — its reportedUtc, "
                    + $"'{report.ReportedUtc}', is not a date and time.");
                continue;
            }

            await ExecuteAsync(
                connection, tx,
                """
                INSERT OR IGNORE INTO companion_reports (instance_id, reported_utc, pack_version)
                VALUES ($instance, $at, $version);
                """,
                cancellationToken,
                ("$instance", report.InstanceId),
                ("$at", reportedAt),
                ("$version", report.PackVersion ?? string.Empty))
                .ConfigureAwait(false);
        }

        foreach (var row in archive.InstanceState ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.Xuid) || string.IsNullOrWhiteSpace(row.InstanceId))
            {
                continue;
            }

            var owner = await ScalarAsync(
                connection, tx, "SELECT COUNT(*) FROM players WHERE xuid = $xuid;",
                cancellationToken, ("$xuid", row.Xuid)).ConfigureAwait(false);

            if (owner == 0)
            {
                warnings.Add($"Skipped allowlist and OP state for {row.Xuid} — no such player.");
                continue;
            }

            await ExecuteAsync(
                connection, tx,
                """
                INSERT INTO player_instance_state (xuid, instance_id, is_allowlisted, op_level)
                VALUES ($xuid, $instance, $allow, $op)
                ON CONFLICT(xuid, instance_id) DO UPDATE SET
                    is_allowlisted = excluded.is_allowlisted,
                    op_level = excluded.op_level;
                """,
                cancellationToken,
                ("$xuid", row.Xuid),
                ("$instance", row.InstanceId),
                ("$allow", row.IsAllowlisted ? 1 : 0),
                ("$op", row.OpLevel))
                .ConfigureAwait(false);

            stateRows++;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new PlayerImportResult(added, updated, sessionsAdded, stateRows, warnings, deathsAdded);
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ScalarAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }
}
