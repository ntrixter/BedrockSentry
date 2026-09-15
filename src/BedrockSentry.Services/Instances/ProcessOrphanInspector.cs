using System.ComponentModel;
using System.Diagnostics;
using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Logging;

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
            // Re-checked here, against this process, now — see the interface. A
            // recycled id is the whole reason, and it is the difference between
            // ending an orphan and ending whatever inherited its number.
            if (ExecutablePathOf(process) is not { } path)
            {
                return new OrphanTermination(
                    false,
                    $"PID {processId} could not be identified, so it was left alone. Only a "
                    + "bedrock_server running from this server's own folder is ever ended.");
            }

            if (!string.Equals(process.ProcessName, BedrockProcessName, StringComparison.OrdinalIgnoreCase)
                || !IsUnder(path, workingDirectory))
            {
                return new OrphanTermination(
                    false,
                    $"PID {processId} is {process.ProcessName} running from {path}, which is not "
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
