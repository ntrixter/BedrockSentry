using System.Globalization;

namespace BedrockSentry.Core.Instances;

/// <summary>What a <c>server-udp-ports</c> value turned out to be.</summary>
/// <remarks>
/// <para><strong>Four states, and the fourth is the one that must not be folded
/// into the first.</strong> An <em>absent</em> key has a measured meaning — under
/// <see cref="ServerTransport.NetherNet"/> the play socket lands on
/// <c>server-port</c>'s UDP side — so it decides which sockets
/// <see cref="ServerPorts.For"/> returns. A value this code cannot read means
/// nothing is known about where play will land, and reporting it as absent would
/// state that measurement about a server it was never taken on.</para>
/// </remarks>
public enum UdpPortsKind
{
    /// <summary>The key is not set: no line, or every line commented out.</summary>
    Absent,

    /// <summary>
    /// <c>start-end</c>, or a single port — the window the server allocates play
    /// sockets from, published under the address clients already reach it on.
    /// </summary>
    LocalWindow,

    /// <summary>
    /// <c>[ip:]external[-external]:internal[-internal]</c> — a window plus the
    /// address and ports the outside world sees it as.
    /// </summary>
    NatMapping,

    /// <summary>Set to something this code does not understand.</summary>
    Unreadable,
}

/// <summary>An inclusive run of port numbers.</summary>
/// <param name="First">The low end.</param>
/// <param name="Last">The high end, equal to <paramref name="First"/> for one port.</param>
public readonly record struct PortSpan(int First, int Last)
{
    /// <summary>How many ports the span holds — one, for a single port.</summary>
    public int Width => Last - First + 1;

    /// <summary>The span as <c>server.properties</c> spells it.</summary>
    public override string ToString() =>
        First == Last
            ? First.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{First}-{Last}");
}

