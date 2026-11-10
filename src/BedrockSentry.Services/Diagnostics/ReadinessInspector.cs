using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using BedrockSentry.Core.Broker;
using BedrockSentry.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// Whether the things BedrockSentry will need later are actually available
/// <em>now</em> (<c>docs/design.md</c> § 13).
/// </summary>
/// <remarks>
/// The other inspectors answer "why can nobody connect". This one answers a
/// question nobody thinks to ask until it is too late: the wrapper offers to
/// take Volume Shadow Copy backups, add firewall rules and set Defender
/// exclusions, and every one of those can fail at the moment it is needed for a
/// reason that was knowable weeks earlier. A VSS service somebody disabled to
/// save memory is discovered when the first snapshot backup fails, which on this
/// codebase's own schedule is at three in the morning.
///
/// Same contract as the rest of the family: three states so "could not ask" is
/// never reported as "not set up", a fix only where there is genuinely something
/// to do, and the fix says what it will change before it is pressed.
/// </remarks>
public sealed class ReadinessInspector : IVssServiceControl
{
    private readonly IPowerShellRunner _powerShell;
    private readonly ILogger<ReadinessInspector> _log;

    public ReadinessInspector(IPowerShellRunner powerShell, ILogger<ReadinessInspector> log)
    {
        _powerShell = powerShell;
        _log = log;
    }

    public const string VssTitle = "Volume Shadow Copy service";
    public const string PrivilegeTitle = "Service account";

    /// <summary>The self-update row's name.</summary>
    public const string SelfUpdateTitle = "Self-update";

    /// <summary>
    /// Whether this install can update itself: <paramref name="inactiveReason"/> is
    /// <c>UpdateService.InactiveReason</c>, null when it can.
    /// </summary>
    /// <remarks>
    /// Missing rather than Unknown when it cannot: the reason is known, and it is one
    /// no button here changes. A service under its own account updates only through
    /// the privilege broker, so with the broker off the row says so here instead of an
    /// update failing later with a sentence about access.
    /// </remarks>
    public static SystemCheck CheckSelfUpdate(string? inactiveReason) =>
        inactiveReason is null
            ? new SystemCheck(SelfUpdateTitle, SystemCheckState.Good, "This install can download and apply its own updates.")
            : new SystemCheck(SelfUpdateTitle, SystemCheckState.Missing, inactiveReason, CanFix: false);

    /// <summary>
    /// Reads the VSS service's status and start type, once.
    /// </summary>
    /// <remarks>
    /// <c>Get-Service</c> rather than <c>vssadmin</c>: vssadmin's output is
    /// localised prose with no machine-readable mode, and the two facts that
    /// matter — is it there, and has somebody disabled it — are both properties
    /// on the service object.
    /// </remarks>
    public async Task<VssState> ReadVssAsync(CancellationToken cancellationToken = default)
    {
        var result = await _powerShell.RunAsync(
                """
                $ErrorActionPreference = 'Stop'
                try {
                  $s = Get-Service -Name 'VSS'
                  [pscustomobject]@{
                    Status = $s.Status.ToString()
                    StartType = $s.StartType.ToString()
                  } | ConvertTo-Json -Compress
                } catch { Write-Error $_.Exception.Message }
                """,
                cancellationToken)
            .ConfigureAwait(false);

        return new VssState(result.Succeeded, result.Output, result.Reason);
    }

