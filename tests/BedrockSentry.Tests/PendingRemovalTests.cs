using BedrockSentry.Core.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// `T169`. The holder that carries a removal report across the navigation off the
/// removed instance's page.
/// </summary>
/// <remarks>
/// Small enough to look not worth testing, which is why it is tested: both ways of
/// getting it wrong are silent. A holder that never clears shows the notice on every
/// later visit to the Status page, and one that clears too eagerly shows it never —
/// and the page half of this feature lives in `.razor` files the suite cannot reach,
/// so this is the only part of it a test can see at all.
/// </remarks>
public sealed class PendingRemovalTests
{
    private static InstanceRemovalOutcome Removed(string name = "survival") => new(
        InstanceId: name,
        Name: name,
        WorkingDirectory: @"C:\servers\survival",
        Files: DirectoryRemoval.Refused,
        FilesFailedBecause: "the directory is outside the instances folder",
        PlayerRecordsForgotten: 12);

    [Fact]
    public void Nothing_posted_means_nothing_to_show()
    {
        Assert.Null(new PendingRemoval().Take());
    }

    [Fact]
    public void A_posted_report_comes_back_whole()
    {
        var notice = new PendingRemoval();
        var outcome = Removed();

        notice.Post(outcome);

        Assert.Same(outcome, notice.Take());
    }

    /// <summary>
    /// The clause that stops the notice following the administrator around.
    /// </summary>
    /// <remarks>
    /// Without it, every later visit to the Status page reports a removal that
    /// happened once — and there is no dialog to dismiss it from a second time.
    /// </remarks>
    [Fact]
    public void Taken_once_and_then_gone()
    {
        var notice = new PendingRemoval();
        notice.Post(Removed());

        Assert.NotNull(notice.Take());
        Assert.Null(notice.Take());
        Assert.Null(notice.Take());
    }

    /// <remarks>
    /// Cannot happen from the panel, because the first removal navigates away. If
    /// it ever does, the newer report is the one worth showing rather than a queue
    /// nobody drains.
    /// </remarks>
    [Fact]
    public void A_second_report_replaces_an_unread_first()
    {
        var notice = new PendingRemoval();

        notice.Post(Removed("creative"));
        notice.Post(Removed("survival"));

        Assert.Equal("survival", notice.Take()?.Name);
        Assert.Null(notice.Take());
    }

    /// <remarks>
    /// A null here would be a notice that renders a blank panel rather than no
    /// panel, which reads as a removal that reported nothing.
    /// </remarks>
    [Fact]
    public void Posting_nothing_is_a_mistake_rather_than_an_empty_notice()
    {
        Assert.Throws<ArgumentNullException>(() => new PendingRemoval().Post(null!));
    }
}
