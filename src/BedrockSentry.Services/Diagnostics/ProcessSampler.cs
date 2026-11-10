using System.Collections.Concurrent;
using System.Diagnostics;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>What a managed process is costing the machine right now.</summary>
/// <param name="WorkingSetBytes">Physical memory the process currently holds.</param>
/// <param name="CpuPercent">
/// Share of one machine's worth of CPU since the previous sample, or null when
/// there has not been a previous one yet. Normalised across all cores, so 100
/// means the whole machine rather than one core of it.
/// </param>
public sealed record ProcessUsage(long WorkingSetBytes, double? CpuPercent)
{
    /// <summary>
    /// The memory as the panel writes it: whole megabytes, because this number is
    /// watched for growth and one decimal of a gigabyte hides fifty megabytes.
    /// </summary>
    public string Memory => $"{WorkingSetBytes / 1024.0 / 1024.0:N0} MB";
}

/// <summary>
/// Reads memory and CPU for the servers the wrapper is running
/// (<c>docs/design.md</c> § 13).
/// </summary>
/// <remarks>
/// Memory is a reading; CPU is a rate, and a rate needs two points. So the
/// previous processor time is kept per process and the answer is the difference
/// — which is why this is a singleton sampled by whatever is on screen rather
/// than a static helper. The first call for a process returns memory and no
/// percentage, and saying "not yet" for one tick beats inventing a number from
/// the process's whole lifetime, which is what a single reading actually
/// describes.
///
/// Memory is the one people watch on a Bedrock server: the world is held in
/// memory and a server that has been up for a fortnight with a busy world looks
/// very different from one restarted this morning. CPU is the supporting
/// evidence for "why is everything lagging".
/// </remarks>
public sealed class ProcessSampler
{
    private readonly record struct Reading(TimeSpan Processor, DateTimeOffset TakenUtc);

    private readonly ConcurrentDictionary<int, Reading> _previous = new();
    private readonly TimeProvider _time;

    public ProcessSampler(TimeProvider time) => _time = time;

    /// <summary>
    /// Samples one process, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Null rather than zero. A process that has just exited, or that this
    /// account cannot open, is not a server using no memory — and a status page
    /// reporting 0 MB for a running server is worse than one reporting nothing.
    /// </remarks>
    public ProcessUsage? Sample(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            var now = _time.GetUtcNow();
            var processor = process.TotalProcessorTime;
            var workingSet = process.WorkingSet64;

            double? percent = null;

            if (_previous.TryGetValue(processId, out var last))
            {
                var elapsed = now - last.TakenUtc;

                // Guard the denominator rather than trusting the caller's
                // cadence: two samples in the same tick would divide by nothing
                // and report an absurd figure on an otherwise idle server.
                if (elapsed > TimeSpan.Zero)
                {
                    var used = (processor - last.Processor).TotalMilliseconds;

                    percent = Math.Clamp(
                        used / elapsed.TotalMilliseconds / Environment.ProcessorCount * 100,
                        0,
                        100);
                }
            }

            _previous[processId] = new Reading(processor, now);
            return new ProcessUsage(workingSet, percent);
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Gone, or not ours to read. Both are "no answer", not "no usage".
            Forget(processId);
            return null;
        }
    }

    /// <summary>
    /// Drops a process's history.
    /// </summary>
    /// <remarks>
    /// Called when a sample fails, so a process id Windows later reuses does not
    /// inherit a predecessor's processor time and report a wild percentage on
    /// its first reading.
    /// </remarks>
    public void Forget(int processId) => _previous.TryRemove(processId, out _);
}
