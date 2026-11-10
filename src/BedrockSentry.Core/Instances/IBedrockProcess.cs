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

    /// <summary>
    /// Whether this factory could actually produce a running server in
    /// <paramref name="workingDirectory"/> — for the real one, what the disk says
    /// about the executable.
    /// </summary>
    /// <remarks>
    /// <para>Asked before a launch rather than discovered by one (<c>T245</c>).
    /// Discovering it meant the process start threw, the watchdog counted a
    /// crash, and two backoffs later the instance gave up under "no further
    /// restarts will be attempted" — a sentence about a crash loop, on a server
    /// that had never been installed.</para>
    ///
    /// <para>It answers with <see cref="ServerPresence"/> rather than a bool
    /// because the world has four states and a bool made the wrapper say a
    /// present-but-refused executable was not there (<c>T246</c>).</para>
    ///
    /// <para><strong>Here rather than on the supervisor</strong>, which is the
    /// obvious place and the wrong one. The supervisor's
    /// <c>IsProvisioned</c> is a question about the disk; this is a question
    /// about whether the thing that turns a definition and a folder into a
    /// process can do so. In an install those coincide. Under a factory that
    /// launches nothing they do not, and a disk read there would refuse every
    /// launch in the suite — so the fiction is answered by the thing that holds
    /// it, and the real answer stays a real disk read.</para>
    /// </remarks>
    ServerPresence PresenceOf(InstanceDefinition definition, string workingDirectory);
}
