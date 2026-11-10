namespace BedrockSentry.Core.Diagnostics;

/// <summary>How long ago a server's last backup was, as the Home page and the Status tab write it.</summary>
public static class LastBackupText
{
    /// <param name="lastUtc">The last successful backup, or null when there has been none.</param>
    /// <param name="historyRead">
    /// False when the backup history could not be read, which says nothing about whether a
    /// backup was taken and so is not "never" (<c>T176</c>).
    /// </param>
    public static string Since(DateTimeOffset? lastUtc, DateTimeOffset nowUtc, bool historyRead = true) =>
        !historyRead ? "not known (history unreadable)"
        : lastUtc is { } at ? $"{UptimeFormatter.Format(nowUtc - at)} ago"
        : "never";
}
