using System.Text.Json;
using System.Text.Json.Serialization;

namespace BedrockSentry.Core.Backups;

/// <summary>
/// Which server a backup was taken from (<c>D89</c>), written beside it as
/// <see cref="NewServerWorld.OriginFile"/>.
/// </summary>
/// <param name="InstanceId">The instance's id, which is what decides "the same server".</param>
/// <param name="Name">Its display name when the backup was taken, for the sentence only.</param>
/// <remarks>
/// <para><strong>A claim made at backup time, and optional for ever.</strong> Every backup
/// taken before this, and every one made by hand, carries none; a backup without one is as
/// restorable as it was, and nothing refuses for lack of it.</para>
///
/// <para>The id, not the name, says whether a restore is going back where it came from: a
/// name can be changed, and two servers can share one.</para>
/// </remarks>
public sealed record BackupOrigin(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("name")] string Name)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The file's text.</summary>
    public string Write() => JsonSerializer.Serialize(this, Indented) + "\n";

    /// <summary>The origin a file names, or null when it is not one this wrote.</summary>
    /// <remarks>
    /// Null rather than a throw: a damaged or hand-edited file says nothing about the world
    /// beside it, so it is read as a backup that records no origin.
    /// </remarks>
    public static BackupOrigin? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BackupOrigin>(text) is { InstanceId: { Length: > 0 } } origin
                ? origin with { Name = string.IsNullOrWhiteSpace(origin.Name) ? origin.InstanceId : origin.Name }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the restore dialog says when a backup came from another server, or null when it
    /// came from this one or records nothing.
    /// </summary>
    /// <param name="origin">What the backup records.</param>
    /// <param name="instanceId">The server it is about to be restored into.</param>
    /// <param name="serverName">That server's display name.</param>
    /// <remarks>
    /// A warning, never a refusal: putting one server's world into another is a thing an
    /// administrator may well mean, and a backup moved between folders by hand is the case
    /// this exists to name.
    /// </remarks>
    public static string? Warning(BackupOrigin? origin, string instanceId, string serverName)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(serverName);

        if (origin is null || string.Equals(origin.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var from = string.Equals(origin.Name, serverName, StringComparison.Ordinal)
            ? $"another server also called {origin.Name}"
            : origin.Name;

        return $"This backup was taken from {from}, not {serverName}. Restoring it puts that server's world here.";
    }
}
