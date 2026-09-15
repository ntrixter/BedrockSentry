using System.Text.Json;
using BedrockSentry.Services.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 7.5. The fixtures are the measured files, byte shapes included:
/// permissions pretty-printed with a three-space indent and a space around the
/// colon, allowlist minified onto one CRLF-terminated line, and — the detail
/// the whole design turned on — the allowlist entry carrying no xuid.
/// </summary>
public sealed class ServerAccessFilesTests : IDisposable
{
    private const string MeasuredAllowlist =
        "[{\"ignoresPlayerLimit\":false,\"name\":\"SteveExample\"}]\r\n";

    private const string MeasuredPermissions =
        "[\r\n   {\r\n      \"permission\" : \"operator\",\r\n      \"xuid\" : \"2535000000000001\"\r\n   }\r\n]\r\n";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-access-{Guid.NewGuid():N}");

    public ServerAccessFilesTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string AllowlistPath => Path.Combine(_directory, AllowlistFile.FileName);
    private string PermissionsPath => Path.Combine(_directory, PermissionsFile.FileName);

    [Fact]
    public void Reads_the_measured_allowlist()
    {
        File.WriteAllText(AllowlistPath, MeasuredAllowlist);

        var entry = Assert.Single(AllowlistFile.Load(AllowlistPath).Entries);

        Assert.Equal("SteveExample", entry.Name);
        Assert.False(entry.IgnoresPlayerLimit);

        // The detail D31 turned on: no xuid until this player first joins.
        Assert.Null(entry.Xuid);
    }

    [Fact]
    public void Reads_the_measured_permissions()
    {
        File.WriteAllText(PermissionsPath, MeasuredPermissions);

        var file = PermissionsFile.Load(PermissionsPath);
        var entry = Assert.Single(file.Entries);

        Assert.Equal(PermissionTier.Operator, entry.Permission);
        Assert.Equal("2535000000000001", entry.Xuid);
        Assert.Equal(PermissionTier.Operator, file.TierOf("2535000000000001"));
        Assert.Null(file.TierOf("999"));
    }

    [Fact]
    public void A_missing_file_is_an_empty_list_not_an_error()
    {
        Assert.Empty(AllowlistFile.Load(AllowlistPath).Entries);
        Assert.Empty(PermissionsFile.Load(PermissionsPath).Entries);
    }

    // Access control is the wrong place to guess. A file that does not parse is
    // reported, not treated as empty — "empty" would render as "nobody is
    // allowlisted" over a list that exists.
    [Fact]
    public void A_malformed_file_throws_rather_than_reading_as_empty()
    {
        File.WriteAllText(AllowlistPath, "{\"not\":\"an array\"}");
        Assert.Throws<JsonException>(() => AllowlistFile.Load(AllowlistPath));

        File.WriteAllText(PermissionsPath, "truncated [");
        Assert.ThrowsAny<JsonException>(() => PermissionsFile.Load(PermissionsPath));
    }

    [Fact]
    public void Allowlist_writes_the_shape_bds_writes()
    {
        var file = AllowlistFile.Load(AllowlistPath);
        Assert.True(file.Add("SteveExample"));
        file.Save(AllowlistPath);

        // Minified, one line, CRLF-terminated, ignoresPlayerLimit first — the
        // measured file exactly, because this entry shape has no unknowns.
        Assert.Equal(MeasuredAllowlist, File.ReadAllText(AllowlistPath));
    }

    [Fact]
    public void Permissions_writes_three_space_indent_and_crlf()
    {
        var file = PermissionsFile.Load(PermissionsPath);
        file.Set("2535000000000001", PermissionTier.Operator);
        file.Save(PermissionsPath);

        var text = File.ReadAllText(PermissionsPath);

        Assert.Contains("\r\n   {", text);
        Assert.Contains("\"permission\"", text);
        Assert.EndsWith("]\r\n", text);

        // And it reads back as itself.
        Assert.Equal(
            PermissionTier.Operator,
            PermissionsFile.Load(PermissionsPath).TierOf("2535000000000001"));
    }

    // The reason both classes are node-based: a field this code has never heard
    // of must survive being edited around. Dropping one is the silent-overwrite
    // failure the measurements were taken to prevent.
    [Fact]
    public void Fields_the_wrapper_does_not_understand_survive_a_round_trip()
    {
        File.WriteAllText(
            AllowlistPath,
            "[{\"ignoresPlayerLimit\":true,\"name\":\"Steve\",\"xuid\":\"123\",\"futureField\":42}]");

        var file = AllowlistFile.Load(AllowlistPath);
        file.Add("Alex");
        file.Save(AllowlistPath);

        var text = File.ReadAllText(AllowlistPath);
        Assert.Contains("\"futureField\":42", text);
        Assert.Contains("\"xuid\":\"123\"", text);
        Assert.Contains("Alex", text);
    }

