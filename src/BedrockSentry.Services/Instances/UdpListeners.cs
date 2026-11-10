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
    /// Which of the three things holding a port can be (<c>D74</c>).
    /// </summary>
    /// <remarks>
    /// This was a <c>bool</c>, and the missing state is what `T200` cost. The
    /// world holds three cases and they want three different answers: a
    /// <see cref="Foreign"/> holder means refuse; <see cref="Ours"/> is this
    /// process's own standing reservation, released by the very start being
    /// checked, so it is the reason a launch <em>can</em> bind rather than a
    /// reason it cannot; and <see cref="Predecessor"/> is neither. A predecessor's
    /// sockets are in another process, so this one cannot release them - but that
    /// process is on its way out, so refusing would fault the auto-starting
    /// server on every service restart.
    ///
    /// Collapsed to two, the third was forced into the second and the launch went
    /// ahead into a port that was genuinely still held: BDS opens the world
    /// before it binds, so the crash is an unclean close on a live world, and it
    /// was reported to the operator as an ordinary watchdog failure with no
    /// mention of ports.
    /// </remarks>
    public enum WrapperHold
    {
        /// <summary>Not this wrapper. A real collision, or an unattributable socket.</summary>
        Foreign,

        /// <summary>This very process. Released by this instance's own start.</summary>
        Ours,

        /// <summary>
        /// Another process running under this wrapper's name — an outgoing host
        /// that has not finished exiting. Released when <em>that</em> process
        /// dies, which is soon and is not something this one can bring about.
        /// </summary>
        Predecessor,
    }

    /// <summary>
    /// Which of <see cref="WrapperHold"/> the holder of a port is.
    /// </summary>
    /// <remarks>
    /// The same matching <see cref="IsThisWrapper"/> does, with the answer kept
    /// whole. See <see cref="ClassifyWrapperProcess"/> for the rule and the
    /// reasons behind it.
    /// </remarks>
    public static WrapperHold HoldKindOf(int processId)
    {
        using var me = Process.GetCurrentProcess();

        return ClassifyWrapperProcess(
            processId, Environment.ProcessId, me.ProcessName, NameOf, InstallRootOf(Environment.ProcessPath), PathOf);
    }

    /// <summary>
    /// Whether <paramref name="processId"/> is this wrapper: this very process, or
    /// another host of this same install.
    /// </summary>
    /// <remarks>
    /// The second case is a predecessor during a service restart or a self-update
    /// — the outgoing host, whose LAN discovery sockets outlive the SCM's "stopped"
    /// by a few seconds (<c>T82</c>). Matched by name and by install root
    /// (<see cref="InstallRootOf"/>), which a self-update keeps while it moves the
    /// version folder. A second install's host (<c>D49</c>/<c>D50</c>) has the name
    /// and not the root, so its hold is a conflict (<c>T404</c>): read as a
    /// predecessor's, it let a server launch into ports that host kept, and BDS
    /// failed at once, four times, with the world open.
    /// </remarks>
    public static bool IsThisWrapper(int processId)
    {
        using var me = Process.GetCurrentProcess();

        return MatchesWrapperProcess(
            processId, Environment.ProcessId, me.ProcessName, NameOf, InstallRootOf(Environment.ProcessPath), PathOf);
    }

    private static string? NameOf(int id)
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

    // Null when it cannot be read, which another account's process usually cannot be:
    // a predecessor runs as this service's own account, so its path reads.
    private static string? PathOf(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The install a host's executable belongs to: the folder above <c>current</c> or
    /// above <c>versions\&lt;build&gt;</c>, or the folder above any other, or null.
    /// </summary>
    public static string? InstallRootOf(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)
            || Path.GetDirectoryName(Path.GetFullPath(executable)) is not { } folder
            || Path.GetDirectoryName(folder) is not { } parent)
        {
            return null;
        }

        return string.Equals(Path.GetFileName(parent), "versions", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(parent)
            : parent;
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
        Func<int, string?> nameOf,
        string? ownInstallRoot,
        Func<int, string?> executableOf) =>
        ClassifyWrapperProcess(processId, ownProcessId, ownProcessName, nameOf, ownInstallRoot, executableOf)
            != WrapperHold.Foreign;

    /// <summary>
    /// The rule behind <see cref="HoldKindOf"/>, with the process table passed
    /// in: whether <paramref name="processId"/> is this process, another running
    /// under <paramref name="ownProcessName"/>, or neither.
    /// </summary>
    /// <remarks>
    /// Split out so the suite can reach it (<c>T151</c> clause 7a). The ordering
    /// half of that clause needs a real service restart on a real machine; this
    /// half does not, and it is the half a regression breaks — getting it wrong
    /// faults the auto-starting server on every boot.
    /// <para>
    /// <paramref name="nameOf"/> returns <c>null</c> for a process that has since
    /// exited, which is <see cref="WrapperHold.Foreign"/>: a reservation held by
    /// nothing is not a reservation. Nothing waits on one either, which is why
    /// <c>D74</c>'s wait cannot be started by a stale PID.
    /// </para>
    /// <para>
    /// A host of another install, or one whose executable cannot be read, is
    /// <see cref="WrapperHold.Foreign"/> too (<c>T404</c>): only a host of
    /// <paramref name="ownInstallRoot"/> is on its way out.
    /// </para>
    /// </remarks>
    public static WrapperHold ClassifyWrapperProcess(
        int processId,
        int ownProcessId,
        string ownProcessName,
        Func<int, string?> nameOf,
        string? ownInstallRoot,
        Func<int, string?> executableOf)
    {
        ArgumentNullException.ThrowIfNull(nameOf);
        ArgumentNullException.ThrowIfNull(executableOf);

        if (processId == ownProcessId)
        {
            return WrapperHold.Ours;
        }

        // netstat reports 0 for a socket it cannot attribute to a process. It is
        // not a PID and must never match, or an unattributable listener would be
        // waved through as our own predecessor.
        if (processId == 0)
        {
            return WrapperHold.Foreign;
        }

        var name = nameOf(processId);

        return name is not null
            && string.Equals(name, ownProcessName, StringComparison.OrdinalIgnoreCase)
            && ownInstallRoot is not null
            && InstallRootOf(executableOf(processId)) is { } theirs
            && string.Equals(
                Path.TrimEndingDirectorySeparator(theirs),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownInstallRoot)),
                StringComparison.OrdinalIgnoreCase)
                ? WrapperHold.Predecessor
                : WrapperHold.Foreign;
    }
}
