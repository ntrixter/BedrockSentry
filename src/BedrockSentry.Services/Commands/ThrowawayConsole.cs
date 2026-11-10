using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Worlds;

namespace BedrockSentry.Services.Commands;

/// <summary>
/// A throwaway run's console as <see cref="StructureConsole"/> drives it (<c>D108</c>):
/// every line the server prints lands in a buffer of its own, where answers are watched for.
/// </summary>
/// <remarks>
/// Nobody reads the buffer but the pass, so it is never shown; what the run did reaches the
/// instance's own console through the notices its caller writes there.
/// </remarks>
public sealed class ThrowawayConsole : IStructureServer, IDisposable
{
    private readonly IBedrockProcess _process;
    private readonly TimeProvider _time;

    public ThrowawayConsole(ThrowawaySession session, string name, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(session);

        _process = session.Process;
        _time = time;
        Name = name;
        _process.OutputReceived += OnOutput;
    }

    public string Name { get; }

    public string InstanceId => "throwaway";

    public bool IsRunning => !_process.HasExited;

    public ConsoleBuffer Console { get; } = new();

    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (_process.HasExited)
        {
            throw new InvalidOperationException($"{Name} is not running.");
        }

        Console.Append(ConsoleOrigin.Command, command, _time.GetUtcNow());
        await _process.SendAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _process.OutputReceived -= OnOutput;

    private void OnOutput(ConsoleOrigin origin, string line) => Console.Append(origin, line, _time.GetUtcNow());
}