    /// <summary>
    /// Decides what the VSS reading means.
    /// </summary>
    /// <remarks>
    /// <b>Stopped is not a problem.</b> VSS is demand-started by design — it
    /// spends almost all of its life stopped on a perfectly healthy machine, and
    /// reporting that as something to fix would train people to ignore this
    /// panel. Only <c>Disabled</c> is actionable, because a disabled service
    /// cannot be demand-started and the backup that needs it fails.
    /// </remarks>
    public static SystemCheck CheckVss(VssState state, bool anyVssBackupsConfigured)
    {
        if (!state.Read)
        {
            return new SystemCheck(
                VssTitle, SystemCheckState.Unknown,
                $"Could not ask Windows about the service. {state.Error}".TrimEnd());
        }

        var (status, startType) = ParseService(state.Json);

        if (startType is null)
        {
            return new SystemCheck(
                VssTitle, SystemCheckState.Unknown,
                "Windows answered, but not with a start type this could read.");
        }

        if (startType.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return new SystemCheck(
                VssTitle,
                SystemCheckState.Missing,
                anyVssBackupsConfigured
                    ? "The service is disabled, and a server here is set to take Volume Shadow "
                      + "Copy backups. Those backups will fail."
                    : "The service is disabled. Nothing here needs it yet, but a Volume Shadow "
                      + "Copy backup would fail if you turned one on.",
                CanFix: true,
                FixDescription:
                    "Sets the Volume Shadow Copy service's start type back to Manual, which is "
                    + "the Windows default. It is not started — Windows starts it on demand.");
        }

        return new SystemCheck(
            VssTitle,
            SystemCheckState.Good,
            // Named rather than hidden, because "stopped" on this row is the
            // normal state and somebody checking will otherwise wonder.
            $"Start type is {startType}, which is what snapshot backups need. "
            + $"It is currently {status ?? "not reported"}, and that is fine — Windows starts it "
            + "when something asks for a snapshot.");
    }

    /// <summary>Sets VSS back to demand-start.</summary>
    public async Task<SystemCheckFix> EnableVssAsync(CancellationToken cancellationToken = default)
    {
        var result = await _powerShell.RunAsync(
                """
                $ErrorActionPreference = 'Stop'
                try {
                  Set-Service -Name 'VSS' -StartupType Manual
                  'ok'
                } catch { Write-Error $_.Exception.Message }
                """,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            _log.LogInformation("Set the Volume Shadow Copy service back to Manual start");
            return new SystemCheckFix(
                true, "Volume Shadow Copy is set to start on demand again.");
        }

        return new SystemCheckFix(false, result.Reason is { Length: > 0 } error
            ? error
            : "The service's start type could not be changed.");
    }

    /// <summary>
    /// Who does the administrative work this panel offers, and whether it can now
    /// (milestone 18.5): the privilege broker, or this process itself.
    /// </summary>
    /// <remarks>
    /// <para>A report, never a fix. A process cannot grant itself privileges, and the
    /// broker is installed or left out by <c>install.ps1</c>, which is a decision
    /// rather than a click.</para>
    /// <para>Listed first because it explains the rows below it. Without the broker, or
    /// with one that does not answer, every firewall fix, Defender exclusion and
    /// snapshot backup fails, each with its own unrelated-looking error; one row
    /// saying why is worth more than three reporting <c>Unknown</c>.</para>
    /// </remarks>
    /// <param name="account">This process's account, or null off Windows.</param>
    /// <param name="mode">Where the work runs, from the service's command line.</param>
    /// <param name="brokerService">The broker service's name.</param>
    /// <param name="answer">
    /// The broker's answer to <see cref="BrokerOperation.Status"/>; read only in
    /// <see cref="BrokerMode.Pipe"/>.
    /// </param>
    public static SystemCheck CheckPrivileges(
        ProcessAccount? account, BrokerMode mode, string brokerService, BrokerResponse? answer)
    {
        if (account is null)
        {
            return new SystemCheck(
                PrivilegeTitle, SystemCheckState.Unknown,
                "Not running on Windows, so there is nothing to check.");
        }

        if (mode is BrokerMode.Pipe)
        {
            return answer is { Succeeded: true }
                ? new SystemCheck(
                    PrivilegeTitle, SystemCheckState.Good,
                    $"Running as {account.Name}. The firewall, Defender and loopback fixes, shadow copies "
                    + $"and self-update are done by the {brokerService} service, which is answering.")
                : new SystemCheck(
                    PrivilegeTitle, SystemCheckState.Missing,
                    $"Running as {account.Name}. The {brokerService} service does this install's "
                    + $"administrative work, and asking it failed: {answer?.Message ?? "it was not asked."} "
                    + "Until it does, the fixes on this page, shadow-copy backups and self-update fail. "
                    + $"Start {brokerService} from Services, or run install.ps1 again.",
                    CanFix: false);
        }

        return account.IsAdministrator switch
        {
            true => new SystemCheck(
                PrivilegeTitle, SystemCheckState.Good,
                $"Running as {account.Name}, which can add firewall rules, set Defender exclusions "
                + "and take shadow copies."),
            false => new SystemCheck(PrivilegeTitle, SystemCheckState.Missing, NoBrokerReason(account), CanFix: false),
            null => new SystemCheck(
                PrivilegeTitle, SystemCheckState.Unknown,
                "Could not read the account this service runs as, so whether it can add firewall "
                + "rules, set Defender exclusions and take shadow copies is unknown."),
        };
    }

