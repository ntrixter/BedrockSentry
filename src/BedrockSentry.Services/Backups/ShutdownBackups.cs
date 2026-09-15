using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// Backup-on-shutdown (<c>docs/design.md</c> § 8.1): maps a stop's reason onto the
/// instance's per-caller toggles, and runs the static backup while the server
/// is down.
/// </summary>
/// <remarks>
/// One mapping, several callers — the scheduled restart now, the update flows
/// at milestones 13 and 13.5 — rather than each caller reading the toggles
/// itself and drifting. It never throws: a failed backup in the stop-start gap
/// is logged and noted on the console, but whatever went wrong, holding the
/// server down over it is not the answer.
/// </remarks>
public sealed class ShutdownBackups
{
    private readonly BackupOrchestrator _orchestrator;
    private readonly TimeProvider _time;
    private readonly ILogger<ShutdownBackups> _log;

    public ShutdownBackups(
        BackupOrchestrator orchestrator, TimeProvider time, ILogger<ShutdownBackups> log)
    {
        _orchestrator = orchestrator;
        _time = time;
        _log = log;
    }

    public async Task RunIfEnabledAsync(
        InstanceSupervisor supervisor, StopReason reason, CancellationToken cancellationToken = default)
    {
        var toggles = supervisor.Definition.Backup.OnShutdown;

        var enabled = reason switch
        {
            StopReason.ScheduledRestart => toggles.OnScheduledRestart,
            StopReason.WrapperUpdate => toggles.OnWrapperUpdate,
            StopReason.ServerUpdate => toggles.OnServerUpdate,
            // Deliberately not ServiceStop: Windows is waiting on the service
            // control manager, and a backup inside that budget risks the whole
            // wrapper being killed mid-copy (docs/design.md § 5.2).
            _ => false,
        };

        if (!enabled)
        {
            return;
        }

        try
        {
            var record = await _orchestrator
                .RunAsync(
                    supervisor,
                    new BackupRequest(BackupReason.Shutdown, $"On {Describe(reason)}"),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!record.Succeeded && !record.Skipped)
            {
                supervisor.Console.Append(
                    ConsoleOrigin.Notice,
                    "the shutdown backup failed — see the Backups tab; the server continues regardless",
                    _time.GetUtcNow());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex, "Backup-on-shutdown failed for {InstanceId}; continuing with the {Reason}",
                supervisor.InstanceId, reason);
        }
    }

    private static string Describe(StopReason reason) => reason switch
    {
        StopReason.ScheduledRestart => "scheduled restart",
        StopReason.WrapperUpdate => "wrapper update",
        StopReason.ServerUpdate => "server update",
        _ => reason.ToString(),
    };
}