    [Fact]
    public void Adding_an_existing_name_is_not_an_edit()
    {
        var file = AllowlistFile.Load(AllowlistPath);
        Assert.True(file.Add("Steve"));

        // Gamertags are case-insensitive; the same person must not be listed twice.
        Assert.False(file.Add("steve"));
        Assert.Single(file.Entries);
    }

    [Fact]
    public void Remove_matches_the_name_case_insensitively()
    {
        var file = AllowlistFile.Load(AllowlistPath);
        file.Add("Some Player 99");

        Assert.True(file.Remove("some player 99"));
        Assert.Empty(file.Entries);
        Assert.False(file.Remove("some player 99"));
    }

    [Fact]
    public void Set_updates_in_place_rather_than_duplicating()
    {
        var file = PermissionsFile.Load(PermissionsPath);
        file.Set("111", PermissionTier.Operator);
        file.Set("111", PermissionTier.Member);

        var entry = Assert.Single(file.Entries);
        Assert.Equal(PermissionTier.Member, entry.Permission);
    }

    // Remove means "back to the server default", which is a different act from
    // setting member — the class doc says why. Both must work.
    [Fact]
    public void Removing_an_override_leaves_the_rest_alone()
    {
        var file = PermissionsFile.Load(PermissionsPath);
        file.Set("111", PermissionTier.Operator);
        file.Set("222", PermissionTier.Visitor);

        Assert.True(file.Remove("111"));

        var entry = Assert.Single(file.Entries);
        Assert.Equal("222", entry.Xuid);
        Assert.Equal(PermissionTier.Visitor, entry.Permission);
    }

    // An unrecognised tier in somebody's file is displayed and preserved, never
    // mapped to a default. The file outranks this code's vocabulary.
    [Fact]
    public void An_unknown_tier_is_preserved_not_normalised()
    {
        File.WriteAllText(
            PermissionsPath,
            "[{\"permission\":\"custom-future-tier\",\"xuid\":\"111\"}]");

        var file = PermissionsFile.Load(PermissionsPath);
        Assert.Equal("custom-future-tier", file.TierOf("111"));

        file.Set("222", PermissionTier.Member);
        file.Save(PermissionsPath);

        Assert.Contains("custom-future-tier", File.ReadAllText(PermissionsPath));
    }

    // T153-RetickDropsJoinIfFull. Untick a player's allowlist box and tick it
    // again and their "Join if full" is gone. The flag lives on the entry and
    // the entry is gone, so under D31 - the file is the source of truth - it
    // genuinely cannot survive the round trip. What was wrong was the silence.

    [Fact]
    public void The_join_if_full_flag_can_be_read_before_the_entry_is_removed()
    {
        File.WriteAllText(
            AllowlistPath,
            "[{\"ignoresPlayerLimit\":true,\"name\":\"SteveExample\"},"
            + "{\"ignoresPlayerLimit\":false,\"name\":\"AlexExample\"}]\r\n");

        var file = AllowlistFile.Load(AllowlistPath);

        Assert.True(file.IgnoresPlayerLimit("SteveExample"));
        Assert.False(file.IgnoresPlayerLimit("AlexExample"));

        // Gamertags are case-insensitive everywhere else here; so is this.
        Assert.True(file.IgnoresPlayerLimit("steveexample"));

        // Not listed is not an exemption.
        Assert.False(file.IgnoresPlayerLimit("NobodyExample"));
    }

    [Fact]
    public void Removing_an_entry_that_could_join_a_full_server_says_what_it_cleared()
    {
        Assert.Equal(
            "SteveExample removed from the allowlist. Their 'Join if full' went with "
            + "the entry — adding them back starts it off again.",
            ServerAccessControl.RemovalDetail("SteveExample", clearedIgnoresPlayerLimit: true));
    }

    [Fact]
    public void Removing_an_ordinary_entry_does_not_mention_a_flag_it_never_had()
    {
        Assert.Equal(
            "AlexExample removed from the allowlist.",
            ServerAccessControl.RemovalDetail("AlexExample", clearedIgnoresPlayerLimit: false));
    }
}
