using System.Text.Json;
using System.Text.Json.Serialization;
using BedrockSentry.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>What version an instance is on, and when it got there.</summary>
/// <param name="Version">As the server itself reported it.</param>
/// <param name="FirstSeenUtc">When the wrapper first saw this version running.</param>
/// <param name="PreviousVersion">
/// What it was before, or null when this is the first version ever recorded for
/// the instance. That distinction is the whole point: the first sighting is an
/// observation, not an update, and reporting it as one would date every server's
/// "last updated" to whenever the wrapper was installed.
/// </param>
/// <param name="Reported">
/// True when the server itself announced this version at startup, which is the
/// ordinary case and the one this log was built for. False when the wrapper
/// installed the files and no server has run on them since — the only situation
/// where nobody is going to announce anything, because an update applied to a
/// stopped instance leaves it stopped. Defaulted to true so records written
/// before this distinction existed read as what they were.
/// </param>
public sealed record ServerVersionRecord(
    string Version,
    DateTimeOffset FirstSeenUtc,
    string? PreviousVersion,
    bool Reported = true)
{
    /// <summary>True when this record represents a version actually changing.</summary>
    [JsonIgnore]
    public bool IsAnUpdate => PreviousVersion is { Length: > 0 };
}

/// <summary>
/// Remembers which Bedrock version each instance is running, and when it
/// changed (<c>docs/design.md</c> § 11).
/// </summary>
/// <remarks>
/// Driven by what the server prints at startup rather than by what the wrapper
/// did, which is what makes it useful before the wrapper can apply an update at
/// all: somebody who stops a server, unzips a new build over it and starts it
/// again gets an accurate "last updated" out of this, and so does an update the
/// wrapper applies later. Recording only our own updates would leave the field
/// empty on every server that has ever been updated by hand — which today is
/// all of them.
///
/// One small JSON per instance beside the backup history, written through
/// <see cref="AtomicFile"/>. This is observed state rather than configuration,
/// so it deliberately does not live in the config file the administrator edits.
/// </remarks>
public sealed class ServerVersionLog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ISentryPaths _paths;
    private readonly ILogger<ServerVersionLog> _log;
    private readonly object _gate = new();

    public ServerVersionLog(ISentryPaths paths, ILogger<ServerVersionLog> log)
    {
        _paths = paths;
        _log = log;
    }

    private string FileFor(string instanceId) =>
        Path.Combine(_paths.Data, "versions", $"{instanceId}.json");

    /// <summary>What is recorded for this instance, or null when nothing is.</summary>
    public ServerVersionRecord? Read(string instanceId)
    {
        var path = FileFor(instanceId);

        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ServerVersionRecord>(File.ReadAllText(path), Json)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // A missing or unreadable record means "we do not know", which every
            // caller already has to handle. It is not worth failing a page over.
            _log.LogWarning(ex, "Could not read the version record for {InstanceId}", instanceId);
            return null;
        }
    }

    /// <summary>
    /// Notes the version a server just reported. Returns the record if this was
    /// a change, or null when it is the same version already recorded.
    /// </summary>
    /// <remarks>
    /// Called from the console pump, so it does nothing at all in the common
    /// case — a server restarting on the version it was already on writes
    /// nothing and touches no file.
    /// </remarks>
    public ServerVersionRecord? Record(string instanceId, string version, DateTimeOffset atUtc) =>
        Write(instanceId, version, atUtc, reported: true);

    /// <summary>
    /// Notes a version the wrapper has just written to disk, for an instance that
    /// is not running and so will not announce it.
    /// </summary>
    /// <remarks>
    /// Deliberately not used on the running path. There the server starts moments
    /// later and says what it is, and its answer is the fact where this would only
    /// be an opinion — the two disagree the instant anybody unzips a build by hand.
    ///
    /// A stopped update has no such reporter: the server may not start for days, and
    /// until it does the wrapper's own knowledge of what it just wrote is the only
    /// information there is. It is also self-correcting, because the next real
    /// startup goes through <see cref="Record"/> and either agrees, writing nothing,
    /// or replaces this outright.
    ///
    /// This replaced a call to <see cref="Forget"/>, which is for removing an
    /// instance (<c>D14</c>) and threw the whole history away — leaving a server the
    /// wrapper had a first-seen date for reading "unknown" the moment its own update
    /// button was pressed (<c>T116</c>).
    /// </remarks>
    public ServerVersionRecord? RecordInstall(string instanceId, string version, DateTimeOffset atUtc) =>
        Write(instanceId, version, atUtc, reported: false);

    private ServerVersionRecord? Write(
        string instanceId, string version, DateTimeOffset atUtc, bool reported)
    {
        version = version.Trim();

        if (version.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            var existing = Read(instanceId);

            if (existing is not null
                && string.Equals(existing.Version, version, StringComparison.OrdinalIgnoreCase))
            {
                // Same version. Reinstalling a build over itself is a repair, not an
                // update, and dating it as one would be wrong. The one thing worth
                // writing is that a server has now confirmed what was only installed.
                if (!reported || existing.Reported)
                {
                    return null;
                }

                var confirmed = existing with { Reported = true };

                try
                {
                    AtomicFile.WriteAllText(
                        FileFor(instanceId), JsonSerializer.Serialize(confirmed, Json));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning(ex, "Could not record the version for {InstanceId}", instanceId);
                }

                return null;
            }

            var record = new ServerVersionRecord(version, atUtc, existing?.Version, reported);

            try
            {
                AtomicFile.WriteAllText(FileFor(instanceId), JsonSerializer.Serialize(record, Json));

                if (record.IsAnUpdate)
                {
                    _log.LogInformation(
                        "Instance {InstanceId} moved from Bedrock {From} to {To}",
                        instanceId, record.PreviousVersion, record.Version);
                }
                else if (reported)
                {
                    _log.LogInformation(
                        "Instance {InstanceId} is running Bedrock {Version}", instanceId, record.Version);
                }
                else
                {
                    // Not "is running": nothing has started on these files yet, and
                    // saying otherwise in the log is the same mistake the page was
                    // making.
                    _log.LogInformation(
                        "Instance {InstanceId} has Bedrock {Version} installed but has not run it",
                        instanceId, record.Version);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never rethrown: this runs off the console pump, and losing a
                // "when did it change" note is not worth stopping the wrapper
                // reading its server's output.
                _log.LogWarning(ex, "Could not record the version for {InstanceId}", instanceId);
                return null;
            }

            return record;
        }
    }

    /// <summary>Forgets an instance's record, for removal (<c>D14</c>).</summary>
    public void Forget(string instanceId)
    {
        try
        {
            var path = FileFor(instanceId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not remove the version record for {InstanceId}", instanceId);
        }
    }
}