    /// <summary>
    /// Why the administrative fixes cannot run in this process: an install left without
    /// the broker (<c>-NoBroker</c>), under an account that is not an administrator.
    /// </summary>
    public static string NoBrokerReason(ProcessAccount account) =>
        $"Running as {account.Name} without the privilege broker. Servers and backups run, but the "
        + "firewall, Defender and loopback fixes, shadow-copy backups and self-update need it. Run "
        + "install.ps1 again without -NoBroker to turn them on.";

    /// <summary>The row's name, needed before its answer arrives.</summary>
    public static string PortTitle(string label) => $"Port availability — {label}";

    /// <summary>
    /// Turns a port conflict list into a row.
    /// </summary>
    /// <remarks>
    /// Reported here as well as on the properties tab, and deliberately: the tab
    /// checks a port somebody is about to save, and this checks the ports
    /// already saved. A collision that appears afterwards — another program
    /// installed, a second instance created — is exactly the kind that presents
    /// as a crash loop with no mention of ports anywhere.
    /// </remarks>
    /// <param name="unreadable">
    /// Why this instance's <c>server.properties</c> could not be read, when it
    /// could not (<c>T210</c>). Null otherwise.
    /// </param>
    public static SystemCheck CheckPorts(
        string label, IReadOnlyList<Instances.PortConflict> conflicts, string? unreadable = null)
    {
        var title = PortTitle(label);

        // Before anything else, because an empty conflict list is what a failed
        // read produces and it is indistinguishable from a clean one (`T210`).
        // "Its ports are free." was rendered against a port a foreign process
        // demonstrably held, on an instance whose file something else had open —
        // a green assurance about ports nothing managed to look up. Unknown is the
        // honest answer and it is the state this panel already has for it.
        if (!string.IsNullOrWhiteSpace(unreadable))
        {
            return new SystemCheck(
                title,
                SystemCheckState.Unknown,
                "Could not read this server's server.properties, so which ports it wants is "
                + $"unknown and nothing about them has been checked. {unreadable}");
        }

        if (conflicts.Count == 0)
        {
            return new SystemCheck(title, SystemCheckState.Good, "Its ports are free.");
        }

        return new SystemCheck(
            title,
            SystemCheckState.Missing,
            Instances.PortConflictText.Describe(conflicts),
            // Nothing to press. The wrapper cannot free somebody else's port,
            // and the answer — change this server's port, or stop whatever holds
            // it — is a choice.
            CanFix: false,
            Warning: Instances.PortConflictText.CrashLoopWarning);
    }

    private static (string? Status, string? StartType) ParseService(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return (null, null);
            }

            return (
                document.RootElement.TryGetProperty("Status", out var status)
                    ? status.GetString()
                    : null,
                document.RootElement.TryGetProperty("StartType", out var startType)
                    ? startType.GetString()
                    : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
