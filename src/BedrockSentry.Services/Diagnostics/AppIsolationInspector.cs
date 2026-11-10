using System.Text.Json;
using BedrockSentry.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>One Minecraft package installed on this machine.</summary>
/// <param name="Family">
/// The package family name, which is what a loopback exemption is keyed on.
/// </param>
public sealed record MinecraftPackage(string Family, bool Exempt);

/// <summary>
/// Whether the Minecraft client installed on this machine is allowed to reach a
/// server running on it — and, on request, an exemption that lets it.
/// </summary>
/// <remarks>
/// <para>
/// The third thing that makes a working server look broken, after the firewall
/// rule and the Defender exclusion, and by far the hardest to find. The Minecraft
/// client is a packaged app, and Windows blocks packaged apps from connecting to
/// anything on their own machine. That is AppContainer network isolation, not the
/// firewall — so the usual diagnosis of turning the firewall off changes nothing,
/// which is precisely the evidence that sends somebody looking somewhere else.
/// </para>
/// <para>
/// Only relevant when Minecraft is installed on the same machine as the server —
/// a dedicated box has no client to isolate, and a row about it there would be
/// noise. Nothing is reported at all in that case, rather than a Good the
/// administrator has to think about.
/// </para>
/// </remarks>
public sealed class AppIsolationInspector : ILoopbackExemptionService
{
    private readonly IPowerShellRunner _powerShell;
    private readonly ILogger<AppIsolationInspector> _log;

    public AppIsolationInspector(IPowerShellRunner powerShell, ILogger<AppIsolationInspector> log)
    {
        _powerShell = powerShell;
        _log = log;
    }

    /// <summary>Reads the installed Minecraft packages and the exemption list, once.</summary>
    /// <remarks>
    /// The exemption list is matched by looking for the package family name
    /// anywhere in the output rather than by parsing the entries. The command
    /// prints a labelled block per exemption and those labels are localised;
    /// the family name inside them is not.
    /// </remarks>
    public async Task<LoopbackExemptions> ReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await _powerShell.RunAsync(
                """
                $ErrorActionPreference = 'Stop'
                try {
                  # A machine without the Appx cmdlets has no packaged apps, so no
                  # Minecraft, which is an answer. Any other error, access denied
                  # above all, is a failure to read: -AllUsers needs an
                  # administrator, and reading it as "none installed" hid a
                  # Minecraft that was there (T395).
                  $packages = @()
                  try {
                    $packages = @(Get-AppxPackage -AllUsers -Name 'Microsoft.Minecraft*' -ErrorAction Stop |
                      ForEach-Object { $_.PackageFamilyName } | Sort-Object -Unique)
                  } catch [System.Management.Automation.CommandNotFoundException] { $packages = @() }

                  $exempt = ''
                  if ($packages.Count -gt 0) {
                    # A native command writing to stderr would otherwise be read as
                    # the whole script having failed.
                    $ErrorActionPreference = 'SilentlyContinue'
                    $exempt = (CheckNetIsolation LoopbackExempt -s 2>$null | Out-String)
                    $ErrorActionPreference = 'Stop'
                  }

                  # An account that does not have the package registered (LocalSystem,
                  # in the broker) sees an exemption by its AppContainer SID only, with
                  # "AppContainer NOT FOUND" for its name (T395), so the SIDs go too.
                  $listed = @([regex]::Matches($exempt, 'S-1-15-2(-\d+)+') | ForEach-Object { $_.Value })

                  # -InputObject keeps the list an array of any length. Piped, Windows
                  # PowerShell 5.1 writes a comma-wrapped array as {"value":[...]}
                  # (T398).
                  ConvertTo-Json -Compress -InputObject @($packages | ForEach-Object {
                    [pscustomobject]@{ Package = $_; Exempt = [bool]($exempt -match [regex]::Escape($_)); Listed = $listed }
                  })
                } catch { Write-Error $_.Exception.Message }
                """,
                cancellationToken)
            .ConfigureAwait(false);

