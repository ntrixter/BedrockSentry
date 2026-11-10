using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>One inbound rule that belongs to a server, as the firewall listed it.</summary>
/// <param name="Id">The rule's own <c>Name</c>, which the firewall keeps unique.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Protocol">Null for a protocol other than TCP or UDP.</param>
/// <param name="Ports">Null unless its <c>LocalPort</c> is one port or one run of ports.</param>
public sealed record ServerRule(string Id, string Name, PortProtocol? Protocol, PortSpan? Ports);

/// <summary>What one step of an update does to the firewall.</summary>
public enum RuleChange
{
    /// <summary>The rule already opens a socket the server uses, under the right name.</summary>
    Keep,

    /// <summary>The rule opens a socket the server uses, under another name.</summary>
    Rename,

    /// <summary>The rule opens a port the server no longer uses; it is moved to one it does.</summary>
    Repoint,

    /// <summary>No rule of the server's is left to move, so one is added.</summary>
    Add,

    /// <summary>The rule opens a port the server no longer uses, and no socket is left to move it to.</summary>
    Remove,
}

/// <summary>One step of <see cref="FirewallRuleSync.Plan"/>.</summary>
/// <param name="Rule">The rule acted on; null for <see cref="RuleChange.Add"/>.</param>
/// <param name="NewName">The name the rule ends with; null for <see cref="RuleChange.Remove"/>.</param>
/// <param name="Socket">The socket the rule ends opening; null for <see cref="RuleChange.Remove"/>.</param>
public sealed record RuleStep(RuleChange Change, ServerRule? Rule, string? NewName, ReachableSockets? Socket);

/// <summary>
/// Keeps the rules <see cref="FirewallInspector"/> made for a server in step with the
/// server: its name after a rename, its ports after a port change (<c>T411</c>).
/// </summary>
/// <remarks>
/// <para>A rule belongs to a server when its description is the server's
/// <see cref="Tag"/>, or when its name matches <see cref="FirewallInspector.RulePattern"/>
/// for the server's current name. The tag is what survives a rename: the name is the
/// server's display name, which the operator changes, and the instance id is not.
/// Rules made before the tag existed carry none, so a rename labels them under the old
/// name first and renames them under the new one second.</para>
/// <para>The plan is computed here from a listing rather than in the script, so the
/// decisions are testable without a firewall.</para>
/// </remarks>
public static partial class FirewallRuleSync
{
    /// <summary>The description every rule made for a server carries.</summary>
    public static string Tag(string instanceId) => $"BedrockSentry server {instanceId}";

    /// <summary>
    /// <paramref name="ruleName"/> under <paramref name="instanceName"/>, keeping its
    /// protocol and ports; null when it is not a name <see cref="FirewallInspector.RuleName(string, PortSpan, PortProtocol)"/> gives.
    /// </summary>
    public static string? Renamed(string ruleName, string instanceName)
    {
        var match = NameSuffix().Match(ruleName);
        return match.Success ? $"BedrockSentry - {instanceName} ({match.Groups[1].Value})" : null;
    }

    /// <summary>The rules a listing script printed: one object or an array of them.</summary>
    /// <remarks>
    /// A rule whose id is not a plain name is left out, so nothing the firewall holds
    /// can reach a script as a wildcard (<c>-Name</c> takes them).
    /// </remarks>
    public static IReadOnlyList<ServerRule> ParseListing(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var elements = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : root.ValueKind == JsonValueKind.Object ? [root] : [];

        var rules = new List<ServerRule>();

        foreach (var element in elements)
        {
            var id = Text(element, "Id");
            var name = Text(element, "Name");

            if (id is null || name is null || !PlainId().IsMatch(id))
            {
                continue;
            }

            rules.Add(new ServerRule(id, name, ProtocolOf(Text(element, "Protocol")), SpanOf(element)));
        }

        return rules;
    }

    /// <summary>
    /// The steps that leave the server's rules opening exactly <paramref name="wanted"/>
    /// under its current name, moving existing rules before adding any.
    /// </summary>
    /// <remarks>
    /// A rule that already opens a wanted socket keeps it. Then each socket still
    /// uncovered takes a leftover rule of the same protocol, so the operator's rule is
    /// updated rather than a second added beside it. A socket with nothing left to take
    /// gets a new rule; a leftover with nothing left to cover is removed, because it
    /// opens a port this server no longer listens on. A rule on another protocol than
    /// TCP or UDP, or on ports this cannot read, is never moved, only removed.
    /// </remarks>
    public static IReadOnlyList<RuleStep> Plan(
        IReadOnlyList<ServerRule> existing, string instanceName, IReadOnlyList<ReachableSockets> wanted)
    {
        var steps = new List<RuleStep>();
        var left = existing.ToList();
        var uncovered = new List<ReachableSockets>();

        foreach (var socket in wanted.Distinct())
        {
            var name = FirewallInspector.RuleName(instanceName, socket.Ports, socket.Protocol);
            var exact = left.FirstOrDefault(r => r.Protocol == socket.Protocol && r.Ports == socket.Ports);

            if (exact is null)
            {
                uncovered.Add(socket);
                continue;
            }

            left.Remove(exact);
            steps.Add(new RuleStep(exact.Name == name ? RuleChange.Keep : RuleChange.Rename, exact, name, socket));
        }

        foreach (var socket in uncovered)
        {
            var name = FirewallInspector.RuleName(instanceName, socket.Ports, socket.Protocol);
            var movable = left.FirstOrDefault(r => r.Protocol == socket.Protocol && r.Ports is not null);

            if (movable is null)
            {
                steps.Add(new RuleStep(RuleChange.Add, null, name, socket));
                continue;
            }

            left.Remove(movable);
            steps.Add(new RuleStep(RuleChange.Repoint, movable, name, socket));
        }

        steps.AddRange(left.Select(r => new RuleStep(RuleChange.Remove, r, null, null)));
        return steps;
    }

