using System.Text.Json;
using Microsoft.Extensions.Configuration;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 4.3: the space-hungry folders are relocatable, live. What has to
/// hold is that a change lands in the config file and in the very next
/// operation at the same time — a path the file holds but the wrapper ignores,
/// or the reverse, is a setting that silently reverts at the next restart.
/// </summary>
public sealed class StorageLocationsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"bs-storage-{Guid.NewGuid():N}");

    private readonly SentryPaths _paths;
    private readonly StorageLocations _storage;

    public StorageLocationsTests()
    {
        _paths = new SentryPaths(Path.Combine(_root, "data"));
        Directory.CreateDirectory(_paths.Config);
        _storage = new StorageLocations(_paths, NullLogger<StorageLocations>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string ConfigPath => Path.Combine(_paths.Config, "bedrocksentry.json");

    [Fact]
    public async Task Moving_the_backups_root_takes_effect_on_the_next_operation()
    {
        var elsewhere = Path.Combine(_root, "volume-d", "backups");

        await _storage.SetBackupsRootAsync(elsewhere);

        Assert.Equal(Path.GetFullPath(elsewhere), _paths.Backups);

        // The consumer that matters resolves per call, which is what makes the
        // change apply with no restart: the next backup simply lands here.
        var history = new BackupHistory(_paths);
        Assert.StartsWith(
            Path.GetFullPath(elsewhere), history.DirectoryFor("survival"), StringComparison.Ordinal);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        Assert.Equal(
            Path.GetFullPath(elsewhere),
            document.RootElement.GetProperty("Sentry").GetProperty("Storage")
                .GetProperty("BackupsRoot").GetString());
    }

    [Fact]
    public async Task Moving_the_packages_root_leaves_the_backups_root_alone()
    {
        var backups = Path.Combine(_root, "volume-d", "backups");
        var packages = Path.Combine(_root, "volume-e", "zips");

        await _storage.SetBackupsRootAsync(backups);
        await _storage.SetServerPackagesRootAsync(packages);

        // Two independent settings behind one apply call — the classic way to
        // lose one is for the other's save to reset it.
        Assert.Equal(Path.GetFullPath(backups), _paths.Backups);
        Assert.Equal(Path.GetFullPath(packages), _paths.ServerPackages);
    }

    [Fact]
    public async Task Blank_returns_to_the_default()
    {
        await _storage.SetBackupsRootAsync(Path.Combine(_root, "volume-d", "backups"));
        await _storage.SetBackupsRootAsync("   ");

        Assert.Equal(_storage.DefaultBackupsRoot, _paths.Backups);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        Assert.Equal(
            "",
            document.RootElement.GetProperty("Sentry").GetProperty("Storage")
                .GetProperty("BackupsRoot").GetString());
    }

    [Fact]
    public async Task A_relative_path_is_refused_and_changes_nothing()
    {
        var before = _paths.Backups;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _storage.SetBackupsRootAsync("volume-d/backups"));

        Assert.Equal(before, _paths.Backups);
        Assert.False(File.Exists(ConfigPath));
    }

    /// <summary>The boot half of the round trip: what was saved is what binds back.</summary>
    [Fact]
    public async Task What_the_page_writes_is_what_the_next_start_applies()
    {
        var elsewhere = Path.Combine(_root, "volume-d", "backups");
        await _storage.SetBackupsRootAsync(elsewhere);

        var bound = new ConfigurationBuilder()
            .AddJsonFile(ConfigPath)
            .Build()
            .GetSection(Core.Configuration.StorageOptions.SectionName)
            .Get<Core.Configuration.StorageOptions>()!;

        var fresh = new SentryPaths(Path.Combine(_root, "data"));
        fresh.ApplyStorageOverrides(bound.BackupsRoot, bound.ServerPackagesRoot);

        Assert.Equal(Path.GetFullPath(elsewhere), fresh.Backups);
        Assert.Equal(fresh.DataRoot + Path.DirectorySeparatorChar + "server-packages",
            fresh.ServerPackages);
    }
}
