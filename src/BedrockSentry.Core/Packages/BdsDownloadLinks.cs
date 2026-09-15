using System.Text.Json;

namespace BedrockSentry.Core.Packages;

/// <summary>Which line of Bedrock builds an instance follows.</summary>
public enum BdsChannel
{
    /// <summary>The shipping game. The default, and what almost everyone wants.</summary>
    Release,

    /// <summary>
    /// Mojang's preview builds. Ahead of release and expected to break; clients
    /// have to be running Preview too, so a preview server is unjoinable from
    /// the retail game.
    /// </summary>
    Preview,
}

/// <summary>One download Mojang is currently offering.</summary>
/// <param name="Version">
/// Parsed out of the file name, because that is the only place a version
/// appears — the feed itself carries none. Null when the name does not follow
/// Mojang's convention, which makes the entry unusable for comparison and is
/// treated as "no update known" rather than as an error.
/// </param>
public sealed record BdsDownload(BdsChannel Channel, string Url, string FileName, Version? Version);

/// <summary>
/// Reads Mojang's download-links feed (<c>docs/design.md</c> § 11).
/// </summary>
/// <remarks>
/// Measured against a real response rather than written from documentation,
/// which in this codebase is not a style preference — a chat recogniser and a
/// <c>D21</c> resolution were both built on assumed formats and both turned out
/// to be wrong. The response is an envelope, not a bare array:
///
/// <code>
/// {"result":{"links":[
///   {"downloadType":"serverBedrockWindows","downloadUrl":".../bedrock-server-1.26.44.3.zip"},
///   ...
/// ]}}
/// </code>
///
/// Three things follow from what was measured:
///
/// <list type="bullet">
/// <item><description>
/// <b>There is no version field.</b> The only statement of which build a link
/// points at is the file name, so the version comes from
/// <see cref="ServerPackageName"/> — the same parser the package store uses, so
/// a downloaded file and a hand-placed one are read identically.
/// </description></item>
/// <item><description>
/// <b>The feed carries Java Edition too.</b> <c>serverJar</c> is a
/// <c>piston-data.mojang.com</c> URL for an entirely different product;
/// anything not recognised as a Bedrock Windows build is ignored rather than
/// offered.
/// </description></item>
/// <item><description>
/// <b>Linux builds are listed and are not ours.</b> The wrapper is a Windows
/// service managing <c>bedrock_server.exe</c>, so only the two Windows types
/// are candidates.
/// </description></item>
/// </list>
/// </remarks>
public static class BdsDownloadLinks
{
    /// <summary>The download types this wrapper can actually install.</summary>
    private static readonly (string Type, BdsChannel Channel)[] Windows =
    [
        ("serverBedrockWindows", BdsChannel.Release),
        ("serverBedrockPreviewWindows", BdsChannel.Preview),
    ];

    /// <summary>
    /// Reads what Mojang is offering. Never throws on a shape it does not
    /// recognise — an entry it cannot read is skipped, and a response it cannot
    /// read at all yields nothing, which the caller reports as "no update
    /// known". Refusing to start because a third party changed a field would be
    /// a worse failure than not knowing about an update.
    /// </summary>
    public static IReadOnlyList<BdsDownload> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            // Every ValueKind is checked before TryGetProperty rather than after,
            // because TryGetProperty *throws* on anything that is not an object
            // — it is "try" about the property, not about the kind. A feed that
            // came back as a bare array would otherwise escape the catch below
            // as an InvalidOperationException.
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !document.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind is not JsonValueKind.Object
                || !result.TryGetProperty("links", out var links)
                || links.ValueKind is not JsonValueKind.Array)
            {
                return [];
            }

            var found = new List<BdsDownload>();

            foreach (var link in links.EnumerateArray())
            {
                if (link.ValueKind is not JsonValueKind.Object
                    || !link.TryGetProperty("downloadType", out var typeNode)
                    || !link.TryGetProperty("downloadUrl", out var urlNode))
                {
                    continue;
                }

                var type = typeNode.GetString();
                var url = urlNode.GetString();

                if (type is null || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var channel = Windows
                    .Where(w => string.Equals(w.Type, type, StringComparison.OrdinalIgnoreCase))
                    .Select(w => (BdsChannel?)w.Channel)
                    .FirstOrDefault();

                if (channel is null)
                {
                    continue;
                }

                // Last segment of the path, which is where the version lives.
                var fileName = url[(url.LastIndexOf('/') + 1)..];
                var (version, _) = ServerPackageName.Parse(fileName);

                found.Add(new BdsDownload(channel.Value, url, fileName, version));
            }

            return found;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>What is on offer for one channel, or null when nothing is.</summary>
    public static BdsDownload? For(IEnumerable<BdsDownload> downloads, BdsChannel channel) =>
        downloads.FirstOrDefault(d => d.Channel == channel);
}
