using System.Text.Json;
using System.Text.Json.Serialization;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Packages;
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
/// <param name="Package">
/// The version of the package this wrapper last laid down here, or null when it
/// never has (<c>D53</c>). What a Preview build is identified by, since its own
/// announcement does not parse.
/// </param>
/// <param name="PackageReported">
/// What the server announced on the first start after that install, or null when
/// it has not started on those files yet. A later announcement that differs means
/// somebody has replaced the files, and <see cref="Package"/> no longer describes
/// them (<see cref="BedrockSentry.Core.Packages.InstalledBuild"/>).
/// </param>
public sealed record ServerVersionRecord(
    string Version,
    DateTimeOffset FirstSeenUtc,
    string? PreviousVersion,
    bool Reported = true,
    string? Package = null,
    string? PackageReported = null)
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

            // The first announcement on files this wrapper installed says what those
            // files call themselves, which is what keeps the package an identity only
            // while it is still true (`D53`).
            var firstOnPackage = reported && existing is { Package: not null, PackageReported: null };
            var packageReported = firstOnPackage ? version : existing?.PackageReported;

            if (existing is not null
                && string.Equals(existing.Version, version, StringComparison.OrdinalIgnoreCase))
            {
                // Same version. Reinstalling a build over itself is a repair, not an
                // update, and dating it as one would be wrong. The one thing worth
                // writing is that a server has now confirmed what was only installed.
                if ((!reported || existing.Reported) && !firstOnPackage)
                {
                    return null;
                }

                Confirm(instanceId, existing with
                {
                    Reported = existing.Reported || reported,
                    PackageReported = packageReported,
                });

                return null;
            }

            // An install onto a stopped server recorded the package's own version, and
            // a Preview build announces itself in another spelling: 1.26.60.21 installed,
            // 1.26.60-beta21 announced. The same files, so a confirmation rather than an
            // update that would date the build twice. An announcement that parses and
            // differs is a different build and falls through to be recorded as one.
            if (firstOnPackage
                && !existing!.Reported
                && (InstalledBuild.Parse(version) is not { } announced
                    || announced == InstalledBuild.Parse(existing.Package)))
            {
                Confirm(instanceId, existing with
                {
                    Version = version,
                    Reported = true,
                    PackageReported = version,
                });

                return null;
            }

            var record = new ServerVersionRecord(
                version, atUtc, existing?.Version, reported, existing?.Package, packageReported);

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

    /// <summary>
    /// Notes the package this wrapper has just laid down on an instance (<c>D53</c>).
    /// </summary>
    /// <param name="package">The package's version, from its file name.</param>
    /// <param name="reportedNow">
    /// What the server has already announced on those files, or null when it has not
    /// started on them yet; the next announcement then fills it in.
    /// </param>
    /// <remarks>
    /// Kept apart from <see cref="ServerVersionRecord.Version"/>'s history: the package is an identity for
    /// comparing against an offer, and <see cref="ServerVersionRecord.FirstSeenUtc"/>
    /// dates what the server said. A server with no record yet, one just created from
    /// a package, gets one saying the package is installed and has not run.
    /// </remarks>
    public void RecordPackage(
        string instanceId, System.Version package, string? reportedNow, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(package);

        lock (_gate)
        {
            var existing = Read(instanceId);
            var reported = reportedNow is { Length: > 0 } ? reportedNow.Trim() : null;

            Confirm(instanceId, existing is null
                ? new ServerVersionRecord(
                    reported ?? package.ToString(), atUtc, null, reported is not null,
                    package.ToString(), reported)
                : existing with { Package = package.ToString(), PackageReported = reported });
        }
    }

    private void Confirm(string instanceId, ServerVersionRecord record)
    {
        try
        {
            AtomicFile.WriteAllText(FileFor(instanceId), JsonSerializer.Serialize(record, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not record the version for {InstanceId}", instanceId);
        }
    }

    /// <summary>
    /// Forgets an instance's record: at removal (<c>D14</c>), and before a new
    /// instance is created under an id a removed one may have left a record for
    /// (<c>T315</c>).
    /// </summary>
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
