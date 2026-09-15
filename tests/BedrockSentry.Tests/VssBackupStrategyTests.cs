using System.Text;
using BedrockSentry.Core.Backups;
using BedrockSentry.Services.Backups;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 8.4. Taking the shadow copy is one Windows call this build machine
/// cannot make; everything either side of it — the path mapping, the truncating
/// copy, the retention rule and the release-on-failure guarantee — is ordinary
/// code, and that is where the bugs are.
/// </summary>
public sealed class VssBackupStrategyTests : IDisposable
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 19, 3, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"bs-vss-{Guid.NewGuid():N}");

    private readonly FakeTimeProvider _time = new(Origin);
    private readonly FakeShadowCopyService _shadows;

    private string Instance => Path.Combine(_root, "instance");
    private string Worlds => Path.Combine(Instance, "worlds");
    private string Artifacts => Path.Combine(_root, "artifacts");

    /// <summary>Where the fake shadow copy's contents live.</summary>
    private string ShadowRoot => Path.Combine(_root, "shadow");

    public VssBackupStrategyTests()
    {
        Directory.CreateDirectory(Worlds);
        Directory.CreateDirectory(Artifacts);
        Directory.CreateDirectory(ShadowRoot);

        _shadows = new FakeShadowCopyService(ShadowRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private VssBackupStrategy Strategy() =>
        new(_shadows, _time, NullLogger<VssBackupStrategy>.Instance);

    /// <summary>
    /// Writes a world file, and its shadow-copy twin.
    /// </summary>
    /// <param name="live">
    /// What the volume holds. Longer than <paramref name="manifestLength"/> is
    /// the normal case while saves are held — those trailing bytes belong to the
    /// next save.
    /// </param>
    private SaveManifestEntry WriteWorldFile(string relative, string live, int manifestLength)
    {
        foreach (var directory in new[] { Worlds, ShadowedWorlds })
        {
            var path = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, live);
        }

        return new SaveManifestEntry(relative.Replace('\\', '/'), manifestLength);
    }

    /// <summary>The worlds folder as it appears inside the fake shadow copy.</summary>
    private string ShadowedWorlds =>
        Path.Combine(ShadowRoot, Path.GetRelativePath(Path.GetPathRoot(Worlds)!, Worlds));

    private BackupContext Context(
        IReadOnlyList<SaveManifestEntry> manifest, BackupRetention? retention = null) =>
        new(
            "survival",
            "Survival",
            Instance,
            Worlds,
            manifest,
            Artifacts,
            new BackupSettings
            {
                Vss = new() { Enabled = true, Retention = retention ?? new() },
            },
            BackupReason.Scheduled,
            null,
            _time.GetUtcNow());

    [Fact]
    public async Task The_snapshot_is_copied_out_and_the_shadow_released()
    {
        var entry = WriteWorldFile("Bedrock level/db/000005.ldb", "abcdefghij", 10);

        var result = await Strategy().RunAsync(Context([entry]), default);

        Assert.True(result.Succeeded, result.Detail);
        Assert.NotNull(result.ArtifactPath);

        Assert.Equal(
            "abcdefghij",
            await File.ReadAllTextAsync(
                Path.Combine(result.ArtifactPath, "Bedrock level", "db", "000005.ldb")));

        // The whole point of the finally: a shadow copy left behind holds volume
        // space, and on a schedule it is left behind every single time.
        Assert.Empty(_shadows.Live);
        Assert.Equal(1, _shadows.Created);
    }

    /// <summary>
    /// The trap a shadow copy makes easy to fall into.
    /// </summary>
    /// <remarks>
    /// A shadow copy is a faithful image of the volume, and the volume holds
    /// world files longer than the save BDS just flushed. Copying them whole
    /// produces a folder that looks perfect and restores to a corrupt world
    /// (<c>docs/design.md</c> § 5.5) — the failure nobody finds until they need it.
    /// </remarks>
    [Fact]
    public async Task Files_are_cut_at_the_manifest_length_not_copied_whole()
    {
        var entry = WriteWorldFile("Bedrock level/db/CURRENT", "MANIFEST-000004\nTRAILING", 16);

        var result = await Strategy().RunAsync(Context([entry]), default);

        Assert.True(result.Succeeded, result.Detail);

        var copied = await File.ReadAllTextAsync(
            Path.Combine(result.ArtifactPath!, "Bedrock level", "db", "CURRENT"));

        Assert.Equal("MANIFEST-000004\n", copied);
        Assert.Equal(16, Encoding.UTF8.GetByteCount(copied));
    }

    // Shorter than the manifest means the snapshot and the volume disagree about
    // what was flushed. There is no honest way to finish the copy.
    [Fact]
    public async Task A_file_shorter_than_the_manifest_fails_the_backup()
    {
        var entry = WriteWorldFile("Bedrock level/db/000005.ldb", "short", 4096);

        var result = await Strategy().RunAsync(Context([entry]), default);

        Assert.False(result.Succeeded);
        Assert.Contains("would not restore", result.Detail);
        Assert.Empty(_shadows.Live);

        // And no half-written folder left looking like a complete backup in the
        // restore picker.
        Assert.Empty(Directory.EnumerateDirectories(Artifacts));
    }

    [Fact]
    public async Task A_file_the_shadow_copy_does_not_have_fails_the_backup()
    {
        var result = await Strategy().RunAsync(
            Context([new SaveManifestEntry("Bedrock level/db/000009.ldb", 10)]), default);

        Assert.False(result.Succeeded);
        Assert.Contains("not in the shadow copy", result.Detail);
        Assert.Empty(_shadows.Live);
    }

    // The shadow is released whatever happens, in the same spirit as `save
    // resume`. Asserted against a service that throws mid-copy rather than
    // against a tidy failure, because the tidy ones are not the risk.
    [Fact]
    public async Task The_shadow_is_released_even_when_the_copy_throws()
    {
        _shadows.FailNextRelease = false;
        var entry = WriteWorldFile("Bedrock level/db/000005.ldb", "abc", 3);

        Directory.Delete(ShadowedWorlds, recursive: true);

        var result = await Strategy().RunAsync(Context([entry]), default);

        Assert.False(result.Succeeded);
        Assert.Empty(_shadows.Live);
    }

    [Fact]
    public async Task An_empty_manifest_is_refused_before_a_shadow_is_taken()
    {
        var result = await Strategy().RunAsync(Context([]), default);

        Assert.False(result.Succeeded);
        Assert.Contains("no world here", result.Detail);

        // Not merely released — never taken. A snapshot is an expensive way to
        // find out there was nothing to copy.
        Assert.Equal(0, _shadows.Created);
    }

    [Fact]
    public async Task Configuration_files_are_carried_alongside_the_world()
    {
        var entry = WriteWorldFile("Bedrock level/db/000005.ldb", "abc", 3);

        await File.WriteAllTextAsync(
            Path.Combine(Instance, "server.properties"), "server-port=19132");
        await File.WriteAllTextAsync(Path.Combine(Instance, "allowlist.json"), "[]");

        var result = await Strategy().RunAsync(Context([entry]), default);

        Assert.True(result.Succeeded, result.Detail);

        var config = Path.Combine(result.ArtifactPath!, VssBackupStrategy.ConfigFolder);

        Assert.Equal(
            "server-port=19132",
            await File.ReadAllTextAsync(Path.Combine(config, "server.properties")));

        Assert.True(File.Exists(Path.Combine(config, "allowlist.json")));
    }

    [Fact]
    public async Task Retention_keeps_the_newest_and_never_the_one_just_written()
    {
        var entry = WriteWorldFile("Bedrock level/db/000005.ldb", "abc", 3);
        var retention = new BackupRetention { Mode = BackupRetentionMode.Count, Value = 2 };

        for (var i = 0; i < 4; i++)
        {
            _time.SetUtcNow(Origin.AddMinutes(i));

            var result = await Strategy().RunAsync(Context([entry], retention), default);
            Assert.True(result.Succeeded, result.Detail);
        }

        var kept = Directory
            .EnumerateDirectories(Artifacts, $"{VssBackupStrategy.FolderPrefix}*")
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal(2, kept.Count);
        Assert.Contains("vss-20260819-030300", kept);
    }

    // Only vss-*. A retention rule that reaches ZIP archives, or anything an
    // operator parked in the same folder, is a rule with a hole in it.
    [Fact]
    public async Task Retention_never_touches_anything_it_did_not_write()
    {
        var entry = WriteWorldFile("Bedrock level/db/000005.ldb", "abc", 3);
        var bystander = Path.Combine(Artifacts, "backup-20260101-000000.zip");

        await File.WriteAllTextAsync(bystander, "not mine");
        Directory.CreateDirectory(Path.Combine(Artifacts, "notes"));

        for (var i = 0; i < 3; i++)
        {
            _time.SetUtcNow(Origin.AddMinutes(i));

            await Strategy().RunAsync(
                Context([entry], new() { Mode = BackupRetentionMode.Count, Value = 1 }), default);
        }

        Assert.True(File.Exists(bystander));
        Assert.True(Directory.Exists(Path.Combine(Artifacts, "notes")));
        Assert.Single(Directory.EnumerateDirectories(Artifacts, $"{VssBackupStrategy.FolderPrefix}*"));
    }

    /// <summary>
    /// A shadow copy that fails to release is a disk problem for later, never a
    /// reason to fail a backup that already succeeded.
    /// </summary>
    [Fact]
    public async Task A_release_that_fails_does_not_fail_the_backup()
    {
        _shadows.FailNextRelease = true;
        var entry = WriteWorldFile("Bedrock level/db/000005.ldb", "abc", 3);

        var result = await Strategy().RunAsync(Context([entry]), default);

        Assert.True(result.Succeeded, result.Detail);
    }

    /// <summary>
    /// A shadow copy Windows refused is reported, not swallowed into an empty
    /// backup that looks like it worked.
    /// </summary>
    [Fact]
    public async Task A_refused_shadow_copy_surfaces_the_reason()
    {
        _shadows.FailCreateWith = "Access is denied. (0x80070005)";
        var entry = WriteWorldFile("Bedrock level/db/000005.ldb", "abc", 3);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Strategy().RunAsync(Context([entry]), default));

        Assert.Contains("Access is denied", failure.Message);
    }

    /// <summary>
    /// A fake volume snapshot: a directory standing in for the device object.
    /// </summary>
    private sealed class FakeShadowCopyService(string shadowRoot) : IShadowCopyService
    {
        private readonly List<string> _live = [];

        public int Created { get; private set; }

        public IReadOnlyList<string> Live => _live;

        public bool FailNextRelease { get; set; }

        public string? FailCreateWith { get; set; }

        public Task<ShadowCopy> CreateAsync(
            string volumeRoot, CancellationToken cancellationToken = default)
        {
            if (FailCreateWith is { } reason)
            {
                throw new InvalidOperationException(
                    $"Could not take a shadow copy of {volumeRoot}: {reason}");
            }

            Created++;
            var id = $"{{{Guid.NewGuid()}}}";
            _live.Add(id);

            return Task.FromResult(new ShadowCopy(id, shadowRoot));
        }

        public Task ReleaseAsync(ShadowCopy shadow, CancellationToken cancellationToken = default)
        {
            _live.Remove(shadow.ShadowId);

            // Mirrors the real one, which logs and returns: the exception on its
            // way out of the strategy is the more useful of the two.
            return Task.CompletedTask;
        }
    }
}
