using System.IO.Compression;
using System.Text;
using BedrockSentry.Core.Packages;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Packages;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

public class ServerPackageNameTests
{
    [Theory]
    [InlineData("bedrock-server-1.26.36.1.zip", "1.26.36.1")]
    [InlineData("bedrock-server-1.21.0.3.zip", "1.21.0.3")]
    [InlineData("BEDROCK-SERVER-1.26.36.1.ZIP", "1.26.36.1")]
    public void Reads_the_version_from_a_mojang_file_name(string fileName, string expected)
    {
        var (version, text) = ServerPackageName.Parse(fileName);

        Assert.Equal(Version.Parse(expected), version);
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Keeps_a_preview_suffix_visible()
    {
        var (version, text) = ServerPackageName.Parse("bedrock-server-1.27.0.20-preview.zip");

        Assert.Equal(Version.Parse("1.27.0.20"), version);
        Assert.Contains("preview", text);
    }

    // An unfamiliar name is a listing detail, not a rejection: refusing to show
    // a file because it was renamed would be worse than showing it unversioned.
    [Theory]
    [InlineData("my-server.zip")]
    [InlineData("bedrock-server.zip")]
    [InlineData("bedrock-server-latest.zip")]
    public void An_unrecognised_name_parses_to_nothing_rather_than_throwing(string fileName)
    {
        var (version, text) = ServerPackageName.Parse(fileName);

        Assert.Null(version);
        Assert.Null(text);
    }

    // More than four parts overflows System.Version; the display text should
    // still survive rather than the whole parse failing.
    [Fact]
    public void A_version_with_too_many_parts_still_lists()
    {
        var (version, text) = ServerPackageName.Parse("bedrock-server-1.2.3.4.5.zip");

        Assert.Null(version);
        Assert.Equal("1.2.3.4.5", text);
    }
}

public sealed class ServerPackageStoreTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly SentryPaths _paths;
    private readonly ServerPackageStore _store;

    public ServerPackageStoreTests()
    {
        _dataRoot = Path.Combine(
            Path.GetTempPath(), "bedrocksentry-tests", Guid.NewGuid().ToString("n"));
        _paths = new SentryPaths(_dataRoot);
        Directory.CreateDirectory(_paths.ServerPackages);
        _store = new ServerPackageStore(_paths, NullLogger<ServerPackageStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a green run over.
        }
    }

