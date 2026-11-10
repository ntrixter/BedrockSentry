namespace BedrockSentry.Core.Instances;

/// <summary>One thing worth saying about a server's <c>server-udp-ports</c>.</summary>
public enum UdpPortNoticeKind
{
    /// <summary>Nothing is set, so play sockets are neither published nor named.</summary>
    NotPinned,

    /// <summary>A window is set, but nothing in it publishes an address.</summary>
    AddressMissing,

    /// <summary>
    /// The address the line publishes is not the one this machine currently
    /// answers on.
    /// </summary>
    AddressMoved,

    /// <summary>The window holds fewer ports than <c>max-players</c> allows players.</summary>
    Narrower,

    /// <summary>The value is set to something this wrapper cannot read.</summary>
    Unreadable,

    /// <summary>
    /// Part of the window is another server's or already open on this machine, so
    /// the server is refused at start (<c>T415</c>).
    /// </summary>
    Taken,
}

/// <summary>One notice, and the sentence that states it.</summary>
/// <param name="Kind">Which notice, for anything that wants to style or order them.</param>
/// <param name="Sentence">
/// What it says, in <c>Core</c> so that a test can read the words rather than a
/// page being the only place they exist.
/// </param>
public sealed record UdpPortNotice(UdpPortNoticeKind Kind, string Sentence)
{
    /// <summary>
    /// Whether this is something the wrapper can call WRONG, as opposed to
    /// something it can only report (<c>T240</c> is the same distinction one type
    /// along; this is <c>T241</c>).
    /// </summary>
    /// <remarks>
    /// <para><strong>Every notice was a fault until 2026-09-18, and that
    /// contradicted the justification written four paragraphs below it.</strong>
    /// <see cref="UdpPortAdvice"/>'s own remarks say the notices are <em>facts
    /// rather than verdicts</em> because nothing here can tell a stale published
    /// address from a deliberate one — and the stance then used
    /// <see cref="UdpPortNoticeKind.AddressMoved"/> to decide <em>faulted</em>, so
    /// an administrator who published a static address reached through a name, or a
    /// second WAN link, was handed a composed <em>"Replace … with this:"</em> line
    /// over a value they had chosen. A justification used to explain a branch, and
    /// then contradicted by the branch beside it (<c>T188</c>).</para>
    ///
    /// <para><strong>The three that remain are objective and the excluded one is
    /// not.</strong> Unreadable means this code cannot parse the value; missing
    /// means nothing is published at all; narrower is arithmetic against
    /// <c>max-players</c>. Each is true or false without knowing anything about the
    /// administrator's network. Whether a published address is stale is not.</para>
    /// </remarks>
    public bool IsFault => Kind is UdpPortNoticeKind.Unreadable
                                or UdpPortNoticeKind.AddressMissing
                                or UdpPortNoticeKind.Narrower
                                or UdpPortNoticeKind.Taken;
}

/// <summary>
/// What the key already says, which is what decides whether a suggested line is
/// advice or an instruction to overwrite somebody's own value.
/// </summary>
/// <remarks>
/// <para><strong>The question is whether the file WORKS, not whether it agrees
/// with the line this would suggest.</strong> A window wider than
/// <c>max-players</c>, a different base port, a mapping written for a router
/// nothing here can see — each of those is sound and none of them matches.
/// Comparing against the suggestion would make this code's own preference the
/// standard and would tell an administrator to overwrite a working value, which
/// is the one thing an advisory feature must not do. The notices already answer
/// the real question from measurements, so the stance is read off them.</para>
/// </remarks>
public enum UdpPortAdviceStance
{
    /// <summary>
    /// The key changes nothing for this server, so there is nothing to advise
    /// about and no state of the key worth reading.
    /// </summary>
    NotApplicable,

    /// <summary>The key is not set, so a suggested line is something to add.</summary>
    Unset,

    /// <summary>
    /// The key is set and nothing here has a complaint about it. No line is
    /// offered and none is withheld for a reason worth printing: there is simply
    /// nothing to change.
    /// </summary>
    Settled,

