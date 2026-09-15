using System.IO.Compression;
using BedrockSentry.Core.Backups;
using BedrockSentry.Services.Backups;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 8.3, § 5.5. The test that matters most is truncation: files on
/// disk are deliberately longer than their manifest lengths, the way a live
/// world's files are while saves are held, and the archive must carry exactly
/// the manifest bytes.
/// </summary>
public sealed class ZipBackupStrategyTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-zip-{Guid.NewGuid():N}");

    private readonly FakeTimeProvider _time =
        new(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));

    public ZipBackupStrategyTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private ZipBackupStrategy Strategy() =>
        new(_time, NullLogger<ZipBackupStrategy>.Instance);

    private string WriteWorldFile(string relativePath, string content)
    {
        var path = Path.Combine(_directory, "worlds", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private BackupContext Context(
        IReadOnlyList<SaveManifestEntry> manifest,
        ZipCompression compression = ZipCompression.Balanced,
        BackupRetention? retention = null)
    {
        var artifacts = Path.Combine(_directory, "artifacts");
        Directory.CreateDirectory(artifacts);

        return new BackupContext(
            "survival", "Survival",
            _directory,
            Path.Combine(_directory, "worlds"),
            manifest,
            artifacts,
            new BackupSettings
            {
                Zip = new()
                {
                    Enabled = true,
                    Compression = compression,
                    Retention = retention ?? new(),
                },
            },
            BackupReason.Manual,
            null,
            _time.GetUtcNow());
    }

    [Fact]
    public async Task Archives_exactly_the_manifest_bytes_not_what_is_on_disk()
    {
        // Longer on disk than in the manifest — the shape of a live world with
        // saves held, where the tail belongs to the next save.
        WriteWorldFile("Bedrock level/db/CURRENT", "snapshot-bytes-PLUS-NEWER-WRITES");
        WriteWorldFile("Bedrock level/level.dat", "world!extra");

        var result = await Strategy().RunAsync(
            Context([
                new SaveManifestEntry("Bedrock level/db/CURRENT", 14),
                new SaveManifestEntry("Bedrock level/level.dat", 6),
            ]),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
        Assert.NotNull(result.ArtifactPath);

        using var zip = ZipFile.OpenRead(result.ArtifactPath);
        Assert.Equal(2, zip.Entries.Count);

        using var reader = new StreamReader(zip.GetEntry("Bedrock level/db/CURRENT")!.Open());
        Assert.Equal("snapshot-bytes", reader.ReadToEnd());

        using var reader2 = new StreamReader(zip.GetEntry("Bedrock level/level.dat")!.Open());
        Assert.Equal("world!", reader2.ReadToEnd());
    }

    /// <summary>
    /// The failure a real install hit on every backup taken while the server was
    /// up: a sharing violation naming <c>db\NNNNNN.log</c>.
    /// </summary>
    /// <remarks>
    /// A running BDS holds the current LevelDB write-ahead log open for writing
    /// for the life of the server, and <see cref="File.OpenRead"/> asks for
    /// <see cref="FileShare.Read"/> — "others may read this but not write it" —
    /// which Windows refuses against an existing writer. The holder below is
    /// opened exactly as such a writer holds it.
    ///
    /// This assertion has teeth on Windows, where CI runs. On Linux the same
    /// open succeeds either way, which is precisely how the strategy shipped
    /// unable to archive a running server with every test passing.
    /// </remarks>
    [Fact]
    public async Task Archives_a_world_whose_files_the_server_still_holds_open()
    {
        var path = WriteWorldFile("Bedrock level/db/000102.log", "held-open-by-bedrock");

        await using var heldByTheServer = new FileStream(
            path, FileMode.Open, FileAccess.Write, FileShare.Read);

        var result = await Strategy().RunAsync(
            Context([new SaveManifestEntry("Bedrock level/db/000102.log", 20)]),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);

        using var zip = ZipFile.OpenRead(result.ArtifactPath!);
        using var reader = new StreamReader(Assert.Single(zip.Entries).Open());
        Assert.Equal("held-open-by-bedrock", reader.ReadToEnd());
    }

    [Fact]
    public async Task A_file_the_manifest_names_but_disk_lacks_fails_the_whole_archive()
    {
        WriteWorldFile("Bedrock level/level.dat", "world!");

        var result = await Strategy().RunAsync(
            Context([
                new SaveManifestEntry("Bedrock level/level.dat", 6),
                new SaveManifestEntry("Bedrock level/db/MISSING", 10),
            ]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("MISSING", result.Detail);

        // No zip and no leftover temp: a backup that exists but cannot restore
        // is worse than a failed one.
        var artifacts = Path.Combine(_directory, "artifacts");
        Assert.Empty(Directory.EnumerateFiles(artifacts));
    }

    [Fact]
    public async Task A_file_shorter_than_its_manifest_length_fails_rather_than_padding()
    {
        WriteWorldFile("Bedrock level/level.dat", "tiny");

        var result = await Strategy().RunAsync(
            Context([new SaveManifestEntry("Bedrock level/level.dat", 100)]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("changed underneath", result.Detail);
    }

    [Fact]
    public async Task An_empty_manifest_is_refused_not_archived()
    {
        var result = await Strategy().RunAsync(Context([]), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("no files", result.Detail);
    }

    [Theory]
    [InlineData(ZipCompression.None)]
    [InlineData(ZipCompression.Balanced)]
    [InlineData(ZipCompression.Maximum)]
    public async Task Every_compression_stop_produces_a_readable_archive(ZipCompression compression)
    {
        WriteWorldFile("Bedrock level/level.dat", "world!");

        var result = await Strategy().RunAsync(
            Context([new SaveManifestEntry("Bedrock level/level.dat", 6)], compression),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);

        using var zip = ZipFile.OpenRead(result.ArtifactPath!);
        using var reader = new StreamReader(Assert.Single(zip.Entries).Open());
        Assert.Equal("world!", reader.ReadToEnd());
    }

    [Fact]
    public async Task Count_retention_keeps_the_newest_and_never_the_one_just_written()
    {
        WriteWorldFile("Bedrock level/level.dat", "world!");
        var manifest = new[] { new SaveManifestEntry("Bedrock level/level.dat", 6) };
        var strategy = Strategy();

        // Three backups, an hour apart; keep 2. The oldest goes, the newest —
        // the one being written — must survive whatever the rule says.
        for (var i = 0; i < 3; i++)
        {
            var result = await strategy.RunAsync(
                Context(manifest, retention: new() { Mode = BackupRetentionMode.Count, Value = 2 }),
                CancellationToken.None);
            Assert.True(result.Succeeded, result.Detail);
            _time.Advance(TimeSpan.FromHours(1));
        }

        var archives = Directory
            .EnumerateFiles(Path.Combine(_directory, "artifacts"), "backup-*.zip")
            .Select(Path.GetFileName)
            .OrderBy(f => f)
            .ToList();

        Assert.Equal(2, archives.Count);
        Assert.DoesNotContain("backup-20260806-120000.zip", archives);
        Assert.Contains("backup-20260806-140000.zip", archives);
    }

    [Fact]
    public async Task Age_retention_prunes_by_days()
    {
        WriteWorldFile("Bedrock level/level.dat", "world!");
        var manifest = new[] { new SaveManifestEntry("Bedrock level/level.dat", 6) };
        var strategy = Strategy();

        var retention = new BackupRetention { Mode = BackupRetentionMode.Days, Value = 7 };

        Assert.True((await strategy.RunAsync(Context(manifest, retention: retention), CancellationToken.None)).Succeeded);

        // Ten days later the first archive is past retention. Age pruning uses
        // the file clock, so age the file itself as a filesystem would have.
        var first = Directory.EnumerateFiles(Path.Combine(_directory, "artifacts"), "backup-*.zip").Single();
        File.SetLastWriteTimeUtc(first, _time.GetUtcNow().UtcDateTime);
        _time.Advance(TimeSpan.FromDays(10));

        Assert.True((await strategy.RunAsync(Context(manifest, retention: retention), CancellationToken.None)).Succeeded);

        var remaining = Directory
            .EnumerateFiles(Path.Combine(_directory, "artifacts"), "backup-*.zip")
            .ToList();

        Assert.Single(remaining);
        Assert.DoesNotContain(first, remaining);
    }

    [Fact]
    public async Task A_hand_placed_zip_in_the_folder_is_never_pruned()
    {
        WriteWorldFile("Bedrock level/level.dat", "world!");
        var artifacts = Path.Combine(_directory, "artifacts");
        Directory.CreateDirectory(artifacts);

        var stranger = Path.Combine(artifacts, "my-own-archive.zip");
        await File.WriteAllTextAsync(stranger, "not ours");

        var result = await Strategy().RunAsync(
            Context(
                [new SaveManifestEntry("Bedrock level/level.dat", 6)],
                retention: new() { Mode = BackupRetentionMode.Count, Value = 1 }),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
        Assert.True(File.Exists(stranger));
    }
}
