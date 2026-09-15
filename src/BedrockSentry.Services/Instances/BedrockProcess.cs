using System.Diagnostics;
using System.Text;
using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// A launched <c>bedrock_server.exe</c> with its pipes redirected.
///
/// Asynchronous reads are safe here because the milestone 4 spike proved it:
/// BDS under <c>NT AUTHORITY\SYSTEM</c> in session 0 answers a command in tens
/// of milliseconds with no console attached, so there is no output buffering to
/// work around (<c>docs/design.md</c> § 5.5). Reading both pipes asynchronously is also
/// not optional — draining one synchronously while the other fills deadlocks
/// once the full pipe blocks the server's own write.
/// </summary>
public sealed class BedrockProcess : IBedrockProcess
{
    private readonly ProcessStartInfo _startInfo;
    private readonly string _instanceId;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private Process? _process;
    private bool _disposed;

    public BedrockProcess(string instanceId, string executablePath, string workingDirectory, ILogger logger)
    {
        _instanceId = instanceId;
        _log = logger;

        _startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // BDS emits UTF-8. Left to default, .NET decodes using the console's
            // code page — often 437 or 1252 — which silently mangles every
            // non-ASCII player name, a bug that only appears once a real player
            // joins. The input side matters for the same reason: `say` and `kick`
            // carry player-authored text back the other way.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
    }

    public int? ProcessId
    {
        get
        {
            try
            {
                return _process?.Id;
            }
            catch (InvalidOperationException)
            {
                // The process object outlives the process; asking a disposed or
                // never-started one for an id throws rather than returning null.
                return null;
            }
        }
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process?.HasExited ?? true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public event Action<ConsoleOrigin, string>? OutputReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_process is not null)
        {
            throw new InvalidOperationException(
                "A BedrockProcess is single-use; create a new one for each start attempt.");
        }

        EnsureExecutableIsUsable();

        var process = new Process { StartInfo = _startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            // A null Data marks the stream closing, not a blank line — a blank
            // line arrives as "". The distinction matters: BDS emits real empty
            // lines (after a `list` header, inside warning blocks) that belong in
            // the transcript.
            if (e.Data is not null)
            {
                Emit(ConsoleOrigin.Stdout, e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Emit(ConsoleOrigin.Stderr, e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _process = process;
        return Task.CompletedTask;
    }

    public async Task SendAsync(string command, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var process = _process
            ?? throw new InvalidOperationException("The instance process has not been started.");

        // Serialised because two callers writing at once can interleave inside a
        // single line, and a half-command is a command BDS will still try to run.
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("The instance process has exited.");
            }

            await process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        var process = _process
            ?? throw new InvalidOperationException("The instance process has not been started.");

        // The parameterless overload also waits for the async readers to drain,
        // so the last lines before an exit are not lost — which is exactly the
        // output an administrator wants after a crash.
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    public void Kill()
    {
        try
        {
            // BDS is a single process today, but killing the tree costs nothing
            // and covers a wrapper script standing in front of it.
            _process?.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone between the check and the call.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to kill instance {InstanceId}", _instanceId);
        }
    }

    /// <summary>
    /// Checks the executable before launching, and says something specific when
    /// it cannot be used.
    /// </summary>
    /// <remarks>
    /// <c>File.Exists</c> answers "false" both for a file that is not there and
    /// for one that cannot be examined — it swallows the access error. Reporting
    /// that as "not found" sends an administrator to look for a missing file
    /// when the file is sitting right where they put it, and the actual problem
    /// is that the service runs as LocalSystem and cannot read the folder. The
    /// two need opposite fixes, so they get different messages.
    /// </remarks>
    private void EnsureExecutableIsUsable()
    {
        var directory = _startInfo.WorkingDirectory;
        var path = _startInfo.FileName;
        var name = Path.GetFileName(path);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"The working directory for instance '{_instanceId}' does not exist: {directory}");
        }

        if (File.Exists(path))
        {
            // Existing is not the same as readable. Probe now, so the failure is
            // reported here rather than as an opaque Win32 error from Start().
            try
            {
                using var probe = File.OpenRead(path);
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException(
                    $"'{name}' exists at {path} but the BedrockSentry service cannot read it. "
                    + "The service runs as LocalSystem, which may not have access to a location "
                    + "under a user profile. Move the server somewhere the service can read, "
                    + "or grant SYSTEM read and execute on that folder.", ex);
            }
        }

        // Not found. Confirm that is really what it is, rather than a directory
        // that cannot be listed — which File.Exists reports identically.
        int siblingCount;
        bool anyExecutable;
        try
        {
            var siblings = Directory.GetFiles(directory);
            siblingCount = siblings.Length;
            anyExecutable = siblings.Any(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"The BedrockSentry service cannot read the working directory for instance "
                + $"'{_instanceId}' at {directory}. The service runs as LocalSystem, which may not "
                + "have access to a location under a user profile. Move the server somewhere the "
                + "service can read, or grant SYSTEM read and execute on that folder.", ex);
        }

        // The file count is the fastest way to tell "wrong folder" from "the
        // empty folder BedrockSentry just created for you and nobody filled in".
        var contents = siblingCount == 0
            ? "that directory is empty"
            : $"that directory holds {siblingCount} file(s)"
              + (anyExecutable ? string.Empty : " and no .exe at all");

        throw new FileNotFoundException(
            $"'{name}' was not found for instance '{_instanceId}': {contents}. "
            + $"Unpack the Bedrock server into {directory} so that {name} sits directly inside it.",
            path);
    }

    private void Emit(ConsoleOrigin origin, string line)
    {
        var handlers = OutputReceived;
        if (handlers is null)
        {
            return;
        }

        // Invoked one at a time rather than through the multicast delegate. A
        // plain Invoke stops at the first handler that throws and never reaches
        // the rest — so a misbehaving log writer would permanently blind the
        // supervisor to "Server started." on every line, not just its own. This
        // pipe has several independent subscribers by design; isolating them is
        // the point.
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<ConsoleOrigin, string>)handler)(origin, line);
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex, "A console output handler threw for instance {InstanceId}", _instanceId);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        if (_process is { } process)
        {
            try
            {
                process.CancelOutputRead();
                process.CancelErrorRead();
            }
            catch (InvalidOperationException)
            {
                // Reads were never begun, or the process never started.
            }

            process.Dispose();
            _process = null;
        }

        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Creates a <see cref="BedrockProcess"/> per start attempt, resolving where the
/// instance actually lives.
/// </summary>
public sealed class BedrockProcessFactory(ILoggerFactory loggerFactory) : IBedrockProcessFactory
{
    public IBedrockProcess Create(string instanceId, InstanceDefinition definition, string workingDirectory)
    {
        var executable = Path.Combine(workingDirectory, definition.Executable);
        return new BedrockProcess(
            instanceId, executable, workingDirectory, loggerFactory.CreateLogger<BedrockProcess>());
    }
}
