namespace BedrockSentry.Services.Instances;

/// <summary>
/// Work on a server's files that has to happen while no process is running,
/// done just before each launch.
/// </summary>
/// <remarks>
/// <para>BDS rewrites a world's pack list when it stops and discards an edit made
/// while it ran (<c>D85</c>), so a change to a world's files can only be made
/// with the process down. The moment that is certainly true, and certainly
/// before the next run, is inside the launch: under the supervisor's start gate,
/// after the checks that can refuse it, before the process is created. Every
/// launch passes there, a watchdog restart included, which a handler on the
/// stopped state cannot promise against a restart that follows at once.</para>
///
/// <para>Whatever it throws is logged and the launch goes on: a server that will
/// not start because of a tidy-up is a worse outcome than a tidy-up left for the
/// next launch.</para>
/// </remarks>
public interface IBeforeLaunch
{
    /// <summary>Runs with the process down, before <paramref name="supervisor"/> launches it.</summary>
    Task BeforeLaunchAsync(InstanceSupervisor supervisor, CancellationToken cancellationToken);
}
