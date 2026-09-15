using System.Text.Json;
using BedrockSentry.Services.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 13. The tests that matter most are the ones about what does
/// <em>not</em> come out: this file is meant to be copied between machines and
/// emailed around, and the configuration it is drawn from holds a password hash
/// and a certificate password.
/// </summary>
public sealed class ConfigArchiveTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("bedrocksentry-cfgarchive-").FullName;

    private readonly ConfigArchive _archive;

    public ConfigArchiveTests()
    {
        var paths = new SentryPaths(_root);
        Directory.CreateDirectory(paths.Config);
        _archive = new ConfigArchive(paths, NullLogger<ConfigArchive>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string ConfigPath => Path.Combine(_root, "config", "bedrocksentry.json");

    private Task WriteConfigAsync(string json) => File.WriteAllTextAsync(ConfigPath, json);

    /// <summary>A configuration with something of everything in it, secrets included.</summary>
    private Task WriteATypicalInstallAsync() => WriteConfigAsync("""
        {
          "Sentry": {
            "Admin": {
              "Username": "steve",
              "PasswordHash": "AQAAAAIAAYagAAAAEK-this-is-the-hash",
              "SecurityStamp": "stamp-value"
            },
            "Tls": {
              "Source": "PfxFile",
              "Port": 8443,
              "PfxPath": "C:\\certs\\sentry.pfx",
              "PfxPassword": "the-certificate-password"
            },
            "BdsUpdate": {
              "CheckForUpdates": true,
              "CheckSchedule": { "Mode": "DailyAt", "TimeOfDay": "04:30" }
            },
            "Update": { "Channel": "build", "Mode": "Notify" },
            "Instances": {
              "Root": "D:\\servers",
              "Servers": {
                "survival": {
                  "Name": "Survival",
                  "ServerUpdate": { "Mode": "Automatic", "AutoApplyDelayMinutes": 45 },
                  "Backup": { "Zip": { "Enabled": true } }
                },
                "creative": { "Name": "Creative" }
              }
            }
          }
        }
        """);

    private async Task<JsonElement> ExportAsync()
    {
        var json = await _archive.ExportAsync(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// The one that would be an incident rather than a bug. Asserted on the raw
    /// text as well as the tree, because a secret nested somewhere unexpected
    /// still counts as having left the building.
    /// </summary>
    [Fact]
    public async Task No_secret_ever_leaves_in_an_export()
    {
        await WriteATypicalInstallAsync();

        var json = await _archive.ExportAsync(DateTimeOffset.UtcNow);

        Assert.DoesNotContain("PasswordHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("this-is-the-hash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SecurityStamp", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the-certificate-password", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PfxPassword", json, StringComparison.OrdinalIgnoreCase);

        // The whole sections, not just their secret leaves.
        Assert.DoesNotContain("Sentry:Admin", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Sentry:Tls", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_export_carries_the_settings_worth_moving()
    {
        await WriteATypicalInstallAsync();

        var config = (await ExportAsync()).GetProperty("config");

        var instances = config.GetProperty("Sentry:Instances");
        Assert.Equal("D:\\servers", instances.GetProperty("Root").GetString());

        var survival = instances.GetProperty("Servers").GetProperty("survival");
        Assert.Equal("Survival", survival.GetProperty("Name").GetString());
        Assert.Equal(45, survival.GetProperty("ServerUpdate").GetProperty("AutoApplyDelayMinutes").GetInt32());
        Assert.True(survival.GetProperty("Backup").GetProperty("Zip").GetProperty("Enabled").GetBoolean());

        Assert.Equal(
            "DailyAt",
            config.GetProperty("Sentry:BdsUpdate").GetProperty("CheckSchedule")
                .GetProperty("Mode").GetString());

        Assert.Equal("build", config.GetProperty("Sentry:Update").GetProperty("Channel").GetString());
    }

    [Fact]
    public async Task An_export_says_what_it_is_and_when_it_was_taken()
    {
        await WriteATypicalInstallAsync();

        var root = await ExportAsync();

        Assert.Equal(ConfigArchive.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Contains("2026-08-17", root.GetProperty("exportedUtc").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("wrapperVersion").GetString()));
    }

    [Fact]
    public async Task A_fresh_install_with_no_config_file_still_exports()
    {
        var root = await ExportAsync();

        Assert.Equal(ConfigArchive.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(root.GetProperty("config").EnumerateObject());
    }

    [Fact]
    public async Task An_import_round_trips_onto_an_empty_install()
    {
        await WriteATypicalInstallAsync();
        var exported = await _archive.ExportAsync(DateTimeOffset.UtcNow);

        await WriteConfigAsync("""{ "Sentry": { "Admin": { "Username": "someone-else" } } }""");

        var result = await _archive.ImportAsync(exported);

        Assert.Equal(["survival", "creative"], result.Instances.Order().Reverse());
        Assert.Equal(2, result.NewInstances.Count);
        Assert.True(result.NeedsRestart);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        var sentry = document.RootElement.GetProperty("Sentry");

        Assert.Equal(
            "Survival",
            sentry.GetProperty("Instances").GetProperty("Servers").GetProperty("survival")
                .GetProperty("Name").GetString());

        // The login on the receiving machine is untouched — the file could not
        // carry one, and a restore re-sets it rather than inheriting one.
        Assert.Equal("someone-else", sentry.GetProperty("Admin").GetProperty("Username").GetString());
    }

    /// <summary>
    /// Merge, not replace. A file older or narrower than this machine is far
    /// more likely than somebody deleting a server by leaving it out.
    /// </summary>
    [Fact]
    public async Task An_instance_the_file_does_not_mention_survives_the_import()
    {
        await WriteConfigAsync("""
            {
              "Sentry": {
                "Instances": { "Servers": { "hardcore": { "Name": "Hardcore" } } }
              }
            }
            """);

        await _archive.ImportAsync("""
            {
              "schemaVersion": 1,
              "config": {
                "Sentry:Instances": { "Servers": { "survival": { "Name": "Survival" } } }
              }
            }
            """);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        var servers = document.RootElement.GetProperty("Sentry")
            .GetProperty("Instances").GetProperty("Servers");

        Assert.Equal("Hardcore", servers.GetProperty("hardcore").GetProperty("Name").GetString());
        Assert.Equal("Survival", servers.GetProperty("survival").GetProperty("Name").GetString());
    }

    /// <summary>
    /// An instance the file <em>does</em> carry is replaced whole, so a setting
    /// removed since the export does not survive inside it.
    /// </summary>
    [Fact]
    public async Task An_instance_the_file_carries_is_replaced_rather_than_blended()
    {
        await WriteConfigAsync("""
            {
              "Sentry": {
                "Instances": {
                  "Servers": {
                    "survival": { "Name": "Survival", "Retired": true, "AutoStart": false }
                  }
                }
              }
            }
            """);

        await _archive.ImportAsync("""
            {
              "schemaVersion": 1,
              "config": {
                "Sentry:Instances": { "Servers": { "survival": { "Name": "Survival" } } }
              }
            }
            """);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        var survival = document.RootElement.GetProperty("Sentry")
            .GetProperty("Instances").GetProperty("Servers").GetProperty("survival");

        Assert.False(survival.TryGetProperty("Retired", out _));
        Assert.False(survival.TryGetProperty("AutoStart", out _));
    }

    /// <summary>
    /// The allow-list has to hold on the way in as well as out. An import is a
    /// file from somewhere else, and an edited one must not be able to write a
    /// login.
    /// </summary>
    [Fact]
    public async Task A_doctored_file_cannot_write_a_section_that_is_not_imported()
    {
        await WriteConfigAsync("""{ "Sentry": { "Admin": { "Username": "steve" } } }""");

        await _archive.ImportAsync("""
            {
              "schemaVersion": 1,
              "config": {
                "Sentry:Admin": { "Username": "attacker", "PasswordHash": "planted" },
                "Sentry:Tls": { "PfxPassword": "planted" },
                "Sentry:Update": { "Channel": "prod" }
              }
            }
            """);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        var sentry = document.RootElement.GetProperty("Sentry");

        Assert.Equal("steve", sentry.GetProperty("Admin").GetProperty("Username").GetString());
        Assert.False(sentry.GetProperty("Admin").TryGetProperty("PasswordHash", out _));
        Assert.False(sentry.TryGetProperty("Tls", out _));

        // And the section that *is* imported still landed.
        Assert.Equal("prod", sentry.GetProperty("Update").GetProperty("Channel").GetString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "schemaVersion": 1 }""")]
    [InlineData("""{ "schemaVersion": 99, "config": {} }""")]
    [InlineData("""{ "schemaVersion": 0, "config": {} }""")]
    [InlineData("""{ "schemaVersion": 1, "config": { "Something:Else": {} } }""")]
    public async Task A_file_that_is_not_an_export_is_refused_with_a_reason(string json)
    {
        await WriteATypicalInstallAsync();
        var before = await File.ReadAllTextAsync(ConfigPath);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _archive.ImportAsync(json));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));

        // Refused before anything was written, not halfway through.
        Assert.Equal(before, await File.ReadAllTextAsync(ConfigPath));
    }

    [Fact]
    public async Task A_preview_says_what_an_import_would_do_without_doing_it()
    {
        await WriteConfigAsync("""
            {
              "Sentry": {
                "Instances": { "Servers": { "survival": { "Name": "Survival" } } }
              }
            }
            """);

        var before = await File.ReadAllTextAsync(ConfigPath);

        var preview = await _archive.PreviewAsync("""
            {
              "schemaVersion": 1,
              "config": {
                "Sentry:Instances": {
                  "Servers": { "survival": { "Name": "Survival" }, "creative": { "Name": "Creative" } }
                },
                "Sentry:Update": { "Channel": "prod" }
              }
            }
            """);

        Assert.Equal(2, preview.Instances.Count);

        // "New" means new here, which is the set that needs a service restart —
        // not everything the file happens to contain.
        Assert.Equal(["creative"], preview.NewInstances);
        Assert.Contains("Sentry:Update", preview.Sections);

        Assert.Equal(before, await File.ReadAllTextAsync(ConfigPath));
    }

    // The writer's whole promise, exercised through this caller: a hand-added
    // key nobody models must survive an import.
    [Fact]
    public async Task A_hand_added_key_elsewhere_in_the_file_survives()
    {
        await WriteConfigAsync("""
            {
              "Logging": { "LogLevel": { "Default": "Debug" } },
              "Sentry": { "SomethingFromANewerBuild": { "Keep": "me" } }
            }
            """);

        await _archive.ImportAsync("""
            {"schemaVersion":1,"config":{"Sentry:Update":{"Channel":"prod"}}}
            """);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));

        Assert.Equal(
            "Debug",
            document.RootElement.GetProperty("Logging").GetProperty("LogLevel")
                .GetProperty("Default").GetString());

        Assert.Equal(
            "me",
            document.RootElement.GetProperty("Sentry").GetProperty("SomethingFromANewerBuild")
                .GetProperty("Keep").GetString());
    }
}
