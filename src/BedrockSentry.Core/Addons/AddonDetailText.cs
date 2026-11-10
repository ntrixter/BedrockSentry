namespace BedrockSentry.Core.Addons;

/// <summary>Whether a pack needs a world experiment switched on, as far as its manifest says.</summary>
/// <remarks>
/// <para><strong>Only Beta APIs can be read off a manifest.</strong> A beta script
/// module needs it, and the module's version says so (<see cref="ScriptModuleRelease"/>).
/// A pack whose blocks or items need any other experiment declares that nowhere,
/// so <see cref="None"/> means "none its manifest shows", which the tab says
/// beside the field rather than here alone.</para>
///
/// <para>Whether the world has Beta APIs on is a different question, answered
/// by <c>level.dat</c>, which this does not read.</para>
/// </remarks>
public enum ExperimentalNeed
{
    /// <summary>No script module, or stable modules only.</summary>
    None,

    /// <summary>A beta script module: the world needs Beta APIs on.</summary>
    BetaApis,

    /// <summary>A script module whose version this cannot place.</summary>
    CannotTell,
}

/// <summary>
/// What the details under an add-on's row say: where it is, what it runs, what
/// it needs, and who wrote it.
/// </summary>
/// <remarks>
/// In <c>Core</c> for the reason <see cref="AddonText"/> is: a sentence decided in
/// a <c>.razor</c> file is checked on a real page or not at all.
/// </remarks>
public static class AddonDetailText
{
    /// <summary>What a field says when the manifest has nothing for it.</summary>
    public const string NotGiven = "Not given";

    /// <summary>Where a pack the library holds and this server does not is.</summary>
    public const string InTheLibrary = "The add-on library";

    /// <summary>Said beside the Experimental field, because its "No" has a limit.</summary>
    public const string ExperimentalNote =
        "Read from the script modules in its manifest: a beta module needs the world's Beta APIs "
        + "experiment. No manifest says whether a pack needs any other experiment.";

    /// <summary>Whether this pack needs Beta APIs, from its script modules.</summary>
    /// <remarks>
    /// A pack with no script module needs none: <see cref="PackManifest.ScriptsRelease"/>
    /// answers <see cref="ScriptModuleRelease.Unknown"/> for it because it names no
    /// module, which is a different thing from naming one this cannot place.
    /// </remarks>
    public static ExperimentalNeed Experimental(PackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!manifest.HasScriptModule)
        {
            return ExperimentalNeed.None;
        }

        return manifest.ScriptsRelease switch
        {
            ScriptModuleRelease.Stable => ExperimentalNeed.None,
            ScriptModuleRelease.Prerelease => ExperimentalNeed.BetaApis,
            _ => ExperimentalNeed.CannotTell,
        };
    }

    /// <summary>The Experimental field's value.</summary>
    public static string Describe(ExperimentalNeed need) => need switch
    {
        ExperimentalNeed.None => "No",
        ExperimentalNeed.BetaApis => "Needs Beta APIs",
        ExperimentalNeed.CannotTell => "Can't tell from its manifest",

        _ => throw new ArgumentOutOfRangeException(
            nameof(need), need, "No sentence is written for this answer."),
    };

    /// <summary>
    /// Whether it runs scripts, and on which modules: <c>Yes: @minecraft/server 2.8.0</c>.
    /// </summary>
    /// <remarks>
    /// Each module's version is shown as the manifest spells it, because the
    /// spelling is the answer: <c>2.1.0-beta</c> is what makes a pack need Beta APIs.
    /// </remarks>
    public static string Scripts(PackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!manifest.HasScriptModule)
        {
            return "No";
        }

        var modules = manifest.ScriptModuleDependencies
            .Select(d => d.Version.Raw is { Length: > 0 } version ? $"{d.Target} {version}" : d.Target)
            .ToList();

        return modules.Count == 0 ? "Yes" : "Yes: " + string.Join(", ", modules);
    }

    /// <summary>
    /// What it needs to load: a Bedrock floor and the packs it depends on.
    /// </summary>
    /// <param name="manifest">The pack.</param>
    /// <param name="nameOf">
    /// What to call a pack it depends on: its kind and the name its row shows, as
    /// a finding calls it (<see cref="AddonText.Needed"/>).
    /// </param>
    public static string Needs(PackManifest manifest, Func<string, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(nameOf);

        var parts = new List<string>();

        switch (manifest.MinEngineVersion.State)
        {
            case PackVersionState.Read:
                parts.Add($"Bedrock {manifest.MinEngineVersion.Version} or newer");
                break;

            case PackVersionState.Malformed:
                parts.Add($"Bedrock {manifest.MinEngineVersion.Raw} (unreadable)");
                break;
        }

        parts.AddRange(manifest.PackDependencies.Select(d => nameOf(d.Target)));

        return parts.Count == 0 ? "Nothing" : string.Join("; ", parts);
    }

    /// <summary>Its authors from <c>metadata.authors</c>, or null when it names none.</summary>
    public static string? Authors(PackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var names = manifest.Authors
            .Select(a => PackDisplayName.WithoutFormatting(a).Trim())
            .Where(a => a.Length > 0)
            .ToList();

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    /// <summary>Its description, or null when it has none worth showing.</summary>
    /// <remarks>
    /// A description that is still a localisation key after the pack's language
    /// file was applied says nothing to a person, so it counts as none.
    /// </remarks>
    public static string? Description(PackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Description is not { } description)
        {
            return null;
        }

        var shown = PackDisplayName.WithoutFormatting(description).Trim();

        return shown.Length == 0 || PackDisplayName.LooksLikeAKey(shown) ? null : shown;
    }

    /// <summary>Where this server's copies of a pack are, for the Where field.</summary>
    /// <remarks>
    /// Longer than the note under a row, which names a place only when it is not
    /// the world: here the question is where to look, so every place is named.
    /// </remarks>
    public static string Where(IEnumerable<PackHome> homes, PackPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(homes);

        var order = PackPlacementLayout.Homes.ToList();
        var named = homes.Distinct().OrderBy(order.IndexOf).Select(home => home switch
        {
            PackHome.World => placement == PackPlacement.Unknown
                ? "This world's pack folders"
                : $"This world's {PackPlacementLayout.DescribeMany(placement).ToLowerInvariant()}",
            PackHome.ServerRoot => "The server's own folder",
            PackHome.Development => "The server's development folder",

            _ => throw new NotSupportedException(
                $"No description for {home}. Add an arm here rather than letting the page " +
                "name a place nobody wrote words for."),
        }).ToList();

        return named.Count == 0 ? InTheLibrary : string.Join(", ", named);
    }
}
