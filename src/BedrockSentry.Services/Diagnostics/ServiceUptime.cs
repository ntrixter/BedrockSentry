using System.Diagnostics;
using BedrockSentry.Core.Diagnostics;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// When the wrapper process started, and how long it has been running.
/// </summary>
/// <remarks>
/// <para>
/// This is the <em>wrapper's</em> uptime, not any managed Bedrock server's. From
/// milestone 4 there will be per-instance uptimes too, and conflating the two
/// would be an easy mistake to make while reading a status page at speed — so the
/// UI labels this one explicitly.
/// </para>
/// <para>
/// A self-update restarts the process, so this resetting to zero is the expected
/// signal that an update landed rather than a fault.
/// </para>
/// </remarks>
public sealed class ServiceUptime
{
    public ServiceUptime()
    {
        // The process start time is a truer answer than a timestamp captured in
        // Program.cs: it includes runtime and host startup, which on a
        // self-contained build with a cold file cache is not negligible.
        try
        {
            using var process = Process.GetCurrentProcess();
            StartedUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
        {
            // Losing a second of precision beats failing to render the status page.
            StartedUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>When the wrapper process started.</summary>
    public DateTimeOffset StartedUtc { get; }

    /// <summary>How long the wrapper has been running.</summary>
    public TimeSpan Elapsed => DateTimeOffset.UtcNow - StartedUtc;

    /// <summary>Human-readable form of <see cref="Elapsed"/>.</summary>
    public string ElapsedDisplay => UptimeFormatter.Format(Elapsed);
}
