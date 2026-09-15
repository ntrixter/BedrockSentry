using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// Reports whether any managed server needs a person (<c>T165</c>).
/// </summary>
/// <remarks>
/// <para><strong>This must never be registered on the liveness endpoint.</strong>
/// <c>UpdateRunner</c> polls that one after swapping a build and rolls the build
/// back unless it reads <c>Healthy</c>. A server whose port happens to be held
/// would then throw away a replacement build that was perfectly fine — which is
/// the whole of <c>T151</c> turned into an update failure. It is mapped on the
/// detailed endpoint only, and the liveness endpoint selects no checks at all.</para>
///
/// <para>Here rather than in <c>Host</c> because the suite cannot see <c>Host</c>,
/// and the severity mapping below is a rule — exactly the kind this project keeps
/// rediscovering in files no test can reach. <c>Services</c> already carries a
/// framework reference, so <c>IHealthCheck</c> needs no new dependency.</para>
/// </remarks>
public sealed class InstanceStateHealthCheck : IHealthCheck
{
    /// <summary>The name this check reports under, in the JSON and in registration.</summary>
    public const string CheckName = "instances";

    private readonly InstanceRegistry _instances;

    public InstanceStateHealthCheck(InstanceRegistry instances) => _instances = instances;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Evaluate(_instances.Statuses()));

    /// <summary>
    /// The rule, with the state passed in so a test can drive every branch.
    /// </summary>
    /// <remarks>
    /// <para><strong>A faulted instance is <c>Degraded</c>, never
    /// <c>Unhealthy</c>.</strong> Two things reach that state and neither means the
    /// wrapper is broken: a start refused because something already holds the port
    /// (<c>T151</c>), which is the wrapper doing its job correctly and refusing to
    /// open a world twice; and a server the watchdog gave up restarting, which
    /// needs a person but leaves the host working. Calling either "unhealthy" would
    /// read as "BedrockSentry is down" when the truthful sentence is "one server is
    /// not running, and here is why".</para>
    ///
    /// <para><c>Backoff</c> is healthy on purpose: a scheduled retry is the
    /// watchdog working, and flagging it would make every ordinary crash-and-recover
    /// show up as a problem the operator has to dismiss.</para>
    ///
    /// <para><c>FaultReason</c> is read only while the state is <c>Faulted</c>. It
    /// is written on entering that state and carried along unread through a later
    /// <c>Stopped</c>, so emitting it unconditionally would attach a stale
    /// explanation to a server somebody stopped deliberately.</para>
    /// </remarks>
    public static HealthCheckResult Evaluate(IReadOnlyList<InstanceStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);

        var faulted = statuses.Where(s => s.State is InstanceState.Faulted).ToList();

        if (faulted.Count == 0)
        {
            return HealthCheckResult.Healthy(
                statuses.Count == 0
                    ? "No servers are configured."
                    : $"{statuses.Count} server{(statuses.Count == 1 ? "" : "s")}, none faulted.");
        }

        var described = faulted
            .Select(s => s.FaultReason is { Length: > 0 } reason
                ? $"{s.Name}: {reason}"
                : $"{s.Name}: faulted, with no reason recorded")
            .ToList();

        return HealthCheckResult.Degraded(
            $"{faulted.Count} of {statuses.Count} server{(statuses.Count == 1 ? "" : "s")} "
            + $"need attention — {string.Join("; ", described)}");
    }
}
