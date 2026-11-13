using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Structures;
using BedrockSentry.Core.Sync;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Worlds;

/// <summary>What a ticking-area action came to: whether BDS did it, what it said, and the list read after.</summary>
/// <param name="Said">BDS's answer, or why there is none.</param>
/// <param name="List">The world's ticking areas read after it, or null when they could not be read.</param>
public sealed record TickingAreaResult(bool Ok, string Said, TickingAreaList? List);

/// <summary>
/// Reads and changes the world's ticking areas through the server's console, and says how the
/// panel shows them in game (<c>D107</c>).
/// </summary>
/// <remarks>
/// <para>The Script API's <c>tickingAreaManager</c> sees only areas its own pack made, so the
/// areas are read with <c>tickingarea list all-dimensions</c> and changed with the commands an
/// operator would type, in the area's dimension (<see cref="TickingAreaCommands"/>). One command
/// at a time across servers, each answer collected from its first line to its last
/// (<see cref="ConsoleCollect"/>).</para>
///
/// <para>Nothing is sent while a sync runs on the server: it adds and removes its own ticking
/// area through the same console, and waits on answers of the same shape.</para>
/// </remarks>
public sealed class TickingAreas : IHostedService
{
    private readonly InstanceRegistry _registry;
    private readonly Func<string, bool> _syncRunning;
    private readonly TimeProvider _time;
    private readonly ILogger<TickingAreas> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TickingAreas(InstanceRegistry registry, WorldSync sync, TimeProvider time, ILogger<TickingAreas> log)
        : this(registry, sync.IsRunning, time, log)
    {
    }

    /// <summary>A seam: whether a sync runs on a server, without building <see cref="WorldSync"/>.</summary>
    internal TickingAreas(InstanceRegistry registry, Func<string, bool> syncRunning, TimeProvider time, ILogger<TickingAreas> log)
    {
        _registry = registry;
        _syncRunning = syncRunning;
        _time = time;
        _log = log;
    }

    /// <summary>How long an answer is waited for; BDS answered each within 50 ms on 1.26.52.3.</summary>
    internal TimeSpan AnswerWithin { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The sentence for a server that is not running.</summary>
    public const string NotRunning = TickingAreaConsole.NotRunning;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registry.CompanionHello += OnHello;
        _registry.TickShow += OnTickShow;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registry.CompanionHello -= OnHello;
        _registry.TickShow -= OnTickShow;
        return Task.CompletedTask;
    }

