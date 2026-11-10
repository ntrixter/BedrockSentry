using System.Diagnostics;
using BedrockSentry.Core.Broker;
using BedrockSentry.Core.Diagnostics;

namespace BedrockSentry.Host;

/// <summary>
/// Records a failure that kills the process before the logging pipeline has
/// produced anything (<c>T138</c>).
/// </summary>
/// <remarks>
/// The event log provider is registered early, but a Kestrel bind failure
/// throws out of <c>app.Run()</c> without any of it reaching a sink. Observed on
/// Windows: a service dying four seconds after install on *"Failed to bind to
/// address http://127.0.0.1:47324: address already in use"*, with the wrapper's own
/// log directory empty and no entry under its event source — only a generic
/// APPCRASH and the .NET Runtime record, neither of which names the port.
///
/// <para>That made <c>docs/windows-setup.md</c>'s first instruction — check the
/// Application event log — useless for exactly the class of failure somebody
/// reading it has. There were two ways to fix that: stop the documentation
/// promising a log that cannot exist, or make the promise true. This is the
/// second, because the first leaves the operator with an APPCRASH and no
/// reason.</para>
///
/// <para>Both destinations are best effort and neither may mask the original
/// exception. The event log needs its source to exist, which only an elevated
/// installer can create; the file needs a data root that may be exactly what is
/// broken. Anything that goes wrong here is written to stderr and dropped —
/// the caller rethrows regardless, so the service control manager still sees a
/// failed start.</para>
/// </remarks>
internal static class StartupFailure
{
    /// <summary>
    /// The event source the installer creates and <c>Program</c> logs under: the
    /// installed service's name, set from <c>--service</c> before anything can fail.
    /// </summary>
    public static string EventSource { get; set; } = BrokerNames.DefaultMainService;

    private static volatile bool _started;

    /// <summary>Which side of a successful startup the process is on (`T143`).</summary>
    public static string Phase => _started ? "shutting down" : "starting";

    /// <summary>Latched from <c>ApplicationStarted</c>; never unset.</summary>
    public static void MarkStarted() => _started = true;

    public static void Record(Exception exception, string logDirectory, string phase)
    {
        var now = DateTimeOffset.Now;
        var message =
            $"BedrockSentry failed while {phase}, at {ClockText.DateAndTimeWithSeconds(now)} {now:zzz}."
            + Environment.NewLine + Environment.NewLine
            + exception;

        WriteToEventLog(message);
        WriteToFile(message, logDirectory);
    }

    private static void WriteToEventLog(string message)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !EventLog.SourceExists(EventSource))
            {
                return;
            }

            // Truncated: the event log refuses an entry over ~32 KB, and a long
            // stack trace losing the whole record is the opposite of the point.
            EventLog.WriteEntry(
                EventSource,
                message.Length > 30000 ? message[..30000] : message,
                EventLogEntryType.Error);
        }
        catch (Exception failed)
        {
            Console.Error.WriteLine($"Could not write the startup failure to the event log: {failed.Message}");
        }
    }

    private static void WriteToFile(string message, string logDirectory)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);

            // One file, overwritten: this is "why did it not start just now",
            // not a history, and an unbounded pile of crash files in the
            // directory somebody is told to look in helps nobody.
            // Not "startup-failure.log", which is what this was called until `T143`:
            // the same file records a shutdown failure, so half the time the name
            // was wrong. The header inside says which phase.
            File.WriteAllText(Path.Combine(logDirectory, "last-failure.log"), message);
        }
        catch (Exception failed)
        {
            Console.Error.WriteLine($"Could not write the startup failure to {logDirectory}: {failed.Message}");
        }
    }
}
