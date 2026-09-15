using System.Text.Json;
using BedrockSentry.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// Whether Windows Firewall lets players reach a server's port, and — on
/// request — a rule that does.
/// </summary>
/// <remarks>
/// "Nobody outside can connect" is the most common problem with a self-hosted
/// game server, and an inbound firewall rule is the most common cause. It is
/// also the one an administrator is least likely to check, because the server
/// itself reports no error at all: it binds the port, says it started, and waits
/// for packets that never arrive.
/// </remarks>
public sealed class FirewallInspector
{
    private readonly IPowerShellRunner _powerShell;
    private readonly ILogger<FirewallInspector> _log;

    public FirewallInspector(IPowerShellRunner powerShell, ILogger<FirewallInspector> log)
    {
        _powerShell = powerShell;
        _log = log;
    }

    /// <summary>The rule BedrockSentry would create for a port.</summary>
    /// <remarks>
    /// Named after the instance and the port so it is obvious in the firewall
    /// UI what created it and why — and so somebody who wants it gone can find
    /// and delete it without knowing anything about this wrapper.
    /// </remarks>
    public static string RuleName(string instanceName, int port) =>
        $"BedrockSentry - {instanceName} (UDP {port})";

    /// <summary>
    /// Every enabled inbound UDP allow rule on the machine, or why they could
    /// not be read.
    /// </summary>
    public sealed record Rules(bool Read, string Json, string Error);

    /// <summary>Reads the machine's inbound UDP rules, once.</summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="Check"/> because the answer is a property of the
    /// machine rather than of any one instance. Asking per instance meant running
    /// an identical whole-firewall dump once for every server on the box and
    /// throwing away all but one port's worth of it.
    /// </para>
    /// <para>
    /// Two bulk queries joined in memory, rather than the obvious pipeline. The
    /// rule-to-filter relationship is one-to-one and keyed by <c>InstanceID</c>,
    /// so the join is exact — but asking for each rule's filter individually is
    /// a separate CIM round trip per rule, and on a stock Windows Server that is
    /// several hundred of them. Measured at 56 seconds on a real box, against
    /// about two for this.
    /// </para>
    /// <para>
    /// <c>ActiveStore</c> is the effective policy, which is the only one that
    /// answers the question actually being asked. The default store omits rules
    /// applied by group policy, so a domain-joined machine whose port was opened
    /// centrally would be reported as closed.
    /// </para>
    /// </remarks>
    public async Task<Rules> ReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await _powerShell.RunAsync(
                """
                $ErrorActionPreference = 'Stop'
                try {
                  $allowed = @{}
                  Get-NetFirewallRule -PolicyStore ActiveStore -Direction Inbound -Action Allow -Enabled True |
                    ForEach-Object { $allowed[$_.InstanceID] = $_.DisplayName }

                  Get-NetFirewallPortFilter -PolicyStore ActiveStore |
                    Where-Object { $_.Protocol -eq 'UDP' -and $allowed.ContainsKey($_.InstanceID) } |
                    ForEach-Object {
                      [pscustomobject]@{ Name = $allowed[$_.InstanceID]; Ports = @($_.LocalPort) }
                    } | ConvertTo-Json -Depth 4 -Compress
                } catch { Write-Error $_.Exception.Message }
                """,
                cancellationToken)
            .ConfigureAwait(false);

