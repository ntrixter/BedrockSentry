using System.Net;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Services.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BedrockSentry.Tests;

/// <summary>A runner that returns whatever the test says PowerShell said.</summary>
internal sealed class FakePowerShell : IPowerShellRunner
{
    private readonly Queue<ScriptResult> _answers = new();

    public List<string> Scripts { get; } = [];

    public FakePowerShell Answers(params ScriptResult[] results)
    {
        foreach (var result in results)
        {
            _answers.Enqueue(result);
        }

        return this;
    }

    public FakePowerShell Prints(string output) => Answers(new ScriptResult(true, output, string.Empty));

    public FakePowerShell Fails(string error) => Answers(ScriptResult.Failed(error));

    public Task<ScriptResult> RunAsync(string script, CancellationToken cancellationToken = default)
    {
        Scripts.Add(script);

        return Task.FromResult(_answers.Count > 0
            ? _answers.Dequeue()
            : new ScriptResult(true, string.Empty, string.Empty));
    }
}

/// <summary>
/// Reading the firewall's answer, which is the part that can be wrong.
///
/// Starting a process is the part that cannot run on a Linux build machine, so
/// it sits behind <see cref="IPowerShellRunner"/> and everything below runs
/// against output shaped the way the real cmdlets shape it.
/// </summary>
public class FirewallInspectorTests
{
    [Theory]
    [InlineData("19132", 19132, true)]
    [InlineData("19132", 19133, false)]
    [InlineData("19130-19140", 19132, true)]
    [InlineData("19130-19140", 19141, false)]
    [InlineData("Any", 19132, true)]
    [InlineData("any", 19132, true)]
    [InlineData("not-a-port", 19132, false)]
    public void Reads_a_single_port_entry(string entry, int port, bool expected) =>
        Assert.Equal(expected, FirewallInspector.CoversPort(entry, port));

    [Fact]
    public void Finds_a_rule_among_several()
    {
        const string json = """
            [
              {"Name":"Some other service","Ports":["8080"]},
              {"Name":"Minecraft Bedrock","Ports":["19132","19133"]}
            ]
            """;

        Assert.Equal("Minecraft Bedrock", FirewallInspector.FindRule(json, 19132));
    }

    // ConvertTo-Json emits a bare object when there is exactly one result and an
    // array when there is more than one. Reading only the array shape misses the
    // single-rule case — which is precisely the shape a machine ends up in after
    // this feature's own Apply fix has run once.
    [Fact]
    public void Finds_a_rule_when_there_is_only_one()
    {
        const string json = """{"Name":"BedrockSentry - Survival (UDP 19132)","Ports":"19132"}""";

        Assert.Equal("BedrockSentry - Survival (UDP 19132)", FirewallInspector.FindRule(json, 19132));
    }

    [Fact]
    public void A_rule_covering_a_range_counts()
    {
        const string json = """[{"Name":"Games","Ports":["19130-19140"]}]""";

        Assert.Equal("Games", FirewallInspector.FindRule(json, 19132));
    }

    [Fact]
    public void No_matching_rule_returns_null() =>
        Assert.Null(FirewallInspector.FindRule("""[{"Name":"Web","Ports":["443"]}]""", 19132));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    public void Unreadable_output_reads_as_no_rule(string json) =>
        Assert.Null(FirewallInspector.FindRule(json, 19132));

    [Fact]
    public async Task An_existing_rule_is_reported_as_good()
    {
        var powerShell = new FakePowerShell().Prints("""[{"Name":"Bedrock","Ports":["19132"]}]""");
        var inspector = new FirewallInspector(powerShell, NullLogger<FirewallInspector>.Instance);

        var check = FirewallInspector.Check(await inspector.ReadAsync(), "Survival", 19132);

        Assert.Equal(SystemCheckState.Good, check.State);
        Assert.False(check.CanFix);
    }

    [Fact]
    public async Task A_missing_rule_is_offered_a_fix_that_says_what_it_will_do()
    {
        var powerShell = new FakePowerShell().Prints("[]");
        var inspector = new FirewallInspector(powerShell, NullLogger<FirewallInspector>.Instance);

        var check = FirewallInspector.Check(await inspector.ReadAsync(), "Survival", 19132);

        Assert.Equal(SystemCheckState.Missing, check.State);
        Assert.True(check.CanFix);

        // The description exists so the change can be read before it is
        // authorised. A button labelled "fix" describes nothing.
        Assert.Contains("BedrockSentry - Survival (UDP 19132)", check.FixDescription);
        Assert.Contains("19132", check.FixDescription);
    }

