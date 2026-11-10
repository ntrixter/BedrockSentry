using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Sync;
using BedrockSentry.Services.Backups;

namespace BedrockSentry.Services.Sync;

/// <summary>
/// Which of a server's backups a world can be read from, newest first: what a sync copies
/// (<c>D103</c>) and what an area restore reads (<c>D108</c>).
/// </summary>
/// <remarks>
/// A backup is usable when it is not a safety backup, opens, holds a world and was not
/// recorded as damaged. A safety backup holds the world a sync or a restore replaced, which
/// is not a world anybody chose to keep.
/// </remarks>
public sealed class UsableBackups(WorldRestorer restorer, BackupHistory history)
{
    /// <summary>Each backup with why it cannot be read, lazily, so the first usable one opens no others.</summary>
    public async Task<IEnumerable<(RestorableBackup Backup, BackupCandidate Candidate)>> ListAsync(
        string instanceId, CancellationToken cancellationToken = default)
    {
        var records = await history.ReadAsync(instanceId, cancellationToken).ConfigureAwait(false);

        return restorer.ListBackups(instanceId).Select(b => (b, new BackupCandidate(b.Name, Unusable(records, b))));
    }

    /// <summary>The newest usable backup, or null.</summary>
    public async Task<RestorableBackup?> NewestAsync(string instanceId, CancellationToken cancellationToken = default) =>
        (await ListAsync(instanceId, cancellationToken).ConfigureAwait(false))
            .Where(c => c.Candidate.Unusable is null)
            .Select(c => c.Backup)
            .FirstOrDefault();

    private string? Unusable(IReadOnlyList<BackupRecord> records, RestorableBackup backup) =>
        SyncSource.IsSafetyBackup(records, backup.Name) ? "is a safety backup, taken before a restore or a sync"
        : DamagedBackups.WasDestroyed(records, backup.Name) ? "was recorded as damaged"
        : !restorer.ReadContents(backup.FullName).CanRestore ? "cannot be opened as a backup"
        : backup.IsFolder && !WorldRestorer.HoldsAWorld(backup.FullName) ? "holds no world"
        : null;
}
