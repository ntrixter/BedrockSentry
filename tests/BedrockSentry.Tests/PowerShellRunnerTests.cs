using System.Diagnostics;
using BedrockSentry.Services.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// The runner's own behaviour — starting a real process and stopping it again.
/// </summary>
/// <remarks>
/// <para>
/// Everything built on the runner is tested against captured output through
/// <see cref="IPowerShellRunner"/>, because that is where the interesting bugs
/// live. These are the exception: a deadline that does not actually kill
/// anything cannot be proved by a fake, and the bug it exists to prevent — a
/// page waiting forever on a cmdlet — is made of a real process.
/// </para>
/// <para>
/// Windows only, and skipped rather than adapted elsewhere. The runner refuses
/// to start anything off Windows by design, so there is nothing on a Linux box
/// for these to be true or false about. CI builds on <c>windows-latest</c>, so
/// they run on every commit rather than only on somebody's desk.
/// </para>
/// </remarks>
public class PowerShellRunnerTests
{
    /// <summary>A fact that reports itself skipped rather than passed off Windows.</summary>
    /// <remarks>
    /// Guarding inside the body with an early return would leave these showing
    /// green on a Linux run without having asserted anything — a test that
    /// silently stops testing is worse than one that is visibly not running.
    /// </remarks>
    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "The runner only starts a process on Windows.";
            }
        }
    }

    /// Long enough that nothing finishes inside it by accident, short enough
    /// that a test suite is still worth running.
    private static readonly TimeSpan Impatient = TimeSpan.FromMilliseconds(300);

    private static PowerShellRunner Runner(TimeSpan? deadline = null) =>
        new(NullLogger<PowerShellRunner>.Instance, deadline);

    // The whole point of the deadline. Without it this returns after a minute;
    // the operator's status page was blocked on exactly this shape of wait.
    [WindowsFact]
    public async Task A_script_that_outruns_its_deadline_gives_up_rather_than_waiting()
    {
        var started = Stopwatch.StartNew();

        var result = await Runner(Impatient).RunAsync("Start-Sleep -Seconds 60");

        started.Stop();

        Assert.False(result.Succeeded);
        Assert.Contains("did not finish", result.Error);

        // Generous, because a cold powershell.exe start is most of this. The
        // claim is "it gave up", not "it gave up promptly".
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(30),
            $"gave up only after {started.Elapsed}, which is no deadline at all");
    }

    // A killed script that leaves its process running turns every timed-out
    // render into another powershell.exe holding a query open, which is a slower
    // and less obvious version of the bug the deadline was added to fix.
    [WindowsFact]
    public async Task A_script_stopped_by_its_deadline_leaves_nothing_running()
    {
        var before = Process.GetProcessesByName("powershell").Select(p => p.Id).ToHashSet();

        await Runner(Impatient).RunAsync("Start-Sleep -Seconds 60");

        // The kill is issued as the call returns; give the OS a moment to reap
        // before counting, or this measures scheduling rather than behaviour.
        await Task.Delay(TimeSpan.FromSeconds(2));

        var survivors = Process.GetProcessesByName("powershell")
            .Where(p => !before.Contains(p.Id))
            .Where(p =>
            {
                try
                {
                    return !p.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            })
            .ToList();

        Assert.Empty(survivors);
    }

    // The deadline is a backstop, not the schedule. A caller who has already
    // navigated away must not wait out the remainder of it — which is why the
    // token source is linked to theirs rather than replacing it.
    [WindowsFact]
    public async Task A_caller_that_gives_up_is_not_made_to_wait_for_the_deadline()
    {
        using var caller = new CancellationTokenSource();
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));

        var started = Stopwatch.StartNew();

        // A generous deadline, so anything but the caller's own cancellation
        // would keep this waiting far longer than the assertion allows.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Runner(TimeSpan.FromMinutes(5)).RunAsync("Start-Sleep -Seconds 60", caller.Token));

        started.Stop();

        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(30),
            $"waited {started.Elapsed} after the caller had given up");
    }

    // Nothing passes a deadline in production; the default is what actually runs.
    [Fact]
    public void The_default_deadline_is_the_one_nobody_had_to_choose() =>
        Assert.Equal(TimeSpan.FromSeconds(30), PowerShellRunner.DefaultDeadline);

    /// <summary>
    /// The container can still build it without being told a deadline.
    /// </summary>
    /// <remarks>
    /// The deadline is an optional constructor argument, and nothing registers a
    /// <see cref="TimeSpan"/>. If the container ever stopped honouring the
    /// default the failure would be a service that will not start — found on the
    /// operator's machine rather than here, and reported as the whole wrapper
    /// being broken rather than as one argument.
    /// </remarks>
    [Fact]
    public void The_container_can_build_the_runner_without_being_given_a_deadline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPowerShellRunner, PowerShellRunner>();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<PowerShellRunner>(provider.GetRequiredService<IPowerShellRunner>());
    }
}

