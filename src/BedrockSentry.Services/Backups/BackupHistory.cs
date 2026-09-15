using System.Text.Json;
using System.Text.Json.Serialization;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// The per-instance record of what backups happened, as a JSON file beside the
/// artifacts themselves.
/// </summary>
/// <remarks>
/// A file, not a database table: this is operational state in the same family as
/// the config file, and the restore picker (milestone 10) wants it readable next
/// to the archives it describes. SQLite stays the player-data backend and only
/// that — a second kind of data in it is how "the player database" stops meaning
/// anything.
///
/// Reads and writes are serialised per instance by the orchestrator, which is
/// the only writer.
/// </remarks>
public sealed class BackupHistory
{
    private readonly ISentryPaths _paths;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public BackupHistory(ISentryPaths paths) => _paths = paths;

    public string DirectoryFor(string instanceId) => Path.Combine(_paths.Backups, instanceId);

    private string FileFor(string instanceId) => Path.Combine(DirectoryFor(instanceId), "history.json");

    public async Task<IReadOnlyList<BackupRecord>> ReadAsync(
        string instanceId, CancellationToken cancellationToken = default)
    {
        var file = FileFor(instanceId);
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(file);
            return await JsonSerializer
                .DeserializeAsync<List<BackupRecord>>(stream, Json, cancellationToken)
                .ConfigureAwait(false) ?? [];
        }
        catch (JsonException)
        {
            // A truncated or hand-mangled history must not make backups
            // impossible — the history describes the archives, it does not gate
            // them. The unreadable file is set aside rather than overwritten, so
            // whatever it held can still be recovered by hand.
            File.Move(file, $"{file}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false);
            return [];
        }
    }

    /// <summary>When the last backup that actually ran and succeeded finished.</summary>
    public async Task<DateTimeOffset?> LastSuccessfulUtcAsync(
        string instanceId, CancellationToken cancellationToken = default)
    {
        var records = await ReadAsync(instanceId, cancellationToken).ConfigureAwait(false);

        // Skipped records do not count: a skip is the *absence* of a backup, and
        // treating it as one would let a quiet week push the "since" window
        // forward until a backup never happens again.
        return records
            .Where(r => r.Succeeded && !r.Skipped)
            .Select(r => (DateTimeOffset?)r.FinishedUtc)
            .Max();
    }

    /// <summary>
    /// The file name of the newest archive this instance actually has, or null
    /// when it has none.
    /// </summary>
    /// <remarks>
    /// <para>Here rather than in the one caller that needed it first, because the
    /// question — "which archive is the newest one an operator could restore" —
    /// belongs to the history, and a selection rule living in a private method of
    /// an untested class is where the next defect goes. <c>T168</c> will want the
    /// same answer.</para>
    ///
    /// <para>Three kinds of record are deliberately not eligible, and naming any
    /// of them as an archive would be worse than naming none: a skipped backup is
    /// the <em>absence</em> of one (same reasoning as
    /// <see cref="LastSuccessfulUtcAsync"/>), a failed one produced nothing, and an
    /// orphan-kill incident note (<c>D56</c> requirement 3) is a timeline marker
    /// rather than a backup. A record can also succeed overall while one strategy
    /// wrote nothing, so the artifact is taken from a strategy that succeeded and
    /// actually produced a path.</para>
    /// </remarks>
    public async Task<string?> LastArchiveNameAsync(
        string instanceId, CancellationToken cancellationToken = default)
    {
        var records = await ReadAsync(instanceId, cancellationToken).ConfigureAwait(false);

        // Oldest first on disk, so walk back from the end.
        for (var i = records.Count - 1; i >= 0; i--)
        {
            var record = records[i];

            if (record.Skipped || !record.Succeeded)
            {
                continue;
            }

            var artifact = record.Strategies
                .Where(r => r.Succeeded && !string.IsNullOrWhiteSpace(r.ArtifactPath))
                .Select(r => r.ArtifactPath!)
                .LastOrDefault();

            if (artifact is not null)
            {
                // Split on both separators rather than Path.GetFileName, which is
                // platform-dependent: these paths are written on Windows, and on
                // Linux a backslash is an ordinary character, so GetFileName hands
                // back the whole path. In production that only ever runs on
                // Windows and would be fine — but the suite runs on both, and a
                // test that passes on one platform and fails on the other is a trap
                // rather than a guard. It also means a path stored with the other
                // separator can never put a full directory tree into an operator's
                // timeline note.
                return artifact[(artifact.LastIndexOfAny(['\\', '/']) + 1)..];
            }
        }

        return null;
    }

    public async Task AppendAsync(
        string instanceId, BackupRecord record, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryFor(instanceId));

        var records = (await ReadAsync(instanceId, cancellationToken).ConfigureAwait(false)).ToList();
        records.Add(record);

        var file = FileFor(instanceId);

        // Write-then-move, so a crash mid-write leaves the old history intact
        // rather than half a JSON document.
        await AtomicFile.WriteAsync(
            file,
            async temp =>
            {
                await using var stream = File.Create(temp);
                await JsonSerializer
                    .SerializeAsync(stream, records, Json, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
