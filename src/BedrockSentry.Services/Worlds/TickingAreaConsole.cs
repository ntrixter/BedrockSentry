using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Structures;
using BedrockSentry.Services.Instances;

namespace BedrockSentry.Services.Worlds;

/// <summary>What one ticking-area command came to on a server's console.</summary>
/// <param name="Lines">BDS's answer, or null when there is none.</param>
public sealed record TickingAreaAnswer(bool Ok, string Said, IReadOnlyList<ConsoleLine>? Lines);

/// <summary>
/// Sends one ticking-area command and collects its answer: the one way the Ticking areas
/// view and a sync (<c>T439</c>) both talk to BDS about ticking areas.
/// </summary>
public static class TickingAreaConsole
{
    /// <summary>The sentence for a server that is not running.</summary>
    public const string NotRunning = "Start the server to read its ticking areas.";

    /// <summary>Listening starts before the send, so a fast answer is not missed.</summary>
    public static async Task<TickingAreaAnswer> AskAsync(
        InstanceSupervisor server, TickingAreaCommand command, TimeSpan within, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(command);

        using var collect = ConsoleCollect.For(server.Console, command.Starts, command.Ends);

        try
        {
            await server.SendCommandAsync(command.Text).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            return new TickingAreaAnswer(false, NotRunning, null);
        }

        var lines = await collect.WaitAsync(within, time).ConfigureAwait(false);
        if (lines is null)
        {
            return new TickingAreaAnswer(false, $"BDS did not answer \"{command.Text}\" within {within.TotalSeconds:0} seconds.", null);
        }

        var first = lines[0];
        return new TickingAreaAnswer(!TickingAreaAnswers.IsRefusal(first), TickingAreaAnswers.Plain(first.Message), lines);
    }
}