/// <summary>
/// Reading an ErrorRecord back as a sentence (`T127`).
/// </summary>
/// <remarks>
/// Pure text, so these run everywhere — which matters, because the failure they
/// describe was found on a real box during a VSS backup and there is no way to
/// provoke it from a build agent. The inputs below are the shape
/// <c>powershell.exe</c> actually writes, kept verbatim rather than tidied.
/// </remarks>
public class PowerShellErrorTests
{
    // The shape this comes back in: the reason buried in a rendering of the script
    // that failed. The operator wants the first line and nothing else on it.
    [Fact]
    public void The_reason_survives_and_the_position_block_does_not()
    {
        const string stderr = """
        Invoke-CimMethod : Initialization failure
        At line:3 char:13
        +   $result = Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName ...
        +             ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
            + CategoryInfo          : NotSpecified: (:) [Invoke-CimMethod], CimException
            + FullyQualifiedErrorId : HRESULT 0x80042302,Microsoft.Management.Infrastructure.CimCmdlets.InvokeCimMethodCommand
        """;

        Assert.Equal("Initialization failure", PowerShellError.Summarize(stderr));
    }

    // The same shape the wrapper's own scripts produce, since they catch and
    // re-raise through Write-Error.
    [Fact]
    public void A_write_error_rendering_loses_its_cmdlet_prefix()
    {
        const string stderr = """
        Write-Error : Access is denied.
        At line:23 char:11
        + } catch { Write-Error $_.Exception.Message }
        +           ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
            + CategoryInfo          : NotSpecified: (:) [Write-Error], WriteErrorException
        """;

        Assert.Equal("Access is denied.", PowerShellError.Summarize(stderr));
    }

    // A message that merely contains a colon is not a cmdlet prefix, and losing
    // its left-hand side would change what it says.
    [Fact]
    public void A_colon_inside_a_message_is_left_alone()
    {
        Assert.Equal(
            "The volume C: could not be snapshotted.",
            PowerShellError.Summarize("The volume C: could not be snapshotted."));
    }

    // The runner's own failures never went through PowerShell at all — they must
    // come back unchanged rather than being parsed as if they had.
    [Fact]
    public void A_message_the_runner_wrote_itself_is_untouched()
    {
        Assert.Equal(
            "The check did not finish within 30 seconds.",
            PowerShellError.Summarize("The check did not finish within 30 seconds."));
    }

    // Nothing to say is said as nothing, so callers can keep testing for empty.
    [Fact]
    public void Silence_stays_silent()
    {
        Assert.Equal(string.Empty, PowerShellError.Summarize(string.Empty));
        Assert.Equal(string.Empty, PowerShellError.Summarize("   \n  "));
    }

    /// <summary>
    /// The shape the box actually hit: the whole failing script block standing
    /// where the cmdlet name goes.
    /// </summary>
    /// <remarks>
    /// PowerShell renders <c>InvocationInfo.MyCommand.Name : message</c>, and for
    /// a script block that name is the block's entire source — so the message is
    /// appended to the last of several lines, none of which are scaffolding. The
    /// first fix handled the two shapes above and turned this one into a one-line
    /// dump of the creation script ending in the two words that mattered, which is
    /// what `T127` failed on.
    /// </remarks>
    [Fact]
    public void A_whole_script_block_in_the_cmdlet_position_still_yields_the_reason()
    {
        const string stderr = """
        $ErrorActionPreference = 'Stop'
        $shadow = [WMIClass]"root\cimv2:Win32_ShadowCopy"
        $result = $shadow.Create($Volume, "ClientAccessible") : Initialization failure
            + CategoryInfo          : NotSpecified: (:) [], RuntimeException
            + FullyQualifiedErrorId : Initialization failure
        """;

        Assert.Equal("Initialization failure", PowerShellError.Summarize(stderr));
    }

    // The marker that counts is the last one. A script block that contains " : "
    // of its own would otherwise cut the message off at the wrong colon and hand
    // the operator half a line of source.
    [Fact]
    public void A_colon_inside_the_script_block_does_not_take_the_message_with_it()
    {
        const string stderr = """
        Write-Host "volume : nothing"
        throw "no such thing" : The system cannot find the file specified.
        """;

        Assert.Equal(
            "The system cannot find the file specified.",
            PowerShellError.Summarize(stderr));
    }

    // The filter drops scaffolding, never content. A rendering that is somehow
    // all scaffolding must not come back as an empty string, because "the backup
    // failed" with no reason is worse than an ugly one.
    [Fact]
    public void A_rendering_with_no_message_left_falls_back_to_what_it_had()
    {
        const string stderr = """
        At line:1 char:1
        + Get-Nothing
        + ~~~~~~~~~~~
        """;

        Assert.Contains("Get-Nothing", PowerShellError.Summarize(stderr));
    }
}
