using BedrockSentry.Core.Diagnostics;

namespace BedrockSentry.Core.Instances;

/// <summary>
/// What should happen to an instance's files and history when it is removed.
/// </summary>
/// <remarks>
/// Three independent questions, because the answers genuinely are independent.
/// Somebody rebuilding a server wants the config entry gone and the world kept;
/// somebody who mis-typed a name during setup wants all of it gone; somebody
/// winding a server down for good may want the world archived but the players
/// forgotten. Collapsing that into one "delete everything?" checkbox would force
/// two of those three to do the rest by hand.
/// </remarks>
/// <param name="DeleteFiles">
/// Delete the instance's working directory — the server binaries, the world, the
/// allowlist and operator lists — and its console transcripts. Off means the
/// files are left exactly where they are and only the wrapper forgets about them.
/// </param>
/// <param name="ForgetPlayers">
/// Erase this instance's player history rather than keeping it. Profiles are
/// global (one per XUID across the whole install), so this removes the sessions
/// recorded on <em>this</em> instance; a player who has also played elsewhere
/// keeps their profile and the rest of their history.
/// </param>
/// <param name="DeleteBackups">
/// Delete this instance's backup folder — every ZIP archive, every snapshot
/// folder and its backup history. Off by default and independent of
/// <paramref name="DeleteFiles"/>: the archives are the one thing somebody
/// reaches for after deciding the removal was a mistake, including the safety
/// backup taken before the last restore, so deleting them is the single step
/// here with no way back. Left off, the folder outlives the instance and
/// nothing will ever list or prune it (<c>D39</c>).
/// </param>
/// <param name="RemoveFirewallRules">
/// Remove the inbound firewall rules the panel's Apply fix added for this server
/// (<c>T400</c>). A rule outliving its server holds a port open for whatever binds
/// it next.
/// </param>
public sealed record RemoveInstanceRequest(
    bool DeleteFiles,
    bool ForgetPlayers,
    bool DeleteBackups = false,
    bool RemoveFirewallRules = false);

/// <summary>
/// What happened to one directory a removal was asked to delete.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three states, not two, and the third is the whole reason this type
/// exists</strong> (<c>T176</c>). This was a <c>bool</c> plus a
/// <c>KeptBecause</c> string, which can say <em>gone</em> and <em>untouched</em>
/// and nothing else — so a delete that destroyed half a folder and then failed
/// was reported as a folder that was kept. On the working directory that is world
/// data lost and the panel saying nothing was lost, against <c>D39</c>'s own
/// description of this as the one step with no way back.
/// </para>
/// <para>
/// <strong>Why <see cref="PartlyDeleted"/> claims so little.</strong>
/// <c>Directory.Delete(recursive: true)</c> reports nothing about how far it got,
/// so <em>any</em> exception from it means an unknown amount is gone. Counting the
/// tree first would make the claim precise and would double the walk on a world
/// folder for a message that ends in "go and look" either way. The honest answer
/// is the cheap one.
/// </para>
/// <para>
/// <strong>What it does <em>not</em> do is stop at the first failure</strong>
/// (<c>T186</c>). This comment used to say it "throws on the first thing it cannot
/// remove, having already removed everything it passed", and the panel told the
/// operator the survivors were "whatever the delete had not reached". Both were
/// wrong. .NET's recursive delete records the first exception, <strong>keeps
/// walking and keeps deleting</strong>, and rethrows at the end — so what is left
/// is what it <em>could not</em> remove, which is a shell, not an arbitrary
/// remainder. Disproved twice, on two independent folders, by the cheapest check
/// available: whether an entry sorting <em>after</em> the locked one had survived.
/// It had not, on either.
/// </para>
/// <para>
/// That makes the survivors enumerable after the throw, so "unknown amount" is now
/// a deliberate choice rather than a limit — kept because the message ends in "go
/// and look" either way, and because a second walk over a world folder to itemise
/// what a person is about to open in Explorer buys nothing. Stated here so the next
/// author knows it is a choice: the old wording made it look forced.
/// </para>
/// </remarks>
public enum DirectoryRemoval
{
    /// <summary>Nothing was asked for, or there was nothing there to delete.</summary>
    NotAsked,

    /// <summary>Gone.</summary>
    Deleted,

    /// <summary>
    /// Nothing was touched, because the path was refused before anything ran.
    /// </summary>
    Refused,

    /// <summary>
    /// It began deleting, failed, and an unknown amount is already gone.
    /// </summary>
    PartlyDeleted,
}

