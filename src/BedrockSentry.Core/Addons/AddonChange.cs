namespace BedrockSentry.Core.Addons;

/// <summary>What a change does to an instance's pack folders.</summary>
public enum AddonFileActionKind
{
    /// <summary>Copy this version out of the library into this instance.</summary>
    CopyFromLibrary,

    /// <summary>
    /// This version's files are already in this instance's folders and the
    /// library holds no copy, so switching it on only registers it (<c>T277</c>).
    /// </summary>
    AlreadyInPlace,

    /// <summary>
    /// Remove this pack's folder from this instance, wherever it is. Both
    /// locations are swept, so a pack an earlier build put in the server root
    /// still goes.
    /// </summary>
    RemoveFromServer,
}

/// <summary>One write to the files.</summary>
public sealed record AddonFileAction(AddonFileActionKind Kind, string Uuid, PackVersion? Version);

/// <summary>What kind of change this is, in an administrator's terms.</summary>
public enum AddonChangeType
{
    Enable,
    Disable,

    /// <summary>
    /// Move an enabled pack to a different version. <strong>Two writes, and the
    /// reason this type exists</strong> — see <see cref="AddonChange"/>.
    /// </summary>
    Update,

    Reorder,
}

/// <summary>Why a change could not be planned.</summary>
public enum AddonChangeRefusal
{
    None,

    /// <summary>The version asked for is not on the shelf.</summary>
    NotInLibrary,

    /// <summary>This pack is not enabled here, so there is nothing to move.</summary>
    NotEnabled,

    /// <summary>It is already at that version.</summary>
    AlreadyAtThatVersion,

    /// <summary>Another version of this uuid is already enabled (<c>D79</c>).</summary>
    AlreadyAtAnotherVersion,

    /// <summary>
    /// Its manifest declares no module kind this understands, so there is no
    /// folder to copy it into and no list to write it to.
    /// </summary>
    /// <remarks>
    /// Refused rather than defaulted. The copy already declines such a pack, so
    /// without this the registration was written on its own — a world naming a
    /// pack whose files were never placed.
    /// </remarks>
    UnknownKindOfPack,

    /// <summary>The list is already in the order asked for (<c>D85</c>).</summary>
    AlreadyInThatOrder,
}

/// <summary>
/// One change to an instance's add-ons: the files and the registration together.
/// </summary>
/// <remarks>
/// <para><strong>The two halves are one object because doing one without the
/// other is the failure this type exists to prevent.</strong> Measured on BDS
/// 1.26.51.1: a world's registration matches on uuid <em>and version</em>, so
/// replacing a pack's files without rewriting the registration makes the pack
/// vanish — and the server reports it with the same <em>"not found"</em>
/// sentence a genuinely missing pack gets. An update is therefore two writes per
/// world, and a caller holding two separate results can perform one and ship the
/// other.</para>
///
/// <para><strong>It is a plan rather than an act.</strong> BDS rewrites a
/// world's files when it stops, so a change written while the server runs is
/// discarded by the server's own exit (<c>D85</c>). Deciding what should happen
/// and doing it are separate, and only the first is here.</para>
/// </remarks>
/// <param name="Placement">
/// <strong>Which of the world's two lists <see cref="WorldList"/> is.</strong> A
/// world keeps behaviour and resource registrations in separate files, so a
/// change carrying a list without saying which one it is leaves the writer to
/// guess — and the guess was the behaviour file for every pack, which made
/// every resource pack a registration Bedrock never read.
/// </param>
public sealed record AddonChange(
    AddonChangeType Type,
    IReadOnlyList<AddonFileAction> Files,
    PackPlacement Placement,
    WorldPackList WorldList,
    string Summary)
{
    /// <summary>
    /// Both halves of an update, or neither.
    /// </summary>
    /// <remarks>
    /// An update whose file actions or whose registration were missing would be
    /// exactly the half-applied state above, so it is checked here rather than
    /// trusted to every construction site.
    /// </remarks>
    public bool IsComplete => Type != AddonChangeType.Update || Files.Count > 0;
}