/// <summary>
/// One <c>server-udp-ports</c> line, read (<c>D71</c>).
/// </summary>
/// <param name="Kind">Which of the four states this line is in.</param>
/// <param name="Local">
/// The ports this machine binds. Set for <see cref="UdpPortsKind.LocalWindow"/>
/// and <see cref="UdpPortsKind.NatMapping"/>, null otherwise — this is the half
/// a port check, a firewall rule and the Status card all want.
/// </param>
/// <param name="External">
/// The ports the outside world reaches, when the line publishes a NAT mapping.
/// </param>
/// <param name="Address">
/// The address the outside world reaches, when the line publishes one. Optional
/// even within a mapping.
/// </param>
/// <param name="Value">The line as written, for showing back to whoever wrote it.</param>
public sealed record ServerUdpPorts(
    UdpPortsKind Kind,
    PortSpan? Local = null,
    PortSpan? External = null,
    string? Address = null,
    string? Value = null)
{
    /// <summary>
    /// The <c>server.properties</c> key, named once for everything that reads or
    /// writes it.
    /// </summary>
    /// <remarks>
    /// <c>ServerPropertiesDocument.AccumulatingKey</c> cites this rather than
    /// carrying its own copy: one string stated in two places is the defect this
    /// codebase keeps re-finding, and the writer's reason for naming it (BDS acts
    /// on every set line) and the reader's are two reasons to name one key.
    /// </remarks>
    public const string Key = "server-udp-ports";

    /// <summary>The key is set and this code knows what it says.</summary>
    public bool IsPinned => Kind is UdpPortsKind.LocalWindow or UdpPortsKind.NatMapping;

    /// <summary>Nothing is set at all.</summary>
    public static readonly ServerUdpPorts Absent = new(UdpPortsKind.Absent);

    /// <summary>
    /// What one <c>server-udp-ports</c> line says. Pass null or blank for a key
    /// that is not set.
    /// </summary>
    /// <remarks>
    /// <para>The discriminator is the number of colons, counted from the right so
    /// that an address carrying its own — which nothing here has measured BDS
    /// accepting, and which this refuses to rule out on that basis — keeps its
    /// colons rather than being split across them. None is a window; one is
    /// <c>external:internal</c>; more leaves everything before the last two as
    /// the address.</para>
    ///
    /// <para><strong>An inverted or out-of-range span is
    /// <see cref="UdpPortsKind.Unreadable"/> rather than repaired.</strong> The
    /// wrapper does not write this key (<c>D71</c> is advisory), so every value
    /// reaching here was typed by somebody; saying plainly that it cannot be read
    /// is worth more than a width computed from a typo, which would then be
    /// compared against <c>max-players</c> and reported as fine.
    /// <c>ServerUdpPortsTests</c> pins each arm.</para>
    /// </remarks>
    public static ServerUdpPorts Parse(string? value)
    {
        var text = value?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            return Absent;
        }

        var lastColon = text.LastIndexOf(':');

        if (lastColon < 0)
        {
            return TrySpan(text, out var window)
                ? new ServerUdpPorts(UdpPortsKind.LocalWindow, Local: window, Value: text)
                : Unreadable(text);
        }

        var internalText = text[(lastColon + 1)..];
        var head = text[..lastColon];
        var secondColon = head.LastIndexOf(':');

        var externalText = secondColon < 0 ? head : head[(secondColon + 1)..];
        var address = secondColon < 0 ? null : head[..secondColon];

        if (address is not null && address.Length == 0)
        {
            return Unreadable(text);
        }

        return TrySpan(internalText, out var local) && TrySpan(externalText, out var published)
            ? new ServerUdpPorts(
                UdpPortsKind.NatMapping,
                Local: local,
                External: published,
                Address: address,
                Value: text)
            : Unreadable(text);

        static ServerUdpPorts Unreadable(string text) =>
            new(UdpPortsKind.Unreadable, Value: text);
    }

    /// <summary>
    /// The accumulated answer for a key BDS reads from <em>every</em> set line
    /// (<c>D72</c>). Empty input is <see cref="Absent"/>.
    /// </summary>
    /// <remarks>
    /// <para>One unreadable line makes the whole answer unreadable, and does so
    /// deliberately: BDS acts on all of them, so a window computed from the lines
    /// that parsed is narrower than what the server will actually use, and it
    /// would be reported as the whole truth.</para>
    ///
    /// <para>Otherwise the local window spans from the lowest first to the highest
    /// last across every line, which over-states a gap between two disjoint
    /// windows. That is the safe direction for the two things this feeds — a
    /// width compared against <c>max-players</c>, and a sentence naming the ports
    /// to forward.</para>
    /// </remarks>
    public static ServerUdpPorts ParseAll(IReadOnlyList<string> values)
    {
        var lines = values
            .Select(Parse)
            .Where(line => line.Kind is not UdpPortsKind.Absent)
            .ToList();

        if (lines.Count == 0)
        {
            return Absent;
        }

        if (lines.Count == 1)
        {
            return lines[0];
        }

        var joined = string.Join(", ", lines.Select(line => line.Value));

        if (lines.Any(line => line.Kind is UdpPortsKind.Unreadable))
        {
            return new ServerUdpPorts(UdpPortsKind.Unreadable, Value: joined);
        }

        var mapping = lines.FirstOrDefault(line => line.Kind is UdpPortsKind.NatMapping);

        return new ServerUdpPorts(
            mapping is null ? UdpPortsKind.LocalWindow : UdpPortsKind.NatMapping,
            Local: new PortSpan(
                lines.Min(line => line.Local!.Value.First),
                lines.Max(line => line.Local!.Value.Last)),
            External: mapping?.External,
            Address: mapping?.Address,
            Value: joined);
    }

    /// <summary>
    /// The line to offer an administrator: <paramref name="maxPlayers"/> ports
    /// from <paramref name="basePort"/>, published on
    /// <paramref name="publicAddress"/> when one is known.
    /// </summary>
    /// <remarks>
    /// <para>Null when <paramref name="maxPlayers"/> is not set, rather than a
    /// width taken from BDS's own default for that key. The default is not read
    /// from anywhere here, and a suggested range that is one player too narrow
    /// fails by refusing a join — the failure nobody connects to a port range.</para>
    ///
    /// <para>The width is <paramref name="maxPlayers"/> exactly, which is the
    /// measured rate of one UDP port per player online rather than a margin
    /// chosen here. The external half mirrors the internal one because the
    /// wrapper cannot see the router: an administrator forwarding a block
    /// straight through is the arrangement it can describe, and any other is
    /// theirs to write.</para>
    /// </remarks>
    public static string? Suggest(string? publicAddress, int basePort, int? maxPlayers)
    {
        if (maxPlayers is not { } players || players < 1)
        {
            return null;
        }

        if (basePort < 1 || basePort + players - 1 > 65535)
        {
            return null;
        }

        var span = new PortSpan(basePort, basePort + players - 1);
        var address = publicAddress?.Trim();

        return string.IsNullOrEmpty(address)
            ? $"{span}:{span}"
            : $"{address}:{span}:{span}";
    }

    private static bool TrySpan(string text, out PortSpan span)
    {
        span = default;
        text = text.Trim();

        if (text.Length == 0)
        {
            return false;
        }

        var dash = text.IndexOf('-');

        if (dash < 0)
        {
            if (!TryPort(text, out var only))
            {
                return false;
            }

            span = new PortSpan(only, only);
            return true;
        }

        if (!TryPort(text[..dash], out var first) || !TryPort(text[(dash + 1)..], out var last))
        {
            return false;
        }

        if (last < first)
        {
            return false;
        }

        span = new PortSpan(first, last);
        return true;
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
        && port is > 0 and <= 65535;
}
