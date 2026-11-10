using System.Text.Json;
using System.Text.Json.Serialization;
using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Addons;

/// <summary>A server's queue as read from disk (<c>D85</c>).</summary>
/// <param name="Changes">Oldest first; empty when nothing is queued or it could not be read.</param>
/// <param name="Unreadable">Why the file could not be read, or null when it was (or there is none).</param>
/// <remarks>
/// An unreadable queue is its own state, never an empty one: a queue read as empty and written
/// back would throw away changes somebody made and is waiting to see applied.
/// </remarks>
public sealed record AddonQueueRead(IReadOnlyList<QueuedAddonChange> Changes, string? Unreadable = null)
{
    public static readonly AddonQueueRead Empty = new([]);

    /// <summary>Whether the queue may be written over.</summary>
    public bool CanWrite => Unreadable is null;
}

/// <summary>
/// Where each server's queue of add-on changes is kept between a change made while it runs
/// and the moment its process has exited (<c>D85</c>): one small JSON per instance under the
/// data root, so it survives a wrapper restart, a crash and a host kill.
/// </summary>
/// <remarks>
/// Observed and intended state rather than configuration, so it is not in the config file the
/// administrator edits, as the version log is not. Writes go through <see cref="AtomicFile"/>.
/// </remarks>
public sealed class AddonQueueStore(ISentryPaths paths, ILogger<AddonQueueStore> log)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();

    /// <summary>Raised with the instance id after its queue is written or forgotten.</summary>
    public event Action<string>? Changed;

    private string Folder => Path.Combine(paths.Data, "addon-queue");

    private string FileFor(string instanceId) => Path.Combine(Folder, $"{instanceId}.json");

    /// <summary>The instance's queue.</summary>
    public AddonQueueRead Read(string instanceId)
    {
        var path = FileFor(instanceId);

        lock (_gate)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return AddonQueueRead.Empty;
                }

                return JsonSerializer.Deserialize<List<QueuedAddonChange>>(File.ReadAllText(path), Json) is { } changes
                    ? new AddonQueueRead(changes)
                    : new AddonQueueRead([], "The file holds no list of changes.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                log.LogWarning(ex, "Could not read the add-on queue of {InstanceId}", instanceId);
                return new AddonQueueRead([], ex.Message);
            }
        }
    }

    /// <summary>Replaces the instance's queue; an empty one deletes the file.</summary>
    public void Write(string instanceId, IReadOnlyList<QueuedAddonChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var path = FileFor(instanceId);

        lock (_gate)
        {
            if (changes.Count == 0)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                Directory.CreateDirectory(Folder);
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(changes, Json));
            }
        }

        Raise(instanceId);
    }

    /// <summary>
    /// Throws away a removed instance's queue, so a later instance given its id does not
    /// inherit changes nobody made to it (<see cref="QueuedChangeOutcome.Discarded"/>).
    /// </summary>
    /// <returns>What was discarded, for the log.</returns>
    public IReadOnlyList<QueuedAddonChange> Forget(string instanceId)
    {
        var read = Read(instanceId);

        try
        {
            lock (_gate)
            {
                if (File.Exists(FileFor(instanceId)))
                {
                    File.Delete(FileFor(instanceId));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not delete the add-on queue of {InstanceId}", instanceId);
        }

        foreach (var change in read.Changes)
        {
            log.LogInformation(
                "{InstanceId} was removed, so its queued add-on change was {Outcome}: {Change}",
                instanceId, QueuedChangeOutcome.Discarded, change.Describe());
        }

        Raise(instanceId);
        return read.Changes;
    }

    /// <summary>The instances with a queue file, by id.</summary>
    public IReadOnlyList<string> Queued()
    {
        try
        {
            return Directory.Exists(Folder)
                ? [.. Directory.EnumerateFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not list the add-on queues");
            return [];
        }
    }

    private void Raise(string instanceId)
    {
        try
        {
            Changed?.Invoke(instanceId);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "A listener threw while being told {InstanceId}'s add-on queue changed", instanceId);
        }
    }
}
