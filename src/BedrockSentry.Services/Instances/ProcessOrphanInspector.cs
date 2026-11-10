using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Finds and ends orphaned Bedrock servers by walking the process table
/// (<c>D56</c>, <c>T149</c>).
/// </summary>
/// <remarks>
/// The only place in the wrapper that ends a process it did not start, which is
/// why the guard is here rather than at any call site: both operations resolve
/// the process themselves and both apply the same two tests. Nothing above this
/// class can ask it to terminate an arbitrary id.
/// </remarks>
public sealed class ProcessOrphanInspector : IOrphanInspector
{
    // Windows reports process names without the extension.
    private const string BedrockProcessName = "bedrock_server";

    private readonly ILogger<ProcessOrphanInspector> _log;

    public ProcessOrphanInspector(ILogger<ProcessOrphanInspector> log) => _log = log;

    public Task<IReadOnlyList<OrphanedServer>> FindAsync(
        string instanceId,
        string workingDirectory,
        IReadOnlyCollection<int> ours,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Find(workingDirectory, ours));

    public Task<OrphanTermination> TerminateAsync(
        string instanceId,
        int processId,
        string workingDirectory,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Terminate(processId, workingDirectory));

    /// <summary>The process-table walk behind <see cref="FindAsync"/>.</summary>
    public IReadOnlyList<OrphanedServer> Find(string workingDirectory, IReadOnlyCollection<int> ours)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return [];
        }

        Process[] candidates;

        try
        {
            candidates = Process.GetProcessesByName(BedrockProcessName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or PlatformNotSupportedException)
        {
            // Best effort on the way in. Not being able to enumerate is not a
            // reason to refuse a start that would have worked — the port
            // pre-flight is still in front of the launch either way.
            _log.LogWarning(ex, "Could not enumerate Bedrock processes while looking for orphans");
            return [];
        }

        var found = new List<OrphanedServer>();

        foreach (var process in candidates)
        {
            using (process)
            {
                if (ours.Contains(process.Id))
                {
                    continue;
                }

                if (ExecutablePathOf(process) is not { } path || !IsUnder(path, workingDirectory))
                {
                    continue;
                }

                found.Add(new OrphanedServer(process.Id, path, StartTimeOf(process)));
            }
        }

        return found;
    }

    /// <summary>The guarded kill behind <see cref="TerminateAsync"/>.</summary>
    public OrphanTermination Terminate(int processId, string workingDirectory)
    {
        Process process;

        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // Gone between the page rendering and the button. Nothing to do, and
            // saying so plainly beats an error about a process that is already
            // in the state the operator wanted.
            return new OrphanTermination(
                true, $"PID {processId} was already gone. Nothing needed ending.");
        }

        using (process)
        {
            // Pinned before anything is read. Process opens a fresh handle by id for
            // each call until its SafeHandle is taken, so without this the check
            // and the kill could reach two different processes that held the id in
            // turn. Taken, the check reads and Kill ends the process this handle names.
            try
            {
                _ = process.SafeHandle;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                return new OrphanTermination(
                    false, $"PID {processId} could not be opened, so it was left alone: {ex.Message}");
            }

            // Re-checked here, against this process, now — see the interface. A
            // recycled id is the whole reason, and it is the difference between
            // ending an orphan and ending whatever inherited its number.
            if (PinnedExecutablePathOf(process) is not { } path)
            {
                return new OrphanTermination(
                    false,
                    $"PID {processId} could not be identified, so it was left alone. Only a "
                    + "bedrock_server running from this server's own folder is ever ended.");
            }

            // The name from the path the handle answered with, not ProcessName,
            // which is read by id from the process table.
            var name = Path.GetFileNameWithoutExtension(path);

            if (!string.Equals(name, BedrockProcessName, StringComparison.OrdinalIgnoreCase)
                || !IsUnder(path, workingDirectory))
            {
                return new OrphanTermination(
                    false,
                    $"PID {processId} is {name} running from {path}, which is not "
                    + "this server's own folder. It was left alone.");
            }

            try
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                _log.LogError(ex, "Could not end orphaned Bedrock server {ProcessId}", processId);

                return new OrphanTermination(
                    false, $"PID {processId} could not be ended: {ex.Message}");
            }

            _log.LogWarning(
                "Ended orphaned Bedrock server {ProcessId} running from {Path}", processId, path);

            return new OrphanTermination(
                true, $"Ended bedrock_server (PID {processId}), which was running from {path}.");
        }
    }

    /// <summary>
    /// The executable behind a process, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Null is a refusal, not an inconvenience: every caller here treats "cannot
    /// identify" as "not a match", so a process the wrapper cannot examine is
    /// never listed as an orphan and never terminated.
    /// </remarks>
    private static string? ExecutablePathOf(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The executable behind a process whose handle is pinned, read through that
    /// handle on Windows; null when it cannot be read.
    /// </summary>
    private static string? PinnedExecutablePathOf(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ExecutablePathOf(process);
        }

        var buffer = new char[32_768];
        var length = buffer.Length;

        return QueryFullProcessImageName(process.SafeHandle, 0, buffer, ref length)
            ? new string(buffer, 0, length)
            : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process, int flags, [Out] char[] buffer, ref int size);

    private static DateTimeOffset? StartTimeOf(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> sits inside <paramref name="directory"/>.
    /// </summary>
    /// <remarks>
    /// Compared as full paths with a trailing separator on the directory, so
    /// <c>C:\instances\survival-old</c> is not read as being inside
    /// <c>C:\instances\survival</c> — a prefix match without the separator would
    /// say it is, and this is the check that decides whether something gets
    /// killed.
    /// </remarks>
    internal static bool IsUnder(string path, string directory)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
                + Path.DirectorySeparatorChar;

            return Path.GetFullPath(path)
                .StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
