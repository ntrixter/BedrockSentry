namespace BedrockSentry.Core.Addons;

/// <summary>
/// Which of a server's copies of one add-on go into the library when somebody asks
/// for it (<c>D78</c> (a)).
/// </summary>
/// <remarks>
/// <para><strong>Every version the library lacks, one copy of each.</strong> A server
/// may hold the same uuid at several versions, and the library offering only some of
/// them still leaves the version picker shut (<c>D112</c>), which asks that the library
/// hold every copy here. One button that leaves the row exactly as it was is a button
/// that did nothing anybody can see.</para>
///
/// <para><strong>The world's copy before the server root's, and the root's before the
/// development folder's</strong>, when one version is in more than one place: the
/// world's is the one this wrapper writes and the one a world backup carries
/// (<c>D97</c>). The others are not read for differences; a version is a version
/// (<c>D79</c>).</para>
///
/// <para><strong>Adding only.</strong> Nothing here deletes, moves or switches off a
/// copy on the server (<c>D78</c>), and a version already on the shelf is left as it
/// is, as an upload leaves it.</para>
/// </remarks>
public static class LibraryCopyIn
{
    /// <summary>The copies to put on the shelf, one per version it lacks, in version order.</summary>
    /// <param name="copies">This server's copies of one uuid.</param>
    /// <param name="shelved">Whether the library holds this version of it.</param>
    public static IReadOnlyList<InstalledPack> Copies(
        IEnumerable<InstalledPack> copies, Func<PackVersion, bool> shelved)
    {
        ArgumentNullException.ThrowIfNull(copies);
        ArgumentNullException.ThrowIfNull(shelved);

        return
        [
            .. copies
                .Where(c => c.Directory is not null
                    && c.Manifest.Version.Version is { } version
                    && !shelved(version))
                .GroupBy(c => c.Manifest.Version.Version!.Value)
                .OrderBy(g => g.Key)
                .Select(g => g.OrderBy(c => c.Home).First()),
        ];
    }

    /// <summary>Whether a row offers Copy to library.</summary>
    /// <param name="uuid">The add-on's uuid.</param>
    /// <param name="copies">This server's copies of it.</param>
    /// <param name="shelved">Whether the library holds this version of it.</param>
    /// <param name="hidden">
    /// Whether the tab leaves it out (<see cref="UntouchedServerPacks.Hides"/>): a pack a
    /// Bedrock package ships is not one somebody wants managed, which is why it is hidden.
    /// </param>
    /// <remarks>
    /// Never for the companion or its resource pack: the wrapper seeds both onto the shelf
    /// itself (<c>D93</c>), and a copy from a server would be a second writer of them.
    /// Never for a uuid the shelf could not file it under.
    /// </remarks>
    public static bool Offered(
        string uuid, IEnumerable<InstalledPack> copies, Func<PackVersion, bool> shelved, bool hidden) =>
        !hidden
        && !CompanionPack.Is(uuid)
        && !CompanionPack.IsResource(uuid)
        && AddonLibraryLayout.IsUsableIdentity(uuid)
        && Copies(copies, shelved).Count > 0;
}