/// <summary>
/// What actually happened. Reported rather than assumed, because "delete the
/// files" can be legitimately refused and an administrator who is told "removed"
/// will not go looking for the folder that is still there — or for the half of one
/// that is no longer there (<c>T176</c>).
/// </summary>
/// <remarks>
/// <para><strong>Both failing directories carry their own path, and
/// <see cref="TranscriptDirectory"/> is why (<c>T192</c>).</strong> This record used
/// to hold exactly one path — <see cref="WorkingDirectory"/> — while reporting
/// failures for three separate folders. So when the transcripts were the thing that
/// half-deleted, the notice named the working directory, which had just been deleted
/// successfully, and sent the reader to the wrong folder.</para>
///
/// <para>That is the <c>T176</c> lesson at one remove: a type with too few fields
/// will be made to lie, exactly as a type with too few states was. The value is
/// nullable because the transcripts are only asked about when the files are, and a
/// path for a folder nobody touched is noise.</para>
///
/// <para><strong>This was lost in the fix for <c>T190</c>, not missing from the
/// start.</strong> Before it, the failure arrived as a hand-authored fragment that
/// spliced the path in mid-sentence; the fix replaced that fragment with the raw
/// exception message and the path went with it. <c>T190</c>'s own severity bound had
/// leaned on that path being printed — it is what made the defect a wording problem
/// rather than a lost-evidence one — so the fix removed the property its entry used
/// to excuse it. Read a fix for what it deletes, not only for what it adds.</para>
/// </remarks>
public sealed record InstanceRemovalOutcome(
    string InstanceId,
    string Name,
    string WorkingDirectory,
    DirectoryRemoval Files,
    string? FilesFailedBecause,
    int PlayerRecordsForgotten,
    DirectoryRemoval Backups = DirectoryRemoval.NotAsked,
    string? BackupsFailedBecause = null,
    DirectoryRemoval Transcripts = DirectoryRemoval.NotAsked,
    string? TranscriptsFailedBecause = null,
    string? TranscriptDirectory = null,
    SystemCheckFix? FirewallRules = null)
{
    /// <summary>
    /// True when something the operator asked to delete is partly gone
    /// (<c>T186</c>).
    /// </summary>
    /// <remarks>
    /// <para>The panel styles the whole notice from this. It used to render every
    /// outcome in the neutral accent palette with <c>role="status"</c>, on a comment
    /// that said a completed removal is not a failure — true when that panel could
    /// only report success, and written before <c>T176</c> added an arm that reports
    /// irreversible loss. So the one sentence in this product that says world data is
    /// gone arrived in muted grey, politely.</para>
    ///
    /// <para><strong>Only <see cref="DirectoryRemoval.PartlyDeleted"/> counts.</strong>
    /// <c>Refused</c> is the arm that can honestly say <em>kept</em> — it is reached
    /// only from the guard that runs before anything is deleted — so styling it as
    /// loss would be <c>T176</c> in the mirror, alarming somebody about a folder that
    /// is intact. <c>NotAsked</c> and <c>Deleted</c> are the ordinary cases.</para>
    ///
    /// <para><strong><see cref="Transcripts"/> is deliberately not counted</strong>
    /// (<c>T190</c>). Console transcripts are log files; a half-deleted one is untidy,
    /// not irreversible, and the whole meaning of this flag is "world data or archives
    /// are gone". It used to count, by accident: a partly-deleted transcript tree was
    /// promoted into <see cref="Files"/>, which turned the danger palette on for a
    /// removal where the world went perfectly.</para>
    ///
    /// <para>Here rather than in the <c>.razor</c> for the standing reason: the suite
    /// cannot reference <c>Host</c>, and a rule about when to shout is exactly the
    /// kind that gets quietly inverted.</para>
    /// </remarks>
    public bool AnythingLost =>
        Files is DirectoryRemoval.PartlyDeleted || Backups is DirectoryRemoval.PartlyDeleted;
}

/// <summary>
/// How a <see cref="DirectoryRemoval"/> reads in the removal log line.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than inline in the log call because the template used to take
/// <em>either</em> a verb (<c>deleted</c>) <em>or</em> a raw exception sentence in
/// the same slot, so a failure arrived mid-clause with no word marking it as one
/// and the line scanned as a list of successes (<c>T179</c>). Somebody searching
/// the log for a removal that went wrong would not have matched it.
/// </para>
/// <para>
/// Every arm is a self-contained phrase, so the line reads as one sentence in all
/// sixteen combinations of the two slots, and <c>RemovalPhraseTests</c> pins each.
/// </para>
/// </remarks>
public static class DirectoryRemovalPhrase
{
    /// <summary>The phrase for one slot of the removal log line.</summary>
    public static string For(DirectoryRemoval outcome, string? failure) => outcome switch
    {
        DirectoryRemoval.Deleted => "deleted",
        DirectoryRemoval.NotAsked => "not deleted",
        DirectoryRemoval.Refused => $"kept, because {Describe(failure)}",

        // The word order is deliberate: the alarming part comes first, so a line
        // truncated by a log viewer still carries it.
        DirectoryRemoval.PartlyDeleted =>
            $"PARTLY DELETED and then failed, so an unknown amount is gone - {Describe(failure)}",

        _ => "not deleted",
    };

