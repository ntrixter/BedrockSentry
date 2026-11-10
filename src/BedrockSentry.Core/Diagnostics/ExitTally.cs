namespace BedrockSentry.Core.Diagnostics;

/// <summary>
/// When a server exited without being asked to, for the Status tab's count over the last
/// day (<c>D48</c>).
/// </summary>
/// <remarks>
/// <para><c>Retrying</c> reports the watchdog's state now, so a server that crash-looped six
/// times this morning and recovered reads the same as one up since Tuesday. This is the
/// field that tells them apart.</para>
///
/// <para>Held in memory: a wrapper restart starts counting again, and the row says from
/// when (<see cref="ExitTallyText"/>). Safe to call from several threads.</para>
/// </remarks>
public sealed class ExitTally
{
    /// <summary>How far back the Status tab counts.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _exits = new();

    /// <param name="countingSinceUtc">When this tally started watching.</param>
    public ExitTally(DateTimeOffset countingSinceUtc) => CountingSinceUtc = countingSinceUtc;

    /// <summary>When this tally started watching; nothing before it is known.</summary>
    public DateTimeOffset CountingSinceUtc { get; }

    /// <summary>Records one exit nobody asked for.</summary>
    public void Add(DateTimeOffset exitedUtc)
    {
        lock (_gate)
        {
            _exits.Enqueue(exitedUtc);
        }
    }

    /// <summary>Exits within <see cref="Window"/> of <paramref name="nowUtc"/>.</summary>
    public int CountWithinWindow(DateTimeOffset nowUtc)
    {
        var cutoff = nowUtc - Window;

        lock (_gate)
        {
            while (_exits.Count > 0 && _exits.Peek() < cutoff)
            {
                _exits.Dequeue();
            }

            return _exits.Count;
        }
    }
}

/// <summary>The Status tab's unexpected-exits row (<c>D48</c>).</summary>
public static class ExitTallyText
{
    /// <summary>The row's label.</summary>
    public const string Label = "Unexpected exits, 24 h";

    /// <summary>
    /// The count, and from when it was counted when the tally has watched for less than a
    /// day, so a wrapper restarted an hour ago does not read as a server quiet all day.
    /// </summary>
    /// <param name="sinceText">How the page writes the tally's start, local time.</param>
    public static string Describe(int count, DateTimeOffset countingSinceUtc, DateTimeOffset nowUtc, string sinceText)
    {
        ArgumentNullException.ThrowIfNull(sinceText);

        return nowUtc - countingSinceUtc >= ExitTally.Window
            ? $"{count}"
            : $"{count} since {sinceText}, when the wrapper began watching";
    }
}
