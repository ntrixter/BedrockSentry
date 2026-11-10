using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BedrockSentry.Core.Backups;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// Removes one backup artifact on an administrator's say-so, and clears what was
/// said about it.
/// </summary>
/// <remarks>
/// <para><strong>Separate from retention, which is the other thing that deletes
/// backups.</strong> Retention decides from names and a count and must never remove
/// a locked artifact (<c>T198</c>, <c>D68</c>); this is a person naming one
/// artifact, so a lock is not a refusal here — it is something the dialog says out
/// loud before the person confirms. Sharing one method between them would be
/// sharing an answer rather than an implementation, which is how the port
/// pre-flight came to give the panel the start path's answer (<c>T151</c> clause
/// 6).</para>
///
/// <para><strong>The artifact's state goes with it.</strong> A note or a lock left
/// behind in <c>artifacts.json</c> for a file that no longer exists is a row
/// retention still counts and nobody can clear from the panel — the orphan shape
/// <c>D63</c> is about, one file down. Cleared for every outcome that leaves the
/// artifact unrestorable, and kept for the one that does not.</para>
/// </remarks>
public sealed class BackupDeleter
{
    private readonly BackupHistory _history;
    private readonly BackupArtifactStore _artifacts;
    private readonly ILogger<BackupDeleter> _log;

    public BackupDeleter(
        BackupHistory history, BackupArtifactStore artifacts, ILogger<BackupDeleter> log)
    {
        _history = history;
        _artifacts = artifacts;
        _log = log;
    }

    /// <summary>
    /// Deletes one archive. A seam for the same reason the two strategies' prunes
    /// have one: the failure this reports cannot be produced on the platform the
    /// code is written on, and skipping the test instead of moving the decision is
    /// how the arm that can lie stays the arm nobody covers (<c>T176</c>).
    /// </summary>
    internal Action<string> RemoveFile { get; set; } = File.Delete;

    /// <summary>
    /// Deletes one folder copy, the whole tree. Not atomic, which is what makes
    /// <see cref="BackupDeletionState.PartlyDeleted"/> reachable at all.
    /// </summary>
    internal Action<string> RemoveDirectory { get; set; } =
        path => Directory.Delete(path, recursive: true);

    /// <summary>Whether the artifact is still on disk. A seam, so the rig above has one too.</summary>
    internal Func<string, bool, bool> StillThere { get; set; } =
        (path, isFolder) => isFolder ? Directory.Exists(path) : File.Exists(path);

    /// <summary>
    /// Removes one of this instance's backups.
    /// </summary>
    /// <remarks>
    /// The containment check runs before anything else and is not a formality: the
    /// path arrives from a page, and an artifact directory is the one place on the
    /// machine this wrapper is willing to delete a whole tree from. It compares
    /// resolved paths rather than the strings it was handed, because
    /// <c>..</c> segments and a mixture of separators are both legal spellings of a
    /// path that leaves the folder (<c>D43</c>, <c>D82</c>).
    /// </remarks>
    public async Task<BackupDeletionOutcome> DeleteAsync(
        string instanceId,
        RestorableBackup archive,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(archive);

        var root = _history.DirectoryFor(instanceId);

        if (!IsInside(root, archive.FullName))
        {
            // Never logged as a warning and left at that: this is the one arm that
            // says a path arrived which this instance has no business deleting.
            _log.LogError(
                "Refused to delete {Path}: it is not inside {Root}", archive.FullName, root);

            return new BackupDeletionOutcome(
                BackupDeletionState.Refused,
                archive.Name,
                "It is not inside this server's own backup folder, so nothing was deleted.");
        }

        var outcome = Remove(archive);

        if (!BackupDeletion.StillRestorable(outcome.State))
        {
            await ForgetAsync(instanceId, archive.Name, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    private BackupDeletionOutcome Remove(RestorableBackup archive)
    {
        // Asked before the delete as well as being a possible answer from it. The
        // race is real — retention runs on a timer — and the two arrive at the same
        // state by different routes, which is why AlreadyGone is a state rather
        // than a message written at one of them.
        if (!StillThere(archive.FullName, archive.IsFolder))
        {
            return new BackupDeletionOutcome(BackupDeletionState.AlreadyGone, archive.Name);
        }

        try
        {
            if (archive.IsFolder)
            {
                RemoveDirectory(archive.FullName);
            }
            else
            {
                RemoveFile(archive.FullName);
            }

            return new BackupDeletionOutcome(BackupDeletionState.Deleted, archive.Name);
        }
        catch (Exception ex) when (BackupDeletion.IsADeleteFailure(ex))
        {
            var state = BackupDeletion.From(ex, archive.IsFolder);

            // A folder that half went is data destroyed, and `D67`'s rule is that
            // such a sentence reaches the record rather than a log nobody reads —
            // so the level here is about the log, and the outcome carries the
            // sentence regardless (`T229`).
            _log.Log(
                state is BackupDeletionState.PartlyDeleted ? LogLevel.Error : LogLevel.Warning,
                ex,
                "Deleting backup {Path} ended as {State}",
                archive.FullName,
                state);

            return new BackupDeletionOutcome(state, archive.Name, ex.Message);
        }
    }

    /// <summary>
    /// Drops the note and the lock for an artifact that is no longer restorable.
    /// </summary>
    /// <remarks>
    /// Failing to clear the state must not turn a successful delete into a reported
    /// failure: the file is gone either way, and the leftover is one unreadable row
    /// in a JSON file rather than a missing backup. Logged, never fatal — the shape
    /// the strategies' prunes already use.
    /// </remarks>
    private async Task ForgetAsync(
        string instanceId, string name, CancellationToken cancellationToken)
    {
        try
        {
            await _artifacts
                .SetAsync(instanceId, name, new BackupArtifactState(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(
                ex, "Deleted backup {Name} but could not clear what was said about it", name);
        }
    }

    private static bool IsInside(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string resolvedRoot;
        string resolved;

        try
        {
            resolvedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            resolved = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path this process cannot even resolve is not one it is going to
            // delete a tree from.
            return false;
        }

        // The artifact sits directly in the instance's own backup folder, so the
        // parent is compared rather than a prefix. A prefix match would accept
        // `…\backups\inst\..\..\windows` on some spellings, and would also accept a
        // sibling folder whose name merely starts with this one's.
        var parent = Path.GetDirectoryName(resolved);

        return parent is not null
            && string.Equals(
                Path.TrimEndingDirectorySeparator(parent),
                resolvedRoot,
                StringComparison.OrdinalIgnoreCase);
    }
}
