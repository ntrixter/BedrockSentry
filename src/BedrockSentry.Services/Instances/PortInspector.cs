using BedrockSentry.Core.Instances;

namespace BedrockSentry.Services.Instances;

/// <summary>A reason a port cannot be used, in words an administrator can act on.</summary>
public sealed record PortConflict(int Port, string Reason);

/// <summary>
/// What one instance's <c>server.properties</c> says about its network: which
/// transport, and therefore which sockets (<c>D70</c>).
/// </summary>
/// <param name="Transport">
/// Null when nothing here recognises the file's value — <em>or</em> when there is
/// no file yet. <paramref name="Value"/> tells those apart.
/// </param>
/// <param name="Value">
/// The file's literal <c>transport</c> value, or null when the key is unset or
/// there is no file. Only worth reading when <paramref name="Transport"/> is null,
/// which is exactly when somebody has to be shown what the file actually says.
/// </param>
/// <remarks>
/// One read answering both questions, because the Status card has to name the
/// transport and the pre-flight has to derive the sockets, and two readers of one
/// file come to disagree about a missing one.
/// </remarks>
/// <param name="Unreadable">
/// Why the file could not be read, when it is there and something else has it
/// (<c>T210</c>). Null in every other case, including no file at all.
/// </param>
/// <param name="UdpPorts">
/// What <c>server-udp-ports</c> says, across every set line (<c>D71</c>, <c>D72</c>).
/// <see cref="ServerUdpPorts.Absent"/> when the file could not be read at all,
/// which is the same sentence <see cref="Unreadable"/> is already carrying rather
/// than a second claim about the key.
/// </param>
/// <param name="MaxPlayers">
/// Its <c>max-players</c>, null when the key is unset — the input the suggested
/// play-port range's width comes from.
/// </param>
public readonly record struct InstancePorts(
    ServerTransport? Transport,
    string? Value,
    IReadOnlyList<ServerPort> Ports,
    string? Unreadable = null,
    ServerUdpPorts? UdpPorts = null,
    int? MaxPlayers = null)
{
    /// <summary>Where this server's play traffic lands (<c>D71</c>).</summary>
    public PlayPorts Play => PlayPorts.For(Transport, PortNumber, UdpPorts);

    /// <summary>
    /// The <c>server-port</c> this model was built from — BDS's own default when
    /// the key is unset, and also when there are no ports at all, which is the
    /// unreadable-file case.
    /// </summary>
    /// <remarks>
    /// Taken back off <see cref="Ports"/> rather than stored beside it. Both
    /// transports put <c>server-port</c> first and a second field holding the same
    /// number is the shape a rename drifts through (<c>T89</c>).
    /// </remarks>
    public int PortNumber =>
        Ports.FirstOrDefault(p => p.Key == ServerPorts.PortKey) is { Port: > 0 } named
            ? named.Port
            : ServerPorts.DefaultPort;

    /// <summary>
    /// What to tell an administrator about this server's play ports, given the
    /// machine's public address (<c>D71</c>).
    /// </summary>
    /// <param name="claims">What else holds ports, from <see cref="PortInspector.ClaimsBesides(string)"/>.</param>
    public UdpPortAdvice AdviceGiven(string? publicAddress, IReadOnlyList<PortClaim>? claims = null) =>
        UdpPortAdvice.For(Transport, PortNumber, MaxPlayers, UdpPorts, publicAddress, claims);

    /// <summary>
    /// What to call this server's transport, in the four states the file can be
    /// in.
    /// </summary>
    /// <remarks>
    /// <para>Here rather than in <c>StatusTab.razor</c>, which the test project
    /// cannot reference at all — the standing reason a rule this shape keeps being
    /// stated twice and enforced once.</para>
    ///
    /// <para><strong>Four states, not three</strong> (<c>T210</c>). A file that is
    /// not there and a file that cannot be read both left every field null, so an
    /// instance whose <c>server.properties</c> something else had open was
    /// described as one that has never started. They are opposite situations: one
    /// is a server that has not run yet, the other is a server that may well be
    /// running right now on ports nobody managed to look up.</para>
    ///
    /// <para>No file at all is <em>not</em> RakNet: BDS writes one at first start,
    /// and a post-1.26.50 BDS writes <c>nethernet</c> into it.</para>
    /// </remarks>
    public string TransportLabel => this switch
    {
        { Transport: ServerTransport.RakNet } => "RakNet",
        { Transport: ServerTransport.NetherNet } => "NetherNet",
        { Value: { Length: > 0 } unknown } => $"\"{unknown}\" — not recognised",
        { Unreadable: { Length: > 0 } } => "— server.properties could not be read",
        _ => "— no server.properties yet",
    };
}

