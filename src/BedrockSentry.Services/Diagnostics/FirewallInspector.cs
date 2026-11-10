using System.Text.Json;
using System.Text.RegularExpressions;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// Whether Windows Firewall lets players reach a server's port, and — on
/// request — a rule that does.
/// </summary>
/// <remarks>
/// "Nobody outside can connect" is the most common problem with a self-hosted
/// game server, and an inbound firewall rule is the most common cause. It is
/// also the one an administrator is least likely to check, because the server
/// itself reports no error at all: it binds the port, says it started, and waits
/// for packets that never arrive.
/// </remarks>
public sealed class FirewallInspector : IFirewallService
{
    private readonly IPowerShellRunner _powerShell;
    private readonly ILogger<FirewallInspector> _log;

    public FirewallInspector(IPowerShellRunner powerShell, ILogger<FirewallInspector> log)
    {
        _powerShell = powerShell;
        _log = log;
    }

    /// <summary>The rule BedrockSentry would create for a port.</summary>
    /// <remarks>
    /// Named after the instance and the port so it is obvious in the firewall
    /// UI what created it and why — and so somebody who wants it gone can find
    /// and delete it without knowing anything about this wrapper.
    /// </remarks>
    public static string RuleName(string instanceName, int port, PortProtocol protocol) =>
        RuleName(instanceName, new PortSpan(port, port), protocol);

    /// <summary>
    /// The rule name for a run of ports — a pinned play window is one rule rather
    /// than <c>max-players</c> of them (<c>T225</c>).
    /// </summary>
    public static string RuleName(string instanceName, PortSpan ports, PortProtocol protocol) =>
        $"BedrockSentry - {instanceName} ({Name(protocol)} {ports})";

    /// <summary>
    /// What every name <see cref="RuleName(string, PortSpan, PortProtocol)"/> gives
    /// this server matches and no other server's does (<c>T400</c>).
    /// </summary>
    /// <remarks>
    /// Anchored on both ends with the protocol and port spelling, because a prefix
    /// alone is shared: "A (UDP 1)" is a legal server name, and its rules begin with
    /// "BedrockSentry - A (". The script runs it on .NET's regex engine, the same one
    /// <c>FirewallRuleNameTests</c> runs it on.
    /// </remarks>
    public static string RulePattern(string instanceName) =>
        $@"^BedrockSentry - {Regex.Escape(instanceName)} \((TCP|UDP) [0-9]+(-[0-9]+)?\)$";

    /// <summary>
    /// How a protocol is spelt in a rule name, a row title and the
    /// <c>New-NetFirewallRule</c> and <c>Get-NetFirewallPortFilter</c> vocabulary,
    /// which happen to agree. One spelling, because a rule created under one and
    /// looked for under another is a rule that never matches its own check.
    /// </summary>
    public static string Name(PortProtocol protocol) =>
        protocol == PortProtocol.Tcp ? "TCP" : "UDP";

    /// <summary>Reads the machine's inbound rules, once.</summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="Check"/> because the answer is a property of the
    /// machine rather than of any one instance. Asking per instance meant running
    /// an identical whole-firewall dump once for every server on the box and
    /// throwing away all but one port's worth of it.
    /// </para>
    /// <para>
    /// Two bulk queries joined in memory, rather than the obvious pipeline. The
    /// rule-to-filter relationship is one-to-one and keyed by <c>InstanceID</c>,
    /// so the join is exact — but asking for each rule's filter individually is
    /// a separate CIM round trip per rule, and on a stock Windows Server that is
    /// several hundred of them. Measured at 56 seconds on a real box, against
    /// about two for this.
    /// </para>
    /// <para>
    /// <c>ActiveStore</c> is the effective policy, which is the only one that
    /// answers the question actually being asked. The default store omits rules
    /// applied by group policy, so a domain-joined machine whose port was opened
    /// centrally would be reported as closed.
    /// </para>
    /// </remarks>
    public async Task<FirewallRules> ReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await _powerShell.RunAsync(
                """
                $ErrorActionPreference = 'Stop'
                try {
                  # Owner comes from the rule itself and Package from the
                  # application filter. Both NARROW a rule to a principal that is
                  # not this service's child process, and neither was collected
                  # until `T229` -- so a rule reading Program='Any' was read as
                  # "every program" when it meant "every program IN THAT PACKAGE".
                  $allowed = @{}
                  Get-NetFirewallRule -PolicyStore ActiveStore -Direction Inbound -Action Allow -Enabled True |
                    ForEach-Object { $allowed[$_.InstanceID] = [pscustomobject]@{
                      Name = $_.DisplayName; Profile = [string]$_.Profile; Owner = [string]$_.Owner } }

                  $programs = @{}
                  $packages = @{}
                  Get-NetFirewallApplicationFilter -PolicyStore ActiveStore |
                    Where-Object { $allowed.ContainsKey($_.InstanceID) } |
                    ForEach-Object {
                      $programs[$_.InstanceID] = [string]$_.Program
                      $packages[$_.InstanceID] = [string]$_.Package
                    }

                  $remotes = @{}
                  Get-NetFirewallAddressFilter -PolicyStore ActiveStore |
                    Where-Object { $allowed.ContainsKey($_.InstanceID) } |
                    ForEach-Object { $remotes[$_.InstanceID] = @($_.RemoteAddress) }

                  $rules = @(
                    Get-NetFirewallPortFilter -PolicyStore ActiveStore |
                      Where-Object {
                        # T220. 'Any' is kept: a rule that opens the port on every
                        # protocol opens it on the one being asked about, and
                        # dropping it here made such a rule invisible however the
                        # C# side matched. 64 rules on one ordinary machine carry
                        # no protocol restriction.
                        ($_.Protocol -eq 'UDP' -or $_.Protocol -eq 'TCP' -or $_.Protocol -eq 'Any') `
                          -and $allowed.ContainsKey($_.InstanceID)
                      } |
                      ForEach-Object {
                        [pscustomobject]@{
                          Name          = $allowed[$_.InstanceID].Name
                          Protocol      = $_.Protocol
                          Ports         = @($_.LocalPort)
                          Profile       = $allowed[$_.InstanceID].Profile
                          Program       = $programs[$_.InstanceID]
                          Package       = $packages[$_.InstanceID]
                          Owner         = $allowed[$_.InstanceID].Owner
                          RemoteAddress = @($remotes[$_.InstanceID])
                        }
                      })

                  [pscustomobject]@{
                    ActiveProfiles = @(Get-NetConnectionProfile | ForEach-Object { "$($_.NetworkCategory)" })
                    Rules          = $rules
                  } | ConvertTo-Json -Depth 5 -Compress
                } catch { Write-Error $_.Exception.Message }
                """,
                cancellationToken)
            .ConfigureAwait(false);

