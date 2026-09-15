using System.Text;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Configuration;
using Microsoft.Extensions.Configuration;

namespace BedrockSentry.Tests;

public sealed class StarterConfigTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly SentryPaths _paths;

    public StarterConfigTests()
    {
        _dataRoot = Path.Combine(
            Path.GetTempPath(), "bedrocksentry-tests", Guid.NewGuid().ToString("n"));
        _paths = new SentryPaths(_dataRoot);
        Directory.CreateDirectory(_paths.Config);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ConfigFile => Path.Combine(_paths.Config, "bedrocksentry.json");

    [Fact]
    public void Writes_a_starter_file_when_none_exists()
    {
        Assert.True(StarterConfig.EnsureCreated(_paths));
        Assert.True(File.Exists(ConfigFile));
    }

    // Runs on every startup, so this is the property that matters most: a
    // "helpful" merge into a file an administrator has edited is how config gets
    // silently rewritten.
    [Fact]
    public void Never_touches_an_existing_file()
    {
        const string existing = """{ "Sentry": { "Tls": { "Port": 9443 } } }""";
        File.WriteAllText(ConfigFile, existing);

        Assert.False(StarterConfig.EnsureCreated(_paths));
        Assert.Equal(existing, File.ReadAllText(ConfigFile));
    }

    [Fact]
    public void Is_idempotent()
    {
        Assert.True(StarterConfig.EnsureCreated(_paths));
        var first = File.ReadAllText(ConfigFile);

        Assert.False(StarterConfig.EnsureCreated(_paths));
        Assert.Equal(first, File.ReadAllText(ConfigFile));
    }

    // The file is loaded by the configuration system on the very next line of
    // Program.cs, so a malformed template would take the service down on first
    // run — the worst possible moment.
    [Fact]
    public void The_template_is_loadable_configuration()
    {
        StarterConfig.EnsureCreated(_paths);

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(ConfigFile, optional: false, reloadOnChange: false)
            .Build();

        var options = configuration.GetSection(InstanceOptions.SectionName).Get<InstanceOptions>();

        Assert.NotNull(options);
        Assert.Empty(options.Servers);
    }

    // install.ps1 rewrites this file with ConvertFrom-Json under Windows
    // PowerShell 5.1, which rejects comments outright — so the template must not
    // contain any, however useful they would be to read.
    [Fact]
    public void The_template_contains_no_json_comments()
    {
        StarterConfig.EnsureCreated(_paths);

        // Line by line rather than a substring search over the whole file. A
        // bare Contains("//") also fires on a URL inside a string value, which
        // is perfectly good JSON that PowerShell 5.1 parses without complaint —
        // and the default public-address endpoint is one.
        foreach (var line in File.ReadAllLines(ConfigFile))
        {
            var trimmed = line.TrimStart();

            Assert.False(
                trimmed.StartsWith("//") || trimmed.StartsWith("/*"),
                $"The starter config has a comment on: {line}");
        }
    }

    [Fact]
    public void The_template_is_strict_json()
    {
        StarterConfig.EnsureCreated(_paths);

        // Default options: no comment skipping, no trailing commas. This is the
        // strictest reader the file will meet.
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ConfigFile));

        Assert.True(document.RootElement.TryGetProperty("Sentry", out _));
    }

    [Fact]
    public void The_template_points_at_the_documentation()
    {
        StarterConfig.EnsureCreated(_paths);
        var text = File.ReadAllText(ConfigFile);

        Assert.Contains("windows-setup.md", text);
        Assert.Contains("restart", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Is_written_as_utf8_without_a_bom()
    {
        StarterConfig.EnsureCreated(_paths);
        var bytes = File.ReadAllBytes(ConfigFile);

        Assert.False(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "a BOM would break tools that read the file as plain ASCII");
        Assert.Equal(Encoding.UTF8.GetString(bytes), File.ReadAllText(ConfigFile));
    }
}
