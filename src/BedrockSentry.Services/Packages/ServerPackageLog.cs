using System.Text.Json;
using System.Text.Json.Serialization;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Packages;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Packages;

/// <summary>
/// What the wrapper knows about the Bedrock zips it has fetched: each zip's channel
/// (<c>T333</c>), and when somebody accepted the Minecraft EULA to fetch the first one
/// (<c>D102</c>).
/// </summary>
/// <remarks>
/// One small JSON file under the data root rather than beside the zips, so it stays put
/// when the package store is moved on Settings → Storage; entries are by file name, which
/// a moved zip keeps. Observed state rather than configuration, so it is not in the config
/// file and not in the settings export. Written through <see cref="AtomicFile"/>.
///
/// A file that exists and cannot be read is left alone: writing then would replace every
/// recorded channel and the acceptance with one line. A file that reads but does not
/// parse is replaced, since nothing in it can be used.
/// </remarks>
public sealed class ServerPackageLog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ISentryPaths _paths;
    private readonly ILogger<ServerPackageLog> _log;
    private readonly object _gate = new();

    public ServerPackageLog(ISentryPaths paths, ILogger<ServerPackageLog> log)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>Where the log lives.</summary>
    public string FilePath => Path.Combine(_paths.Data, "server-packages.json");

    /// <summary>When the Minecraft EULA was accepted here, or null when it has not been.</summary>
    public DateTimeOffset? EulaAcceptedUtc => Load().Contents.MojangEulaAcceptedUtc;

    /// <summary>Every line, oldest first.</summary>
    public IReadOnlyList<PackageLogEntry> Entries() => Load().Contents.Entries;

    /// <summary>The channel last recorded for a zip, or null.</summary>
    public BdsChannel? ChannelOf(string fileName) => PackageChannels.For(fileName, Entries());

    /// <summary>Records that the EULA was accepted, once; a later call changes nothing.</summary>
    /// <returns>False when the log could not be read or written, so nothing was recorded.</returns>
    public bool AcceptEula(DateTimeOffset atUtc)
    {
        lock (_gate)
        {
            var (contents, writable) = Load();

            if (!writable)
            {
                return false;
            }

            if (contents.MojangEulaAcceptedUtc is not null)
            {
                return true;
            }

            if (!Save(contents with { MojangEulaAcceptedUtc = atUtc }))
            {
                return false;
            }

            _log.LogInformation("The Minecraft EULA was accepted to download a Bedrock server");
            return true;
        }
    }

    /// <summary>Adds a line unless the latest one for this zip already says the same.</summary>
    /// <returns>True when a line was added.</returns>
    public bool Record(string fileName, BdsChannel channel, PackageChannelSource source, DateTimeOffset atUtc)
    {
        lock (_gate)
        {
            var (contents, writable) = Load();

            if (!writable || PackageChannels.For(fileName, contents.Entries) == channel)
            {
                return false;
            }

            if (!Save(contents with
                {
                    Entries = [.. contents.Entries, new PackageLogEntry(fileName, channel, atUtc, source)],
                }))
            {
                return false;
            }

            _log.LogInformation("Recorded {FileName} as a {Channel} build ({Source})", fileName, channel, source);
            return true;
        }
    }

    private (LogFile Contents, bool Writable) Load()
    {
        var path = FilePath;
        string text;

        try
        {
            if (!File.Exists(path))
            {
                return (LogFile.Empty, true);
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read the package log {Path}", path);
            return (LogFile.Empty, false);
        }

        try
        {
            var contents = JsonSerializer.Deserialize<LogFile>(text, Json) ?? LogFile.Empty;
            return (contents with { Entries = contents.Entries ?? [] }, true);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "The package log {Path} does not parse, and the next line written replaces it", path);
            return (LogFile.Empty, true);
        }
    }

    /// <remarks>
    /// A failed write is logged and reported, never thrown: the callers are a download that
    /// has already landed and a check that has already read the feed, and neither should fail
    /// over its bookkeeping.
    /// </remarks>
    private bool Save(LogFile contents)
    {
        try
        {
            Directory.CreateDirectory(_paths.Data);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(contents, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not write the package log {Path}", FilePath);
            return false;
        }
    }

    private sealed record LogFile(DateTimeOffset? MojangEulaAcceptedUtc, IReadOnlyList<PackageLogEntry> Entries)
    {
        public static LogFile Empty { get; } = new(null, []);
    }
}
