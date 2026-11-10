using System.IO.Compression;
using BedrockSentry.Core.Addons;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Addons;

/// <summary>What one start's seeding of the companion pack did.</summary>
public enum CompanionSeedOutcome
{
    /// <summary>This version was not on the shelf, and now is.</summary>
    Added,

    /// <summary>This version was already on the shelf, and nothing was written.</summary>
    AlreadyPresent,

    /// <summary>This build carries no companion pack beside its executable.</summary>
    NotShipped,

    /// <summary>The build carries the pack and it is not on the shelf; the reason says why.</summary>
    NotAdded,
}

/// <summary>What seeding did at this start, kept for the panel.</summary>
/// <param name="Version">The version the shelf holds or was given; null when nothing was read.</param>
/// <param name="Reason">Why the pack is not on the shelf, for <see cref="CompanionSeedOutcome.NotAdded"/>.</param>
public sealed record CompanionSeedResult(
    CompanionSeedOutcome Outcome, PackVersion? Version, string? Reason);

/// <summary>
/// Puts the companion pack this build carries onto the add-on library, at every
/// start (<c>D93</c>).
/// </summary>
/// <remarks>
/// <para><strong>Through <see cref="AddonLibrary.ImportAsync"/>, the path an
/// upload takes.</strong> The shipped folder is zipped in memory and imported, so
/// it meets the same archive rules, is staged and moved the same way, and a
/// version already on the shelf is left exactly as it is.</para>
///
/// <para><strong>At every start, with nothing recorded about earlier ones.</strong>
/// A start whose write failed is retried by the next, and a shelf emptied by hand
/// is refilled. A version already held costs one directory check.</para>
///
/// <para><strong>It never stops the wrapper starting.</strong> A pack missing
/// from the shelf is one feature unavailable; a service that will not start is
/// every server down. The outcome is kept in <see cref="Last"/> for the panel as
/// well as logged.</para>
/// </remarks>
public sealed class CompanionPackSeeder : IHostedService
{
    /// <summary>
    /// The folder beside the executable that carries the pack. The Host project
    /// copies <c>companion-pack/</c> there under this name, and
    /// <c>scripts/ReleaseContents.ps1</c> requires its two files in every release.
    /// </summary>
    public const string ShippedFolderName = "companion-pack";

    /// <summary>
    /// The folder beside <see cref="ShippedFolderName"/> that carries the companion's
    /// resource pack (<c>D109</c>), seeded the same way.
    /// </summary>
    public const string ResourcesFolderName = "companion-resources";

    private readonly AddonLibrary _library;
    private readonly ILogger<CompanionPackSeeder> _logger;
    private readonly string _shipped;
    private readonly string _resources;
    private readonly Func<string, MemoryStream> _zip;

    public CompanionPackSeeder(AddonLibrary library, ILogger<CompanionPackSeeder> logger)
        : this(library, logger, Path.Combine(AppContext.BaseDirectory, ShippedFolderName))
    {
    }

