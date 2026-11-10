using BedrockSentry.Core.Diagnostics;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// The firewall's rules as last read, shared by every out-of-date firewall strip
/// (<c>T414</c>), so a tab opened after another shows the strip from the same read.
/// </summary>
/// <remarks>
/// <para>A read starts a PowerShell, through the broker where there is one, and took
/// about 10 s on the first tabs a server opened; each tab's strip read for itself, so
/// each waited that long before it showed. A reading younger than <see cref="MaxAge"/>
/// is reused, and strips that ask while a read runs wait on that one read.</para>
/// <para>A reading that failed is not kept, so the next strip reads again. The strip's
/// own update calls <see cref="Forget"/>; any other change to the rules, a rename's or
/// one made outside this wrapper, shows within <see cref="MaxAge"/>.</para>
/// </remarks>
public sealed class FirewallReadings(IFirewallService firewall, TimeProvider? time = null)
{
    /// <summary>How long a reading is reused.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private Task<FirewallRules>? _reading;
    private DateTimeOffset _started;

    /// <summary>
    /// The last reading that succeeded, however old, or null. A port change moves the
    /// sockets, not the rules, so a strip answers from this at once and again when the
    /// fresh reading lands (<c>T414</c> clause 1: an expired reading took 15 s).
    /// </summary>
    public FirewallRules? Latest
    {
        get
        {
            lock (_gate)
            {
                return _reading is { IsCompletedSuccessfully: true } done && done.Result.Read ? done.Result : _latest;
            }
        }
    }

    private FirewallRules? _latest;

    /// <summary>The rules, from a reading younger than <see cref="MaxAge"/> when there is one.</summary>
    public Task<FirewallRules> ReadAsync()
    {
        lock (_gate)
        {
            var reusable = _reading is { } running
                && (!running.IsCompleted || (running.IsCompletedSuccessfully && running.Result.Read))
                && _time.GetUtcNow() - _started < MaxAge;

            if (!reusable)
            {
                if (_reading is { IsCompletedSuccessfully: true } previous && previous.Result.Read)
                {
                    _latest = previous.Result;
                }

                _started = _time.GetUtcNow();
                _reading = firewall.ReadAsync(CancellationToken.None);
            }

            return _reading!;
        }
    }

    /// <summary>Drops the reading, after a change to the rules, so the next read is fresh.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _reading = null;
            _latest = null;
        }
    }
}
