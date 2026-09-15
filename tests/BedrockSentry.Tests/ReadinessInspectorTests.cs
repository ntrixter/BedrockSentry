using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Services.Diagnostics;
using BedrockSentry.Services.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 13. The readings are Windows-only; the decisions made from them are
/// not, and the decisions are the half that can be wrong — so they are pure
/// static methods over a captured reading, the same split the firewall and
/// Defender checks use.
/// </summary>
public class ReadinessInspectorTests
{
    private static ReadinessInspector.VssState Read(string status, string startType) =>
        new(true, $$"""{"Status":"{{status}}","StartType":"{{startType}}"}""", string.Empty);

    /// <summary>
    /// The one that matters most, because it is the one a naive check gets
    /// wrong: VSS is demand-started and spends almost all of its life stopped on
    /// a perfectly healthy machine.
    /// </summary>
    [Theory]
    [InlineData("Stopped", "Manual")]
    [InlineData("Running", "Manual")]
    [InlineData("Stopped", "Automatic")]
    public void A_stopped_but_startable_service_is_good_not_broken(string status, string startType)
    {
        var check = ReadinessInspector.CheckVss(Read(status, startType), false);

        Assert.Equal(SystemCheckState.Good, check.State);
        Assert.False(check.CanFix);
    }

    [Fact]
    public void A_disabled_service_is_the_only_actionable_state()
    {
        var check = ReadinessInspector.CheckVss(Read("Stopped", "Disabled"), false);

        Assert.Equal(SystemCheckState.Missing, check.State);
        Assert.True(check.CanFix);

        // Says what pressing it changes, before it is pressed.
        Assert.Contains("Manual", check.FixDescription);
    }

    // Changes the sentence, never the state. A disabled service is worth
    // reporting either way, but only one of these two is true at a time.
    [Fact]
    public void Whether_anything_needs_it_changes_the_wording_not_the_verdict()
    {
        var idle = ReadinessInspector.CheckVss(Read("Stopped", "Disabled"), false);
        var inUse = ReadinessInspector.CheckVss(Read("Stopped", "Disabled"), true);

        Assert.Equal(idle.State, inUse.State);
        Assert.Contains("Nothing here needs it yet", idle.Detail);
        Assert.Contains("will fail", inUse.Detail);
    }

    /// <summary>
    /// "Could not ask Windows" is not "the service is disabled". Reporting the
    /// first as the second would have somebody changing a service nothing had
    /// inspected.
    /// </summary>
    [Fact]
    public void A_read_that_did_not_happen_is_unknown_and_offers_nothing()
    {
        var check = ReadinessInspector.CheckVss(
            new ReadinessInspector.VssState(false, string.Empty, "Access is denied."), false);

        Assert.Equal(SystemCheckState.Unknown, check.State);
        Assert.False(check.CanFix);
        Assert.Contains("Access is denied.", check.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"Status":"Stopped"}""")]
    public void An_answer_that_does_not_parse_is_unknown_rather_than_a_guess(string json)
    {
        var check = ReadinessInspector.CheckVss(
            new ReadinessInspector.VssState(true, json, string.Empty), false);

        Assert.Equal(SystemCheckState.Unknown, check.State);
        Assert.False(check.CanFix);
    }

    [Fact]
    public void Free_ports_are_good_and_carry_no_warning()
    {
        var check = ReadinessInspector.CheckPorts("Survival", []);

        Assert.Equal(SystemCheckState.Good, check.State);
        Assert.Null(check.Warning);
    }

    /// <summary>
    /// The warning is the point of this row. A Bedrock server that cannot bind
    /// starts, binds nothing and exits, which the watchdog reports as a crash
    /// loop with no mention of ports anywhere.
    /// </summary>
    [Fact]
    public void A_contended_port_is_reported_with_what_holds_it_and_why_it_matters()
    {
        var check = ReadinessInspector.CheckPorts(
            "Survival",
            [
                new PortConflict(19132, "is already open on this machine by SomeOtherThing (4242)"),
                new PortConflict(19133, "is Creative's server-portv6"),
            ]);

        Assert.Equal(SystemCheckState.Missing, check.State);
        Assert.Contains("19132", check.Detail);
        Assert.Contains("SomeOtherThing", check.Detail);
        Assert.Contains("19133", check.Detail);
        Assert.Contains("crash loop", check.Warning);

        // Nothing to press: the wrapper cannot free somebody else's port, and
        // the answer is a choice between two things only a person can make.
        Assert.False(check.CanFix);
    }

    [Fact]
    public void The_port_row_is_named_before_it_is_answered()
    {
        Assert.Contains("Survival", ReadinessInspector.PortTitle("Survival"));
    }

    /// <summary>
    /// A process cannot grant itself privileges, so this row is a report
    /// whatever it finds — a button implying otherwise would be a lie.
    /// </summary>
    [Fact]
    public void The_privilege_row_never_offers_a_fix()
    {
        var check = ReadinessInspector.CheckPrivileges();

        Assert.False(check.CanFix);
        Assert.Equal(ReadinessInspector.PrivilegeTitle, check.Title);

        // Off Windows there is nothing to inspect, and saying so beats claiming
        // the account is fine.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(SystemCheckState.Unknown, check.State);
        }
    }
}
