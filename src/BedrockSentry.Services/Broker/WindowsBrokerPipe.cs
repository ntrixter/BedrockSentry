using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace BedrockSentry.Services.Broker;

/// <summary>The broker's pipe on Windows, with the access list that is half its boundary.</summary>
/// <remarks>
/// <para>Who may open it: LocalSystem, Administrators and the main service's own
/// service SID, read and write only, so none of them can create another instance of
/// the name. The NETWORK SID is denied outright, whatever .NET does about remote
/// clients, because there is no <c>PipeOptions</c> flag that says it.</para>
/// <para>The other half is <see cref="ServiceProcessCallerCheck"/>: the service SID is
/// carried by every process the main service starts, Bedrock included, so the access
/// list alone admits them all.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsBrokerPipeFactory : IBrokerPipeFactory
{
    private readonly string _pipeName;
    private readonly PipeSecurity _security;

    /// <param name="pipeName">The pipe's name, derived from the installed service name.</param>
    /// <param name="mainServiceName">The main service, whose service SID the list names.</param>
    public WindowsBrokerPipeFactory(string pipeName, string mainServiceName)
    {
        _pipeName = pipeName;
        _security = Security(mainServiceName);
    }

    /// <summary>The access list, public so a test on Windows can read it back.</summary>
    /// <exception cref="IdentityNotMappedException">
    /// No service of that name is installed, so it has no SID to name. The broker
    /// does not start rather than serve a pipe nobody can be checked against.
    /// </exception>
    public static PipeSecurity Security(string mainServiceName)
    {
        var service = (SecurityIdentifier)new NTAccount("NT SERVICE", mainServiceName).Translate(typeof(SecurityIdentifier));
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(service, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return security;
    }

    public NamedPipeServerStream Create(bool firstInstance) =>
        NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            BrokerPipeServer.Instances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            inBufferSize: 0,
            outBufferSize: 0,
            _security);
}

/// <summary>The main service's admin pipe on Windows: elevated administrators and LocalSystem only.</summary>
/// <remarks>
/// <para>The list is the whole check (<c>D62</c>). An administrator's unelevated token
/// carries the Administrators SID for deny only, so it opens nothing, and an ordinary
/// local account, which may not stop the service, may no longer drain its servers
/// either (<c>T177</c>). NETWORK is denied outright.</para>
/// <para>The service's own account gets <see cref="PipeAccessRights.ReadWrite"/> and
/// <see cref="PipeAccessRights.CreateNewInstance"/>: Windows checks a second instance
/// against the list for the read and write its open mode asks as well as for the new
/// instance, and with the instance right alone the service under
/// <c>NT SERVICE\&lt;ServiceName&gt;</c> could not create it (<c>T71</c>, 2026-10-21; an
/// elevated runner never sees this). With both held at the instance limit, nothing can
/// add a third. A server carrying the account runs at low integrity, and the pipe's default medium label
/// refuses it every write, so it opens nothing for writing (<c>D126</c>). OWNER RIGHTS is named
/// with the instance right alone, because the account that creates the pipe owns it,
/// and an owner otherwise holds READ_CONTROL and WRITE_DAC implicitly. The installer
/// still checks that the process answering is the service's.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsAdminPipeFactory : IAdminPipeFactory
{
    private readonly string _pipeName;
    private readonly PipeSecurity _security = Security();

    /// <summary>OWNER RIGHTS (S-1-3-4): an entry naming it replaces the owner's implicit rights.</summary>
    public static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    public WindowsAdminPipeFactory(string pipeName) => _pipeName = pipeName;

    /// <summary>The access list, public so a test on Windows can read it back.</summary>
    public static PipeSecurity Security()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(OwnerRights, PipeAccessRights.CreateNewInstance, AccessControlType.Allow));

        if (WindowsIdentity.GetCurrent().User is { } self && self != system)
        {
            security.AddAccessRule(new PipeAccessRule(
                self, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        }

        return security;
    }

    public NamedPipeServerStream Create(bool firstInstance) =>
        NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            AdminPipeServer.Instances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            inBufferSize: 0,
            outBufferSize: 0,
            _security);
}

/// <summary>
/// Admits a caller only when its process is the main service's, as the service
/// control manager reports it now.
/// </summary>
/// <remarks>
/// Asked on every connection rather than remembered: the service restarts with a new
/// process id, and an id remembered past that could be handed to anything. The
/// caller is connected while it is asked, so its id cannot be reissued in between.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ServiceProcessCallerCheck : IBrokerCallerCheck
{
    private readonly string _mainServiceName;

    public ServiceProcessCallerCheck(string mainServiceName) => _mainServiceName = mainServiceName;

    public string? Refuse(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client))
        {
            return $"the caller's process could not be identified ({new Win32Exception().Message})";
        }

        if (ServiceProcessId(_mainServiceName) is not { } service)
        {
            return $"{_mainServiceName} is not running, so no caller is its process";
        }

        return client == service
            ? null
            : $"process {client} is not {_mainServiceName} (process {service})";
    }

    /// <summary>The service's process id, or null when it is not running or cannot be asked.</summary>
    public static uint? ServiceProcessId(string serviceName)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);

        if (manager == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus);

            if (service == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                if (!QueryServiceStatusEx(
                        service, ScStatusProcessInfo, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _))
                {
                    return null;
                }

                return status.CurrentState == ServiceRunning && status.ProcessId != 0 ? status.ProcessId : null;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service, int infoLevel, out ServiceStatusProcess status, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

/// <summary>
/// Trusts the server end of the broker's pipe only when its process is the broker
/// service's, as the service control manager reports it now.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceProcessServerCheck : IBrokerServerCheck
{
    private readonly string _brokerServiceName;

    public ServiceProcessServerCheck(string brokerServiceName) => _brokerServiceName = brokerServiceName;

    public string? Refuse(NamedPipeClientStream pipe)
    {
        if (!ServiceProcessCallerCheck.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server))
        {
            return $"its process could not be identified ({new Win32Exception().Message})";
        }

        if (ServiceProcessCallerCheck.ServiceProcessId(_brokerServiceName) is not { } broker)
        {
            return $"{_brokerServiceName} is not running, so process {server} is not it";
        }

        return server == broker ? null : $"process {server} is not {_brokerServiceName} (process {broker})";
    }
}
