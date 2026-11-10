using System.Collections.Concurrent;
using System.Globalization;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Packages;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Packages;

/// <summary>The Bedrock files an update kept, when a copy is still on disk.</summary>
/// <param name="Path">The folder, so a message can name what to look for.</param>
/// <param name="Taken">
/// When the update that kept them ran, or null if the folder's name cannot be
/// read as a stamp — somebody may have renamed or hand-made it.
/// </param>
/// <param name="Bytes">What it costs, which is the reason to mention it at all.</param>
public sealed record KeptBuild(string Path, DateTimeOffset? Taken, long Bytes);

/// <summary>What an update attempt did.</summary>
/// <param name="Applied">The build is now installed and, if it was running, healthy.</param>
/// <param name="RolledBack">It was applied, failed to come up, and was put back.</param>
/// <param name="Detail">One sentence for the page, always populated.</param>
public sealed record BdsInstallResult(
    bool Applied,
    bool RolledBack,
    string Version,
    string Detail);

/// <summary>
/// Applies a Bedrock build to an instance (<c>docs/design.md</c> § 11).
/// </summary>
/// <remarks>
/// The sequence is the one every other planned outage in this codebase uses —
/// warn the players, drain, back up if that instance asked for it, do the work,
/// start again — because it is one method with several callers rather than
/// three implementations that drift. What is specific to an update is only the
/// middle: replace what the package contains, keep everything it does not.
///
/// Then the health gate, which is the whole reason this is safe to offer. A
/// server that does not report itself started within the budget is not
/// something to leave in place and hope about: the previous binaries go back and
/// it is started again on the version that was working. Mojang ships the
/// occasional build that will not run on somebody's box, and finding that out at
/// 3am with no way back is precisely what this exists to prevent.
/// </remarks>
public sealed class BdsUpdateInstaller
{
    private readonly ServerPackageStore _packages;
    private readonly ShutdownBackups _shutdownBackups;
    private readonly ServerVersionLog _versions;
    private readonly TimeProvider _time;
    private readonly ILogger<BdsUpdateInstaller> _log;
    private readonly TimeSpan _healthTimeout;
    private readonly TimeSpan _drainBudget;

    public BdsUpdateInstaller(
        ServerPackageStore packages,
        ShutdownBackups shutdownBackups,
        ServerVersionLog versions,
        TimeProvider time,
        ILogger<BdsUpdateInstaller> log,
        TimeSpan? healthTimeout = null,
        TimeSpan? drainBudget = null)
    {
        _packages = packages;
        _shutdownBackups = shutdownBackups;
        _versions = versions;
        _time = time;
        _log = log;

        // Generous. A first launch on a new build regenerates caches and the
        // measured warm start was 1.6 seconds, so this is not a performance
        // budget — it is the point at which "not started yet" becomes "not
        // going to start".
        _healthTimeout = healthTimeout ?? TimeSpan.FromMinutes(3);
        _drainBudget = drainBudget ?? TimeSpan.FromSeconds(45);
    }

