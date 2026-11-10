using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Commands;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Packages;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Packages;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Commands;

/// <summary>A pack a throwaway run loads, copied from where an instance has it.</summary>
/// <param name="Directory">The pack's folder.</param>
public sealed record ThrowawayPack(string Directory, string Uuid, PackVersion Version, PackPlacement Placement);

/// <summary>What one throwaway run is asked to do.</summary>
/// <param name="Executable">The server's file name inside the package: <c>bedrock_server.exe</c>.</param>
/// <param name="Packs">Packs to switch on in the throwaway world, in the order the instance lists them.</param>
/// <param name="LevelDat">
/// An instance's <c>level.dat</c> to start the throwaway world from, or null for a new
/// world. It carries the world's experiments, without which a script pack on a beta
/// module loads nothing.
/// </param>
/// <param name="Plan">
/// What to send once the server has started. It is handed the session and returns every
/// exchange it made, in order.
/// </param>
public sealed record ThrowawayRequest(
    ServerPackage Package,
    string Executable,
    IReadOnlyList<ThrowawayPack> Packs,
    string? LevelDat,
    Func<ThrowawaySession, CancellationToken, Task<IReadOnlyList<ConsoleExchange>>> Plan)
{
    /// <summary>
    /// A backup whose world the run starts on in place of a new one (<c>D108</c>), or null.
    /// The world keeps its own pack lists and the packs it carries; <see cref="Packs"/> go
    /// into the run's server folders, where the world's lists find them by uuid.
    /// <see cref="LevelDat"/> is not used with it.
    /// </summary>
    public RestorableBackup? World { get; init; }

    /// <summary>
    /// Reads the world after a graceful stop and before the run's folder is deleted, handed
    /// the world's folder; null to read nothing. A throw from it fails the run.
    /// </summary>
    public Func<string, CancellationToken, Task>? AfterStop { get; init; }
}

/// <summary>How a throwaway run went.</summary>
/// <param name="Transcript">Every exchange, or null when the run failed.</param>
/// <param name="Reason">Why it failed, worded to sit after a colon; null when it worked.</param>
/// <param name="Announced">What the server called itself at its start.</param>
public sealed record ThrowawayResult(
    IReadOnlyList<ConsoleExchange>? Transcript, string? Reason, TimeSpan Took, string? Announced)
{
    /// <summary>The run read what it was sent to read.</summary>
    public bool Succeeded => Transcript is not null;
}

/// <summary>
/// Runs a copy of a server package nobody plays on, asks it things, and deletes it
/// (<c>D105</c>).
/// </summary>
/// <remarks>
/// <para><strong>Nothing an administrator has is touched.</strong> The package is
/// extracted into its own folder under the data root, with its own world, its own two
/// ports, cheats on and LAN visibility off; an instance's packs and <c>level.dat</c> are
/// copied in, never read in place by the server. The folder is deleted when the run
/// ends, however it ends.</para>
///
/// <para><strong>Cheats on is the point.</strong> With <c>allow-cheats=false</c>,
/// BDS 1.26.52.3 lists 17 names in <c>help</c> and no add-on command; with it on, 91 and
/// every add-on command. A world started from a <c>level.dat</c> with cheats off still
/// lists all 44 pages, because <c>server.properties</c> wins (measured on Linux BDS
/// 1.26.52.3).</para>
///
/// <para><strong>A pack is switched on before the first start.</strong> A world folder
/// holding only <c>world_behavior_packs.json</c> and the pack loads it on the start that
/// creates the world. A pack whose dependency is missing is not loaded at all
/// (<c>Configured pack … was not found and was ignored</c>), so the caller sends the
/// dependencies with it.</para>
/// </remarks>
public sealed class ThrowawayServer
{
    /// <summary>The world every run creates.</summary>
    public const string LevelName = "completion";

    /// <summary>How long a start may take before the run gives up and kills it.</summary>
    public static readonly TimeSpan StartBudget = TimeSpan.FromMinutes(3);