    // "We could not ask" is not "there is no rule". Reporting the first as the
    // second would have people adding rules to solve a problem they do not have,
    // and would offer to change a machine nothing had successfully inspected.
    [Fact]
    public async Task A_check_that_could_not_run_is_unknown_and_offers_nothing()
    {
        var powerShell = new FakePowerShell().Fails("Access is denied.");
        var inspector = new FirewallInspector(powerShell, NullLogger<FirewallInspector>.Instance);

        var check = FirewallInspector.Check(await inspector.ReadAsync(), "Survival", 19132);

        Assert.Equal(SystemCheckState.Unknown, check.State);
        Assert.False(check.CanFix);
        Assert.Contains("Access is denied.", check.Detail);
    }

    // A display name is interpolated into a script. Single quotes are doubled so
    // it cannot end the string and turn the remainder into something to execute.
    [Fact]
    public async Task An_apostrophe_in_a_name_cannot_break_out_of_the_script()
    {
        var powerShell = new FakePowerShell().Prints("ok");
        var inspector = new FirewallInspector(powerShell, NullLogger<FirewallInspector>.Instance);

        const string hostile = "Steve's Server'; Remove-Item C:\\ -Recurse; '";

        await inspector.AllowAsync(hostile, 19132);

        var script = Assert.Single(powerShell.Scripts);

        // The whole rule name appears inside one pair of quotes with every
        // internal quote doubled, so nothing in it is ever read as script.
        var expected = FirewallInspector.RuleName(hostile, 19132).Replace("'", "''");

        Assert.Contains($"'{expected}'", script);
    }
}

/// <summary>Reading Defender's exclusion list.</summary>
public class DefenderInspectorTests
{
    private static string Path(params string[] parts) =>
        System.IO.Path.Combine([System.IO.Path.GetTempPath(), .. parts]);

    [Fact]
    public void An_exact_match_counts()
    {
        var directory = Path("instances", "survival");

        Assert.NotNull(DefenderInspector.FindExclusion($"[\"{Escape(directory)}\"]", directory));
    }

    // Somebody who excluded the whole instance root has already covered every
    // instance under it, and offering to add each one again would be noise.
    [Fact]
    public void A_parent_exclusion_counts()
    {
        var parent = Path("instances");
        var child = Path("instances", "survival");

        Assert.Equal(parent, DefenderInspector.FindExclusion($"[\"{Escape(parent)}\"]", child));
    }

    // The other direction is not containment: excluding one instance says
    // nothing about its sibling or its parent.
    [Fact]
    public void A_child_exclusion_does_not_count_for_its_parent()
    {
        var parent = Path("instances");
        var child = Path("instances", "survival");

        Assert.Null(DefenderInspector.FindExclusion($"[\"{Escape(child)}\"]", parent));
    }

    [Fact]
    public void A_sibling_that_merely_starts_the_same_does_not_count()
    {
        var excluded = Path("instances", "survival");
        var other = Path("instances", "survival-2");

        Assert.Null(DefenderInspector.FindExclusion($"[\"{Escape(excluded)}\"]", other));
    }

    [Fact]
    public void A_trailing_separator_does_not_change_the_answer()
    {
        var directory = Path("instances", "survival");
        var withSlash = directory + System.IO.Path.DirectorySeparatorChar;

        Assert.NotNull(DefenderInspector.FindExclusion($"[\"{Escape(withSlash)}\"]", directory));
    }

    // One exclusion serialises as a bare string rather than an array.
    [Fact]
    public void A_single_exclusion_is_read_too()
    {
        var directory = Path("instances", "survival");

        Assert.NotNull(DefenderInspector.FindExclusion($"\"{Escape(directory)}\"", directory));
    }

    [Fact]
    public void No_exclusions_reads_as_not_excluded() =>
        Assert.Null(DefenderInspector.FindExclusion("[]", Path("instances", "survival")));

    // Defender may be absent, replaced by another product, or managed by policy.
    // None of those is "your world is being scanned".
    [Fact]
    public async Task A_check_that_could_not_run_is_unknown_and_offers_nothing()
    {
        var powerShell = new FakePowerShell().Fails("The term 'Get-MpPreference' is not recognized.");
        var inspector = new DefenderInspector(powerShell, NullLogger<DefenderInspector>.Instance);

        var check = DefenderInspector.Check(await inspector.ReadAsync(), "Survival", Path("survival"));

        Assert.Equal(SystemCheckState.Unknown, check.State);
        Assert.False(check.CanFix);
    }

