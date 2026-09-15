using BedrockSentry.Services.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// The one place the port-conflict sentences live (<c>T149</c>, and the
/// <c>T126</c> class before it). Three renderers read it; this pins what they
/// all get.
/// </summary>
public class PortConflictTextTests
{
    private static readonly PortConflict Held =
        new(19136, "is already open on this machine by bedrock_server (PID 8172)");

    private static readonly PortConflict Sibling =
        new(19137, "is Creative's server-portv6");

    [Fact]
    public void A_line_is_the_port_and_the_reason_as_one_sentence()
    {
        Assert.Equal(
            "Port 19136 is already open on this machine by bedrock_server (PID 8172).",
            PortConflictText.Line(Held));
    }

    [Fact]
    public void Describe_joins_every_conflict_one_sentence_each()
    {
        var text = PortConflictText.Describe([Held, Sibling]);

        Assert.Equal(
            "Port 19136 is already open on this machine by bedrock_server (PID 8172). "
            + "Port 19137 is Creative's server-portv6.",
            text);
    }

    // The refusal is what an operator reads on a red badge and on the console.
    // It has to carry the holder — process and PID — and say that nothing was
    // opened, because the whole point of refusing is that the world was never
    // touched by a second process.
    [Fact]
    public void The_refusal_names_the_holder_and_says_the_world_was_not_opened()
    {
        var reason = PortConflictText.RefusalReason([Held]);

        Assert.StartsWith("not started: ", reason);
        Assert.Contains("bedrock_server (PID 8172)", reason);
        Assert.Contains("19136", reason);
        Assert.Contains("Nothing was launched", reason);
        Assert.Contains("world was not opened", reason);
    }

    [Fact]
    public void The_crash_loop_warning_says_what_the_watchdog_will_and_will_not_tell_you()
    {
        Assert.Contains("crash loop", PortConflictText.CrashLoopWarning);
        Assert.Contains("no mention of ports", PortConflictText.CrashLoopWarning);
    }

    /// <summary>
    /// Both refusal reasons open with the same four words, and until now nothing
    /// said so. `RestoreResult.Summary()` reads straight on from "The server
    /// was", so a producer that dropped the opening would render "The server was
    /// Port 19132 is already open" on the panel — grammatical nonsense the suite
    /// could not see, because the sentence used to live in a .razor file.
    /// </summary>
    [Fact]
    public void Every_refusal_reason_opens_with_not_started()
    {
        Assert.StartsWith(
            "not started: ",
            PortConflictText.RefusalReason([Held]),
            StringComparison.Ordinal);

        Assert.StartsWith(
            "not started: ",
            BedrockSentry.Core.Instances.OrphanText.RefusalReason(
                [new BedrockSentry.Core.Instances.OrphanedServer(
                    8172, @"C:\instances\survival\bedrock_server.exe", null)]),
            StringComparison.Ordinal);
    }
}
