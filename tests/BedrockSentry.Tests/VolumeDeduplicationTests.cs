using BedrockSentry.Core.Diagnostics;

namespace BedrockSentry.Tests;

/// <summary>
/// The storage section's deduplication column (`D55`): matching what the
/// service reports to the volumes the wrapper cares about, and what the cell
/// then says. Pure, so it runs everywhere — the WMI read itself is Windows-only
/// and deliberately thin.
/// </summary>
public class VolumeDeduplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 15, 0, 0, TimeSpan.Zero);

    private static DeduplicationRow Row(
        string volume = "D:",
        bool enabled = true,
        long saved = 12L * 1024 * 1024 * 1024,
        int percent = 74,
        DateTimeOffset? optimised = null,
        bool succeeded = true,
        string? message = null) =>
        new(volume, enabled, saved, percent, optimised ?? Now.AddHours(-3), succeeded, message);

    // The ordinary answer on a client edition, and on a server that has not
    // added the role. Null rows, not an empty list — the two mean different
    // things and the page shows them differently.
    [Fact]
    public void No_role_means_every_volume_is_not_installed()
    {
        var result = VolumeDeduplication.Assemble([@"C:\", @"D:\"], rows: null);

        Assert.All(result.Values, v => Assert.Equal(DeduplicationState.NotInstalled, v.State));
        Assert.False(VolumeDeduplication.ColumnWorthShowing(result.Values));
    }

    [Fact]
    public void Role_present_but_volume_not_enabled_reads_as_not_enabled()
    {
        var result = VolumeDeduplication.Assemble([@"C:\"], []);

        Assert.Equal(DeduplicationState.NotEnabled, result[@"C:\"].State);
        Assert.Equal("Not enabled", result[@"C:\"].Describe(Now));
        Assert.True(VolumeDeduplication.ColumnWorthShowing(result.Values));
    }

    // DriveInfo says "D:\", the service says "D:", and somebody's config says
    // "d:\". The join has to survive all three or every volume reads as not
    // enabled on a machine where it plainly is.
    [Theory]
    [InlineData(@"D:\", "D:")]
    [InlineData(@"d:\", "D:")]
    [InlineData(@"D:\", "d:")]
    [InlineData("D:", @"D:\")]
    public void A_row_matches_its_root_whatever_the_separator_or_case(string root, string reported)
    {
        var result = VolumeDeduplication.Assemble([root], [Row(volume: reported)]);

        Assert.Equal(DeduplicationState.Enabled, result[root].State);
    }

    // The result is keyed by the root as it was asked for, because that is the
    // key HostInspector already holds and will look up with.
    [Fact]
    public void Results_are_keyed_by_the_root_as_passed_in()
    {
        var result = VolumeDeduplication.Assemble([@"E:\"], [Row(volume: "E:")]);

        Assert.True(result.ContainsKey(@"E:\"));
    }

    [Fact]
    public void A_healthy_volume_names_the_saving_the_share_and_when()
    {
        var result = VolumeDeduplication.Assemble([@"D:\"], [Row()]);
        var text = result[@"D:\"].Describe(Now);

        Assert.StartsWith("Saving 12 GB (74% of the volume)", text);
        Assert.EndsWith(" ago", text);
        Assert.False(result[@"D:\"].NeedsAttention);
    }

    // Post-processing: enabled today, nothing has happened until the schedule
    // fires. "Saving 0 B (0%)" would read as broken.
    [Fact]
    public void Enabled_but_never_optimised_says_so_rather_than_saving_nothing()
    {
        var row = new DeduplicationRow("D:", true, 0, 0, null, true, null);
        var result = VolumeDeduplication.Assemble([@"D:\"], [row]);

        Assert.Equal("Enabled — not yet optimised", result[@"D:\"].Describe(Now));
        Assert.False(result[@"D:\"].NeedsAttention);
    }

    // The case worth an alarm. A failing job is a volume filling up while the
    // headline number still looks fine.
    [Fact]
    public void A_failed_optimisation_is_flagged_and_carries_the_reason()
    {
        var result = VolumeDeduplication.Assemble(
            [@"D:\"], [Row(succeeded: false, message: "The volume is nearly full.")]);
        var state = result[@"D:\"];

        Assert.True(state.NeedsAttention);
        Assert.StartsWith("Last optimisation failed", state.Describe(Now));
        Assert.EndsWith(": The volume is nearly full.", state.Describe(Now));
    }

    [Fact]
    public void A_failed_optimisation_with_no_message_does_not_trail_a_colon()
    {
        var result = VolumeDeduplication.Assemble([@"D:\"], [Row(succeeded: false)]);

        Assert.DoesNotContain(":", result[@"D:\"].Describe(Now)[^5..]);
    }

    // Disabled after use keeps its number: the data is still deduplicated, and
    // the free-space column beside it is still leaning on that.
    [Fact]
    public void Disabled_with_data_still_deduplicated_says_how_much()
    {
        var result = VolumeDeduplication.Assemble([@"D:\"], [Row(enabled: false)]);
        var state = result[@"D:\"];

        Assert.Equal(DeduplicationState.NotEnabled, state.State);
        Assert.Equal("Disabled — 12 GB still deduplicated", state.Describe(Now));
        Assert.False(state.NeedsAttention);
    }

    // Installed but unreadable is neither "not installed" nor "not enabled". An
    // administrator who turned it on is told the panel cannot see it.
    [Fact]
    public void Unreadable_is_its_own_state_and_carries_the_reason()
    {
        var result = VolumeDeduplication.AssembleUnknown([@"C:\", @"D:\"], "Access is denied.");

        Assert.All(result.Values, v => Assert.Equal(DeduplicationState.Unknown, v.State));
        Assert.Equal("Could not be read: Access is denied.", result[@"C:\"].Describe(Now));
        Assert.True(VolumeDeduplication.ColumnWorthShowing(result.Values));
    }

    [Fact]
    public void A_job_stamped_in_the_future_reads_as_just_now_not_negative()
    {
        var result = VolumeDeduplication.Assemble([@"D:\"], [Row(optimised: Now.AddMinutes(5))]);

        Assert.EndsWith("optimised just now", result[@"D:\"].Describe(Now));
    }

    [Theory]
    [InlineData(@"C:\", "C:")]
    [InlineData("c:", "C:")]
    [InlineData(@"  D:\  ", "D:")]
    [InlineData(@"\\?\Volume{abc}\", @"\\?\VOLUME{ABC}")]
    public void Normalisation_strips_the_separator_and_case(string input, string expected) =>
        Assert.Equal(expected, VolumeDeduplication.NormaliseVolume(input));
}