        return new LoopbackExemptions(result.Succeeded, result.Output, result.Reason);
    }

    /// <summary>
    /// The Minecraft packages the read found. Empty means none is installed here.
    /// </summary>
    /// <remarks>
    /// Public and static so it can be tested against captured output, which is
    /// where this can actually be wrong.
    /// </remarks>
    public static IReadOnlyList<MinecraftPackage> Packages(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            var root = document.RootElement;

            // Windows PowerShell 5.1's rendering of a piped, comma-wrapped array, which
            // every read before T398's fix produced.
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("value", out var wrapped)
                && wrapped.ValueKind == JsonValueKind.Array)
            {
                root = wrapped;
            }

            var entries = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().ToList()
                : [root];

            var packages = new List<MinecraftPackage>();

            foreach (var entry in entries)
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("Package", out var family)
                    || family.ValueKind != JsonValueKind.String
                    || family.GetString() is not { Length: > 0 } name)
                {
                    continue;
                }

                var exempt = (entry.TryGetProperty("Exempt", out var value)
                              && value.ValueKind == JsonValueKind.True)
                             || Listed(entry).Contains(AppContainerSid(name), StringComparer.OrdinalIgnoreCase);

                packages.Add(new MinecraftPackage(name, exempt));
            }

            return packages;
        }
        catch (JsonException)
        {
            // Unreadable output is not evidence that Minecraft is installed, and
            // claiming a package that is not there would offer an exemption for
            // something that cannot use it.
            return [];
        }
    }

    /// <summary>The AppContainer SIDs the exemption list named, as the probe found them.</summary>
    private static IEnumerable<string> Listed(JsonElement entry)
    {
        if (!entry.TryGetProperty("Listed", out var listed))
        {
            return [];
        }

        // One SID may arrive bare rather than as a list.
        List<JsonElement> values = listed.ValueKind switch
        {
            JsonValueKind.Array => listed.EnumerateArray().ToList(),
            JsonValueKind.String => [listed],
            _ => [],
        };

        return values.Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!);
    }

    /// <summary>
    /// The AppContainer SID Windows gives a package family: <c>S-1-15-2-</c> and the first
    /// seven little-endian words of the SHA-256 of the lower-cased name in UTF-16.
    /// </summary>
    /// <remarks>
    /// What <c>DeriveAppContainerSidFromAppContainerName</c> returns, worked out here so the
    /// rule can be tested anywhere; Minecraft's is the SID loopback guides quote for it.
    /// </remarks>
    public static string AppContainerSid(string family)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.Unicode.GetBytes(family.ToLowerInvariant()));

        var words = Enumerable.Range(0, 7).Select(i => BitConverter.ToUInt32(hash, i * 4));
        return "S-1-15-2-" + string.Join('-', words);
    }

    /// <summary>The row's name, needed before its answer arrives.</summary>
    public static string CheckTitle(string family) => $"Local Minecraft — {Friendly(family)}";

    /// <summary>
    /// The name somebody would recognise, rather than the package identifier.
    /// </summary>
    public static string Friendly(string family) => family switch
    {
        var f when f.StartsWith("Microsoft.MinecraftUWP_", StringComparison.OrdinalIgnoreCase) =>
            "Minecraft",
        var f when f.StartsWith("Microsoft.MinecraftWindowsBeta_", StringComparison.OrdinalIgnoreCase) =>
            "Minecraft Preview",
        _ => family,
    };

    /// <summary>
    /// Whether the client on this machine can reach a server on this machine.
    /// </summary>
    public static SystemCheck Check(MinecraftPackage package)
    {
        var title = CheckTitle(package.Family);

        return package.Exempt
            ? new SystemCheck(
                title, SystemCheckState.Good,
                "This machine's Minecraft client is allowed to connect to a server running here.")
            : new SystemCheck(
                title,
                SystemCheckState.Missing,
                "Minecraft is installed here but is not allowed to connect to a server on this "
                + "same machine. Windows isolates packaged apps from their own host, which is "
                + "separate from the firewall — so this fails identically to a blocked port, and "
                + "turning the firewall off does not change it. Players elsewhere are unaffected.",
                CanFix: true,
                FixDescription:
                    $"Grants {Friendly(package.Family)} a loopback exemption, and nothing else. "
                    + "Connect to 127.0.0.1 afterwards rather than this machine's network address.",
                Warning:
                    "An exemption lets that app reach any service running on this machine, not "
                    + "only Bedrock. It is a small hole rather than the firewall one it resembles, "
                    + "and Windows may drop it again when the app updates.");
    }

    /// <summary>The row shown when the check itself could not run.</summary>
    /// <param name="unavailable">Why the read needs the broker, when this install has none.</param>
    public static SystemCheck Unavailable(string error, string? unavailable = null) =>
        new("Local Minecraft", SystemCheckState.Unknown,
            FirewallInspector.Unread($"Could not tell whether Minecraft is installed here. {error}", unavailable));

    /// <summary>Grants the loopback exemption described by <see cref="Check"/>.</summary>
    public async Task<SystemCheckFix> ExemptAsync(
        string family, CancellationToken cancellationToken = default)
    {
        // Machine-derived rather than typed, but quoted the same way as everything
        // else that reaches a script — the rule is worth more than the exception.
        var quoted = family.Replace("'", "''");

        var result = await _powerShell.RunAsync(
                $$"""
                $ErrorActionPreference = 'Stop'
                try {
                  # -a adds to the list; it does not replace it, so an exemption
                  # somebody else added survives this.
                  $output = (CheckNetIsolation LoopbackExempt -a -n='{{quoted}}' 2>$null | Out-String)
                  if ($LASTEXITCODE -ne 0) { throw "CheckNetIsolation exited with $LASTEXITCODE." }
                  'ok'
                } catch { Write-Error $_.Exception.Message }
                """,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            _log.LogWarning("Granted a loopback exemption to {Package}", family);

            return new SystemCheckFix(
                true,
                $"{Friendly(family)} may now connect to this machine. Use 127.0.0.1 rather than "
                + "this machine's network address.");
        }

        return new SystemCheckFix(false, result.Reason is { Length: > 0 } error
            ? error
            : "The exemption could not be granted.");
    }
}
