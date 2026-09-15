using BedrockSentry.Core;

namespace BedrockSentry.Tests;

/// <summary>
/// Covers how a build identifies itself to the self-update mechanism.
/// </summary>
/// <remarks>
/// Getting this wrong is quiet and expensive: a commit misread as absent means the
/// updater compares nothing useful and never sees a new build, and a version string
/// misread as a commit means it compares a value that never changes. Neither
/// produces an error — the wrapper simply stops updating.
/// </remarks>
public sealed class BuildInfoTests
{
    [Fact]
    public void Splits_a_CI_stamped_version_into_version_and_commit()
    {
        // Exactly the shape observed in a real publish.
        var (version, sha) = BuildInfo.ParseInformationalVersion(
            "0.3.0-build.1+d8c483640cf2187a5d53f83dec2f3553bb2080c0");

        Assert.Equal("0.3.0-build.1", version);
        Assert.Equal("d8c483640cf2187a5d53f83dec2f3553bb2080c0", sha);
    }

    [Fact]
    public void Handles_a_plain_version_with_no_commit()
    {
        var (version, sha) = BuildInfo.ParseInformationalVersion("0.3.0");

        Assert.Equal("0.3.0", version);
        Assert.Equal(string.Empty, sha);
    }

    [Theory]
    [InlineData("1.0.0+nightly")]
    [InlineData("1.0.0+abc123")]
    [InlineData("1.0.0+not-forty-characters-long-but-close")]
    public void Does_not_mistake_ordinary_build_metadata_for_a_commit(string informational)
    {
        var (version, sha) = BuildInfo.ParseInformationalVersion(informational);

        // The whole string is the version; nothing here is a commit.
        Assert.Equal(informational, version);
        Assert.Equal(string.Empty, sha);
    }

    [Fact]
    public void Rejects_a_forty_character_suffix_that_is_not_hexadecimal()
    {
        var suffix = new string('z', 40);

        var (version, sha) = BuildInfo.ParseInformationalVersion($"1.0.0+{suffix}");

        Assert.Equal($"1.0.0+{suffix}", version);
        Assert.Equal(string.Empty, sha);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Degrades_predictably_on_an_empty_value(string informational)
    {
        var (version, sha) = BuildInfo.ParseInformationalVersion(informational);

        Assert.Equal("0.0.0", version);
        Assert.Equal(string.Empty, sha);
    }

    [Fact]
    public void Splits_on_the_last_separator()
    {
        var (version, sha) = BuildInfo.ParseInformationalVersion(
            "1.0.0+meta+d8c483640cf2187a5d53f83dec2f3553bb2080c0");

        Assert.Equal("1.0.0+meta", version);
        Assert.Equal("d8c483640cf2187a5d53f83dec2f3553bb2080c0", sha);
    }
}