/// <summary>A planned change, or why there is not one.</summary>
public readonly record struct AddonChangePlan(AddonChange? Change, AddonChangeRefusal Refusal)
{
    public static AddonChangePlan Of(AddonChange change) => new(change, AddonChangeRefusal.None);

    public static AddonChangePlan Refused(AddonChangeRefusal refusal) => new(null, refusal);
}

/// <summary>Works out what a change to an instance's add-ons would take.</summary>
/// <remarks>
/// Pure, and it names versions rather than paths: where the library keeps a
/// version and where an instance keeps its packs are the caller's business, and
/// keeping them out of here is what lets every arm be a unit test.
/// </remarks>
public static class AddonChangePlanner
{
    /// <summary>Switch a pack on at a particular version.</summary>
    /// <param name="placement">
    /// Which kind of pack this is, which decides both the folder its files go
    /// into and the list it is registered in. Taken from the pack's own manifest
    /// rather than from the list it is being added to.
    /// </param>
    /// <param name="inPlace">
    /// Whether this version's files are already in the instance's folders. A pack
    /// placed there by hand that the library does not hold is switched on where
    /// it is (<c>T277</c>); one the library holds is copied from the library.
    /// </param>
    /// <param name="replaceCopies">
    /// Whether this server has a copy of another version, which goes before the library's
    /// copy arrives (<c>D112</c>). The caller offers another version only when the library
    /// holds every copy this server has, so nothing it deletes is lost.
    /// </param>
    public static AddonChangePlan Enable(
        WorldPackList world,
        PackPlacement placement,
        string uuid,
        PackVersion version,
        Func<string, PackVersion, bool> inLibrary,
        Func<string, PackVersion, bool>? inPlace = null,
        bool replaceCopies = false)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(inLibrary);
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);

        if (PackPlacementLayout.WorldListFileName(placement) is null)
        {
            return AddonChangePlan.Refused(AddonChangeRefusal.UnknownKindOfPack);
        }

        var fromLibrary = inLibrary(uuid, version);

        if (!fromLibrary && inPlace?.Invoke(uuid, version) != true)
        {
            return AddonChangePlan.Refused(AddonChangeRefusal.NotInLibrary);
        }

        var change = world.Enable(uuid, version);
        var files = fromLibrary ? AddonFileActionKind.CopyFromLibrary : AddonFileActionKind.AlreadyInPlace;

        return change.Kind switch
        {
            // D112. A copy of another version goes first, as an update's does: the
            // library's copy lands in the folder named by the uuid, and copying over
            // another version's files leaves a mix of both.
            WorldPackChangeKind.Enabled => AddonChangePlan.Of(new AddonChange(
                AddonChangeType.Enable,
                replaceCopies && fromLibrary
                    ? [new AddonFileAction(AddonFileActionKind.RemoveFromServer, uuid, null), new AddonFileAction(files, uuid, version)]
                    : [new AddonFileAction(files, uuid, version)],
                placement,
                change.List,
                $"Switch on {uuid} {version}")),

            WorldPackChangeKind.AlreadyEnabled =>
                AddonChangePlan.Refused(AddonChangeRefusal.AlreadyAtThatVersion),

            _ => AddonChangePlan.Refused(AddonChangeRefusal.AlreadyAtAnotherVersion),
        };
    }

    /// <summary>Switch a pack off and take its files out of the instance.</summary>
    /// <param name="placement">
    /// <strong>The list the registration is in, not the one its pack belongs
    /// in.</strong> A world written before the placement was consulted holds its
    /// resource packs in the behaviour file, and switching off has to reach
    /// those or the panel reports a pack as on with no way to remove it.
    /// </param>
    public static AddonChangePlan Disable(WorldPackList world, PackPlacement placement, string uuid)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);

        if (PackPlacementLayout.WorldListFileName(placement) is null)
        {
            return AddonChangePlan.Refused(AddonChangeRefusal.UnknownKindOfPack);
        }

        var change = world.Disable(uuid);

        return change.Kind == WorldPackChangeKind.Disabled
            ? AddonChangePlan.Of(new AddonChange(
                AddonChangeType.Disable,
                [new AddonFileAction(AddonFileActionKind.RemoveFromServer, uuid, null)],
                placement,
                change.List,
                $"Switch off {uuid}"))
            : AddonChangePlan.Refused(AddonChangeRefusal.NotEnabled);
    }

    /// <summary>
    /// Move an enabled pack to another version — <strong>both writes</strong>.
    /// </summary>
    /// <remarks>
    /// It is one operation rather than a disable followed by an enable, because
    /// the two-step version leaves a window in which the world names a pack that
    /// is not there. Presented to an administrator as an update, which is what
    /// it is, rather than as two things they have to get right in order.
    /// </remarks>
    public static AddonChangePlan Update(
        WorldPackList world,
        PackPlacement placement,
        string uuid,
        PackVersion to,
        Func<string, PackVersion, bool> inLibrary)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(inLibrary);
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);

        if (PackPlacementLayout.WorldListFileName(placement) is null)
        {
            return AddonChangePlan.Refused(AddonChangeRefusal.UnknownKindOfPack);
        }

        if (world.EntryFor(uuid) is not { } existing)
        {
            return AddonChangePlan.Refused(AddonChangeRefusal.NotEnabled);
        }

        if (existing.Version.Version == to)
        {
            return AddonChangePlan.Refused(AddonChangeRefusal.AlreadyAtThatVersion);
        }

        if (!inLibrary(uuid, to))
        {
            return AddonChangePlan.Refused(AddonChangeRefusal.NotInLibrary);
        }

        var change = world.SetVersion(uuid, to);

        if (change.Kind != WorldPackChangeKind.VersionChanged)
        {
            return AddonChangePlan.Refused(AddonChangeRefusal.NotEnabled);
        }

        // The old folder goes and the new one arrives, in that order, so a pack
        // whose two versions would land in the same place cannot leave a mix of
        // both — the library names folders by uuid, so they do.
        return AddonChangePlan.Of(new AddonChange(
            AddonChangeType.Update,
            [
                new AddonFileAction(AddonFileActionKind.RemoveFromServer, uuid, existing.Version.Version),
                new AddonFileAction(AddonFileActionKind.CopyFromLibrary, uuid, to),
            ],
            placement,
            change.List,
            $"Update {uuid} to {to}"));
    }

    /// <summary>Move a pack in the order, which decides what wins.</summary>
    /// <remarks>
    /// No file actions: reordering rewrites the world's list and moves no pack
    /// folder in either location, which is why it is the one change that is safe
    /// to describe as small.
    /// </remarks>
    /// <param name="placement">
    /// The list the registration is in. Order is per list — a behaviour pack and
    /// a resource pack are in different stacks and neither can be moved past the
    /// other — so a reorder names which one it rewrote.
    /// </param>
    public static AddonChangePlan Reorder(
        WorldPackList world, PackPlacement placement, string uuid, int index)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (PackPlacementLayout.WorldListFileName(placement) is null)
        {
            return AddonChangePlan.Refused(AddonChangeRefusal.UnknownKindOfPack);
        }

        var change = world.MoveTo(uuid, index);

        return change.Kind == WorldPackChangeKind.Reordered
            ? AddonChangePlan.Of(new AddonChange(
                AddonChangeType.Reorder, [], placement, change.List,
                $"Move {uuid} to position {index + 1}"))
            : AddonChangePlan.Refused(AddonChangeRefusal.NotEnabled);
    }

    /// <summary>
    /// Put a list's packs in an order queued while the server ran (<c>D85</c>); see
    /// <see cref="WorldPackList.Arrange"/>.
    /// </summary>
    /// <returns>Refused with <see cref="AddonChangeRefusal.AlreadyInThatOrder"/> when it is already in that order.</returns>
    public static AddonChangePlan Arrange(WorldPackList world, PackPlacement placement, IReadOnlyList<string> order)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (PackPlacementLayout.WorldListFileName(placement) is null)
        {
            return AddonChangePlan.Refused(AddonChangeRefusal.UnknownKindOfPack);
        }

        var change = world.Arrange(order);

        return change.Kind == WorldPackChangeKind.Reordered
            ? AddonChangePlan.Of(new AddonChange(
                AddonChangeType.Reorder, [], placement, change.List, "Set the load order queued while the server ran"))
            : AddonChangePlan.Refused(AddonChangeRefusal.AlreadyInThatOrder);
    }
}
