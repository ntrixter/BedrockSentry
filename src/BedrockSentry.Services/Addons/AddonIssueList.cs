using BedrockSentry.Core.Addons;

namespace BedrockSentry.Services.Addons;

/// <summary>One card on the Issues view.</summary>
/// <param name="Key">How it is recognised on the next read, for hiding (<c>D96</c>).</param>
/// <param name="Subject">
/// The add-on it is about, as its row names it (<c>T299</c>), or "This server" for
/// the note about script output.
/// </param>
/// <param name="SubjectUuid">The add-on's uuid, or null for the server-wide note.</param>
/// <param name="Row">The add-on's row, or null when it has none or is the server.</param>
/// <param name="OffersScriptOutput">Whether the card carries the button that turns script output on.</param>
/// <param name="PointsToOrder">Whether the card is about the order, so it links there.</param>
/// <param name="OffersRemoveEntry">
/// Whether the card carries the button that takes the entry out of the world's list: a
/// pack the world asks for and this server does not have (<c>T416</c>).
/// </param>
public sealed record AddonIssue(
    string Key,
    AddonIssueTone Tone,
    string Subject,
    string? SubjectUuid,
    AddonRow? Row,
    string Headline,
    string? Detail,
    bool Hidden,
    bool OffersScriptOutput,
    bool PointsToOrder,
    bool OffersRemoveEntry = false)
{
    /// <summary>The label's word.</summary>
    public string Label => AddonIssues.Label(Tone);

    /// <summary>The label's badge and the card's edge.</summary>
    public string ToneClass => Tone switch
    {
        AddonIssueTone.Problem => "addon-issue-problem",
        AddonIssueTone.Warning => "addon-issue-warning",
        AddonIssueTone.NotChecked => "addon-issue-unknown",
        AddonIssueTone.Info => "addon-issue-info",

        _ => throw new InvalidOperationException($"No style is chosen for {Tone}."),
    };
}

/// <summary>Every issue the tab shows, and which are hidden.</summary>
public sealed class AddonIssueList
{
    /// <summary>What the server-wide note calls its subject.</summary>
    public const string ServerSubject = "This server";

    private AddonIssueList(IReadOnlyList<AddonIssue> all) => All = all;

    /// <summary>No issues, before anything has been read.</summary>
    public static AddonIssueList Empty { get; } = new([]);

    /// <summary>Every issue, hidden ones included, worst first.</summary>
    public IReadOnlyList<AddonIssue> All { get; }

    /// <summary>The issues nobody has hidden.</summary>
    public IReadOnlyList<AddonIssue> Open => [.. All.Where(i => !i.Hidden)];

    /// <summary>How many are hidden.</summary>
    public int HiddenCount => All.Count(i => i.Hidden);

    /// <summary>Every key, which is what a hide or unhide trims the stored set to.</summary>
    public IEnumerable<string> Keys => All.Select(i => i.Key);

    /// <summary>The line above the cards.</summary>
    public string Summary => AddonIssues.Summary(Open.Count, HiddenCount);

    /// <summary>How many open issues are about one add-on, for its row.</summary>
    public int OpenFor(string uuid) =>
        All.Count(i => !i.Hidden && string.Equals(i.SubjectUuid, uuid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Build the list from the findings, the listing and the script output state.</summary>
    /// <param name="hidden">The keys this instance has hidden.</param>
    public static AddonIssueList Build(
        IReadOnlyList<AddonFinding> findings,
        AddonsListing listing,
        ScriptOutputState scriptOutput,
        IReadOnlyCollection<string> hidden)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(hidden);

        var isHidden = hidden.ToHashSet(StringComparer.Ordinal);
        var issues = new List<AddonIssue>();

        foreach (var finding in findings)
        {
            var key = AddonIssues.KeyOf(finding);

            issues.Add(new AddonIssue(
                key,
                AddonIssues.ToneOf(finding.Severity),
                listing.SubjectOf(finding.Subject),
                finding.Subject,
                listing.Find(finding.Subject),
                AddonText.Headline(finding.Kind),
                finding.Detail,
                isHidden.Contains(key),
                OffersScriptOutput: false,
                PointsToOrder: finding.Kind is AddonFindingKind.RegisteredTwice
                    or AddonFindingKind.ShadowedByEarlierPack,
                OffersRemoveEntry: finding.Kind is AddonFindingKind.RegisteredPackNotInstalled));
        }

        // D91. Its own card rather than a finding: the pack may be working, and
        // this is about whether anybody can see it.
        if (Headline(scriptOutput) is { } headline)
        {
            var key = AddonIssues.ScriptOutputKey(scriptOutput.ToString());

            issues.Add(new AddonIssue(
                key,
                scriptOutput == ScriptOutputState.Unreadable ? AddonIssueTone.NotChecked : AddonIssueTone.Info,
                ServerSubject,
                null,
                null,
                headline,
                ScriptOutput.Describe(scriptOutput),
                isHidden.Contains(key),
                OffersScriptOutput: ScriptOutput.CanTurnOn(scriptOutput),
                PointsToOrder: false));
        }

        return new AddonIssueList(issues);
    }

    /// <summary>The script output card's headline, or null when there is no card.</summary>
    private static string? Headline(ScriptOutputState state) => state switch
    {
        ScriptOutputState.NotApplicable or ScriptOutputState.Visible => null,
        ScriptOutputState.Hidden or ScriptOutputState.NotSet or ScriptOutputState.LevelHidden =>
            "Script output does not reach the console",
        ScriptOutputState.FileMissing => "Script output cannot be set here",
        ScriptOutputState.Unreadable => "Whether script output reaches the console is not known",

        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No card is written for this state."),
    };
}
