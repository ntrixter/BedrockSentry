using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BedrockSentry.Core.Commands;

namespace BedrockSentry.Services.Commands;

/// <summary>One published release of <c>@minecraft/vanilla-data</c>.</summary>
/// <param name="Tarball">Where npm serves it.</param>
/// <param name="Integrity">npm's <c>sha512-</c> digest of the tarball.</param>
public sealed record VanillaRelease(string Version, string Tarball, string Integrity);

/// <summary>Fetches Mojang's <c>@minecraft/vanilla-data</c> from the npm registry (<c>D105</c>).</summary>
/// <remarks>
/// <para>Two requests: the package's abbreviated metadata (about 140 KB, every release
/// and its tarball), then one tarball (about 100 KB). The tarball is checked against the
/// <c>sha512</c> the metadata names before anything in it is read, and only the
/// <c>package/lib/mojang-*.d.ts</c> files are read from it: text, parsed for names, never
/// run.</para>
///
/// <para>A tarball must come from the registry's own host. The metadata names its URL,
/// and a reply that pointed somewhere else would be fetched only because it said so.</para>
/// </remarks>
public sealed class VanillaDataClient(IHttpClientFactory clients)
{
    /// <summary>The named client, so this traffic stays separable in a diagnosis.</summary>
    public const string HttpClientName = "vanilla-data";

    /// <summary>The package's metadata.</summary>
    public const string MetadataUrl = "https://registry.npmjs.org/@minecraft%2fvanilla-data";

    private const string RegistryRoot = "https://registry.npmjs.org/";
    private const long MetadataLimit = 16 * 1024 * 1024;
    private const long TarballLimit = 16 * 1024 * 1024;
    private const long DeclarationLimit = 8 * 1024 * 1024;

    /// <summary>Every published release.</summary>
    /// <exception cref="VanillaDataException">The registry did not answer with a list.</exception>
    public async Task<IReadOnlyList<VanillaRelease>> ListAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MetadataUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.npm.install-v1+json"));

        var body = await SendAsync(request, MetadataLimit, cancellationToken).ConfigureAwait(false);
        return ParseReleases(body);
    }

    /// <summary>One release's names, by the type the help forms use.</summary>
    /// <exception cref="VanillaDataException">The tarball could not be fetched, did not match, or held no names.</exception>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FetchAsync(
        VanillaRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (!release.Tarball.StartsWith(RegistryRoot, StringComparison.Ordinal))
        {
            throw new VanillaDataException($"the registry named a tarball on another host, {release.Tarball}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, release.Tarball);
        var tarball = await SendAsync(request, TarballLimit, cancellationToken).ConfigureAwait(false);

        Verify(tarball, release);
        return Read(tarball, release.Version);
    }

    /// <summary>The releases in an abbreviated metadata document.</summary>
    internal static IReadOnlyList<VanillaRelease> ParseReleases(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("versions", out var versions)
                || versions.ValueKind != JsonValueKind.Object)
            {
                throw new VanillaDataException("the registry's answer listed no releases");
            }

            var releases = new List<VanillaRelease>();
            foreach (var version in versions.EnumerateObject())
            {
                if (version.Value.TryGetProperty("dist", out var dist)
                    && dist.TryGetProperty("tarball", out var tarball) && tarball.GetString() is { Length: > 0 } url
                    && dist.TryGetProperty("integrity", out var integrity) && integrity.GetString() is { Length: > 0 } digest)
                {
                    releases.Add(new VanillaRelease(version.Name, url, digest));
                }
            }

            return releases;
        }
        catch (JsonException ex)
        {
            throw new VanillaDataException($"the registry's answer was not JSON ({ex.Message.TrimEnd('.')})");
        }
    }

    /// <summary>Checks a tarball against the registry's digest.</summary>
    internal static void Verify(byte[] tarball, VanillaRelease release)
    {
        const string Prefix = "sha512-";
        var expected = release.Integrity
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(d => d.StartsWith(Prefix, StringComparison.Ordinal));

        if (expected is null)
        {
            throw new VanillaDataException($"the registry gave release {release.Version} no sha512 digest");
        }

        var actual = Prefix + Convert.ToBase64String(SHA512.HashData(tarball));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new VanillaDataException($"release {release.Version}'s tarball does not match the registry's sha512");
        }
    }

    /// <summary>The names in a tarball's declaration files.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Read(byte[] tarball, string version)
    {
        var files = new List<(string FileName, string Text)>();

        try
        {
            using var gzip = new GZipStream(new MemoryStream(tarball), CompressionMode.Decompress);
            using var tar = new TarReader(gzip);

            while (tar.GetNextEntry() is { } entry)
            {
                var name = entry.Name.Replace('\\', '/');
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    || entry.DataStream is null
                    || !name.StartsWith("package/lib/", StringComparison.Ordinal)
                    || !VanillaRegistries.Files.ContainsKey(Path.GetFileName(name)))
                {
                    continue;
                }

                if (entry.Length > DeclarationLimit)
                {
                    throw new VanillaDataException($"{name} in release {version} is larger than {DeclarationLimit / 1024 / 1024} MB");
                }

                using var reader = new StreamReader(entry.DataStream, Encoding.UTF8);
                files.Add((Path.GetFileName(name), reader.ReadToEnd()));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw new VanillaDataException($"release {version}'s tarball could not be read ({ex.Message.TrimEnd('.')})");
        }

        var lists = VanillaRegistries.Read(files);
        if (!lists.TryGetValue("Block", out var blocks) || blocks.Count == 0)
        {
            throw new VanillaDataException($"release {version} holds no block names");
        }

        return lists;
    }

    private async Task<byte[]> SendAsync(HttpRequestMessage request, long limit, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(HttpClientName);
        http.Timeout = TimeSpan.FromSeconds(30);

        try
        {
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new VanillaDataException(
                    $"the registry answered {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());
            }

            if (response.Content.Headers.ContentLength > limit)
            {
                throw new VanillaDataException($"the registry's answer is larger than {limit / 1024 / 1024} MB");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    throw new VanillaDataException($"the registry's answer is larger than {limit / 1024 / 1024} MB");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (HttpRequestException ex)
        {
            throw new VanillaDataException($"the registry could not be reached ({ex.Message.TrimEnd('.')})");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VanillaDataException("the registry did not answer within 30 seconds");
        }
    }
}

/// <summary>Why Mojang's data could not be fetched, worded to sit after a colon.</summary>
public sealed class VanillaDataException(string reason) : Exception(reason);
