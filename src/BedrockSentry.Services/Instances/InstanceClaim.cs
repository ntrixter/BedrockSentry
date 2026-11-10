using BedrockSentry.Core.Instances;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// One job's claim on a server's starts and stops, from
/// <see cref="InstanceSupervisor.ClaimAsync"/> until it is disposed.
/// </summary>
/// <remarks>
/// <para>While it is held, a start or stop asked for by anyone else is refused
/// with <see cref="InstanceClaimedException"/>, and so is a second claim: a job
/// that stops a server, changes its files and starts it again runs its three
/// steps with nothing between them. The holder starts and stops through this
/// object.</para>
///
/// <para>A stop for <see cref="StopReason.ServiceStop"/> or
/// <see cref="StopReason.WrapperUpdate"/> is not refused: the wrapper itself is
/// going, and Windows waits for nobody (<c>docs/design.md</c> § 5.2). The
/// wrapper's own update waits for every claim before it drains.</para>
/// </remarks>
public sealed class InstanceClaim : IDisposable
{
    private readonly InstanceSupervisor _supervisor;

    internal InstanceClaim(InstanceSupervisor supervisor, string purpose)
    {
        _supervisor = supervisor;
        Purpose = purpose;
    }

    /// <summary>What the server is busy with, as <see cref="ClaimText"/> names it.</summary>
    public string Purpose { get; }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _supervisor.StartAsync(this, cancellationToken);

    public Task StopAsync(GracefulStopRequest request, CancellationToken cancellationToken = default) =>
        _supervisor.StopAsync(this, request, cancellationToken);

    /// <summary>Stops, runs <paramref name="whileStopped"/>, and starts, as the holder.</summary>
    public Task RestartAsync(
        GracefulStopRequest request,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? whileStopped = null,
        DrainCountdown? countdown = null) =>
        _supervisor.RestartAsync(this, request, cancellationToken, whileStopped, countdown);

    /// <summary>Ends the claim; anyone may start and stop the server again.</summary>
    public void Dispose() => _supervisor.Release(this);
}