    /// <summary>
    /// The countdown an update somebody just clicked gets: a minute's notice,
    /// then ten seconds, then down.
    /// </summary>
    /// <remarks>
    /// Deliberately not the instance's restart-schedule countdown, which
    /// defaults to fifteen minutes. That number is right for an unattended
    /// event people plan their evening around, and wrong for a button labelled
    /// "Update now" — the operator is at the keyboard and has asked for it. An
    /// <see cref="BdsUpdateMode.Automatic"/> update is the unattended case and
    /// gets the full schedule instead, which is why this is a parameter rather
    /// than a constant inside the method.
    /// </remarks>
    public static readonly IReadOnlyList<TimeSpan> PromptWarnings =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromSeconds(10),
    ];

    /// <param name="warnings">
    /// When to warn players before the server goes down. Null uses the
    /// instance's restart-schedule countdown; empty stops without warning.
    /// </param>
    /// <exception cref="InstanceClaimedException">
    /// Another job claims the server, such as a sync; nothing was stopped or installed.
    /// </exception>
    /// <remarks>
    /// Claimed for its length (<see cref="ClaimText.Update"/>), so no other job's
    /// start or stop lands between the swap and the health check, and a sync cannot
    /// begin on files being replaced.
    /// </remarks>
    public async Task<BdsInstallResult> ApplyAsync(
        InstanceSupervisor supervisor,
        ServerPackage package,
        IReadOnlyList<TimeSpan>? warnings = null,
        CancellationToken cancellationToken = default)
    {
        var version = package.VersionText ?? package.FileName;

        using var claim = await supervisor
            .ClaimAsync(ClaimText.Update(version), cancellationToken)
            .ConfigureAwait(false);

        var wasRunning = supervisor.Status.State is InstanceState.Running or InstanceState.Starting;

        // One kept build, never a pile (`D37`). The copy from an earlier update has
        // served its purpose the moment a newer one starts, and on the stopped path
        // below nothing else ever removes it — so without this an instance updated a
        // few times carries a full copy of every build it has ever been on. Measured
        // at 303 MB each against a 303 MB server (`T115`).
        //
        // This does discard the copy a rolled-back update left for diagnosis, but only
        // when somebody has asked for another update, which is a decision to move on.
        foreach (var older in KeptBuildFolders(supervisor.WorkingDirectory))
        {
            TryRemove(older);
        }

        var stamp = _time.GetUtcNow().ToString(KeptStamp, CultureInfo.InvariantCulture);
        var keptAt = Path.Combine(supervisor.WorkingDirectory, KeptPrefix + stamp);

        ServerPackageStore.ServerUpdateSwap? swap = null;

        async Task SwapAsync(CancellationToken token)
        {
            // The instance's own backup-on-shutdown toggle for updates, which
            // has existed in config since milestone 7 waiting for this caller.
            await _shutdownBackups
                .RunIfEnabledAsync(supervisor, StopReason.ServerUpdate, token)
                .ConfigureAwait(false);

            supervisor.Console.Append(
                ConsoleOrigin.Notice,
                $"installing Bedrock {version}; the world and this server's configuration are untouched",
                _time.GetUtcNow());

            swap = await _packages
                .UpdateInPlaceAsync(package, supervisor.WorkingDirectory, keptAt, token)
                .ConfigureAwait(false);

            // The size cache is only sound for a folder that has stopped growing.
            // The page asking for it ticks once a second and is very likely to have
            // asked while this was still copying, which would otherwise leave it
            // reporting a partial size for good — measured on a real install as
            // 209 MB against an actual 302 MB.
            _keptSizes.TryRemove(keptAt, out _);
        }

        if (!wasRunning)
        {
            // Nothing to warn, drain or health-check. A stopped server is simply
            // updated where it stands, and whether the new build runs is found
            // out when somebody starts it.
            await SwapAsync(cancellationToken).ConfigureAwait(false);

            // The version the last run announced described files that are no longer
            // there, so it stops being the answer the moment they are replaced.
            // Left in place it had the page calling a stopped server "running" the
            // build it had just been moved off, and offering that build again
            // (`T116`).
            supervisor.ForgetReportedVersion();

            // What the wrapper just wrote, which on this path is the only account
            // there will be until somebody starts the server. This used to be
            // `Forget`, which is for removing an instance and took the whole
            // history with it.
            _versions.RecordInstall(supervisor.InstanceId, version, _time.GetUtcNow());
            RecordPackage(supervisor, package, reportedNow: null);

            return new BdsInstallResult(
                true, false, version,
                $"Bedrock {version} installed. The server was stopped, so it has not been "
                + "started to check the new build runs — start it when you are ready.");
        }

        // Warned the same way a scheduled restart warns, because from a player's
        // side it is the same event: the server is going away shortly and they
        // should find somewhere safe to stand. Only the lead time differs, and
        // that is the caller's to choose.
        warnings ??= supervisor.Definition.RestartSchedule.WarningsOrDefault;

        await claim
            .RestartAsync(
                new GracefulStopRequest(
                    StopReason.ServerUpdate,
                    warnings.Count > 0 ? warnings.Max() : TimeSpan.Zero,
                    warnings,
                    _drainBudget)
                {
                    // So the notices name the build, the way the automatic
                    // countdown's do. On this path they are the only notices
                    // players get (`T134`).
                    Version = version,
                },
                cancellationToken,
                whileStopped: SwapAsync)
            .ConfigureAwait(false);

        // Refused, not failed (`T151`). The start pre-flight faults an instance
        // whose port is held or whose world already has a process on it, and
        // nothing was launched — so waiting out the health timeout would spend
        // minutes proving nothing and then roll back a build that is fine. The
        // build is not the problem here and must not be blamed for it; the kept
        // copy stays on disk either way, because this build has not proven
        // itself yet.
        //
        // Only a refusal (`D38`): a build that crashed fast enough for the watchdog to
        // give up before this line is also Faulted, and that one is rolled back below.
        if (HealthGateText.RefusedBeforeTheGate(supervisor.Status) is { } refusal)
        {
            _log.LogWarning(
                "{InstanceId} did not start after installing Bedrock {Version}: {Reason}",
                supervisor.InstanceId, version, refusal);

            // The new files stay, so they are what the next start will announce.
            RecordPackage(supervisor, package, reportedNow: null);

            return new BdsInstallResult(
                true,
                false,
                version,
                $"Bedrock {version} is installed, but the server did not start: {refusal}");
        }

        // `D38`. The wait ends early when the watchdog gives up on the new build:
        // nothing will start it again, so the rest of the budget would be downtime.
        var outcome = await supervisor
            .WaitForReadyOutcomeAsync(_healthTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is ReadyWait.Ready)
        {
            // Cleared only on success. A rolled-back update leaves the previous
            // binaries on disk deliberately — somebody diagnosing why a build
            // would not run wants them.
            TryRemove(keptAt);

            // The server reports its own version on startup and the log records
            // the change from that, so the version itself is not written here: this
            // would be the wrapper's opinion, and the console's is the fact. Which
            // package it installed IS the wrapper's to say, and is what a Preview
            // build is identified by (`D53`). Only on success: a rolled-back update
            // put the previous files back, and the previous record describes them.
            RecordPackage(supervisor, package, supervisor.Status.ServerVersion);

            return new BdsInstallResult(
                true, false, version, $"Bedrock {version} is installed and the server is running.");
        }

        var why = HealthGateText.WhyRolledBack(version, outcome, _healthTimeout, supervisor.Status);

        _log.LogError(
            "{InstanceId} on Bedrock {Version}: {Why}; rolling back", supervisor.InstanceId, version, why);

        supervisor.Console.Append(
            ConsoleOrigin.Notice, $"{why}; putting the previous build back", _time.GetUtcNow());

        await claim
            .StopAsync(
                GracefulStopRequest.Immediate(StopReason.ServerUpdate, TimeSpan.FromSeconds(30)),
                cancellationToken)
            .ConfigureAwait(false);

        if (swap is not null)
        {
            _packages.RollBack(supervisor.WorkingDirectory, swap);
        }

        await claim.StartAsync(cancellationToken).ConfigureAwait(false);

        var recovered = await supervisor
            .WaitForReadyAsync(_healthTimeout, cancellationToken)
            .ConfigureAwait(false);

        return new BdsInstallResult(
            false,
            true,
            version,
            recovered
                ? $"Bedrock {version} did not start, so the previous build was put back and the "
                  + "server is running again. Pin the working version if you want this build left alone."
                : $"Bedrock {version} did not start and the previous build has been restored, but "
                  + "the server has not reported itself started either. Check the console.");
    }

    private void RecordPackage(InstanceSupervisor supervisor, ServerPackage package, string? reportedNow)
    {
        // A package whose name carries no version is installable but identifies
        // nothing, so it leaves the record as it was.
        if (package.Version is { } installed)
        {
            _versions.RecordPackage(supervisor.InstanceId, installed, reportedNow, _time.GetUtcNow());
        }

        EventRaising.RaiseIndependently(
            Installed, supervisor.InstanceId,
            ex => _log.LogWarning(ex, "A listener threw while being told {Instance} has a new build", supervisor.InstanceId));
    }

    /// <summary>Raised with the instance's id after a package has been installed into it.</summary>
    /// <remarks>
    /// Console completion listens, to read the new version's commands and fetch Mojang's
    /// data for it (<c>D105</c>). A singleton for the life of the service: whatever
    /// subscribes must unsubscribe.
    /// </remarks>
    public event Action<string>? Installed;

    private const string KeptPrefix = ".previous-";
    private const string KeptStamp = "yyyyMMdd-HHmmss";

    // Sizing one of these walks ten thousand files, and the page that asks ticks
    // once a second. A kept build never changes after the update that wrote it, so
    // the answer is cached against the folder's own path and dropped when it goes.
    private readonly ConcurrentDictionary<string, long> _keptSizes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The previous Bedrock files still on disk for an instance, or null when
    /// there are none.
    /// </summary>
    /// <remarks>
    /// Cheap enough for a page that re-reads on every tick: one directory listing,
    /// and a size that is only ever computed once per folder.
    /// </remarks>
    public KeptBuild? KeptBuildFor(string workingDirectory)
    {
        var folders = KeptBuildFolders(workingDirectory);
        if (folders.Count == 0)
        {
            return null;
        }

        // Only one should exist. If a build from before this rule is still around,
        // describe the newest rather than picking arbitrarily.
        var newest = folders[^1];

        var stamp = Path.GetFileName(newest)[KeptPrefix.Length..];

        DateTimeOffset? taken = DateTimeOffset.TryParseExact(
            stamp,
            KeptStamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

        return new KeptBuild(newest, taken, _keptSizes.GetOrAdd(newest, SizeOf));
    }

    /// <summary>
    /// Every kept-build folder in an instance, oldest first.
    /// </summary>
    /// <remarks>
    /// The stamp is fixed-width and most-significant-first, so ordering the names
    /// as text orders them by time — no parsing needed to find the newest.
    /// </remarks>
    private static IReadOnlyList<string> KeptBuildFolders(string workingDirectory)
    {
        try
        {
            var found = Directory.GetDirectories(workingDirectory, KeptPrefix + "*");
            Array.Sort(found, StringComparer.OrdinalIgnoreCase);
            return found;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Being unable to list them is not a reason to fail an update, and the
            // page simply says nothing about a copy it could not see.
            return [];
        }
    }

    private static long SizeOf(string directory)
    {
        try
        {
            var total = 0L;

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One unreadable file makes the total slightly low, which is a
                    // better answer than no total at all.
                }
            }

            return total;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return 0;
        }
    }

    private void TryRemove(string directory)
    {
        _keptSizes.TryRemove(directory, out _);

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover folder is untidy, not dangerous, and its name says what
            // it is.
            _log.LogWarning(ex, "Could not remove {Directory} after a successful update", directory);
        }
    }
}
