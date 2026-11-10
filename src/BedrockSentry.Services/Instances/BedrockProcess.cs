using System.Diagnostics;
using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// A launched <c>bedrock_server.exe</c> with its pipes redirected.
///
/// Asynchronous reads are safe here because the milestone 4 spike proved it:
/// BDS under <c>NT AUTHORITY\SYSTEM</c> in session 0 answers a command in tens
/// of milliseconds with no console attached, so there is no output buffering to
/// work around (<c>docs/design.md</c> § 5.5). That was measured as LocalSystem;
/// at low integrity (§ 17.6, <c>D126</c>) it is <c>T385</c>'s to measure again. Reading both pipes asynchronously is also
/// not optional — draining one synchronously while the other fills deadlocks
/// once the full pipe blocks the server's own write.
/// </summary>
public sealed class BedrockProcess : IBedrockProcess
{
    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly IServerProcessStarter _starter;
    private readonly string _instanceId;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>How long an exit waits for the console's last lines.</summary>
    public static readonly TimeSpan ReaderGrace = TimeSpan.FromSeconds(5);

    private StartedServer? _started;
    private Process? _process;
    private Task _reading = Task.CompletedTask;
    private bool _disposed;

    /// <param name="starter">How the process is started (<see cref="ServerStarters.ForThisMachine"/>).</param>
    public BedrockProcess(
        string instanceId, string executablePath, string workingDirectory, ILogger logger, IServerProcessStarter starter)
    {
        _instanceId = instanceId;
        _log = logger;
        _executablePath = executablePath;
        _workingDirectory = workingDirectory;
        _starter = starter;
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

        var started = _starter.Start(_executablePath, _workingDirectory);

        _started = started;
        _process = started.Process;
        _reading = Task.WhenAll(
            Task.Run(() => ReadAsync(started.Output, ConsoleOrigin.Stdout)),
            Task.Run(() => ReadAsync(started.Error, ConsoleOrigin.Stderr)));

        return Task.CompletedTask;
    }

    /// <summary>Hands every line of one pipe to <see cref="OutputReceived"/> until it closes.</summary>
    private async Task ReadAsync(StreamReader reader, ConsoleOrigin origin)
    {
        try
        {
            // Null marks the stream closing, not a blank line, which arrives as "".
            // BDS writes real empty lines (after a `list` header, inside warning
            // blocks) that belong in the transcript.
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                Emit(origin, line);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
            // Disposed while reading, or the pipe broke with the process.
        }
    }

    /// <summary>
    /// Writes one command to the server's stdin.
    /// </summary>
    /// <remarks>
    /// <para><strong>A throw from here means the command was NOT sent</strong>, and
    /// that is a guarantee callers rely on rather than a description of what
    /// happens to be true. <c>BackupOrchestrator</c> sends <c>save hold</c> outside
    /// the <c>try</c> whose <c>finally</c> resumes, so a throw that had left a hold
    /// in force would be a live server with saves paused and nothing on its way to
    /// resume them.</para>
    ///
    /// <para>Three arms make it hold, and all three are before any write: the token
    /// is observed at the gate, an exited process throws, and the command is handed
    /// to the pipe in a single flush. <c>A_send_that_throws_has_not_reached_the_server</c>
    /// pins it against the server's own state, which is what a comment cannot do —
    /// and <c>D87</c> is the open question of whether the orchestrator should depend
    /// on this at all.</para>
    /// </remarks>
    public async Task SendAsync(string command, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var input = _started?.Input
            ?? throw new InvalidOperationException("The instance process has not been started.");
        var process = _process!;

        // Serialised because two callers writing at once can interleave inside a
        // single line, and a half-command is a command BDS will still try to run.
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("The instance process has exited.");
            }

