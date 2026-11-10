using System.Text.Json.Serialization;

namespace BedrockSentry.Core.Commands;

/// <summary>One command sent to a throwaway server and every line it answered with.</summary>
/// <param name="Reply">The raw lines, prefixes and colour codes included, in the order printed.</param>
public sealed record ConsoleExchange(string Command, IReadOnlyList<string> Reply)
{
    /// <summary>The reply without the <c>[timestamp LEVEL]</c> prefixes.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Messages => [.. Reply.Select(HelpPages.Message)];
}

/// <summary>A run's exchanges as text: each command on a <c>&gt;&gt;&gt; </c> line, its reply under it.</summary>
/// <remarks>
/// <para>The transcript is what a run keeps, and the commands are built from it by the
/// same code whether the run happened on this machine or is the BDS 1.26.52.3 run that
/// ships with BedrockSentry. The server never prints a line starting
/// <c>&gt;&gt;&gt; </c>.</para>
///
/// <para>A NUL the server writes ahead of a line now and then (<c>T160</c>; one sat in
/// front of <c>camerashake</c>'s reply in the 1.26.52.3 capture) is dropped on the way
/// out, so the file stays text. It carries nothing: <c>BdsLogParser</c> reads the line
/// the same with it or without it.</para>
/// </remarks>
public static class CaptureTranscript
{
    /// <summary>What starts a command's line.</summary>
    public const string SentPrefix = ">>> ";

    /// <summary>
    /// Stands in for a command over the lines the server printed while it started, which
    /// answer nothing but show which packs it loaded.
    /// </summary>
    public const string StartLabel = "(start)";

    /// <summary>Writes <paramref name="exchanges"/> in order.</summary>
    public static void Write(TextWriter writer, IEnumerable<ConsoleExchange> exchanges)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(exchanges);

        foreach (var exchange in exchanges)
        {
            writer.Write(SentPrefix);
            writer.Write(exchange.Command);
            writer.Write('\n');
            foreach (var line in exchange.Reply)
            {
                writer.Write(line.Replace("\0", string.Empty, StringComparison.Ordinal));
                writer.Write('\n');
            }
        }
    }

    /// <summary>Reads exchanges back; lines before the first command are dropped.</summary>
    public static IReadOnlyList<ConsoleExchange> Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var exchanges = new List<ConsoleExchange>();
        string? command = null;
        var reply = new List<string>();

        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith(SentPrefix, StringComparison.Ordinal))
            {
                if (command is not null)
                {
                    exchanges.Add(new ConsoleExchange(command, reply));
                }

                command = line[SentPrefix.Length..];
                reply = [];
            }
            else if (command is not null)
            {
                reply.Add(line);
            }
        }

        if (command is not null)
        {
            exchanges.Add(new ConsoleExchange(command, reply));
        }

        return exchanges;
    }
}
