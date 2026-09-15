using System.IO.Compression;
using BedrockSentry.Core.Backups;
using BedrockSentry.Services.Backups;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 8.3. The reserved prefix is the whole design: configuration has to
/// travel in the same archive as the world without ever being mistaken for a
/// world folder, and an archive written before any of this existed has to keep
/// restoring exactly as it did.
/// </summary>
public class ServerConfigFileNameTests
{
    [Fact]
    public void An_entry_name_round_trips_to_the_file_it_names()
    {
        var entry = ServerConfigFiles.EntryFor("server.properties");

        Assert.True(ServerConfigFiles.IsConfigEntry(entry));
        Assert.Equal("server.properties", ServerConfigFiles.FileNameOf(entry));
    }

    // The prefix exists so an entry at the archive root is unambiguously a path
    // inside a world. If a world entry ever read as configuration, restoring
    // would write somebody's level data into the instance root.
    [Theory]
    [InlineData("Bedrock level/level.dat")]
    [InlineData("server.properties")]
    [InlineData("Bedrock level/db/CURRENT")]
    public void A_world_entry_is_never_configuration(string entryName)
    {
        Assert.False(ServerConfigFiles.IsConfigEntry(entryName));
        Assert.Null(ServerConfigFiles.FileNameOf(entryName));
    }

    // These are four flat files by definition, so anything with structure in
    // this namespace was not written by the wrapper — and the traversal cases
    // are the reason the check is not just "strip the prefix".
    [Theory]
    [InlineData("_serverconfig/")]
    [InlineData("_serverconfig/../../evil.txt")]
    [InlineData("_serverconfig/..")]
    [InlineData("_serverconfig/nested/file.json")]
    [InlineData("_serverconfig/nested\\file.json")]
    public void Anything_nested_or_climbing_is_refused(string entryName)
    {
        Assert.Null(ServerConfigFiles.FileNameOf(entryName));
    }

    [Fact]
    public void Present_lists_only_the_files_that_are_there()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bs-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "server.properties"), "server-port=19132");
            File.WriteAllText(Path.Combine(directory, "allowlist.json"), "[]");
            File.WriteAllText(Path.Combine(directory, "bedrock_server.exe"), "not config");

            Assert.Equal(
                ["server.properties", "allowlist.json"],
                ServerConfigFiles.Present(directory).Select(f => f.Name));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}

