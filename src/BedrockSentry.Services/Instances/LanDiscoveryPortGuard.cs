using System.Net;
using System.Net.Sockets;
using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Keeps the LAN discovery pair — 19132 and 19133 — for the instance that
/// actually configured them, by holding the sockets itself while any other
/// instance starts (<c>D24</c>).
/// </summary>
/// <remarks>
/// Bedrock binds the discovery pair whenever <c>enable-lan-visibility</c> is on,
/// whatever <c>server-port</c> says. Only the first server to bind them gets
/// them; the rest find them taken, soft-fail, and run on their own ports without
/// ever retrying. That is harmless right up until the instance that needs 19132
/// is not the one that got there first — then it cannot start at all, on a port
/// another server neither configured nor advertises.
///
/// <b>And the bind happens <em>after</em> <c>Server started.</c></b> — measured
/// on 1.26.44 from a real console, where <c>used for LAN discovery</c> prints
/// after the ready line. The first cut of this class released its hold on ready,
/// reasoning the server was "past its own bind" by then; it was past its
/// <em>gameplay</em> bind only, and BDS took the freshly released pair a moment
/// later. Reported on build.60 as the port blocker not working: a server on
/// 19136 held 19132 while the server configured for 19132 crash-looped against
/// it.
///
/// Turning the setting off is not the way out. A server with it off answers an
/// unconnected ping with a bare RakNet pong and no server info payload, so the
/// client has nothing to read and refuses the join, while the wrapper reports the
/// instance healthy — the process is up and its socket is bound. Unreachable is a
/// worse outcome than the collision, and far harder to recognise.
///
/// So the wrapper takes the pair itself and <b>keeps it</b> — a standing hold,
/// not a hold for the length of a startup — whenever an instance that does not
/// own the pair starts while some configured instance does own it. The
/// non-owner's discovery bind fails exactly as it would if the owner were
/// already running, which is a path Bedrock already handles, and the pair is
/// let go at the one moment it is safe to: when the owning instance itself
/// starts, inside the same serialised start slot, so nothing can slip between
/// the release and the owner's bind. Running non-owners never retry the bind,
/// which is what makes that window safe.
///
/// When no configured instance owns the pair at all, nothing is held — a
/// wrapper squatting on 19132 forever would make every server on the box
/// undiscoverable on the LAN for the benefit of an owner that does not exist.
///
/// Starts are serialised for the same reason: two instances starting at once
/// would race for the pair, which is the problem rather than the fix.
/// </remarks>
public sealed class LanDiscoveryPortGuard : IDisposable
{
    // BDS's own defaults, and the discovery pair are the same two numbers. Taken
    // from Core rather than restated, so the pre-flight's idea of the default port
    // and this guard's idea of the pair cannot come apart.
    public const int DefaultPortV4 = ServerPorts.DefaultPort;
    public const int DefaultPortV6 = ServerPorts.DefaultPortV6;

    /// <summary>
    /// How long the start slot is held when a server never reports itself
    /// started. The slot only — the sockets are standing and are released by the
    /// owner's own start, so a slow server can no longer lock the owner out.
    /// Bounded so one wedged start cannot stop every other instance starting.
    /// </summary>
    public static readonly TimeSpan DefaultHoldTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long to keep going back for the pair after a bind fails, and how often.
    /// Long enough to outlast an outgoing wrapper process that has not finished
    /// exiting, short enough that a pair genuinely held by something else is
    /// reported rather than retried forever.
    /// </summary>
    public static readonly TimeSpan DefaultRetryFor = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultRetryEvery = TimeSpan.FromMilliseconds(500);

    private readonly SemaphoreSlim _starts = new(1, 1);
    private readonly ILogger<LanDiscoveryPortGuard> _log;
    private readonly int _portV4;
    private readonly int _portV6;
    private readonly TimeSpan _retryFor;
    private readonly TimeSpan _retryEvery;
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// The standing hold. Touched only while the start slot is held — by a start,
    /// or by the retry below, which takes the same slot — which is what makes a
    /// plain pair of fields safe.
    /// </summary>
    private UdpClient? _heldV4;
    private UdpClient? _heldV6;

    /// <summary>The retry in flight, if any. One at a time.</summary>
    private Task? _retry;

    /// <summary>
    /// Whether any configured instance owns the discovery pair right now. Set by
    /// the registry, which is the only thing that can see every instance; live
    /// rather than captured, because instances are created and retired at
    /// runtime. Null — nothing wired it — reads as "no owner", which holds
    /// nothing and matches the guard's behaviour before it existed.
    /// </summary>
    public Func<bool>? OwnerExists { get; set; }

    /// <summary>
    /// Whether the instance that owns the pair is running right now.
    /// </summary>
    /// <remarks>
    /// This is the difference between the two reasons a bind can fail, and the guard
    /// was blind to it. If the owner is <em>running</em>, it holds the pair itself:
    /// the ordinary case, protecting it exactly as well as we would have, and nothing
    /// to do. If it is not running and the bind still fails, something else has the
    /// pair and the owner is locked out of the ports it configured — the failure this
    /// class exists to prevent — so it is worth going back for.
    ///
    /// Null reads as "not running", which is the cautious answer: it retries.
    /// </remarks>
    public Func<bool>? OwnerRunning { get; set; }

