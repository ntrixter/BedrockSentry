using System.Collections.Concurrent;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// Fires each instance's scheduled backups (<c>docs/design.md</c> § 8.1). The clock,
/// nothing else: due-ness decisions live in <see cref="EvaluateAsync"/>, the
/// activity skip lives in the orchestrator, and the daylight-saving arithmetic
/// lives with the restart schedule it was solved for.
/// </summary>
public sealed class BackupScheduler(
    InstanceRegistry registry,
    BackupOrchestrator orchestrator,
    BackupHistory history,
    TimeProvider timeProvider,
    ILogger<BackupScheduler> logger) : BackgroundService
{
    /// <summary>
    /// A minute, not the restart scheduler's ten seconds: backups have no player
    /// countdown to land on time, so the tick only has to be finer than the
    /// shortest schedule anyone can set. That is now a minute rather than an
    /// hour, which this still satisfies — a minute schedule simply fires on the
    /// tick after it comes due.
    /// </summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    /// <summary>See <see cref="RestartScheduler.TimeZone"/>.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>
    /// When each instance last had a scheduled backup *attempted* — skipped
    /// counts. Anchoring on successes alone would make a failing strategy retry
    /// every tick, and a quiet server re-evaluate its skip every minute.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempt =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Next due instant per instance for daily schedules.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextDaily =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, bool> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When each instance last had an activity-triggered backup, for the
    /// minimum gap. Separate from <see cref="_lastAttempt"/> on purpose: a
    /// scheduled backup should not silently consume somebody's join trigger,
    /// and a busy evening of joins should not push the daily backup around.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastActivityBackup =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Instances whose player-activity event is already hooked.</summary>
    private readonly ConcurrentDictionary<string, bool> _subscribed =
        new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await EvaluateAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
    }

    /// <summary>
    /// One pass over every instance. Public for the RestartScheduler reason:
    /// this is the decision worth testing, and the loop above is only a timer.
    /// </summary>
    public async Task EvaluateAsync(CancellationToken cancellationToken = default)
    {
        foreach (var supervisor in registry.All)
        {
            try
            {
                // Here rather than at construction because instances can be
                // created while the wrapper runs (milestone 4). A tick's delay
                // before a brand-new instance's joins are watched is not worth a
                // registry-level event to avoid.
                Subscribe(supervisor);

                await ConsiderAsync(supervisor, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One instance's schedule must not stop the others being checked.
                logger.LogError(
                    ex, "Failed to evaluate the backup schedule for {InstanceId}",
                    supervisor.InstanceId);
            }
        }
    }

    private void Subscribe(InstanceSupervisor supervisor)
    {
        if (_subscribed.TryAdd(supervisor.InstanceId, true))
        {
            supervisor.PlayerActivity += OnPlayerActivity;
        }
    }

    /// <summary>
    /// A player arrived or left (<c>docs/design.md</c> § 8.1).
    /// </summary>
    /// <remarks>
    /// Runs on the console pump, so it decides quickly and hands the actual
    /// backup to a task of its own. Blocking here would stop the wrapper
    /// reading its server's output for the length of a world copy.
    /// </remarks>
    private void OnPlayerActivity(InstanceSupervisor supervisor, PlayerEvent playerEvent)
    {
        var settings = supervisor.Definition.Backup.OnPlayerActivity;

        var wanted = playerEvent.Kind switch
        {
            PlayerEventKind.Connected => settings.OnJoin,
            PlayerEventKind.Disconnected => settings.OnLeave,
            _ => false,
        };

        if (!wanted)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();

        // A player on a poor connection can join and drop a dozen times in a
        // minute, and each of those would otherwise be a hold-query-copy-resume
        // against a live server. The gap is what makes this a trigger rather
        // than a way to leave saves paused.
        if (_lastActivityBackup.TryGetValue(supervisor.InstanceId, out var last)
            && now - last < settings.MinimumGap)
        {
            return;
        }

        if (!_inFlight.TryAdd(supervisor.InstanceId, true))
        {
            return;
        }

        _lastActivityBackup[supervisor.InstanceId] = now;

        var note = playerEvent.Kind == PlayerEventKind.Connected
            ? $"{playerEvent.Gamertag} joined"
            : $"{playerEvent.Gamertag} left";

        _ = Task.Run(async () =>
        {
            try
            {
                await orchestrator
                    .RunAsync(supervisor, new BackupRequest(BackupReason.PlayerActivity, note))
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // A backup was already running for this instance. Nothing is
                // wrong and nothing is lost: whatever it archives is newer than
                // the event that raced it.
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex, "Player-activity backup failed for {InstanceId}", supervisor.InstanceId);
            }
            finally
            {
                _inFlight.TryRemove(supervisor.InstanceId, out _);
            }
        });
    }

    private async Task ConsiderAsync(InstanceSupervisor supervisor, CancellationToken cancellationToken)
    {
        var schedule = supervisor.Definition.Backup.Schedule;
        if (schedule.Mode == RestartScheduleMode.None)
        {
            return;
        }

        if (_inFlight.ContainsKey(supervisor.InstanceId))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        bool due;

        if (schedule.Mode is RestartScheduleMode.EveryNHours or RestartScheduleMode.EveryNMinutes)
        {
            if (schedule.Interval is not { } interval)
            {
                return;
            }

            var anchor = await AnchorAsync(supervisor.InstanceId, now, cancellationToken).ConfigureAwait(false);

            // Overdue means due now — a backup is not a restart, where jumping
            // to the next slot is the kinder behaviour. If the wrapper was down
            // past the interval, the world has unarchived changes and waiting
            // another period would stretch the exposure further.
            due = now - anchor >= interval;
        }
        else
        {
            var next = _nextDaily.GetOrAdd(
                supervisor.InstanceId,
                _ => schedule.NextDailyOccurrence(now, TimeZone) ?? DateTimeOffset.MaxValue);

            due = now >= next;
        }

        if (!due)
        {
            return;
        }

        if (!_inFlight.TryAdd(supervisor.InstanceId, true))
        {
            return;
        }

        // Off the tick loop, like the restart scheduler: one instance's slow
        // strategy must not hold up every other instance's schedule.
        _ = RunAsync(supervisor, cancellationToken);
    }

    private async Task RunAsync(InstanceSupervisor supervisor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        _lastAttempt[supervisor.InstanceId] = now;
        _nextDaily.TryRemove(supervisor.InstanceId, out _);

        try
        {
            var record = await orchestrator
                .RunAsync(supervisor, new BackupRequest(BackupReason.Scheduled), cancellationToken)
                .ConfigureAwait(false);

            if (record.Skipped)
            {
                logger.LogInformation(
                    "Scheduled backup of {InstanceId} skipped: {Reason}",
                    supervisor.InstanceId, record.SkipReason);
            }
            else
            {
                logger.LogInformation(
                    "Scheduled backup of {InstanceId} {Outcome}",
                    supervisor.InstanceId, record.Succeeded ? "succeeded" : "failed");
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down; nothing to record.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled backup of {InstanceId} failed", supervisor.InstanceId);
        }
        finally
        {
            _inFlight.TryRemove(supervisor.InstanceId, out _);
        }
    }

    /// <summary>
    /// Where the interval counts from: the last attempt this run, else the last
    /// record in history, else now — so a fresh install waits one interval
    /// rather than backing up at first tick, before anyone has played at all.
    /// </summary>
    private async Task<DateTimeOffset> AnchorAsync(
        string instanceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_lastAttempt.TryGetValue(instanceId, out var known))
        {
            return known;
        }

        var records = await history.ReadAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var anchor = records.Count > 0 ? records[^1].FinishedUtc : now;

        _lastAttempt[instanceId] = anchor;
        return anchor;
    }
}
