using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Packages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BedrockSentry.Tests;

public class InstanceIdTests
{
    [Theory]
    [InlineData("Survival", "survival")]
    [InlineData("My Server", "my-server")]
    [InlineData("Creative  Mode", "creative-mode")]
    [InlineData("Steve's Server!", "steve-s-server")]
    [InlineData("  Padded  ", "padded")]
    public void Slugs_a_display_name(string name, string expected) =>
        Assert.Equal(expected, InstanceId.FromName(name));

    // The id becomes a folder name and a URL segment, so a name that slugs to
    // nothing still has to produce something usable rather than being rejected —
    // the name itself is perfectly good for a person to read.
    [Theory]
    [InlineData("!!!")]
    [InlineData("---")]
    [InlineData("日本語")]
    public void A_name_that_slugs_to_nothing_still_yields_an_id(string name) =>
        Assert.Equal("instance", InstanceId.FromName(name));

    [Fact]
    public void Appends_a_counter_until_the_id_is_free()
    {
        var taken = new HashSet<string> { "survival", "survival-2" };

        Assert.Equal("survival-3", InstanceId.MakeUnique("survival", taken.Contains));
        Assert.Equal("creative", InstanceId.MakeUnique("creative", taken.Contains));
    }
}

public class ServerPropertiesTests : IDisposable
{
    private readonly string _directory;