    // This is the one thing in the wrapper that makes a machine less protected,
    // so the offer has to say so before it is taken.
    [Fact]
    public async Task A_missing_exclusion_warns_about_what_it_costs()
    {
        var powerShell = new FakePowerShell().Prints("[]");
        var inspector = new DefenderInspector(powerShell, NullLogger<DefenderInspector>.Instance);

        var check = DefenderInspector.Check(await inspector.ReadAsync(), "Survival", Path("survival"));

        Assert.Equal(SystemCheckState.Missing, check.State);
        Assert.True(check.CanFix);
        Assert.NotNull(check.Warning);
        Assert.Contains("reduction in protection", check.Warning);
    }

    [Fact]
    public async Task An_apostrophe_in_a_path_cannot_break_out_of_the_script()
    {
        var powerShell = new FakePowerShell().Prints("ok");
        var inspector = new DefenderInspector(powerShell, NullLogger<DefenderInspector>.Instance);

        const string hostile = "C:\\Steve's'; Remove-Item C:\\ -Recurse; '";

        await inspector.ExcludeAsync(hostile);

        var script = Assert.Single(powerShell.Scripts);

        Assert.Contains($"'{hostile.Replace("'", "''")}'", script);
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\");
}

/// <summary>
/// D20. The cache is not an optimisation: this feeds a page that re-renders once
/// a second, and without it the wrapper would make a request per second, for the
/// life of the process, to somebody else's free service.
/// </summary>
public class PublicAddressServiceTests
{
    private sealed class CountingHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    private static (PublicAddressService Service, CountingHandler Handler, FakeTimeProvider Time)
        Build(string body, HostDetailsOptions? options = null)
    {
        var handler = new CountingHandler(body);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));

        var service = new PublicAddressService(
            new HttpClient(handler),
            new StaticOptionsMonitor<HostDetailsOptions>(options ?? new HostDetailsOptions()),
            time,
            NullLogger<PublicAddressService>.Instance);

        return (service, handler, time);
    }

    [Fact]
    public async Task Reads_the_address_and_then_stops_asking()
    {
        var (service, handler, time) = Build("203.0.113.77\n");

        Assert.Equal("203.0.113.77", (await service.GetAsync()).Address);

        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal("203.0.113.77", (await service.GetAsync()).Address);
        }

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Asks_again_once_the_cache_expires()
    {
        var (service, handler, time) = Build("203.0.113.77");

        await service.GetAsync();
        time.Advance(TimeSpan.FromMinutes(31));
        await service.GetAsync();

        Assert.Equal(2, handler.Calls);
    }

    // "Turned off" and "could not reach the internet" call for different
    // actions, so they are different notes rather than one silence.
    [Fact]
    public async Task Turned_off_says_so_and_contacts_nobody()
    {
        var (service, handler, _) = Build(
            "203.0.113.77", new HostDetailsOptions { DiscoverPublicAddress = false });

        var (address, note) = await service.GetAsync();

        Assert.Null(address);
        Assert.Contains("turned off", note);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task An_answer_that_is_not_an_address_is_rejected()
    {
        var (service, _, _) = Build("<html>rate limited</html>");

        var (address, note) = await service.GetAsync();

        Assert.Null(address);
        Assert.NotNull(note);
    }
}

/// <summary>The domain check — the whole of "why can't anyone connect".</summary>
public class DomainCheckTests
{
    [Fact]
    public void A_mismatch_names_both_addresses()
    {
        var check = new DomainCheck(
            "play.example.com", ["198.51.100.4"], "203.0.113.77",
            DomainCheckResult.Mismatch,
            "play.example.com points at 198.51.100.4, but this machine's public address is 203.0.113.77.");

        Assert.Equal(DomainCheckResult.Mismatch, check.Result);
        Assert.Contains("198.51.100.4", check.Detail);
        Assert.Contains("203.0.113.77", check.Detail);
    }
}

/// <summary>
/// What the host checks cost, which is the reason the status page once took
/// minutes to load.
/// </summary>
/// <remarks>
/// These assert on the shape of the work rather than on any answer. The bug they
/// exist to prevent produced entirely correct results — it just asked for them
/// several hundred times more than it needed to, and a test that only checks the
/// verdict cannot tell the difference.
/// </remarks>
public class HostProbeCostTests
{
    private static string Directory(string name) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bedrocksentry", name);

    // The rules belong to the machine, not to a server. Reading them per
    // instance meant a box with three servers ran the same whole-firewall dump
    // three times and discarded all but one port's worth of each.
    [Fact]
    public async Task The_firewall_is_read_once_however_many_servers_ask()
    {
        var powerShell = new FakePowerShell().Prints("""[{"Name":"Bedrock","Ports":["19132"]}]""");
        var inspector = new FirewallInspector(powerShell, NullLogger<FirewallInspector>.Instance);

        var rules = await inspector.ReadAsync();

        FirewallInspector.Check(rules, "Survival", 19132);
        FirewallInspector.Check(rules, "Creative", 19134);
        FirewallInspector.Check(rules, "Hardcore", 19136);

        Assert.Single(powerShell.Scripts);
    }

