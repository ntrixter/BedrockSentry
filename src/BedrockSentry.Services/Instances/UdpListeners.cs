using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace BedrockSentry.Services.Instances;

/// <summary>A UDP socket open on this machine, and what has it open.</summary>
/// <param name="ProcessId">Zero when the owner could not be established.</param>
public readonly record struct UdpListener(int Port, int ProcessId);

/// <summary>
/// Which UDP ports are in use on this machine, and by whom.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IPGlobalProperties.GetActiveUdpListeners"/> answers the first half
/// and not the second, and the second half is what makes the answer useful:
/// "port 19132 is already open" leaves an administrator to go hunting, while
/// "port 19132 is open by bedrock_server.exe (PID 8124)" ends the question. It
/// is also what lets the port check tell a genuine conflict apart from an
/// instance's own running server, which would otherwise be reported as
/// colliding with itself.
/// </para>
/// <para>
/// Windows exposes the owning process through <c>GetExtendedUdpTable</c>. There
/// is no portable equivalent, so anywhere else this falls back to the ports
/// alone — which is what the tests run against.
/// </para>
/// </remarks>
public static class UdpListeners
{
    private const uint AddressFamilyIpV4 = 2;
    private const uint AddressFamilyIpV6 = 23;

    /// <summary>UDP_TABLE_OWNER_PID.</summary>
    private const uint TableWithOwningProcess = 1;

    private const uint NoError = 0;

    // DllImport rather than the newer LibraryImport: the source generator that
    // backs LibraryImport emits unsafe code, and turning AllowUnsafeBlocks on
    // for the whole assembly to get one P/Invoke is a poor trade. Nothing about
    // this signature needs custom marshalling.
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(
        nint table,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        uint addressFamily,
        uint tableClass,
        uint reserved);

    /// <summary>Every UDP socket currently open, both address families.</summary>
    public static IReadOnlyList<UdpListener> All()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [.. IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveUdpListeners()
                .Select(endpoint => new UdpListener(endpoint.Port, 0))];
        }

        try
        {
            // MIB_UDPROW_OWNER_PID   { addr, port, pid }                  — 12 bytes
            // MIB_UDP6ROW_OWNER_PID  { addr[16], scope, port, pid }       — 28 bytes
            return
            [
                .. Read(AddressFamilyIpV4, rowSize: 12, portOffset: 4, processOffset: 8),
                .. Read(AddressFamilyIpV6, rowSize: 28, portOffset: 20, processOffset: 24),
            ];
        }
        catch (DllNotFoundException)
        {
            // Nothing here is worth failing a save over. Without the answer the
            // check is where it was before this existed: the server itself
            // reports the collision if there is one.
            return [];
        }
        catch (EntryPointNotFoundException)
        {
            return [];
        }
    }

    private static List<UdpListener> Read(uint family, int rowSize, int portOffset, int processOffset)
    {
        var listeners = new List<UdpListener>();

        // Called twice on purpose: once with no buffer to be told how big one
        // has to be, then again to fill it.
        uint size = 0;
        GetExtendedUdpTable(nint.Zero, ref size, false, family, TableWithOwningProcess, 0);

        if (size == 0)
        {
            return listeners;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, false, family, TableWithOwningProcess, 0) != NoError)
            {
                return listeners;
            }

            var count = Marshal.ReadInt32(buffer);

            for (var i = 0; i < count; i++)
            {
                var row = buffer + sizeof(int) + (i * rowSize);

                listeners.Add(new UdpListener(
                    PortOf(Marshal.ReadInt32(row + portOffset)),
                    Marshal.ReadInt32(row + processOffset)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return listeners;
    }

    /// <summary>
    /// The port out of the table's DWORD, which holds it in network byte order.
    /// </summary>
    /// <remarks>
    /// Public so it can be tested directly. Read straight through, 19132 comes
    /// out as 48202 — itself a plausible-looking port number, which is exactly
    /// the kind of wrong that survives being glanced at.
    /// </remarks>
    public static int PortOf(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    /// <summary>
    /// What to call the owner of <paramref name="processId"/> in a sentence.
    /// </summary>
    public static string Describe(int processId)
    {
        if (processId == 0)
        {
            return "another program";
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return $"{process.ProcessName} (PID {processId})";
        }
        catch (ArgumentException)
        {
            // Exited between the table read and here.
            return $"PID {processId}";
        }
        catch (InvalidOperationException)
        {
            return $"PID {processId}";
        }
    }

    /// <summary>
    /// Whether <paramref name="processId"/> is this wrapper: this very process, or
    /// another process running under the same executable name.
    /// </summary>
    /// <remarks>
    /// The second case is a predecessor during a service restart or a self-update
    /// — the outgoing host, whose LAN discovery sockets outlive the SCM's "stopped"
    /// by a few seconds (<c>T82</c>). Matched by process name rather than path on
    /// purpose: after a self-update the two run from different version
    /// directories, and the handover is the case this exists for. The cost is that
    /// a second install's host (<c>D49</c>/<c>D50</c>, unsupported but allowed)
    /// also matches, and is treated as a reservation rather than a conflict — which
    /// only loses a refusal that was never about it. It is not a
    /// <c>bedrock_server.exe</c>, so it cannot have a world open (<c>T149</c>).
    /// </remarks>
    public static bool IsThisWrapper(int processId)
    {
        using var me = Process.GetCurrentProcess();

        return MatchesWrapperProcess(processId, Environment.ProcessId, me.ProcessName, NameOf);

        static string? NameOf(int id)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                return process.ProcessName;
            }
            catch (ArgumentException)
            {
                // No such process: the predecessor exited between the netstat
                // snapshot and this lookup, which is the common case.
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The decision behind <see cref="IsThisWrapper"/>, with the process table
    /// passed in: whether <paramref name="processId"/> is this process, or
    /// another running under <paramref name="ownProcessName"/>.
    /// </summary>
    /// <remarks>
    /// Split out so the suite can reach it (<c>T151</c> clause 7). The clause it
    /// came from can only be run on a real box across a real service restart,
    /// and it passed for the wrong reason: the predecessor's discovery ports were
    /// already released when the successor launched, so this matching was never
    /// consulted and could not have failed. The boot *ordering* genuinely needs a
    /// box. The *matching* does not, and it is the half a regression breaks —
    /// getting it wrong faults the auto-starting server on every restart.
    /// <paramref name="nameOf"/> returns <c>null</c> for a process that has since
    /// exited, which is not a match: a reservation held by nothing is not a
    /// reservation.
    /// </remarks>
    public static bool MatchesWrapperProcess(
        int processId,
        int ownProcessId,
        string ownProcessName,
        Func<int, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(nameOf);

        if (processId == ownProcessId)
        {
            return true;
        }

        // netstat reports 0 for a socket it cannot attribute to a process. It is
        // not a PID and must never match, or an unattributable listener would be
        // waved through as our own predecessor.
        if (processId == 0)
        {
            return false;
        }

        var name = nameOf(processId);

        return name is not null
            && string.Equals(name, ownProcessName, StringComparison.OrdinalIgnoreCase);
    }
}