/// <summary>
/// Answers "is this port free" before a port change is saved rather than after
/// the server fails to start because of it.
/// </summary>
/// <remarks>
/// A port collision is the single most confusing way for a Bedrock server to
/// fail. The process starts, binds nothing, and exits — the wrapper sees a
/// crash, retries, and reports a crash loop with no hint that the cause is
/// another program holding 19132. Checking first turns that into a sentence.
/// </remarks>
public sealed class PortInspector : IStartPreflight
{
    /// <summary>
    /// The <c>server.properties</c> key the suggested play-port range's width
    /// comes from (<c>D71</c>) — one port per player online.
    /// </summary>
    public const string MaxPlayersKey = "max-players";

    private readonly InstanceRegistry _registry;

    public PortInspector(InstanceRegistry registry) => _registry = registry;

    /// <summary>
    /// The start path's question, answered from the same two sources the
    /// Readiness panel uses: the instance's own file for which ports it wants,
    /// and <see cref="Check"/> for who has them (<c>T149</c>).
    /// </summary>
    /// <remarks>
    /// Never skips the system check. The panel can afford to be advisory; a
    /// launch cannot, because the thing being prevented is a second process
    /// opening a world that a first one — very possibly this instance's own
    /// orphan after a service restart — still has open.
    /// </remarks>
    public IReadOnlyList<PortConflict> ConflictsForStart(string instanceId, string workingDirectory) =>
        BindsOf(workingDirectory)
            .SelectMany(p => Check(instanceId, p.Port, p.Protocol, liveOnly: true))
            .ToList();

    /// <inheritdoc />
    /// <remarks>
    /// Reads the live socket table rather than the registry, because a
    /// predecessor is by definition not in this process's registry — it is
    /// another process, on its way out, whose sockets outlive the service state
    /// Windows reports. Grouped by port so a host bound to both address families
    /// answers once.
    /// </remarks>
    public IReadOnlyList<int> PredecessorHolds(string workingDirectory)
    {
        var wanted = PortsOf(workingDirectory);

        if (wanted.Count == 0)
        {
            return [];
        }

        // Asked per protocol, because a predecessor holding a NetherNet
        // instance's TCP listener leaves no row in the UDP table at all - the
        // wait would never start and the launch would go straight into the held
        // port, which is the whole of T200.
        return wanted
            .GroupBy(p => p.Protocol)
            .SelectMany(byProtocol =>
            {
                var ports = byProtocol.Select(p => p.Port).ToHashSet();

                return ListeningSockets.On(byProtocol.Key)
                    .Where(listener => ports.Contains(listener.Port))
                    .GroupBy(listener => listener.Port)
                    .Where(byPort => byPort
                        .Select(listener => listener.ProcessId)
                        .Distinct()
                        .Any(pid => UdpListeners.HoldKindOf(pid) == UdpListeners.WrapperHold.Predecessor))
                    .Select(byPort => byPort.Key);
            })
            .Distinct()
            .Order()
            .ToList();
    }

    /// <summary>
    /// The <em>panel's</em> question: everything worth telling a person about
    /// <paramref name="instanceId"/>'s ports, including a stopped sibling whose
    /// file names the same one.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately <strong>not</strong> <see cref="ConflictsForStart"/>, and the
    /// difference is the whole point of this method existing (<c>T151</c> clause 6).
    /// The two share <see cref="PortsOf"/>, <see cref="Check"/> and
    /// <c>PortConflictText</c> — so the port discovery and the sentences cannot
    /// drift — and differ on exactly one thing, <c>liveOnly</c>, which is the one
    /// thing they must differ on.</para>
    ///
    /// <para><strong>This regressed once and the way it regressed is worth knowing.</strong>
    /// The panel used to call <see cref="Check"/> with its default and was moved onto
    /// <see cref="ConflictsForStart"/> so that "the two callers cannot drift". They
    /// could not — they agreed on the start path's answer, which is the wrong answer
    /// here, and the panel silently stopped reporting config-only collisions for a
    /// whole release. Sharing an implementation is not the same as sharing an answer.
    /// The doc on <c>liveOnly</c> said so at the time and did not prevent it, which is
    /// why <c>InstanceCreationTests</c> now pins the two to different values instead.</para>
    /// </remarks>
    public IReadOnlyList<PortConflict> ConflictsToShow(string instanceId, string workingDirectory) =>
        BindsOf(workingDirectory)
            .SelectMany(p => Check(instanceId, p.Port, p.Protocol))
            .ToList();

