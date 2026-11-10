namespace BedrockSentry.Core.Instances;

/// <summary>
/// A server is claimed by a job, so a start, a stop or another job was refused
/// before it did anything.
/// </summary>
/// <remarks>
/// An <see cref="InvalidOperationException"/>, so a caller that shows a refusal's
/// message shows this one. A caller that waits instead, such as a scheduled restart
/// or an automatic update, routes by this type and reads <see cref="Purpose"/>.
/// </remarks>
public sealed class InstanceClaimedException : InvalidOperationException
{
    public InstanceClaimedException(string server, string purpose)
        : base(ClaimText.Refusal(server, purpose))
    {
        Server = server;
        Purpose = purpose;
    }

    /// <summary>The server's name.</summary>
    public string Server { get; }

    /// <summary>The job it is busy with, as <see cref="ClaimText"/> names it.</summary>
    public string Purpose { get; }
}
