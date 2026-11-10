using System.Runtime.Versioning;
using System.Security.Principal;
using BedrockSentry.Core.Broker;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Services.Broker;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>The account this process runs as.</summary>
/// <param name="IsAdministrator">Null when Windows would not say.</param>
public sealed record ProcessAccount(string Name, bool? IsAdministrator)
{
    /// <summary>What a process that cannot read its own token reports.</summary>
    public static readonly ProcessAccount Unreadable = new("an account this service could not read", null);

    /// <summary>This process's account, <see cref="Unreadable"/> when it cannot be read, or null off Windows.</summary>
    public static ProcessAccount? Current() => OperatingSystem.IsWindows() ? ReadWindows() : null;

    [SupportedOSPlatform("windows")]
    private static ProcessAccount ReadWindows()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();

            return new ProcessAccount(
                identity.Name ?? "an unnamed account",
                new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return Unreadable;
        }
    }
}

/// <summary>
/// Where this service's administrative work runs (milestone 18.5), as the panel needs
/// to say it: the Readiness row, and whether a fix button can do anything here.
/// </summary>
public sealed class PrivilegedWork
{
    private readonly IBrokerChannel? _broker;

    /// <param name="mode">From the service's command line (<see cref="BrokerNames.ModeArgument"/>).</param>
    /// <param name="brokerService">The broker service's name.</param>
    /// <param name="broker">The broker's channel, in <see cref="BrokerMode.Pipe"/>; null otherwise.</param>
    /// <param name="account">This process's account, or null off Windows.</param>
    public PrivilegedWork(BrokerMode mode, string brokerService, IBrokerChannel? broker, ProcessAccount? account)
    {
        Mode = mode;
        BrokerService = brokerService;
        _broker = mode is BrokerMode.Pipe ? broker : null;
        Account = account;
    }

    public BrokerMode Mode { get; }

    public string BrokerService { get; }

    public ProcessAccount? Account { get; }

    /// <summary>
    /// Why a firewall, Defender, loopback or VSS fix cannot run here, or null when one
    /// can be tried.
    /// </summary>
    /// <remarks>
    /// Only without the broker, under an account that is not an administrator: the fix
    /// would fail with a sentence about access. With the broker each fix is tried, and an
    /// unreachable broker answers with its own reason.
    /// </remarks>
    public string? FixesUnavailable =>
        Mode is BrokerMode.InProcess && Account is { IsAdministrator: false }
            ? "Needs the privilege broker, which this install leaves out (Service account, under Readiness, says more)."
            : null;

    /// <summary>The Readiness row, asking the broker whether it answers.</summary>
    public async Task<SystemCheck> CheckAsync(CancellationToken ct = default)
    {
        var answer = _broker is null || Account is null
            ? null
            : await _broker.SendAsync(new BrokerRequest { Operation = BrokerOperation.Status }, ct);

        return ReadinessInspector.CheckPrivileges(Account, Mode, BrokerService, answer);
    }
}
