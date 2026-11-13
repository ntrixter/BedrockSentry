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
/// Four things write it: the orchestrator's runs, the restorer's safety backups, the
/// orphan cleanup's notes, and the trim and removal <c>D98</c> added. Each write reads,
/// changes and replaces the whole file, so they are serialised per instance here.
/// Reads are not: the instance Backups tab, the all-instances Backups page, the Status
/// page, the scheduler's skip check, both restorers and the orphan cleanup all read
/// the file directly, so a read can land inside the replace a write ends with.
/// <see cref="ReadAsync"/> is what makes that safe (<c>T305</c>).
/// </remarks>
public sealed class BackupHistory
{
    /// <summary>How many times a read tries to open the file before it gives up.</summary>
    private const int OpenAttempts = 10;

    /// <summary>The pause between those attempts.</summary>
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly ISentryPaths _paths;

    /// <summary>One writer at a time per instance, across every caller.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _writers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens the file for a read. A seam: the suite cannot hold a file the way Windows does.</summary>
    /// <remarks>
    /// Shares write and delete, so a read in progress never stands in the way of
    /// the writer's replace, which <see cref="AtomicFile"/> already retries from its
    /// side.
    /// </remarks>
    internal Func<string, Stream> OpenForRead { get; init; } = path =>
        new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, FileOptions.Asynchronous);

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
            await using var stream = await OpenAsync(file, cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                return [];
            }

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

    /// <summary>
    /// Opens the file, trying again for a moment while Windows says it is in use.
    /// Null when it is gone.
    /// </summary>
    /// <remarks>
    /// <para><strong>The retry is for the write's replace.</strong> A write ends
    /// with <c>MoveFileEx</c> replacing the file, and for that instant Windows holds
    /// it, so an open landing there throws a sharing violation (<c>T305</c>,
    /// measured by the suite under load on Windows). <c>rename()</c> on Linux has
    /// no such window. Ten tries 25 ms apart outlast the replace by a wide margin;
    /// a file still refused after them is refused for another reason, and that
    /// exception is the one the caller gets, never an empty history.</para>
    ///
    /// <para><strong>Gone is not in use.</strong> A file removed between the
    /// existence check and the open (an instance's backups being deleted) reads as
    /// no history, the answer the check itself would have given.</para>
    /// </remarks>
    private async Task<Stream?> OpenAsync(string file, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return OpenForRead(file);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < OpenAttempts)
            {
                await Task.Delay(OpenRetryDelay, cancellationToken).ConfigureAwait(false);
            }
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
        // An import is not this server's backup (`D110`).
        return records
            .Where(r => r.Succeeded && !r.Skipped && !r.IsImported)
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

            if (record.Skipped || !record.Succeeded || record.IsImported)
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

    public Task AppendAsync(
        string instanceId, BackupRecord record, CancellationToken cancellationToken = default) =>
        ChangeAsync(
            instanceId,
            records =>
            {
                records.Add(record);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Adds a row where its start puts it, rather than at the end (<c>D110</c>).
    /// </summary>
    /// <remarks>
    /// An imported backup carries the time it was taken, which can be years before the
    /// newest run. The file is oldest first, and readers take its last row as the newest.
    /// </remarks>
    public Task InsertAsync(
        string instanceId, BackupRecord record, CancellationToken cancellationToken = default) =>
        ChangeAsync(
            instanceId,
            records =>
            {
                var at = records.FindIndex(r => r.StartedUtc > record.StartedUtc);
                records.Insert(at < 0 ? records.Count : at, record);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Drops rows older than <paramref name="keepDays"/> that nothing still needs
    /// (<c>D98</c>, <see cref="BackupHistoryRetention.Keep"/>).
    /// </summary>
    /// <returns>How many rows went.</returns>
    public async Task<int> TrimAsync(
        string instanceId, int keepDays, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        var onDisk = OnDisk(instanceId);

        await ChangeAsync(
            instanceId,
            records =>
            {
                var kept = BackupHistoryRetention.Keep(records, now, keepDays, onDisk);
                removed = records.Count - kept.Count;

                if (removed == 0)
                {
                    return false;
                }

                records.Clear();
                records.AddRange(kept);
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        return removed;
    }

    /// <summary>
    /// Removes one run's row when an administrator asks (<c>D98</c>).
    /// </summary>
    /// <returns>
    /// False when the run is not in the history, or when a backup it describes is still
    /// on disk (<see cref="BackupHistoryRetention.CanRemove"/>).
    /// </returns>
    public async Task<bool> RemoveAsync(
        string instanceId, BackupRecord run, CancellationToken cancellationToken = default)
    {
        var removed = false;
        var onDisk = OnDisk(instanceId);

        await ChangeAsync(
            instanceId,
            records =>
            {
                var index = records.FindIndex(r => BackupHistoryRetention.SameRun(r, run));

                if (index < 0 || !BackupHistoryRetention.CanRemove(records[index], onDisk))
                {
                    return false;
                }

                records.RemoveAt(index);
                removed = true;
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        return removed;
    }

    /// <summary>
    /// The runs among <paramref name="records"/> an administrator may remove now, with
    /// the backup folder listed once for all of them.
    /// </summary>
    public IReadOnlyList<BackupRecord> Removable(string instanceId, IReadOnlyList<BackupRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var onDisk = OnDisk(instanceId);
        return records.Where(r => BackupHistoryRetention.CanRemove(r, onDisk)).ToList();
    }

    /// <summary>
    /// Whether a backup a row names is on disk, by its name in this instance's backup
    /// folder.
    /// </summary>
    /// <remarks>
    /// By name rather than by the path recorded, as retention decides (<c>T198</c>): a
    /// backups folder that has been moved keeps its names and not its paths. Only
    /// ZIP archives, imported worlds and folder copies count. A custom command's manifest file is left
    /// behind by design and is not a backup, so it keeps no row alive.
    /// </remarks>
    private Func<string, bool> OnDisk(string instanceId)
    {
        var directory = DirectoryFor(instanceId);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (Directory.Exists(directory))
            {
                names.UnionWith(Directory
                    .EnumerateFiles(directory, $"{ZipBackupStrategy.ArchivePrefix}*{ZipBackupStrategy.ArchiveSuffix}")
                    .Select(Path.GetFileName)
                    .OfType<string>());

                names.UnionWith(Directory
                    .EnumerateFiles(directory, $"{ZipBackupStrategy.ArchivePrefix}*{ZipBackupStrategy.McWorldSuffix}")
                    .Select(Path.GetFileName)
                    .OfType<string>());

                names.UnionWith(Directory
                    .EnumerateDirectories(directory, $"{FolderBackupStrategy.FolderPrefix}*")
                    .Select(Path.GetFileName)
                    .OfType<string>());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed says nothing is gone, so nothing is
            // trimmed or removed on its account: keep everything rather than guess.
            return _ => true;
        }

        return pathOrName =>
        {
            var trimmed = pathOrName.TrimEnd('\\', '/');
            return names.Contains(trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..]);
        };
    }

    /// <summary>A record with both times at offset zero, the same instants (<c>T447</c>).</summary>
    internal static BackupRecord InUtc(BackupRecord record) =>
        record.StartedUtc.Offset == TimeSpan.Zero && record.FinishedUtc.Offset == TimeSpan.Zero
            ? record
            : record with { StartedUtc = record.StartedUtc.ToUniversalTime(), FinishedUtc = record.FinishedUtc.ToUniversalTime() };

    /// <summary>
    /// Reads, changes and replaces the file, one writer per instance at a time.
    /// </summary>
    /// <param name="change">Edits the rows; returns false when nothing needs writing.</param>
    private async Task ChangeAsync(
        string instanceId, Func<List<BackupRecord>, bool> change, CancellationToken cancellationToken)
    {
        var writer = _writers.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(DirectoryFor(instanceId));

            var records = (await ReadAsync(instanceId, cancellationToken).ConfigureAwait(false)).ToList();

            if (!change(records))
            {
                return;
            }

            // T447. Every time written as UTC: a record built from a file's local stamp
            // carried the machine's offset (-06:00) in fields named Utc. The same instant,
            // so an older record read with its offset is rewritten as UTC unchanged.
            records = [.. records.Select(InUtc)];

            // Write-then-move, so a crash mid-write leaves the old history intact
            // rather than half a JSON document.
            await AtomicFile.WriteAsync(
                FileFor(instanceId),
                async temp =>
                {
                    await using var stream = File.Create(temp);
                    await JsonSerializer
                        .SerializeAsync(stream, records, Json, cancellationToken)
                        .ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.Release();
        }
    }
}
