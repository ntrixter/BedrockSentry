using BedrockSentry.Core.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// The removal log line's phrases (`T179`).
/// </summary>
/// <remarks>
/// The template used to take either a verb or a raw exception sentence in the same
/// slot, so a failure landed mid-clause with nothing marking it as one and the line
/// read as a list of successes: <em>"files deleted, backups The process cannot
/// access the file..."</em>. Anybody grepping the log for a removal that went wrong
/// would not have matched it. These pin every arm as a self-contained phrase.
/// </remarks>
public class RemovalPhraseTests
{
    [Theory]
    [InlineData(DirectoryRemoval.Deleted, null, "deleted")]
    [InlineData(DirectoryRemoval.NotAsked, null, "not deleted")]
    public void A_plain_outcome_is_one_phrase(
        DirectoryRemoval outcome, string? failure, string expected) =>
        Assert.Equal(expected, DirectoryRemovalPhrase.For(outcome, failure));

    [Fact]
    public void A_refusal_names_its_reason_inside_the_sentence()
    {
        var phrase = DirectoryRemovalPhrase.For(
            DirectoryRemoval.Refused, "the directory is outside the instances folder.");

        Assert.Equal(
            "kept, because the directory is outside the instances folder", phrase);
    }

    /// <summary>
    /// The alarming words come first, so a line truncated by a log viewer still
    /// carries them.
    /// </summary>
    [Fact]
    public void A_partial_delete_leads_with_the_loss()
    {
        var phrase = DirectoryRemovalPhrase.For(
            DirectoryRemoval.PartlyDeleted, "The process cannot access the file.");

        Assert.StartsWith("PARTLY DELETED", phrase);
        Assert.Contains("unknown amount is gone", phrase);
        Assert.Contains("The process cannot access the file", phrase);
    }

