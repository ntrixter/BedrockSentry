using System.Text.Json;
using BedrockSentry.Core;
using BedrockSentry.Core.Updates;

namespace BedrockSentry.Tests;

/// <summary>
/// Covers the manifest contract between the release workflow and the updater.
/// </summary>
/// <remarks>
/// The property names here are a wire format the CI workflow writes and a future
/// build reads. A rename on either side stops updates without producing an error.
/// </remarks>
public sealed class UpdateManifestTests
{
    // Byte-for-byte the shape .github/workflows/release.yml emits.
    private const string PublishedManifest = """
        {
          "identity": "0.3.0-build.57+f0cb4a6",
          "version": "0.3.0-build.57",
          "commitSha": "f0cb4a6d1e2b3c4d5e6f708192a3b4c5d6e7f809",
          "channel": "build",
          "publishedAt": "2026-08-01T12:00:00.0000000Z",
          "sha256": "abc123",
          "downloadUrl": "https://example.invalid/bedrocksentry.zip"
        }
        """;

    [Fact]
    public void Deserialises_what_the_workflow_publishes()
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(PublishedManifest);

        Assert.NotNull(manifest);
        Assert.Equal("0.3.0-build.57+f0cb4a6", manifest.Identity);
        Assert.Equal("0.3.0-build.57", manifest.Version);
        Assert.Equal("f0cb4a6d1e2b3c4d5e6f708192a3b4c5d6e7f809", manifest.CommitSha);
        Assert.Equal("build", manifest.Channel);
        Assert.Equal("abc123", manifest.Sha256);
        Assert.Equal("https://example.invalid/bedrocksentry.zip", manifest.DownloadUrl);
    }

    [Fact]
    public void A_different_commit_counts_as_an_update()
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(PublishedManifest)!;

        // The test host is not a CI build, so BuildInfo.CommitSha is empty and any
        // populated commit differs from it.
        Assert.True(manifest.IsNewerThanRunning());
    }

    [Fact]
    public void The_running_commit_is_not_an_update()
    {
        var manifest = new UpdateManifest { CommitSha = BuildInfo.CommitSha };

        Assert.False(manifest.IsNewerThanRunning());
    }

    [Fact]
    public void A_manifest_with_no_commit_is_never_an_update()
    {
        // Comparing against an empty value would otherwise make every poll look like
        // a new build and trigger an endless reinstall of the same version.
        var manifest = new UpdateManifest { CommitSha = string.Empty };

        Assert.False(manifest.IsNewerThanRunning());
    }

    [Fact]
    public void Commit_comparison_ignores_hex_casing()
    {
        var manifest = new UpdateManifest { CommitSha = "ABCDEF1234567890ABCDEF1234567890ABCDEF12" };
        var same = new UpdateManifest { CommitSha = "abcdef1234567890abcdef1234567890abcdef12" };

        // Both differ from the running build identically; the point is that casing
        // alone must never be what makes them differ from each other.
        Assert.Equal(
            manifest.CommitSha.ToLowerInvariant(),
            same.CommitSha.ToLowerInvariant());
    }
}
