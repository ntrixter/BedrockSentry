using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;

namespace BedrockSentry.Updater;

/// <summary>
/// Stops the service, points the <c>current</c> junction at the new build, starts it
/// again, and restores the previous build if the new one does not come up healthy.
/// </summary>
/// <remarks>
/// <para>
/// The swap is a junction flip rather than a file copy. Two renames are close to
/// atomic and trivially reversible; copying several hundred files over a stopped
/// service is neither, and a failure halfway through leaves a directory that is
/// neither the old build nor the new one.
/// </para>
/// <para>
/// Health-gating the result is what makes an update safe to leave unattended
/// (<c>D11</c>). Without it, a build that throws on startup takes the web UI down
/// with it — and the web UI is the only remote control surface this product has.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class UpdateRunner
{
    private const string CurrentLinkName = "current";

    public static async Task<int> RunAsync(string planPath)
    {
        UpdatePlan plan;

        try
        {
            plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath))
                ?? throw new InvalidOperationException("The plan file was empty.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read the update plan '{planPath}': {ex.Message}");
            return 3;
        }

        using var log = new UpdateLog(plan.LogPath);

        log.Write($"BedrockSentry updater starting. {plan.PreviousIdentity} -> {plan.TargetIdentity}");

        // Refuse to operate on the tree this process is running from: a lock on a
        // version directory it may prune, or a helper running through the junction it
        // flips, fails after the service is already stopped.
        var here = Path.GetFullPath(AppContext.BaseDirectory);
        var installRoot = Path.GetFullPath(plan.InstallRoot);

        if (HelperLocation.InsideABuild(here, installRoot))
        {
            log.Write($"REFUSED: the helper is running from a build inside {installRoot}. It must be staged elsewhere first.");
            await WriteResultAsync(plan, false, false, "The updater was run from inside a build it would replace.");
            return 4;
        }

        if (!Directory.Exists(plan.TargetVersionDirectory))
        {
            log.Write($"REFUSED: target version directory does not exist: {plan.TargetVersionDirectory}");
            await WriteResultAsync(plan, false, false, "The staged build was missing.");
            return 1;
        }

        if (!Directory.Exists(plan.PreviousVersionDirectory))
        {
            // Without a previous build there is nothing to roll back to, so the whole
            // safety argument for applying the update evaporates.
            log.Write($"REFUSED: previous version directory does not exist: {plan.PreviousVersionDirectory}");
            await WriteResultAsync(plan, false, false, "The previous build was missing, so a rollback would not be possible.");
            return 1;
        }

        var currentLink = Path.Combine(installRoot, CurrentLinkName);

        try
        {
            if (!await StopServicesAsync(plan, log))
            {
                // Nothing has been touched yet, so this is the cheapest possible
                // failure: whatever was stopped starts again on the build it ran.
                TryStartServices(plan, log);
                await WriteResultAsync(plan, false, false, "The service could not be stopped; no files were changed.");
                return 1;
            }

            PointAt(currentLink, plan.TargetVersionDirectory, log);

            StartServices(plan, log);

            var (healthy, detail) = await WaitForHealthAsync(plan, log);

            if (healthy)
            {
                log.Write($"Update applied: now running {plan.TargetIdentity}.");
                Prune(installRoot, plan, log);
                await WriteResultAsync(plan, true, false, $"Updated to {plan.TargetIdentity}.");
                return 0;
            }

            log.Write($"Health check failed: {detail}. Rolling back to {plan.PreviousIdentity}.");

            return await RollBackAsync(plan, currentLink, log, detail);
        }
        catch (Exception ex)
        {
            log.Write($"Unhandled failure: {ex}");
            return await RollBackAsync(plan, currentLink, log, ex.Message);
        }
    }

    /// <summary>The main service, then the broker: the main service may ask the broker for something as it stops.</summary>
    private static async Task<bool> StopServicesAsync(UpdatePlan plan, UpdateLog log)
    {
        if (!await StopServiceAsync(plan, log))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(plan.BrokerServiceName) || StopBroker(plan, log);
    }

    private static bool StopBroker(UpdatePlan plan, UpdateLog log)
    {
        try
        {
            using var broker = new ServiceController(plan.BrokerServiceName);

            if (broker.Status != ServiceControllerStatus.Stopped)
            {
                log.Write($"Stopping {plan.BrokerServiceName}...");
                broker.Stop();
                broker.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(plan.StopTimeoutSeconds));
            }

            return true;
        }
        catch (Exception ex)
        {
            // The main service is already stopped. Starting it again on the build it
            // was running is the cheapest way back, and the caller does that.
            log.Write($"Could not stop {plan.BrokerServiceName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The broker, then the main service, which asks the broker for things as it starts.</summary>
    private static void StartServices(UpdatePlan plan, UpdateLog log)
    {
        if (!string.IsNullOrWhiteSpace(plan.BrokerServiceName))
        {
            using var broker = new ServiceController(plan.BrokerServiceName);
            log.Write($"Starting {plan.BrokerServiceName}...");
            broker.Refresh();

            if (broker.Status == ServiceControllerStatus.Stopped)
            {
                broker.Start();
            }
        }

        StartService(plan, log);
    }

    private static async Task<bool> StopServiceAsync(UpdatePlan plan, UpdateLog log)
    {
        using var service = new ServiceController(plan.ServiceName);

        try
        {
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                log.Write("Service was already stopped.");
                return true;
            }

            log.Write($"Stopping {plan.ServiceName} (timeout {plan.StopTimeoutSeconds}s)...");
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(plan.StopTimeoutSeconds));
            log.Write("Service stopped.");
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            // Fully qualified: System.TimeoutException is a different type and would
            // not be caught here, which would look like an unhandled crash.
            log.Write($"Service did not stop within {plan.StopTimeoutSeconds}s; forcing.");

            if (!KillService(plan, log))
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            log.Write($"Could not stop the service: {ex.Message}");
            return false;
        }

        // Process exit does not mean every handle is released. Renaming the directory
        // while the executable is still mapped fails with a sharing violation.
        return await WaitForUnlockAsync(plan, log);
    }

    private static bool KillService(UpdatePlan plan, UpdateLog log)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("BedrockSentry.Host"))
            {
                using (process)
                {
                    // entireProcessTree matters from milestone 4 onward, when the host
                    // owns bedrock_server.exe children that would otherwise be orphaned.
                    log.Write($"Killing process {process.Id}.");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(30_000);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Write($"Could not force the service to stop: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> WaitForUnlockAsync(UpdatePlan plan, UpdateLog log)
    {
        var target = Path.Combine(plan.PreviousVersionDirectory, "BedrockSentry.Host.exe");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (!File.Exists(target))
                {
                    return true;
                }

                using var handle = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                await Task.Delay(500);
            }
        }

        log.Write("The previous build's files are still locked after 30s; aborting before touching anything.");
        return false;
    }

    /// <summary>Repoints the <c>current</c> junction.</summary>
    /// <remarks>
    /// Created with <c>mklink /J</c> rather than <see cref="Directory.CreateSymbolicLink"/>
    /// so the reparse type matches what <c>install.ps1</c> laid down, and because a
    /// junction needs no <c>SeCreateSymbolicLinkPrivilege</c>.
    /// </remarks>
    private static void PointAt(string linkPath, string targetDirectory, UpdateLog log)
    {
        if (Directory.Exists(linkPath))
        {
            // Non-recursive: on a junction this removes the link and leaves the
            // target and its contents alone.
            Directory.Delete(linkPath, recursive: false);
        }

        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(linkPath);
        psi.ArgumentList.Add(targetDirectory);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start cmd.exe to create the junction.");

        process.WaitForExit(30_000);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"mklink failed ({process.ExitCode}): {process.StandardError.ReadToEnd().Trim()}");
        }

        log.Write($"current -> {Path.GetFileName(targetDirectory)}");
    }

    private static void TryStartServices(UpdatePlan plan, UpdateLog log)
    {
        try
        {
            StartServices(plan, log);
        }
        catch (Exception ex)
        {
            log.Write($"Could not start the services again: {ex.Message}");
        }
    }

    private static void StartService(UpdatePlan plan, UpdateLog log)
    {
        using var service = new ServiceController(plan.ServiceName);

        log.Write($"Starting {plan.ServiceName}...");
        service.Start();
    }

    private static async Task<(bool Healthy, string Detail)> WaitForHealthAsync(UpdatePlan plan, UpdateLog log)
    {
        using var http = HealthProbe.CreateClient();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(plan.HealthTimeoutSeconds);
        var lastDetail = "no response";

        while (DateTimeOffset.UtcNow < deadline)
        {
            // A crashed service fails in seconds rather than burning the whole window.
            try
            {
                using var service = new ServiceController(plan.ServiceName);
                service.Refresh();

                if (service.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
                {
                    return (false, "the service stopped immediately after starting");
                }
            }
            catch (InvalidOperationException)
            {
                return (false, "the service is no longer registered");
            }

            try
            {
                var (verdict, detail) = await HealthProbe.ReadAsync(http, plan.HealthUrl, plan.TargetIdentity);

                if (verdict is not HealthProbe.Verdict.NotYet)
                {
                    return (verdict is HealthProbe.Verdict.Healthy, detail);
                }

                lastDetail = detail;
            }
            catch (Exception ex)
            {
                lastDetail = ex.Message;
            }

            await Task.Delay(2_000);
        }

        log.Write($"Health check timed out after {plan.HealthTimeoutSeconds}s.");
        return (false, $"timed out after {plan.HealthTimeoutSeconds}s ({lastDetail})");
    }

    private static async Task<int> RollBackAsync(UpdatePlan plan, string currentLink, UpdateLog log, string reason)
    {
        try
        {
            using (var service = new ServiceController(plan.ServiceName))
            {
                service.Refresh();

                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(plan.StopTimeoutSeconds));
                }
            }

            if (!string.IsNullOrWhiteSpace(plan.BrokerServiceName))
            {
                StopBroker(plan, log);
            }

            PointAt(currentLink, plan.PreviousVersionDirectory, log);
            StartServices(plan, log);

            log.Write($"Rolled back to {plan.PreviousIdentity}.");

            await WriteResultAsync(plan, false, true,
                $"{plan.TargetIdentity} failed to start ({reason}); rolled back to {plan.PreviousIdentity}.");

            return 1;
        }
        catch (Exception ex)
        {
            // The worst outcome available, and the one worth shouting about: the box
            // may now have no running service at all.
            log.Write($"ROLLBACK FAILED: {ex}");

            await WriteResultAsync(plan, false, false,
                $"{plan.TargetIdentity} failed ({reason}) AND the rollback failed ({ex.Message}). " +
                $"Point '{currentLink}' at '{plan.PreviousVersionDirectory}' by hand and start the service.");

            return 2;
        }
    }

    /// <summary>Removes version directories other than the live one and its predecessor.</summary>
    /// <remarks>
    /// Keeping the previous build is what makes the next update's rollback possible,
    /// so pruning down to one would quietly disarm the safety net.
    /// </remarks>
    private static void Prune(string installRoot, UpdatePlan plan, UpdateLog log)
    {
        try
        {
            var versionsRoot = Path.Combine(installRoot, "versions");

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(plan.TargetVersionDirectory),
                Path.GetFullPath(plan.PreviousVersionDirectory),
            };

            foreach (var directory in Directory.GetDirectories(versionsRoot))
            {
                if (keep.Contains(Path.GetFullPath(directory)))
                {
                    continue;
                }

                Directory.Delete(directory, recursive: true);
                log.Write($"Pruned {Path.GetFileName(directory)}.");
            }
        }
        catch (Exception ex)
        {
            // Disk space, not correctness. The update already succeeded.
            log.Write($"Could not prune old versions: {ex.Message}");
        }
    }

    private static async Task WriteResultAsync(UpdatePlan plan, bool succeeded, bool rolledBack, string message)
    {
        if (string.IsNullOrWhiteSpace(plan.ResultPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.ResultPath)!);

            var result = new UpdateResult
            {
                Succeeded = succeeded,
                RolledBack = rolledBack,
                Identity = plan.TargetIdentity,
                Message = message,
                CompletedUtc = DateTimeOffset.UtcNow,
            };

            await File.WriteAllTextAsync(
                plan.ResultPath,
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not write the result file: {ex.Message}");
        }
    }
}

/// <summary>Appends to the helper's own log file, and to stdout when run by hand.</summary>
/// <remarks>
/// The helper runs while the service is down, so it cannot use the wrapper's logging.
/// If an update goes wrong this file is the only account of what happened.
/// </remarks>
internal sealed class UpdateLog : IDisposable
{
    private readonly StreamWriter? _writer;

    public UpdateLog(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open the update log '{path}': {ex.Message}");
        }
    }

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.UtcNow:u}  {message}";

        Console.WriteLine(line);
        _writer?.WriteLine(line);
    }

    public void Dispose() => _writer?.Dispose();
}