    /// <summary>How long a graceful <c>stop</c> may take.</summary>
    public static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(30);

    private readonly ISentryPaths _paths;
    private readonly ServerPackageStore _packages;
    private readonly Func<string, string, IBedrockProcess> _launch;
    private readonly Func<IReadOnlySet<int>> _portsInUse;
    private readonly ILogger<ThrowawayServer> _log;

    private readonly TimeSpan _startBudget;
    private readonly TimeSpan _batchBudget;

    /// <summary>The folders of runs going now, which <see cref="SweepAsync"/> leaves alone.</summary>
    private readonly ConcurrentDictionary<string, byte> _live = new(StringComparer.Ordinal);

    /// <param name="launch">Starts the server: the executable's path and the folder it runs in.</param>
    /// <param name="portsInUse">Every port the run must leave alone: the instances' and any open now.</param>
    /// <param name="startBudget">How long a start may take; <see cref="StartBudget"/> unless a test says otherwise.</param>
    /// <param name="batchBudget">How long one batch may take; <see cref="ThrowawaySession.BatchBudget"/> unless a test says otherwise.</param>
    public ThrowawayServer(
        ISentryPaths paths,
        ServerPackageStore packages,
        Func<string, string, IBedrockProcess> launch,
        Func<IReadOnlySet<int>> portsInUse,
        ILogger<ThrowawayServer> log,
        TimeSpan? startBudget = null,
        TimeSpan? batchBudget = null)
    {
        _paths = paths;
        _packages = packages;
        _launch = launch;
        _portsInUse = portsInUse;
        _log = log;
        _startBudget = startBudget ?? StartBudget;
        _batchBudget = batchBudget ?? ThrowawaySession.BatchBudget;
    }

    /// <summary>Where the runs' folders live; anything here is a run's.</summary>
    /// <remarks>
    /// Under the instances folder, whose low label every folder beneath it inherits: a run
    /// is a server at low integrity like any other (<c>D126</c>), the
    /// service cannot label a folder itself, and the installer labels only that one. Under
    /// <see cref="ISentryPaths.Completion"/> every run was refused before it started
    /// (<c>T403</c>). The leading dot keeps it apart from an instance id.
    /// </remarks>
    public string RunsDirectory => Path.Combine(_paths.Instances, ".throwaway");

    /// <summary>Where runs were kept before <c>T403</c>; swept, never written.</summary>
    public string LegacyRunsDirectory => Path.Combine(_paths.Completion, "runs");

    /// <summary>The run. Never throws but for cancellation; a failure is in the result.</summary>
    public async Task<ThrowawayResult> RunAsync(ThrowawayRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var clock = Stopwatch.StartNew();
        var folder = await ClaimFolderAsync().ConfigureAwait(false);
        IBedrockProcess? process = null;
        ThrowawaySession? session = null;

        try
        {
            await _packages.ExtractAsync(request.Package, folder, cancellationToken).ConfigureAwait(false);

            var executable = Path.Combine(folder, request.Executable);
            if (!File.Exists(executable))
            {
                return Failed($"the package {request.Package.FileName} has no {request.Executable}");
            }

            MakeRunnable(executable);

            var (port, portV6) = ThrowawayPorts.Pick(_portsInUse());
            var level = request.World is { } backup ? PlaceBackupWorld(folder, backup) : LevelName;
            await PreparePropertiesAsync(folder, port, portV6, level, cancellationToken).ConfigureAwait(false);

            if (request.World is null)
            {
                PrepareWorld(folder, request);
            }
            else
            {
                PrepareServerPacks(folder, request.Packs);
            }

            process = _launch(executable, folder);
            session = new ThrowawaySession(process, _batchBudget);
            await process.StartAsync(cancellationToken).ConfigureAwait(false);

            if (await session.WaitUntilStartedAsync(_startBudget, cancellationToken).ConfigureAwait(false) is { } refusal)
            {
                return Failed(refusal);
            }

            IReadOnlyList<ConsoleExchange> transcript =
            [
                new(CaptureTranscript.StartLabel, session.StartLines),
                .. await request.Plan(session, cancellationToken).ConfigureAwait(false),
            ];

            var stopped = await StopAsync(process, cancellationToken).ConfigureAwait(false);

            if (request.AfterStop is { } read)
            {
                // A killed server's world is whatever was on disk when it died, so it is
                // not read: what a save answered may not be in it.
                if (!stopped)
                {
                    return Failed($"the server did not stop within {StopBudget.TotalSeconds:0} seconds, so its world was not read");
                }

                await read(Path.Combine(folder, "worlds", level), cancellationToken).ConfigureAwait(false);
            }

            return new ThrowawayResult(transcript, null, clock.Elapsed, session.Announced);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ThrowawayFailure failure)
        {
            return Failed(failure.Message);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A throwaway run of {Package} failed", request.Package.FileName);
            return Failed(ex.Message.TrimEnd('.'));
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }

                await process.DisposeAsync().ConfigureAwait(false);
            }

