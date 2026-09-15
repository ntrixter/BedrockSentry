namespace BedrockSentry.Core.Diagnostics;

public enum SystemCheckState
{
    /// <summary>Nothing to do.</summary>
    Good,

    /// <summary>Something is missing, and BedrockSentry can add it.</summary>
    Missing,

    /// <summary>The check itself could not run, so nothing is known either way.</summary>
    Unknown,
}

/// <summary>
/// One thing about the host that either is or is not set up, and — when it is
/// not — exactly what BedrockSentry would do about it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FixDescription"/> is not decoration. These checks change the
/// machine's security posture, so the administrator gets to read the change
/// before authorising it rather than pressing a button labelled "fix" and
/// finding out afterwards. A check with no <see cref="CanFix"/> is a report.
/// </para>
/// <para>
/// <see cref="SystemCheckState.Unknown"/> is deliberately not folded into
/// <see cref="SystemCheckState.Missing"/>. "Defender is not excluding your
/// world" and "we could not ask Defender anything" call for different actions,
/// and reporting the second as the first would have people adding exclusions to
/// solve a problem they do not have.
/// </para>
/// </remarks>
public sealed record SystemCheck(
    string Title,
    SystemCheckState State,
    string Detail,
    bool CanFix = false,
    string? FixDescription = null,
    string? Warning = null);

/// <summary>The outcome of pressing the fix button.</summary>
public sealed record SystemCheckFix(bool Succeeded, string Message);