    /// <summary>Writes a zip into the store with the given entries.</summary>
    private string WritePackage(string fileName, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_paths.ServerPackages, fileName);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (entryPath, content) in entries)
        {
            var entry = archive.CreateEntry(entryPath);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        return path;
    }

    private static (string Path, string Content)[] ATypicalServer() =>
    [
        ("bedrock_server.exe", "not really an executable"),
        ("server.properties", "server-name=Dedicated Server"),
        ("allowlist.json", "[]"),
        ("permissions.json", "[]"),
        ("behavior_packs/vanilla/manifest.json", "{}"),
    ];

    private string TargetDirectory()
    {
        var path = Path.Combine(_dataRoot, "instances", "Survival");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Lists_packages_newest_version_first()
    {
        WritePackage("bedrock-server-1.21.0.3.zip", ("a.txt", "x"));
        WritePackage("bedrock-server-1.26.36.1.zip", ("a.txt", "x"));
        WritePackage("bedrock-server-1.25.0.1.zip", ("a.txt", "x"));

        Assert.Equal(
            ["bedrock-server-1.26.36.1.zip", "bedrock-server-1.25.0.1.zip", "bedrock-server-1.21.0.3.zip"],
            _store.List().Select(p => p.FileName));
    }

    [Fact]
    public void Lists_unversioned_packages_last_but_does_list_them()
    {
        WritePackage("something-else.zip", ("a.txt", "x"));
        WritePackage("bedrock-server-1.26.36.1.zip", ("a.txt", "x"));

        var listed = _store.List();

        Assert.Equal(2, listed.Count);
        Assert.Equal("something-else.zip", listed[^1].FileName);
        Assert.Equal("something-else.zip", listed[^1].Display);
    }

    [Fact]
    public void Ignores_files_that_are_not_zips()
    {
        File.WriteAllText(Path.Combine(_paths.ServerPackages, "readme.txt"), "put zips here");
        WritePackage("bedrock-server-1.26.36.1.zip", ("a.txt", "x"));

        Assert.Single(_store.List());
    }

    [Fact]
    public void An_empty_store_is_a_valid_state()
    {
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Extracts_a_package_into_an_instance_directory()
    {
        WritePackage("bedrock-server-1.26.36.1.zip", ATypicalServer());
        var target = TargetDirectory();

        await _store.ExtractAsync(_store.List()[0], target);

        Assert.True(File.Exists(Path.Combine(target, "bedrock_server.exe")));
        Assert.True(File.Exists(Path.Combine(target, "server.properties")));
        Assert.True(File.Exists(Path.Combine(target, "behavior_packs", "vanilla", "manifest.json")));
    }

    // A zip that wraps everything in one folder would otherwise put the
    // executable a level below where every other part of the wrapper looks.
    [Fact]
    public async Task Strips_a_single_wrapping_folder()
    {
        WritePackage("bedrock-server-1.26.36.1.zip",
            ("bedrock-server-1.26.36.1/bedrock_server.exe", "x"),
            ("bedrock-server-1.26.36.1/server.properties", "y"),
            ("bedrock-server-1.26.36.1/worlds/level/db/CURRENT", "z"));

        var target = TargetDirectory();
        await _store.ExtractAsync(_store.List()[0], target);

        Assert.True(File.Exists(Path.Combine(target, "bedrock_server.exe")));
        Assert.True(File.Exists(Path.Combine(target, "worlds", "level", "db", "CURRENT")));
    }

    [Fact]
    public async Task Keeps_a_real_top_level_layout_flat()
    {
        WritePackage("bedrock-server-1.26.36.1.zip",
            ("bedrock_server.exe", "x"),
            ("behavior_packs/vanilla/manifest.json", "{}"));

        var target = TargetDirectory();
        await _store.ExtractAsync(_store.List()[0], target);

        Assert.True(File.Exists(Path.Combine(target, "bedrock_server.exe")));
        Assert.True(File.Exists(Path.Combine(target, "behavior_packs", "vanilla", "manifest.json")));
    }

    // Zip slip. An entry naming a path outside the target would otherwise be
    // written wherever it asked, by a service running as LocalSystem.
    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("../../escaped.txt")]
    [InlineData("subdir/../../escaped.txt")]
    [InlineData("..\\escaped.txt")]
    public async Task Refuses_an_entry_that_escapes_the_instance_directory(string entryPath)
    {
        WritePackage("bedrock-server-1.26.36.1.zip",
            ("bedrock_server.exe", "x"),
            (entryPath, "malicious"));

        var target = TargetDirectory();

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.ExtractAsync(_store.List()[0], target));

        Assert.Contains("outside", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dataRoot, "instances", "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(_dataRoot, "escaped.txt")));
    }

    // A sibling directory sharing a name prefix must not count as "inside" —
    // a plain string prefix check would let "…\Survival-evil" through.
    [Fact]
    public async Task A_sibling_directory_with_a_shared_prefix_is_still_outside()
    {
        WritePackage("bedrock-server-1.26.36.1.zip", ("../Survival-evil/planted.txt", "x"));

        var target = TargetDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.ExtractAsync(_store.List()[0], target));

        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "instances", "Survival-evil")));
    }

    // A BDS zip carries a default server.properties, allowlist.json and
    // permissions.json. Extracting over a configured server would silently reset
    // its settings and empty its allowlist.
    [Fact]
    public async Task Refuses_a_directory_that_already_holds_files()
    {
        WritePackage("bedrock-server-1.26.36.1.zip", ATypicalServer());

        var target = TargetDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(target, "server.properties"), "server-name=My Carefully Tuned Server");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.ExtractAsync(_store.List()[0], target));

        Assert.Contains("already contains files", ex.Message);
        Assert.Contains(
            "My Carefully Tuned Server",
            await File.ReadAllTextAsync(Path.Combine(target, "server.properties")));
    }

    // The registry creates the working directory before anything installs into
    // it, so "empty but present" has to be the normal case, not a blocker.
    [Fact]
    public async Task An_existing_empty_directory_is_fine()
    {
        WritePackage("bedrock-server-1.26.36.1.zip", ATypicalServer());
        var target = TargetDirectory();

        await _store.ExtractAsync(_store.List()[0], target);

        Assert.True(File.Exists(Path.Combine(target, "bedrock_server.exe")));
    }

    [Fact]
    public async Task A_package_removed_since_it_was_listed_fails_clearly()
    {
        WritePackage("bedrock-server-1.26.36.1.zip", ATypicalServer());
        var package = _store.List()[0];
        File.Delete(package.FullPath);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(
            () => _store.ExtractAsync(package, TargetDirectory()));

        Assert.Contains("bedrock-server-1.26.36.1.zip", ex.Message);
    }

    [Fact]
    public void Find_matches_case_insensitively()
    {
        WritePackage("bedrock-server-1.26.36.1.zip", ("a.txt", "x"));

        Assert.NotNull(_store.Find("BEDROCK-SERVER-1.26.36.1.ZIP"));
        Assert.Null(_store.Find("nope.zip"));
    }

    // docs/design.md § 11. Updating an existing install is the opposite contract to
    // provisioning one: the directory is full on purpose, and the whole job is
    // knowing what to leave alone.
    private string InstalledServer(params (string Path, string Content)[] extra)
    {
        var target = TargetDirectory();

        var files = new (string Path, string Content)[]
        {
            ("bedrock_server.exe", "the 1.26.36.1 executable"),
            ("server.properties", "server-name=My Carefully Tuned Server\nserver-port=19134"),
            ("allowlist.json", """[{"name":"Steve","xuid":"2535000000000000"}]"""),
            ("permissions.json", """[{"permission":"operator","xuid":"2535000000000000"}]"""),
            ("worlds/Bedrock level/level.dat", "years of building"),
            ("behavior_packs/vanilla/manifest.json", """{"version":"1.26.36"}"""),
            ("behavior_packs/mine/manifest.json", """{"name":"something I wrote"}"""),
            ("config/default/permissions.json", """{"allowed_modules":[]}"""),
        };

        foreach (var (relative, content) in files.Concat(extra))
        {
            var path = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return target;
    }

    private string BackupDirectory() => Path.Combine(_dataRoot, "kept");

    private static string Read(string directory, string relative) =>
        File.ReadAllText(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public async Task An_update_replaces_the_binaries_and_leaves_the_world_and_configuration()
    {
        var target = InstalledServer();

        WritePackage("bedrock-server-1.26.40.zip",
            ("bedrock_server.exe", "the 1.26.40 executable"),
            ("server.properties", "server-name=Dedicated Server\nserver-port=19132"),
            ("allowlist.json", "[]"),
            ("permissions.json", "[]"),
            ("worlds/Bedrock level/level.dat", "the zip's empty world"),
            ("config/default/permissions.json", """{"allowed_modules":["@minecraft/server"]}"""),
            ("behavior_packs/vanilla/manifest.json", """{"version":"1.26.40"}"""));

        var swap = await _store.UpdateInPlaceAsync(
            _store.Find("bedrock-server-1.26.40.zip")!, target, BackupDirectory());

        Assert.Equal("the 1.26.40 executable", Read(target, "bedrock_server.exe"));
        Assert.Equal("""{"version":"1.26.40"}""", Read(target, "behavior_packs/vanilla/manifest.json"));

        // Everything an administrator configured, untouched.
        Assert.Contains("My Carefully Tuned Server", Read(target, "server.properties"));
        Assert.Contains("19134", Read(target, "server.properties"));
        Assert.Contains("Steve", Read(target, "allowlist.json"));
        Assert.Contains("operator", Read(target, "permissions.json"));
        Assert.Equal("years of building", Read(target, "worlds/Bedrock level/level.dat"));
        Assert.Equal("""{"allowed_modules":[]}""", Read(target, "config/default/permissions.json"));

        Assert.Contains("bedrock_server.exe", swap.Overwritten);
        Assert.DoesNotContain("server.properties", swap.Overwritten);
        Assert.DoesNotContain("worlds/Bedrock level/level.dat", swap.Overwritten);
    }

    // Packs are absent from the preserved list and survive anyway, because an
    // update writes only what the package contains and deletes nothing. Worth a
    // test precisely because the mechanism is indirect.
    [Fact]
    public async Task A_pack_the_package_does_not_contain_is_left_where_it_is()
    {
        var target = InstalledServer();

        WritePackage("bedrock-server-1.26.40.zip", ("bedrock_server.exe", "new"));

        await _store.UpdateInPlaceAsync(
            _store.Find("bedrock-server-1.26.40.zip")!, target, BackupDirectory());

        Assert.Equal("""{"name":"something I wrote"}""", Read(target, "behavior_packs/mine/manifest.json"));
    }

    [Fact]
    public async Task Rolling_back_restores_what_was_replaced_and_removes_what_was_added()
    {
        var target = InstalledServer();

        WritePackage("bedrock-server-1.26.40.zip",
            ("bedrock_server.exe", "the 1.26.40 executable"),
            ("bedrock_server_how_to.html", "a file the old build never had"));

        var swap = await _store.UpdateInPlaceAsync(
            _store.Find("bedrock-server-1.26.40.zip")!, target, BackupDirectory());

        Assert.Contains("bedrock_server.exe", swap.Overwritten);
        Assert.Contains("bedrock_server_how_to.html", swap.Added);
        Assert.Equal(2, swap.FilesWritten);

        _store.RollBack(target, swap);

        Assert.Equal("the 1.26.36.1 executable", Read(target, "bedrock_server.exe"));
        Assert.False(File.Exists(Path.Combine(target, "bedrock_server_how_to.html")));

        // The world was never in the swap, so a rollback cannot have touched it.
        Assert.Equal("years of building", Read(target, "worlds/Bedrock level/level.dat"));
    }

    [Fact]
    public async Task An_update_strips_a_wrapping_folder_the_same_way_an_install_does()
    {
        var target = InstalledServer();

        WritePackage("bedrock-server-1.26.40.zip",
            ("bedrock-server-1.26.40/bedrock_server.exe", "the 1.26.40 executable"),
            ("bedrock-server-1.26.40/server.properties", "server-port=19132"));

        await _store.UpdateInPlaceAsync(
            _store.Find("bedrock-server-1.26.40.zip")!, target, BackupDirectory());

        Assert.Equal("the 1.26.40 executable", Read(target, "bedrock_server.exe"));
        Assert.Contains("19134", Read(target, "server.properties"));
        Assert.False(System.IO.Directory.Exists(Path.Combine(target, "bedrock-server-1.26.40")));
    }

    [Fact]
    public void Deleting_a_package_removes_the_zip_and_nothing_else()
    {
        WritePackage("bedrock-server-1.26.36.1.zip", ATypicalServer());
        WritePackage("bedrock-server-1.26.40.zip", ATypicalServer());

        Assert.True(_store.Delete("bedrock-server-1.26.36.1.zip"));

        Assert.Equal(["bedrock-server-1.26.40.zip"], _store.List().Select(p => p.FileName));
    }

    // A listing is not an authorisation: this deletes files as LocalSystem, and
    // a name the store does not list must not resolve to a path on disk.
    [Theory]
    [InlineData("not-there.zip")]
    [InlineData("../../../windows/system32/drivers/etc/hosts")]
    public void Deleting_something_the_store_does_not_list_does_nothing(string fileName)
    {
        WritePackage("bedrock-server-1.26.36.1.zip", ATypicalServer());

        Assert.False(_store.Delete(fileName));
        Assert.Single(_store.List());
    }

    [Fact]
    public async Task Deleting_the_package_a_server_was_installed_from_leaves_that_server_alone()
    {
        WritePackage("bedrock-server-1.26.36.1.zip", ATypicalServer());
        var target = TargetDirectory();

        await _store.ExtractAsync(_store.List()[0], target);
        Assert.True(_store.Delete("bedrock-server-1.26.36.1.zip"));

        // Extraction copies files in, so nothing installed depends on the zip
        // still being there. Worth a test because the opposite is the natural
        // assumption, and it is what would make the delete button frightening.
        Assert.True(File.Exists(Path.Combine(target, "bedrock_server.exe")));
        Assert.Empty(_store.List());
    }

    [Fact]
    public void The_stores_total_size_is_the_sum_of_what_it_holds()
    {
        WritePackage("bedrock-server-1.26.36.1.zip", ATypicalServer());
        WritePackage("bedrock-server-1.26.40.zip", ATypicalServer());

        Assert.Equal(_store.List().Sum(p => p.SizeBytes), _store.TotalBytes());
        Assert.True(_store.TotalBytes() > 0);
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("subdir/../../escaped.txt")]
    [InlineData("..\\escaped.txt")]
    public async Task An_update_refuses_an_entry_that_escapes_the_instance_directory(string entryPath)
    {
        var target = InstalledServer();

        WritePackage("bedrock-server-1.26.40.zip", ("bedrock_server.exe", "x"), (entryPath, "malicious"));

        await Assert.ThrowsAsync<InvalidDataException>(() => _store.UpdateInPlaceAsync(
            _store.Find("bedrock-server-1.26.40.zip")!, target, BackupDirectory()));

        Assert.False(File.Exists(Path.Combine(_dataRoot, "instances", "escaped.txt")));
    }
}