    /// <summary>Reads the world's ticking areas, keeping them on the server for its outlines.</summary>
    public async Task<TickingAreaResult> ReadAsync(InstanceSupervisor server)
    {
        ArgumentNullException.ThrowIfNull(server);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ReadLockedAsync(server).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds an area: one of <see cref="TickingAreaCommands"/>' adds.</summary>
    public Task<TickingAreaResult> AddAsync(InstanceSupervisor server, TickingAreaCommand add) =>
        ChangeAsync(server, async () => await RunAsync(server, add).ConfigureAwait(false));

    /// <summary>Removes an area and the panel's settings for it.</summary>
    public async Task<TickingAreaResult> RemoveAsync(InstanceSupervisor server, TickingArea area)
    {
        ArgumentNullException.ThrowIfNull(area);

        var result = await ChangeAsync(
            server, async () => await RunAsync(server, TickingAreaCommands.Remove(area.Name, area.Dimension)).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (result.Ok)
        {
            await _registry.SetTickingAreasAsync(server.InstanceId, TickingAreaEdits.Without(server.Definition.TickingAreas, area))
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// The panel's edit: Bedrock cannot change an area in place, so the old one is removed and
    /// <paramref name="add"/> sent; if BDS refuses the new one, the old one is put back as it
    /// was listed. Its settings follow it to its new name and dimension.
    /// </summary>
    public async Task<TickingAreaResult> ReplaceAsync(
        InstanceSupervisor server, TickingArea old, TickingAreaCommand add, string newName, BedrockDimension newDimension)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(add);

        var result = await ChangeAsync(server, async () =>
        {
            var removed = await RunAsync(server, TickingAreaCommands.Remove(old.Name, old.Dimension)).ConfigureAwait(false);
            if (!removed.Ok)
            {
                return removed;
            }

            var added = await RunAsync(server, add).ConfigureAwait(false);
            if (added.Ok)
            {
                return added;
            }

            var back = await RunAsync(server, TickingAreaCommands.Add(old)).ConfigureAwait(false);
            return new TickingAreaResult(
                false,
                back.Ok
                    ? $"{added.Said} {old.Name} was put back as it was."
                    : $"{added.Said} {old.Name} could not be put back: {back.Said}",
                null);
        }).ConfigureAwait(false);

        if (result.Ok)
        {
            var settings = server.Definition.TickingAreas;
            var index = TickingAreaOutlines.Index(settings, old);
            if (index >= 0)
            {
                var moved = TickingAreaEdits.Without(settings, old);
                moved.Add(new TickingAreaSettings
                {
                    Name = newName, Dimension = newDimension.Id,
                    ShowInGame = settings[index].ShowInGame, Color = settings[index].Color,
                });
                await _registry.SetTickingAreasAsync(server.InstanceId, moved).ConfigureAwait(false);
            }
        }

        return result;
    }

    /// <summary>Marks an area to be loaded before the world starts, or not.</summary>
    public Task<TickingAreaResult> SetPreloadAsync(InstanceSupervisor server, TickingArea area, bool preload) =>
        ChangeAsync(server, async () =>
            await RunAsync(server, TickingAreaCommands.SetPreload(area.Name, area.Dimension, preload)).ConfigureAwait(false));

    /// <summary>Shows or hides an area in game and sets its colour; sends the outlines.</summary>
    public Task SetDisplayAsync(InstanceSupervisor server, TickingArea area, bool show, string? color)
    {
        ArgumentNullException.ThrowIfNull(server);
        return _registry.SetTickingAreasAsync(
            server.InstanceId, TickingAreaEdits.WithDisplay(server.Definition.TickingAreas, area, show, color));
    }

    /// <summary>Runs one change under the gate, then reads the list again whatever it came to.</summary>
    private async Task<TickingAreaResult> ChangeAsync(InstanceSupervisor server, Func<Task<TickingAreaResult>> change)
    {
        ArgumentNullException.ThrowIfNull(server);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Refusal(server) is { } refused)
            {
                return new TickingAreaResult(false, refused, null);
            }

            var result = await change().ConfigureAwait(false);
            var read = await ReadLockedAsync(server).ConfigureAwait(false);
            _log.LogInformation("Ticking areas on {InstanceId}: {Said}", server.InstanceId, result.Said);
            return result with { List = read.List };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TickingAreaResult> ReadLockedAsync(InstanceSupervisor server)
    {
        if (Refusal(server) is { } refused)
        {
            return new TickingAreaResult(false, refused, null);
        }

        var answer = await AskAsync(server, TickingAreaCommands.List()).ConfigureAwait(false);
        if (answer.Lines is not { } lines)
        {
            return new TickingAreaResult(false, answer.Said, null);
        }

        var list = TickingAreaList.Parse([.. lines.Select(l => l.Message)]);
        if (list is null)
        {
            return new TickingAreaResult(false, $"BDS answered the list with \"{answer.Said}\".", null);
        }

        server.TickingAreas = new TickingAreaRead(list, _time.GetUtcNow());
        return new TickingAreaResult(true, answer.Said, list);
    }

    private string? Refusal(InstanceSupervisor server) =>
        _syncRunning(server.InstanceId) ? SyncAreaEdits.SyncRunning
        : server.Status.State != InstanceState.Running ? NotRunning
        : null;

    private async Task<TickingAreaResult> RunAsync(InstanceSupervisor server, TickingAreaCommand command)
    {
        var ran = await AskAsync(server, command).ConfigureAwait(false);
        return new TickingAreaResult(ran.Ok, ran.Said, null);
    }

    private Task<TickingAreaAnswer> AskAsync(InstanceSupervisor server, TickingAreaCommand command) =>
        TickingAreaConsole.AskAsync(server, command, AnswerWithin, _time);

    private void OnHello(InstanceSupervisor server)
    {
        if (server.Definition.TickingAreas.Any(s => s.ShowInGame))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if ((await ReadAsync(server).ConfigureAwait(false)).Ok)
                    {
                        await server.SendOutlinesAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not read the ticking areas of {InstanceId} after its start", server.InstanceId);
                }
            });
        }
    }

    private void OnTickShow(InstanceSupervisor server, TickShowCommand command) =>
        _ = Task.Run(() => HandleTickShowAsync(server, command));

    /// <summary>Answers one <c>showtickingarea</c>; what the tests wait on.</summary>
    internal async Task HandleTickShowAsync(InstanceSupervisor server, TickShowCommand command)
    {
        TickingAreaEdit edit;

        try
        {
            var read = await ReadAsync(server).ConfigureAwait(false);
            if (read.List is not { } list)
            {
                edit = new TickingAreaEdit(null, false, [read.Said]);
            }
            else
            {
                edit = TickingAreaEdits.Show(list, server.Definition.TickingAreas, command);
                if (edit.Updated is { } updated)
                {
                    await _registry.SetTickingAreasAsync(server.InstanceId, updated).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "showtickingarea on {InstanceId} could not be saved", server.InstanceId);
            edit = new TickingAreaEdit(null, false, [$"BedrockSentry could not save that: {ex.Message}"]);
        }

        if (command.Player is not { } player)
        {
            foreach (var line in edit.Lines)
            {
                server.Console.Append(ConsoleOrigin.Notice, edit.Ok ? line : $"showtickingarea refused: {line}", _time.GetUtcNow());
            }

            return;
        }

        try
        {
            foreach (var reply in SyncAreaEdits.Replies(player, edit.Ok, edit.Lines))
            {
                await server.SendCommandAsync(reply).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not answer {Player}'s showtickingarea on {InstanceId}", player, server.InstanceId);
        }
    }
}