    /// <summary>
    /// No arm may produce a phrase that reads as success, and none may be empty —
    /// an empty slot is how the original defect rendered.
    /// </summary>
    [Fact]
    public void Every_outcome_produces_a_phrase_and_only_one_of_them_reads_as_success()
    {
        var phrases = Enum.GetValues<DirectoryRemoval>()
            .Select(o => DirectoryRemovalPhrase.For(o, "a reason"))
            .ToArray();

        Assert.All(phrases, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        Assert.Single(phrases, p => p == "deleted");
    }

    /// <summary>
    /// A missing reason must not leave a dangling "because".
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_failure_with_no_reason_still_reads_as_a_sentence(string? failure)
    {
        foreach (var outcome in new[] { DirectoryRemoval.Refused, DirectoryRemoval.PartlyDeleted })
        {
            var phrase = DirectoryRemovalPhrase.For(outcome, failure);

            Assert.Contains("no reason was recorded", phrase);
            Assert.DoesNotContain("because  ", phrase);
        }
    }

    // ---- T187: the panel puts another sentence after the reason ----------------

    /// <summary>
    /// Every <c>Refused</c> reason the product can produce, ended.
    /// </summary>
    /// <remarks>
    /// <para>The arm that was broken, and the reason it survived: not one of
    /// <c>InstanceDirectoryRemoval</c>'s refusals ends in a full stop, so the panel
    /// rendered <em>"... nothing that looks like a Bedrock server Delete C:\... by
    /// hand if you meant to."</em> The <c>PartlyDeleted</c> arms read correctly only
    /// by luck, because exception messages usually do end in a period — which is why
    /// <c>T176</c>'s observation of the partial sentence looked clean and this went
    /// unnoticed beside it.</para>
    ///
    /// <para>These strings are copied from the refusal methods rather than produced
    /// by them: those need a real path on disk to reach, and what is being pinned
    /// here is the punctuation rule, not the refusals. The case that would catch a
    /// drift is the last one — a reason that already ends in a stop must not gain a
    /// second.</para>
    /// </remarks>
    [Theory]
    [InlineData("there is no directory recorded for it")]
    [InlineData("'D:\\x' is the root of a drive")]
    [InlineData("'C:\\a' is one of BedrockSentry's own folders")]
    [InlineData("'C:\\a' contains 'C:\\a\\b', which BedrockSentry needs")]
    [InlineData("'C:\\a' is a junction or symbolic link")]
    [InlineData("'C:\\a' is not a usable path")]
    public void A_reason_is_ended_so_the_next_sentence_does_not_run_into_it(string reason)
    {
        var sentence = DirectoryRemovalPhrase.Sentence(reason);

        Assert.EndsWith(".", sentence);
        Assert.StartsWith(reason, sentence);
        Assert.DoesNotContain("..", sentence);
    }

    [Theory]
    [InlineData("the file is in use.", "the file is in use.")]
    [InlineData("is it though?", "is it though?")]
    [InlineData("stop!", "stop!")]
    public void A_reason_that_already_ends_does_not_gain_a_second_terminator(
        string reason, string expected) =>
        Assert.Equal(expected, DirectoryRemovalPhrase.Sentence(reason));

    /// <summary>
    /// A missing reason still ends, because the panel prints a sentence after it
    /// either way.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_reason_is_still_a_sentence(string? reason) =>
        Assert.Equal("no reason was recorded.", DirectoryRemovalPhrase.Sentence(reason));

    /// <summary>
    /// The log line keeps trimming, and that is not an inconsistency.
    /// </summary>
    /// <remarks>
    /// Slots there are joined with a semicolon, so a full stop mid-line reads as the
    /// end of the entry. Two callers, two answers, on purpose — pinned together so
    /// the next person to "make them consistent" sees that the difference is the
    /// point (<c>T151</c> clause 6 is what that mistake costs).
    /// </remarks>
    [Fact]
    public void The_log_slot_and_the_panel_sentence_deliberately_differ()
    {
        const string Reason = "the file is in use.";

        Assert.Equal("kept, because the file is in use", DirectoryRemovalPhrase.For(
            DirectoryRemoval.Refused, Reason));

        Assert.Equal("the file is in use.", DirectoryRemovalPhrase.Sentence(Reason));
    }

    // ---- T186: when the notice should shout ------------------------------------

    private static InstanceRemovalOutcome Outcome(
        DirectoryRemoval files, DirectoryRemoval backups = DirectoryRemoval.NotAsked) =>
        new("i", "Survival", @"C:\\srv", files, null, 0, backups);

    /// <summary>
    /// Only a partial delete counts as loss, on either folder.
    /// </summary>
    /// <remarks>
    /// The panel drives its palette and its ARIA role from this. It used to render
    /// every outcome in the neutral accent palette with <c>role="status"</c>, on a
    /// CSS comment saying a completed removal is not a failure — written before
    /// <c>T176</c> added an arm that is one, so the only sentence in this product
    /// reporting irreversible data loss arrived muted and polite.
    /// </remarks>
    [Theory]
    [InlineData(DirectoryRemoval.PartlyDeleted, DirectoryRemoval.NotAsked)]
    [InlineData(DirectoryRemoval.NotAsked, DirectoryRemoval.PartlyDeleted)]
    [InlineData(DirectoryRemoval.Deleted, DirectoryRemoval.PartlyDeleted)]
    [InlineData(DirectoryRemoval.PartlyDeleted, DirectoryRemoval.PartlyDeleted)]
    public void A_partial_delete_on_either_folder_is_loss(
        DirectoryRemoval files, DirectoryRemoval backups) =>
        Assert.True(Outcome(files, backups).AnythingLost);

    /// <summary>
    /// <c>Refused</c> is the arm this must not shout about.
    /// </summary>
    /// <remarks>
    /// It is reached only from the guard that runs <em>before</em> anything is
    /// deleted, so it is the one state that can honestly say the folder was kept.
    /// Styling it as loss would be <c>T176</c> in the mirror — telling somebody their
    /// world is damaged when nothing was touched — which is why this case is
    /// enumerated rather than left to the default.
    /// </remarks>
    [Theory]
    [InlineData(DirectoryRemoval.Refused, DirectoryRemoval.Refused)]
    [InlineData(DirectoryRemoval.Deleted, DirectoryRemoval.Deleted)]
    [InlineData(DirectoryRemoval.NotAsked, DirectoryRemoval.NotAsked)]
    [InlineData(DirectoryRemoval.Deleted, DirectoryRemoval.Refused)]
    [InlineData(DirectoryRemoval.Refused, DirectoryRemoval.NotAsked)]
    public void Nothing_else_is_loss(DirectoryRemoval files, DirectoryRemoval backups) =>
        Assert.False(Outcome(files, backups).AnythingLost);

    /// <summary>
    /// Every state is covered by one of the two theories above.
    /// </summary>
    /// <remarks>
    /// A fifth <c>DirectoryRemoval</c> added later would be absent from both and the
    /// panel would quietly treat it as "not loss" — the default that let this defect
    /// exist in the first place. This fails the moment the enum grows.
    /// </remarks>
    [Fact]
    public void Every_removal_state_has_been_decided_about()
    {
        var states = Enum.GetValues<DirectoryRemoval>();

        Assert.Equal(4, states.Length);

        foreach (var state in states)
        {
            var lost = Outcome(state).AnythingLost;
            Assert.Equal(state is DirectoryRemoval.PartlyDeleted, lost);
        }
    }

    // ---- T190: two render sites, opposite capitalisation -----------------------

    /// <summary>
    /// The panel uses a reason in two positions and they want opposite casing.
    /// </summary>
    /// <remarks>
    /// <para>This is the <c>T151</c> clause 6 guard in the same shape the file already
    /// uses for <see cref="DirectoryRemovalPhrase.For"/> versus
    /// <see cref="DirectoryRemovalPhrase.Sentence"/>: the protection is a test pinning
    /// the two callers to <strong>different</strong> answers, not the fact that they
    /// share a helper. Making two sites agree is only an improvement when both want the
    /// same answer.</para>
    ///
    /// <para>What made this invisible is that the older behaviour was not merely
    /// untested, it was pinned: <c>A_reason_is_ended_so_the_next_sentence_does_not_run
    /// _into_it</c> asserts the initial casing survives, and every case it pins is a
    /// <c>Refused</c> reason — which the panel renders after a colon, where lowercase
    /// is right. So the suite certified the behaviour for one site and said nothing
    /// about the other.</para>
    /// </remarks>
    [Theory]
    [InlineData("the file is in use", "the file is in use.", "The file is in use.")]
    [InlineData("'C:\\a' is a junction or symbolic link",
        "'C:\\a' is a junction or symbolic link.", "'C:\\a' is a junction or symbolic link.")]
    [InlineData("Access to the path is denied.",
        "Access to the path is denied.", "Access to the path is denied.")]
    public void A_reason_after_a_colon_and_one_that_opens_a_sentence_differ(
        string reason, string afterColon, string opening)
    {
        Assert.Equal(afterColon, DirectoryRemovalPhrase.Sentence(reason));
        Assert.Equal(opening, DirectoryRemovalPhrase.OpeningSentence(reason));
    }

    /// <summary>
    /// Only the first character is touched.
    /// </summary>
    /// <remarks>
    /// These reasons carry Windows paths, and upper-casing beyond the first letter
    /// would corrupt one. A reason that already begins with a capital, a quote or a
    /// path separator comes back byte-for-byte.
    /// </remarks>
    [Theory]
    [InlineData("the console transcripts at 'C:\\a\\Logs' could not all be deleted")]
    [InlineData("deleting 'D:\\worlds\\db' failed")]
    public void Opening_a_sentence_changes_the_first_letter_and_nothing_else(string reason)
    {
        var opened = DirectoryRemovalPhrase.OpeningSentence(reason);
        var plain = DirectoryRemovalPhrase.Sentence(reason);

        Assert.Equal(char.ToUpperInvariant(plain[0]), opened[0]);
        Assert.Equal(plain[1..], opened[1..]);
    }

    /// <summary>
    /// A missing reason still opens a sentence properly.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_reason_still_opens_a_sentence(string? reason) =>
        Assert.Equal("No reason was recorded.", DirectoryRemovalPhrase.OpeningSentence(reason));

    // ---- T190: the transcripts are not world data ------------------------------

    private static InstanceRemovalOutcome WithTranscripts(DirectoryRemoval transcripts) =>
        new("i", "Survival", @"C:\srv", DirectoryRemoval.Deleted, null, 0,
            DirectoryRemoval.NotAsked, null, transcripts, "a log file is in use");

    /// <summary>
    /// A transcript tree that part-deleted is not loss, in any state.
    /// </summary>
    /// <remarks>
    /// <para>Console transcripts are log files. A half-deleted one is untidy, not
    /// irreversible, and <c>AnythingLost</c> means "world data or archives are
    /// gone" — the flag the panel turns the danger palette on from.</para>
    ///
    /// <para>It counted before, by accident. A part-deleted transcript tree was
    /// promoted into <c>Files</c>, so a removal in which the world was deleted
    /// perfectly rendered as loss and, worse, said so: the operator was told part of
    /// the world was already gone and sent to check a folder that was no longer
    /// there. The promotion was guarded by <c>files is not PartlyDeleted</c> and
    /// <c>files</c> at that point could only be <c>Deleted</c> or
    /// <c>PartlyDeleted</c>, so there was <strong>no execution path</strong> on which
    /// that sentence was true.</para>
    /// </remarks>
    [Theory]
    [InlineData(DirectoryRemoval.PartlyDeleted)]
    [InlineData(DirectoryRemoval.Refused)]
    [InlineData(DirectoryRemoval.Deleted)]
    [InlineData(DirectoryRemoval.NotAsked)]
    public void A_transcript_failure_is_never_loss(DirectoryRemoval transcripts) =>
        Assert.False(WithTranscripts(transcripts).AnythingLost);

    /// <summary>
    /// And the world's own outcome is still reported truthfully beside it.
    /// </summary>
    /// <remarks>
    /// The point of the separate slot: <c>Files</c> says what happened to the working
    /// directory and only that, which is what its name claims.
    /// </remarks>
    [Fact]
    public void A_transcript_failure_leaves_the_files_outcome_alone()
    {
        var outcome = WithTranscripts(DirectoryRemoval.PartlyDeleted);

        Assert.Equal(DirectoryRemoval.Deleted, outcome.Files);
        Assert.Null(outcome.FilesFailedBecause);
        Assert.Equal(DirectoryRemoval.PartlyDeleted, outcome.Transcripts);
        Assert.Equal("a log file is in use", outcome.TranscriptsFailedBecause);
    }
}