    public LanDiscoveryPortGuard(
        ILogger<LanDiscoveryPortGuard> log,
        int portV4 = DefaultPortV4,
        int portV6 = DefaultPortV6,
        TimeSpan? retryFor = null,
        TimeSpan? retryEvery = null)
    {
        _log = log;
        _portV4 = portV4;
        _portV6 = portV6;
        _retryFor = retryFor ?? DefaultRetryFor;
        _retryEvery = retryEvery ?? DefaultRetryEvery;
    }

    public int PortV4 => _portV4;

    public int PortV6 => _portV6;

    /// <summary>
    /// Whether these are the ports an instance configured for itself, in which
    /// case it is the one the pair is being held <em>for</em> and must not be
    /// held against.
    /// </summary>
    /// <remarks>
    /// <para><strong><see cref="ServerTransport.RakNet"/> only</strong>, which is
    /// not a detail (<c>D70</c>). Bedrock's answer-on-19132-whatever-your-port
    /// behaviour is documented for <c>transport=raknet</c> and nothing else, so a
    /// <see cref="ServerTransport.NetherNet"/> server configured for 19132 never
    /// performs discovery on the pair whatever it binds. Counting it as the owner
    /// would hand the pair to a server that does not use it and leave every other
    /// instance's discovery unprotected for as long as it ran.</para>
    ///
    /// <para><strong>The transport rather than the protocol, which is a
    /// correction.</strong> This asked <em>does it hold a UDP socket on one of
    /// these numbers</em>, which agreed with the transport only for as long as a
    /// NetherNet server had no UDP row at all. It has one now — play lands on
    /// <c>server-port</c>'s UDP side when <c>server-udp-ports</c> is unset
    /// (<c>D71</c>) — so the old test would have called such a server on 19132 the
    /// owner. A derived signal that happens to agree is not the question being
    /// asked, and it stops agreeing without anything announcing it
    /// (<c>T151</c> clause 6).</para>
    /// </remarks>
    public bool OwnsDiscoveryPair(InstancePorts model) =>
        model.Transport is ServerTransport.RakNet
        && model.Ports.Any(p => p.Protocol == PortProtocol.Udp
            && (p.Port == _portV4 || p.Port == _portV6));

    /// <summary>
    /// Takes the start slot, and the standing hold on the discovery pair with it
    /// unless <paramref name="instanceOwnsPair"/>. Disposing releases the slot and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// This said "dispose to release both" until `T223`, and it is where that
    /// entry's false sentences came from: the sockets are standing, and the
    /// owner's own start is what frees them. A caller writing from this line
    /// inherits whatever it gets wrong (`T145`), so the arm the two readings
    /// differ on is pinned rather than described —
    /// <c>The_hold_outlives_the_starting_servers_ready_line</c> disposes a
    /// non-owner's hold and asserts the pair is still bound afterwards.
    /// </remarks>
    /// <remarks>
    /// Binding is best-effort. Failing to bind means something already holds the
    /// port — usually the owning instance, running — and that protects it just as
    /// well as we would have. It is not an error and must not fail the start.
    /// </remarks>
    public async Task<IAsyncDisposable> HoldAsync(
        string instanceId, bool instanceOwnsPair, CancellationToken cancellationToken = default)
    {
        await _starts.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (instanceOwnsPair)
        {
            // The pair is this instance's to take, and this is the one safe
            // moment to hand it over: the slot is held, so no other start can
            // race the bind, and running non-owners never retry theirs.
            ReleaseStandingHold(instanceId);
            return new Hold(this, instanceId, owned: true);
        }

        if (OwnerExists?.Invoke() == true)
        {
            // Standing, not for the length of this startup. BDS binds the pair
            // after it reports started, so a hold released on ready was released
            // exactly in time for the wrong server to take it.
            _heldV4 ??= TryBind(IPAddress.Any, _portV4);
            _heldV6 ??= TryBind(IPAddress.IPv6Any, _portV6);

            if (_heldV4 is not null || _heldV6 is not null)
            {
                _log.LogDebug(
                    "Holding the LAN discovery pair for its owner while {InstanceId} runs", instanceId);
            }

            // A failed bind used to end here, on the reasoning that whatever holds
            // the pair is the owner and protects it just as well. That holds only
            // while the owner is running. On a service restart it is the *outgoing
            // wrapper process* still holding the pair — Windows reports a service
            // stopped before its process has exited — and giving up at that moment
            // hands the pair to whichever BDS binds discovery next, seconds later.
            // That is `T82`: observed once in nine auto-starts, and silent both ways.
            if ((_heldV4 is null || _heldV6 is null) && OwnerRunning?.Invoke() != true)
            {
                StartRetry();
            }
        }
        else
        {
            // No configured instance owns the pair, so there is nobody to keep
            // it for — and an owner that was just retired must free it.
            ReleaseStandingHold(instanceId);
        }

        return new Hold(this, instanceId, owned: false);
    }

