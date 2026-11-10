using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>What a script did.</summary>
/// <param name="Succeeded">Exit code zero and nothing on standard error.</param>
/// <param name="Error">
/// Standard error, exactly as PowerShell wrote it. Keep using this for the log,
/// where the position block is worth having; use <see cref="Reason"/> for
/// anything an operator reads.
/// </param>
public sealed record ScriptResult(bool Succeeded, string Output, string Error)
{
    public static ScriptResult Failed(string error) => new(false, string.Empty, error);

    /// <summary>What went wrong, in the words a person needs and no others.</summary>
    /// <remarks>See <see cref="PowerShellError.Summarize"/> — this is `T127`.</remarks>
    public string Reason => PowerShellError.Summarize(Error);
}

/// <summary>
/// Turning PowerShell's error rendering into a sentence.
/// </summary>
/// <remarks>
/// <para>
/// An ErrorRecord on standard error is not one line, it is a small report: the
/// message, then <c>At line:N char:M</c>, then the source that failed echoed
/// back with <c>+</c> in front of it and squiggles underneath, then
/// <c>CategoryInfo</c> and <c>FullyQualifiedErrorId</c>. Handed to an operator
/// whole — which is what every caller here used to do — a VSS failure arrives as
/// a screen of script source with "Initialization failure" somewhere inside it
/// (`T127`). The Readiness panel exists to predict that failure; the message it
/// produces when it happens anyway should lead with the reason.
/// </para>
/// <para>
/// The filter is structural rather than a list of known messages: every line of
/// the position block and its trailer begins with <c>+</c>, <c>~</c>, or
/// <c>At line:</c>, and none of those can begin a message PowerShell wrote
/// itself. So this drops scaffolding, never content — and if a rendering ever
/// appears that leaves nothing behind, the original text is returned rather than
/// an empty string, since a confusing message beats no message.
/// </para>
/// </remarks>
public static class PowerShellError
{
    public static string Summarize(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return string.Empty;
        }

        var kept = stderr
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => !IsScaffolding(line.TrimStart()))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (kept.Count == 0)
        {
            return stderr.Trim();
        }

        // One line is the classic rendering: "<Cmdlet> : <message>".
        if (kept.Count == 1)
        {
            return StripCmdletPrefix(kept[0]);
        }

        // Several lines mean PowerShell could not name a cmdlet to blame, so it
        // printed the *whole failing script block* as the source and appended the
        // message to its last line. That is `T127`: none of the script's own lines
        // start with the scaffolding markers, and the head before " : " has spaces
        // in it, so nothing was dropped and nothing was stripped — the operator got
        // the entire creation script on one line, ending in the two words that
        // actually mattered.
        //
        // The message is the tail of the last line, and everything above it is the
        // source. Read from the end deliberately: a script block can contain " : "
        // of its own, and the marker that matters is the last one.
        //
        // The message can itself run to several lines (Get-AppxPackage's "Access is
        // denied." comes twice, `T399`), so the marker is looked for upward from the
        // last line, and the lines below it are the rest of the message.
        for (var line = kept.Count - 1; line >= 0; line--)
        {
            var marker = kept[line].LastIndexOf(" : ", StringComparison.Ordinal);

            if (marker >= 0 && marker + 3 < kept[line].Length)
            {
                var message = new List<string> { kept[line][(marker + 3)..].Trim() };

                foreach (var rest in kept.Skip(line + 1))
                {
                    if (!rest.Equals(message[^1], StringComparison.Ordinal))
                    {
                        message.Add(rest);
                    }
                }

                return string.Join(" ", message);
            }
        }

        // No marker to cut on. Better an ugly line than no reason at all, which is
        // the rule the fallback below follows too.
        return string.Join(" ", kept);
    }

    private static bool IsScaffolding(string line) =>
        line.StartsWith('+')
        || line.StartsWith('~')
        || line.StartsWith("At line:", StringComparison.Ordinal)
        || line.StartsWith("At char:", StringComparison.Ordinal);

    /// <summary>
    /// Drops the <c>Invoke-CimMethod : </c> that PowerShell puts in front of the
    /// message, which names the cmdlet the wrapper chose rather than anything the
    /// person reading can act on.
    /// </summary>
    /// <remarks>
    /// Only for a leading token that actually looks like a cmdlet — a
    /// <c>Verb-Noun</c> with no spaces in it — so a message that merely contains
    /// a colon keeps all of itself.
    /// </remarks>
    private static string StripCmdletPrefix(string line)
    {
        var trimmed = line.Trim();
        var marker = trimmed.IndexOf(" : ", StringComparison.Ordinal);

        if (marker <= 0)
        {
            return trimmed;
        }

        var head = trimmed[..marker];

        return head.Contains('-') && !head.Contains(' ')
            ? trimmed[(marker + 3)..].Trim()
            : trimmed;
    }
}

