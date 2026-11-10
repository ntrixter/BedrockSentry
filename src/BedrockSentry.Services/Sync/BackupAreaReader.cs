using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Commands;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Packages;
using BedrockSentry.Core.Structures;
using BedrockSentry.Services.Addons;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Commands;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Worlds;

namespace BedrockSentry.Services.Sync;

/// <summary>The pieces read out of a backup, or why they were not.</summary>
public sealed record BackupAreaRead(IReadOnlyList<StructurePackFile>? Files, string? Failure);

/// <summary>Reads pieces of an area out of a server's backup without touching the server (<c>D108</c>).</summary>
public interface IBackupAreaReader
{
    /// <param name="progress">Told each piece's label as it is saved on the copy.</param>
    Task<BackupAreaRead> ReadAsync(
        InstanceSupervisor server,
        RestorableBackup backup,
        IReadOnlyList<LabelledPiece> pieces,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads an area out of a backup on a throwaway copy of the server's own Bedrock build
/// (<c>D108</c>), while the server keeps running.
/// </summary>
/// <remarks>
/// <para>The throwaway starts on the backup's world (<see cref="ThrowawayRequest.World"/>) with
/// the server's packs from outside its world beside it, so a block a pack adds is the same
/// block when the piece is put back; the packs the world carries come with it (<c>D97</c>).
/// Each piece is saved with <c>structure save … disk</c> as a sync saves (<see cref="StructureConsole"/>),
/// and read out of the stopped copy's database before its folder is deleted.</para>
///
/// <para>A backup's world keeps its ticking areas, and a dimension holds ten, so the copy
/// removes them all first: a pass borrows one per piece. Measured on Linux BDS 1.26.52.3, a
/// copy on a backup's world started in 2.8 s and saved a 32 × 384 × 32 piece in about a second.</para>
/// </remarks>
/// <param name="packageOf">The package holding the build the server runs, or null when the store has none.</param>
/// <param name="packsOf">The server's packs a copy loads beside the backup's world (<see cref="PacksOutsideTheWorld"/>).</param>
public sealed class BackupAreaReader(
    ThrowawayServer throwaway,
    Func<InstanceSupervisor, ServerPackage?> packageOf,
    Func<InstanceSupervisor, IReadOnlyList<ThrowawayPack>> packsOf,
    StructureConsole console,
    TimeProvider time) : IBackupAreaReader
{
    /// <summary>Clears every ticking area the backup's world holds, in each dimension.</summary>
    internal static IReadOnlyList<string> ClearTickingAreas { get; } =
        [.. BedrockDimension.All.Select(d => d.Run("tickingarea remove_all"))];

    public async Task<BackupAreaRead> ReadAsync(
        InstanceSupervisor server,
        RestorableBackup backup,
        IReadOnlyList<LabelledPiece> pieces,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(pieces);

        if (packageOf(server) is not { } package)
        {
            return new BackupAreaRead(null,
                "the Bedrock package this server runs is not in the package store, so no copy of it can read the backup. "
                + "Download its version again on the Updates page.");
        }

        List<StructurePackFile>? files = null;

        var request = new ThrowawayRequest(package, server.Definition.Executable, packsOf(server), null, PlanAsync)
        {
            World = backup,
            AfterStop = (world, _) =>
            {
                var (read, failure) = WorldSync.ReadPieces(world, pieces, folder: null);
                files = failure is null ? read : throw new ThrowawayFailure(failure);
                return Task.CompletedTask;
            },
        };

        var result = await throwaway.RunAsync(request, cancellationToken).ConfigureAwait(false);

        return result.Succeeded && files is not null
            ? new BackupAreaRead(files, null)
            : new BackupAreaRead(null, $"the backup could not be read: {(result.Reason ?? "the copy read nothing").TrimEnd('.')}.");

        async Task<IReadOnlyList<ConsoleExchange>> PlanAsync(ThrowawaySession session, CancellationToken ct)
        {
            var cleared = await session.ExchangeAsync(ClearTickingAreas, ct).ConfigureAwait(false);

            using var copy = new ThrowawayConsole(session, $"The copy reading {backup.Name}", time);
            var saved = await console
                .SaveAsync(copy, pieces, new Progress(progress), ct)
                .ConfigureAwait(false);

            if (saved.State != StructurePassState.Finished)
            {
                throw new ThrowawayFailure((saved.Reason ?? "not every piece was saved").TrimEnd('.'));
            }

            return cleared;
        }
    }

    /// <summary>The server's packs that live outside its world: the world's own come with the backup.</summary>
    public static IReadOnlyList<ThrowawayPack> PacksOutsideTheWorld(InstanceAddonState state) =>
        [.. state.Installed
            .Where(p => p.Home != PackHome.World
                && p.Directory is not null
                && p.Manifest.Version.Version is not null
                && p.Manifest.Placement is not PackPlacement.Unknown)
            .Select(p => new ThrowawayPack(p.Directory!, p.Manifest.Uuid, p.Manifest.Version.Version!.Value, p.Manifest.Placement))];

    private sealed class Progress(IProgress<string>? inner) : IProgress<StructurePieceOutcome>
    {
        public void Report(StructurePieceOutcome value)
        {
            if (value.State == StructurePieceState.Done)
            {
                inner?.Report(value.Piece.Label);
            }
        }
    }
}