    /// <summary>
    /// Goes back for the pair, in the background, for as long as it is still ours to
    /// take.
    /// </summary>
    /// <remarks>
    /// Background rather than a wait inside the start, because the start must not be
    /// delayed by a port the wrapper may never get. Each attempt takes the start slot
    /// for the length of one bind and no longer, so the sockets are still only ever
    /// touched under the slot, and a start queued behind an attempt waits microseconds.
    /// One retry runs at a time: a second failing start joins the one already going
    /// rather than beginning another.
    /// </remarks>
    private void StartRetry()
    {
        if (_retry is { IsCompleted: false })
        {
            return;
        }

        _retry = Task.Run(RetryAsync);
    }

    private async Task RetryAsync()
    {
        var deadline = DateTimeOffset.UtcNow + _retryFor;

        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(_retryEvery, _shutdown.Token).ConfigureAwait(false);
                await _starts.WaitAsync(_shutdown.Token).ConfigureAwait(false);

                try
                {
                    if (OwnerExists?.Invoke() != true || OwnerRunning?.Invoke() == true)
                    {
                        // Either nobody owns the pair any more, or the owner is up and
                        // holding it itself. Both mean there is nothing left to do.
                        return;
                    }

                    _heldV4 ??= TryBind(IPAddress.Any, _portV4);
                    _heldV6 ??= TryBind(IPAddress.IPv6Any, _portV6);

                    if (_heldV4 is not null && _heldV6 is not null)
                    {
                        _log.LogInformation(
                            "Took the LAN discovery pair ({PortV4}/{PortV6}) for its owner after "
                            + "something else released it", _portV4, _portV6);
                        return;
                    }
                }
                finally
                {
                    _starts.Release();
                }
            }

            // Said out loud, because the whole failure mode is that it is silent. The
            // owner will fail to start against ports it configured, and the crash will
            // not mention them.
            _log.LogWarning(
                "Could not take the LAN discovery pair ({PortV4}/{PortV6}) within {Seconds} seconds; "
                + "something else is holding it. The instance configured for those ports will not be "
                + "able to start until it is released — restarting the service clears it.",
                _portV4, _portV6, _retryFor.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (ObjectDisposedException)
        {
            // Disposed while waiting on the slot; same thing.
        }
    }

    /// <summary>Lets the standing hold go. Call only while the slot is held.</summary>
    private void ReleaseStandingHold(string instanceId)
    {
        if (_heldV4 is null && _heldV6 is null)
        {
            return;
        }

        try { _heldV4?.Dispose(); } catch (SocketException) { }
        try { _heldV6?.Dispose(); } catch (SocketException) { }

        _heldV4 = null;
        _heldV6 = null;

        _log.LogDebug("Released the LAN discovery pair ahead of {InstanceId}", instanceId);
    }

    /// <summary>
    /// Lets the standing sockets go. The host disposes its singletons on
    /// shutdown, and a socket held into process teardown is harmless — this
    /// exists so tests that share the port do not bleed into each other, and so
    /// the release is explicit rather than left to the finalizer.
    /// </summary>
    public void Dispose()
    {
        // Before the semaphore goes, so a retry waiting on the slot is woken by
        // cancellation rather than by an ObjectDisposedException it has to catch.
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }

        try { _heldV4?.Dispose(); } catch (SocketException) { }
        try { _heldV6?.Dispose(); } catch (SocketException) { }

        _heldV4 = null;
        _heldV6 = null;
        _shutdown.Dispose();
        _starts.Dispose();
    }

    private UdpClient? TryBind(IPAddress address, int port)
    {
        try
        {
            return new UdpClient(new IPEndPoint(address, port));
        }
        catch (SocketException)
        {
            // Already held. Nothing to do and nothing to say: this is the
            // ordinary case whenever the owning instance is running.
            return null;
        }
    }

    private sealed class Hold : IAsyncDisposable
    {
        private readonly LanDiscoveryPortGuard _guard;
        private readonly string _instanceId;
        private readonly bool _owned;
        private bool _released;

        public Hold(LanDiscoveryPortGuard guard, string instanceId, bool owned)
        {
            _guard = guard;
            _instanceId = instanceId;
            _owned = owned;
        }

        /// <summary>
        /// Releases the start slot. The sockets are deliberately not here any
        /// more — they are standing, and the owner's own start is what frees
        /// them. A leaked slot still stops every start, so this stays as small
        /// and unconditional as it can be.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            if (_released)
            {
                return ValueTask.CompletedTask;
            }

            _released = true;
            _guard._starts.Release();

            if (!_owned)
            {
                _guard._log.LogDebug("Start slot released after {InstanceId}", _instanceId);
            }

            return ValueTask.CompletedTask;
        }
    }
}
