using System.Text.Json;
using BedrockSentry.Core;
using BedrockSentry.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// The servers a drain for the wrapper's own update stopped, kept on disk so the wrapper
/// that starts after the update starts them again, auto-start or not (milestone 13.5).
/// </summary>
/// <remarks>
/// <para>Auto-start alone brought back only the servers set to start with the service, so
/// one started by hand stayed down after an update its operator may not have watched, and
/// its world kept the previous companion pack until somebody started it
/// (<see cref="Addons.CompanionPackUpgrade"/> moves the companion at a launch).</para>
///
/// <para>Written by <see cref="ManagedServerShutdown.DrainAsync"/>, which both update paths
/// drain through, and taken by <see cref="InstanceHostedService"/> at the next start,
/// whichever build that is. A start the drain's own caller makes, when the helper cannot be
/// launched, takes its ids off first.</para>
///
/// <para>A rolled-back update brings the same servers back (<c>T402</c>). The build under
/// test starts once before its health check fails and takes the list, and the rollback then
/// stops what it started. So a take moves the list to <see cref="TakenPath"/> with the taking
/// build's name, and a start by any other build takes it from there. The taking build's own
/// next start, and the next drain, drop it: by then the update it belonged to is settled.</para>
///
/// <para>A build is named by the version folder <c>current</c> points at, where there is one,
/// rather than by <see cref="BuildInfo.Identity"/>: one build's files unpacked under a second
/// identity, which is how the rollback is exercised (<c>T388</c> clause 2), report the same
/// identity from two folders, and the updater never switches a folder to itself.</para>
/// </remarks>
public sealed class ServersToStartAgain(
    ISentryPaths paths, ILogger<ServersToStartAgain> log, string? build = null)
{
    private readonly Lock _gate = new();
    private readonly string _identity = build ?? BuildInfo.Identity;

    /// <summary>Where the ids are kept.</summary>
    public string FilePath => Path.Combine(paths.Update, "start-again.json");

    /// <summary>Where a taken list is kept, with the name of the build that took it.</summary>
    public string TakenPath => Path.Combine(paths.Update, "start-again.taken.json");

    /// <summary>Adds <paramref name="instanceIds"/> to the servers the next start starts.</summary>
    public void Record(IReadOnlyCollection<string> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);

        lock (_gate)
        {
            // A new drain belongs to a new update; the last one's taken list is settled.
            Delete(TakenPath);

            if (instanceIds.Count == 0)
            {
                return;
            }

            Write([.. Read().Union(instanceIds, StringComparer.Ordinal)]);
        }
    }

    /// <summary>Takes <paramref name="instanceIds"/> off, once something has started them.</summary>
    public void Forget(IEnumerable<string> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);

        lock (_gate)
        {
            Write([.. Read().Except(instanceIds, StringComparer.Ordinal)]);
        }
    }

    /// <summary>
    /// The servers to start again: the recorded list, kept aside under this build's name,
    /// or else a list another build took, which a rollback has handed back.
    /// </summary>
    public IReadOnlyList<string> Take()
    {
        lock (_gate)
        {
            var ids = Read();

            if (ids.Count > 0)
            {
                WriteTaken(new TakenList(_identity, ids));
                Write([]);
                return ids;
            }

            var taken = ReadTaken();
            Delete(TakenPath);

            return taken is not null && !string.Equals(taken.TakenBy, _identity, StringComparison.Ordinal)
                ? taken.Ids
                : [];
        }
    }

    private sealed record TakenList(string TakenBy, List<string> Ids);

    private TakenList? ReadTaken()
    {
        try
        {
            return File.Exists(TakenPath)
                ? JsonSerializer.Deserialize<TakenList>(File.ReadAllText(TakenPath)) is { TakenBy: not null, Ids: not null } taken
                    ? taken
                    : null
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.LogWarning(ex, "Could not read {Path}; a rolled-back update's servers start only if set to", TakenPath);
            return null;
        }
    }

    private void WriteTaken(TakenList taken)
    {
        try
        {
            Directory.CreateDirectory(paths.Update);
            AtomicFile.WriteAllText(TakenPath, JsonSerializer.Serialize(taken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not write {Path}", TakenPath);
        }
    }

    private void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    private List<string> Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unread, the servers stay down as they did before this existed; the
            // service still starts.
            log.LogWarning(ex, "Could not read {Path}; servers stopped for an update start only if set to", FilePath);
            return [];
        }
    }

    private void Write(List<string> ids)
    {
        try
        {
            if (ids.Count == 0)
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }

                return;
            }

            Directory.CreateDirectory(paths.Update);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(ids));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not write {Path}", FilePath);
        }
    }
}
