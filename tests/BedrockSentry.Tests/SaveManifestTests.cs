using BedrockSentry.Core.Backups;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 5.5. The accepted lines are the measured shape; the rejected ones
/// are the neighbours it must not match — the marker sentence, list replies,
/// and ordinary text that happens to contain a colon and a number.
/// </summary>
public sealed class SaveManifestTests
{
    [Fact]
    public void Reads_the_measured_manifest_line()
    {
        var line =
            "Bedrock level/db/000005.ldb:509, Bedrock level/db/CURRENT:16, "
            + "Bedrock level/level.dat:2543, Bedrock level/levelname.txt:13";

        Assert.True(SaveManifest.TryParse(line, out var entries));
        Assert.Equal(4, entries.Count);
        Assert.Equal("Bedrock level/db/000005.ldb", entries[0].RelativePath);
        Assert.Equal(509, entries[0].Length);
        Assert.Equal("Bedrock level/levelname.txt", entries[3].RelativePath);
        Assert.Equal(13, entries[3].Length);
    }

    // The world folder name can carry spaces ("Bedrock level"), and paths keep
    // them — the split is on ", " between entries, never on whitespace.
    [Fact]
    public void A_single_entry_manifest_parses()
    {
        Assert.True(SaveManifest.TryParse("My World/level.dat:2543", out var entries));
        Assert.Equal("My World/level.dat", Assert.Single(entries).RelativePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Data saved. Files are now ready to be copied.")]
    [InlineData("Saving...")]
    [InlineData("Changes to the world are resumed.")]
    [InlineData("There are 0/10 players online:")]
    [InlineData("Difficulty: 1 EASY")]
    [InlineData("score: 5, time: 3")]
    [InlineData("Bedrock level/level.dat")]
    [InlineData("Bedrock level/level.dat:")]
    [InlineData("Bedrock level/level.dat:abc")]
    [InlineData("Bedrock level/level.dat:-1")]
    public void Everything_else_is_left_alone(string? line)
    {
        Assert.False(SaveManifest.TryParse(line, out _));
    }

    // One bad segment poisons the line: half a manifest copied is a corrupt
    // restore, so it is all or nothing.
    [Fact]
    public void A_line_with_one_malformed_segment_is_rejected_whole()
    {
        Assert.False(SaveManifest.TryParse(
            "Bedrock level/db/CURRENT:16, not a manifest entry", out _));
    }
}