    /// <summary>
    /// The enabled rules named for the server that open a port it no longer uses, from
    /// the machine-wide read <see cref="FirewallInspector.ReadAsync"/> makes.
    /// </summary>
    /// <remarks>
    /// Matched on the name only, since the read carries no description; a rule renamed
    /// with its server matches again. Empty when the read failed: an unread firewall
    /// is the socket rows' to report.
    /// </remarks>
    public static IReadOnlyList<string> Stale(
        FirewallRules rules, string instanceName, IReadOnlyList<ReachableSockets> wanted)
    {
        if (!rules.Read || string.IsNullOrWhiteSpace(rules.Json))
        {
            return [];
        }

        var pattern = new Regex(FirewallInspector.RulePattern(instanceName), RegexOptions.None, TimeSpan.FromSeconds(1));
        var stale = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(rules.Json);
            var root = document.RootElement;

            var container = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Rules", out var listed)
                ? listed
                : root;

            var elements = container.ValueKind == JsonValueKind.Array
                ? container.EnumerateArray().ToList()
                : container.ValueKind == JsonValueKind.Object ? [container] : [];

            foreach (var element in elements)
            {
                if (Text(element, "Name") is not { } name || !pattern.IsMatch(name))
                {
                    continue;
                }

                var protocol = ProtocolOf(Text(element, "Protocol"));
                var ports = SpanOf(element);

                if (protocol is not { } p || ports is not { } s || !wanted.Contains(new ReachableSockets(s, p)))
                {
                    stale.Add(name);
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return stale.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>What an update does, in one sentence, for the button's description and its outcome.</summary>
    public static string Describe(IReadOnlyList<RuleStep> steps)
    {
        var parts = new List<string>();

        foreach (var step in steps)
        {
            var socket = step.Socket is { } s ? $"{FirewallInspector.Name(s.Protocol)} {s.Ports}" : null;

            switch (step.Change)
            {
                case RuleChange.Rename:
                    parts.Add($"renamed \"{step.Rule!.Name}\" to \"{step.NewName}\"");
                    break;
                case RuleChange.Repoint:
                    parts.Add($"moved \"{step.Rule!.Name}\" to {socket}");
                    break;
                case RuleChange.Add:
                    parts.Add($"added \"{step.NewName}\"");
                    break;
                case RuleChange.Remove:
                    parts.Add($"removed \"{step.Rule!.Name}\"");
                    break;
                case RuleChange.Keep:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(steps), step.Change, "Not a rule change.");
            }
        }

        if (parts.Count == 0)
        {
            return "Already up to date.";
        }

        var sentence = string.Join("; ", parts);
        return char.ToUpperInvariant(sentence[0]) + sentence[1..] + ".";
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static PortProtocol? ProtocolOf(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "TCP" => PortProtocol.Tcp,
        "UDP" => PortProtocol.Udp,
        _ => null,
    };

    /// <summary>The rule's <c>LocalPort</c> as one span, or null for any other shape.</summary>
    private static PortSpan? SpanOf(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("Ports", out var ports))
        {
            return null;
        }

        var entries = ports.ValueKind switch
        {
            JsonValueKind.Array => ports.EnumerateArray().ToList(),
            JsonValueKind.String or JsonValueKind.Number => [ports],
            _ => [],
        };

        if (entries.Count != 1)
        {
            return null;
        }

        var text = entries[0].ValueKind == JsonValueKind.Number
            ? entries[0].GetRawText()
            : entries[0].GetString() ?? string.Empty;

        var match = SpanText().Match(text.Trim());

        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var first))
        {
            return null;
        }

        var last = first;

        if (match.Groups[2].Success
            && !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out last))
        {
            return null;
        }

        return first is >= 1 and <= 65535 && last >= first && last <= 65535 ? new PortSpan(first, last) : null;
    }

    [GeneratedRegex(@"^BedrockSentry - .* \(((?:TCP|UDP) [0-9]+(?:-[0-9]+)?)\)$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NameSuffix();

    [GeneratedRegex(@"^([0-9]{1,5})(?:-([0-9]{1,5}))?$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SpanText();

    [GeneratedRegex(@"^[A-Za-z0-9{}._-]{1,128}\z", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlainId();
}
