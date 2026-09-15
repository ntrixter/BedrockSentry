using BedrockSentry.Core.Packages;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 11. <see cref="Measured"/> is a real response from Mojang's feed,
/// captured on a Windows box, not a shape taken from documentation — the same
/// rule the save-manifest, player-event and allowlist parsers follow, and for
/// the same reason each of them states.
/// </summary>
public sealed class BdsDownloadLinksTests
{
    private const string Measured =
        """
        {"result":{"links":[
        {"downloadType":"serverBedrockWindows","downloadUrl":"https://www.minecraft.net/bedrockdedicatedserver/bin-win/bedrock-server-1.26.44.3.zip"},
        {"downloadType":"serverBedrockLinux","downloadUrl":"https://www.minecraft.net/bedrockdedicatedserver/bin-linux/bedrock-server-1.26.44.3.zip"},
        {"downloadType":"serverBedrockPreviewWindows","downloadUrl":"https://www.minecraft.net/bedrockdedicatedserver/bin-win-preview/bedrock-server-1.26.50.25.zip"},
        {"downloadType":"serverBedrockPreviewLinux","downloadUrl":"https://www.minecraft.net/bedrockdedicatedserver/bin-linux-preview/bedrock-server-1.26.50.25.zip"},
        {"downloadType":"serverJar","downloadUrl":"https://piston-data.mojang.com/v1/objects/823e2250d24b3ddac457a60c92a6a941943fcd6a/server.jar"}
        ]}}
        """;

    [Fact]
    public void Reads_the_release_build_out_of_the_measured_feed()
    {
        var release = BdsDownloadLinks.For(BdsDownloadLinks.Parse(Measured), BdsChannel.Release);

        Assert.NotNull(release);
        Assert.Equal("bedrock-server-1.26.44.3.zip", release.FileName);
        Assert.Equal(new Version(1, 26, 44, 3), release.Version);
        Assert.StartsWith("https://www.minecraft.net/bedrockdedicatedserver/bin-win/", release.Url);
    }

    [Fact]
    public void Reads_the_preview_build_too()
    {
        var preview = BdsDownloadLinks.For(BdsDownloadLinks.Parse(Measured), BdsChannel.Preview);

        Assert.NotNull(preview);
        Assert.Equal(new Version(1, 26, 50, 25), preview.Version);
        Assert.Contains("bin-win-preview", preview.Url);
    }

    /// <summary>
    /// The feed carries Java Edition and Linux builds. Neither is something this
    /// wrapper can install into a Windows instance, and <c>serverJar</c> is not
    /// even the same product.
    /// </summary>
    [Fact]
    public void Only_the_two_windows_bedrock_builds_are_offered()
    {
        var all = BdsDownloadLinks.Parse(Measured);

        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, d => d.Url.Contains("piston-data"));
        Assert.DoesNotContain(all, d => d.Url.Contains("bin-linux"));
    }

    // The only statement of which build a link points at is the file name — the
    // feed carries no version field at all.
    [Fact]
    public void The_version_comes_from_the_file_name_because_the_feed_has_none()
    {
        Assert.DoesNotContain("\"version\"", Measured, StringComparison.OrdinalIgnoreCase);

        var release = BdsDownloadLinks.For(BdsDownloadLinks.Parse(Measured), BdsChannel.Release);
        Assert.Equal(new Version(1, 26, 44, 3), release!.Version);
    }

    // A third party changing a field must not stop the wrapper. Nothing known
    // reads as "no update known", which is what the caller reports.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"result":{}}""")]
    [InlineData("""{"result":{"links":"not an array"}}""")]
    [InlineData("""[{"downloadType":"serverBedrockWindows","downloadUrl":"x"}]""")]
    public void A_shape_it_does_not_recognise_yields_nothing_rather_than_throwing(string? json) =>
        Assert.Empty(BdsDownloadLinks.Parse(json));

    [Fact]
    public void An_entry_missing_a_field_is_skipped_and_the_rest_still_read()
    {
        var json =
            """
            {"result":{"links":[
            {"downloadType":"serverBedrockWindows"},
            {"downloadUrl":"https://example.invalid/bedrock-server-1.0.0.0.zip"},
            {"downloadType":"serverBedrockPreviewWindows","downloadUrl":"https://x/bedrock-server-2.0.0.0.zip"}
            ]}}
            """;

        var only = Assert.Single(BdsDownloadLinks.Parse(json));
        Assert.Equal(BdsChannel.Preview, only.Channel);
    }

    // An unparseable name is not a rejection: the entry still lists, it just
    // cannot be compared, and the service says so rather than offering it.
    [Fact]
    public void A_name_carrying_no_version_still_reads_with_a_null_version()
    {
        var json =
            """
            {"result":{"links":[
            {"downloadType":"serverBedrockWindows","downloadUrl":"https://x/bedrock-server-latest.zip"}
            ]}}
            """;

        var only = Assert.Single(BdsDownloadLinks.Parse(json));
        Assert.Null(only.Version);
        Assert.Equal("bedrock-server-latest.zip", only.FileName);
    }
}