    /// <summary>
    /// One reason, ended so the sentence after it does not run into it
    /// (<c>T187</c>).
    /// </summary>
    /// <remarks>
    /// <para>The panel puts a further sentence after the reason — <em>"Delete
    /// &lt;path&gt; by hand if you meant to."</em> — and the <c>Refused</c> reasons
    /// come from <c>InstanceDirectoryRemoval</c>, where <strong>not one of them ends
    /// in a full stop</strong>. So that arm always read as a run-on, and the backups
    /// arm ended with no terminator at all. The <c>PartlyDeleted</c> arms looked
    /// right only by luck, because exception messages usually do end in a period —
    /// which is why <c>T176</c>'s observation of the partial sentence looked
    /// clean.</para>
    ///
    /// <para>Here rather than in the <c>.razor</c> for the usual reason: the suite
    /// cannot reference <c>Host</c>, and this is a rule about punctuation that four
    /// render arms share. <see cref="For"/> keeps trimming instead, because a log
    /// slot is joined with a semicolon and a full stop mid-line reads as the end of
    /// the entry.</para>
    /// </remarks>
    public static string Sentence(string? failure)
    {
        var reason = Describe(failure);

        return reason.Length > 0 && reason[^1] is '.' or '!' or '?'
            ? reason
            : reason + '.';
    }

    /// <summary>
    /// The same reason, capitalised, for the arms where it <em>begins</em> a sentence
    /// rather than following a colon (<c>T190</c>).
    /// </summary>
    /// <remarks>
    /// <para><strong>Two render sites with opposite requirements, and until now nothing
    /// told them apart.</strong> The panel uses a reason in two positions: after a colon
    /// — <em>"kept, untouched: the file is in use."</em> — where lowercase is correct;
    /// and immediately after a bolded lead-in, where it opens a sentence and lowercase
    /// is wrong. <see cref="Sentence"/> preserves the initial casing, and
    /// <c>RemovalPhraseTests</c> <em>pins</em> that it does, every pinned case being a
    /// <c>Refused</c> reason — which is exactly the post-colon position. So the tested
    /// behaviour was right for one site and silently wrong for the other.</para>
    ///
    /// <para>It shipped visibly once: a hand-authored fragment beginning <em>"the
    /// console transcripts at ..."</em> was spliced into the sentence-initial slot and
    /// rendered with a lowercase <em>t</em>. Exception messages usually start with a
    /// capital, which is why the other arms looked correct — <c>T187</c>'s "right by
    /// luck" again, one level down and in the file that warns about it.</para>
    ///
    /// <para>Only the first character changes. The rest is left alone on purpose: these
    /// reasons contain paths, and upper-casing anything beyond the first letter would
    /// corrupt one.</para>
    /// </remarks>
    public static string OpeningSentence(string? failure)
    {
        var sentence = Sentence(failure);

        return sentence.Length > 0 && char.IsLower(sentence[0])
            ? char.ToUpperInvariant(sentence[0]) + sentence[1..]
            : sentence;
    }

    private static string Describe(string? failure) =>
        string.IsNullOrWhiteSpace(failure) ? "no reason was recorded" : failure.TrimEnd('.', ' ');
}

/// <summary>
/// An instance the wrapper has been told to stop managing but has not forgotten.
/// </summary>
/// <remarks>
/// It has no supervisor, so it appears in none of the live views — which is
/// correct (there is no server to manage) and also how a retired instance
/// becomes invisible. Something has to list them, or "retire" is a one-way door
/// wearing a reversible label.
/// </remarks>
public sealed record RetiredInstance(string InstanceId, string Name, string WorkingDirectory);

/// <summary>
/// The player history recorded against an instance.
/// </summary>
/// <remarks>
/// A seam, not an abstraction for its own sake: the SQLite player store arrives
/// at milestone 5, and removing an instance has to be able to ask about its
/// history before then. <see cref="NoPlayerDataStore"/> is the honest answer
/// until it does — nothing is recorded, so nothing can be erased, and the
/// removal reports exactly that instead of implying it deleted something.
/// </remarks>
public interface IInstancePlayerData
{
    /// <summary>How many stored records mention this instance.</summary>
    Task<int> CountAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>Erases them. Returns how many were removed.</summary>
    Task<int> ForgetAsync(string instanceId, CancellationToken cancellationToken = default);
}

/// <summary>There is no player database yet. Nothing stored, nothing to erase.</summary>
public sealed class NoPlayerDataStore : IInstancePlayerData
{
    public Task<int> CountAsync(string instanceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public Task<int> ForgetAsync(string instanceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
