using BedrockSentry.Core.Configuration;

namespace BedrockSentry.Tests;

/// <summary>
/// The bug this exists for: four writers used a fixed <c>path + ".tmp"</c>, and
/// a fixed temp name is not a private workspace. Two web requests editing one
/// instance, or a request racing the backup scheduler, both write the same file.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-atomic-{Guid.NewGuid():N}");

    public AtomicFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string Path_(string name) => System.IO.Path.Combine(_directory, name);

    [Fact]
    public void Writes_the_file_and_leaves_no_temp_behind()
    {
        var path = Path_("thing.json");

        AtomicFile.WriteAllText(path, "contents");

        Assert.Equal("contents", File.ReadAllText(path));
        Assert.Single(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public void Overwrites_an_existing_file()
    {
        var path = Path_("thing.json");
        File.WriteAllText(path, "old");

        AtomicFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public async Task Creates_the_directory_when_it_is_missing()
    {
        var path = Path_(System.IO.Path.Combine("nested", "deeper", "thing.json"));

        await AtomicFile.WriteAllTextAsync(path, "contents");

        Assert.Equal("contents", await File.ReadAllTextAsync(path));
    }

    // The whole point of the GUID. With a fixed name the second writer's open
    // fails outright on Windows, and where it does not, one write lands inside
    // the other's file.
    [Fact]
    public async Task Concurrent_writers_do_not_collide()
    {
        var path = Path_("contended.json");

        var writers = Enumerable
            .Range(0, 24)
            .Select(i => Task.Run(() => AtomicFile.WriteAllText(path, $"writer-{i}")))
            .ToArray();

        await Task.WhenAll(writers);

        // Whoever won, the file is one writer's whole content and nobody's half.
        var text = await File.ReadAllTextAsync(path);
        Assert.Matches(@"^writer-\d+$", text);

        // And every temp was cleaned up behind them.
        Assert.Single(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public async Task A_failed_write_leaves_the_previous_file_intact()
    {
        var path = Path_("precious.json");
        await File.WriteAllTextAsync(path, "the good copy");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AtomicFile.WriteAsync(path, _ => throw new InvalidOperationException("nope")));

        Assert.Equal("the good copy", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public async Task The_streaming_overload_writes_through_the_temp()
    {
        var path = Path_("streamed.bin");

        await AtomicFile.WriteAsync(path, async temp =>
        {
            await using var stream = File.Create(temp);
            await stream.WriteAsync("streamed"u8.ToArray());
        });

        Assert.Equal("streamed", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFileSystemEntries(_directory));
    }
}
