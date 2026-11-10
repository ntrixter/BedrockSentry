using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;

namespace BedrockSentry.Services.Packages;

/// <summary>
/// Why the update health gate put the previous build back, worded from how its wait
/// ended (<c>D38</c>).
/// </summary>
/// <remarks>
/// The gate said "did not start within 3 minutes" whatever happened, including when
/// the watchdog had given up on the new build seconds in and the wait ended there.
/// </remarks>
public static class HealthGateText
{
    /// <summary>
    /// The refusal that left the new build unlaunched, or null when the gate should wait
    /// and judge the build.
    /// </summary>
    /// <remarks>
    /// Only <see cref="InstanceFault.RefusedToStart"/>: a build that crashed fast enough
    /// for the watchdog to give up before the gate looks is Faulted too, and is the
    /// build's fault, so it is rolled back rather than kept.
    /// </remarks>
    public static string? RefusedBeforeTheGate(InstanceStatus status) =>
        status is { State: InstanceState.Faulted, FaultKind: InstanceFault.RefusedToStart, FaultReason: { } refusal }
            ? refusal
            : null;

    /// <summary>The sentence the console and the log carry, without a closing stop.</summary>
    public static string WhyRolledBack(string version, ReadyWait outcome, TimeSpan budget, InstanceStatus status) =>
        outcome switch
        {
            ReadyWait.NotStarting when status is { FaultKind: InstanceFault.GaveUp, FaultReason: { Length: > 0 } reason } =>
                $"Bedrock {version} did not start: the watchdog {reason}",

            // A retry refused part-way through the gate; the refusal itself is on the console.
            ReadyWait.NotStarting => $"Bedrock {version} did not start, and the server is no longer trying to",
            _ => $"Bedrock {version} did not start within {budget.TotalMinutes:0} minutes",
        };
}
