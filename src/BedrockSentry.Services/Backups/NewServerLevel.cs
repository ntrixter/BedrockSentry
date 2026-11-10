using System.IO.Compression;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>What was done about which world a new server loads (<c>T335</c>).</summary>
public enum LevelOutcome
{
    /// <summary>Its <c>level-name</c> already named the world, or there was no world to name.</summary>
    Unchanged,

    /// <summary><c>level-name</c> was written to name the world.</summary>
    Set,

    /// <summary>
    /// <c>level-name</c> names no world here and could not be written, so Bedrock would
    /// generate a new world instead of loading the one that went in.
    /// </summary>
    NotSet,
}

/// <summary>Which world a new server loads, and how that was settled (<c>T335</c>).</summary>
/// <param name="Level">The world it loads, or the one it should load when <see cref="LevelOutcome.NotSet"/>.</param>
/// <param name="Worlds">How many worlds are in its <c>worlds</c> folder.</param>
/// <param name="Failure">Why <c>level-name</c> could not be written.</param>
public readonly record struct LevelSettled(LevelOutcome Outcome, string? Level, int Worlds, string? Failure = null)
{
    /// <summary>Whether starting the server loads the world that went in.</summary>
    public bool LoadsTheWorld => Outcome != LevelOutcome.NotSet;

    /// <summary>The sentence a summary carries, or empty when there is nothing to say.</summary>
    public string Clause() => Outcome switch
    {
        LevelOutcome.Unchanged when Worlds > 1 =>
            $"It holds {Worlds} worlds and loads '{Level}'; level-name on its Server properties tab "
            + "chooses another.",

        LevelOutcome.Unchanged => "",

        LevelOutcome.Set when Worlds > 1 =>
            $"It holds {Worlds} worlds, and its level-name was set to '{Level}' to load that one; "
            + "level-name on its Server properties tab chooses another.",

        LevelOutcome.Set => $"Its level-name was set to '{Level}' to load that world.",

        LevelOutcome.NotSet =>
            $"Its level-name could not be set to '{Level}': {Failure} Set level-name to '{Level}' on its "
            + "Server properties tab before starting it, or Bedrock generates a new world instead of "
            + "loading this one.",

        _ => throw new ArgumentOutOfRangeException(nameof(Outcome), Outcome, "Unhandled level outcome."),
    };
}

/// <summary>
/// The steps a new server made around a backup or a world file shares: which world it
/// loads, and its own name and ports over a configuration that came with the world.
/// </summary>
internal static class NewServerLevel
{
    /// <summary>The folders under <c>worlds</c> that hold a <c>level.dat</c>, by name.</summary>
    internal static IReadOnlyList<string> WorldsIn(string worldsDirectory) =>
        Directory.Exists(worldsDirectory)
            ?
            [
                .. Directory.EnumerateDirectories(worldsDirectory)
                    .Where(folder => File.Exists(Path.Combine(folder, "level.dat")))
                    .Select(folder => Path.GetFileName(folder))
                    .Order(StringComparer.Ordinal),
            ]
            : [];

