namespace BedrockSentry.Core.Addons;

/// <summary>
/// The packs an instance has that nobody here put there and nothing here uses.
/// </summary>
/// <remarks>
/// <para><strong>A Bedrock package ships dozens of its own packs</strong> — 71
/// <c>behavior_packs</c> folders over 16 uuids on a stock root, plus 65 resource
/// packs — so a fresh instance listed 27 rows and raised an
/// <see cref="AddonFindingKind.InstalledButNotRegistered"/> warning for every one of
/// them before an administrator had added anything. That is an inventory rather than
/// a set of findings: every one of them is off on purpose and none is actionable
/// (<c>D90</c>).</para>
///
/// <para><strong>The rule is four conditions, not "is it Mojang's".</strong>
/// Nothing on disk says who wrote a pack, and a rule that guessed would be wrong
/// about somebody's own work. A pack is hidden only when all four hold: every
/// folder of it is in the <em>server root</em>, the shared library has never
/// seen its uuid, the world does not register it, and no switched-on pack
/// depends on it. Any one of those failing shows the row.</para>
///
/// <para><strong>The fourth arrived with <c>T284</c>.</strong> A switched-on pack
/// depended on one of Bedrock's own, which the first three conditions hid, so the
/// dependency finding pointed at a row that was not there. A pack something uses
/// is used, whoever shipped it.</para>
///
/// <para><strong>Each condition is doing work, and the second is the one that is
/// easy to leave out.</strong> This wrapper wrote into the server root until
/// <c>T271</c>, so an instance configured by an earlier build has an
/// administrator's own packs sitting exactly where Bedrock's do — and the
/// library is what tells them apart. Without it, hiding root packs would hide
/// the packs somebody most wants to see.</para>
///
/// <para><strong>It answers `D90`'s own objection rather than overruling it.</strong>
/// That entry recommended a collapsed section over hiding, because hiding "makes
/// the tab disagree with the world's own pack list" — true of a rule that hid
/// registered packs, and this one cannot: a uuid in either world list is shown
/// whatever else is true of it.</para>
///
/// <para><strong>And it is never silent.</strong> <see cref="Sentence"/> states
/// the rule on the page, so an administrator who put a pack in the server root
/// by hand is told why it is not listed rather than left to wonder. An absence
/// nobody mentions is the shape this codebase keeps recording.</para>
/// </remarks>
public static class UntouchedServerPacks
{
    /// <summary>
    /// Whether the tab hides this uuid.
    /// </summary>
    /// <param name="homes">
    /// Every location this uuid has a folder in. Empty means it has none here —
    /// a library-only row — which is never hidden, because the row is the only
    /// way to switch it on.
    /// </param>
    /// <param name="inLibrary">Whether the shared library holds this uuid.</param>
    /// <param name="registered">
    /// Whether either of the world's pack lists names it, in any version and in
    /// either list. Registered in the <em>wrong</em> list still counts: that is a
    /// finding an administrator has to be able to see.
    /// </param>
    /// <param name="needed">
    /// Whether a switched-on pack depends on it, from <see cref="NeededBy"/>.
    /// </param>
    public static bool Hides(
        IReadOnlyCollection<PackHome> homes, bool inLibrary, bool registered, bool needed)
    {
        ArgumentNullException.ThrowIfNull(homes);

        if (inLibrary || registered || needed || homes.Count == 0)
        {
            return false;
        }

        // EVERY folder, not any. A uuid with a copy in the world is one somebody
        // put there, whatever else it has in the root.
        foreach (var home in homes)
        {
            if (home != PackHome.ServerRoot)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The uuids a switched-on pack depends on.</summary>
    /// <param name="installed">Every pack folder on the instance.</param>
    /// <param name="switchedOn">Whether the world registers a uuid, in either list.</param>
    /// <remarks>
    /// Every copy of a switched-on uuid counts, not only the version the world
    /// names: the cost of an older copy's dependency is a row shown that could have
    /// been hidden, and the cost of missing the right one is <c>T284</c> again.
    /// </remarks>
    public static IReadOnlySet<string> NeededBy(
        IEnumerable<InstalledPack> installed, Func<string, bool> switchedOn)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(switchedOn);

        return installed
            .Where(p => switchedOn(p.Manifest.Uuid))
            .SelectMany(p => p.Manifest.PackDependencies)
            .Select(d => d.Target)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What the tab says about its list: an empty state, or a note under the table.</summary>
    /// <param name="shown">How many rows the table has.</param>
    /// <param name="hidden">How many uuids it left out.</param>
    /// <remarks>
    /// <strong>One statement when there are no rows</strong> (<c>T283</c>). The
    /// empty state said <em>"No add-ons here yet"</em> directly above
    /// <em>"27 add-ons are not listed"</em>: each true of something different, and
    /// contradictory on one screen. With no rows the empty state carries the count
    /// itself and there is no separate note; with rows there is no empty state.
    /// </remarks>
    public static ListingText Describe(int shown, int hidden) => shown > 0
        ? new(null, Sentence(hidden))
        : new(hidden switch
        {
            <= 0 => "No add-ons here yet.",

            1 => "No add-ons have been added here. The one in the server's own folder is not "
                + "listed: it is not in the library and not switched on, and a Bedrock server "
                + "ships dozens of these.",

            _ => $"No add-ons have been added here. The {hidden} in the server's own folder are "
                + "not listed: they are not in the library and not switched on, and a Bedrock "
                + "server ships dozens of these.",
        }, null);

    /// <summary>What the page says instead of the rows it left out.</summary>
    /// <param name="count">How many uuids were hidden. Zero returns null.</param>
    /// <remarks>
    /// States the rule rather than naming Mojang, because the rule is what a
    /// reader can check against their own folder — and because nothing here
    /// knows who wrote a pack.
    /// </remarks>
    public static string? Sentence(int count) => count switch
    {
        <= 0 => null,

        1 => "One add-on is not listed: it is in the server's own folder, not in the "
            + "library, and not switched on. A Bedrock server ships dozens of these.",

        _ => $"{count} add-ons are not listed: they are in the server's own folder, not in "
            + "the library, and not switched on. A Bedrock server ships dozens of these.",
    };

    /// <summary>What the page says while the rows it left out are shown under it.</summary>
    /// <param name="count">How many uuids were hidden. Zero returns null.</param>
    /// <remarks>
    /// <see cref="Sentence"/> says they are not listed, which stops being true the
    /// moment somebody asks to see them, so the shown list gets its own sentence
    /// stating the same rule.
    /// </remarks>
    public static string? ShownSentence(int count) => count switch
    {
        <= 0 => null,

        1 => "The add-on below is in the server's own folder, not in the library, and not "
            + "switched on. A Bedrock server ships dozens of these.",

        _ => $"These {count} add-ons are in the server's own folder, not in the library, and not "
            + "switched on. A Bedrock server ships dozens of these.",
    };
}

/// <summary>What the Add-ons tab says about its list.</summary>
/// <param name="EmptyState">The sentence in place of an empty table, or null when there are rows.</param>
/// <param name="HiddenNote">The note under the table about what it left out, or null.</param>
public readonly record struct ListingText(string? EmptyState, string? HiddenNote);
