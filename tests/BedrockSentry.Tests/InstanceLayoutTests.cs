using BedrockSentry.Core.Instances;

namespace BedrockSentry.Tests;

public class InstanceLayoutTests
{
    private static string Root => OperatingSystem.IsWindows() ? @"C:\instances" : "/instances";

    [Fact]
    public void Defaults_to_a_folder_named_after_the_instance()
    {
        var path = InstanceLayout.ResolveWorkingDirectory(
            Root, "survival", new InstanceDefinition { Name = "Survival" });

        Assert.Equal(Path.GetFullPath(Path.Combine(Root, "Survival")), path);
    }

    [Fact]
    public void Falls_back_to_the_id_when_no_name_is_set()
    {
        var path = InstanceLayout.ResolveWorkingDirectory(Root, "survival", new InstanceDefinition());

        Assert.Equal(Path.GetFullPath(Path.Combine(Root, "survival")), path);
    }

    // docs/design.md § 4: the root is a convenience, not a constraint. An override must
    // be honoured verbatim so a large world can live on another volume.
    [Fact]
    public void An_override_wins_over_the_root_entirely()
    {
        var elsewhere = OperatingSystem.IsWindows() ? @"D:\worlds\big" : "/mnt/worlds/big";

        var path = InstanceLayout.ResolveWorkingDirectory(
            Root, "survival", new InstanceDefinition { Name = "Survival", WorkingDirectory = elsewhere });

        Assert.Equal(Path.GetFullPath(elsewhere), path);
    }

    // A name with a separator would put the server somewhere other than where the
    // administrator is looking, and one with ".." would put it outside the root.
    // Both fail silently: the server starts, in the wrong place.
    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("nested/deep")]
    public void A_name_cannot_escape_the_root(string name)
    {
        var path = InstanceLayout.ResolveWorkingDirectory(
            Root, "survival", new InstanceDefinition { Name = name });

        var rootFull = Path.GetFullPath(Root);
        Assert.StartsWith(rootFull, path, StringComparison.Ordinal);

        // And it lands exactly one level down, not several.
        var relative = Path.GetRelativePath(rootFull, path);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, relative);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, relative);
    }

    [Fact]
    public void A_name_of_only_dots_falls_back_to_the_id()
    {
        var path = InstanceLayout.ResolveWorkingDirectory(
            Root, "survival", new InstanceDefinition { Name = ".." });

        Assert.Equal(Path.GetFullPath(Path.Combine(Root, "survival")), path);
    }

    // Windows silently strips trailing dots and spaces, so "backup." and "backup"
    // would become one folder while looking like two in config.
    [Theory]
    [InlineData("backup.", "backup")]
    [InlineData("backup ", "backup")]
    [InlineData("  spaced  ", "spaced")]
    public void Trailing_dots_and_spaces_are_removed(string name, string expected) =>
        Assert.Equal(expected, InstanceLayout.SanitiseFolderName(name, "fallback"));

    // CreateDirectory("CON") fails outright on Windows; renaming beats a startup
    // crash an administrator cannot interpret.
    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("LPT1")]
    [InlineData("NUL.log")]
    public void Reserved_device_names_are_prefixed(string name) =>
        Assert.StartsWith("_", InstanceLayout.SanitiseFolderName(name, "fallback"));

    [Fact]
    public void An_ordinary_name_is_left_alone() =>
        Assert.Equal("Survival 2026", InstanceLayout.SanitiseFolderName("Survival 2026", "fallback"));

    [Fact]
    public void An_empty_name_falls_back() =>
        Assert.Equal("fallback", InstanceLayout.SanitiseFolderName("   ", "fallback"));
}
