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
public sealed record InstanceSize(long WorldBytes, long FolderBytes, DateTimeOffset TakenUtc)
{
    public string World => ByteSize.Format(WorldBytes);

    public string Folder => ByteSize.Format(FolderBytes);
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
/// This is the first piece of <c>D48-StatusConsumptionBlock</c>, which specifies
/// world and folder size, free space, growth and a sparkline on a 15-minute
/// directory-walk sample. The rest of that block extends this rather than
/// growing a second answer to the same question.
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
                var folder = Measure(workingDirectory);
                var world = Measure(Path.Combine(workingDirectory, "worlds"));

                _samples[instanceId] = new InstanceSize(world, folder, _time.GetUtcNow());
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
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone or locked between the listing and the stat.
            }
        }

        return total;
    }

    private void Raise(string instanceId) =>
        EventRaising.RaiseIndependently(
            Sampled,
            instanceId,
            ex => _log.LogDebug(ex, "A listener threw while being told {InstanceId} was measured", instanceId));
}
