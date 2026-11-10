using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Backups;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Ending an orphaned server, and leaving a record that it happened
/// (<c>D56</c>, <c>T149</c>).
/// </summary>
/// <remarks>
/// A service rather than page code, because two things have to happen together
/// and only one of them is the kill. `D56`'s third requirement is that the
/// instance's backup timeline says an orphan was ended and when — so an operator
/// reading the Backups tab can tell which archives predate the incident and
/// which follow it. A page that terminated a process and then forgot to write
/// that line would satisfy the visible half of the requirement and lose the half
/// that matters weeks later.
/// </remarks>
public sealed class OrphanCleanup
{
    private readonly IOrphanInspector _inspector;
    private readonly BackupHistory _history;
    private readonly TimeProvider _time;
    private readonly ILogger<OrphanCleanup> _log;

    public OrphanCleanup(
        IOrphanInspector inspector,
        BackupHistory history,
        TimeProvider time,
        ILogger<OrphanCleanup> log)
    {
        _inspector = inspector;
        _history = history;
        _time = time;
        _log = log;
    }

    public Task<IReadOnlyList<OrphanedServer>> FindAsync(
        InstanceSupervisor supervisor, CancellationToken cancellationToken = default) =>
        _inspector.FindAsync(
            supervisor.InstanceId,
            supervisor.WorkingDirectory,
            supervisor.Status.ProcessId is { } pid ? [pid] : [],
            cancellationToken);

    /// <summary>
    /// Ends one orphan and records it. The record is written only when something
    /// was actually ended.
    /// </summary>
    /// <remarks>
    /// The kill happens first and the record second, which is the honest order:
    /// a failure to write history must not leave a process running, and an
    /// unrecorded kill is recoverable by reading the console. The reverse — a
    /// record of something that did not happen — is not recoverable at all,
    /// because it would tell somebody their backups sit on the wrong side of an
    /// incident that never occurred.
    /// </remarks>
    public async Task<OrphanTermination> TerminateAsync(
        InstanceSupervisor supervisor,
        OrphanedServer orphan,
        CancellationToken cancellationToken = default)
    {
        var result = await _inspector.TerminateAsync(
                supervisor.InstanceId, orphan.ProcessId, supervisor.WorkingDirectory, cancellationToken)
            .ConfigureAwait(false);

        supervisor.Console.Append(
            ConsoleOrigin.Notice,
            result.Succeeded ? result.Detail : $"ALERT: {result.Detail}",
            _time.GetUtcNow());

        if (!result.Succeeded)
        {
            return result;
        }

        var at = _time.GetUtcNow();

        // `T162`: the note names the newest archive that predates the kill rather
        // than pointing "above this line", which was the wrong direction in a
        // newest-first table. Read before the append, so the entry being written
        // cannot count as its own predecessor. Best effort — a note that cannot
        // name an archive still says the rest, and failing to read history is not
        // a reason to leave a killed process unrecorded.
        string? lastArchive = null;

        try
        {
            lastArchive = await _history.LastArchiveNameAsync(supervisor.InstanceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(
                ex, "Could not read {InstanceId}'s backup history to name the last archive",
                supervisor.InstanceId);
        }

        try
        {
            await _history
                .AppendAsync(
                    supervisor.InstanceId,
                    new BackupRecord(
                        at,
                        at,
                        BackupReason.Manual,
                        OrphanText.IncidentNote(orphan, lastArchive),
                        Skipped: true,
                        SkipReason: "Not a backup — an orphaned server was ended.",
                        Succeeded: false,
                        Strategies: []),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Recorded on the console above either way. Losing the timeline entry
            // is worth a warning, not a failure of an operation that already
            // succeeded.
            _log.LogWarning(
                ex, "Ended an orphan for {InstanceId} but could not record it in backup history",
                supervisor.InstanceId);
        }

        return result;
    }
}