        return new FirewallRules(result.Succeeded, result.Output, result.Reason);
    }

    /// <summary>
    /// What the row is called, which the page needs before there is an answer to
    /// put in it.
    /// </summary>
    public static string CheckTitle(string instanceName, int port, PortProtocol protocol) =>
        CheckTitle(instanceName, new PortSpan(port, port), protocol);

    /// <summary>The row title for a run of ports (<c>T225</c>).</summary>
    public static string CheckTitle(string instanceName, PortSpan ports, PortProtocol protocol) =>
        $"Firewall — {instanceName}, {Name(protocol)} {ports}";

    /// <summary>Whether an enabled inbound allow rule exists for a port on a protocol.</summary>
    /// <remarks>
    /// <para>Any rule counts, not only one of ours. Somebody who opened the port by
    /// hand years ago has a working server, and telling them it is broken —
    /// then offering to add a second, redundant rule — would be wrong twice.</para>
    ///
    /// <para><strong>The protocol is not decoration</strong> (<c>D70</c>). This
    /// read UDP rules only and offered a UDP rule to fix them, so for a
    /// <see cref="ServerTransport.NetherNet"/> server — whose <c>server-port</c> is
    /// a TCP listener — it reported the port closed when a TCP rule already allowed
    /// it, and its <em>fix</em> added a rule that opens nothing the server uses.
    /// Wrong on the reading and wrong on the remedy, which is worse: an
    /// administrator who presses the button has a rule, a green row, and a server
    /// nobody outside can still reach.</para>
    ///
    /// <para><strong>Such a server needs both, not the other one</strong>
    /// (<c>D71</c>). TCP carries the handshake and play lands on the same number's
    /// UDP side unless <c>server-udp-ports</c> moves it, so
    /// <see cref="ServerPorts.For"/> returns two sockets and this check runs once
    /// per socket. Swapping UDP for TCP would have been the same defect with its
    /// sign flipped — the handshake open and nothing to play over.</para>
    /// </remarks>
    /// <param name="serverExecutable">
    /// The full path of this instance's <c>bedrock_server.exe</c>, so a rule scoped
    /// to some other program is not counted (<c>T207</c>). Null means it is not
    /// known, and then only rules that apply to every program count.
    /// </param>
    public static SystemCheck Check(
        FirewallRules rules, string instanceName, int port, PortProtocol protocol, string? serverExecutable) =>
        Check(rules, instanceName, new PortSpan(port, port), protocol, serverExecutable);

    /// <inheritdoc cref="Check(FirewallRules, string, int, PortProtocol, string?)" />
    /// <remarks>
    /// A run of ports is one row and one rule (<c>T225</c>): a play window as wide
    /// as <c>max-players</c> would otherwise be ten rows to read and ten buttons to
    /// press, for one thing an administrator has to do once.
    /// </remarks>
    /// <param name="stale">
    /// The server's own rules that open a port it no longer uses
    /// (<see cref="FirewallRuleSync.Stale"/>). When there are any, the fix moves them
    /// rather than adding a rule beside them (<c>T411</c>).
    /// </param>
    public static SystemCheck Check(
        FirewallRules rules,
        string instanceName,
        PortSpan ports,
        PortProtocol protocol,
        string? serverExecutable,
        string? unavailable = null,
        IReadOnlyList<string>? stale = null)
    {
        var title = CheckTitle(instanceName, ports, protocol);

        if (!rules.Read)
        {
            return new SystemCheck(
                title, SystemCheckState.Unknown,
                Unread($"Could not read the firewall rules. {rules.Error}", unavailable));
        }

        var match = FindRule(rules.Json, ports, protocol, serverExecutable);

        return match is not null
            ? new SystemCheck(title, SystemCheckState.Good, $"Allowed by \"{match}\".")
            : new SystemCheck(
                title,
                SystemCheckState.Missing,
                $"Nothing lets {Name(protocol)} {ports} in. Players on this network can connect; players "
                + "outside it cannot, and the server reports no error either way.",
                CanFix: true,
                FixDescription: stale is { Count: > 0 }
                    ? $"Moves {string.Join(", ", stale.Select(r => $"\"{r}\""))} to the ports the server "
                      + "uses now, rather than adding a rule beside it."
                    : $"Adds one inbound rule named \"{RuleName(instanceName, ports, protocol)}\" allowing "
                      + $"{Name(protocol)} {ports} on all profiles. Nothing else is opened, and deleting the "
                      + "rule in Windows Defender Firewall undoes it.");
    }

    /// <summary>
    /// A failed read's sentence, with why the read needs the broker when this install
    /// has none (<c>T394</c> clause 2, <see cref="PrivilegedWork.FixesUnavailable"/>).
    /// </summary>
    internal static string Unread(string failed, string? unavailable) =>
        unavailable is null ? failed : $"{failed.TrimEnd()} {unavailable}";

    /// <summary>
    /// The name of the first rule that actually lets <paramref name="port"/> in
    /// for this server, or null.
    /// </summary>
    /// <remarks>
    /// <para>Public and static so it can be tested against captured output. This is
    /// where the interesting failures are: a single rule serialises as an object
    /// rather than an array, ports arrive as strings, and a range like
    /// <c>19130-19140</c> covers 19132 without ever mentioning it.</para>
    ///
    /// <para><strong>A rule matching a port is not a rule that lets anything
    /// in</strong> (<c>T207</c>). The query used to select name, protocol and ports
    /// and nothing else, so <em>program scope, profile and remote address could not
    /// be considered by anything downstream</em> — and a rule whose
    /// <c>LocalPort</c> is <c>Any</c> then matched unconditionally. Measured on one
    /// ordinary Windows machine: <strong>45 rules matched a single Bedrock UDP port
    /// unconditionally</strong>, out of 300 enabled inbound allow rules, 54 of which
    /// carry <c>LocalPort=Any</c> on UDP and 61 on TCP. So <em>every</em> firewall
    /// row read
    /// "Allowed by …", and the row's <strong>Apply fix</strong> control became unreachable
    /// because nothing ever reported as blocked. The harm is not hypothetical: a
    /// client could not join a server this panel called open, and no rule covered
    /// that instance's executable at all.</para>
    ///
    /// <para>So four things now have to be true, and each is the answer to a way a
    /// rule can exist and do nothing for this server: the <strong>protocol</strong>
    /// matches, the <strong>ports</strong> cover, the rule applies to <strong>this
    /// program</strong>, and it applies on an <strong>active profile</strong> to a
    /// <strong>remote address</strong> that includes the outside world. That last
    /// one is the check's own question — a rule scoped to <c>LocalSubnet</c>
    /// answers "players on this network can connect", which is the half that was
    /// never in doubt.</para>
    ///
    /// <para><strong>Interface type is deliberately not consulted</strong>, and
    /// saying so is cheaper than somebody discovering it: a rule bound to one
    /// interface type is rare enough that adding a fifth bulk query for it was not
    /// worth the round trip. What it leaves is a <strong>false ALLOWED</strong> — a
    /// row saying allowed where the rule does not reach every adapter — which is
    /// the dangerous direction, and it is accepted here only because the shape is
    /// rare. If that ever bites, the query is the place, not this method.</para>
    ///
    /// <para><strong>"Over-report" is not used in this file, and that is deliberate.</strong>
    /// It meant this paragraph's case — a false <em>allowed</em>, the dangerous
    /// direction — and also the opposite one 140 lines below, where erring toward
    /// <em>blocked</em> is the safe direction. One word, two opposite meanings,
    /// both load-bearing, inside the file written to close <c>T207</c>; and by the
    /// time it was noticed the wrong one had been copied out and used to excuse
    /// this very omission. Say <strong>false allowed</strong> or <strong>false
    /// blocked</strong>; neither can be read the other way.</para>
    /// </remarks>
    public static string? FindRule(string json, int port, PortProtocol protocol, string? serverExecutable) =>
        FindRule(json, new PortSpan(port, port), protocol, serverExecutable);

    /// <summary>
    /// The rule that opens <em>every</em> port in <paramref name="ports"/>, or null
    /// (<c>T225</c>).
    /// </summary>
    public static string? FindRule(
        string json, PortSpan ports, PortProtocol protocol, string? serverExecutable)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            var root = document.RootElement;
            var active = ActiveProfilesOf(root);

            // The payload is an object carrying the machine's active profiles and
            // its rules. A bare array or object is the older shape, which carried
            // no scope at all — accepted so captured output stays readable, and it
            // simply has no Program or Profile to consider.
            var container = root.ValueKind == JsonValueKind.Object
                            && root.TryGetProperty("Rules", out var listed)
                ? listed
                : root;

            var rules = container.ValueKind == JsonValueKind.Array
                ? container.EnumerateArray()
                : SingleElement(container);

            foreach (var rule in rules)
            {
                if (rule.ValueKind != JsonValueKind.Object
                    || !rule.TryGetProperty("Ports", out var entryPorts))
                {
                    continue;
                }

                // A rule with no Protocol at all is not assumed to be the one
                // being asked about. Output from before this field existed, or
                // from anything else, would otherwise let a UDP rule answer a
                // question about TCP — the reading `D70` exists to stop.
                //
                // A rule that SAYS 'Any' is a different thing entirely and answers
                // for both (`T220`). The paragraph above is about an ABSENT field,
                // where nothing has been claimed; 'Any' is the rule claiming more
                // than was asked, and refusing it reported a genuinely open port as
                // blocked — then offered to add a second, redundant rule, which the
                // remarks on this method call being wrong twice.
                if (!rule.TryGetProperty("Protocol", out var spoken)
                    || spoken.ValueKind != JsonValueKind.String
                    || !SpeaksFor(spoken.GetString(), protocol))
                {
                    continue;
                }

                if (!Covers(entryPorts, ports)
                    || !AppliesToEveryPrincipal(rule)
                    || !AppliesToProgram(rule, serverExecutable)
                    || !AppliesOnAnActiveProfile(rule, active)
                    || !ReachesTheOutsideWorld(rule))
                {
                    continue;
                }

                return rule.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()!
                    : "an existing rule";
            }
        }
        catch (JsonException)
        {
            // Output that is not JSON means the check did not really succeed;
            // "no rule found" is the safe reading, and the worst it costs is an
            // offer to add a rule that is already there.
        }

        return null;
    }

    /// <summary>The network categories currently in use, empty when unknown.</summary>
    private static IReadOnlyList<string> ActiveProfilesOf(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("ActiveProfiles", out var profiles)
            || profiles.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return profiles
            .EnumerateArray()
            .Where(p => p.ValueKind == JsonValueKind.String)
            .Select(p => Firewall(p.GetString()!))
            .Where(p => p.Length > 0)
            .ToList();
    }

    /// <summary>
    /// A connection profile's name in the firewall's vocabulary.
    /// </summary>
    /// <remarks>
    /// <c>Get-NetConnectionProfile</c> says <c>DomainAuthenticated</c> where a rule
    /// says <c>Domain</c>. One spelling in, or a domain machine's rules never match
    /// its own network — the same class as reading a rule under one protocol
    /// spelling and creating it under another.
    /// </remarks>
    private static string Firewall(string networkCategory) =>
        networkCategory.Trim() switch
        {
            "DomainAuthenticated" => "Domain",
            var other => other,
        };

    /// <summary>
    /// Whether a rule applies to programs generally, rather than being narrowed to
    /// one AppContainer package or to one user's own traffic.
    /// </summary>
    /// <remarks>
    /// <para><strong>This is <c>T229</c>, and it is the false clear <c>T207</c> was
    /// written to remove, re-created by <c>T220</c>'s fix.</strong> Keeping
    /// <c>Protocol='Any'</c> was right; what came with it was every rule carrying a
    /// package SID or an owner SID, whose <c>Program</c> reads <c>Any</c> and whose
    /// <c>LocalPort</c> reads <c>Any</c>. On one ordinary machine that is 45 rules
    /// with an owner and 13 with a package, and every instance's row went green
    /// naming a Windows WebView rule while the instance had no rule of its own.
    /// Measured by running this matcher against that machine's real rule data under
    /// both filter shapes: 0 rules accepted before, 13 after, same data.</para>
    ///
    /// <para><strong>Over-gating is the cheap direction here and that is why both
    /// are refused.</strong> A rule wrongly refused costs one redundant rule, which
    /// the row's own fix offers to add. A rule wrongly accepted costs a green row on
    /// a server nobody outside can reach, with the fix control not rendering at all
    /// because nothing reports as blocked — that is the evening <c>T207</c> records.
    /// An owner naming the service's own account is the arguable case, since each
    /// server runs under a restricted copy of it; it is refused anyway, because a
    /// rule that names a principal has not said it applies to this one.</para>
    ///
    /// <para><strong><c>LocalUser</c> is not consulted, and the reason first written
    /// here was false</strong> (<c>T237</c>). It said the field reads <c>Any</c> on
    /// every rule and would discriminate nothing; measured across 300 inbound
    /// enabled allow rules, <strong>2 carry a real SDDL principal</strong>. The
    /// conclusion survives only because both of those are independently narrowed by
    /// a dimension that IS read — so the check is right today by luck, and nothing
    /// fails the day a rule is scoped by user alone.
    /// The honest reason to leave it out is that an SDDL string needs resolving
    /// against the account the server runs as before it can be compared, which is a
    /// second question; until it is read, a user-only rule is accepted and
    /// over-reports as allowed.</para>
    ///
    /// <para><c>HostCheckTests</c> pins a package-scoped and an owner-scoped rule, which
    /// is the arm that was missing — its sibling already predicted this failure in
    /// words and then asserted only the program dimension.</para>
    /// </remarks>
    private static bool AppliesToEveryPrincipal(JsonElement rule) =>
        !NarrowedBy(rule, "Package") && !NarrowedBy(rule, "Owner");

    /// <summary>
    /// Whether <paramref name="field"/> names a principal. Absent, empty and
    /// <c>Any</c> all mean it does not.
    /// </summary>
    private static bool NarrowedBy(JsonElement rule, string field)
    {
        if (!rule.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var named = value.GetString();

        return !string.IsNullOrWhiteSpace(named)
               && !named.Trim().Equals("Any", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a rule applies to this server's executable.</summary>
    /// <remarks>
    /// <para>An absent, empty or <c>Any</c> program means the rule applies to every
    /// program, which is what the wrapper's own rule looks like and what most
    /// hand-written port rules look like. A rule naming a <em>different</em>
    /// program is the case that was being counted and must not be: those are what
    /// every row on a stock machine was matching.</para>
    ///
    /// <para><strong><c>Any</c> means every program OF THE PRINCIPAL THE RULE IS
    /// SCOPED TO</strong>, which is the sentence this comment was missing and
    /// <c>T229</c> is what that cost. <see cref="AppliesToEveryPrincipal"/> settles
    /// that half and runs before this one, so the two questions are asked
    /// separately rather than one of them standing in for both.</para>
    /// </remarks>
    private static bool AppliesToProgram(JsonElement rule, string? serverExecutable)
    {
        if (!rule.TryGetProperty("Program", out var program)
            || program.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        var named = program.GetString();

        if (string.IsNullOrWhiteSpace(named)
            || named.Equals("Any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Not knowing our own executable is not a reason to accept a rule written
        // for somebody else's.
        return serverExecutable is not null
               && string.Equals(
                   Path.TrimEndingDirectorySeparator(named.Trim()),
                   Path.TrimEndingDirectorySeparator(serverExecutable.Trim()),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a rule is in force on a network this machine is on.</summary>
    /// <remarks>
    /// <para>With no active profiles read — older captured output, or a machine with
    /// no connection profile at all — this does not filter. Guessing would turn
    /// every row blocked.</para>
    ///
    /// <para><strong>THE TWO COSTS WERE THE WRONG WAY ROUND HERE until <c>T229</c>,
    /// and the predicate above depends on getting them right.</strong> This said a
    /// false allowed costs a redundant rule and a false blocked costs somebody their
    /// evening. It is the other way: a false <em>blocked</em> offers a redundant
    /// rule, which the row's own fix adds in one press; a false <em>allowed</em>
    /// shows a green row on a server nobody outside can reach and renders no fix
    /// control at all, which is the evening <c>T207</c> records losing.</para>
    ///
    /// <para>Not filtering when the profiles cannot be read therefore takes the
    /// expensive direction, and it is kept only because the alternative turns every
    /// row on such a machine blocked at once. Named as false allowed and false
    /// blocked rather than as an over-report, which is the term that meant the
    /// reverse of this 140 lines above.</para>
    /// </remarks>
    private static bool AppliesOnAnActiveProfile(JsonElement rule, IReadOnlyList<string> active)
    {
        if (active.Count == 0
            || !rule.TryGetProperty("Profile", out var profile)
            || profile.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        var declared = profile.GetString();

        if (string.IsNullOrWhiteSpace(declared)
            || declared.Contains("Any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return declared
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(d => active.Contains(d, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a rule admits connections from outside this network, which is the
    /// question this whole check exists to answer.
    /// </summary>
    /// <remarks>
    /// A rule scoped to <c>LocalSubnet</c>, or to a named range, answers "players
    /// on this network can connect" — the half that was never in doubt. Absent or
    /// <c>Any</c> is the unrestricted case.
    /// </remarks>
    private static bool ReachesTheOutsideWorld(JsonElement rule)
    {
        if (!rule.TryGetProperty("RemoteAddress", out var remote))
        {
            return true;
        }

        var values = remote.ValueKind == JsonValueKind.Array
            ? remote.EnumerateArray()
            : SingleElement(remote);

        var any = false;

        foreach (var value in values)
        {
            any = true;

            if (value.ValueKind == JsonValueKind.String
                && value.GetString() is { } text
                && text.Trim().Equals("Any", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // No entries at all is the unrestricted case; entries that are all
        // something other than Any is a scoped rule.
        return !any;
    }

    private static IEnumerable<JsonElement> SingleElement(JsonElement element)
    {
        yield return element;
    }

    private static bool Covers(JsonElement ports, PortSpan wanted)
    {
        var values = ports.ValueKind == JsonValueKind.Array
            ? ports.EnumerateArray()
            : SingleElement(ports);

        foreach (var value in values)
        {
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };

            if (text is not null && CoversSpan(text, wanted))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether one <c>LocalPort</c> entry covers a port. Entries are a number,
    /// a <c>from-to</c> range, or the word <c>Any</c>.
    /// </summary>
    /// <summary>
    /// Whether a rule's <c>Protocol</c> field answers for <paramref name="asked"/>.
    /// </summary>
    /// <remarks>
    /// Windows spells "this rule is not restricted by protocol" as <c>Any</c>, and
    /// such a rule opens the port on TCP and UDP alike. Matching the name alone
    /// dropped every one of them (<c>T220</c>) — a false <em>blocked</em>, which is
    /// the safe direction and was still a trade nothing in the file had made.
    /// </remarks>
    public static bool SpeaksFor(string? spoken, PortProtocol asked) =>
        string.Equals(spoken, Name(asked), StringComparison.OrdinalIgnoreCase)
        || string.Equals(spoken, "Any", StringComparison.OrdinalIgnoreCase);

    public static bool CoversPort(string entry, int port) =>
        CoversSpan(entry, new PortSpan(port, port));

    /// <summary>
    /// Whether one <c>LocalPort</c> entry covers <em>every</em> port in
    /// <paramref name="wanted"/>.
    /// </summary>
    /// <remarks>
    /// <para>One entry has to cover the whole run. A window covered by two rules
    /// between them reads as <strong>blocked</strong> — a false blocked, which is
    /// the safe direction and is stated rather than discovered: the remedy the
    /// panel then offers is one rule that does cover it, and an administrator ends
    /// up with a redundant rule rather than an unreachable server.</para>
    /// </remarks>
    public static bool CoversSpan(string entry, PortSpan wanted)
    {
        var text = entry.Trim();

        if (text.Equals("Any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dash = text.IndexOf('-');
        if (dash > 0)
        {
            return int.TryParse(text[..dash], out var from)
                   && int.TryParse(text[(dash + 1)..], out var to)
                   && wanted.First >= from
                   && wanted.Last <= to;
        }

        return int.TryParse(text, out var single)
               && single == wanted.First
               && single == wanted.Last;
    }

    /// <summary>Adds the rule described by <see cref="CheckAsync"/>.</summary>
    public Task<SystemCheckFix> AllowAsync(
        string instanceName,
        int port,
        PortProtocol protocol,
        CancellationToken cancellationToken = default) =>
        AllowAsync(instanceName, new PortSpan(port, port), protocol, cancellationToken);

    /// <summary>
    /// Adds one rule covering a whole run of ports (<c>T225</c>).
    /// </summary>
    /// <remarks>
    /// <c>New-NetFirewallRule</c> takes a range on <c>-LocalPort</c>, so a pinned
    /// play window is one rule. It is composed through <see cref="PortSpan"/>'s own
    /// spelling, which is the same one <c>server.properties</c> uses and the same
    /// one <see cref="CoversSpan"/> reads back — a rule created under one spelling
    /// and looked for under another is a rule that never matches its own check.
    /// </remarks>
    public Task<SystemCheckFix> AllowAsync(
        string instanceId,
        string instanceName,
        PortSpan ports,
        PortProtocol protocol,
        CancellationToken cancellationToken = default) =>
        AllowAsync(instanceName, ports, protocol, cancellationToken, instanceId);

    /// <summary>Adds the rule for <paramref name="ports"/>, labelled with <paramref name="instanceName"/>.</summary>
    /// <param name="instanceId">
    /// The server's id, written into the rule's description so a rename can find it
    /// (<see cref="FirewallRuleSync.Tag"/>); null leaves the description empty.
    /// </param>
    public async Task<SystemCheckFix> AllowAsync(
        string instanceName,
        PortSpan ports,
        PortProtocol protocol,
        CancellationToken cancellationToken = default,
        string? instanceId = null)
    {
        var name = RuleName(instanceName, ports, protocol);

        var result = await _powerShell.RunAsync(
                $$"""
                $ErrorActionPreference = 'Stop'
                try {
                  {{NewRule(name, ports, protocol, instanceId)}}
                  'ok'
                } catch { Write-Error $_.Exception.Message }
                """,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            _log.LogWarning(
                "Added firewall rule {Rule} for {Protocol} {Ports}", name, Name(protocol), ports);
            return new SystemCheckFix(true, $"Added \"{name}\".");
        }

        return new SystemCheckFix(false, result.Reason is { Length: > 0 } error
            ? error
            : "The rule could not be added.");
    }

    /// <summary>Removes the rules <see cref="AllowAsync(string, PortSpan, PortProtocol, CancellationToken)"/> made for a server.</summary>
    /// <remarks>
    /// Every store's inbound rules, not only the active ones: a disabled rule is one
    /// somebody turns back on. A rule carrying the server's tag, or named for it
    /// (<see cref="FirewallRuleSync"/>); one renamed by hand and untagged no longer
    /// matches and stays, which is the operator's call to have made.
    /// </remarks>
    public async Task<SystemCheckFix> RemoveServerRulesAsync(
        string instanceId,
        string instanceName,
        CancellationToken cancellationToken = default)
    {
        var result = await _powerShell.RunAsync(
                $$"""
                $ErrorActionPreference = 'Stop'
                try {
                  $rules = @(Get-NetFirewallRule -Direction Inbound |
                    Where-Object { {{Belongs(instanceId, instanceName)}} })
                  $rules | ForEach-Object { $_.DisplayName }
                  if ($rules.Count -gt 0) { $rules | Remove-NetFirewallRule }
                } catch { Write-Error $_.Exception.Message }
                """,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return new SystemCheckFix(false, result.Reason is { Length: > 0 } error
                ? error
                : "Its firewall rules could not be removed.");
        }

        var removed = result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (removed.Length == 0)
        {
            return new SystemCheckFix(true, "No firewall rule was made for it.");
        }

        _log.LogWarning("Removed firewall rules {Rules} of {Instance}", removed, instanceName);
        return new SystemCheckFix(true, $"Removed {string.Join(", ", removed.Select(r => $"\"{r}\""))}.");
    }

    /// <summary>
    /// Labels every rule of the server with <paramref name="instanceName"/> and its tag,
    /// keeping each rule's protocol and ports (<c>T411</c>).
    /// </summary>
    /// <remarks>
    /// A rename calls it twice: under the old name, which tags the rules made before
    /// the tag existed, then under the new one, which finds them by the tag.
    /// </remarks>
    public async Task<SystemCheckFix> RenameServerRulesAsync(
        string instanceId,
        string instanceName,
        CancellationToken cancellationToken = default)
    {
        var (rules, unlisted) = await ListServerRulesAsync(instanceId, instanceName, cancellationToken).ConfigureAwait(false);

        if (rules is null)
        {
            return new SystemCheckFix(false, unlisted!);
        }

        var renames = rules
            .Select(r => (Rule: r, NewName: FirewallRuleSync.Renamed(r.Name, instanceName)))
            .Where(r => r.NewName is not null)
            .ToList();

        if (renames.Count == 0)
        {
            return new SystemCheckFix(true, "No firewall rule was made for it.");
        }

        var tag = Quote(FirewallRuleSync.Tag(instanceId));
        var script = string.Join(Environment.NewLine, renames.Select(r =>
            $"  Set-NetFirewallRule -Name '{Quote(r.Rule.Id)}' -NewDisplayName '{Quote(r.NewName!)}' -Description '{tag}'"));

        var fix = await RunStepsAsync(script, "Its firewall rules could not be renamed.", cancellationToken).ConfigureAwait(false);

        if (!fix.Succeeded)
        {
            return fix;
        }

        var changed = renames.Where(r => r.Rule.Name != r.NewName).ToList();

        if (changed.Count == 0)
        {
            return new SystemCheckFix(true, "Its firewall rules already carry its name.");
        }

        _log.LogWarning("Renamed firewall rules of {Instance}: {Rules}", instanceName, changed.Select(r => r.NewName));
        return new SystemCheckFix(true,
            $"Renamed {string.Join(", ", changed.Select(r => $"\"{r.Rule.Name}\" to \"{r.NewName}\""))}.");
    }

    /// <summary>
    /// Moves the server's rules to <paramref name="sockets"/>, the ports it uses now,
    /// rather than adding new ones beside them (<c>T411</c>); see <see cref="FirewallRuleSync.Plan"/>.
    /// </summary>
    public async Task<SystemCheckFix> UpdateServerRulesAsync(
        string instanceId,
        string instanceName,
        IReadOnlyList<ReachableSockets> sockets,
        CancellationToken cancellationToken = default)
    {
        var (rules, unlisted) = await ListServerRulesAsync(instanceId, instanceName, cancellationToken).ConfigureAwait(false);

        if (rules is null)
        {
            return new SystemCheckFix(false, unlisted!);
        }

        var steps = FirewallRuleSync.Plan(rules, instanceName, sockets);
        var tag = Quote(FirewallRuleSync.Tag(instanceId));
        var lines = new List<string>();

        foreach (var step in steps)
        {
            switch (step.Change)
            {
                case RuleChange.Keep:
                case RuleChange.Rename:
                    lines.Add($"  Set-NetFirewallRule -Name '{Quote(step.Rule!.Id)}' -NewDisplayName '{Quote(step.NewName!)}' -Description '{tag}'");
                    break;
                case RuleChange.Repoint:
                    lines.Add($"  Get-NetFirewallRule -Name '{Quote(step.Rule!.Id)}' | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -LocalPort {step.Socket!.Value.Ports}");
                    lines.Add($"  Set-NetFirewallRule -Name '{Quote(step.Rule.Id)}' -NewDisplayName '{Quote(step.NewName!)}' -Description '{tag}'");
                    break;
                case RuleChange.Add:
                    lines.Add("  " + NewRule(step.NewName!, step.Socket!.Value.Ports, step.Socket.Value.Protocol, instanceId));
                    break;
                case RuleChange.Remove:
                    lines.Add($"  Remove-NetFirewallRule -Name '{Quote(step.Rule!.Id)}'");
                    break;
                default:
                    throw new InvalidOperationException($"{step.Change} is not a rule change.");
            }
        }

        var described = FirewallRuleSync.Describe(steps);

        if (lines.Count == 0)
        {
            return new SystemCheckFix(true, described);
        }

        var fix = await RunStepsAsync(
                string.Join(Environment.NewLine, lines), "Its firewall rules could not be updated.", cancellationToken)
            .ConfigureAwait(false);

        if (!fix.Succeeded)
        {
            return fix;
        }

        _log.LogWarning("Updated firewall rules of {Instance}: {Steps}", instanceName, described);
        return new SystemCheckFix(true, described);
    }

    /// <summary>Every inbound rule of the server, in every store, with its port filter.</summary>
    private async Task<(IReadOnlyList<ServerRule>? Rules, string? Error)> ListServerRulesAsync(
        string instanceId, string instanceName, CancellationToken cancellationToken)
    {
        var result = await _powerShell.RunAsync(
                $$"""
                $ErrorActionPreference = 'Stop'
                try {
                  $rules = @(Get-NetFirewallRule -Direction Inbound |
                    Where-Object { {{Belongs(instanceId, instanceName)}} } |
                    ForEach-Object {
                      $filter = $_ | Get-NetFirewallPortFilter
                      [pscustomobject]@{
                        Id       = [string]$_.Name
                        Name     = [string]$_.DisplayName
                        Protocol = [string]$filter.Protocol
                        Ports    = @($filter.LocalPort | ForEach-Object { [string]$_ })
                      }
                    })
                  ConvertTo-Json -InputObject $rules -Depth 3 -Compress
                } catch { Write-Error $_.Exception.Message }
                """,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return (null, result.Reason is { Length: > 0 } error ? error : "Its firewall rules could not be read.");
        }

        try
        {
            return (FirewallRuleSync.ParseListing(result.Output), null);
        }
        catch (JsonException ex)
        {
            return (null, $"The firewall's list of its rules could not be read: {ex.Message}");
        }
    }

    private async Task<SystemCheckFix> RunStepsAsync(string steps, string failed, CancellationToken cancellationToken)
    {
        var result = await _powerShell.RunAsync(
                $$"""
                $ErrorActionPreference = 'Stop'
                try {
                {{steps}}
                  'ok'
                } catch { Write-Error $_.Exception.Message }
                """,
                cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded
            ? new SystemCheckFix(true, string.Empty)
            : new SystemCheckFix(false, result.Reason is { Length: > 0 } error ? error : failed);
    }

    /// <summary>The <c>Where-Object</c> test for a rule of this server: its tag, or its name.</summary>
    private static string Belongs(string instanceId, string instanceName) =>
        $"$_.Description -eq '{Quote(FirewallRuleSync.Tag(instanceId))}' -or "
        + $"[regex]::IsMatch($_.DisplayName, '{Quote(RulePattern(instanceName))}')";

    private static string NewRule(string name, PortSpan ports, PortProtocol protocol, string? instanceId) =>
        $"New-NetFirewallRule -DisplayName '{Quote(name)}' -Direction Inbound -Action Allow "
        + $"-Protocol {Name(protocol)} -LocalPort {ports} -Profile Any"
        + (instanceId is null ? string.Empty : $" -Description '{Quote(FirewallRuleSync.Tag(instanceId))}'")
        + " | Out-Null";

    /// <summary>
    /// Single-quoted and doubled, so a name containing an apostrophe cannot end the
    /// string and turn the rest into something else to run.
    /// </summary>
    private static string Quote(string text) => text.Replace("'", "''");
}