        return new Rules(result.Succeeded, result.Output, result.Reason);
    }

    /// <summary>
    /// What the row is called, which the page needs before there is an answer to
    /// put in it.
    /// </summary>
    public static string CheckTitle(string instanceName, int port) =>
        $"Firewall — {instanceName}, UDP {port}";

    /// <summary>Whether an enabled inbound allow rule exists for a UDP port.</summary>
    /// <remarks>
    /// Any rule counts, not only one of ours. Somebody who opened the port by
    /// hand years ago has a working server, and telling them it is broken —
    /// then offering to add a second, redundant rule — would be wrong twice.
    /// </remarks>
    public static SystemCheck Check(Rules rules, string instanceName, int port)
    {
        var title = CheckTitle(instanceName, port);

        if (!rules.Read)
        {
            return new SystemCheck(
                title, SystemCheckState.Unknown,
                $"Could not read the firewall rules. {rules.Error}");
        }

        var match = FindRule(rules.Json, port);

        return match is not null
            ? new SystemCheck(title, SystemCheckState.Good, $"Allowed by \"{match}\".")
            : new SystemCheck(
                title,
                SystemCheckState.Missing,
                $"Nothing lets UDP {port} in. Players on this network can connect; players "
                + "outside it cannot, and the server reports no error either way.",
                CanFix: true,
                FixDescription:
                    $"Adds one inbound rule named \"{RuleName(instanceName, port)}\" allowing "
                    + $"UDP {port} on all profiles. Nothing else is opened, and deleting the "
                    + "rule in Windows Defender Firewall undoes it.");
    }

    /// <summary>
    /// The name of the first rule covering <paramref name="port"/>, or null.
    /// </summary>
    /// <remarks>
    /// Public and static so it can be tested against captured output. This is
    /// where the interesting failures are: a single rule serialises as an object
    /// rather than an array, ports arrive as strings, and a range like
    /// <c>19130-19140</c> covers 19132 without ever mentioning it.
    /// </remarks>
    public static string? FindRule(string json, int port)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            // ConvertTo-Json emits a bare object when there is exactly one
            // result and an array when there is more than one. Reading only the
            // array shape misses the single-rule case entirely — which is the
            // shape a machine set up by this very fix ends up in.
            var rules = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                : SingleElement(document.RootElement);

            foreach (var rule in rules)
            {
                if (rule.ValueKind != JsonValueKind.Object
                    || !rule.TryGetProperty("Ports", out var ports))
                {
                    continue;
                }

                if (Covers(ports, port))
                {
                    return rule.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String
                        ? name.GetString()!
                        : "an existing rule";
                }
            }
        }
        catch (JsonException)
        {
            // Output that is not JSON means the check did not really succeed;
            // "no rule found" is the safe reading, and the worst it costs is an
            // offer to add a rule that is already there.
        }

        return null;
    }

    private static IEnumerable<JsonElement> SingleElement(JsonElement element)
    {
        yield return element;
    }

    private static bool Covers(JsonElement ports, int port)
    {
        var values = ports.ValueKind == JsonValueKind.Array
            ? ports.EnumerateArray()
            : SingleElement(ports);

        foreach (var value in values)
        {
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };

            if (text is not null && CoversPort(text, port))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether one <c>LocalPort</c> entry covers a port. Entries are a number,
    /// a <c>from-to</c> range, or the word <c>Any</c>.
    /// </summary>
    public static bool CoversPort(string entry, int port)
    {
        var text = entry.Trim();

        if (text.Equals("Any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dash = text.IndexOf('-');
        if (dash > 0)
        {
            return int.TryParse(text[..dash], out var from)
                   && int.TryParse(text[(dash + 1)..], out var to)
                   && port >= from
                   && port <= to;
        }

        return int.TryParse(text, out var single) && single == port;
    }

    /// <summary>Adds the rule described by <see cref="CheckAsync"/>.</summary>
    public async Task<SystemCheckFix> AllowAsync(
        string instanceName, int port, CancellationToken cancellationToken = default)
    {
        var name = RuleName(instanceName, port);

        // Single-quoted and doubled, so a display name containing an apostrophe
        // cannot end the string and turn the rest into something else to run.
        var quoted = name.Replace("'", "''");

        var result = await _powerShell.RunAsync(
                $$"""
                $ErrorActionPreference = 'Stop'
                try {
                  New-NetFirewallRule -DisplayName '{{quoted}}' -Direction Inbound -Action Allow `
                    -Protocol UDP -LocalPort {{port}} -Profile Any | Out-Null
                  'ok'
                } catch { Write-Error $_.Exception.Message }
                """,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            _log.LogWarning("Added firewall rule {Rule} for UDP {Port}", name, port);
            return new SystemCheckFix(true, $"Added \"{name}\".");
        }

        return new SystemCheckFix(false, result.Reason is { Length: > 0 } error
            ? error
            : "The rule could not be added.");
    }
}
