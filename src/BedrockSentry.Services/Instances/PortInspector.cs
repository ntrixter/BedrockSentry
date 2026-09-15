
namespace BedrockSentry.Services.Instances;

/// <summary>A reason a port cannot be used, in words an administrator can act on.</summary>
public sealed record PortConflict(int Port, string Reason);

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
        PortsOf(workingDirectory)
            .SelectMany(p => Check(instanceId, p.Port, liveOnly: true))
            .ToList();

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
        PortsOf(workingDirectory)
            .SelectMany(p => Check(instanceId, p.Port))
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
    public IReadOnlyList<PortConflict> Check(string instanceId, int port, bool skipSystemCheck = false, bool liveOnly = false)
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
        var ownPorts = new HashSet<int>();

        // Instances this port has already been blamed on, so a server whose
        // running sockets agree with its own file is not reported twice.
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var other in _registry.All)
        {
            var mine = other.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase);

            if (mine)
            {
                ownProcess = other.Status.ProcessId;

                foreach (var (_, own) in PortsOf(other.WorkingDirectory))
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

            foreach (var (key, used) in PortsOf(other.WorkingDirectory))
            {
                if (used == port)
                {
                    conflicts.Add(new PortConflict(port, $"is {other.Definition.Name}'s {key}"));
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
        var owners = UdpListeners.All()
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
            if (UdpListeners.IsThisWrapper(owner) && ownPorts.Contains(port))
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

    /// <summary>The ports an instance occupies, as its own file describes them.</summary>
    /// <remarks>
    /// Only <c>server-port</c> and <c>server-portv6</c>. LAN visibility used to
    /// add 19132 and 19133 here on top of those. Bedrock really does take the
    /// discovery pair whatever the configured ports say, but only the first
    /// instance started gets them; the rest soft-fail and run on their own ports
    /// without retrying. Listing the pair as held by every such instance turned
    /// one contended resource into a conflict between every pair of instances,
    /// and rejected the save that would have turned the setting on. D24 covers
    /// how the pair should be arbitrated — it is a start-order question, not
    /// something a saved file can answer.
    /// </remarks>
    public static IReadOnlyList<(string Key, int Port)> PortsOf(string workingDirectory)
    {
        var path = ServerProperties.PathFor(workingDirectory);

        // A file that is not there yet is the same case as a key that is not set:
        // BDS will run on its own defaults and bind them (`T151` clause 8b). It
        // used to return nothing, so such an instance had no Readiness row and no
        // start pre-flight at all — the exact hole clause 8 closed for the
        // commented-out shape, still open for the no-file one. Two instances on
        // the box were in that state.
        ServerPropertiesDocument? document = null;

        if (File.Exists(path))
        {
            try
            {
                document = ServerPropertiesDocument.Parse(File.ReadAllLines(path));
            }
            catch (IOException)
            {
                // Being unable to READ a file that exists is different: something
                // else has it open and we cannot know what it says. Falling back
                // to the defaults here would invent ports for a server that may
                // well name others, so this one keeps returning nothing.
                return [];
            }
        }

        var ports = new List<(string, int)>();

        // A key that is absent, commented out, or unparseable is not "no port" —
        // BDS runs on its own default, binds it, and holds it against everything
        // else. Reporting nothing left such a server with no Readiness row and,
        // once `T151` landed, no start pre-flight either: the launch asked about
        // an empty list of ports and could never be refused. That is a hole in
        // the protection `T149` exists to provide, and it is closed here rather
        // than in either reader, so the panel and the supervisor keep answering
        // from one source.
        //
        // `SetValueOf` returns null for a commented `#server-port=19132`, which
        // is the case this most often is: the shipped file documents the default
        // rather than setting it.
        foreach (var (key, fallback) in ((string Key, int Fallback)[])
                 [
                     ("server-port", LanDiscoveryPortGuard.DefaultPortV4),
                     ("server-portv6", LanDiscoveryPortGuard.DefaultPortV6),
                 ])
        {
            ports.Add((
                key,
                document?.SetValueOf(key) is { } text && int.TryParse(text, out var value)
                    ? value
                    : fallback));
        }

        return ports;
    }

}
