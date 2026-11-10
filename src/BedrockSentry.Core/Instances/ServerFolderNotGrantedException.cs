namespace BedrockSentry.Core.Instances;

/// <summary>
/// A server's folder could not be given to the token it runs under, or lacks its low label, so it
/// was not started; a relaunch will not change that.
/// </summary>
/// <remarks>
/// Its own type rather than a <see cref="ServerNotLaunchableException"/>: that one
/// carries what the disk says about the executable, and here the executable is there
/// and readable. Routed by this type, so the supervisor records the sentence as a
/// refusal rather than counting a crash (<c>T250</c>).
/// </remarks>
public sealed class ServerFolderNotGrantedException(string workingDirectory, string reason, Exception? innerException = null)
    : Exception(Sentence(workingDirectory, reason), innerException)
{
    /// <summary>What the supervisor records.</summary>
    public static string Sentence(string workingDirectory, string reason) =>
        $"not started: a server runs under a restricted account that may change only its own folder, "
        + $"and {workingDirectory} could not be given to it ({reason}). Nothing has been changed. An "
        + $"administrator gives it with install.ps1 -GrantFolder \"{workingDirectory}\", then start it.";
}