    /// <summary>
    /// The key is set and at least one notice says what is wrong with it, so a
    /// suggested line replaces a value rather than filling a blank.
    /// </summary>
    Faulted,
}

/// <summary>
/// What the wrapper has to say about an instance's <c>server-udp-ports</c>
/// (<c>D71</c>) — a line to apply and whatever has since stopped being true about
/// the one already there.
/// </summary>
/// <remarks>
/// <para><strong>Advisory, and that is the decision rather than an unfinished
/// half.</strong> The published half of this key encodes a fact about somebody's
/// router, which nothing inside the machine can verify; a wrong one breaks
/// external connections silently, and a wrapper that rewrites
/// <c>server.properties</c> on its own schedule is a second writer against a file
/// the server owns. So this computes and shows, and an administrator applies.</para>
///
/// <para><strong>Drift is the failure that suggesting once otherwise creates.</strong>
/// A line applied in March and a DHCP lease that moves in April leaves external
/// players unable to connect with nothing anywhere explaining why, and the wrapper
/// is the only thing on the machine watching both values.
/// <see cref="UdpPortNoticeKind.Narrower"/> is the same failure through the other
/// input: <c>max-players</c> is raised in the panel, the advisory line stays where
/// it was, and the server runs out of ports at the old ceiling.</para>
///
/// <para><strong>The notices are facts rather than verdicts</strong>, which is why
/// <see cref="UdpPortNoticeKind.AddressMoved"/> names both values instead of
/// calling the file wrong. Nothing here can tell a stale address from a deliberate
/// one — a static address reached through a name, a second WAN link — and an
/// advisory that cries wolf is one somebody stops reading.</para>
/// </remarks>
/// <param name="Stance">
/// What the key already says, in the four states the world can be in. Read this
/// rather than matching on a sentence, and rather than deriving it from which of
/// the fields below came back null.
/// </param>
/// <param name="Lead">
/// The one sentence this block leads with: what the key says now, and whether
/// the suggested line fills a blank or replaces a value. Null when there is
/// neither a line to introduce nor a settled value to report back, which leaves
/// <paramref name="SuggestionBlockedBy"/> to speak for itself.
/// </param>
/// <param name="Suggested">
/// The line to put in <c>server.properties</c>, or null when it cannot be
/// computed <em>or</em> when nothing needs changing.
/// </param>
/// <param name="SuggestionBlockedBy">
/// Why there is no <paramref name="Suggested"/> line, when
/// <see cref="Applies"/> is true and one was wanted. Null whenever a line
/// was produced — <em>we could not work one out</em> and <em>nothing needed
/// working out</em> are different answers and used to render as the same blank —
/// and null under <see cref="UdpPortAdviceStance.Settled"/>, where no line was
/// wanted in the first place and a reason for its absence would read as a fault.
/// </param>
/// <param name="Notices">What has stopped being true, in the order to show it.</param>
public sealed record UdpPortAdvice(
    UdpPortAdviceStance Stance,
    string? Lead,
    string? Suggested,
    string? SuggestionBlockedBy,
    IReadOnlyList<UdpPortNotice> Notices)
{
    /// <summary>
    /// Whether there is anything at all to say: BDS documents the key as ignored
    /// under <see cref="ServerTransport.RakNet"/>, and a transport this wrapper
    /// does not recognise is one it has nothing to advise about.
    /// </summary>
    /// <remarks>
    /// Read off <see cref="Stance"/> rather than carried beside it. As a field of
    /// its own it crossed with three stances to make eight combinations, four of
    /// which describe no server — <em>does not apply</em> and <em>is set and
    /// faulted</em> among them.
    /// </remarks>
    public bool Applies => Stance is not UdpPortAdviceStance.NotApplicable;

    /// <summary>
    /// Whether the Status card points down to a change (<c>T427</c>): a line to add
    /// or a value to replace. A settled key has nothing to change, and the card said
    /// "Play ports need a change" above a block saying there was none.
    /// </summary>
    public bool AsksForChange => Stance is UdpPortAdviceStance.Unset or UdpPortAdviceStance.Faulted;

    private static readonly UdpPortAdvice Silent =
        new(UdpPortAdviceStance.NotApplicable, null, null, null, []);

    /// <summary>
    /// Everything worth telling an administrator about this instance's play-port
    /// configuration.
    /// </summary>
    /// <param name="transport">This instance's transport, null when unknown.</param>
    /// <param name="port">Its <c>server-port</c>, null when the key is unset.</param>
    /// <param name="maxPlayers">Its <c>max-players</c>, null when the key is unset.</param>
    /// <param name="udpPorts">Its <c>server-udp-ports</c>, read across every set line.</param>
    /// <param name="publicAddress">
    /// This machine's public address as last discovered (<c>D20</c>), or null when
    /// the lookup is off or did not answer. Null never produces
    /// <see cref="UdpPortNoticeKind.AddressMoved"/>: not knowing the live address
    /// is not evidence that the published one is stale.
    /// </param>
    /// <param name="claims">
    /// What else holds or names ports, this server's own file left out
    /// (<c>T415</c>). A window that runs into one is a fault, and the suggested
    /// line starts past them. Null reads as nothing in the way.
    /// </param>
    public static UdpPortAdvice For(
        ServerTransport? transport,
        int? port,
        int? maxPlayers,
        ServerUdpPorts? udpPorts,
        string? publicAddress,
        IReadOnlyList<PortClaim>? claims = null)
    {
        if (transport is not ServerTransport.NetherNet)
        {
            return Silent;
        }

        var configured = udpPorts ?? ServerUdpPorts.Absent;
        var basePort = port ?? ServerPorts.DefaultPort;
        var notices = new List<UdpPortNotice>();

        switch (configured.Kind)
        {
            case UdpPortsKind.Absent:
                notices.Add(new UdpPortNotice(
                    UdpPortNoticeKind.NotPinned,
                    "server-udp-ports is not set. Play sockets land on the server port's "
                    + "UDP side, and nothing publishes an address for players outside this "
                    + "network to reach."));
                break;

            case UdpPortsKind.Unreadable:
                notices.Add(new UdpPortNotice(
                    UdpPortNoticeKind.Unreadable,
                    $"server-udp-ports is set to \"{configured.Value}\", which this wrapper "
                    + "cannot read. Nothing below describes where play traffic will go."));
                break;

            default:
                if (string.IsNullOrEmpty(configured.Address))
                {
                    notices.Add(new UdpPortNotice(
                        UdpPortNoticeKind.AddressMissing,
                        "server-udp-ports names ports but no address, so nothing tells a "
                        + "player outside this network where to reach them."));
                }
                else if (!string.IsNullOrEmpty(publicAddress)
                    && !configured.Address.Trim().Equals(publicAddress.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    notices.Add(new UdpPortNotice(
                        UdpPortNoticeKind.AddressMoved,
                        $"server-udp-ports publishes {configured.Address}. This machine's "
                        + $"public address is currently {publicAddress}."));
                }

                if (configured.Local is { } window && maxPlayers is { } players && window.Width < players)
                {
                    notices.Add(new UdpPortNotice(
                        UdpPortNoticeKind.Narrower,
                        $"server-udp-ports holds {window.Width} port"
                        + (window.Width == 1 ? "" : "s")
                        + $" and max-players allows {players}. Play takes one port per player "
                        + "online, so the last players to join have nowhere to land."));
                }

                if (configured.Local is { } local && claims is not null
                    && NewServerPorts.Describe(claims, [new ReachableSockets(local, PortProtocol.Udp)]) is { Count: > 0 } taken)
                {
                    // Not "will not start": a window is bound one port per player as
                    // they join, so a server whose window overlaps another's started and
                    // ran beside it (`T359` clause 4, 2026-11-09). The overlap is what is known.
                    notices.Add(new UdpPortNotice(
                        UdpPortNoticeKind.Taken,
                        $"server-udp-ports takes UDP {local}, and some of it is already taken: "
                        + string.Join(" ", taken)));
                }

                break;
        }

        // Absent always adds its own notice, so this arm has to come before the
        // notices are consulted or an unset key would read as a faulted one.
        //
        // `T241`. FAULT notices decide the stance; the rest are reported and change
        // nothing. A moved address is a notice and not a fault, because the remarks
        // above say in as many words that nothing here can tell a stale published
        // address from a deliberate one -- and a stance of Faulted composes a
        // "Replace ... with this:" line over whatever the administrator wrote.
        var stance = configured.Kind switch
        {
            UdpPortsKind.Absent => UdpPortAdviceStance.Unset,
            _ when notices.Any(n => n.IsFault) => UdpPortAdviceStance.Faulted,
            _ => UdpPortAdviceStance.Settled,
        };

        // Withheld here rather than at the render, so that a caller which never
        // reads the stance still cannot tell somebody to overwrite a working
        // value. Over-gating costs a sentence; under-gating costs them the
        // configuration they wrote.
        // From the server port up, past anything in the way (`T415`).
        var suggestedBase = claims is not null && maxPlayers is > 0
            ? NewServerPorts.FreeWindow(claims, basePort, maxPlayers.Value)
            : basePort;
        var suggested = stance is UdpPortAdviceStance.Settled || suggestedBase is not { } free
            ? null
            : ServerUdpPorts.Suggest(publicAddress, free, maxPlayers);

        return new UdpPortAdvice(
            Stance: stance,
            Lead: LeadFor(stance, configured, suggested),
            Suggested: suggested,
            SuggestionBlockedBy: stance is not UdpPortAdviceStance.Settled && suggested is null
                ? BlockedBecause(basePort, maxPlayers)
                : null,
            Notices: notices);
    }

    /// <summary>
    /// The sentence above the block, which is where the key's own state is
    /// reported back.
    /// </summary>
    /// <remarks>
    /// <para><strong>The settled arm exists because a block that merely goes
    /// quiet cannot be told from one that has not noticed.</strong> Somebody who
    /// applies the line, reloads, and reads nothing has no way to distinguish
    /// <em>this took</em> from <em>this was ignored</em>, so the value is said
    /// back to them.</para>
    ///
    /// <para><strong>The faulted arm deliberately does not quote the current
    /// value</strong>, because the notices printed immediately above it already
    /// do wherever the value is the thing at fault. What it has to carry is the
    /// word <em>replace</em>: the two states this whole type exists to keep
    /// apart are a blank being filled and a value being overwritten.</para>
    /// </remarks>
    private static string? LeadFor(
        UdpPortAdviceStance stance, ServerUdpPorts configured, string? suggested)
    {
        if (stance is UdpPortAdviceStance.Settled)
        {
            // Every value this reads today arrives through Parse, which keeps the
            // line it read. One that does not is still set, and saying so without
            // the value beats quoting an empty string as though it were one.
            return string.IsNullOrEmpty(configured.Value)
                ? "server-udp-ports is set, and nothing here has a change to suggest."
                : $"server-udp-ports is set to \"{configured.Value}\". Play sockets come "
                  + "from the ports it names, and nothing here has a change to suggest.";
        }

        // No line to introduce: BlockedBecause is the whole of what is left to
        // say, and a lead above it would be introducing nothing.
        if (suggested is null)
        {
            return null;
        }

        // Exhaustive over the enum rather than falling through to the Unset
        // sentence, which would hand a server the key does not apply to an
        // instruction to set it -- this entry's own defect one call along.
        return stance switch
        {
            UdpPortAdviceStance.Faulted =>
                "Replace the server-udp-ports line in server.properties with this:",
            UdpPortAdviceStance.Unset =>
                "Put this in server.properties to pin the play ports and publish them:",
            _ => null,
        };
    }

    private static string BlockedBecause(int basePort, int? maxPlayers) =>
        maxPlayers is not { } players || players < 1
            ? "Set max-players and this fills in — the range has to be one port wide per player, "
              + "and BDS's own default for that key is not read from anywhere here."
            : $"A range {players} wide starting at {basePort} would run past port 65535.";
}
