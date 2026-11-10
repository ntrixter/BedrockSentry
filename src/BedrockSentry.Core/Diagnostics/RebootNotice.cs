namespace BedrockSentry.Core.Diagnostics;

/// <summary>
/// What the Status page reads to warn of a restart, and how the warning reads
/// (<c>T188</c>, <c>D64</c>).
/// </summary>
/// <remarks>
/// <para>Each signal is something Windows has pending that can restart the machine by
/// itself: a component install, or Windows Update, waiting on a restart. The page warns
/// when at least one is set.</para>
///
/// <para><strong>The file-rename queue is not read</strong> (<c>D64</c>, answered by the
/// operator 2026-09-25). <c>PendingFileRenameOperations</c> is work Windows does if a
/// restart happens; it cannot cause one, and anything that updates itself while its files
/// are in use leaves entries there. Read as a third signal it kept a notice on the page
/// from 2026-09-14 to 2026-09-25 on a machine with nothing pending, holding 12 entries at
/// the first reading and 60 at the last, while the two signals here read false every
/// time.</para>
/// </remarks>
public static class RebootNotice
{
    /// <summary>
    /// The registry keys under <c>HKEY_LOCAL_MACHINE</c> whose presence means Windows is
    /// waiting to restart, each with the reason the page gives for it.
    /// </summary>
    /// <remarks>
    /// The second is the flag Windows Update sets when an installed update needs a
    /// restart. Windows Update can also be asked directly, from PowerShell with
    /// <c>(New-Object -ComObject Microsoft.Update.SystemInfo).RebootRequired</c>;
    /// <c>T188</c> clause 2 compares the two.
    /// </remarks>
    public static readonly IReadOnlyList<(string Key, string Reason)> Signals =
    [
        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
            "a component install is waiting on a restart"),
        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
            "Windows Update is waiting on a restart"),
    ];

    /// <summary>The reasons that apply, in the order <see cref="Signals"/> lists them.</summary>
    /// <param name="keyExists">Whether a key under <c>HKEY_LOCAL_MACHINE</c> exists.</param>
    public static IReadOnlyList<string> Reasons(Func<string, bool> keyExists) => Read(keyExists).Reasons;

    /// <summary>
    /// The reasons that apply, and the first signal that could not be read, said as a
    /// sentence, or null when every one was.
    /// </summary>
    /// <remarks>
    /// Three answers, not two: a key this account may not open is neither pending nor
    /// clear, and reading it as "not pending" would say nothing is waiting on a machine
    /// that may restart tonight (<c>T176</c>).
    /// </remarks>
    /// <param name="keyExists">Whether a key under <c>HKEY_LOCAL_MACHINE</c> exists.</param>
    public static (IReadOnlyList<string> Reasons, string? Unreadable) Read(Func<string, bool> keyExists)
    {
        ArgumentNullException.ThrowIfNull(keyExists);

        List<string> reasons = [];
        string? unreadable = null;

        foreach (var (key, reason) in Signals)
        {
            try
            {
                if (keyExists(key))
                {
                    reasons.Add(reason);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                unreadable ??= $"Could not tell whether {reason}: {ex.Message}";
            }
        }

        return (reasons, unreadable);
    }

    /// <summary>The headline, or null when nothing is pending.</summary>
    public static string? Headline(IReadOnlyList<string> reasons) =>
        reasons is { Count: > 0 } ? "Windows is waiting to restart." : null;

    /// <summary>The sentence between the headline and the reasons, or null.</summary>
    public static string? Consequence(IReadOnlyList<string> reasons) =>
        reasons is { Count: > 0 } ? "It will take every server down at a moment of its choosing —" : null;
}