/// <summary>
/// Runs a PowerShell script and returns what it printed.
/// </summary>
/// <remarks>
/// An interface so the things built on it — the firewall and Defender checks —
/// can be tested against captured output. Their bugs live in reading the answer
/// and deciding what it means, not in starting a process, and on a Linux build
/// machine starting the process is the one part that cannot run at all.
/// </remarks>
public interface IPowerShellRunner
{
    Task<ScriptResult> RunAsync(string script, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs scripts through <c>powershell.exe</c>.
/// </summary>
/// <remarks>
/// <para>
/// Windows PowerShell rather than a .NET API, because the firewall and Defender
/// configuration have no supported managed surface — the alternatives are COM
/// interop against <c>HNetCfg.FwPolicy2</c> and WMI, both of which are more code
/// and more brittle than asking the cmdlets Microsoft actually documents.
/// </para>
/// <para>
/// Every caller here asks for JSON back rather than parsing display output.
/// The text the cmdlets print is localised and reformatted between versions;
/// the JSON is neither.
/// </para>
/// </remarks>
public sealed class PowerShellRunner : IPowerShellRunner
{
    /// <summary>
    /// How long any one script gets before it is killed.
    /// </summary>
    /// <remarks>
    /// Every caller of this runs while somebody is waiting on a page, and a
    /// cmdlet with no deadline is indistinguishable from a hung panel. Generous
    /// rather than tight: the answer these produce is worth a few seconds, and
    /// a check that runs out of time reports itself as unknown, which costs an
    /// administrator a recheck rather than handing them a wrong answer.
    /// </remarks>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);

    private readonly ILogger<PowerShellRunner> _log;
    private readonly TimeSpan _deadline;

    /// <param name="deadline">
    /// <c>D22</c>. Overridable so the timeout can be proved in milliseconds
    /// rather than in half a minute, which is the difference between a test that
    /// runs on every commit and one nobody keeps. Deliberately a constructor
    /// argument and not a configuration setting: the only correct value is "long
    /// enough", and one set too short would quietly turn working checks into
    /// unknown ones — which reads as the checks being broken rather than as the
    /// number being wrong.
    /// </param>
    public PowerShellRunner(ILogger<PowerShellRunner> log, TimeSpan? deadline = null)
    {
        _log = log;
        _deadline = deadline ?? DefaultDeadline;
    }

    public async Task<ScriptResult> RunAsync(
        string script, CancellationToken cancellationToken = default)
    {
        // The only place that knows about the platform. Guarding inside each
        // check instead would put the same branch in every one of them and make
        // the interesting half — reading the answer and deciding what it means —
        // impossible to exercise anywhere but Windows.
        if (!OperatingSystem.IsWindows())
        {
            return ScriptResult.Failed("These checks only run on Windows.");
        }

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            // NoProfile because a profile that prints a banner would end up in
            // the JSON; NonInteractive so a cmdlet that wants confirmation fails
            // rather than hanging a page render forever.
            ArgumentList =
            {
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy", "Bypass",
                "-Command", script,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Linked rather than replacing the caller's token, so navigating away
        // still abandons the work immediately instead of waiting out the
        // deadline for an answer nobody is going to read.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadline);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("powershell.exe did not start.");

            try
            {
                // Both streams read concurrently with the wait. Reading one to
                // the end first deadlocks as soon as the other fills its pipe
                // buffer, which for a firewall rule dump is not a hypothetical
                // size.
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var error = process.StandardError.ReadToEndAsync(deadline.Token);

                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

                var stdout = await output.ConfigureAwait(false);
                var stderr = await error.ConfigureAwait(false);

                return new ScriptResult(process.ExitCode == 0 && stderr.Trim().Length == 0, stdout, stderr);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The deadline, not the caller. Kill the whole tree: the cmdlets
                // here start their own children, and leaving those running would
                // turn every timed-out render into another orphaned powershell.exe
                // holding a WMI query open.
                Kill(process);

                _log.LogWarning(
                    "A PowerShell check did not finish within {Seconds:0}s and was stopped",
                    _deadline.TotalSeconds);

                return ScriptResult.Failed(
                    $"The check did not finish within {_deadline.TotalSeconds:0} seconds.");
            }
            catch (OperationCanceledException)
            {
                // The caller gave up. Same cleanup, no complaint.
                Kill(process);
                throw;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not run a PowerShell check");
            return ScriptResult.Failed(ex.Message);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is SystemException or AggregateException)
        {
            // It exited between the check and the kill, or a child outlived the
            // attempt. Either way there is nothing left worth reporting.
        }
    }
}
