namespace BedrockSentry.Core.Diagnostics;

/// <summary>What Windows having work queued actually means for the servers.</summary>
public enum RebootUrgency
{
    /// <summary>Nothing is pending.</summary>
    None,

    /// <summary>
    /// Windows has work it will do <em>if</em> the machine restarts, and nothing
    /// that will cause one.
    /// </summary>
    Queued,

    /// <summary>Something pending can restart the machine without being asked.</summary>
    Restarting,
}

/// <summary>
/// One thing Windows has pending, and whether it can cause a restart by itself.
/// </summary>
/// <param name="CanCauseARestart">
/// True only for the signals that actually lead to an unattended restart. A queue
/// Windows drains <em>if</em> a restart happens is not one of them.
/// </param>
public sealed record RebootSignal(string Reason, bool CanCauseARestart);

/// <summary>
/// How the Status page's reboot warning reads (<c>T188</c>).
/// </summary>
/// <remarks>
/// <para><strong>The defect this exists for.</strong> Three independent registry
/// signals were ORed into one <c>bool</c> and given one headline — <em>"Windows is
/// waiting to restart. It will take every server down at a moment of its
/// choosing."</em> Two of them can do that. The third,
/// <c>PendingFileRenameOperations</c>, is a <strong>passive queue</strong> Windows
/// drains if and when a restart happens; it cannot cause one. So the mildest and by
/// far the commonest signal inherited the consequence of the most severe, and
/// inherited a claim that is, for it, untrue.</para>
///
/// <para><strong>The cost is the usual one for a warning that is usually wrong.</strong>
/// The banner stood continuously for at least two days on a machine with
/// nothing whatever pending — fired by OneDrive queuing its own previous version for
/// deletion at the next restart. That queue is non-empty on a large share of
/// perfectly healthy Windows installs, because anything that updates itself while
/// its files are in use leaves entries there. A machine about to be taken down
/// therefore rendered identically to a machine that is completely fine.</para>
///
/// <para><strong>It is the T177 shape, and that is why it is worth a type rather
/// than an <c>if</c>.</strong> The method's own doc comment stated the
/// justification — "Windows will eventually take every server down without asking,
/// and the administrator would rather choose the moment" — which is true of two
/// signals and false of the third, and the third was added under it. The code was
/// written from the justification rather than from the branch, exactly as
/// <c>T177</c>'s false security reasoning and <c>T175</c>'s inverted comment were.
/// A <c>bool</c> could not have said the difference, so the fix is the
/// <c>T176</c> one: count the states the world can be in before choosing the
/// type.</para>
/// </remarks>
public static class RebootNotice
{
    /// <summary>The strongest thing any of these signals means.</summary>
    public static RebootUrgency UrgencyOf(IReadOnlyList<RebootSignal> signals)
    {
        if (signals is null || signals.Count == 0)
        {
            return RebootUrgency.None;
        }

        foreach (var signal in signals)
        {
            if (signal.CanCauseARestart)
            {
                return RebootUrgency.Restarting;
            }
        }

        return RebootUrgency.Queued;
    }

    /// <summary>The headline, or null when there is nothing to say.</summary>
    public static string? Headline(RebootUrgency urgency) => urgency switch
    {
        RebootUrgency.Restarting => "Windows is waiting to restart.",
        RebootUrgency.Queued => "Windows has files queued for the next restart.",
        _ => null,
    };

    /// <summary>
    /// The sentence after the headline, or null. Only the restarting case makes a
    /// claim about the servers.
    /// </summary>
    public static string? Consequence(RebootUrgency urgency) => urgency switch
    {
        RebootUrgency.Restarting =>
            "It will take every server down at a moment of its choosing —",

        // Deliberately no claim at all. This state is honest information and not a
        // warning, and saying anything about servers here is what T188 was.
        RebootUrgency.Queued => "Nothing here will restart the machine on its own —",

        _ => null,
    };
}
