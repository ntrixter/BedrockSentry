using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Services.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// Whether the Minecraft client on this machine can reach a server on it.
/// </summary>
/// <remarks>
/// The check exists because this failure is indistinguishable from a firewall
/// problem and is not one — turning the firewall off changes nothing, which is
/// exactly the evidence that sends somebody looking in the wrong place. These
/// tests are mostly about not claiming things: an absent client must produce no
/// row, and an unreadable answer must not produce an offer.
/// </remarks>
public class AppIsolationTests
{
    private const string Retail = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";
    private const string Preview = "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe";

    [Fact]
    public void An_exempted_client_is_good()
    {
        var package = Assert.Single(
            AppIsolationInspector.Packages($$"""[{"Package":"{{Retail}}","Exempt":true}]"""));

        var check = AppIsolationInspector.Check(package);

        Assert.Equal(SystemCheckState.Good, check.State);
        Assert.False(check.CanFix);
    }

    [Fact]
    public void An_isolated_client_is_offered_an_exemption_that_says_what_it_costs()
    {
        var package = Assert.Single(
            AppIsolationInspector.Packages($$"""[{"Package":"{{Retail}}","Exempt":false}]"""));

        var check = AppIsolationInspector.Check(package);

        Assert.Equal(SystemCheckState.Missing, check.State);
        Assert.True(check.CanFix);
        Assert.NotNull(check.Warning);

        // The whole reason the row is worth having: it says this is not the
        // firewall, because everyone checks the firewall first.
        Assert.Contains("firewall", check.Detail);

        // And it says where to connect afterwards, since the exemption alone
        // does not make the machine's own network address work.
        Assert.Contains("127.0.0.1", check.FixDescription);
    }

    // A dedicated server has no client to isolate. A Good row there would be a
    // thing to read and dismiss on every visit, forever.
    [Fact]
    public void No_minecraft_here_means_no_row_at_all()
    {
        Assert.Empty(AppIsolationInspector.Packages("[]"));
        Assert.Empty(AppIsolationInspector.Packages(string.Empty));
    }

    // Both can be installed side by side, and they are exempted separately.
    [Fact]
    public void Retail_and_preview_are_separate_rows()
    {
        var packages = AppIsolationInspector.Packages(
            $$"""
            [{"Package":"{{Retail}}","Exempt":true},{"Package":"{{Preview}}","Exempt":false}]
            """);

        Assert.Equal(2, packages.Count);
        Assert.Equal("Minecraft", AppIsolationInspector.Friendly(packages[0].Family));
        Assert.Equal("Minecraft Preview", AppIsolationInspector.Friendly(packages[1].Family));

        Assert.Equal(SystemCheckState.Good, AppIsolationInspector.Check(packages[0]).State);
        Assert.Equal(SystemCheckState.Missing, AppIsolationInspector.Check(packages[1]).State);
    }

    // ConvertTo-Json emits a bare object for a single result. A machine with
    // exactly one Minecraft installed is the ordinary case, so reading only the
    // array shape would miss almost everybody.
    [Fact]
    public void A_single_package_arrives_as_an_object_and_is_still_read()
    {
        var package = Assert.Single(
            AppIsolationInspector.Packages($$"""{"Package":"{{Retail}}","Exempt":false}"""));

        Assert.Equal(Retail, package.Family);
    }

    // Output that is not JSON is not evidence Minecraft is installed, and
    // inventing a package would offer an exemption to something absent.
    [Theory]
    [InlineData("not json at all")]
    [InlineData("""[{"Exempt":true}]""")]
    [InlineData("""[{"Package":"","Exempt":true}]""")]
    public void Unreadable_output_claims_nothing(string json) =>
        Assert.Empty(AppIsolationInspector.Packages(json));

    // Anything other than an explicit true is not an exemption. Guessing the
    // other way would report Good and leave somebody unable to connect with
    // nothing left on the page suggesting why.
    [Theory]
    [InlineData("""[{"Package":"Microsoft.MinecraftUWP_8wekyb3d8bbwe","Exempt":false}]""")]
    [InlineData("""[{"Package":"Microsoft.MinecraftUWP_8wekyb3d8bbwe","Exempt":null}]""")]
    [InlineData("""[{"Package":"Microsoft.MinecraftUWP_8wekyb3d8bbwe"}]""")]
    public void Only_an_explicit_exemption_counts(string json) =>
        Assert.False(Assert.Single(AppIsolationInspector.Packages(json)).Exempt);

    // Same rule as every other check: could-not-ask is Unknown, and Unknown
    // never offers to change the machine.
    [Fact]
    public void A_check_that_could_not_run_is_unknown_and_offers_nothing()
    {
        var check = AppIsolationInspector.Unavailable("Access is denied.");

        Assert.Equal(SystemCheckState.Unknown, check.State);
        Assert.False(check.CanFix);
        Assert.Contains("Access is denied.", check.Detail);
    }

    [Fact]
    public void An_unrecognised_package_is_named_by_its_identifier() =>
        Assert.Equal("Some.Other_pkg", AppIsolationInspector.Friendly("Some.Other_pkg"));

    // The exemption is additive. Replacing the list would silently revoke
    // whatever somebody else had exempted on this machine.
    [Fact]
    public async Task Granting_an_exemption_adds_to_the_list_rather_than_replacing_it()
    {
        var powerShell = new FakePowerShell().Prints("ok");
        var inspector = new AppIsolationInspector(
            powerShell, NullLogger<AppIsolationInspector>.Instance);

        await inspector.ExemptAsync(Retail);

        var script = Assert.Single(powerShell.Scripts);

        Assert.Contains("LoopbackExempt -a", script);
        Assert.DoesNotContain("LoopbackExempt -c", script);
    }

    [Fact]
    public async Task An_apostrophe_in_a_package_name_cannot_break_out_of_the_script()
    {
        var powerShell = new FakePowerShell().Prints("ok");
        var inspector = new AppIsolationInspector(
            powerShell, NullLogger<AppIsolationInspector>.Instance);

        await inspector.ExemptAsync("evil'; Remove-Item C:\\ -Recurse; '");

        Assert.Contains("evil''; Remove-Item", Assert.Single(powerShell.Scripts));
    }

    [Fact]
    public async Task A_failed_exemption_reports_why()
    {
        var powerShell = new FakePowerShell().Fails("Access is denied.");
        var inspector = new AppIsolationInspector(
            powerShell, NullLogger<AppIsolationInspector>.Instance);

        var fix = await inspector.ExemptAsync(Retail);

        Assert.False(fix.Succeeded);
        Assert.Contains("Access is denied.", fix.Message);
    }
}
