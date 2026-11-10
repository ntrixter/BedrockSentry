namespace BedrockSentry.Core.Packages;

/// <summary>How the package log came to know a package's channel.</summary>
public enum PackageChannelSource
{
    /// <summary>The wrapper downloaded it from Mojang's offer on that channel.</summary>
    Downloaded,

    /// <summary>It was already in the package store when Mojang offered it on that channel.</summary>
    Offered,
}

/// <summary>One line of the package log: a zip's channel and how it was learned.</summary>
public sealed record PackageLogEntry(
    string FileName,
    BdsChannel Channel,
    DateTimeOffset RecordedUtc,
    PackageChannelSource Source);

/// <summary>Which channel each zip in the package store came from (<c>T333</c>).</summary>
/// <remarks>
/// Mojang names a Preview zip the way it names a Release one,
/// <c>bedrock-server-1.26.60.28.zip</c>, so nothing in the store can tell them apart. The
/// feed can: each link says which channel it is for. So the channel is written down when
/// a zip is downloaded from an offer, or when an offer names a zip already in the store.
/// A zip nothing has recorded has no channel, and a page shows it untagged rather than
/// guessing.
/// </remarks>
public static class PackageChannels
{
    /// <summary>The channel last recorded for <paramref name="fileName"/>, or null.</summary>
    /// <remarks>
    /// The log is appended to, never rewritten, so a name can appear more than once; the
    /// latest line is the answer. Names compare as the store's folder does, ignoring case.
    /// </remarks>
    public static BdsChannel? For(string fileName, IReadOnlyList<PackageLogEntry> log)
    {
        for (var i = log.Count - 1; i >= 0; i--)
        {
            if (string.Equals(log[i].FileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return log[i].Channel;
            }
        }

        return null;
    }
}
