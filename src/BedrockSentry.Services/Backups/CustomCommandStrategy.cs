using System.Diagnostics;
using System.Text;
using BedrockSentry.Core.Backups;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// Runs a user-provided command once the snapshot is ready
/// (<c>docs/design.md</c> § 8.2).
/// </summary>
/// <remarks>
/// The command runs through the platform shell so pipelines and script files
/// both work, and gets its context as environment variables rather than
/// arguments — paths with spaces survive that without quoting rules to get
/// wrong. The manifest is written to a file first and its path handed over,
/// because a script copying live world files has the same truncation problem
/// every other strategy has: the files on disk may be longer than the snapshot
/// while saves are held, and only the manifest lengths are real
/// (<c>docs/design.md</c> § 5.5).
///
/// The timeout is a constructor argument for the <c>D22</c> reason, and it is
/// not optional in spirit: saves are held while this runs, so a script that
/// hangs is holding a live server's writes with it.
/// </remarks>
public sealed class CustomCommandStrategy : IBackupStrategy
{
    private readonly CustomCommandPolicy _policy;
    private readonly ILogger<CustomCommandStrategy> _log;
    private readonly TimeSpan _timeout;

    public CustomCommandStrategy(
        CustomCommandPolicy policy, ILogger<CustomCommandStrategy> log, TimeSpan? timeout = null)
    {
        _policy = policy;
        _log = log;
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
    }

    public string Name => "custom-command";

    public bool IsEnabled(BackupSettings settings) => settings.CustomCommand.IsSet;

    /// <inheritdoc />
    /// <remarks>
    /// The install-wide gate (<c>D45</c>). Deliberately not folded into
    /// <see cref="IsEnabled"/>: a strategy dropped there is dropped silently, and
    /// if this were the only one the run would report "No backup strategy is
    /// enabled for this instance" over a ticked box — a sentence the operator
    /// would rightly read as the wrapper being broken.
    /// </remarks>
    public BlockedStrategy? Blocked(BackupSettings settings) => _policy.Blocks(Name);

    public async Task<BackupStrategyResult> RunAsync(
        BackupContext context, CancellationToken cancellationToken)
    {
        // Above everything — above reading the command, above writing the manifest,
        // above Process.Start. Redundant while the orchestrator is the only caller,
        // because it drops a blocked strategy before it gets here, and worth having
        // anyway: "the gate covers any caller" is a claim, and this is the line that
        // makes it true for one that builds a BackupContext itself.
        if (Blocked(context.Settings) is { } block)
        {
            _log.LogWarning(
                "The custom backup command for {InstanceId} was refused: {Why}",
                context.InstanceId, block.Detail);

            return new BackupStrategyResult(Name, false, block.Detail, null);
        }

        var command = ReadCommand(context);
        if (command is null)
        {
            return new BackupStrategyResult(Name, false, "No command is configured.", null);
        }

        // Kept beside the artifacts rather than in temp, and left behind on
        // purpose: it documents what the snapshot contained, which is exactly
        // the question someone restoring from a custom backup is asking.
        var manifestFile = Path.Combine(
            context.ArtifactDirectory,
            $"manifest-{context.StartedUtc:yyyyMMdd-HHmmss}.txt");

        await File.WriteAllLinesAsync(
            manifestFile,
            context.Manifest.Select(e => $"{e.RelativePath}:{e.Length}"),
            cancellationToken).ConfigureAwait(false);

        using var process = new Process();
        process.StartInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"{command}\"")
            : new ProcessStartInfo("/bin/sh", ["-c", command]);

        process.StartInfo.WorkingDirectory = context.WorkingDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        var environment = process.StartInfo.Environment;
        environment["BEDROCKSENTRY_INSTANCE_ID"] = context.InstanceId;
        environment["BEDROCKSENTRY_INSTANCE_NAME"] = context.InstanceName;
        environment["BEDROCKSENTRY_WORKING_DIR"] = context.WorkingDirectory;
        // The world where it LIVES, not where this run happens to be reading it
        // from (`D66`). A custom command is somebody's own script against their own
        // server; handing it a shadow-copy device path it did not ask for would
        // break every script that writes, and the ones that only read are better
        // served by the path they can reproduce by hand.
        environment["BEDROCKSENTRY_WORLDS_DIR"] = context.WorldsDirectory;
        environment["BEDROCKSENTRY_ARTIFACT_DIR"] = context.ArtifactDirectory;
        environment["BEDROCKSENTRY_MANIFEST_FILE"] = manifestFile;
        environment["BEDROCKSENTRY_REASON"] = context.Reason.ToString();
        environment["BEDROCKSENTRY_NOTE"] = context.Note ?? "";

        // Bounded, newest kept: a chatty script must not grow the history file
        // without limit, and the end of the output is where errors live.
        var output = new ConcurrentTail(maxChars: 4000);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.Add(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { output.Add(e.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Saves are held while this runs. A hung script does not get to
            // hold a live server's writes with it — kill it, resume happens
            // upstream either way.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }

            var why = cancellationToken.IsCancellationRequested
                ? "The backup was cancelled."
                : $"The command was still running after {_timeout.TotalMinutes:0} minutes and was stopped.";

            _log.LogWarning("Custom backup command for {InstanceId} was killed: {Why}", context.InstanceId, why);
            return new BackupStrategyResult(Name, false, $"{why} Output: {output}", null);
        }

        var succeeded = process.ExitCode == 0;
        if (!succeeded)
        {
            _log.LogWarning(
                "Custom backup command for {InstanceId} exited with {ExitCode}",
                context.InstanceId, process.ExitCode);
        }

        return new BackupStrategyResult(
            Name,
            succeeded,
            succeeded ? Trimmed(output.ToString()) : $"Exit code {process.ExitCode}. Output: {output}",
            manifestFile);
    }

    private static string? ReadCommand(BackupContext context)
    {
        var command = context.Settings.CustomCommand.Command;
        return string.IsNullOrWhiteSpace(command) ? null : command.Trim();
    }

    private static string? Trimmed(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>Last N characters of whatever arrived, thread-safe.</summary>
    private sealed class ConcurrentTail
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        private readonly int _maxChars;

        public ConcurrentTail(int maxChars) => _maxChars = maxChars;

        public void Add(string line)
        {
            lock (_gate)
            {
                if (_text.Length > 0)
                {
                    _text.AppendLine();
                }

                _text.Append(line);

                if (_text.Length > _maxChars)
                {
                    _text.Remove(0, _text.Length - _maxChars);
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }
}
