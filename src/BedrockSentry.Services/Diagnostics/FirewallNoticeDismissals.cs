using System.Text.Json;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// The out-of-date firewall notices an administrator dismissed, per server (<c>T414</c>).
/// </summary>
/// <remarks>
/// <para>A dismissal names what it was shown: the stale rules and the sockets the server
/// used then (<see cref="Fingerprint"/>). A later port change or a new stale rule is a
/// different notice and shows again; the one dismissed stays away, across restarts.</para>
/// <para>Kept in one file under the data folder. A file that cannot be read is no
/// dismissals, so the notice shows rather than being hidden by a fault.</para>
/// </remarks>
public sealed class FirewallNoticeDismissals(ISentryPaths paths, ILogger<FirewallNoticeDismissals> log)
{
    private readonly object _gate = new();
    private Dictionary<string, string>? _dismissed;

    private string FilePath => Path.Combine(paths.Data, "firewall-notices.json");

    /// <summary>What a notice says, as one string that changes whenever the notice would.</summary>
    public static string Fingerprint(IReadOnlyList<string> stale, IReadOnlyList<ReachableSockets> sockets) =>
        string.Join("|", stale.Order(StringComparer.Ordinal))
        + "=>"
        + string.Join("|", sockets.Select(s => $"{FirewallInspector.Name(s.Protocol)} {s.Ports}").Order(StringComparer.Ordinal));

    public bool IsDismissed(string instanceId, string fingerprint)
    {
        lock (_gate)
        {
            return Load().TryGetValue(instanceId, out var dismissed) && dismissed == fingerprint;
        }
    }

    public void Dismiss(string instanceId, string fingerprint)
    {
        lock (_gate)
        {
            var dismissed = Load();
            dismissed[instanceId] = fingerprint;

            try
            {
                Directory.CreateDirectory(paths.Data);
                var temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(dismissed));
                File.Move(temporary, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Dismissed for as long as the service runs; the next start shows it again.
                log.LogWarning(ex, "Could not record a dismissed firewall notice for {InstanceId}", instanceId);
            }
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_dismissed is not null)
        {
            return _dismissed;
        }

        try
        {
            _dismissed = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.LogWarning(ex, "Could not read the dismissed firewall notices; showing them all");
            _dismissed = [];
        }

        return _dismissed;
    }
}
