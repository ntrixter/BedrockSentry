namespace BedrockSentry.Core.Instances;

/// <summary>
/// One launched <c>bedrock_server.exe</c>, wrapped thinly.
///
/// Thin on purpose: this is the boundary the supervisor's state machine is
/// tested against, and the less policy that lives here the more of that machine
/// can be exercised without a real Minecraft server. A <see cref="IBedrockProcess"/>
/// is single-use — a process that has exited cannot be restarted, so the
/// supervisor asks the factory for a fresh one per attempt.
/// </summary>
public interface IBedrockProcess : IAsyncDisposable
{
    int? ProcessId { get; }

    bool HasExited { get; }

    /// <summary>
    /// Raised for every line read from the server, on a background thread.
    /// Handlers must not block: the pipe is not drained while one is running,
    /// and a stalled reader is indistinguishable from a hung server.
    /// </summary>
    event Action<ConsoleOrigin, string>? OutputReceived;

    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes a single command line to the server's standard input.</summary>
    Task SendAsync(string command, CancellationToken cancellationToken = default);

    /// <summary>Completes with the process exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Terminates the process and its children. The last resort after a graceful
    /// stop has overrun its budget, never the first move.
    /// </summary>
    void Kill();
}

/// <summary>
/// Creates a process per start attempt. Exists so the supervisor can be tested
/// without launching anything.
/// </summary>
public interface IBedrockProcessFactory
{
    IBedrockProcess Create(string instanceId, InstanceDefinition definition, string workingDirectory);
}
