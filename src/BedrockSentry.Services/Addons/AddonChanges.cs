using System.Collections.Concurrent;
using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Addons;

/// <summary>
/// Makes the Add-ons tab's changes, now on a server with no process and queued on one with
/// (<c>D85</c> (a)), and applies a server's queue once its process has exited.
/// </summary>
/// <remarks>
/// <para><strong>Why a queue.</strong> BDS rewrites a world's pack lists when it stops, so a
/// write made while it runs is discarded by its own exit, silently and at the moment it was
/// expected to take effect. The wrapper holds the one moment such a write is safe: after the
/// process has exited.</para>
///
/// <para><strong>Two moments, either order.</strong> The queue is applied when a server reaches
/// Stopped or Faulted, and again before every launch (<see cref="IBeforeLaunch"/>), because a
/// crash, a host kill, an orphan reclaim (<c>D56</c>) or a stop from outside the panel ends the
/// process without the panel's stop. Each change is planned again against the world as it is
/// then, and applying one twice is a no-op (<see cref="QueuedAddonChange"/>).</para>
///
/// <para><strong>One gate per server</strong> around every change and every application, so a
/// Start pressed while the queue is being written waits for it rather than launching into a
/// half-written list, and a change made while the queue applies cannot interleave with it.</para>
///
/// <para>A server something has claimed (<see cref="InstanceClaim"/>) is left alone: the job
/// holding it may be replacing the world, and the queue applies at its next stop or start.</para>
/// </remarks>
public sealed class AddonChanges(
    InstanceAddons addons,
    AddonLibrary library,
    AddonQueueStore queue,
    InstanceRegistry registry,
    TimeProvider time,
    ILogger<AddonChanges> log) : IBeforeLaunch, IHostedService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Makes a change now, or queues it when the server has a process (<see cref="AddonQueue.MustWait"/>).
    /// </summary>
    /// <remarks>
    /// A change to be queued is planned against the world as it is now first, so one that
    /// could never apply is refused while somebody is looking rather than failing later.
    /// </remarks>
    public async Task<AddonWrite> SubmitAsync(
        InstanceSupervisor supervisor, QueuedAddonChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(change);

        var gate = GateFor(supervisor.InstanceId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (supervisor.Status.ClaimedFor is { } purpose)
            {
                return AddonWrite.Refused(ClaimText.Refusal(supervisor.Definition.Name, purpose));
            }

            if (!AddonQueue.MustWait(supervisor.Status.State))
            {
                return await ApplyOneAsync(supervisor, change, fromQueue: false, cancellationToken).ConfigureAwait(false)
                    is { } refused
                    ? AddonWrite.Refused(refused)
                    : AddonWrite.Written;
            }

            var state = addons.Read(supervisor);

            if (!state.CanAct)
            {
                return AddonWrite.Refused(state.Reason ?? "This server's add-ons could not be read.");
            }

            if (Check(state, change) is { } why)
            {
                return AddonWrite.Refused(why);
            }

            var read = queue.Read(supervisor.InstanceId);

            if (!read.CanWrite)
            {
                return AddonWrite.Refused(AddonsTabText.QueueUnreadable(read.Unreadable!));
            }

            queue.Write(supervisor.InstanceId, AddonQueue.With(read.Changes, change));
            Say(supervisor, $"Queued until {supervisor.Definition.Name} stops: {change.Describe()}.");

            return AddonWrite.Queued;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Takes one change off a server's queue.</summary>
    /// <returns>Null when it is gone; otherwise why not.</returns>
    public async Task<string?> DiscardAsync(
        InstanceSupervisor supervisor, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supervisor);

        var gate = GateFor(supervisor.InstanceId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var read = queue.Read(supervisor.InstanceId);

            if (!read.CanWrite)
            {
                return AddonsTabText.QueueUnreadable(read.Unreadable!);
            }

            queue.Write(supervisor.InstanceId, AddonQueue.Without(read.Changes, id));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>The process is down and the start gate held, which is the moment the queue is safe to write.</remarks>
    public Task BeforeLaunchAsync(InstanceSupervisor supervisor, CancellationToken cancellationToken) =>
        ApplyQueueAsync(supervisor, launching: true, cancellationToken);

    /// <summary>
    /// Applies a server's queue, oldest first, writing the queue back after each change so a
    /// crash partway leaves only what is still to do.
    /// </summary>
    /// <param name="launching">
    /// True from <see cref="BeforeLaunchAsync"/>, where the state already says Starting and
    /// the process is not yet there.
    /// </param>
    /// <returns>What became of each change tried.</returns>
    public async Task<IReadOnlyList<(QueuedAddonChange Change, QueuedChangeOutcome Outcome)>> ApplyQueueAsync(
        InstanceSupervisor supervisor, bool launching = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supervisor);

        var gate = GateFor(supervisor.InstanceId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // A restart holds a claim from its stop to its start, and is the stop the queue
            // waits for (`T417`); a job that may replace the world is not.
            if ((!launching && AddonQueue.MustWait(supervisor.Status.State))
                || supervisor.Status.ClaimedFor is { } purpose && !ClaimText.OnlyRestarts(purpose))
            {
                return [];
            }

            var read = queue.Read(supervisor.InstanceId);

            if (read.Changes.Count == 0)
            {
                if (read.Unreadable is { } why)
                {
                    Say(supervisor, AddonsTabText.QueueUnreadable(why));
                }

                return [];
            }

            var results = new List<(QueuedAddonChange, QueuedChangeOutcome)>();
            var kept = new List<QueuedAddonChange>();
            var when = launching ? $"before {supervisor.Definition.Name} started" : $"now that {supervisor.Definition.Name} has stopped";

            for (var i = 0; i < read.Changes.Count; i++)
            {
                var change = read.Changes[i];
                string? refused;

                try
                {
                    refused = await ApplyOneAsync(supervisor, change, fromQueue: true, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    refused = ex.Message;
                }

                if (refused is null)
                {
                    results.Add((change, QueuedChangeOutcome.Applied));
                    Say(supervisor, $"Applied {when}: {change.Describe()}.");
                }
                else
                {
                    results.Add((change, QueuedChangeOutcome.Failed));
                    kept.Add(change with { Failure = refused });
                    Say(supervisor, $"Not applied {when}, and still queued: {change.Describe()}. {refused}");
                }

                queue.Write(supervisor.InstanceId, [.. kept, .. read.Changes.Skip(i + 1)]);
            }

            return results;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Throws away the queues of servers the config no longer names, which a removal that
    /// stopped partway or a server deleted from the config by hand leaves behind. The config's
    /// names rather than the running supervisors, so a server the registry passed over at
    /// start keeps what it had.
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.StatusChanged += OnStatusChanged;
        registry.Released += OnReleased;

        var known = registry.Options.Servers.Keys
            .Concat(registry.All.Select(s => s.InstanceId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var orphan in queue.Queued().Where(id => !known.Contains(id)))
        {
            queue.Forget(orphan);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        registry.StatusChanged -= OnStatusChanged;
        registry.Released -= OnReleased;
        return Task.CompletedTask;
    }

    /// <summary>A server retired or removed takes its queue with it (<see cref="QueuedChangeOutcome.Discarded"/>).</summary>
    private void OnReleased(string instanceId) => queue.Forget(instanceId);

    /// <summary>A server that has just lost its process, with something queued, gets its queue now.</summary>
    private void OnStatusChanged(InstanceSupervisor supervisor, InstanceStatus status)
    {
        if (status.State is not (InstanceState.Stopped or InstanceState.Faulted)
            || queue.Read(supervisor.InstanceId).Changes.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyQueueAsync(supervisor).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not apply the add-on queue of {InstanceId}", supervisor.InstanceId);
            }
        });
    }

    /// <summary>Why a change could not apply to the world as it is now, or null when it could.</summary>
    private string? Check(InstanceAddonState state, QueuedAddonChange change)
    {
        switch (change.Kind)
        {
            case QueuedAddonKind.Settings:
                var plan = PackSettingsSave.Plan(
                    state.Settings, state.SettingsState, change.Uuid!, change.SettingValues(), state.SchemaFor);

                return plan.CanWrite
                    ? null
                    : plan.Refusal == SettingsSaveRefusal.FileNotRead
                        ? "This world's add-on settings file could not be read, so nothing was queued for it."
                        : string.Join(" ", plan.Problems.Select(p => p.Detail));

            case QueuedAddonKind.Arrange:
                return null;

            default:
                return PlanFor(state, change) is { } listPlan && listPlan.Change is null
                    ? AddonText.Refusal(listPlan.Refusal)
                    : null;
        }
    }

    /// <summary>The list change a queued change plans to, or null for one that is not a list change.</summary>
    private AddonChangePlan? PlanFor(InstanceAddonState state, QueuedAddonChange change)
    {
        var uuid = change.Uuid!;

        return change.Kind switch
        {
            QueuedAddonKind.SwitchOn when PackVersion.Parse(change.Version).Version is { } version =>
                addons.PlanEnable(state, state.Registrations.For(change.Placement), change.Placement, uuid, version),

            QueuedAddonKind.SwitchOff when state.Registrations.Find(uuid) is { } on =>
                AddonChangePlanner.Disable(state.Registrations.For(on.Placement), on.Placement, uuid),

            QueuedAddonKind.SwitchOff => AddonChangePlan.Refused(AddonChangeRefusal.NotEnabled),

            QueuedAddonKind.ChangeVersion when state.Registrations.Find(uuid) is { } on
                                             && PackVersion.Parse(change.Version).Version is { } to =>
                AddonChangePlanner.Update(
                    state.Registrations.For(on.Placement), on.Placement, uuid, to, (u, v) => library.Find(u, v) is not null),

            QueuedAddonKind.ChangeVersion => AddonChangePlan.Refused(AddonChangeRefusal.NotEnabled),

            QueuedAddonKind.Arrange =>
                AddonChangePlanner.Arrange(state.Registrations.For(change.Placement), change.Placement, change.Order ?? []),

            QueuedAddonKind.SwitchOn => AddonChangePlan.Refused(AddonChangeRefusal.NotInLibrary),

            _ => null,
        };
    }

    /// <summary>
    /// Writes one change to the world, with what follows it: the companion's resources after
    /// the companion (<c>D109</c>) and the instance's holds after a version is chosen or a pack
    /// switched off (<c>D112</c>).
    /// </summary>
    /// <param name="fromQueue">
    /// True when applying a queued change, where one that is already true is done: a change
    /// applied after a stop is applied again before the next start if the queue could not be
    /// written in between.
    /// </param>
    /// <returns>Null when it is made; otherwise why not.</returns>
    private async Task<string?> ApplyOneAsync(
        InstanceSupervisor supervisor, QueuedAddonChange change, bool fromQueue, CancellationToken cancellationToken)
    {
        var state = addons.Read(supervisor);

        if (!state.CanAct)
        {
            return state.Reason ?? "This server's add-ons could not be read.";
        }

        if (change.Kind is QueuedAddonKind.Settings)
        {
            var saved = await addons.SaveSettingsAsync(
                supervisor, state, change.Uuid!, change.SettingValues(), cancellationToken).ConfigureAwait(false);

            return saved.Saved
                ? null
                : saved.Reason ?? string.Join(" ", saved.Problems.Select(p => p.Detail));
        }

        if (PlanFor(state, change) is not { } plan)
        {
            return $"{change.Describe()} is not a change this knows how to make.";
        }

        if (plan.Change is not { } planned)
        {
            var alreadyTrue = plan.Refusal switch
            {
                AddonChangeRefusal.AlreadyAtThatVersion => change.Kind is QueuedAddonKind.SwitchOn or QueuedAddonKind.ChangeVersion,
                AddonChangeRefusal.NotEnabled => change.Kind is QueuedAddonKind.SwitchOff,
                AddonChangeRefusal.AlreadyInThatOrder => true,
                _ => false,
            };

            return fromQueue && alreadyTrue ? null : AddonText.Refusal(plan.Refusal);
        }

        if (await addons.ApplyAsync(supervisor, state, planned, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        string? after = null;

        if (change.Uuid is { } uuid && CompanionPack.Is(uuid))
        {
            after = await addons.FollowCompanionAsync(supervisor, cancellationToken).ConfigureAwait(false);
        }

        return after ?? await RecordHoldsAsync(supervisor, change, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An older version than the library's newest holds the pack there, the newest lets it go,
    /// and a pack switched off is no longer held (<c>D112</c>). The companion follows the
    /// library whatever this says (<c>D106</c>).
    /// </summary>
    /// <returns>
    /// Null when written or nothing to write; otherwise why not. From the queue that keeps the
    /// change, and the next attempt finds it already true and writes the holds again.
    /// </returns>
    private async Task<string?> RecordHoldsAsync(
        InstanceSupervisor supervisor, QueuedAddonChange change, CancellationToken cancellationToken)
    {
        if (change.Uuid is not { } uuid || CompanionPack.Is(uuid))
        {
            return null;
        }

        var holds = supervisor.Definition.Addons.Holds;
        IReadOnlyList<AddonHold>? after = change.Kind switch
        {
            QueuedAddonKind.SwitchOn or QueuedAddonKind.ChangeVersion
                when PackVersion.Parse(change.Version).Version is { } chosen =>
                AddonHolds.AfterChoosing(holds, uuid, chosen, Newest(uuid)),
            QueuedAddonKind.SwitchOff => AddonHolds.AfterSwitchingOff(holds, uuid),
            _ => null,
        };

        if (after is null)
        {
            return null;
        }

        try
        {
            await registry.SetAddonHoldsAsync(supervisor.InstanceId, after, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The change was made, but which add-ons are held could not be saved: {ex.Message}";
        }
    }

    private PackVersion? Newest(string uuid) =>
        library.List().FirstOrDefault(p => string.Equals(p.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
            is { Versions.Count: > 0 } shelved
            ? shelved.Newest.Version
            : null;

    private SemaphoreSlim GateFor(string instanceId) => _gates.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));

    private void Say(InstanceSupervisor supervisor, string notice)
    {
        log.LogInformation("{Instance}: {Notice}", supervisor.InstanceId, notice);
        supervisor.Console.Append(ConsoleOrigin.Notice, notice, time.GetUtcNow());
    }
}
