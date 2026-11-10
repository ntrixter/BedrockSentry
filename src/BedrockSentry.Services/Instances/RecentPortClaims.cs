using BedrockSentry.Core.Instances;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// What else holds ports, read at most once per <see cref="MaxAge"/> for a page that
/// renders every second (<c>T426</c>).
/// </summary>
/// <remarks>
/// The read walks every socket on the machine and asks who owns each. On the Server
/// properties tab it ran on every render, the circuit spent its time there, and the
/// tab stopped answering input on Windows. The Status tab had kept its own five-second
/// copy; both now share this one.
/// </remarks>
public sealed class RecentPortClaims(Func<IReadOnlyList<PortClaim>> read, TimeProvider? time = null)
{
    /// <summary>How old a reading may be before it is read again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IReadOnlyList<PortClaim>? _claims;
    private DateTimeOffset _readAt;

    /// <summary>The claims, from a reading younger than <see cref="MaxAge"/> when there is one.</summary>
    public IReadOnlyList<PortClaim> Get()
    {
        var now = _time.GetUtcNow();

        if (_claims is null || now - _readAt >= MaxAge)
        {
            _claims = read();
            _readAt = now;
        }

        return _claims;
    }
}
