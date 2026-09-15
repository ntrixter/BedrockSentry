using System.Text.Json;
using System.Text.Json.Nodes;
using BedrockSentry.Services.Configuration;

namespace BedrockSentry.Tests;

/// <summary>
/// Covers the config writer's core promise: it changes what it was asked to change
/// and leaves everything else exactly as it found it.
/// </summary>
/// <remarks>
/// This is the file the first-run wizard writes credentials into, and the one an
/// administrator hand-edits. Both of those break badly if a write silently discards
/// keys, so the preservation cases matter more than the happy path.
/// </remarks>
public sealed class JsonConfigWriterTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("bedrocksentry-tests-").FullName;

    private string PathFor(string name) => Path.Combine(_directory, name);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Creates_the_file_when_it_does_not_exist()
    {
        var path = PathFor("new.json");

        await JsonConfigWriter.MergeAsync(path, new Dictionary<string, JsonNode?>
        {
            ["Sentry:Admin:Username"] = JsonValue.Create("steve"),
        });

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        Assert.Equal(
            "steve",
            document.RootElement.GetProperty("Sentry").GetProperty("Admin").GetProperty("Username").GetString());
    }

    [Fact]
    public async Task Preserves_keys_it_was_not_asked_to_change()
    {
        var path = PathFor("existing.json");

        await File.WriteAllTextAsync(path, """
            {
              "Sentry": {
                "Admin": { "Username": "old", "PasswordHash": "hash" },
                "Tls": { "Port": 9443 }
              },
              "SomethingAnOperatorAdded": true
            }
            """);

        await JsonConfigWriter.MergeAsync(path, new Dictionary<string, JsonNode?>
        {
            ["Sentry:Admin:Username"] = JsonValue.Create("new"),
        });

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = document.RootElement;

        Assert.Equal("new", root.GetProperty("Sentry").GetProperty("Admin").GetProperty("Username").GetString());

        // A POCO round-trip would have dropped both of these.
        Assert.Equal("hash", root.GetProperty("Sentry").GetProperty("Admin").GetProperty("PasswordHash").GetString());
        Assert.Equal(9443, root.GetProperty("Sentry").GetProperty("Tls").GetProperty("Port").GetInt32());
        Assert.True(root.GetProperty("SomethingAnOperatorAdded").GetBoolean());
    }

    [Fact]
    public async Task Creates_intermediate_objects_for_a_deep_path()
    {
        var path = PathFor("shallow.json");

        await File.WriteAllTextAsync(path, """{ "Unrelated": 1 }""");

        await JsonConfigWriter.MergeAsync(path, new Dictionary<string, JsonNode?>
        {
            ["Sentry:Update:Channel"] = JsonValue.Create("Build"),
        });

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        Assert.Equal(
            "Build",
            document.RootElement.GetProperty("Sentry").GetProperty("Update").GetProperty("Channel").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("Unrelated").GetInt32());
    }

    [Fact]
    public async Task Replaces_a_scalar_standing_where_an_object_is_needed()
    {
        var path = PathFor("conflict.json");

        await File.WriteAllTextAsync(path, """{ "Sentry": "not-an-object" }""");

        await JsonConfigWriter.MergeAsync(path, new Dictionary<string, JsonNode?>
        {
            ["Sentry:Admin:Username"] = JsonValue.Create("steve"),
        });

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        Assert.Equal(
            "steve",
            document.RootElement.GetProperty("Sentry").GetProperty("Admin").GetProperty("Username").GetString());
    }

    [Fact]
    public async Task Refuses_to_overwrite_a_malformed_file()
    {
        var path = PathFor("broken.json");
        const string original = "{ this is not json";

        await File.WriteAllTextAsync(path, original);

        await Assert.ThrowsAnyAsync<Exception>(() => JsonConfigWriter.MergeAsync(
            path,
            new Dictionary<string, JsonNode?> { ["Sentry:Admin:Username"] = JsonValue.Create("steve") }));

        // Starting fresh on a parse failure would quietly destroy whatever the
        // operator was halfway through editing.
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Leaves_no_temporary_files_behind()
    {
        var path = PathFor("clean.json");

        await JsonConfigWriter.MergeAsync(path, new Dictionary<string, JsonNode?>
        {
            ["A:B"] = JsonValue.Create(1),
        });

        Assert.Equal(new[] { "clean.json" }, Directory.GetFiles(_directory).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task Writes_a_null_when_asked_to()
    {
        var path = PathFor("nulls.json");

        await File.WriteAllTextAsync(path, """{ "Sentry": { "Admin": { "Username": "steve" } } }""");

        await JsonConfigWriter.MergeAsync(path, new Dictionary<string, JsonNode?>
        {
            ["Sentry:Admin:Username"] = null,
        });

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("Sentry").GetProperty("Admin").GetProperty("Username").ValueKind);
    }
}