    [Fact]
    public async Task Defender_is_asked_once_however_many_servers_ask()
    {
        var powerShell = new FakePowerShell().Prints($"[\"{Directory("a").Replace("\\", "\\\\")}\"]");
        var inspector = new DefenderInspector(powerShell, NullLogger<DefenderInspector>.Instance);

        var exclusions = await inspector.ReadAsync();

        DefenderInspector.Check(exclusions, "Survival", Directory("a"));
        DefenderInspector.Check(exclusions, "Creative", Directory("b"));

        Assert.Single(powerShell.Scripts);
    }

    /// <summary>
    /// Two bulk queries joined in memory, never a lookup per rule.
    /// </summary>
    /// <remarks>
    /// The original asked each matching rule for its own port filter inside a
    /// ForEach-Object. That is one CIM round trip per rule, and on a stock
    /// Windows Server it measured 56 seconds against about two for this. Counting
    /// the cmdlets is a blunt guard, but it is the one that fails the moment
    /// somebody reintroduces the pipeline that caused it.
    /// </remarks>
    [Fact]
    public async Task The_firewall_read_never_asks_rule_by_rule()
    {
        var powerShell = new FakePowerShell().Prints("[]");
        var inspector = new FirewallInspector(powerShell, NullLogger<FirewallInspector>.Instance);

        await inspector.ReadAsync();

        var script = Assert.Single(powerShell.Scripts);

        Assert.Equal(1, Occurrences(script, "Get-NetFirewallRule"));
        Assert.Equal(1, Occurrences(script, "Get-NetFirewallPortFilter"));
    }

    // The default store leaves out rules applied by group policy, so a
    // domain-joined machine whose port was opened centrally would be reported as
    // closed — and offered a redundant rule to fix a problem it does not have.
    [Fact]
    public async Task The_firewall_read_asks_for_the_policy_actually_in_force()
    {
        var powerShell = new FakePowerShell().Prints("[]");
        var inspector = new FirewallInspector(powerShell, NullLogger<FirewallInspector>.Instance);

        await inspector.ReadAsync();

        Assert.Contains("ActiveStore", Assert.Single(powerShell.Scripts));
    }

    // A read that failed has to stay failed for every row it feeds. Deciding a
    // row against an unread snapshot must report Unknown rather than treating an
    // empty answer as "no rule exists" and offering to add one.
    [Fact]
    public async Task A_failed_read_leaves_every_server_unknown()
    {
        var powerShell = new FakePowerShell().Fails("Access is denied.");
        var inspector = new FirewallInspector(powerShell, NullLogger<FirewallInspector>.Instance);

        var rules = await inspector.ReadAsync();

        foreach (var port in new[] { 19132, 19134 })
        {
            var check = FirewallInspector.Check(rules, "Survival", port);

            Assert.Equal(SystemCheckState.Unknown, check.State);
            Assert.False(check.CanFix);
        }
    }

    // ConvertTo-Json over an empty collection prints nothing at all, which is a
    // successful read of a machine with no UDP rules — not a failed one. Reading
    // it as failure would report Unknown and withhold the fix from exactly the
    // machine that needs it most.
    [Fact]
    public async Task A_machine_with_no_rules_at_all_reads_as_missing_not_unknown()
    {
        var powerShell = new FakePowerShell().Prints(string.Empty);
        var inspector = new FirewallInspector(powerShell, NullLogger<FirewallInspector>.Instance);

        var check = FirewallInspector.Check(await inspector.ReadAsync(), "Survival", 19132);

        Assert.Equal(SystemCheckState.Missing, check.State);
        Assert.True(check.CanFix);
    }

    // The load-bearing invariant of the three-state model: a button that changes
    // the machine's security posture may only ever appear against a check that
    // actually established something is missing.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Nothing_but_a_confirmed_gap_is_ever_offered_a_fix(bool readable)
    {
        var powerShell = readable
            ? new FakePowerShell().Prints("""[{"Name":"Bedrock","Ports":["19132"]}]""")
            : new FakePowerShell().Fails("Access is denied.");

        var inspector = new FirewallInspector(powerShell, NullLogger<FirewallInspector>.Instance);

        var check = FirewallInspector.Check(await inspector.ReadAsync(), "Survival", 19132);

        Assert.NotEqual(SystemCheckState.Missing, check.State);
        Assert.False(check.CanFix);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;

        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }
}
