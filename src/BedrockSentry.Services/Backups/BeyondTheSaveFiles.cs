using BedrockSentry.Core.Backups;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// Opens the files a backup copies beside the save (<c>D97</c>), for both formats.
/// </summary>
internal static class BeyondTheSaveFiles
{
    /// <summary>
    /// Opens one file beside the save, or records why it could not be and returns
    /// null.
    /// </summary>
    /// <remarks>
    /// <para>Opened the way a file a running server holds is opened, since BDS may be
    /// writing any of them (<c>docs/design.md</c> § 5.5).</para>
    ///
    /// <para>A file that is gone is not recorded: it was deleted after the listing,
    /// and the backup is the folder as it stood when the copy reached it
    /// (<see cref="WorldFolder.LeftOut"/>). One that is there and cannot be opened is
    /// added to <paramref name="unreadable"/>, which the result names.</para>
    /// </remarks>
    public static FileStream? Open(
        string source, string relative, List<string> unreadable, ILogger log, string instanceId)
    {
        try
        {
            return ServerFileReader.Open(source);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(
                ex, "Could not copy {File} into the backup of {InstanceId}; the rest is copied anyway",
                relative, instanceId);

            unreadable.Add(relative);
            return null;
        }
    }
}
