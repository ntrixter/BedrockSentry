namespace BedrockSentry.Core.Configuration;

/// <summary>
/// Switches that make the wrapper easier to drive from a test harness, bound
/// from <c>Sentry:Testing</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here weakens something on purpose, so everything here is off by
/// default and says so when it is on.</b> A section of switches that quietly do
/// nothing until somebody sets them is fine; a section that quietly does
/// something is not, and the difference is whether the running wrapper tells
/// anybody. That is the licence-manifest lesson in another form — a rule nothing
/// enforces is already broken, and a switch nothing announces is one somebody
/// leaves on.
/// </para>
/// <para>
/// <b>Hand-edited only.</b> Deliberately absent from the Settings page: a panel
/// that can switch off its own confirmation is not a confirmation. It is also
/// absent from the settings export, and gets that for free — <c>ConfigArchive</c>
/// carries an <em>allow</em>-list of sections, so a new one is excluded until
/// somebody decides otherwise, which is the whole reason it is shaped that way.
/// </para>
/// </remarks>
public sealed class TestingOptions
{
    public const string SectionName = "Sentry:Testing";

    /// <summary>
    /// Drop the password from Remove, Retire and Restore, leaving the
    /// confirmation itself in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Those three dialogs each require the signed-in administrator's password
    /// before they will act, which is right for a panel that binds every
    /// interface and wrong for an unattended test run — it has blocked scheduled
    /// runs repeatedly, most recently the restore clause of <c>T151</c>.
    /// </para>
    /// <para>
    /// <b>What it does not do.</b> The confirmation stays: the dialog still
    /// opens, still names what it is about to destroy, and still has to be
    /// accepted. Only the password comes off. It does not touch the player-record
    /// erase on the Players page, which is destruction of data about a person
    /// rather than about a server — a deliberate exclusion rather than an
    /// oversight, and one to revisit only if somebody asks.
    /// </para>
    /// </remarks>
    public bool SkipDestructiveConfirmPassword { get; set; }
}
