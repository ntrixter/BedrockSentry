using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
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
public sealed class ReadinessInspector
{
    private readonly IPowerShellRunner _powerShell;
    private readonly ILogger<ReadinessInspector> _log;

    public ReadinessInspector(IPowerShellRunner powerShell, ILogger<ReadinessInspector> log)
    {
        _powerShell = powerShell;
        _log = log;
    }

    /// <summary>What the machine said about the Volume Shadow Copy service.</summary>
    public sealed record VssState(bool Read, string Json, string Error);

    public const string VssTitle = "Volume Shadow Copy service";
    public const string PrivilegeTitle = "Service account";

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
    /// Whether the account the wrapper runs as can do the things it offers.
    /// </summary>
    /// <remarks>
    /// A report, never a fix. A process cannot grant itself privileges it does
    /// not have, and a button implying otherwise would be a lie — the answer is
    /// to reinstall the service under an account that has them, which is a
    /// decision rather than a click.
    ///
    /// This matters more than it looks. Run under a plain user account, the
    /// wrapper still starts, still serves the panel and still runs servers — and
    /// then every firewall fix, every Defender exclusion and every snapshot
    /// backup fails, each with its own unrelated-looking error. One row saying
    /// why is worth more than three checks reporting <c>Unknown</c>.
    /// </remarks>
    public static SystemCheck CheckPrivileges()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SystemCheck(
                PrivilegeTitle, SystemCheckState.Unknown,
                "Not running on Windows, so there is nothing to check.");
        }

        return CheckWindowsPrivileges();
    }

    [SupportedOSPlatform("windows")]
    private static SystemCheck CheckWindowsPrivileges()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            var name = identity.Name ?? "an unnamed account";

            if (principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                return new SystemCheck(
                    PrivilegeTitle, SystemCheckState.Good,
                    $"Running as {name}, which can add firewall rules, set Defender exclusions "
                    + "and take shadow copies.");
            }

            return new SystemCheck(
                PrivilegeTitle,
                SystemCheckState.Missing,
                $"Running as {name}, which is not an administrator. Servers will run, but the "
                + "firewall and Defender fixes on this page and Volume Shadow Copy backups will "
                + "all fail. Reinstall the service under LocalSystem or an administrator account "
                + "to change this.",
                // No fix. A process cannot grant itself privileges, and a button
                // that pretended to would be worse than none.
                CanFix: false);
        }
        catch (Exception ex)
        {
            return new SystemCheck(
                PrivilegeTitle, SystemCheckState.Unknown,
                $"Could not read the account this is running as. {ex.Message}");
        }
    }

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
    public static SystemCheck CheckPorts(string label, IReadOnlyList<Instances.PortConflict> conflicts)
    {
        var title = PortTitle(label);

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
