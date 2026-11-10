using System.Collections.Concurrent;
using BedrockSentry.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>How much disk one instance is using, as of a moment.</summary>
/// <param name="WorldBytes">
/// Everything under the instance's <c>worlds</c> folder. The number worth
/// watching: it is what grows, what a backup weighs, and what a restore needs
/// room for.
/// </param>
/// <param name="FolderBytes">
/// The whole working directory, so including the Bedrock binaries and packs.
/// Answers "what is this server costing me" rather than "what is at risk".
/// </param>
/// <param name="TakenUtc">When the walk ran. Shown, because it is not live.</param>
/// <param name="FreeBytes">
/// Free space on the volume the world is on, not the system drive's (<c>D48</c>):
/// instances can have volumes of their own (<c>T96b</c>, <c>T99</c>). Null when the
/// volume could not be asked.
/// </param>
/// <param name="Growth">The world's samples over the last day, this one included.</param>
public sealed record InstanceSize(
    long WorldBytes, long FolderBytes, DateTimeOffset TakenUtc, long? FreeBytes = null, WorldGrowthHistory? Growth = null)
{
    public string World => ByteSize.Format(WorldBytes);

    public string Folder => ByteSize.Format(FolderBytes);

    /// <summary>The free space, or a dash when it is not known.</summary>
    public string Free => FreeBytes is { } free ? $"{ByteSize.Format(free)} free" : "—";

    /// <summary>Never null: a sample with no history behind it has only itself.</summary>
    public WorldGrowthHistory History => Growth ?? WorldGrowthHistory.Empty.With(new WorldSample(TakenUtc, WorldBytes));
}

/// <summary>
/// Measures instance and world sizes on a slow cadence, off the render path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not simply computed where it is displayed.</b> The Status tab
/// re-reads <c>server.properties</c> on every render, deliberately, because a
/// stale port is a small lie. A directory walk cannot join that: a world is
/// thousands of files and several gigabytes, and doing it per render would make
/// the tab crawl and would keep the disk busy underneath a running server for no
/// benefit — the number moves over hours, not frames.
/// </para>
/// <para>
/// So: a cached answer, returned instantly, with a walk kicked off in the
/// background when it is stale. Callers get the last sample and the time it was
/// taken, and are told when a new one lands via <see cref="Sampled"/> rather than
/// polling for it.
/// </para>
/// <para>
/// The directory-walk half of <c>D48-StatusConsumptionBlock</c>: world and folder
/// size, free space on the world's volume, and a day of world sizes for the growth
/// row and its sparkline. <see cref="InstanceSizeSchedule"/> walks every instance on
/// the 15-minute cadence whether or not a page is open, so the day fills in.
/// </para>
/// </remarks>
public sealed class InstanceSizeSampler
{
    /// <summary>
    /// How stale a sample may be before looking at it starts a new walk.
    /// </summary>
    /// <remarks>
    /// <c>D48</c>'s figure. Long because the walk is the expensive part and the
    /// answer barely moves: a world that gains a megabyte an hour is a busy one.
    /// </remarks>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time;
    private readonly ILogger<InstanceSizeSampler> _log;

    private readonly ConcurrentDictionary<string, InstanceSize> _samples = new(StringComparer.Ordinal);

    /// <summary>Instances with a walk in flight, so two views do not start two.</summary>
    private readonly ConcurrentDictionary<string, byte> _walking = new(StringComparer.Ordinal);

    public InstanceSizeSampler(TimeProvider time, ILogger<InstanceSizeSampler> log)
    {
        _time = time;
        _log = log;
    }

    /// <summary>Raised with the instance id after a walk produces a new sample.</summary>
    /// <remarks>
    /// So a page can redraw when the first sample lands rather than showing a dash
    /// until something unrelated re-renders it — the defect `T152` and `T164` both
    /// were. Subscribers are invoked one at a time and their exceptions swallowed;
    /// this runs on a thread-pool thread with nobody to report to.
    /// </remarks>
    public event Action<string>? Sampled;

    /// <summary>
    /// The last sample, or null if there has never been one. Never walks on the
    /// calling thread; a stale or missing sample starts a background walk.
    /// </summary>
    public InstanceSize? Latest(string instanceId, string workingDirectory)
    {
        _samples.TryGetValue(instanceId, out var sample);

        if (sample is null || _time.GetUtcNow() - sample.TakenUtc > MaxAge)
        {
            Refresh(instanceId, workingDirectory);
        }

        return sample;
    }

    /// <summary>Walks now, in the background, unless one is already running.</summary>
    public void Refresh(string instanceId, string workingDirectory)
    {
        if (!_walking.TryAdd(instanceId, 0))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var worlds = Path.Combine(workingDirectory, "worlds");
                var folder = Measure(workingDirectory);
                var world = Measure(worlds);
                var now = _time.GetUtcNow();

                var history = _samples.TryGetValue(instanceId, out var previous)
                    ? previous.History
                    : WorldGrowthHistory.Empty;

                _samples[instanceId] = new InstanceSize(
                    world, folder, now, FreeSpace(Directory.Exists(worlds) ? worlds : workingDirectory),
                    history.With(new WorldSample(now, world)));
                Raise(instanceId);
            }
            catch (Exception ex)
            {
                // A walk that fails leaves the previous sample in place, which is
                // better than replacing a real number with a zero. Instance
                // directories move, get locked, and sit on volumes that go away.
                _log.LogDebug(ex, "Could not measure {InstanceId}", instanceId);
            }
            finally
            {
                _walking.TryRemove(instanceId, out _);
            }
        });
    }

    /// <summary>
    /// Bytes under <paramref name="directory"/>, or 0 if it is not there.
    /// </summary>
    /// <remarks>
    /// Enumerated rather than collected into a list: a world is thousands of
    /// files and there is no reason to hold them all. Individual files that
    /// vanish mid-walk are skipped — a running server rotates and compacts
    /// underneath this constantly, and a backup being taken at the same moment is
    /// the normal case, so a walk that threw on the first missing file would
    /// almost never finish.
    /// </remarks>
    private static long Measure(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        // .NET does not recurse into a linked folder. A linked file is left out, and a
        // deduplicated file, a reparse point that is its own storage, is counted
        // (Security.ReparseTags).
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", options))
        {
            try
            {
                if (!Security.ReparseTags.IsLink(file))
                {
                    total += file.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone or locked between the listing and the stat.
            }
        }

        return total;
    }

    /// <summary>Free bytes on the volume holding <paramref name="path"/>, or null.</summary>
    /// <remarks>
    /// By the volume's root, so a volume mounted into a folder rather than given a
    /// letter reports the volume holding that folder's drive letter.
    /// </remarks>
    internal static long? FreeSpace(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root
                ? new DriveInfo(root).AvailableFreeSpace
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The instances with a sample.</summary>
    public IReadOnlyCollection<string> Known => [.. _samples.Keys];

    /// <summary>Forgets an instance's samples, so a later instance given its id starts afresh.</summary>
    public void Forget(string instanceId) => _samples.TryRemove(instanceId, out _);

    private void Raise(string instanceId) =>
        EventRaising.RaiseIndependently(
            Sampled,
            instanceId,
            ex => _log.LogDebug(ex, "A listener threw while being told {InstanceId} was measured", instanceId));
}
