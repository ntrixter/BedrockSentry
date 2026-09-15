using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// The world and folder sizes on the instance Status tab — the first slice of
/// `D48`.
/// </summary>
public sealed class InstanceSizeSamplerTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("bedrocksentry-size-").FullName;

    private readonly InstanceSizeSampler _sampler =
        new(TimeProvider.System, NullLogger<InstanceSizeSampler>.Instance);

    public InstanceSizeSamplerTests()
    {
        // A working directory shaped like a real one: binaries at the top, the
        // world underneath, so the two numbers are genuinely different.
        Write(Path.Combine(_root, "bedrock_server.exe"), 500);
        Write(Path.Combine(_root, "worlds", "Bedrock level", "level.dat"), 200);
        Write(Path.Combine(_root, "worlds", "Bedrock level", "db", "000005.ldb"), 300);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static void Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private async Task<InstanceSize> SampleAsync(string instanceId = "survival")
    {
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sampler.Sampled += id => { if (id == instanceId) landed.TrySetResult(); };

        _sampler.Refresh(instanceId, _root);

        await landed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var size = _sampler.Latest(instanceId, _root);
        Assert.NotNull(size);
        return size;
    }

    /// <remarks>
    /// The two numbers answer different questions: the world is what grows and
    /// what a backup weighs, the folder is what the server costs in disk. If they
    /// ever come out equal, the world walk has silently become the folder walk.
    /// </remarks>
    [Fact]
    public async Task The_world_is_measured_apart_from_the_binaries_around_it()
    {
        var size = await SampleAsync();

        Assert.Equal(500, size.WorldBytes);
        Assert.Equal(1000, size.FolderBytes);
        Assert.NotEqual(size.WorldBytes, size.FolderBytes);
    }

    /// <remarks>
    /// A dash, not a zero: nothing measured yet and an empty world are different
    /// facts, and the second one would read as the world having gone.
    /// </remarks>
    [Fact]
    public void Nothing_measured_yet_reads_as_nothing_rather_than_zero()
    {
        Assert.Null(_sampler.Latest("never-seen", _root));
    }

    /// <remarks>
    /// The whole reason this is not computed where it is displayed. Latest must
    /// answer from the cache on the calling thread, whatever it has to do next.
    /// </remarks>
    [Fact]
    public async Task Latest_answers_immediately_and_never_walks_on_the_caller()
    {
        await SampleAsync();

        var started = DateTimeOffset.UtcNow;
        for (var i = 0; i < 500; i++)
        {
            _ = _sampler.Latest("survival", _root);
        }

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task An_instance_whose_directory_has_gone_measures_as_empty()
    {
        Directory.Delete(_root, recursive: true);

        var size = await SampleAsync("gone");

        Assert.Equal(0, size.WorldBytes);
        Assert.Equal(0, size.FolderBytes);
    }

    /// <remarks>
    /// Two views of one instance must not start two walks of the same world.
    /// </remarks>
    [Fact]
    public async Task Overlapping_requests_do_not_start_a_second_walk()
    {
        var samples = 0;
        _sampler.Sampled += _ => Interlocked.Increment(ref samples);

        for (var i = 0; i < 20; i++)
        {
            _sampler.Refresh("survival", _root);
        }

        await Task.Delay(500);

        Assert.InRange(Volatile.Read(ref samples), 1, 3);
    }
}
