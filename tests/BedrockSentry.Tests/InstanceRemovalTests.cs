using BedrockSentry.Services.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// The guards on deleting an instance's files.
///
/// This is the only thing BedrockSentry does that cannot be undone, and its
/// input is a path somebody typed into a config file months earlier. Every case
/// below is a way that path could be wrong, and each one costs a manual delete
/// to be safe about and a hard drive to be wrong about.
/// </summary>
public sealed class InstanceDirectoryRemovalTests : IDisposable
{
    private readonly string _root;

    public InstanceDirectoryRemovalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "bedrocksentry-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Make(string name, params string[] contents)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);

        foreach (var item in contents)
        {
            File.WriteAllText(Path.Combine(directory, item), "x");
        }

        return directory;
    }

    [Theory]
    [InlineData("bedrock_server.exe")]
    [InlineData("server.properties")]
    [InlineData("allowlist.json")]
    public void A_directory_that_looks_like_a_server_may_be_deleted(string marker) =>
        Assert.Null(InstanceDirectoryRemoval.Refuse(Make("survival", marker), []));

    [Fact]
    public void An_empty_directory_may_be_deleted() =>
        Assert.Null(InstanceDirectoryRemoval.Refuse(Make("empty"), []));

    [Fact]
    public void A_directory_that_is_not_there_is_not_a_refusal() =>
        Assert.Null(InstanceDirectoryRemoval.Refuse(Path.Combine(_root, "gone"), []));

    // The catch-all for a working directory that was never a server directory.
    // Someone pointing an instance at their Documents folder and then removing
    // it is the case this exists for.
    [Fact]
    public void A_directory_holding_somebody_elses_files_is_refused()
    {
        var directory = Make("documents", "taxes.pdf", "notes.txt");

        var refusal = InstanceDirectoryRemoval.Refuse(directory, []);

        Assert.NotNull(refusal);
        Assert.Contains("nothing that looks like a Bedrock server", refusal);
    }

    [Fact]
    public void The_root_of_a_drive_is_refused()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(_root))!;

        Assert.Contains("root of a drive", InstanceDirectoryRemoval.Refuse(root, []));
    }

    [Fact]
    public void One_of_the_wrappers_own_folders_is_refused()
    {
        var data = Make("data", "server.properties");

        Assert.Contains("BedrockSentry's own", InstanceDirectoryRemoval.Refuse(data, [data]));
    }

    // The dangerous one. A working directory of C:\ProgramData\BedrockSentry
    // looks like a server if an exe was ever unpacked into it, and deleting it
    // takes the config, the credentials and every other instance with it.
    [Fact]
    public void A_directory_containing_one_of_the_wrappers_own_folders_is_refused()
    {
        var outer = Make("outer", "bedrock_server.exe");
        var inner = Path.Combine(outer, "config");
        Directory.CreateDirectory(inner);

        var refusal = InstanceDirectoryRemoval.Refuse(outer, [inner]);

        Assert.NotNull(refusal);
        Assert.Contains("which BedrockSentry needs", refusal);
    }

    // "C:\Bedrock" does not contain "C:\BedrockOther", however the strings look.
    [Fact]
    public void A_name_that_merely_starts_the_same_is_not_containment()
    {
        var instance = Make("bedrock", "server.properties");
        var other = Make("bedrockother");

        Assert.Null(InstanceDirectoryRemoval.Refuse(instance, [other]));
    }

    [Fact]
    public void An_empty_path_is_refused() =>
        Assert.NotNull(InstanceDirectoryRemoval.Refuse("   ", []));
}
