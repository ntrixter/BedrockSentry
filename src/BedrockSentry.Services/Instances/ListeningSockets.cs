using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using BedrockSentry.Core.Instances;

namespace BedrockSentry.Services.Instances;

/// <summary>A socket open on this machine, and what has it open.</summary>
/// <param name="ProcessId">Zero when the owner could not be established.</param>
public readonly record struct SocketListener(int Port, int ProcessId);

/// <summary>
/// Who holds a port, on whichever protocol is being asked about (<c>D70</c>).
/// </summary>
/// <remarks>
/// <para>One front door rather than a caller picking a table, because picking the
/// wrong one does not fail — it answers <em>free</em>. A
/// <see cref="ServerTransport.NetherNet"/> server holds <c>server-port</c> as a
/// TCP listener from the moment it starts, so a pre-flight reading only the UDP
/// table was asking a table that could not contain the answer and clearing the
/// launch. That is a false clear on the one check written to stop a second process
/// opening a live world (<c>T149</c>).</para>
///
/// <para><strong>And the UDP half of that number is not reliably empty either</strong>
/// (<c>D71</c>): play sockets land there once players connect, so the same
/// question answered <em>free</em> against an idle server and <em>taken</em>
/// against a busy one. Whichever way it fell, it fell on how many people happened
/// to be playing, which is the worst property a pre-flight can have — so such a
/// server is asked on both tables rather than one being chosen for it. Only when
/// <c>server-udp-ports</c> moves play elsewhere does that number go back to being
/// TCP alone, and then it is the file saying so rather than a capture.</para>
///
/// <para>The UDP half stays in <see cref="UdpListeners"/>, which also owns the
/// process-identity rules that <c>D74</c> pinned and which are the same whatever
/// protocol a socket speaks. This type adds the TCP table beside it and chooses
/// between them.</para>
/// </remarks>
public static class ListeningSockets
{
    private const uint AddressFamilyIpV4 = 2;
    private const uint AddressFamilyIpV6 = 23;

    /// <summary>
    /// TCP_TABLE_OWNER_PID_LISTENER. Deliberately not the ALL class: that one
    /// returns every connection, so each outbound socket's local ephemeral port
    /// would read as a port somebody holds, and a browser would refuse a start.
    /// </summary>
    private const uint ListenersWithOwningProcess = 3;

    private const uint NoError = 0;

    // DllImport rather than LibraryImport for the reason UdpListeners gives: the
    // source generator emits unsafe code, and switching AllowUnsafeBlocks on for
    // the assembly to get one P/Invoke is a poor trade.
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(
        nint table,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        uint addressFamily,
        uint tableClass,
        uint reserved);

    /// <summary>Every socket currently open on <paramref name="protocol"/>, both address families.</summary>
    public static IReadOnlyList<SocketListener> On(PortProtocol protocol) => protocol switch
    {
        PortProtocol.Tcp => Tcp(),
        _ => [.. UdpListeners.All().Select(l => new SocketListener(l.Port, l.ProcessId))],
    };

    private static IReadOnlyList<SocketListener> Tcp()
    {
        if (!OperatingSystem.IsWindows())
        {
            // No owning process off Windows, the same limit the UDP reader has
            // and the same shape the tests run against.
            return [.. IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => new SocketListener(endpoint.Port, 0))];
        }

        try
        {
            // MIB_TCPROW_OWNER_PID   { state, addr, port, remote, remotePort, pid }         — 24 bytes
            // MIB_TCP6ROW_OWNER_PID  { addr[16], scope, port, remote[16], remoteScope,
            //                          remotePort, state, pid }                             — 56 bytes
            return
            [
                .. Read(AddressFamilyIpV4, rowSize: 24, portOffset: 8, processOffset: 20),
                .. Read(AddressFamilyIpV6, rowSize: 56, portOffset: 20, processOffset: 52),
            ];
        }
        catch (DllNotFoundException)
        {
            // Nothing here is worth failing a start over. Without the answer the
            // check is where it was before this existed.
            return [];
        }
        catch (EntryPointNotFoundException)
        {
            return [];
        }
    }

    private static List<SocketListener> Read(uint family, int rowSize, int portOffset, int processOffset)
    {
        var listeners = new List<SocketListener>();

        // Called twice on purpose: once with no buffer to be told how big one has
        // to be, then again to fill it.
        uint size = 0;
        GetExtendedTcpTable(nint.Zero, ref size, false, family, ListenersWithOwningProcess, 0);

        if (size == 0)
        {
            return listeners;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, ListenersWithOwningProcess, 0) != NoError)
            {
                return listeners;
            }

            var count = Marshal.ReadInt32(buffer);

            for (var i = 0; i < count; i++)
            {
                var row = buffer + sizeof(int) + (i * rowSize);

                listeners.Add(new SocketListener(
                    // The MIB tables hold the port in network byte order — a
                    // property of the tables rather than of UDP, which is why the
                    // conversion is shared rather than written again here. Read
                    // straight through, 19132 comes out as 48202.
                    UdpListeners.PortOf(Marshal.ReadInt32(row + portOffset)),
                    Marshal.ReadInt32(row + processOffset)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return listeners;
    }
}