            await input.WriteLineAsync(command.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
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

        // The readers are waited for as well, so the last lines before an exit are
        // not lost — which is exactly the output an administrator wants after a crash.
        // For a while only: a child another part of this service started at the same
        // moment can inherit the console's write ends and hold them open until it
        // exits, and an exit is not left unreported waiting on that.
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAny(_reading, Task.Delay(ReaderGrace, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
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
    /// Checks the executable before launching, and refuses rather than failing
    /// when it cannot be used (<c>T250</c>).
    /// </summary>
    /// <remarks>
    /// <para><c>File.Exists</c> answers "false" both for a file that is not there
    /// and for one that cannot be examined — it swallows the access error.
    /// Reporting that as "not found" sends an administrator to look for a missing
    /// file when the file is sitting right where they put it, and the actual
    /// problem is that the service's account cannot read the folder.
    /// The two need opposite fixes, so they get different sentences.</para>
    ///
    /// <para><strong>Opening the file is the only check that can tell them
    /// apart</strong>, which is <c>T246</c>:
    /// <c>InstanceLayout.PresenceOf</c> reads the parent directory's index, and a
    /// Deny ACE on one file does not change that index — so it reports a denied
    /// executable <see cref="ServerPresenceState.Installed"/>, correctly, and cannot
    /// report it any other way. Measured on a denied file: <c>File.Exists</c> is
    /// true, <c>GetAttributes</c> succeeds, <c>FileInfo.Length</c> succeeds, and only
    /// the open throws. So the launch guard's answer and this one differ by design,
    /// and this is the later and the narrower of the two.</para>
    ///
    /// <para><strong>Every answer here is a refusal, not a failure.</strong> Each
    /// describes the installation rather than this attempt, so no relaunch can
    /// change any of them — which is why they leave by
    /// <see cref="ServerNotLaunchableException"/> and not as the ordinary exception
    /// types a launch failure throws. A locked file is deliberately not among them:
    /// an <see cref="IOException"/> from the open passes through, because a lock is
    /// a moment and retrying it is the right thing.</para>
    ///
    /// <para>The sentences come from <see cref="MissingServerText.RefusalReason"/>,
    /// which the launch guard one branch earlier already uses, and what is peculiar
    /// to this check travels as the state's <see cref="ServerPresence.Reason"/>
    /// rather than as a sentence of its own. A second sentence per state is how two
    /// surfaces describing one missing server come to disagree, and only one of the
    /// two would ever be the one an administrator reads.</para>
    /// </remarks>
    private void EnsureExecutableIsUsable()
    {
        var directory = _workingDirectory;
        var path = _executablePath;
        var name = Path.GetFileName(path);

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
                // No Reason: Windows says "Access to the path is denied", which is
                // what the sentence for this state already says at greater length.
                throw Refuse(new ServerPresence(ServerPresenceState.Unreadable), ex);
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
        // The same mapping InstanceLayout.PresenceOf uses, rather than a filter of
        // this file's own: a working directory that is gone and one that cannot be
        // listed are different states with different repairs, and deciding that
        // twice is how the two readers come to disagree (`T252`).
        catch (Exception ex) when (ServerPresence.From(ex) is { } failure)
        {
            throw Refuse(failure, ex);
        }

        // The file count is the fastest way to tell "wrong folder" from "the
        // empty folder BedrockSentry just created for you and nobody filled in",
        // and it travels as the state's Reason rather than as a sentence of its
        // own — a second sentence about one state is how the two surfaces that
        // describe a missing server come to disagree.
        var contents = siblingCount == 0
            ? "that directory is empty"
            : $"that directory holds {siblingCount} file(s)"
              + (anyExecutable ? string.Empty : " and no .exe at all");

        throw Refuse(new ServerPresence(ServerPresenceState.Missing, contents));

        ServerNotLaunchableException Refuse(ServerPresence presence, Exception? cause = null) =>
            new(MissingServerText.RefusalReason(directory, name, presence), presence, cause);
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

        // Closing the pipes ends the readers. Closing a pipe a reader is still
        // blocked on can itself block until the read returns, so while one is, the
        // close is left to finish on its own rather than holding up the caller.
        if (_started is { } started)
        {
            if (_reading.IsCompleted)
            {
                started.Dispose();
            }
            else
            {
                _ = Task.Run(started.Dispose);
            }
        }

        _started = null;
        _process = null;

        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Creates a <see cref="BedrockProcess"/> per start attempt, resolving where the
/// instance actually lives.
/// </summary>
public sealed class BedrockProcessFactory(ILoggerFactory loggerFactory, IServerProcessStarter starter) : IBedrockProcessFactory
{
    public IBedrockProcess Create(string instanceId, InstanceDefinition definition, string workingDirectory)
    {
        var executable = InstanceLayout.ExecutablePath(workingDirectory, definition);
        return new BedrockProcess(
            instanceId, executable, workingDirectory, loggerFactory.CreateLogger<BedrockProcess>(), starter);
    }

    /// <summary>The real answer: what the disk says about the file this factory would launch.</summary>
    public ServerPresence PresenceOf(InstanceDefinition definition, string workingDirectory) =>
        InstanceLayout.PresenceOf(workingDirectory, definition);
}
