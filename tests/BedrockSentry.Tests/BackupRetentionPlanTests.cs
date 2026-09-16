using BedrockSentry.Core.Backups;

namespace BedrockSentry.Tests;

/// <summary>
/// Pins the retention decision itself, away from any filesystem.
/// </summary>
/// <remarks>
/// The defect these exist for could not be reproduced by writing real folders: it
/// needed the just-written artifact to sort behind an older one, which on a real
/// disk depends on timestamp granularity and on what else has touched the files.
/// Deciding from names makes every one of those cases an ordinary argument (T198).
/// </remarks>
public class BackupRetentionPlanTests
{
    private const string Vss = "vss-";
    private const string Zip = "backup-";
    private const string ZipExt = ".zip";

    private static BackupRetention Count(int value) =>
        new() { Mode = BackupRetentionMode.Count, Value = value };

    private static BackupRetention Days(int value) =>
        new() { Mode = BackupRetentionMode.Days, Value = value };

    private static readonly DateTime Now = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Keeping_one_of_three_removes_two()
    {
        var removed = BackupRetentionPlan.NamesToRemove(
            ["vss-20260101-000000", "vss-20260101-000100", "vss-20260101-000200"],
            Vss, "", Count(1), Now, justWritten: "vss-20260101-000200");

        Assert.Equal(["vss-20260101-000100", "vss-20260101-000000"], removed);
    }

    /// <summary>
    /// THE ARM THAT COULD LIE. The just-written artifact does not sort newest — the
    /// state a coarse or rewritten filesystem timestamp used to produce. It must
    /// still survive, and it must still occupy its keep slot, so exactly one other
    /// artifact is kept and the rest go. Before the fix this left Value + 1.
    /// </summary>
    [Fact]
    public void The_one_just_written_is_kept_and_costs_a_slot_even_when_it_is_not_newest()
    {
        var removed = BackupRetentionPlan.NamesToRemove(
            ["vss-20260101-000000", "vss-20260101-000100", "vss-20260101-000200"],
            Vss, "", Count(2), Now, justWritten: "vss-20260101-000000");

        // Kept: the just-written one (pinned) plus the newest of the rest.
        Assert.Equal(["vss-20260101-000100"], removed);
        Assert.DoesNotContain("vss-20260101-000000", removed);
    }

    [Fact]
    public void The_one_just_written_is_never_removed_by_the_days_rule_either()
    {
        // Its own stamp is a year older than the cutoff, which is what a clock that
        // moved backwards would produce. It is still the artifact this run wrote, so
        // the age rule must not reach it - and the other one is inside the window,
        // so nothing at all is removed.
        var removed = BackupRetentionPlan.NamesToRemove(
            ["vss-20250101-000000", "vss-20260109-120000"],
            Vss, "", Days(1), Now, justWritten: "vss-20250101-000000");

        Assert.Empty(removed);
    }

    [Fact]
    public void A_name_without_a_readable_stamp_is_never_removed_and_never_counted()
    {
        var removed = BackupRetentionPlan.NamesToRemove(
            ["vss-notes", "vss-", "vss-20260101-000000", "vss-20260101-000100"],
            Vss, "", Count(1), Now, justWritten: "vss-20260101-000100");

        Assert.Equal(["vss-20260101-000000"], removed);
    }

    [Fact]
    public void Keep_all_and_a_non_positive_count_remove_nothing()
    {
        string[] names = ["vss-20260101-000000", "vss-20260101-000100"];

        Assert.Empty(BackupRetentionPlan.NamesToRemove(
            names, Vss, "", new BackupRetention(), Now, null));

        Assert.Empty(BackupRetentionPlan.NamesToRemove(
            names, Vss, "", Count(0), Now, null));
    }

    [Fact]
    public void Nothing_is_removed_when_there_are_fewer_than_the_count()
    {
        var removed = BackupRetentionPlan.NamesToRemove(
            ["vss-20260101-000000"], Vss, "", Count(3), Now, justWritten: "vss-20260101-000000");

        Assert.Empty(removed);
    }

    [Fact]
    public void The_days_rule_measures_from_the_stamp()
    {
        var removed = BackupRetentionPlan.NamesToRemove(
            ["vss-20260101-000000", "vss-20260109-120000"],
            Vss, "", Days(2), Now, justWritten: null);

        Assert.Equal(["vss-20260101-000000"], removed);
    }

    [Fact]
    public void A_suffix_is_honoured_so_the_zip_strategy_reads_its_own_names()
    {
        var removed = BackupRetentionPlan.NamesToRemove(
            ["backup-20260101-000000.zip", "backup-20260101-000100.zip"],
            Zip, ZipExt, Count(1), Now, justWritten: "backup-20260101-000100.zip");

        Assert.Equal(["backup-20260101-000000.zip"], removed);
    }

    /// <summary>
    /// Composing and reading a name go through one constant, so the format cannot be
    /// written one way and parsed another — which is the shape that let ordering and
    /// naming disagree in the first place.
    /// </summary>
    [Fact]
    public void A_composed_name_reads_back_as_the_time_it_was_composed_from()
    {
        var started = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

        var vss = BackupRetentionPlan.ComposeName(Vss, started);
        var zip = BackupRetentionPlan.ComposeName(Zip, started, ZipExt);

        Assert.Equal("vss-20260304-050607", vss);
        Assert.Equal("backup-20260304-050607.zip", zip);

        Assert.True(BackupRetentionPlan.TryReadStamp(vss, Vss, "", out var back));
        Assert.Equal(started.UtcDateTime, back);

        Assert.True(BackupRetentionPlan.TryReadStamp(zip, Zip, ZipExt, out var backZip));
        Assert.Equal(started.UtcDateTime, backZip);
    }

    [Fact]
    public void A_name_belonging_to_another_strategy_is_not_read_as_this_one()
    {
        Assert.False(BackupRetentionPlan.TryReadStamp(
            "backup-20260101-000000.zip", Vss, "", out _));

        Assert.False(BackupRetentionPlan.TryReadStamp(
            "vss-20260101-000000", Zip, ZipExt, out _));
    }
}
