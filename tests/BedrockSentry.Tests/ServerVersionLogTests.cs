using BedrockSentry.Core.Configuration;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 11. Driven by what the server prints at startup rather than by
/// what the wrapper did, which is what makes "last updated" answerable before
/// the wrapper can apply an update at all — today every server on every install
/// has only ever been updated by hand.
/// </summary>
public sealed class ServerVersionLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-versions-{Guid.NewGuid():N}");

    private readonly ServerVersionLog _log;

    private static readonly DateTimeOffset Noon = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    public ServerVersionLogTests()
    {
        Directory.CreateDirectory(_directory);
        _log = new ServerVersionLog(new Paths(_directory), NullLogger<ServerVersionLog>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Nothing_is_known_about_a_server_that_has_never_run() =>
        Assert.Null(_log.Read("survival"));

    // The first sighting is an observation, not an update. Dating it as one
    // would tell every administrator their server was "updated" on the day they
    // installed the wrapper.
    [Fact]
    public void The_first_version_seen_is_recorded_but_is_not_an_update()
    {
        var record = _log.Record("survival", "1.26.40", Noon);

        Assert.NotNull(record);
        Assert.False(record.IsAnUpdate);
        Assert.Null(record.PreviousVersion);
        Assert.Equal("1.26.40", record.Version);
        Assert.Equal(Noon, record.FirstSeenUtc);
    }

    [Fact]
    public void A_changed_version_is_an_update_and_remembers_what_it_came_from()
    {
        _log.Record("survival", "1.26.40", Noon);

        var updated = _log.Record("survival", "1.26.44.3", Noon.AddDays(3));

        Assert.NotNull(updated);
        Assert.True(updated.IsAnUpdate);
        Assert.Equal("1.26.40", updated.PreviousVersion);
        Assert.Equal(Noon.AddDays(3), updated.FirstSeenUtc);
    }

    // Every ordinary restart reports the version again. Rewriting the record
    // would move "last updated" forward every time the server bounced, which is
    // the one thing the field must not do.
    [Fact]
    public void Seeing_the_same_version_again_changes_nothing()
    {
        _log.Record("survival", "1.26.40", Noon);

        Assert.Null(_log.Record("survival", "1.26.40", Noon.AddHours(6)));
        Assert.Null(_log.Record("survival", "1.26.40", Noon.AddDays(30)));

        Assert.Equal(Noon, _log.Read("survival")!.FirstSeenUtc);
    }

    [Fact]
    public void Version_comparison_ignores_case_and_surrounding_space()
    {
        _log.Record("survival", "1.26.40", Noon);

        Assert.Null(_log.Record("survival", "  1.26.40  ", Noon.AddHours(1)));
    }

    [Fact]
    public void Each_instance_is_recorded_on_its_own()
    {
        _log.Record("survival", "1.26.40", Noon);
        _log.Record("creative", "1.26.44.3", Noon);

        Assert.Equal("1.26.40", _log.Read("survival")!.Version);
        Assert.Equal("1.26.44.3", _log.Read("creative")!.Version);
    }

    [Fact]
    public void An_empty_version_is_not_recorded()
    {
        Assert.Null(_log.Record("survival", "   ", Noon));
        Assert.Null(_log.Read("survival"));
    }

    // D14. Removing an instance takes its version record with it.
    [Fact]
    public void Forgetting_an_instance_removes_its_record()
    {
        _log.Record("survival", "1.26.40", Noon);
        _log.Forget("survival");

        Assert.Null(_log.Read("survival"));
    }

    // An unreadable record means "we do not know", which every caller already
    // handles. It is not worth failing a page render over.
    [Fact]
    public void A_corrupt_record_reads_as_unknown_rather_than_throwing()
    {
        _log.Record("survival", "1.26.40", Noon);

        var path = Path.Combine(_directory, "data", "versions", "survival.json");
        File.WriteAllText(path, "{ this is not json");

        Assert.Null(_log.Read("survival"));
    }

    private sealed class Paths(string root) : ISentryPaths
    {
        public string DataRoot => root;
        public string Config => Path.Combine(root, "config");
        public string Certificates => Path.Combine(root, "certs");
        public string DataProtectionKeys => Path.Combine(root, "keys");
        public string Data => Path.Combine(root, "data");
        public string Backups => Path.Combine(root, "backups");
        public string Update => Path.Combine(root, "update");
        public string Logs => Path.Combine(root, "logs");
        public string InstanceLogs => Path.Combine(Logs, "instances");
        public string Instances => Path.Combine(root, "instances");
        public string ServerPackages => Path.Combine(root, "server-packages");
        public void EnsureCreated() => Directory.CreateDirectory(root);
    }
}
