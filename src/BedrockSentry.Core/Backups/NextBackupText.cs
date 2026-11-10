using BedrockSentry.Core.Diagnostics;

namespace BedrockSentry.Core.Backups;

/// <summary>The two lines and the colour of the <em>Next run</em> card.</summary>
/// <param name="Headline">The value line.</param>
/// <param name="Detail">The sentence that qualifies it.</param>
/// <param name="IsFault">Whether this is a state to say loudly.</param>
public readonly record struct NextBackupLines(string Headline, string Detail, bool IsFault);

/// <summary>
/// What the <em>Next run</em> card says, given when a run is due and whether
/// anything would write one.
/// </summary>
/// <remarks>
/// <para><strong>Two questions, and the format one outranks the schedule.</strong>
/// A server with every event ticked and no format enabled read <em>"still backed
/// up on the events ticked under Settings"</em> — each half true, the sentence
/// false, because a trigger with nothing to write fires and produces nothing.
/// <see cref="BackupFormatSummary.BackupFormatState.Disabled"/> is answered
/// before <see cref="NextBackup.State"/> is looked at, and
/// <c>NextBackupTextTests</c> pins that for every schedule state rather than for
/// the one somebody had in mind.</para>
///
/// <para><strong>It does not decide whether the scheduler fires.</strong>
/// <see cref="NextBackup.IsDue"/> is the scheduler's question and is untouched:
/// a run that starts and reports that no strategy is enabled is a record of an
/// attempt, and suppressing it here would take that away to make a card tidier
/// (<c>T151</c> clause 6 — one rule, two callers who want different answers
/// from it).</para>
///
/// <para>In <c>Core</c> because it was a <c>switch</c> in a <c>.razor</c> the
/// suite cannot read, with a <c>_ =&gt;</c> arm that would have absorbed a new
/// state silently (<c>T252</c>).</para>
/// </remarks>
public static class NextBackupText
{
    /// <summary>What the card says.</summary>
    /// <param name="due">When the next unattended run is due.</param>
    /// <param name="format">What, if anything, would write it.</param>
    /// <param name="now">
    /// The current moment, passed in so the countdown is testable rather than
    /// read off the clock inside a switch.
    /// </param>
    public static NextBackupLines For(
        NextBackup due, BackupFormatSummary.BackupFormatState format, DateTimeOffset now)
    {
        if (format == BackupFormatSummary.BackupFormatState.Disabled)
        {
            return new NextBackupLines(
                BackupFormatSummary.Headline(BackupFormatSummary.BackupFormatState.Disabled),
                "No backup type is switched on, so a scheduled or triggered run would " +
                "write nothing. Turn one on under Settings.",
                IsFault: true);
        }

        // ONE switch producing both lines, not one per paragraph. The value and
        // the sentence qualifying it are two halves of one answer, and two
        // switches over one enum is how this panel has contradicted itself
        // before (`T126`).
        //
        // The skipped-run sentence is here rather than left to be discovered:
        // the moment above is when the question is ASKED, and a card promising
        // an archive would be overstating what the scheduler does.
        return due.State switch
        {
            NextBackupState.At => new NextBackupLines(
                $"In {UptimeFormatter.Format(due.At!.Value - now)}",
                "The next scheduled backup is at " +
                ClockText.TimeOnDay(due.At!.Value.ToLocalTime()) +
                ". A scheduled run is skipped if nobody has played since the last one.",
                IsFault: false),

            NextBackupState.DueNow => new NextBackupLines(
                "Due now",
                "A scheduled backup will start within a minute. A scheduled run is skipped " +
                "if nobody has played since the last one.",
                IsFault: false),

            NextBackupState.EventsOnly => new NextBackupLines(
                "Nothing is on a timer",
                "This server is still backed up on the events ticked under Settings.",
                IsFault: false),

            NextBackupState.Nothing => new NextBackupLines(
                "None",
                "There are no scheduled or event triggered backups setup.",
                IsFault: true),

            _ => throw new NotSupportedException(
                $"No lines for {due.State}. Add an arm here rather than letting the card " +
                "render a state nobody wrote words for."),
        };
    }
}