    /// <summary>Points <c>level-name</c> at a world the server holds (<c>T335</c>).</summary>
    /// <param name="workingDirectory">The new server's folder.</param>
    /// <param name="backupLevelName">What the backup's own <c>server.properties</c> set, if it carried one.</param>
    /// <param name="read">A seam for the unreadable arm, which cannot be arranged as root.</param>
    /// <remarks>
    /// Never throws for a file it cannot read or write: by here the server exists, and the
    /// answer reaches the summary instead (<c>D67</c>).
    /// </remarks>
    internal static async Task<LevelSettled> SettleAsync(
        string workingDirectory,
        string? backupLevelName,
        ILogger log,
        CancellationToken cancellationToken,
        Func<string, string[]>? read = null)
    {
        var worlds = WorldsIn(Path.Combine(workingDirectory, "worlds"));

        if (worlds.Count == 0)
        {
            return new LevelSettled(LevelOutcome.Unchanged, null, 0);
        }

        var path = ServerProperties.PathFor(workingDirectory);
        var file = ServerProperties.ReadFile(path, read ?? File.ReadAllLines);

        if (file.State == PropertiesReadState.Unreadable)
        {
            // Nothing is known about which world it loads, so no claim that it is the right one.
            var wanted = NewServerWorld.LevelToSet(worlds, levelName: "", backupLevelName)
                         ?? NewServerWorld.DefaultLevelName;

            return new LevelSettled(
                LevelOutcome.NotSet, wanted, worlds.Count,
                $"{ServerProperties.FileName} could not be read: {file.Why}");
        }

        file.Values.TryGetValue("level-name", out var current);

        if (NewServerWorld.LevelToSet(worlds, current, backupLevelName) is not { } level)
        {
            return new LevelSettled(
                LevelOutcome.Unchanged,
                string.IsNullOrEmpty(current) ? NewServerWorld.DefaultLevelName : current,
                worlds.Count);
        }

        try
        {
            await ServerProperties.SetAsync(
                    path,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["level-name"] = level },
                    cancellationToken)
                .ConfigureAwait(false);

            log.LogInformation("Set level-name to {Level} in {Path}", level, path);
            return new LevelSettled(LevelOutcome.Set, level, worlds.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Could not set level-name to {Level} in {Path}", level, path);
            return new LevelSettled(LevelOutcome.NotSet, level, worlds.Count, ex.Message);
        }
    }

    /// <summary>
    /// Puts a new server's own name and ports over a <c>server.properties</c> that came
    /// with a backup or a world file.
    /// </summary>
    /// <remarks>
    /// <para><c>D42</c> settles that a restored copy is a faithful copy and this is the
    /// documented exception to it. The archive's file carries the original server's
    /// <c>server-port</c>, <c>server-portv6</c> and <c>server-name</c>; left alone, the copy
    /// binds the number the original is already on and the start pre-flight refuses it
    /// (<c>T149</c>).</para>
    ///
    /// <para><strong><c>transport</c> is deliberately NOT rewritten here.</strong> The port
    /// is a fact about this machine and cannot be shared; the transport is a fact about the
    /// server, and copying it is what faithful means.</para>
    /// </remarks>
    internal static async Task<CopiedProperties> ReapplyIdentityAsync(
        string workingDirectory,
        string name,
        int? port,
        IReadOnlyList<string> configFiles,
        ILogger log,
        CancellationToken cancellationToken)
    {
        if (!configFiles.Contains(ServerProperties.FileName, StringComparer.OrdinalIgnoreCase))
        {
            return CopiedProperties.NotRestored;
        }

        var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["server-name"] = [name],
        };

        if (port is { } p)
        {
            values["server-port"] = [p.ToString()];
            values["server-portv6"] = [(p + 1).ToString()];

            // `D127`. The original's play window would land the copy's play on the
            // original's UDP ports; unset, play goes to the copy's own server-port.
            values[ServerUdpPorts.Key] = [];
        }

