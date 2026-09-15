namespace BedrockSentry.Core.Instances;

/// <summary>
/// Carries the report of a removal across the navigation that leaves the removed
/// instance's page.
/// </summary>
/// <remarks>
/// <para>
/// Removing an instance used to leave the administrator on that instance's own
/// page, which no longer describes anything — the supervisor is gone from the
/// registry but the page still holds the object it resolved, so the tab strip and
/// every tab carry on rendering a server that does not exist (<c>T169</c>). The
/// fix is to go to the Status page, and this exists because going there would
/// otherwise throw away the one report of what actually happened.
/// </para>
/// <para>
/// <b>Why that report matters enough to carry.</b>
/// <see cref="InstanceRemovalOutcome"/> says it in its own summary: deleting the
/// files can be legitimately refused, and somebody told only "removed" will not
/// go looking for the folder that is still there. Leaving <em>delete the files</em>
/// unticked is an ordinary choice, and then this is the only thing that names the
/// directory left on disk.
/// </para>
/// <para>
/// <b>Lives in <c>Core</c>, though only the web UI uses it</b>, because
/// <see cref="Take"/>'s once-and-then-nothing behaviour is exactly the kind of rule
/// that gets broken later — a holder that never clears shows the notice on every
/// later visit to the Status page, and one that clears too eagerly shows it never.
/// A rule left in the Host project is one the test suite cannot reach, which is
/// this codebase's recurring defect. It takes no dependencies, so <c>Core</c>
/// still depends on nothing.
/// </para>
/// <para>
/// <b>Named for what it holds, not for what is drawn.</b> The visible panel is
/// <c>RemovalNotice.razor</c> in the Host project. Both were called
/// <c>RemovalNotice</c> for about a minute and the compiler refused the ambiguity,
/// which was the right answer: one name for the thing that carries a report and the
/// thing that renders it is the same trouble as one word for two facts.
/// </para>
/// <para>
/// <b>Registered scoped</b>, which for Blazor Server means one per circuit: a
/// singleton would show one administrator's removal to another, and a transient
/// would hand the page a different instance from the one that was posted to.
/// </para>
/// </remarks>
public sealed class PendingRemoval
{
    private InstanceRemovalOutcome? _pending;

    /// <summary>Holds a report for whichever page reads it next.</summary>
    /// <remarks>
    /// A second post replaces an unread first rather than queueing. Two removals
    /// with no page render between them cannot happen from the panel — the first
    /// one navigates — and if it ever did, the newer report is the one worth
    /// showing.
    /// </remarks>
    public void Post(InstanceRemovalOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        _pending = outcome;
    }

    /// <summary>
    /// The report, once. Null afterwards, and null when nothing was posted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clearing on read is what stops the notice reappearing every time the
    /// administrator returns to the Status page. It also means the caller has to
    /// keep what it gets rather than calling this again on a later render.
    /// </para>
    /// <para>
    /// <strong>The interactive guard at the call site is kept, and this paragraph
    /// used to justify it wrongly</strong> (`T169`). It said that on a prerendering
    /// page this must not be called until the circuit is live "or the prerender eats
    /// the notice". It cannot: this is registered <em>scoped</em>, the prerender pass
    /// runs in the HTTP request scope, and that is a different container from the
    /// circuit — so a prerender read would empty a different instance of this class
    /// and the circuit's copy would be untouched. The guard is sufficient; its
    /// necessity is unproven, and it stays for the two reasons that survive: reading
    /// once on the interactive pass is what the caller means, and the guard is what
    /// keeps that true if this is ever registered as a singleton. A comment that
    /// argues from the wrong mechanism is how the next change inherits the error.
    /// </para>
    /// </remarks>
    public InstanceRemovalOutcome? Take()
    {
        var pending = _pending;
        _pending = null;
        return pending;
    }
}