    /// <summary>
    /// Every reason <paramref name="port"/> is unavailable to
    /// <paramref name="instanceId"/>, or an empty list when it is free.
    /// </summary>
    /// <param name="skipSystemCheck">
    /// Leaves the machine's own sockets out of it. For tests, which cannot
    /// depend on what happens to be listening on the box they run on.
    /// </param>
    /// <param name="liveOnly">
    /// Only what has the port open <em>right now</em>; another instance's file
    /// merely naming the same port is not reported. The start path wants this
    /// (<c>T149</c>): the thing it prevents is a second process opening a world,
    /// and a stopped sibling's <c>server.properties</c> is not a process. The
    /// adoption form already promises such a pair that "only one of them can run
    /// at a time" — refusing both would be that promise broken. The panel keeps
    /// the default, because for a person reading it the file collision is worth
    /// knowing about before either server is started.
    /// <para>Which caller takes which is not a matter of comment any more:
    /// <see cref="ConflictsForStart"/> passes <c>true</c>,
    /// <see cref="ConflictsToShow"/> takes the default, and a test asserts they
    /// disagree. This paragraph is a description, not the guard.</para>
    /// </param>
    /// <param name="protocol">
    /// Which socket table to look in, and which of another instance's ports count
    /// as the same port. A <see cref="ServerTransport.NetherNet"/> server holds
    /// <c>server-port</c> over TCP, so asking the UDP table about it returns
    /// <em>free</em> whoever has it (<c>D70</c>).
    /// </param>
    public IReadOnlyList<PortConflict> Check(
        string instanceId,
        int port,
        PortProtocol protocol,
        bool skipSystemCheck = false,
        bool liveOnly = false)
    {
        var conflicts = new List<PortConflict>();

        if (port is < 1 or > 65535)
        {
            conflicts.Add(new PortConflict(port, "is not a port number; the range is 1 to 65535"));
            return conflicts;
        }

        // Which running process belongs to which instance, so a socket found
        // below can be named rather than reported as an anonymous program.
        var instanceOf = new Dictionary<int, string>();
        int? ownProcess = null;

        // The ports this instance's own file claims, so the wrapper's standing hold
        // on the discovery pair can be recognised as a reservation kept *for* it
        // rather than reported as something in its way.
        var ownPorts = new HashSet<ServerPort>();

        // Instances this port has already been blamed on, so a server whose
        // running sockets agree with its own file is not reported twice.
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var other in _registry.All)
        {
            var mine = other.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase);

            if (mine)
            {
                ownProcess = other.Status.ProcessId;

                foreach (var own in BindsOf(other.WorkingDirectory))
                {
                    ownPorts.Add(own);
                }

                continue;
            }

            if (other.Status.ProcessId is { } processId)
            {
                instanceOf[processId] = other.Definition.Name;
            }

            // The name map above is still wanted so a live socket can be
            // attributed; only the file's claim is left out. Nothing is added to
            // `named` either, so a sibling that really is running on this port is
            // reported by its socket below rather than suppressed as already said.
            if (liveOnly)
            {
                continue;
            }

