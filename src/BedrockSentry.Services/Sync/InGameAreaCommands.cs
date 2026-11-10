using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Sync;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Sync;

/// <summary>
/// Answers <c>syncarea</c> typed in game or on the console (<c>D106</c> step 4): applies it to
/// the server's Sync Areas through <see cref="SyncAreaEdits"/>, saves them as the Sync tab
/// does, and answers whoever typed it. Starts <c>restorearea</c> (<c>D108</c>) and answers
/// whether it started; there is no confirmation, and the countdown before the stop is the
/// warning.
/// </summary>
/// <remarks>
/// A player is answered through the companion pack (<c>scriptevent bedrocksentry:reply</c>),
/// since only the pack can send a player a message; the console is answered on its own page.
/// One edit at a time across servers, so two quick commands each read what the other saved.
/// An edit is refused while a sync runs on that server, which read the areas as it started,
/// as the Sync tab is locked then.
/// </remarks>
public sealed class InGameAreaCommands : IHostedService
{
    private readonly InstanceRegistry _registry;
    private readonly Func<string, bool> _syncRunning;
    private readonly AreaRestore? _restore;
    private readonly TimeProvider _time;
    private readonly ILogger<InGameAreaCommands> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InGameAreaCommands(
        InstanceRegistry registry, WorldSync sync, AreaRestore restore, TimeProvider time, ILogger<InGameAreaCommands> log)
        : this(registry, sync.IsRunning, time, log, restore)
    {
    }

    /// <summary>A seam: whether a sync runs on a server, without building <see cref="WorldSync"/>.</summary>
    internal InGameAreaCommands(
        InstanceRegistry registry,
        Func<string, bool> syncRunning,
        TimeProvider time,
        ILogger<InGameAreaCommands> log,
        AreaRestore? restore = null)
    {
        _registry = registry;
        _syncRunning = syncRunning;
        _restore = restore;
        _time = time;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registry.AreaCommand += OnAreaCommand;
        _registry.RestoreCommand += OnRestoreCommand;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registry.AreaCommand -= OnAreaCommand;
        _registry.RestoreCommand -= OnRestoreCommand;
        return Task.CompletedTask;
    }

    /// <summary>Raised on the console pump, so the work goes to a task of its own.</summary>
    private void OnAreaCommand(InstanceSupervisor supervisor, SyncAreaCommand command) =>
        _ = Task.Run(() => HandleAsync(supervisor, command));

    private void OnRestoreCommand(InstanceSupervisor supervisor, AreaRestoreCommand command) =>
        _ = Task.Run(() => HandleRestoreAsync(supervisor, command));

    /// <summary>Starts one restore and answers whether it started.</summary>
    /// <remarks>
    /// A started restore says the rest on the console; a player hears only this, since the
    /// restart that follows disconnects them.
    /// </remarks>
    internal async Task HandleRestoreAsync(InstanceSupervisor supervisor, AreaRestoreCommand command)
    {
        SyncAreaEdit answer;

        try
        {
            if (_restore is null)
            {
                throw new InvalidOperationException("This wrapper cannot restore an area.");
            }

            var started = await _restore
                .StartAsync(supervisor.InstanceId, command.Target, command.Backup)
                .ConfigureAwait(false);

            answer = new SyncAreaEdit(null, true, [AreaRestoreText.Accepted(started.Title, started.BackupName)]);
            _log.LogInformation(
                "restorearea {Title} from {Backup} on {InstanceId} from {Who}",
                started.Title, started.BackupName, supervisor.InstanceId, command.Player ?? "the console");
        }
        catch (InvalidOperationException ex)
        {
            answer = new SyncAreaEdit(null, false, [ex.Message]);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "restorearea on {InstanceId} could not start", supervisor.InstanceId);
            answer = new SyncAreaEdit(null, false, [$"BedrockSentry could not start the restore: {ex.Message}"]);
        }

        // On the console a started restore's own notices say what it does.
        await AnswerAsync(supervisor, command.Player, answer, "restorearea", consoleWhenOk: false).ConfigureAwait(false);
    }

    /// <summary>Applies one command and answers it; what the console's notices and the tests wait on.</summary>
    internal async Task HandleAsync(InstanceSupervisor supervisor, SyncAreaCommand command)
    {
        SyncAreaEdit edit;
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (command.Action != SyncAreaAction.List && _syncRunning(supervisor.InstanceId))
            {
                edit = new SyncAreaEdit(null, false, [SyncAreaEdits.SyncRunning]);
            }
            else
            {
                edit = SyncAreaEdits.Apply(supervisor.Definition.Sync, command);

                if (edit.Updated is { } updated)
                {
                    await _registry.SetSyncAsync(supervisor.InstanceId, updated).ConfigureAwait(false);
                    _log.LogInformation(
                        "syncarea {Action} {Name} on {InstanceId} from {Who}",
                        command.Action, command.Name, supervisor.InstanceId, command.Player ?? "the console");
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "syncarea {Action} on {InstanceId} could not be saved", command.Action, supervisor.InstanceId);
            edit = new SyncAreaEdit(null, false, [$"BedrockSentry could not save the Sync Areas: {ex.Message}"]);
        }
        finally
        {
            _gate.Release();
        }

        await AnswerAsync(supervisor, command.Player, edit, "syncarea", consoleWhenOk: true).ConfigureAwait(false);
    }

    private async Task AnswerAsync(
        InstanceSupervisor supervisor, string? player, SyncAreaEdit edit, string verb, bool consoleWhenOk)
    {
        if (player is null)
        {
            if (edit.Ok && !consoleWhenOk)
            {
                return;
            }

            foreach (var line in edit.Lines)
            {
                supervisor.Console.Append(ConsoleOrigin.Notice, edit.Ok ? line : $"{verb} refused: {line}", _time.GetUtcNow());
            }

            return;
        }

        try
        {
            foreach (var reply in SyncAreaEdits.Replies(player, edit.Ok, edit.Lines))
            {
                await supervisor.SendCommandAsync(reply).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // A server that stopped since the command was typed has nobody to answer.
            _log.LogWarning(ex, "Could not answer {Player}'s {Verb} on {InstanceId}", player, verb, supervisor.InstanceId);
        }
    }
}