    public ServerPropertiesTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "bedrocksentry-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(params string[] lines)
    {
        var path = ServerProperties.PathFor(_directory);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Reads_settings_and_ignores_comments()
    {
        var path = Write(
            "# The name shown in the server list",
            "server-name=Dedicated Server",
            "",
            "server-port=19132");

        var values = ServerProperties.Read(path);

        Assert.Equal("Dedicated Server", values["server-name"]);
        Assert.Equal("19132", values["server-port"]);
        Assert.Equal(2, values.Count);
    }

    // The file Mojang ships is mostly documentation. Rewriting it from a parsed
    // dictionary would throw all of that away the first time the wrapper changed
    // a port number.
    [Fact]
    public async Task Editing_preserves_comments_and_ordering()
    {
        var path = Write(
            "# The name shown in the server list",
            "server-name=Dedicated Server",
            "# Allowed values: 1 to 65535",
            "server-port=19132",
            "gamemode=survival");

        await ServerProperties.SetAsync(path, new Dictionary<string, string>
        {
            ["server-name"] = "Survival",
            ["server-port"] = "19134",
        });

        var lines = await File.ReadAllLinesAsync(path);

        Assert.Equal(
            [
                "# The name shown in the server list",
                "server-name=Survival",
                "# Allowed values: 1 to 65535",
                "server-port=19134",
                "gamemode=survival",
            ],
            lines);
    }

    [Fact]
    public async Task A_setting_that_is_not_there_is_appended()
    {
        var path = Write("server-name=Dedicated Server");

        await ServerProperties.SetAsync(path, new Dictionary<string, string>
        {
            ["server-portv6"] = "19135",
        });

        Assert.Contains("server-portv6=19135", await File.ReadAllLinesAsync(path));
    }

    [Fact]
    public void A_missing_file_reads_as_empty_rather_than_throwing() =>
        Assert.Empty(ServerProperties.Read(Path.Combine(_directory, "nope.properties")));

    [Fact]
    public void Reads_the_port_back()
    {
        Write("server-port=19140");

        Assert.Equal(19140, ServerProperties.PortOf(_directory));
    }
}

public sealed class InstanceCreationTests : IAsyncLifetime
{
    /// <summary>A fact that reports itself skipped rather than failed off Windows.</summary>
    /// <remarks>
    /// Only <see cref="Our_own_hold_on_an_instances_port_is_not_a_conflict_for_it"/>
    /// needs this, and only because it is the one case in this file that runs with
    /// the system check *on*. Attributing a UDP socket to the process holding it
    /// takes <c>GetExtendedUdpTable</c>, which exists on Windows alone;
    /// <see cref="UdpListeners.All"/> falls back to
    /// <c>IPGlobalProperties.GetActiveUdpListeners</c> everywhere else, and that
    /// reports endpoints with no owner, so every listener comes back as process 0.
    /// The guard being tested compares the owner against
    /// <c>Environment.ProcessId</c>, which can therefore never match off Windows —
    /// the test fails there for want of an operating system feature rather than
    /// because the wrapper is wrong.
    ///
    /// Skipped rather than deleted: CI runs on windows-latest, where this is a real
    /// assertion about a guard that governs whether an instance's own port reads as
    /// a conflict against itself. Losing it would cost the cover that matters most.
    /// Skipping rather than an early return inside the body, for the reason the
    /// other two of these give: a test that silently stops testing shows green
    /// without having asserted anything.
    /// </remarks>
    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Only Windows reports which process holds a UDP port.";
            }
        }
    }

    private readonly string _dataRoot;
    private readonly SentryPaths _paths;
    private readonly FakeProcessFactory _factory = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 3, 7, 0, 0, TimeSpan.Zero));

    private InstanceRegistry? _registry;

    public InstanceCreationTests()
    {
        _dataRoot = Path.Combine(
            Path.GetTempPath(), "bedrocksentry-tests", Guid.NewGuid().ToString("n"));
        _paths = new SentryPaths(_dataRoot);
        Directory.CreateDirectory(_paths.Config);
        Directory.CreateDirectory(_paths.ServerPackages);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_registry is not null)
        {
            await _registry.DisposeAsync();
        }

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private InstanceRegistry Registry()
    {
        var options = new InstanceOptions();
        options.ConsoleLog.Enabled = false;

        // No countdown. The clock here is frozen, so a warning lead would leave
        // any stop waiting on a timer nothing is going to advance.
        options.ServiceStopWarning = TimeSpan.Zero;

        return _registry = new InstanceRegistry(
            new StaticOptionsMonitor<InstanceOptions>(options),
            _paths, _factory, _time, NullLoggerFactory.Instance,
            new ServerPackageStore(_paths, NullLogger<ServerPackageStore>.Instance),
            new NoPlayerDataStore());
    }

    private void WritePackage(string fileName = "bedrock-server-1.26.36.1.zip")
    {
        using var stream = new FileStream(
            Path.Combine(_paths.ServerPackages, fileName), FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        void Add(string path, string content)
        {
            var entry = archive.CreateEntry(path);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        Add("bedrock_server.exe", "x");

        // Laid out the way Mojang's own file is — setting first, documentation
        // under it, blank line between — because that layout is what the
        // properties parser and the D21 edit both have to survive.
        Add("server.properties",
            """
            server-name=Dedicated Server
            # Used as the server name

            server-port=19132
            # Which IPv4 port the server should listen to.

            server-portv6=19133
            # Which IPv6 port the server should listen to.

            enable-lan-visibility=true
            # Listen and respond to clients that are looking for servers on the LAN.

            """);
    }

    private string ConfigPath => Path.Combine(_paths.Config, "bedrocksentry.json");

    [Fact]
    public async Task Creates_an_instance_and_installs_a_server_into_it()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        Assert.Equal("survival", created.InstanceId);
        Assert.True(created.IsProvisioned);
        Assert.True(File.Exists(Path.Combine(created.WorkingDirectory, "bedrock_server.exe")));
    }

    // Live registration is the point. Writing only the config file would leave a
    // button whose result appears after a service restart, which is barely better
    // than editing the file by hand.
    [Fact]
    public async Task The_new_instance_is_usable_immediately()
    {
        WritePackage();
        var registry = Registry();

        await registry.CreateAsync(new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip"));

        Assert.Single(registry.All);
        Assert.True(registry.TryGet("survival", out _));
        Assert.Contains(registry.Statuses(), s => s.Name == "Survival");
    }

    [Fact]
    public async Task Records_the_instance_in_the_config_file()
    {
        WritePackage();
        var registry = Registry();

        await registry.CreateAsync(new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip"));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        var survival = document.RootElement
            .GetProperty("Sentry").GetProperty("Instances").GetProperty("Servers")
            .GetProperty("survival");

        Assert.Equal("Survival", survival.GetProperty("Name").GetString());
        Assert.True(survival.GetProperty("AutoStart").GetBoolean());
    }

    [Fact]
    public async Task Leaves_the_rest_of_the_config_file_alone()
    {
        await File.WriteAllTextAsync(
            ConfigPath, """{ "Sentry": { "Tls": { "Port": 9443 } } }""");

        WritePackage();
        var registry = Registry();
        await registry.CreateAsync(new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip"));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        Assert.Equal(
            9443,
            document.RootElement.GetProperty("Sentry").GetProperty("Tls").GetProperty("Port").GetInt32());
    }

    // Every BDS package ships listening on 19132, so a second instance created
    // from the same zip silently collides with the first — one starts, the other
    // exits, and it looks like a crash loop with no cause.
    [Fact]
    public async Task Writes_the_chosen_port_into_server_properties()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Creative", "bedrock-server-1.26.36.1.zip", 19134));

        var values = ServerProperties.Read(ServerProperties.PathFor(created.WorkingDirectory));

        Assert.Equal("19134", values["server-port"]);
        Assert.Equal("19135", values["server-portv6"]);   // Bedrock takes the next number for IPv6
        Assert.Equal("Creative", values["server-name"]);
    }

    // D24. Every instance keeps Bedrock's own default, which is on. D21 turned it
    // off from the second server onward, to stop that server taking 19132 and
    // 19133 from the first. It did stop that — and left those servers answering a
    // ping with no server info, so no client could join them, while the wrapper
    // went on reporting them healthy. LanDiscoveryPortGuard holds the pair across
    // another instance's startup instead: same contention settled, without taking
    // the server away from the people trying to reach it.
    [Fact]
    public async Task Every_instance_keeps_bedrocks_own_lan_visibility_default()
    {
        WritePackage();
        var registry = Registry();

        var first = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var second = await registry.CreateAsync(
            new NewInstanceRequest("Creative", "bedrock-server-1.26.36.1.zip", 19134));

        foreach (var created in new[] { first, second })
        {
            Assert.Equal(
                "true",
                ServerProperties.Read(
                    ServerProperties.PathFor(created.WorkingDirectory))["enable-lan-visibility"]);
        }
    }

    [Fact]
    public async Task Creating_an_instance_leaves_the_lan_visibility_line_alone()
    {
        WritePackage();
        var registry = Registry();

        await registry.CreateAsync(new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var second = await registry.CreateAsync(
            new NewInstanceRequest("Creative", "bedrock-server-1.26.36.1.zip", 19134));

        var lines = await File.ReadAllLinesAsync(
            ServerProperties.PathFor(second.WorkingDirectory));

        // The package's own line, in the package's own place, said once — not
        // rewritten, and not appended at the bottom leaving the file saying it
        // twice with the two copies disagreeing.
        Assert.Single(lines, l => l.StartsWith("enable-lan-visibility="));
        Assert.Equal(
            "# Listen and respond to clients that are looking for servers on the LAN.",
            lines[Array.IndexOf(lines, "enable-lan-visibility=true") + 1]);
    }

    // BDS accepts the setting but ships no line for it, so opting out otherwise
    // means retyping it into every new instance through the "add a setting" row.
    [Fact]
    public async Task A_new_instance_is_opted_out_of_server_telemetry()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        Assert.Equal(
            "false",
            ServerProperties.Read(
                ServerProperties.PathFor(created.WorkingDirectory))["emit-server-telemetry"]);
    }

    // The settings that are painful to change later, chosen once on the form.
    [Fact]
    public async Task World_settings_chosen_on_the_form_are_written()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(new NewInstanceRequest(
            "Survival",
            "bedrock-server-1.26.36.1.zip",
            19132,
            World: new NewWorldSettings
            {
                Seed = "1234567890",
                Gamemode = "creative",
                Difficulty = "peaceful",
                AllowCheats = true,
                AllowList = true,
                EmitTelemetry = false,
            }));

        var values = ServerProperties.Read(
            ServerProperties.PathFor(created.WorkingDirectory));

        Assert.Equal("1234567890", values["level-seed"]);
        Assert.Equal("creative", values["gamemode"]);
        Assert.Equal("peaceful", values["difficulty"]);
        Assert.Equal("true", values["allow-cheats"]);
        Assert.Equal("true", values["allow-list"]);
        Assert.Equal("false", values["emit-server-telemetry"]);
    }

    [Fact]
    public async Task The_form_defaults_are_survival_hard_no_cheats_no_allowlist()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(new NewInstanceRequest(
            "Survival", "bedrock-server-1.26.36.1.zip", 19132, World: new NewWorldSettings()));

        var values = ServerProperties.Read(
            ServerProperties.PathFor(created.WorkingDirectory));

        Assert.Equal("survival", values["gamemode"]);
        Assert.Equal("hard", values["difficulty"]);
        Assert.Equal("false", values["allow-cheats"]);
        Assert.Equal("false", values["allow-list"]);
        Assert.Equal("true", values["emit-server-telemetry"]);
    }

    // Writing an empty level-seed is the same as omitting it, but it reads in
    // the file as though a choice was made.
    [Fact]
    public async Task A_blank_seed_writes_no_seed_line()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(new NewInstanceRequest(
            "Survival", "bedrock-server-1.26.36.1.zip", 19132,
            World: new NewWorldSettings { Seed = "   " }));

        Assert.DoesNotContain(
            "level-seed",
            ServerProperties.Read(ServerProperties.PathFor(created.WorkingDirectory)).Keys);
    }

    // A default, not a policy: adopting a directory whose server was already
    // configured must not have that choice quietly reversed by instance creation.
    [Fact]
    public async Task An_existing_telemetry_choice_survives_creation()
    {
        var registry = Registry();

        var directory = Path.Combine(_dataRoot, "adopted");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "bedrock_server.exe"), "their binary");
        await File.WriteAllLinesAsync(
            ServerProperties.PathFor(directory),
            ["server-name=Adopted", "emit-server-telemetry=true"]);

        var adopted = await registry.AdoptAsync("Adopted", directory);

        Assert.Equal(
            "true",
            ServerProperties.Read(
                ServerProperties.PathFor(adopted.WorkingDirectory))["emit-server-telemetry"]);
    }

    [Fact]
    public async Task Suggests_a_port_no_instance_is_using()
    {
        WritePackage();
        var registry = Registry();

        Assert.Equal(19132, registry.SuggestPort());

        await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        // 19133 is the first instance's IPv6 port, so the next free pair is 19134.
        Assert.Equal(19134, registry.SuggestPort());
    }

    [Fact]
    public async Task A_second_instance_with_the_same_name_gets_its_own_id()
    {
        WritePackage();
        var registry = Registry();

        var first = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var second = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19134));

        Assert.Equal("survival", first.InstanceId);
        Assert.Equal("survival-2", second.InstanceId);
        Assert.NotEqual(first.WorkingDirectory, second.WorkingDirectory);
    }

    [Fact]
    public async Task An_instance_can_be_created_without_installing_anything()
    {
        var registry = Registry();

        var created = await registry.CreateAsync(new NewInstanceRequest("Empty"));

        Assert.False(created.IsProvisioned);
        Assert.True(Directory.Exists(created.WorkingDirectory));
    }

    // A port collision is the single most confusing way for Bedrock to fail: the
    // process starts, binds nothing and exits, so the wrapper reports a crash
    // loop with no hint that another server is holding the port. These checks
    // exist to turn that into a sentence before the save rather than after the
    // third retry.
    [Fact]
    public async Task A_port_another_instance_uses_is_reported_as_a_conflict()
    {
        WritePackage();
        var registry = Registry();
        var inspector = new PortInspector(registry);

        await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var second = await registry.CreateAsync(
            new NewInstanceRequest("Creative", "bedrock-server-1.26.36.1.zip", 19134));

        var conflict = Assert.Single(
            inspector.Check(second.InstanceId, 19132, skipSystemCheck: true));

        Assert.Contains("Survival", conflict.Reason);
        Assert.Contains("server-port", conflict.Reason);
    }

    // T151 clause 6. THE GUARD, and it exists because a doc comment was not one.
    //
    // The Readiness panel and the start pre-flight must ask DIFFERENT questions
    // of the same inspector. The panel was once moved onto ConflictsForStart so
    // the two "cannot drift" - and they did not, they agreed on the start path's
    // answer, and the panel stopped reporting config-only collisions for a whole
    // release. So this asserts the disagreement rather than describing it.
    //
    // Asserted as "does the sibling appear", not "is it empty": these two run
    // with the system check ON, and a stray listener on the build machine must
    // not be able to fail the test.
    [Fact]
    public async Task The_panel_reports_a_stopped_siblings_port_and_the_start_path_does_not()
    {
        WritePackage();
        var registry = Registry();
        var inspector = new PortInspector(registry);

        var first = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var second = await registry.CreateAsync(
            new NewInstanceRequest("Creative", "bedrock-server-1.26.36.1.zip", 19134));

        // Point the second at the first's port, both stopped. Nothing is running,
        // so the only possible conflict is the file collision.
        File.WriteAllText(
            Path.Combine(second.WorkingDirectory, "server.properties"),
            "server-port=19132\nserver-portv6=19133\n");

        var shown = inspector.ConflictsToShow(second.InstanceId, second.WorkingDirectory);
        var forStart = inspector.ConflictsForStart(second.InstanceId, second.WorkingDirectory);

        // The person is told.
        Assert.Contains(shown, c => c.Reason.Contains("Survival", StringComparison.Ordinal));

        // The launch is not refused for it: a stopped sibling is not a process,
        // and the adoption form promises such a pair that only one runs at a time.
        Assert.DoesNotContain(forStart, c => c.Reason.Contains("Survival", StringComparison.Ordinal));

        _ = first;
    }

    // T151 clause 8b. A server.properties that is not there yet is the same case
    // as a key that is not set: BDS runs on its own defaults and binds them.
    // Returning nothing left such an instance with no Readiness row and no start
    // pre-flight at all - the hole clause 8 closed for the commented-out shape.
    [Fact]
    public void An_instance_with_no_server_properties_still_claims_the_bds_defaults()
    {
        var empty = Path.Combine(_paths.DataRoot, "no-properties-here");
        Directory.CreateDirectory(empty);

        var ports = PortInspector.PortsOf(empty);

        Assert.Equal(
            [("server-port", 19132), ("server-portv6", 19133)],
            ports);
    }

    [Fact]
    public async Task A_free_port_reports_no_conflict()
    {
        WritePackage();
        var registry = Registry();
        var inspector = new PortInspector(registry);

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        Assert.Empty(inspector.Check(created.InstanceId, 19200, skipSystemCheck: true));
    }

    // An instance never conflicts with itself. Without this the properties page
    // would flag its own port the moment anyone opened it.
    [Fact]
    public async Task An_instance_does_not_conflict_with_its_own_port()
    {
        WritePackage();
        var registry = Registry();
        var inspector = new PortInspector(registry);

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        Assert.Empty(inspector.Check(created.InstanceId, 19132, skipSystemCheck: true));
    }

    // D24. Bedrock does take 19132 and 19133 when LAN visibility is on, but only
    // the first instance started gets them; the rest soft-fail and run on their
    // own ports without retrying. So it is a start-order problem, not something
    // a saved file can predict. Listing the pair as held by every such instance
    // turned one contended resource into a conflict between every pair of them,
    // and rejected the save that would have turned the setting on: the one thing
    // every instance after the first needed.
    [Fact]
    public async Task An_instance_holds_only_the_ports_its_own_file_names()
    {
        WritePackage();
        var registry = Registry();
        var inspector = new PortInspector(registry);

        await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19500));
        var second = await registry.CreateAsync(
            new NewInstanceRequest("Creative", "bedrock-server-1.26.36.1.zip", 19600));

        Assert.Empty(inspector.Check(second.InstanceId, 19132, skipSystemCheck: true));
        Assert.Empty(inspector.Check(second.InstanceId, 19133, skipSystemCheck: true));

        // And the check that matters is untouched: a port another instance
        // really does name is still reported.
        Assert.NotEmpty(inspector.Check(second.InstanceId, 19500, skipSystemCheck: true));
    }

    /// <summary>
    /// `T113`. The wrapper takes the LAN discovery pair itself and keeps it for the
    /// instance that configured those ports (`D24`, `T82`), releasing it when that
    /// instance starts. The readiness check reported that as a conflict against the
    /// very instance it was being kept for.
    /// </summary>
    /// <remarks>
    /// The facts were right — the port was held, by that process — and the meaning
    /// was wrong. It made the one instance guaranteed to get its ports the only one
    /// carrying a warning, under wording about crash loops that does not apply, and
    /// the obvious remedies make things worse: `T82` records that turning LAN
    /// visibility off leaves a server nothing can join.
    ///
    /// The socket here is bound by the test process, which is what
    /// <c>Environment.ProcessId</c> reports, so it stands in for the wrapper's own
    /// hold exactly. Note this runs with the system check *on* — every other case in
    /// this file skips it, which is why the branch went unnoticed.
    /// </remarks>
    [WindowsFact]
    public async Task Our_own_hold_on_an_instances_port_is_not_a_conflict_for_it()
    {
        WritePackage();
        var registry = Registry();
        var inspector = new PortInspector(registry);

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 24500));

        using var ourHold = new UdpClient(new IPEndPoint(IPAddress.Any, 24500));

        Assert.Empty(inspector.Check(created.InstanceId, 24500));

        // The guard is narrow. Our own process holding a port this instance does not
        // claim — 24501 is its v6 port, so this has to reach well past the pair — is
        // still worth reporting.
        using var elsewhere = new UdpClient(new IPEndPoint(IPAddress.Any, 24600));

        Assert.NotEmpty(inspector.Check(created.InstanceId, 24600));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task A_number_that_is_not_a_port_is_refused(int port)
    {
        var registry = Registry();
        var inspector = new PortInspector(registry);

        await Task.CompletedTask;

        Assert.Contains("1 to 65535", Assert.Single(inspector.Check("survival", port)).Reason);
    }

    // D14. The reversible half: nothing on disk is touched, and the config entry
    // stays so the instance's player history keeps a name attached to it.
    [Fact]
    public async Task Retiring_stops_managing_it_and_leaves_everything_else_alone()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var directory = created.WorkingDirectory;

        await registry.RetireAsync(created.InstanceId);

        Assert.Empty(registry.All);
        Assert.False(registry.TryGet(created.InstanceId, out _));
        Assert.True(Directory.Exists(directory));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        Assert.True(
            document.RootElement
                .GetProperty("Sentry").GetProperty("Instances").GetProperty("Servers")
                .GetProperty(created.InstanceId).GetProperty("Retired").GetBoolean());
    }

    // Retiring is only reversible if something can still see the instance. It has
    // no supervisor by design, so it is absent from every live view — which is
    // exactly how a "reversible" action becomes a one-way door.
    [Fact]
    public async Task A_retired_instance_is_still_listed()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        await registry.RetireAsync(created.InstanceId);

        var retired = Assert.Single(registry.Retired);

        Assert.Equal(created.InstanceId, retired.InstanceId);
        Assert.Equal("Survival", retired.Name);
        Assert.Equal(created.WorkingDirectory, retired.WorkingDirectory);
    }

    [Fact]
    public async Task Retiring_and_restoring_leaves_the_config_exactly_as_it_was()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        var before = await File.ReadAllTextAsync(ConfigPath);

        await registry.RetireAsync(created.InstanceId);
        var restored = await registry.RestoreAsync(created.InstanceId);

        // Byte for byte. The Retired key is removed rather than written back as
        // false, so a round trip leaves nothing behind to show it happened —
        // which is what "reversible" ought to mean.
        Assert.Equal(before, await File.ReadAllTextAsync(ConfigPath));

        Assert.Single(registry.All);
        Assert.Empty(registry.Retired);
        Assert.True(registry.TryGet(created.InstanceId, out _));
        Assert.False(restored.Definition.Retired);
        Assert.Equal(created.WorkingDirectory, restored.WorkingDirectory);
    }

    // Auto-start means "come up with the service", and a restore is not a service
    // start. Bringing a server back and having it immediately accept players is
    // not something to do as a side effect of a Restore button.
    [Fact]
    public async Task Restoring_leaves_the_server_stopped_even_with_auto_start_on()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132, AutoStart: true));

        await registry.RetireAsync(created.InstanceId);
        var restored = await registry.RestoreAsync(created.InstanceId);

        Assert.Equal(InstanceState.Stopped, restored.Status.State);
        Assert.Null(restored.Status.ProcessId);
    }

    [Fact]
    public async Task A_restored_instance_can_be_started()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        await registry.RetireAsync(created.InstanceId);
        var restored = await registry.RestoreAsync(created.InstanceId);

        // The retired definition refuses to start — that is what retirement is.
        // Restoring has to actually clear it, not merely re-register a supervisor
        // that will throw the moment anyone presses Start.
        await restored.StartAsync();

        Assert.Equal(InstanceState.Starting, restored.Status.State);
    }

    [Fact]
    public async Task An_instance_that_is_not_retired_cannot_be_restored()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.RestoreAsync(created.InstanceId));
    }

    [Fact]
    public async Task An_instance_that_was_removed_cannot_be_restored()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        await registry.RemoveAsync(
            created.InstanceId, new RemoveInstanceRequest(DeleteFiles: false, ForgetPlayers: false));

        Assert.Empty(registry.Retired);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => registry.RestoreAsync(created.InstanceId));
    }

    [Fact]
    public async Task Removing_without_deleting_files_keeps_the_world()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var directory = created.WorkingDirectory;

        var outcome = await registry.RemoveAsync(
            created.InstanceId, new RemoveInstanceRequest(DeleteFiles: false, ForgetPlayers: false));

        Assert.Equal(DirectoryRemoval.NotAsked, outcome.Files);
        Assert.Null(outcome.FilesFailedBecause);
        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(Path.Combine(directory, "bedrock_server.exe")));

        // The entry has to actually go, not be set to null: a key that still
        // exists binds to a default and the instance comes back on next start.
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        Assert.False(
            document.RootElement
                .GetProperty("Sentry").GetProperty("Instances").GetProperty("Servers")
                .TryGetProperty(created.InstanceId, out _));
    }

    [Fact]
    public async Task Removing_with_delete_takes_the_files_too()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var directory = created.WorkingDirectory;

        var outcome = await registry.RemoveAsync(
            created.InstanceId, new RemoveInstanceRequest(DeleteFiles: true, ForgetPlayers: false));

        Assert.Equal(DirectoryRemoval.Deleted, outcome.Files);
        Assert.False(Directory.Exists(directory));
        Assert.Empty(registry.All);
    }

    // D39. The archives live outside the instance directory, which is why
    // deleting the world leaves them behind — a per-instance backup folder
    // outlived its instance and nothing listed or pruned it.
    [Fact]
    public async Task Removing_leaves_the_backups_alone_unless_they_are_asked_for()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var backups = BackupFolderFor(created.InstanceId);

        var outcome = await registry.RemoveAsync(
            created.InstanceId, new RemoveInstanceRequest(DeleteFiles: true, ForgetPlayers: false));

        Assert.NotEqual(DirectoryRemoval.Deleted, outcome.Backups);
        Assert.True(Directory.Exists(backups));
        Assert.True(File.Exists(Path.Combine(backups, "backup-20260827-120000.zip")));
    }

    [Fact]
    public async Task Removing_takes_the_backups_when_they_are_asked_for()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var backups = BackupFolderFor(created.InstanceId);

        var outcome = await registry.RemoveAsync(
            created.InstanceId,
            new RemoveInstanceRequest(DeleteFiles: true, ForgetPlayers: false, DeleteBackups: true));

        Assert.Equal(DirectoryRemoval.Deleted, outcome.Backups);
        Assert.Null(outcome.BackupsFailedBecause);
        Assert.False(Directory.Exists(backups));
    }

    /// <summary>
    /// A delete that gets part-way reports that it got part-way — not that the
    /// folder was kept (`T176`).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the arm that had no test, and it was the only arm that could
    /// lie.</strong> Every assertion around this outcome checked the success path —
    /// <c>Assert.Null(BackupsFailedBecause)</c> or a boolean — so nothing covered
    /// what happens when the delete throws. On the box it destroyed a history file,
    /// threw on the locked archive, and the panel said the archives were kept.
    /// </para>
    /// <para>
    /// <strong>The failure is injected, and that is deliberate rather than lazy.</strong>
    /// Reproducing it for real needs a file lock Windows enforces and Unix does not —
    /// two earlier rigs were tried here and both <em>succeeded</em> in deleting
    /// everything, one because an open handle does not block <c>unlink</c> and one
    /// because the tests run as root. Skipping off Windows would leave this arm
    /// uncovered on the platform the code is written on, which is how it survived in
    /// the first place. So the seam deletes a file and then throws, exactly as
    /// <c>Directory.Delete</c> does when it meets something it cannot remove. Whether
    /// a real lock produces that exception is Windows' business (<c>T176</c>); whether
    /// a thrown delete is *reported* as a partial one is this.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_delete_that_fails_part_way_says_so_rather_than_saying_it_kept_the_folder()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var backups = BackupFolderFor(created.InstanceId);

        // Two entries, so there is something to lose as well as something to fail on.
        var doomed = Path.Combine(backups, "history.json");
        File.WriteAllText(doomed, "{}");
        var survivor = Path.Combine(backups, "backup-20260827-120000.zip");

        registry.DeleteDirectoryTree = directory =>
        {
            // What Directory.Delete(recursive: true) does: deletes everything it
            // can, records the first failure, and rethrows at the end, reporting
            // nothing about how far it got.
            //
            // This comment used to say it stops at the first thing it cannot remove
            // (T186). The rig below was right and the sentence describing it was
            // wrong — it deletes all the unblocked files and only then throws, which
            // is the real behaviour. The stop-at-first claim was disproved twice, on
            // two independent folders, by checking whether an entry sorting after the
            // locked one had survived; it had not, on either.
            //
            // Ordered on purpose. GetFiles makes no promise about order, and the
            // first version of this rig threw before it had deleted anything on some
            // runs - a test whose own destruction is non-deterministic cannot assert
            // what was lost.
            var blocked = Directory.GetFiles(directory)
                .Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var file in Directory.GetFiles(directory).Except(blocked))
            {
                File.Delete(file);
            }

            if (blocked.FirstOrDefault() is { } held)
            {
                throw new IOException(
                    $"The process cannot access the file '{Path.GetFileName(held)}' "
                    + "because it is being used by another process.");
            }
        };

        var outcome = await registry.RemoveAsync(
            created.InstanceId,
            new RemoveInstanceRequest(DeleteFiles: true, ForgetPlayers: false, DeleteBackups: true));

        Assert.Equal(DirectoryRemoval.PartlyDeleted, outcome.Backups);
        Assert.NotNull(outcome.BackupsFailedBecause);

        // The whole point: something really was destroyed, and the outcome may not
        // call that "kept" or "not asked for".
        Assert.False(File.Exists(doomed));
        Assert.True(File.Exists(survivor));
        Assert.NotEqual(DirectoryRemoval.Refused, outcome.Backups);
        Assert.NotEqual(DirectoryRemoval.NotAsked, outcome.Backups);

        // And the log line says so as a sentence rather than splicing the exception
        // into a list of successes (`T179`).
        Assert.StartsWith(
            "PARTLY DELETED",
            DirectoryRemovalPhrase.For(outcome.Backups, outcome.BackupsFailedBecause));
    }

    /// <summary>
    /// A transcript tree that part-deletes is reported, not discarded (<c>T185</c>).
    /// </summary>
    /// <remarks>
    /// <para>The console transcripts are a separate directory under
    /// <c>logs\instances</c>, and <c>DeleteFiles</c> promises them too. Their
    /// outcome used to go into a <c>ref ignored</c> and be thrown away, so a tree
    /// that half-deleted produced a Warning saying <em>"files were deleted"</em> and
    /// a panel saying the files and world were deleted — <c>T176</c>'s exact thesis
    /// on a third directory, left standing by <c>T176</c>'s own fix.</para>
    ///
    /// <para>The working directory succeeds here on purpose. That is the arm that
    /// can lie: if the world folder had failed too, the outcome would read
    /// <c>PartlyDeleted</c> for its own reasons and the transcript's result could go
    /// on being discarded unnoticed.</para>
    /// </remarks>
    [Fact]
    public async Task A_transcript_tree_that_fails_part_way_is_not_reported_as_a_clean_removal()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        var transcripts = Path.Combine(_dataRoot, "logs", "instances", created.InstanceId);
        Directory.CreateDirectory(transcripts);
        File.WriteAllText(Path.Combine(transcripts, "console-20260914.log"), "x");

        registry.DeleteDirectoryTree = directory =>
        {
            if (directory.Contains("logs", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "The process cannot access the file 'console-20260914.log' "
                    + "because it is being used by another process.");
            }

            Directory.Delete(directory, recursive: true);
        };

        var outcome = await registry.RemoveAsync(
            created.InstanceId,
            new RemoveInstanceRequest(DeleteFiles: true, ForgetPlayers: false, DeleteBackups: false));

        // `T190`. The transcript failure is reported in ITS OWN SLOT. This used to
        // assert `outcome.Files == PartlyDeleted`, because the fix reported the
        // transcripts by overwriting the working directory's result — and the panel
        // then told the operator part of the world was already gone, and to go and
        // check a folder that this very test proves is no longer there.
        Assert.Equal(DirectoryRemoval.PartlyDeleted, outcome.Transcripts);
        Assert.NotNull(outcome.TranscriptsFailedBecause);
        Assert.Contains("console-20260914.log", outcome.TranscriptsFailedBecause);

        // `T192`. THE OUTCOME CARRIES THE TRANSCRIPTS' OWN PATH, and the assertion
        // that matters is the second one: it must not be the working directory. On
        // this very execution the working directory was deleted SUCCESSFULLY, so a
        // notice printing it beside a transcript failure sends the reader to a folder
        // that is both the wrong one and no longer there.
        //
        // This is what the fix for `T190` removed. Before it, the failure text was a
        // hand-authored fragment that spliced the path in; the fix replaced it with
        // the raw exception message and the path went with it — and `T190`'s severity
        // bound had leaned on that path being printed.
        Assert.Equal(transcripts, outcome.TranscriptDirectory);
        Assert.NotEqual(created.WorkingDirectory, outcome.TranscriptDirectory);

        // The world really did go, and `Files` now says so rather than being made to
        // carry a second meaning. This assertion is the whole defect: with the
        // promotion in place, Files read PartlyDeleted for a directory that is gone.
        Assert.False(Directory.Exists(created.WorkingDirectory));
        Assert.Equal(DirectoryRemoval.Deleted, outcome.Files);
        Assert.Null(outcome.FilesFailedBecause);

        // A log tree is not world data, so the notice must not shout (`T190`).
        Assert.False(outcome.AnythingLost);

        // And the log line still marks it as a failure rather than listing it as a
        // success — in the transcripts slot, which is where it happened.
        Assert.StartsWith(
            "PARTLY DELETED",
            DirectoryRemovalPhrase.For(outcome.Transcripts, outcome.TranscriptsFailedBecause));
        Assert.Equal(
            "deleted",
            DirectoryRemovalPhrase.For(outcome.Files, outcome.FilesFailedBecause));
    }

    /// <summary>
    /// The transcripts can never come back <c>Refused</c>, so the panel does not offer
    /// a sentence for it.
    /// </summary>
    /// <remarks>
    /// <para><c>T192</c>, and the observation behind it was not a defect — it was an
    /// unreachable arm, which is the <c>T188</c> shape. <c>RemovalNotice.razor</c>
    /// tested <c>PartlyDeleted or Refused</c> for the transcripts, but they are
    /// assigned only from <c>TryDeleteDirectory</c>, whose returns do not include
    /// <c>Refused</c>. So the arm could not fire — and had it fired, its wording
    /// (<em>"were not all deleted"</em>) would have been wrong, because <c>Refused</c>
    /// means nothing was touched. Only the files arm has a refusing guard, because
    /// only the working directory has something worth refusing over.</para>
    ///
    /// <para><strong>Deleting the arm is half the fix; this is the other half.</strong>
    /// An unreachable branch removed on a reading is a fact nothing holds, and the next
    /// author adding a refusal for transcripts would re-create the wrong sentence with
    /// no warning. Pinned here so that change fails the suite instead.</para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_transcript_removal_never_reports_refused(bool transcriptDeleteThrows)
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        var transcripts = Path.Combine(_dataRoot, "logs", "instances", created.InstanceId);
        Directory.CreateDirectory(transcripts);
        File.WriteAllText(Path.Combine(transcripts, "console-20260915.log"), "x");

        // Both ways the transcript delete can end, driven through the one seam that
        // assigns it. Neither yields Refused, and there is no second assigning caller.
        registry.DeleteDirectoryTree = directory =>
        {
            if (directory.Contains("logs", StringComparison.OrdinalIgnoreCase)
                && transcriptDeleteThrows)
            {
                throw new IOException("a log file is in use");
            }

            Directory.Delete(directory, recursive: true);
        };

        var outcome = await registry.RemoveAsync(
            created.InstanceId,
            new RemoveInstanceRequest(DeleteFiles: true, ForgetPlayers: false, DeleteBackups: false));

        Assert.NotEqual(DirectoryRemoval.Refused, outcome.Transcripts);

        // They were attempted, so the path is there for the panel to print (`T192`).
        Assert.NotEqual(DirectoryRemoval.NotAsked, outcome.Transcripts);
        Assert.Equal(transcripts, outcome.TranscriptDirectory);
    }

    // Independent of the files, deliberately: somebody keeping the folder to
    // rebuild from may still want the archives gone, and the reverse.
    [Fact]
    public async Task The_backups_choice_is_independent_of_the_files_choice()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var directory = created.WorkingDirectory;
        var backups = BackupFolderFor(created.InstanceId);

        var outcome = await registry.RemoveAsync(
            created.InstanceId,
            new RemoveInstanceRequest(DeleteFiles: false, ForgetPlayers: false, DeleteBackups: true));

        Assert.NotEqual(DirectoryRemoval.Deleted, outcome.Files);
        Assert.True(Directory.Exists(directory));
        Assert.Equal(DirectoryRemoval.Deleted, outcome.Backups);
        Assert.False(Directory.Exists(backups));
    }

    // An instance that never took a backup has no folder. That is nothing to do,
    // not a failure — the removal must not report a refusal for it.
    [Fact]
    public async Task Asking_to_delete_backups_that_do_not_exist_is_not_a_refusal()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        var outcome = await registry.RemoveAsync(
            created.InstanceId,
            new RemoveInstanceRequest(DeleteFiles: true, ForgetPlayers: false, DeleteBackups: true));

        Assert.Null(outcome.BackupsFailedBecause);
        Assert.Equal(DirectoryRemoval.NotAsked, outcome.Backups);
        Assert.Empty(registry.All);
    }

    /// <summary>Gives an instance a backup folder with one archive in it.</summary>
    private string BackupFolderFor(string instanceId)
    {
        var directory = Path.Combine(_paths.Backups, instanceId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "backup-20260827-120000.zip"), "archive");
        File.WriteAllText(Path.Combine(directory, "history.json"), "[]");
        return directory;
    }

    // The guard has to hold through the registry, not only in isolation: an
    // instance pointed somewhere it should not be still gets removed from the
    // wrapper, and the outcome says plainly that its files were left behind.
    //
    // This is deliberately not registered through AdoptAsync — the folder holds
    // no server.properties or executable, which AdoptAsync now correctly
    // refuses on its own. AllowExistingFiles reaches into the registry the way
    // a hand-edited WorkingDirectory once could, so RemoveAsync's own guard —
    // the one under test here — is exercised in isolation from CreateAsync's.
    [Fact]
    public async Task A_directory_that_may_not_be_deleted_is_reported_rather_than_deleted()
    {
        var elsewhere = Path.Combine(_dataRoot, "not-a-server");
        Directory.CreateDirectory(elsewhere);
        await File.WriteAllTextAsync(Path.Combine(elsewhere, "taxes.pdf"), "x");

        var registry = Registry();
        var created = await registry.CreateAsync(
            new NewInstanceRequest("Adopted", WorkingDirectory: elsewhere, AllowExistingFiles: true));

        var outcome = await registry.RemoveAsync(
            created.InstanceId, new RemoveInstanceRequest(DeleteFiles: true, ForgetPlayers: false));

        Assert.Equal(DirectoryRemoval.Refused, outcome.Files);
        Assert.NotNull(outcome.FilesFailedBecause);
        Assert.True(Directory.Exists(elsewhere));
        Assert.Empty(registry.All);
    }

    // A server killed mid-write leaves a world that may not open again — which
    // matters most in exactly the case where the administrator asked to *keep*
    // the files. Removing an instance must not be the thing that corrupts the
    // world it just handed back.
    [Fact]
    public async Task A_running_instance_is_stopped_properly_before_it_is_removed()
    {
        WritePackage();
        _factory.ProcessesExitOnStop = true;
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132, StartNow: true));

        Assert.Equal(InstanceState.Starting, created.Status.State);
        var process = _factory.Latest;

        await registry.RemoveAsync(
            created.InstanceId, new RemoveInstanceRequest(DeleteFiles: false, ForgetPlayers: false));

        Assert.Contains("stop", process.Sent);
        Assert.False(process.Killed);
    }

    [Fact]
    public async Task Removing_one_instance_leaves_the_others_alone()
    {
        WritePackage();
        var registry = Registry();

        var first = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var second = await registry.CreateAsync(
            new NewInstanceRequest("Creative", "bedrock-server-1.26.36.1.zip", 19134));

        await registry.RemoveAsync(
            first.InstanceId, new RemoveInstanceRequest(DeleteFiles: true, ForgetPlayers: false));

        Assert.Single(registry.All);
        Assert.True(registry.TryGet(second.InstanceId, out _));
        Assert.True(Directory.Exists(second.WorkingDirectory));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        Assert.True(
            document.RootElement
                .GetProperty("Sentry").GetProperty("Instances").GetProperty("Servers")
                .TryGetProperty(second.InstanceId, out _));
    }

    // Both, because either alone is a lie of a different kind: memory only and
    // it reverts at the next service start, file only and it looks like the
    // toggle did nothing.
    [Fact]
    public async Task Auto_start_is_written_to_memory_and_to_the_file()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132, AutoStart: true));

        await registry.SetAutoStartAsync(created.InstanceId, false);

        Assert.False(created.Definition.AutoStart);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        Assert.False(
            document.RootElement
                .GetProperty("Sentry").GetProperty("Instances").GetProperty("Servers")
                .GetProperty(created.InstanceId).GetProperty("AutoStart").GetBoolean());
    }

    // Milestone 16: the rest of instance management, editable from the UI rather
    // than only from bedrocksentry.json.
    [Fact]
    public async Task Renaming_changes_the_name_and_nothing_else()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        var id = created.InstanceId;
        var directory = created.WorkingDirectory;

        await registry.RenameAsync(id, "  The Long Dark  ");

        Assert.Equal("The Long Dark", created.Definition.Name);

        // T89: asserting the definition alone is what let this ship broken. The
        // published status carries its own copy of the name, and every surface a
        // person actually looks at — the Instances tab strip, the Status page
        // table — reads that copy rather than the definition. It stayed stale for
        // the supervisor's lifetime, so one page load showed the new name in the
        // title and the old one in the strip.
        Assert.Equal("The Long Dark", created.Status.Name);
        Assert.Equal("The Long Dark", Assert.Single(registry.Statuses()).Name);

        // The identity is what everything else hangs off: the config key, the
        // folder on disk, and every session row already attributed to it (D14).
        // A rename that moved any of those would be a migration wearing a text
        // box.
        Assert.Equal(id, created.InstanceId);
        Assert.Equal(directory, created.WorkingDirectory);
        Assert.True(Directory.Exists(directory));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        var servers = document.RootElement
            .GetProperty("Sentry").GetProperty("Instances").GetProperty("Servers");

        Assert.Equal("The Long Dark", servers.GetProperty(id).GetProperty("Name").GetString());
    }

    /// <summary>
    /// `Statuses()` orders by the name it publishes, so a rename has to move the
    /// row as well as relabel it (`T89`).
    /// </summary>
    /// <remarks>
    /// Worth its own test rather than an extra assertion above: the ordering is a
    /// second reader of the same stale copy, and it fails in a quieter way. A
    /// mislabelled row is obvious once you look; a correctly labelled row sorted
    /// under its old name just looks like the list is in no particular order.
    /// </remarks>
    [Fact]
    public async Task Renaming_reorders_the_published_statuses()
    {
        WritePackage();
        var registry = Registry();

        var alpha = await registry.CreateAsync(
            new NewInstanceRequest("Alpha", "bedrock-server-1.26.36.1.zip", 19132));
        var zulu = await registry.CreateAsync(
            new NewInstanceRequest("Zulu", "bedrock-server-1.26.36.1.zip", 19136));

        Assert.Equal(["Alpha", "Zulu"], registry.Statuses().Select(s => s.Name));

        // Rename the one that sorts first to something that sorts last.
        await registry.RenameAsync(alpha.InstanceId, "Zebra");

        Assert.Equal(["Zebra", "Zulu"], registry.Statuses().Select(s => s.Name));

        // And back the other way, so a passing test cannot be an accident of the
        // order these two were created in.
        await registry.RenameAsync(zulu.InstanceId, "Aardvark");

        Assert.Equal(["Aardvark", "Zebra"], registry.Statuses().Select(s => s.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Renaming_to_nothing_is_refused(string name)
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.RenameAsync(created.InstanceId, name));

        Assert.Equal("Survival", created.Definition.Name);
    }

    [Fact]
    public async Task Watchdog_settings_are_written_to_memory_and_to_the_file()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        await registry.SetWatchdogAsync(created.InstanceId, new WatchdogOptions
        {
            MaxConsecutiveFailures = 7,
            InitialBackoff = TimeSpan.FromSeconds(20),
            MaxBackoff = TimeSpan.FromMinutes(9),
            BackoffMultiplier = 2,
            StableAfter = TimeSpan.FromMinutes(4),
        });

        Assert.Equal(7, created.Definition.Watchdog.MaxConsecutiveFailures);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        var watchdog = document.RootElement
            .GetProperty("Sentry").GetProperty("Instances").GetProperty("Servers")
            .GetProperty(created.InstanceId).GetProperty("Watchdog");

        Assert.Equal(7, watchdog.GetProperty("MaxConsecutiveFailures").GetInt32());
        Assert.Equal("00:00:20", watchdog.GetProperty("InitialBackoff").GetString());
        Assert.Equal("00:09:00", watchdog.GetProperty("MaxBackoff").GetString());
        Assert.Equal("00:04:00", watchdog.GetProperty("StableAfter").GetString());
        Assert.Equal(2, watchdog.GetProperty("BackoffMultiplier").GetDouble());
    }

    /// <summary>
    /// What the panel wrote is what the next service start reads.
    /// </summary>
    /// <remarks>
    /// The three settings this milestone made editable are the ones whose types
    /// do not survive a careless round trip — <see cref="TimeSpan"/> and
    /// <see cref="TimeOnly"/> both have several plausible spellings and the
    /// configuration binder accepts one of them. Asserting the JSON alone would
    /// pass on a file the wrapper cannot read back, which presents as settings
    /// that silently revert on restart.
    /// </remarks>
    [Fact]
    public async Task Everything_the_panel_writes_binds_back_to_the_same_values()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        await registry.RenameAsync(created.InstanceId, "The Long Dark");

        await registry.SetWatchdogAsync(created.InstanceId, new WatchdogOptions
        {
            MaxConsecutiveFailures = 7,
            InitialBackoff = TimeSpan.FromSeconds(20),
            MaxBackoff = TimeSpan.FromMinutes(9),
            BackoffMultiplier = 2.5,
            StableAfter = TimeSpan.FromMinutes(4),
        });

        await registry.SetRestartScheduleAsync(created.InstanceId, new RestartSchedule
        {
            Mode = RestartScheduleMode.DailyAt,
            IntervalHours = 6,
            IntervalMinutes = 45,
            TimeOfDay = new TimeOnly(4, 30),
            Warnings = [TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10)],
        });

        var bound = new ConfigurationBuilder()
            .AddJsonFile(ConfigPath)
            .Build()
            .GetSection(InstanceOptions.SectionName)
            .Get<InstanceOptions>()!
            .Servers[created.InstanceId];

        Assert.Equal("The Long Dark", bound.Name);

        Assert.Equal(7, bound.Watchdog.MaxConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(20), bound.Watchdog.InitialBackoff);
        Assert.Equal(TimeSpan.FromMinutes(9), bound.Watchdog.MaxBackoff);
        Assert.Equal(2.5, bound.Watchdog.BackoffMultiplier);
        Assert.Equal(TimeSpan.FromMinutes(4), bound.Watchdog.StableAfter);

        Assert.Equal(RestartScheduleMode.DailyAt, bound.RestartSchedule.Mode);
        Assert.Equal(6, bound.RestartSchedule.IntervalHours);
        Assert.Equal(45, bound.RestartSchedule.IntervalMinutes);
        Assert.Equal(new TimeOnly(4, 30), bound.RestartSchedule.TimeOfDay);

        Assert.Equal(
            [TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10)],
            bound.RestartSchedule.WarningsOrDefault);
    }

    [Fact]
    public async Task A_restart_schedule_is_written_to_memory_and_to_the_file()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        await registry.SetRestartScheduleAsync(created.InstanceId, new RestartSchedule
        {
            Mode = RestartScheduleMode.DailyAt,
            TimeOfDay = new TimeOnly(4, 30),
            Warnings = [TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1)],
        });

        Assert.Equal(RestartScheduleMode.DailyAt, created.Definition.RestartSchedule.Mode);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        var schedule = document.RootElement
            .GetProperty("Sentry").GetProperty("Instances").GetProperty("Servers")
            .GetProperty(created.InstanceId).GetProperty("RestartSchedule");

        Assert.Equal("DailyAt", schedule.GetProperty("Mode").GetString());
        Assert.Equal("04:30", schedule.GetProperty("TimeOfDay").GetString());
        Assert.Equal(
            ["00:10:00", "00:01:00"],
            schedule.GetProperty("Warnings").EnumerateArray().Select(w => w.GetString()));
    }

    /// <summary>
    /// Shortening a countdown has to actually shorten it.
    /// </summary>
    /// <remarks>
    /// The warning points are a list, and the configuration binder appends to a
    /// collection rather than replacing it — so a merge that left the old
    /// elements in place would keep announcing the points somebody had just
    /// removed, with nothing on the page to explain where they came from.
    /// </remarks>
    [Fact]
    public async Task Shortening_the_warning_list_replaces_it_rather_than_merging()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        await registry.SetRestartScheduleAsync(created.InstanceId, new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            Warnings =
            [
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(1),
            ],
        });

        await registry.SetRestartScheduleAsync(created.InstanceId, new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            Warnings = [TimeSpan.FromMinutes(2)],
        });

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        var warnings = document.RootElement
            .GetProperty("Sentry").GetProperty("Instances").GetProperty("Servers")
            .GetProperty(created.InstanceId)
            .GetProperty("RestartSchedule").GetProperty("Warnings");

        Assert.Equal(["00:02:00"], warnings.EnumerateArray().Select(w => w.GetString()));
    }

    /// <summary>
    /// "Warn nobody" has to survive a service restart.
    /// </summary>
    /// <remarks>
    /// The configuration system emits no keys for an empty JSON array, so an
    /// empty warning list is indistinguishable from the property being absent —
    /// and absent means the fifteen-minute default. A zero-length point is the
    /// documented way to say it, and it is filtered out when the countdown is
    /// planned, which leaves no warnings and no lead. Written as a round trip
    /// because that is the only way the bug shows: the file looks right either
    /// way.
    /// </remarks>
    [Fact]
    public async Task No_warning_at_all_does_not_bind_back_to_the_default()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        await registry.SetRestartScheduleAsync(created.InstanceId, new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            Warnings = [TimeSpan.Zero],
        });

        var bound = new ConfigurationBuilder()
            .AddJsonFile(ConfigPath)
            .Build()
            .GetSection(InstanceOptions.SectionName)
            .Get<InstanceOptions>()!
            .Servers[created.InstanceId];

        Assert.Equal([TimeSpan.Zero], bound.RestartSchedule.WarningsOrDefault);
        Assert.DoesNotContain(TimeSpan.FromMinutes(15), bound.RestartSchedule.WarningsOrDefault);
    }

    // ---- storage: per-instance folders, the instances root, and adoption ----

    [Fact]
    public async Task A_chosen_folder_is_honoured_and_recorded()
    {
        WritePackage();
        var registry = Registry();
        var chosen = Path.Combine(_dataRoot, "elsewhere", "survival");

        var created = await registry.CreateAsync(new NewInstanceRequest(
            "Survival", "bedrock-server-1.26.36.1.zip", 19132, WorkingDirectory: chosen));

        Assert.Equal(Path.GetFullPath(chosen), created.WorkingDirectory);
        Assert.True(File.Exists(Path.Combine(chosen, "bedrock_server.exe")));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        Assert.Equal(
            chosen,
            document.RootElement
                .GetProperty("Sentry").GetProperty("Instances").GetProperty("Servers")
                .GetProperty(created.InstanceId).GetProperty("WorkingDirectory").GetString());
    }

    // A relative path would resolve against the service's own working directory,
    // which for a Windows Service is System32.
    [Fact]
    public async Task A_relative_folder_is_refused()
    {
        WritePackage();
        var registry = Registry();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.CreateAsync(new NewInstanceRequest(
                "Survival", "bedrock-server-1.26.36.1.zip", 19132,
                WorkingDirectory: "servers/survival")));
    }

    [Fact]
    public async Task Installing_into_a_folder_that_already_has_files_is_refused()
    {
        WritePackage();
        var registry = Registry();
        var occupied = Path.Combine(_dataRoot, "occupied");

        Directory.CreateDirectory(occupied);
        await File.WriteAllTextAsync(Path.Combine(occupied, "bedrock_server.exe"), "an old server");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.CreateAsync(new NewInstanceRequest(
                "Survival", "bedrock-server-1.26.36.1.zip", 19132, WorkingDirectory: occupied)));

        // And the refusal recorded nothing — the folder's contents are untouched
        // and no instance exists.
        Assert.Equal("an old server", await File.ReadAllTextAsync(Path.Combine(occupied, "bedrock_server.exe")));
        Assert.Empty(registry.All);
    }

    [Fact]
    public async Task Two_instances_can_never_share_a_folder()
    {
        WritePackage();
        var registry = Registry();
        var shared = Path.Combine(_dataRoot, "shared");

        await registry.CreateAsync(new NewInstanceRequest(
            "Survival", "bedrock-server-1.26.36.1.zip", 19132, WorkingDirectory: shared));

        // Equal, and nested in either direction — a server inside another
        // server's folder is backed up by both and restored by either.
        foreach (var overlap in new[]
                 {
                     shared,
                     Path.Combine(shared, "inner"),
                     Path.GetDirectoryName(shared)!,
                 })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => registry.CreateAsync(new NewInstanceRequest(
                    "Creative", "bedrock-server-1.26.36.1.zip", 19134, WorkingDirectory: overlap)));
        }
    }

    [Fact]
    public async Task Changing_the_root_pins_existing_instances_where_they_are()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        var before = created.WorkingDirectory;
        var newRoot = Path.Combine(_dataRoot, "volume-d", "servers");

        await registry.SetRootAsync(newRoot);

        // The existing server did not move, and its folder is now recorded
        // explicitly so the next service start resolves it the same way instead
        // of re-deriving it from the root that just changed.
        Assert.Equal(before, created.WorkingDirectory);
        Assert.Equal(before, created.Definition.WorkingDirectory);

        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath)))
        {
            var sentry = document.RootElement.GetProperty("Sentry").GetProperty("Instances");

            Assert.Equal(Path.GetFullPath(newRoot), sentry.GetProperty("Root").GetString());
            Assert.Equal(
                before,
                sentry.GetProperty("Servers").GetProperty(created.InstanceId)
                    .GetProperty("WorkingDirectory").GetString());
        }

        // And a server created afterwards lands under the new root.
        var later = await registry.CreateAsync(
            new NewInstanceRequest("Creative", "bedrock-server-1.26.36.1.zip", 19134));

        Assert.StartsWith(
            Path.GetFullPath(newRoot) + Path.DirectorySeparatorChar,
            later.WorkingDirectory,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stopped_server_can_be_rehomed_and_the_new_path_sticks()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        var newHome = Path.Combine(_dataRoot, "volume-d", "survival");

        // Empty target: the files have not been moved yet, and the caller is
        // told so — starting now would just be a crash with extra steps.
        var ready = await registry.SetWorkingDirectoryAsync(created.InstanceId, newHome);
        Assert.False(ready);

        // The supervisor was rebuilt against the new folder — same id, same
        // definition, new home.
        var rebuilt = registry.Get(created.InstanceId);
        Assert.Equal(Path.GetFullPath(newHome), rebuilt.WorkingDirectory);
        Assert.Equal("Survival", rebuilt.Definition.Name);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        Assert.Equal(
            Path.GetFullPath(newHome),
            document.RootElement.GetProperty("Sentry").GetProperty("Instances")
                .GetProperty("Servers").GetProperty(created.InstanceId)
                .GetProperty("WorkingDirectory").GetString());
    }

    [Fact]
    public async Task Rehoming_to_a_folder_that_already_holds_the_server_reports_ready()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        // The admin moved the files first, then points the wrapper at them —
        // the other legitimate order.
        var newHome = Path.Combine(_dataRoot, "volume-d", "survival");
        Directory.CreateDirectory(newHome);
        File.WriteAllText(Path.Combine(newHome, "bedrock_server.exe"), "x");

        Assert.True(await registry.SetWorkingDirectoryAsync(created.InstanceId, newHome));
    }

    [Fact]
    public async Task A_running_server_cannot_be_rehomed()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        _factory.ProcessesExitOnStop = true;
        await created.StartAsync();
        _factory.Latest.Ready();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.SetWorkingDirectoryAsync(
                created.InstanceId, Path.Combine(_dataRoot, "volume-d", "survival")));

        Assert.Contains("Stop the server", refusal.Message);
        Assert.Equal(
            InstanceLayout.ResolveWorkingDirectory(registry.Root, created.InstanceId, created.Definition),
            registry.Get(created.InstanceId).WorkingDirectory);
    }

    [Fact]
    public async Task Rehoming_onto_another_instances_folder_is_refused()
    {
        WritePackage();
        var registry = Registry();

        var survival = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var creative = await registry.CreateAsync(
            new NewInstanceRequest("Creative", "bedrock-server-1.26.36.1.zip", 19134));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.SetWorkingDirectoryAsync(creative.InstanceId, survival.WorkingDirectory));

        // But re-stating its own current folder is a no-op, not an overlap with
        // itself.
        Assert.True(await registry.SetWorkingDirectoryAsync(
            creative.InstanceId, creative.WorkingDirectory));
    }

    [Fact]
    public async Task Adopting_registers_the_folder_without_installing_anything()
    {
        var registry = Registry();
        var existing = Path.Combine(_dataRoot, "old-server");

        Directory.CreateDirectory(existing);
        await File.WriteAllTextAsync(Path.Combine(existing, "bedrock_server.exe"), "their binary");
        await File.WriteAllTextAsync(
            Path.Combine(existing, "server.properties"),
            "server-name=The Long Haul\nserver-port=19140\nlevel-name=Big World\n");
        await File.WriteAllTextAsync(Path.Combine(existing, "allowlist.json"), "[]");
        Directory.CreateDirectory(Path.Combine(existing, "worlds", "Big World"));

        var adopted = await registry.AdoptAsync("The Long Haul", existing);

        Assert.Equal(Path.GetFullPath(existing), adopted.WorkingDirectory);

        // Adoption is the caller saying "manage what is there", so nothing the
        // folder already owned may change: the binary is theirs, the port is
        // theirs, the world is theirs. Auto-start defaults off because the old
        // arrangement may still be running the server.
        Assert.False(adopted.Definition.AutoStart);
        Assert.Equal("their binary", await File.ReadAllTextAsync(Path.Combine(existing, "bedrock_server.exe")));

        var properties = ServerProperties.Read(Path.Combine(existing, "server.properties"));
        Assert.Equal("19140", properties["server-port"]);
        Assert.Equal("The Long Haul", properties["server-name"]);
        Assert.True(Directory.Exists(Path.Combine(existing, "worlds", "Big World")));
    }

    [Fact]
    public async Task Adopting_a_folder_with_no_server_in_it_is_refused()
    {
        var registry = Registry();
        var empty = Path.Combine(_dataRoot, "not-a-server");
        Directory.CreateDirectory(empty);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.AdoptAsync("Nothing", empty));

        Assert.Contains("bedrock_server.exe", refusal.Message);
        Assert.Empty(registry.All);
    }

    [Fact]
    public async Task Adopting_an_already_managed_folder_is_refused()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.AdoptAsync("Again", created.WorkingDirectory));

        Assert.Contains("already managed", refusal.Message);
    }

    [Fact]
    public async Task Adopting_inside_the_wrappers_own_data_is_refused()
    {
        var registry = Registry();
        var inside = Path.Combine(_paths.Backups, "survival");

        Directory.CreateDirectory(inside);
        await File.WriteAllTextAsync(Path.Combine(inside, "bedrock_server.exe"), "x");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.AdoptAsync("Sneaky", inside));
    }

    [Fact]
    public void The_inspector_reads_what_the_folder_says_about_itself()
    {
        var existing = Path.Combine(_dataRoot, "inspect-me");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "bedrock_server.exe"), "x");
        File.WriteAllText(
            Path.Combine(existing, "server.properties"),
            "server-name=Old Faithful\nserver-port=19144\nlevel-name=Geyser\n");

        var report = BdsFolderInspector.Inspect(existing);

        Assert.True(report.Exists);
        Assert.True(report.HasExecutable);
        Assert.Equal("Old Faithful", report.ServerName);
        Assert.Equal(19144, report.Port);
        Assert.Equal("Geyser", report.WorldName);

        Assert.False(BdsFolderInspector.Inspect(Path.Combine(_dataRoot, "missing")).Exists);
    }

    [Fact]
    public async Task A_blank_name_is_rejected()
    {
        var registry = Registry();

        await Assert.ThrowsAsync<ArgumentException>(
            () => registry.CreateAsync(new NewInstanceRequest("   ")));
    }

    // Nothing is recorded until the files are down, so a failed install leaves no
    // half-made instance to clean up before trying again.
    [Fact]
    public async Task A_missing_package_leaves_no_instance_behind()
    {
        var registry = Registry();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.CreateAsync(new NewInstanceRequest("Survival", "not-there.zip")));

        Assert.Empty(registry.All);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task An_explicit_working_directory_is_honoured_and_recorded()
    {
        WritePackage();
        var elsewhere = Path.Combine(_dataRoot, "other-volume", "big-world");
        var registry = Registry();

        var created = await registry.CreateAsync(new NewInstanceRequest(
            "Survival", "bedrock-server-1.26.36.1.zip", WorkingDirectory: elsewhere));

        Assert.Equal(Path.GetFullPath(elsewhere), created.WorkingDirectory);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        Assert.Equal(
            elsewhere,
            document.RootElement.GetProperty("Sentry").GetProperty("Instances")
                .GetProperty("Servers").GetProperty("survival")
                .GetProperty("WorkingDirectory").GetString());
    }

    // A default, name-derived folder used to skip the overlap check entirely —
    // only an explicitly chosen folder was validated. Adopting a folder under
    // a different display name than its own, then creating a fresh instance
    // whose default folder happens to land on the same place, used to succeed
    // silently: two supervisors over one folder.
    [Fact]
    public async Task A_default_folder_that_collides_with_an_adopted_instance_is_refused()
    {
        var registry = Registry();

        var adoptedFolder = Path.Combine(registry.Root, "Survival");
        Directory.CreateDirectory(adoptedFolder);
        await File.WriteAllTextAsync(Path.Combine(adoptedFolder, "bedrock_server.exe"), "their binary");
        await registry.AdoptAsync("Old Survival", adoptedFolder);

        WritePackage();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.CreateAsync(
                new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132)));

        Assert.Single(registry.All);
    }

    // The non-empty-folder guard used to run only when a package was being
    // extracted, so a packageless create pointed at an existing install — the
    // form's own Folder field makes this one click away — could silently take
    // it over and rewrite its server.properties (port, allow-list, gamemode).
    [Fact]
    public async Task A_packageless_create_into_a_non_empty_folder_is_also_refused()
    {
        var registry = Registry();
        var occupied = Path.Combine(_dataRoot, "someone-elses-server");

        Directory.CreateDirectory(occupied);
        await File.WriteAllTextAsync(
            Path.Combine(occupied, "server.properties"), "server-port=19140\n");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.CreateAsync(new NewInstanceRequest("Survival", WorkingDirectory: occupied)));

        Assert.Equal(
            "server-port=19140\n", await File.ReadAllTextAsync(Path.Combine(occupied, "server.properties")));
        Assert.Empty(registry.All);
    }

    // AdoptAsync's whole contract is "nothing changes but server-name" — the
    // shared property-writing path used to add emit-server-telemetry=false
    // whenever the key was absent, true of nearly every real BDS install,
    // reaching adoption just as much as a fresh package extraction.
    [Fact]
    public async Task Adopting_never_writes_a_telemetry_line_the_folder_never_had()
    {
        var registry = Registry();
        var directory = Path.Combine(_dataRoot, "old-server");

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "bedrock_server.exe"), "their binary");
        await File.WriteAllLinesAsync(
            ServerProperties.PathFor(directory), ["server-name=Old Server"]);

        await registry.AdoptAsync("Old Server", directory);

        Assert.DoesNotContain(
            "emit-server-telemetry",
            ServerProperties.Read(ServerProperties.PathFor(directory)).Keys);
    }

    // The default folder is derived from the display name, not the id — so a
    // rename with nothing pinned yet used to leave config's notion of this
    // instance's folder pointed at a name it no longer has. Invisible until a
    // later root change re-derived the path from the new name and durably
    // pinned a folder the instance never actually ran from.
    [Fact]
    public async Task Renaming_pins_the_folder_so_a_later_root_change_cannot_mis_pin_it()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));

        var originalFolder = created.WorkingDirectory;
        Assert.Null(created.Definition.WorkingDirectory);

        await registry.RenameAsync(created.InstanceId, "Creative Hub");

        // Pinned to the folder it actually runs from, not re-derived from the
        // new name.
        Assert.Equal(originalFolder, created.Definition.WorkingDirectory);
        Assert.Equal(originalFolder, created.WorkingDirectory);

        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath)))
        {
            var server = document.RootElement
                .GetProperty("Sentry").GetProperty("Instances").GetProperty("Servers")
                .GetProperty(created.InstanceId);

            Assert.Equal("Creative Hub", server.GetProperty("Name").GetString());
            Assert.Equal(originalFolder, server.GetProperty("WorkingDirectory").GetString());
        }

        await registry.SetRootAsync(Path.Combine(_dataRoot, "volume-d", "servers"));

        Assert.Equal(originalFolder, created.Definition.WorkingDirectory);
        Assert.Equal(originalFolder, created.WorkingDirectory);
    }

    // The same drift the constructor's OnChange handler warns about: a hand
    // edit to the config file, made since this process loaded it, that this
    // process's Options.Servers snapshot does not know about. Pinning only
    // what is in memory would write the new root over an instance that is
    // sitting right there in the file, unpinned.
    [Fact]
    public async Task A_root_change_is_refused_when_the_config_file_was_edited_by_hand()
    {
        WritePackage();
        var registry = Registry();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", "bedrock-server-1.26.36.1.zip", 19132));
        var before = created.WorkingDirectory;

        var text = await File.ReadAllTextAsync(ConfigPath);
        var edited = JsonNode.Parse(text)!.AsObject();
        edited["Sentry"]!["Instances"]!["Servers"]!["ghost"] =
            JsonNode.Parse("""{ "Name": "Ghost" }""");
        await File.WriteAllTextAsync(ConfigPath, edited.ToJsonString());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.SetRootAsync(Path.Combine(_dataRoot, "volume-d", "servers")));

        Assert.Contains("hand", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Refused before anything changed — not merely reported afterwards.
        Assert.Equal(before, created.WorkingDirectory);
        Assert.Null(created.Definition.WorkingDirectory);

        using var after = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        Assert.False(
            after.RootElement.GetProperty("Sentry").GetProperty("Instances").TryGetProperty("Root", out _));
    }
    // ---- D43 / D52: the operating system's own directories -------------------

    /// <summary>
    /// Points the guard at a pretend OS tree under this test's own temp folder. The
    /// real rules name paths a test must not create files in.
    /// </summary>
    private (InstanceRegistry Registry, string Reserved) RegistryWithAPretendOs()
    {
        var registry = Registry();
        var reserved = Path.Combine(_dataRoot, "pretend-os");

        registry.SystemDirectories =
            [new ReservedSystemDirectory(reserved, ReservedScope.Subtree, "a pretend Windows")];

        return (registry, reserved);
    }

    /// <summary>
    /// THE ONE THAT MATTERS: a refused folder is never created.
    /// </summary>
    /// <remarks>
    /// The guard sits above <c>Directory.CreateDirectory</c>, and this is the assertion
    /// that keeps it there. A refusal that has already made the folder has done the one
    /// thing it existed to prevent — and on the OS directories, "made a folder in
    /// C:\Windows and then said no" is worse than either outcome alone.
    /// </remarks>
    [Fact]
    public async Task A_refused_folder_is_never_created()
    {
        var (registry, reserved) = RegistryWithAPretendOs();
        await using var _ = registry;

        var wanted = Path.Combine(reserved, "bedrock");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.CreateAsync(new NewInstanceRequest("Survival", WorkingDirectory: wanted)));

        Assert.Contains("a pretend Windows", refusal.Message);
        Assert.Contains("such as", refusal.Message);

        Assert.False(Directory.Exists(wanted));
        Assert.False(Directory.Exists(reserved));
    }

    /// <summary>
    /// Adoption refuses with the <em>guard's</em> message, not "does not exist".
    /// </summary>
    /// <remarks>
    /// The guard used to sit below both the existence check and the executable check,
    /// so the operator got a true-but-useless sentence about a folder they can see.
    /// This pins the hoist.
    /// </remarks>
    [Fact]
    public async Task Adopting_a_folder_under_an_operating_system_directory_is_refused()
    {
        var (registry, reserved) = RegistryWithAPretendOs();
        await using var _ = registry;

        var existing = Path.Combine(reserved, "bedrock");
        Directory.CreateDirectory(existing);
        await File.WriteAllTextAsync(Path.Combine(existing, "bedrock_server.exe"), "x");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.AdoptAsync("Theirs", existing));

        Assert.Contains("a pretend Windows", refusal.Message);
        Assert.DoesNotContain("does not exist", refusal.Message);
        Assert.DoesNotContain("does not look like", refusal.Message);
    }

    [Fact]
    public async Task Re_homing_into_a_refused_folder_is_refused_and_changes_nothing()
    {
        var (registry, reserved) = RegistryWithAPretendOs();
        await using var _ = registry;

        WritePackage();
        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", PackageFileName: "bedrock-server-1.26.36.1.zip"));

        var before = created.WorkingDirectory;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.SetWorkingDirectoryAsync(created.InstanceId, Path.Combine(reserved, "moved")));

        Assert.Equal(before, registry.Get(created.InstanceId).WorkingDirectory);
        Assert.False(Directory.Exists(Path.Combine(reserved, "moved")));
    }

    /// <summary>
    /// A share is refused at every door, on both platforms.
    /// </summary>
    /// <remarks>
    /// Unconditional because the check runs on the raw input before
    /// <c>Path.GetFullPath</c>, which on Linux would mangle a UNC string into something
    /// unrecognisable and make this Windows-only for no reason.
    /// </remarks>
    [Fact]
    public async Task A_network_share_is_refused_at_creation_and_at_adoption_and_when_re_homing()
    {
        await using var registry = Registry();
        WritePackage();

        const string Share = @"\\fileserver\bedrock\survival";

        var create = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.CreateAsync(new NewInstanceRequest("Survival", WorkingDirectory: Share)));
        Assert.Contains("network share", create.Message);

        var adopt = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.AdoptAsync("Theirs", Share));
        Assert.Contains("network share", adopt.Message);

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Elsewhere", PackageFileName: "bedrock-server-1.26.36.1.zip"));

        var rehome = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.SetWorkingDirectoryAsync(created.InstanceId, Share));
        Assert.Contains("network share", rehome.Message);
    }

    // The hole the extended-path rule closes: \\?\ bypasses every path rule, because
    // Path.GetFullPath leaves it alone and \\?\C:\Windows does not equal C:\Windows.
    [Fact]
    public async Task An_extended_length_path_is_refused_rather_than_slipping_past_the_rules()
    {
        await using var registry = Registry();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.CreateAsync(
                new NewInstanceRequest("Survival", WorkingDirectory: @"\\?\C:\Windows\bedrock")));

        Assert.Contains("extended-length or device path", refusal.Message);
    }

    /// <summary>
    /// The instances root is refused by the same rules, at the moment it is set.
    /// </summary>
    /// <remarks>
    /// Without this the root is accepted happily and every create after it fails,
    /// with the reason a page away from the setting that caused it — and because
    /// re-homing refuses the same paths, there would be no way forward from inside
    /// the panel.
    /// </remarks>
    [Fact]
    public async Task Pointing_the_instances_root_at_an_operating_system_directory_is_refused()
    {
        var (registry, reserved) = RegistryWithAPretendOs();
        await using var _ = registry;

        var before = registry.Root;

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.SetRootAsync(Path.Combine(reserved, "servers")));

        Assert.Contains("New servers would be created", refusal.Message);
        Assert.Contains("a pretend Windows", refusal.Message);

        Assert.Equal(before, registry.Root);
        Assert.False(Directory.Exists(Path.Combine(reserved, "servers")));
    }

    /// <summary>
    /// The adopt form's pre-flight says exactly what the refusal says.
    /// </summary>
    /// <remarks>
    /// <para>Not "they call the same method" — that is what <c>T151</c> clause 6 did,
    /// and it made two callers agree on an answer only one of them wanted. Here the
    /// pre-flight genuinely wants the refusal's answer, and what pins it is the
    /// <em>string</em>: if the two ever diverge, the operator is told one thing while
    /// typing and another when they press the button.</para>
    ///
    /// <para>The form itself lives in <c>Host</c>, which the suite cannot reference —
    /// hence the rule being in <c>Services</c> and this test reaching the method the
    /// <c>.razor</c> calls. That is the fifth lesson of the recurring class applied
    /// rather than re-learnt.</para>
    /// </remarks>
    [Fact]
    public async Task The_adopt_pre_flight_gives_the_same_sentence_as_the_refusal()
    {
        var (registry, reserved) = RegistryWithAPretendOs();
        await using var _ = registry;

        var existing = Path.Combine(reserved, "bedrock");
        Directory.CreateDirectory(existing);
        await File.WriteAllTextAsync(Path.Combine(existing, "bedrock_server.exe"), "x");

        var preFlight = registry.WhyThisFolderCannotHoldAServer(existing);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.AdoptAsync("Theirs", existing));

        Assert.NotNull(preFlight);
        Assert.Equal(refusal.Message, preFlight);

        // A share, which is the case the pre-flight was wrong about: it used to read
        // "That folder does not exist, or the path is not a full path".
        var share = registry.WhyThisFolderCannotHoldAServer(@"\\fileserver\bedrock");
        Assert.NotNull(share);
        Assert.Contains("network share", share);

        // And a folder that is fine gets no sentence at all, on either platform.
        Assert.Null(registry.WhyThisFolderCannotHoldAServer(Path.Combine(_dataRoot, "ordinary")));

        // Half-typed is not an error. This runs on every keystroke.
        Assert.Null(registry.WhyThisFolderCannotHoldAServer("D"));
        Assert.Null(registry.WhyThisFolderCannotHoldAServer(""));
    }

    /// <summary>
    /// The carve-out, against the REAL rules rather than a pretend tree.
    /// </summary>
    /// <remarks>
    /// There is no carve-out branch in the code — the wrapper's own instances root is
    /// allowed because the ProgramData rule is folder-itself. This is where that is
    /// enforced, and it is deliberately run with <c>ReservedSystemDirectories.All</c>
    /// so a rule added later that swallows the instances root fails here.
    /// </remarks>
    [Fact]
    public async Task The_configured_instances_root_is_not_refused_by_the_real_rules()
    {
        await using var registry = Registry();
        Assert.Equal(ReservedSystemDirectories.All, registry.SystemDirectories);

        WritePackage();

        var created = await registry.CreateAsync(
            new NewInstanceRequest("Survival", PackageFileName: "bedrock-server-1.26.36.1.zip"));

        Assert.True(Directory.Exists(created.WorkingDirectory));
    }
}