            // Matched on the protocol too, which is what stops the sentence
            // naming a `server-portv6` a NetherNet instance's file has but its
            // server ignores -- PortsOf does not return one for it at all.
            //
            // It no longer excuses a RakNet sibling's UDP 19132 against a
            // NetherNet instance on the same number, and the comment here said it
            // did: play lands on that number's UDP side unless a window is set
            // (`D71`), so those two servers cannot both run and the refusal is
            // correct. The protocol match reaches the same answer either way,
            // because PortsOf is what changed -- which is why nothing failed and
            // only this sentence was left saying the opposite.
            foreach (var used in BindsOf(other.WorkingDirectory))
            {
                if (used.Port == port && used.Protocol == protocol)
                {
                    conflicts.Add(new PortConflict(port, $"is {other.Definition.Name}'s {used.Key}"));
                    named.Add(other.Definition.Name);
                }
            }
        }

        if (skipSystemCheck)
        {
            return conflicts;
        }

        // Deduplicated by owner: a server bound to both address families shows
        // up twice on the same port, which is one conflict, not two.
        var owners = ListeningSockets.On(protocol)
            .Where(listener => listener.Port == port)
            .Select(listener => listener.ProcessId)
            .Distinct();

        foreach (var owner in owners)
        {
            // This instance's own server. Reporting it would flag the properties
            // page of every running instance for the port it is running on.
            if (ownProcess is { } own && owner == own)
            {
                continue;
            }

            // The wrapper's own standing hold on the LAN discovery pair, kept for this
            // very instance (`D24`, `T82`). Not a conflict — a reservation, released
            // by this instance's own start, and the reason it *can* bind rather than a
            // reason it cannot. Reported as a conflict it made the one instance
            // guaranteed to get its ports the only one carrying a warning, under the
            // crash-loop wording meant for a genuine collision, and pointed the
            // operator at remedies that make things worse: `T82` records that
            // enable-lan-visibility=false leaves a server nothing can join (`T113`).
            //
            // Only our own wrapper, and only for a port this instance's own file
            // claims. A foreign holder, or another instance's server that has taken
            // the pair, still reports — which is the case `T72` covers and the one
            // that genuinely stops a start.
            //
            // "Our own wrapper" includes a predecessor of this process, not just
            // this process. `T82` measured the outgoing service keeping these
            // sockets open for 6.4 seconds after the SCM reported it stopped; on
            // every restart and self-update the incoming wrapper starts the pair's
            // owner inside that window, and now that a start can be refused
            // (`T149`), a predecessor counted as a foreign holder would fault that
            // one instance on every boot. It is not a second process on the world;
            // it is the same reservation, a few seconds from being released.
            //
            // The hold is a UdpClient, so it can only ever be a reason a UDP port
            // looks taken. A TCP listener on the same number is something else
            // entirely and still reports - which is why the protocol is part of
            // the match rather than the number alone (`D70`).
            if (UdpListeners.IsThisWrapper(owner)
                && ownPorts.Any(own => own.Port == port && own.Protocol == protocol))
            {
                continue;
            }

            if (instanceOf.TryGetValue(owner, out var instanceName))
            {
                // A managed server whose live sockets disagree with its own
                // file — usually because the file was edited and the server has
                // not been restarted. Worth saying; worth saying only once.
                if (named.Add(instanceName))
                {
                    conflicts.Add(new PortConflict(
                        port, $"is open right now by {instanceName}'s running server"));
                }

                continue;
            }

            conflicts.Add(new PortConflict(
                port, $"is already open on this machine by {UdpListeners.Describe(owner)}"));
        }

        return conflicts;
    }

    /// <summary>
    /// Every port a new server could run into (<c>ON1</c>): each instance's sockets as
    /// its own file names them, its play window included, and whatever else on this
    /// machine is listening.
    /// </summary>
    public IReadOnlyList<PortClaim> Claims() => Claims(ListeningSockets.On, UdpListeners.IsThisWrapper);

    /// <inheritdoc cref="Claims()" />
    /// <param name="listening">The socket tables; a seam, since a test cannot choose what its machine has open.</param>
    /// <param name="isThisWrapper">Whether a process is this wrapper, whose UDP sockets are the LAN discovery hold.</param>
    /// <remarks>
    /// <para>The files rather than the running servers, as <see cref="ConflictsToShow"/>
    /// reads them: a stopped server still wants its ports back when it starts.</para>
    /// <para>A running instance's own sockets and the wrapper's discovery hold
    /// (<c>D24</c>) are left out of the machine's half, because each is a server's
    /// file claim already listed, and a second sentence about the same port names
    /// "another program" for something the panel runs.</para>
    /// </remarks>
    internal IReadOnlyList<PortClaim> Claims(
        Func<PortProtocol, IReadOnlyList<SocketListener>> listening,
        Func<int, bool> isThisWrapper) =>
        Claims(listening, isThisWrapper, UdpListeners.Describe);

    /// <inheritdoc cref="Claims()" />
    /// <param name="listening">The socket tables.</param>
    /// <param name="isThisWrapper">Whether a process is this wrapper.</param>
    /// <param name="describe">How a process is named in a claim.</param>
    /// <remarks>
    /// Each process is asked about once per call, however many sockets it holds
    /// (<c>T426</c>). On Windows both questions open the process by id, and asked per
    /// socket on a machine with hundreds open they took seconds, on every render of a
    /// page that reads this.
    /// </remarks>
    internal IReadOnlyList<PortClaim> Claims(
        Func<PortProtocol, IReadOnlyList<SocketListener>> listening,
        Func<int, bool> isThisWrapper,
        Func<int, string> describe)
    {
        isThisWrapper = Once(isThisWrapper);
        describe = Once(describe);

        var claims = FileClaims(_registry.All).ToList();
        var managed = _registry.All
            .Select(instance => instance.Status.ProcessId)
            .OfType<int>()
            .ToHashSet();

        foreach (var protocol in new[] { PortProtocol.Udp, PortProtocol.Tcp })
        {
            foreach (var socket in listening(protocol).Distinct())
            {
                if (managed.Contains(socket.ProcessId)
                    || (protocol is PortProtocol.Udp && socket.ProcessId != 0 && isThisWrapper(socket.ProcessId)))
                {
                    continue;
                }

                claims.Add(new PortClaim(
                    new PortSpan(socket.Port, socket.Port),
                    protocol,
                    $"already open on this machine by {describe(socket.ProcessId)}"));
            }
        }

        return claims;
    }

    private static Func<int, T> Once<T>(Func<int, T> ask)
    {
        var answers = new Dictionary<int, T>();

        return id =>
        {
            if (!answers.TryGetValue(id, out var answer))
            {
                answer = ask(id);
                answers[id] = answer;
            }

            return answer;
        };
    }

    /// <summary>
    /// What one server's ports could run into: <see cref="Claims()"/> less what its own
    /// file names, so its window is not reported as taken by itself (<c>T415</c>).
    /// </summary>
    public IReadOnlyList<PortClaim> ClaimsBesides(string instanceId) =>
        ClaimsBesides(instanceId, ListeningSockets.On, UdpListeners.IsThisWrapper);

    /// <inheritdoc cref="ClaimsBesides(string)" />
    internal IReadOnlyList<PortClaim> ClaimsBesides(
        string instanceId,
        Func<PortProtocol, IReadOnlyList<SocketListener>> listening,
        Func<int, bool> isThisWrapper)
    {
        var own = FileClaims(_registry.All.Where(
                instance => instance.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase)))
            .ToHashSet();

        return Claims(listening, isThisWrapper).Where(claim => !own.Contains(claim)).ToList();
    }

    /// <summary>
    /// The first port from <paramref name="start"/> up where a RakNet server clashes with
    /// no instance's file and nothing open on this machine: restore-as-new's suggestion.
    /// </summary>
    /// <remarks>
    /// The machine's sockets as well as the files (<c>T404</c>): from the files alone it
    /// offered a port another program, a second wrapper's server among them, already had.
    /// </remarks>
    public int SuggestPort(int start = ServerPorts.DefaultPort) =>
        SuggestPort(ListeningSockets.On, UdpListeners.IsThisWrapper, start);

    /// <inheritdoc cref="SuggestPort(int)" />
    internal int SuggestPort(
        Func<PortProtocol, IReadOnlyList<SocketListener>> listening, Func<int, bool> isThisWrapper, int start = ServerPorts.DefaultPort) =>
        NewServerPorts.Next(Claims(listening, isThisWrapper), ServerTransport.RakNet, pinWindow: false, start) ?? start;

    /// <summary>
    /// The sockets each of <paramref name="instances"/> binds as its own file names
    /// them, its play window included: the half of <see cref="Claims()"/> that needs no
    /// socket table, which <see cref="InstanceRegistry.SuggestPort"/> reads too.
    /// </summary>
    public static IEnumerable<PortClaim> FileClaims(IEnumerable<InstanceSupervisor> instances)
    {
        foreach (var instance in instances)
        {
            var model = ModelOf(instance.WorkingDirectory);
            var name = instance.Definition.Name;

            foreach (var port in model.Ports)
            {
                yield return new PortClaim(new PortSpan(port.Port, port.Port), port.Protocol, $"{name}'s {port.Key}");
            }

            if (model.Play is { Kind: PlayPortKind.Pinned, Span: { } window })
            {
                yield return new PortClaim(window, PortProtocol.Udp, $"in {name}'s {ServerUdpPorts.Key}");
            }
        }
    }

    /// <summary>The sockets an instance binds, as its own file describes them.</summary>
    /// <remarks>
    /// <para>Only <c>server-port</c> and <c>server-portv6</c>. LAN visibility used
    /// to add 19132 and 19133 here on top of those. Bedrock really does take the
    /// discovery pair whatever the configured ports say, but only the first
    /// instance started gets them; the rest soft-fail and run on their own ports
    /// without retrying. Listing the pair as held by every such instance turned one
    /// contended resource into a conflict between every pair of instances, and
    /// rejected the save that would have turned the setting on. <c>D24</c> covers
    /// how the pair should be arbitrated — it is a start-order question, not
    /// something a saved file can answer.</para>
    ///
    /// <para><strong>Which of those two ports exist, and on what protocol, comes
    /// from the file's own <c>transport</c> key</strong> (<c>D70</c>). The rule
    /// itself is <see cref="ServerPorts.For"/> in <c>Core</c>, where the Status
    /// card and a unit test can both reach it; this method is only the reading of
    /// the file.</para>
    /// </remarks>
    public static IReadOnlyList<ServerPort> PortsOf(string workingDirectory) =>
        ModelOf(workingDirectory).Ports;

    /// <summary>
    /// <see cref="PortsOf"/> and every port of a NetherNet server's pinned play window
    /// (<c>D71</c>): what a start and the panel check for another holder.
    /// </summary>
    /// <remarks>
    /// A window port another program held let the server start, took the signalling
    /// handshake, and left every player at "Kelp" with nothing naming the port
    /// (<c>T410</c>: a second install's host held UDP 19132, the window's only port).
    /// </remarks>
    public static IReadOnlyList<ServerPort> BindsOf(string workingDirectory)
    {
        var model = ModelOf(workingDirectory);

        if (model.Play is not { Kind: PlayPortKind.Pinned, Span: { } window })
        {
            return model.Ports;
        }

        return
        [
            .. model.Ports,
            .. Enumerable.Range(window.First, window.Width)
                .Select(port => new ServerPort(ServerUdpPorts.Key, port, PortProtocol.Udp)),
        ];
    }

    /// <summary>
    /// What the outside world has to reach for players to connect to the instance
    /// at <paramref name="workingDirectory"/> (<c>T225</c>).
    /// </summary>
    /// <remarks>
    /// Beside <see cref="PortsOf"/> rather than derived from it by a caller: the
    /// two answer different questions and the firewall surface was reading the
    /// wrong one.
    /// </remarks>
    public static IReadOnlyList<ReachableSockets> ReachableOn(string workingDirectory)
    {
        var model = ModelOf(workingDirectory);

        return ServerPorts.ReachableFor(
            model.Transport, model.PortNumber, portV6: null, model.UdpPorts);
    }

    /// <summary>
    /// The transport an instance's file names and the sockets that follow from it.
    /// </summary>
    /// <remarks>See <see cref="PortsOf"/>, which is this without the label.</remarks>
    public static InstancePorts ModelOf(string workingDirectory) =>
        ModelOf(workingDirectory, File.ReadAllLines);

    /// <inheritdoc cref="ModelOf(string)" />
    /// <param name="read">
    /// How the file is read. A seam, not a configuration point (<c>T222</c>).
    /// </param>
    /// <remarks>
    /// <para><strong>The seam exists because the arm that matters cannot be
    /// reached on the platform this is written on.</strong> The interesting failure
    /// is an ACL denial, and on Linux the build runs as root, which bypasses
    /// permissions — so a rig that chmods a file away <em>succeeds</em> in reading
    /// it and the test proves nothing. `T176`'s rule: when a failure mode is not
    /// reproducible where the code is written, put in a seam rather than skipping
    /// the test.</para>
    ///
    /// <para>It earned itself immediately. Narrowing the catch back to
    /// <c>IOException</c> left the whole suite green, which is how an ACL-denied
    /// file came to throw out of the Readiness panel, the start path and the
    /// registry with nothing anywhere to notice.</para>
    /// </remarks>
    internal static InstancePorts ModelOf(string workingDirectory, Func<string, string[]> read)
    {
        var path = ServerProperties.PathFor(workingDirectory);

        // A file that is not there yet is not the same case as a key that is not
        // set, and this is the distinction D70 turns on. An absent `transport`
        // key in a file that exists is RakNet — measured, on a server whose file
        // predates the key (`D75`). A file that does not exist yet says nothing at
        // all: BDS writes one at first start, and what a post-1.26.50 BDS writes
        // into it is `transport=nethernet`. So that case is the unknown transport
        // and gets ServerPorts.For's over-gating union rather than the RakNet pair
        // it used to get, which would have been a false clear the moment such an
        // instance started (`T151` clause 8b is the fixture for this shape).
        ServerPropertiesDocument? document = null;

        if (File.Exists(path))
        {
            try
            {
                document = ServerPropertiesDocument.Parse(read(path));
            }
            // UnauthorizedAccessException is NOT an IOException, so an ACL-denied
            // file used to escape this handler entirely and throw out of the whole
            // Readiness panel build -- and out of the start path and the registry,
            // which call the same method (`T222`). A file that exists and cannot be
            // read is one state however Windows refuses it; catching only half the
            // refusals left a fifth state the remarks above do not list, and an
            // unhandled one is worse than either of the two they warn about.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Being unable to READ a file that exists is different again:
                // something else has it open and we cannot know what it says.
                // Falling back to the defaults would invent ports for a server
                // that may well name others, so this one keeps returning nothing.
                //
                // AND RETURNING NOTHING IS NOT THE SAME AS SAYING NOTHING (`T210`).
                // An empty port list yields an empty conflict list, which rendered
                // Good and "Its ports are free." — a green assurance about ports
                // this never managed to look up, measured against a port a foreign
                // process demonstrably held. The reason travels with the emptiness
                // so a reader can tell the two apart; two paragraphs below, this
                // same method already says an assurance is worse than an absence.
                return new InstancePorts(null, null, [], ex.Message);
            }
        }

        if (document is null)
        {
            return new InstancePorts(
                null, null, ServerPorts.For(transport: null, port: null, portV6: null));
        }

        // A key that is absent, commented out, or unparseable is not "no port" —
        // BDS runs on its own default, binds it, and holds it against everything
        // else. Reporting nothing left such a server with a GREEN Readiness row —
        // no conflicts to report, so "Its ports are free." — and, once `T151`
        // landed, no start pre-flight either: the launch asked about an empty list
        // of ports and could never be refused. An assurance is worse than an
        // absence, because it is acted on (`T202`). That is a hole in the
        // protection `T149` exists to provide, and it is closed here rather than in
        // either reader, so the panel and the supervisor keep answering from one
        // source.
        //
        // `SetValueOf` returns null for a commented `#server-port=19132`, which is
        // the case this most often is: the shipped file documents the default
        // rather than setting it.
        var value = document.SetValueOf(ServerTransports.PropertyKey);
        var transport = ServerTransports.Parse(value);

        // ValuesOf rather than SetValueOf, because BDS acts on every set line of
        // this one key and the first line is a fraction of the configuration
        // (`D72`). Reading it with the same call the writer collapses with is what
        // stops the two sides of one key disagreeing again.
        var udpPorts = ServerUdpPorts.ParseAll(
            document.ValuesOf(ServerPropertiesDocument.AccumulatingKey));

        return new InstancePorts(
            transport,
            value,
            ServerPorts.For(
                transport,
                NumberAt(ServerPorts.PortKey),
                NumberAt(ServerPorts.PortV6Key),
                udpPorts),
            Unreadable: null,
            UdpPorts: udpPorts,
            MaxPlayers: NumberAt(MaxPlayersKey));

        int? NumberAt(string key) =>
            document.SetValueOf(key) is { } text && int.TryParse(text, out var value)
                ? value
                : null;
    }
}