        return await WriteIdentityAsync(workingDirectory, values, configFiles, log, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The keys that are a fact about this server on this machine rather than about its world:
    /// its name and every port it binds (<c>D127</c>).
    /// </summary>
    internal static readonly IReadOnlyList<string> IdentityKeys =
        ["server-name", "server-port", "server-portv6", ServerUdpPorts.Key];

    /// <summary>
    /// This server's own name and ports as its <c>server.properties</c> sets them now, every
    /// set line of each, an empty list for one it does not set; null when it has no file or
    /// it cannot be read.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>>? IdentityOf(string workingDirectory)
    {
        try
        {
            var path = ServerProperties.PathFor(workingDirectory);

            if (!File.Exists(path))
            {
                return null;
            }

            var document = ServerPropertiesDocument.Parse(File.ReadAllLines(path));

            return IdentityKeys.ToDictionary(k => k, document.ValuesOf, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts a server's own name and ports, read by <see cref="IdentityOf"/> before the restore,
    /// back over the <c>server.properties</c> a restore of another backup's configuration wrote
    /// (<c>D127</c>). A key the server did not set is commented out, so none of the backup's
    /// server's ports is left behind.
    /// </summary>
    /// <remarks>
    /// <c>D42</c>'s exception, on the in-place path: a backup of another server carries that
    /// server's ports, and two servers on one port is a pair of which one will not start
    /// (<c>T359</c> clause 4). A backup of this server can carry ports it has since left, and
    /// the ports it is on now are the ones the firewall rules and the players know.
    /// <c>server-udp-ports</c> is a port too: left out, the server took the other's play
    /// window while the summary said it kept its ports (<c>T359</c> clause 4, 2026-11-09).
    /// </remarks>
    internal static Task<CopiedProperties> KeepIdentityAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? identity,
        IReadOnlyList<string> configFiles,
        ILogger log,
        CancellationToken cancellationToken)
    {
        if (identity is null)
        {
            // Nothing was read to keep: the server had no file, or it could not be read.
            return Task.FromResult(configFiles.Contains(ServerProperties.FileName, StringComparer.OrdinalIgnoreCase)
                ? CopiedProperties.LeftAsTheOriginals
                : CopiedProperties.NotRestored);
        }

        return WriteIdentityAsync(workingDirectory, identity, configFiles, log, cancellationToken);
    }

    private static async Task<CopiedProperties> WriteIdentityAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<string>> values,
        IReadOnlyList<string> configFiles,
        ILogger log,
        CancellationToken cancellationToken)
    {
        if (!configFiles.Contains(ServerProperties.FileName, StringComparer.OrdinalIgnoreCase))
        {
            return CopiedProperties.NotRestored;
        }

        try
        {
            await ServerProperties.SetAllAsync(ServerProperties.PathFor(workingDirectory), values, cancellationToken)
                .ConfigureAwait(false);

            return CopiedProperties.Rewritten;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(
                ex, "Could not write the server's own port over its restored server.properties in {Directory}",
                workingDirectory);

            return CopiedProperties.LeftAsTheOriginals;
        }
    }

    /// <summary>
    /// Empties a half-written world folder so the server is empty rather than half a
    /// world, and says whether it managed it.
    /// </summary>
    /// <remarks>
    /// <c>Directory.Delete(recursive: true)</c> deletes as it walks and rethrows at the end
    /// (<c>T186</c>), so a failure here means some of the extraction is still on disk — a
    /// world Bedrock would open and read as corrupt. That is why the answer is carried into
    /// the summary instead of being logged: the two outcomes need different advice, and
    /// only one of them is safe to start.
    /// </remarks>
    internal static bool TryClear(string worldsDirectory, ILogger log)
    {
        try
        {
            if (Directory.Exists(worldsDirectory))
            {
                Directory.Delete(worldsDirectory, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not clear the half-written world at {Directory}", worldsDirectory);
            return false;
        }
    }

    /// <summary>The level folder a world with no folder of its own goes into: the one the server loads.</summary>
    internal static string LevelOf(string workingDirectory) =>
        ServerProperties.Read(ServerProperties.PathFor(workingDirectory)).TryGetValue("level-name", out var level)
        && level.Length > 0
            ? level
            : NewServerWorld.DefaultLevelName;

    /// <summary>The non-empty <c>level-name</c> in some lines of a <c>server.properties</c>, or null.</summary>
    internal static string? LevelNameIn(IEnumerable<string> lines) =>
        ServerProperties.Parse(lines).TryGetValue("level-name", out var level) && level.Length > 0 ? level : null;

    /// <summary>A text entry's lines.</summary>
    internal static List<string> LinesOf(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        var lines = new List<string>();

        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }
}
