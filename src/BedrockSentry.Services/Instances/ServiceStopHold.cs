using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Keeps a Windows service reporting that it is stopping until the host has stopped,
/// every server's drain included (<c>T167</c>, <c>D32</c>).
/// </summary>
/// <remarks>
/// <para><strong>The defect.</strong> The framework's service lifetime waits the host's
/// shutdown timeout and then reports <c>SERVICE_STOPPED</c> whether the host has finished
/// or not. A drain that ran past it left <c>bedrock_server.exe</c> saving behind a service
/// Windows called stopped, and a start in that gap refused every auto-start. Answered by
/// the operator 2026-09-25: report stopped only once every server has exited.</para>
///
/// <para><strong>Unbounded on purpose.</strong> A server's stop kills it when its drain
/// budget runs out and then waits for the exit, so the drain ends unless something holds
/// an instance's gate (<c>D32</c>). While it does, "stopping" is the true answer.</para>
///
/// <para>Here rather than in the host's lifetime, which the test project cannot reach.</para>
/// </remarks>
public static class ServiceStopHold
{
    /// <summary>How often Windows is told the stop is still going.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// What each report asks for: three intervals, so one late report is not read as a
    /// hung stop by a program watching the service.
    /// </summary>
    public static readonly TimeSpan Asked = Interval * 3;

    /// <summary>
    /// Carries out a stop Windows asked for, and returns once the host has stopped.
    /// </summary>
    /// <param name="shutdownTimeout">The longest <paramref name="stopHost"/> waits.</param>
    /// <param name="stopHost">
    /// The framework's own stop. It records that Windows asked, starts the host's stop and
    /// waits up to <paramref name="shutdownTimeout"/> for it. It runs before anything here
    /// waits: until it has recorded the request, the host's last step asks Windows to stop
    /// the service a second time.
    /// </param>
    /// <param name="stoppedWithin">
    /// Waits up to the time given for the host to stop; true once it has.
    /// </param>
    /// <param name="requestMoreTime">Tells Windows the stop needs this much longer.</param>
    /// <param name="time">Measures how far past the timeout the stop ran.</param>
    /// <param name="log">Where the overrun is written; the Application log takes warnings.</param>
    /// <returns>How far past <paramref name="shutdownTimeout"/> the stop ran; zero if it did not.</returns>
    public static TimeSpan Run(
        TimeSpan shutdownTimeout,
        Action stopHost,
        Func<TimeSpan, bool> stoppedWithin,
        Action<TimeSpan> requestMoreTime,
        TimeProvider time,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(stopHost);
        ArgumentNullException.ThrowIfNull(stoppedWithin);
        ArgumentNullException.ThrowIfNull(requestMoreTime);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        // The framework's wait reports nothing while it runs, so this report covers it.
        var reporting = Report(requestMoreTime, shutdownTimeout + Asked, log);

        stopHost();

        if (stoppedWithin(TimeSpan.Zero))
        {
            return TimeSpan.Zero;
        }

        var began = time.GetTimestamp();

        log.LogWarning(
            "The service stop has run past the host's {Timeout}s shutdown timeout. Windows is told "
            + "the service is still stopping until every server has exited.",
            shutdownTimeout.TotalSeconds);

        do
        {
            reporting = reporting && Report(requestMoreTime, Asked, log);
        }
        while (!stoppedWithin(Interval));

        var overran = time.GetElapsedTime(began);

        log.LogWarning(
            "The host stopped {Overran:0.#}s past its {Timeout}s shutdown timeout; Windows is told the "
            + "service has stopped.",
            overran.TotalSeconds,
            shutdownTimeout.TotalSeconds);

        return overran;
    }

    /// <summary>One report; false once one has failed, so a failure is logged once.</summary>
    private static bool Report(Action<TimeSpan> requestMoreTime, TimeSpan asked, ILogger log)
    {
        try
        {
            requestMoreTime(asked);
            return true;
        }
        catch (Exception ex)
        {
            // Never the end of the hold. A report that cannot be made changes what a
            // program watching the service is told; saying stopped early is the defect.
            log.LogWarning(
                ex,
                "Could not tell Windows the service stop needs more time. The stop is held until "
                + "the host has stopped anyway.");
            return false;
        }
    }
}