public sealed class ConfigInBackupTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-cfgzip-{Guid.NewGuid():N}");

    private readonly FakeTimeProvider _time =
        new(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));

    public ConfigInBackupTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private void WriteWorldFile(string relativePath, string content)
    {
        var path = Path.Combine(_directory, "worlds", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteInstanceFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_directory, name), content);

    private BackupContext Context(params SaveManifestEntry[] manifest)
    {
        var artifacts = Path.Combine(_directory, "artifacts");
        Directory.CreateDirectory(artifacts);

        return new BackupContext(
            "survival", "Survival",
            _directory,
            Path.Combine(_directory, "worlds"),
            manifest,
            artifacts,
            new BackupSettings { Zip = new() { Enabled = true, Retention = new() } },
            BackupReason.Manual,
            null,
            _time.GetUtcNow());
    }

    private Task<BackupStrategyResult> RunAsync(params SaveManifestEntry[] manifest) =>
        new ZipBackupStrategy(_time, NullLogger<ZipBackupStrategy>.Instance)
            .RunAsync(Context(manifest), CancellationToken.None);

    [Fact]
    public async Task The_archive_carries_the_servers_own_configuration()
    {
        WriteWorldFile("Bedrock level/level.dat", "world!");
        WriteInstanceFile("server.properties", "server-port=19134");
        WriteInstanceFile("allowlist.json", """[{"name":"Steve"}]""");
        WriteInstanceFile("permissions.json", """[{"permission":"operator"}]""");

        var result = await RunAsync(new SaveManifestEntry("Bedrock level/level.dat", 6));

        Assert.True(result.Succeeded, result.Detail);
        Assert.Contains("3 config files", result.Detail);

        using var zip = ZipFile.OpenRead(result.ArtifactPath!);

        using var properties = new StreamReader(
            zip.GetEntry("_serverconfig/server.properties")!.Open());
        Assert.Equal("server-port=19134", properties.ReadToEnd());

        using var allowlist = new StreamReader(zip.GetEntry("_serverconfig/allowlist.json")!.Open());
        Assert.Contains("Steve", allowlist.ReadToEnd());

        Assert.NotNull(zip.GetEntry("_serverconfig/permissions.json"));
    }

    // Configuration is not part of the save, so `save hold` says nothing about
    // it and there is no manifest length to honour — but it must never displace
    // a world entry or change what the world half of the archive contains.
    [Fact]
    public async Task Configuration_is_added_beside_the_world_never_instead_of_it()
    {
        WriteWorldFile("Bedrock level/level.dat", "world!extra");
        WriteInstanceFile("server.properties", "server-port=19132");

        var result = await RunAsync(new SaveManifestEntry("Bedrock level/level.dat", 6));

        Assert.True(result.Succeeded, result.Detail);

        using var zip = ZipFile.OpenRead(result.ArtifactPath!);
        Assert.Equal(2, zip.Entries.Count);

        // Still truncated at the manifest length: the tail belongs to the next
        // save, and adding configuration must not have touched that rule.
        using var reader = new StreamReader(zip.GetEntry("Bedrock level/level.dat")!.Open());
        Assert.Equal("world!", reader.ReadToEnd());
    }

    [Fact]
    public async Task An_instance_with_no_configuration_files_still_backs_up()
    {
        WriteWorldFile("Bedrock level/level.dat", "world!");

        var result = await RunAsync(new SaveManifestEntry("Bedrock level/level.dat", 6));

        Assert.True(result.Succeeded, result.Detail);
        Assert.DoesNotContain("config file", result.Detail);

        using var zip = ZipFile.OpenRead(result.ArtifactPath!);
        Assert.Single(zip.Entries);
    }

    /// <summary>
    /// BDS rewrites <c>allowlist.json</c> and <c>permissions.json</c> whenever
    /// somebody changes them in game, so it may well be holding one open as the
    /// backup runs — the same Windows sharing rule that broke world archiving.
    /// This assertion has teeth on Windows, where CI runs.
    /// </summary>
    [Fact]
    public async Task Configuration_the_server_still_holds_open_is_archived_anyway()
    {
        WriteWorldFile("Bedrock level/level.dat", "world!");
        WriteInstanceFile("allowlist.json", """[{"name":"Steve"}]""");

        await using var heldByTheServer = new FileStream(
            Path.Combine(_directory, "allowlist.json"),
            FileMode.Open, FileAccess.Write, FileShare.Read);

        var result = await RunAsync(new SaveManifestEntry("Bedrock level/level.dat", 6));

        Assert.True(result.Succeeded, result.Detail);

        using var zip = ZipFile.OpenRead(result.ArtifactPath!);
        Assert.NotNull(zip.GetEntry("_serverconfig/allowlist.json"));
    }

    // A configuration file that cannot be read is a note in the log, never a
    // failed backup: a world with no server.properties beats no world at all.
    [Fact]
    public async Task An_unreadable_configuration_file_does_not_fail_the_archive()
    {
        WriteWorldFile("Bedrock level/level.dat", "world!");

        // A directory where a file is expected: File.Exists is false, so this
        // is skipped rather than raising — the same shape as a file that
        // vanished between the listing and the read.
        Directory.CreateDirectory(Path.Combine(_directory, "whitelist.json"));
        WriteInstanceFile("server.properties", "server-port=19132");

        var result = await RunAsync(new SaveManifestEntry("Bedrock level/level.dat", 6));

        Assert.True(result.Succeeded, result.Detail);
        Assert.Contains("1 config file", result.Detail);
    }
}