// InstanceRegistry.PathsOverlap is internal (InternalsVisibleTo) precisely so
// this path math can be checked directly, against constructed paths — a
// volume root is not something a test should point real filesystem
// operations at.
public sealed class PathsOverlapTests
{
    // Path.TrimEndingDirectorySeparator deliberately never trims a bare
    // volume root, so appending a separator unconditionally compares against
    // a double separator that can never match — an instance adopted at a
    // drive root used to never be found to overlap anything else on it.
    [Fact]
    public void A_volume_root_overlaps_everything_beneath_it()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var child = Path.Combine(root, "bedrock", "survival");

        Assert.True(InstanceRegistry.PathsOverlap(root, child));
        Assert.True(InstanceRegistry.PathsOverlap(child, root));
    }

    [Fact]
    public void A_volume_root_equals_itself()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.True(InstanceRegistry.PathsOverlap(root, root));
    }

    // The separator has to matter: without it, "foo" would be read as
    // containing "foobar".
    [Fact]
    public void Similarly_named_siblings_under_a_root_do_not_overlap()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var a = Path.Combine(root, "foo");
        var b = Path.Combine(root, "foobar");

        Assert.False(InstanceRegistry.PathsOverlap(a, b));
        Assert.False(InstanceRegistry.PathsOverlap(b, a));
    }

}