            await DeleteAsync(folder).ConfigureAwait(false);
            _live.TryRemove(folder, out _);
        }

        ThrowawayResult Failed(string reason) => new(null, reason, clock.Elapsed, session?.Announced);
    }

    /// <summary>Deletes what earlier runs left: a run the service was killed in the middle of.</summary>
    /// <remarks>
    /// A run going now is skipped. Completion sweeps a minute after the service starts, and
    /// an area restore started inside that minute lost its copy's world under it
    /// (<c>D108</c>, measured on the Linux Host).
    /// </remarks>
    public async Task SweepAsync()
    {
        foreach (var runs in new[] { RunsDirectory, LegacyRunsDirectory })
        {
            if (!Directory.Exists(runs))
            {
                continue;
            }

            foreach (var left in Directory.EnumerateDirectories(runs))
            {
                if (_live.ContainsKey(left))
                {
                    continue;
                }

                await DeleteAsync(left).ConfigureAwait(false);
            }
        }
    }

    private static void MakeRunnable(string executable)
    {
        // The package store writes files without their zip permissions, so the Linux
        // build's server arrives without its execute bit. Windows has no such bit.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executable,
                File.GetUnixFileMode(executable) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        }
    }

    private static async Task PreparePropertiesAsync(
        string folder, int port, int portV6, string level, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, ServerProperties.FileName);
        var document = ServerPropertiesDocument.Parse(
            File.Exists(path) ? await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false) : []);

        document.Set("allow-cheats", "true");
        document.Set("server-port", port.ToString(CultureInfo.InvariantCulture));
        document.Set("server-portv6", portV6.ToString(CultureInfo.InvariantCulture));
        document.Set("enable-lan-visibility", "false");
        document.Set("level-name", level);

        await document.SaveAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Extracts the backup's world into the run's <c>worlds</c> folder; returns its folder's name.</summary>
    private static string PlaceBackupWorld(string folder, RestorableBackup backup)
    {
        var worlds = Path.Combine(folder, "worlds");
        WorldRestorer.Extract(backup, worlds, EmptySnapshotStage.IntoAThrowaway);

        var (level, refusal) = WorldRestorer.WorldToTake(backup, NewServerLevel.WorldsIn(worlds));
        return level ?? throw new ThrowawayFailure(refusal!);
    }

    private static void PrepareServerPacks(string folder, IReadOnlyList<ThrowawayPack> packs)
    {
        var index = 0;

        foreach (var pack in packs)
        {
            if (PackPlacementLayout.ServerFolder(pack.Placement) is { } home)
            {
                CopyTree(pack.Directory, Path.Combine(folder, home, $"sentry{index++}"));
            }
        }
    }

    private static void PrepareWorld(string folder, ThrowawayRequest request)
    {
        var world = Path.Combine(folder, "worlds", LevelName);

        if (request.LevelDat is { } levelDat)
        {
            Directory.CreateDirectory(world);
            CopyShared(levelDat, Path.Combine(world, "level.dat"));
        }

        foreach (var placement in new[] { PackPlacement.Behavior, PackPlacement.Resource })
        {
            var packs = request.Packs.Where(p => p.Placement == placement).ToList();
            if (packs.Count == 0)
            {
                continue;
            }

            var home = Path.Combine(world, PackPlacementLayout.ServerFolder(placement)!);
            var list = WorldPackList.Empty;
            var index = 0;

            foreach (var pack in packs)
            {
                CopyTree(pack.Directory, Path.Combine(home, $"pack{index++}"));
                list = list.Enable(pack.Uuid, pack.Version).List;
            }

            File.WriteAllText(Path.Combine(world, PackPlacementLayout.WorldListFileName(placement)!), list.Write());
        }
    }

    // level.dat may be open in the instance's own running server, which rewrites it now
    // and then. Reading past its lock costs nothing here: a torn copy makes a throwaway
    // world that fails to start, and the run reports that.
    private static void CopyShared(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyShared(file, target);
        }
    }

    /// <summary>Stops the server; true when it exited on its own.</summary>
    private async Task<bool> StopAsync(IBedrockProcess process, CancellationToken cancellationToken)
    {
        try
        {
            await process.SendAsync("stop", cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(StopBudget, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException)
        {
            // The transcript is already in hand; the finally kills what did not stop.
            _log.LogInformation("A throwaway server did not stop within {Budget}; it is killed", StopBudget);
            return false;
        }
    }

    /// <summary>The lowest-numbered run folder no run holds, emptied of what an earlier run left.</summary>
    /// <remarks>
    /// Numbered rather than fresh each time: under <c>D124</c> Windows kept an AppContainer profile
    /// for every folder a server ran in, so a new name per run left one more profile in the
    /// service account's profile at every run (<c>T403</c>). A slot whose leftovers will not
    /// delete is passed over for the next.
    /// </remarks>
    private async Task<string> ClaimFolderAsync()
    {
        for (var slot = 0; ; slot++)
        {
            var folder = Path.Combine(RunsDirectory, "run" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (!_live.TryAdd(folder, 0))
            {
                continue;
            }

            await DeleteAsync(folder).ConfigureAwait(false);

            if (!Directory.Exists(folder))
            {
                return folder;
            }

            _live.TryRemove(folder, out _);
        }
    }

    // A server that has just exited can hold its files for a moment on Windows, so the
    // delete is tried a few times; what still will not go is left for the next sweep.
    private async Task DeleteAsync(string folder)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5)
                {
                    _log.LogWarning(ex, "Could not delete the throwaway folder {Folder}; the next start sweeps it", folder);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt)).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>A run that stopped for a reason it can name.</summary>
public sealed class ThrowawayFailure(string reason) : Exception(reason);

/// <summary>The two ports a throwaway run listens on.</summary>
public static class ThrowawayPorts
{
    /// <summary>Where the search starts: above the ports servers are usually given.</summary>
    public const int First = 19200;

    /// <summary>Where it stops.</summary>
    public const int Last = 19400;

    /// <summary>A free pair, two apart like an instance's, or a failure when none is.</summary>
    /// <param name="inUse">Every port taken, whether by an instance's file or by a socket open now.</param>
    public static (int Port, int PortV6) Pick(IReadOnlySet<int> inUse)
    {
        ArgumentNullException.ThrowIfNull(inUse);

        for (var port = First; port + 1 <= Last; port += 2)
        {
            if (!inUse.Contains(port) && !inUse.Contains(port + 1))
            {
                return (port, port + 1);
            }
        }

        throw new ThrowawayFailure($"no two ports were free between {First} and {Last}");
    }
}
