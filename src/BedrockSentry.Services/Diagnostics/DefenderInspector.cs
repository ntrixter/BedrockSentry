using System.Text.Json;
using BedrockSentry.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// Whether Microsoft Defender is excluding the folders a running server writes
/// to constantly, and — on request — an exclusion for one that is not.
/// </summary>
/// <remarks>
/// <para>
/// A real-time scanner walking a live LevelDB costs throughput and can hold a
/// file open at the moment BDS wants to replace it, which shows up as a stalled
/// save rather than as anything mentioning antivirus.
/// </para>
/// <para>
/// This is also the one thing in the wrapper that makes a machine <em>less</em>
/// protected, so it is scoped as narrowly as the problem allows: the instance's
/// own folder, never a drive, never a file type. The offer says what it is
/// giving up in the same breath as what it buys, and it is never applied
/// without somebody pressing the button.
/// </para>
/// </remarks>
public sealed class DefenderInspector
{
    private readonly IPowerShellRunner _powerShell;
    private readonly ILogger<DefenderInspector> _log;

    public DefenderInspector(IPowerShellRunner powerShell, ILogger<DefenderInspector> log)
    {
        _powerShell = powerShell;
        _log = log;
    }

    /// <summary>
    /// Defender's configured path exclusions, or why they could not be read.
    /// </summary>
    public sealed record Exclusions(bool Read, string Json, string Error);

    /// <summary>Reads Defender's exclusion list, once.</summary>
    /// <remarks>
    /// Separate from <see cref="Check"/> for the same reason the firewall read
    /// is: the exclusion list belongs to the machine, not to an instance, and
    /// asking once per server meant paying for an identical answer every time.
    /// </remarks>
    public async Task<Exclusions> ReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await _powerShell.RunAsync(
                """
                $ErrorActionPreference = 'Stop'
                try {
                  @((Get-MpPreference).ExclusionPath) | ConvertTo-Json -Compress
                } catch { Write-Error $_.Exception.Message }
                """,
                cancellationToken)
            .ConfigureAwait(false);

        return new Exclusions(result.Succeeded, result.Output, result.Reason);
    }

    /// <summary>The row's name, needed before its answer arrives.</summary>
    public static string CheckTitle(string label) => $"Defender exclusion — {label}";

    public static SystemCheck Check(Exclusions exclusions, string label, string directory)
    {
        var title = CheckTitle(label);

        if (!exclusions.Read)
        {
            // Defender may be absent, replaced by another product, or managed by
            // policy. None of those is "your world is being scanned", and saying
            // so would send somebody adding an exclusion they do not need.
            return new SystemCheck(
                title, SystemCheckState.Unknown,
                $"Could not ask Defender. {exclusions.Error}");
        }

        var covering = FindExclusion(exclusions.Json, directory);

        return covering is not null
            ? new SystemCheck(title, SystemCheckState.Good, $"Covered by {covering}.")
            : new SystemCheck(
                title,
                SystemCheckState.Missing,
                $"{directory} is not excluded. Defender scans the world as the server writes "
                + "it, which costs throughput and can hold a file open at the moment a save "
                + "wants to replace it — visible as a slow or stalled save, never as anything "
                + "mentioning antivirus.",
                CanFix: true,
                FixDescription: $"Adds a path exclusion for {directory} and nothing else.",
                Warning:
                    "An exclusion means Defender stops scanning that folder entirely, including "
                    + "anything that later ends up in it — an add-on pack, a restored backup, a "
                    + "file somebody copies in. That is a real reduction in protection, and it "
                    + "is why this is a folder rather than a drive and a button rather than a "
                    + "default.");
    }

    /// <summary>
    /// The configured exclusion covering <paramref name="directory"/>, or null.
    /// </summary>
    /// <remarks>
    /// Public and static to be testable against captured output. A parent
    /// exclusion counts: somebody who excluded the whole instance root has
    /// already covered every instance under it, and offering to add each one
    /// again would be noise.
    /// </remarks>
    public static string? FindExclusion(string json, string directory)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        string wanted;
        try
        {
            wanted = Normalise(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            var entries = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : [document.RootElement];

            foreach (var entry in entries)
            {
                if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { } value)
                {
                    continue;
                }

                var excluded = Normalise(value);

                if (wanted.Equals(excluded, StringComparison.OrdinalIgnoreCase)
                    || wanted.StartsWith(excluded + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            // Unreadable means unknown, and unknown reads as "not excluded",
            // which at worst offers an exclusion that is already there.
        }

        return null;
    }

    private static string Normalise(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    public async Task<SystemCheckFix> ExcludeAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        var quoted = directory.Replace("'", "''");

        var result = await _powerShell.RunAsync(
                $$"""
                $ErrorActionPreference = 'Stop'
                try {
                  Add-MpPreference -ExclusionPath '{{quoted}}'
                  'ok'
                } catch { Write-Error $_.Exception.Message }
                """,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            // Warning rather than Information: this reduced the machine's
            // protection, and the record of that should not need a raised log
            // level to find later.
            _log.LogWarning("Added a Defender exclusion for {Directory}", directory);
            return new SystemCheckFix(true, $"Excluded {directory}.");
        }

        return new SystemCheckFix(false, result.Reason is { Length: > 0 } error
            ? error
            : "The exclusion could not be added.");
    }
}
