using BedrockSentry.Core.Addons;

namespace BedrockSentry.Core.Instances;

/// <summary>The companion pack on a server's current run, in one word or two.</summary>
public enum CompanionRunState
{
    /// <summary>Switched on, and the server is not running.</summary>
    Stopped,

    /// <summary>It said hello and answered the console's probe.</summary>
    Reporting,

    /// <summary>It said hello and has not answered the probe.</summary>
    NotAnswering,

    /// <summary>Switched on, running, and no hello.</summary>
    Silent,

    /// <summary>It speaks a line format this build does not read.</summary>
    WrongProtocol,
}

/// <summary>What the companion pack said on one run of a server (milestone 12).</summary>
/// <param name="Protocol">The line format its hello announced.</param>
/// <param name="PackVersion">The pack's own version, from its hello.</param>
/// <param name="ReportedUtc">When its hello arrived.</param>
/// <param name="ProbeAnswered">
/// Whether it answered the console's <c>scriptevent</c> probe on this run, which
/// is what asking for positions before a stop (<c>D35</c>) relies on.
/// </param>
public sealed record CompanionRun(
    int Protocol, string PackVersion, DateTimeOffset ReportedUtc, bool ProbeAnswered)
{
    /// <summary>Whether this build reads the lines the pack prints.</summary>
    public bool Readable => Protocol == CompanionPack.Protocol;

    /// <summary>
    /// What the Add-ons tab says about the companion pack on the server's current
    /// run, or null when there is nothing to say.
    /// </summary>
    /// <param name="run">The run's state, null before a hello.</param>
    /// <param name="running">Whether the server is running.</param>
    /// <param name="switchedOn">Whether the pack is switched on for this server's world.</param>
    public static string? Describe(CompanionRun? run, bool running, bool switchedOn)
    {
        // The label beside the pack's name is StateOf, below; both walk the same
        // arms in the same order, and CompanionRunTests pins that they agree.
        if (!running)
        {
            return null;
        }

        if (run is null)
        {
            // Switched on and silent: the pack speaks as the world loads, so a
            // running server without a hello has a pack that is not reaching
            // the console. Switched off, the row's own button says it all.
            return switchedOn
                ? "The companion pack has not reported on this run. It speaks as the world loads, so "
                    + "check that script output reaches the console."
                : null;
        }

        if (!run.Readable)
        {
            return $"The companion pack speaks protocol {run.Protocol} and this build of BedrockSentry "
                + $"reads {CompanionPack.Protocol}, so nothing it reports is recorded.";
        }

        return run.ProbeAnswered
            ? $"The companion pack {run.PackVersion} is reporting on this run."
            : $"The companion pack {run.PackVersion} is reporting on this run, but has not answered the "
                + "console, so players' positions will not be asked for when the server stops.";
    }

    /// <summary>
    /// The short label beside the pack's name, or null when there is nothing to say:
    /// not switched on, and nothing heard from it.
    /// </summary>
    /// <remarks>
    /// The same arms as <see cref="Describe"/>, which is the sentence behind the
    /// label, except that a stopped server with the pack switched on has a label:
    /// the row says when the pack will report rather than saying nothing.
    /// </remarks>
    public static CompanionRunState? StateOf(CompanionRun? run, bool running, bool switchedOn)
    {
        if (!running)
        {
            return switchedOn ? CompanionRunState.Stopped : null;
        }

        if (run is null)
        {
            return switchedOn ? CompanionRunState.Silent : null;
        }

        if (!run.Readable)
        {
            return CompanionRunState.WrongProtocol;
        }

        return run.ProbeAnswered ? CompanionRunState.Reporting : CompanionRunState.NotAnswering;
    }

    /// <summary>The words of the label.</summary>
    public static string Label(CompanionRunState state) => state switch
    {
        CompanionRunState.Stopped => "Reports while the server runs",
        CompanionRunState.Reporting => "Reporting on this run",
        CompanionRunState.NotAnswering => "Not answering",
        CompanionRunState.Silent => "Silent",
        CompanionRunState.WrongProtocol => "Wrong protocol",

        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No label is written for this state."),
    };

    /// <summary>The label's badge: green when it works, amber when it may not, red when nothing is recorded.</summary>
    public static string BadgeClass(CompanionRunState state) => state switch
    {
        CompanionRunState.Stopped => "state-badge",
        CompanionRunState.Reporting => "state-badge state-running",
        CompanionRunState.NotAnswering or CompanionRunState.Silent => "state-badge state-warning",
        CompanionRunState.WrongProtocol => "state-badge state-faulted",

        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No badge is chosen for this state."),
    };
}
