namespace BedrockSentry.Core.Backups;

/// <summary>
/// A live shadow copy, and where its contents can be read from.
/// </summary>
/// <param name="ShadowId">
/// The GUID Windows gave it, which is also how it is released.
/// </param>
/// <param name="DeviceObject">
/// The volume's device path, e.g.
/// <c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3</c>. Never ends in a
/// separator — Windows does not put one there, and
/// <c>ShadowCopyPaths</c> is the only thing that should be joining to it.
/// </param>
public sealed record ShadowCopy(string ShadowId, string DeviceObject);

/// <summary>
/// Takes and releases Volume Shadow Copies (<c>docs/design.md</c> § 8.4).
/// </summary>
/// <remarks>
/// An interface with one Windows implementation, not because a second platform
/// is coming, but because everything else in the VSS strategy is testable and
/// this one call is not. Behind a seam, the copy loop, the truncation, the path
/// mapping and the release-on-failure rule can all be exercised on any machine;
/// welded in, none of them could.
/// </remarks>
public interface IShadowCopyService
{
    /// <summary>
    /// Snapshots the volume containing <paramref name="volumeRoot"/>
    /// (e.g. <c>C:\</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Windows refused. The message is what it said — usually a missing
    /// privilege, a disabled VSS service, or a volume that cannot be snapshotted
    /// at all (a network share, a FAT32 disk).
    /// </exception>
    /// <param name="instanceId">
    /// The server being backed up. The broker snapshots the volume holding that
    /// server's folder, resolved from the configuration, and never uses
    /// <paramref name="volumeRoot"/>.
    /// </param>
    /// <param name="volumeRoot">The volume, for the in-process implementation.</param>
    Task<ShadowCopy> CreateAsync(
        string instanceId, string volumeRoot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a shadow copy. Never throws.
    /// </summary>
    /// <remarks>
    /// Deliberately quiet. This runs in a <c>finally</c>, where the interesting
    /// exception is usually the one already on its way out — and a failure to
    /// release is a disk-space problem for later, not a reason to lose the
    /// reason the backup failed. It is logged instead.
    /// </remarks>
    Task ReleaseAsync(ShadowCopy shadow, CancellationToken cancellationToken = default);
}

