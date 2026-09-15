using BedrockSentry.Services.Configuration;

namespace BedrockSentry.Tests;

/// <summary>
/// Choosing where instances live, which is asked once and then lived with.
/// </summary>
public class InstanceRootTests
{
    private static string Rooted(params string[] parts) =>
        Path.Combine(OperatingSystem.IsWindows() ? @"D:\" : "/srv", Path.Combine(parts));

    [Fact]
    public void A_full_path_is_accepted() => Assert.Null(InstanceRoot.Problem(Rooted("instances")));

    [Fact]
    public void Nothing_at_all_is_rejected()
    {
        Assert.NotNull(InstanceRoot.Problem(null));
        Assert.NotNull(InstanceRoot.Problem("   "));
    }

    // A relative path resolves against the service's working directory, which is
    // the install tree — replaced wholesale by a self-update. Accepting one would
    // put every world somewhere that disappears on the next update.
    [Fact]
    public void A_relative_path_is_rejected() =>
        Assert.NotNull(InstanceRoot.Problem(Path.Combine("instances", "here")));

    // "Access is denied" from a later mkdir sends somebody to permissions when
    // the real answer is that the name is taken.
    [Fact]
    public void A_file_where_the_folder_should_be_says_so()
    {
        var file = Path.Combine(Path.GetTempPath(), $"bedrocksentry-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(file, string.Empty);

        try
        {
            Assert.Contains("is a file", InstanceRoot.Problem(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    // Accepting the default must leave the setting absent rather than pinning the
    // resolved path, or instances stop following the data root when it moves.
    [Theory]
    [InlineData("instances", "instances", true)]
    [InlineData("instances/", "instances", true)]
    [InlineData("elsewhere", "instances", false)]
    public void The_default_is_recognised_however_it_was_typed(
        string chosen, string fallback, bool expected) =>
        Assert.Equal(expected, InstanceRoot.IsDefault(Rooted(chosen), Rooted(fallback)));

    [Fact]
    public void Nothing_chosen_is_not_the_default() =>
        Assert.False(InstanceRoot.IsDefault(null, Rooted("instances")));

    // Existing and readable is not the same as writable, and the difference only
    // shows up when the first server tries to start.
    [Fact]
    public void Preparing_a_usable_folder_creates_it_and_leaves_no_probe_behind()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bedrocksentry-{Guid.NewGuid():N}");

        try
        {
            InstanceRoot.Prepare(root);

            Assert.True(Directory.Exists(root));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Preparing_a_folder_that_already_exists_is_not_an_error()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bedrocksentry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            InstanceRoot.Prepare(root);
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