    /// <param name="zip">
    /// Reads the shipped folder into an archive. A seam, because a file in the
    /// install folder that cannot be read cannot be arranged on every platform the
    /// suite runs on.
    /// </param>
    /// <param name="resourcesDirectory">
    /// The resource pack's folder; by default <see cref="ResourcesFolderName"/> beside
    /// <paramref name="shippedDirectory"/>.
    /// </param>
    internal CompanionPackSeeder(
        AddonLibrary library,
        ILogger<CompanionPackSeeder> logger,
        string shippedDirectory,
        Func<string, MemoryStream>? zip = null,
        string? resourcesDirectory = null)
    {
        _library = library;
        _logger = logger;
        _shipped = shippedDirectory;
        _resources = resourcesDirectory
            ?? Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(shippedDirectory)) ?? "", ResourcesFolderName);
        _zip = zip ?? Zip;
    }

    /// <summary>What this start's seeding of the companion did; null until it has run.</summary>
    public CompanionSeedResult? Last { get; private set; }

    /// <summary>What this start's seeding of the companion's resources did; null until it has run.</summary>
    public CompanionSeedResult? LastResources { get; private set; }

    /// <summary>
    /// What the add-on pages say about this start's seeding: a sentence when the
    /// pack is not on the shelf, null when it is or seeding has not run.
    /// </summary>
    public static string? Describe(CompanionSeedResult? result) => result?.Outcome switch
    {
        CompanionSeedOutcome.NotShipped =>
            "This build of BedrockSentry carries no companion pack, so no server can switch it on.",
        CompanionSeedOutcome.NotAdded =>
            "The companion pack could not be added to the add-on library at this start"
            + (result.Reason is { Length: > 0 } reason ? $": {reason.TrimEnd('.')}." : ".")
            + " It is tried again at every start.",
        _ => null,
    };

    /// <summary>
    /// What the add-on pages say about this start's seeding of the companion's resources
    /// (<c>D109</c>): a sentence when they are not on the shelf, null when they are.
    /// </summary>
    public static string? DescribeResources(CompanionSeedResult? result) => result?.Outcome switch
    {
        CompanionSeedOutcome.NotShipped =>
            "This build of BedrockSentry carries no companion resources, so the companion draws no outlines.",
        CompanionSeedOutcome.NotAdded =>
            "The companion's resources, which draw its outlines, could not be added to the add-on library at this start"
            + (result.Reason is { Length: > 0 } reason ? $": {reason.TrimEnd('.')}." : ".")
            + " It is tried again at every start.",
        _ => null,
    };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Last = await SeedOrLogAsync(_shipped, ShippedFolderName, CompanionPack.Uuid, "companion pack", cancellationToken).ConfigureAwait(false);
        LastResources = await SeedOrLogAsync(_resources, ResourcesFolderName, CompanionPack.ResourceUuid, "companion resources", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CompanionSeedResult> SeedOrLogAsync(
        string directory, string name, string uuid, string what, CancellationToken cancellationToken)
    {
        try
        {
            return await SeedAsync(directory, name, uuid, what, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Every exception, not a list of expected ones: the promise is that
            // the wrapper starts whatever went wrong here.
            _logger.LogError(ex, "The {What} could not be added to the add-on library", what);
            return new(CompanionSeedOutcome.NotAdded, null, ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<CompanionSeedResult> SeedAsync(
        string directory, string name, string uuid, string what, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(directory, AddonLibraryLayout.ManifestFileName)))
        {
            _logger.LogWarning(
                "This build carries no {What} at {Directory}", what, directory);

            return new(CompanionSeedOutcome.NotShipped, null, null);
        }

        PackImportResult result;

        using (var archive = _zip(directory))
        {
            result = await _library.ImportAsync(archive, name, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!result.Accepted)
        {
            _logger.LogWarning("The add-on library refused the {What}: {Reason}", what, result.Reason);
            return new(CompanionSeedOutcome.NotAdded, null, result.Reason);
        }

        // By uuid: the folder holds one pack, and one that is not the pack this folder
        // is for is reported rather than taken for it.
        if (result.Packs.FirstOrDefault(p => string.Equals(p.Uuid, uuid, StringComparison.OrdinalIgnoreCase)) is not { } pack)
        {
            _logger.LogWarning("The {What} folder at {Directory} holds no pack with uuid {Uuid}", what, directory, uuid);
            return new(CompanionSeedOutcome.NotAdded, null, $"the folder {directory} holds no pack with uuid {uuid}");
        }

        switch (pack.Outcome)
        {
            case PackImportOutcome.Imported:
                _logger.LogInformation(
                    "Added the {What} {Version} to the add-on library", what, pack.Version);
                return new(CompanionSeedOutcome.Added, pack.Version, null);

            case PackImportOutcome.AlreadyPresent:
                return new(CompanionSeedOutcome.AlreadyPresent, pack.Version, null);

            default:
                _logger.LogWarning(
                    "The {What} could not be added to the add-on library: {Reason}", what, pack.Reason);
                return new(CompanionSeedOutcome.NotAdded, pack.Version, pack.Reason);
        }
    }

    /// <summary>The shipped folder as an archive, entries named as an upload's would be.</summary>
    private static MemoryStream Zip(string directory)
    {
        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
                zip.CreateEntryFromFile(file, name);
            }
        }

        buffer.Position = 0;
        return buffer;
    }
}
