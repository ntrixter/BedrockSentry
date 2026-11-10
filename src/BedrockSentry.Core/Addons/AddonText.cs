namespace BedrockSentry.Core.Addons;

/// <summary>
/// What the add-on surfaces say, in an administrator's words.
/// </summary>
/// <remarks>
/// <para>In <c>Core</c> rather than in the markup for the reason this codebase
/// keeps paying for: the test project does not reference the web project, so a
/// switch over an enum left in a <c>.razor</c> is checked on a real page or not
/// at all — and its <c>_ =&gt; throw</c> arm fires at whoever is using the panel
/// rather than at a build. <c>AddonTextTests</c> walks both enums.</para>
///
/// <para>The headline states what is wrong; <see cref="Subject"/> names the pack
/// it is about, and the finding's own detail carries any other names. Keeping
/// them apart is what lets the claim be tested without a renderer and stops a
/// pack's name becoming part of a sentence nothing checks.</para>
/// </remarks>
public static class AddonText
{
    /// <summary>
    /// What a missing pack's card adds (<c>T416</c>): the list naming it and the version
    /// asked for, since without its folder there is no name to show.
    /// </summary>
    /// <param name="listFile">The world's list file the entry is in.</param>
    /// <param name="version">The version the entry asks for, as read.</param>
    public static string NotInstalledDetail(string? listFile, string version) =>
        $"Listed in {listFile ?? "the world's pack list"} at version {version}, with no pack of that id "
        + "on this server, so Bedrock skips it. Upload the add-on, or remove the entry.";

    /// <summary>The line a finding leads with.</summary>
    public static string Headline(AddonFindingKind kind) => kind switch
    {
        AddonFindingKind.RegisteredVersionNotInstalled =>
            "A different version of this add-on is switched on than the one installed",

        AddonFindingKind.RegisteredPackNotInstalled =>
            "This server is asking for an add-on it does not have",

        AddonFindingKind.InstalledButNotRegistered =>
            "This add-on is in the folder but is not switched on",

        AddonFindingKind.InstalledInTwoPlaces =>
            "This add-on is in two places at once, and only one of them is loaded",

        AddonFindingKind.WorldCopyLoadsOverServerCopy =>
            "This add-on is in this world and in the server's own folder, and Bedrock loads the copy in this world",

        AddonFindingKind.RegisteredInTheWrongList =>
            "This add-on is switched on in the wrong list, so Bedrock never loads it",

        AddonFindingKind.EngineTooOld =>
            "This add-on needs a newer Bedrock than this server runs",

        AddonFindingKind.MissingPackDependency =>
            "This add-on needs another one that is not switched on here",

        AddonFindingKind.ScriptsNeedBetaApis =>
            "This add-on uses a beta script module, which needs Beta APIs switched on for this world",

        AddonFindingKind.ScriptsNotPermitted =>
            "This server does not let add-ons run scripts",

        AddonFindingKind.ShadowedByEarlierPack =>
            "Another add-on higher up the list overrides part of this one",

        // No direction word: this is about the world's file, not about a
        // position on the page the reader is looking at.
        AddonFindingKind.RegisteredTwice =>
            "This server's world switches this add-on on more than once",

        AddonFindingKind.CouldNotCheck =>
            "Something about this add-on could not be checked",

        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "No headline is written for this finding."),
    };

    /// <summary>What a finding calls the add-on it is about.</summary>
    /// <param name="uuid">The finding's subject.</param>
    /// <param name="rowName">
    /// The name the add-on's row on the tab shows, or null when it has no row.
    /// </param>
    /// <remarks>
    /// <para><strong>The row's own name</strong> (<c>T299</c>). The headline says
    /// <em>this add-on</em>, and with several packs switched on nothing said which
    /// one it meant: two Beta APIs findings were told apart only by their wording.
    /// Taking the row's name rather than reading the manifest again means a finding
    /// and the table cannot call one pack two things.</para>
    ///
    /// <para>A uuid the world asks for that no folder and no library holds has no
    /// row, and its uuid is then the one thing the finding and the world's list
    /// share.</para>
    /// </remarks>
    public static string Subject(string uuid, string? rowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);

        return string.IsNullOrWhiteSpace(rowName) ? uuid : rowName;
    }

    /// <summary>What a pack another one depends on is called: its kind, then its name.</summary>
    /// <param name="uuid">The pack depended on.</param>
    /// <param name="name">The name its row shows, or null when nothing here holds it.</param>
    /// <param name="placement">Its kind, or <see cref="PackPlacement.Unknown"/> when not known.</param>
    /// <param name="withUuid">Add the uuid after a name, as a finding does.</param>
    /// <remarks>
    /// <para><strong>The kind first</strong> (<c>T309</c>). Bedrock names both of its
    /// chemistry packs <em>Chemistry</em>, and the resource pack needs the behaviour
    /// pack, so its finding read <em>Chemistry needs Chemistry</em> and its details
    /// said it needs <em>Chemistry</em>, which reads as itself.</para>
    ///
    /// <para>A finding keeps the uuid beside a name, because a name can be a
    /// folder's and the uuid is what the world's list will carry. A pack nothing
    /// here holds is named by its uuid, and its kind is not known.</para>
    /// </remarks>
    public static string Needed(string uuid, string? name, PackPlacement placement, bool withUuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);

        var kind = PackPlacementLayout.Describe(placement);

        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, uuid, StringComparison.OrdinalIgnoreCase))
        {
            return $"the {kind} {uuid}";
        }

        return withUuid ? $"the {kind} {name} ({uuid})" : $"the {kind} {name}";
    }

    /// <summary>Why a change was not made.</summary>
    public static string Refusal(AddonChangeRefusal refusal) => refusal switch
    {
        // Only when the version is not in this server's folders either, since a
        // pack placed there by hand is switched on where it is (T277).
        AddonChangeRefusal.NotInLibrary =>
            "The add-on library does not hold that version.",

        AddonChangeRefusal.NotEnabled =>
            "This server is not using that add-on.",

        AddonChangeRefusal.AlreadyAtThatVersion =>
            "That is already the version in use.",

        AddonChangeRefusal.AlreadyInThatOrder =>
            "The add-ons are already in that order.",

        // Named rather than warned about: Bedrock resolves an add-on to one
        // version, so a second entry is an ambiguity rather than a choice.
        AddonChangeRefusal.AlreadyAtAnotherVersion =>
            "Another version of this add-on is already switched on. Switch that one off first — "
            + "Bedrock resolves an add-on to one version, so two is not a choice it can make.",

        // Named as a fact about the pack rather than as a wrapper rule: the
        // manifest is where the kind is declared, so that is where it is fixed.
        AddonChangeRefusal.UnknownKindOfPack =>
            "This add-on's manifest does not say whether it is a behaviour pack or a resource "
            + "pack, so there is nowhere to put it and nothing that would load it.",

        AddonChangeRefusal.None =>
            throw new ArgumentOutOfRangeException(
                nameof(refusal), refusal, "Nothing was refused, so there is nothing to say."),

        _ => throw new ArgumentOutOfRangeException(
            nameof(refusal), refusal, "No sentence is written for this refusal."),
    };
}
