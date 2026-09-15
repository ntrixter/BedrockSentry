using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Updates;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Updates;

/// <summary>
/// What a read of the channel manifest came back with.
/// </summary>
/// <param name="Manifest">
/// The build the channel is pointing at, or null when nothing usable arrived.
/// </param>
/// <param name="RetryAfter">
/// How long the server asked to be left alone, when it said so — otherwise null.
/// </param>
/// <remarks>
/// <b>This is deliberately not inferred from a status code alone.</b> A private
/// repository answers 404 for a reason that has nothing to do with rate limiting
/// (<c>D13</c>), and that is the shape every install is in until this repository
/// goes public — so reading a 404 as a throttle would stop the scheduled check
/// everywhere, for an hour at a time, on the most common failure there is.
/// Populated only when the response actually carried a <c>Retry-After</c>, or the
/// status was 429, which means it and nothing else.
/// </remarks>
public sealed record ManifestFetch(UpdateManifest? Manifest, TimeSpan? RetryAfter)
{
    /// <summary>A fetch that produced nothing and no throttle.</summary>
    public static readonly ManifestFetch Nothing = new(null, null);
}

/// <summary>
/// Fetches the channel manifest, downloads a release, verifies it, and stages it into
/// its own version directory.
/// </summary>
/// <remarks>
/// Everything here runs inside the <em>old, working</em> build. That ordering is a
/// safety property rather than an accident: a build that cannot start is never in a
/// position to download or stage anything, so a bad release cannot cascade.
/// </remarks>
public sealed class UpdateClient
{
    private readonly HttpClient _http;
    private readonly InstallLayout _layout;
    private readonly ILogger<UpdateClient> _logger;

    public UpdateClient(HttpClient http, InstallLayout layout, ILogger<UpdateClient> logger)
    {
        _http = http;
        _layout = layout;
        _logger = logger;
    }

    /// <summary>
    /// How long to stand off a 429 that did not say, and the ceiling on any
    /// answer that did.
    /// </summary>
    /// <remarks>
    /// A 429 with no <c>Retry-After</c> is a server asking for less traffic
    /// without saying how much less, and guessing small defeats the purpose.
    /// The cap exists because the value is somebody else's: a garbled header,
    /// or one naming a date years out, must not be able to wedge update
    /// checking for the life of the service.
    /// </remarks>
    private static readonly TimeSpan UnspecifiedRetryAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongestRetryAfter = TimeSpan.FromHours(1);

    /// <summary>Reads the manifest describing the newest build on a channel.</summary>
    public async Task<ManifestFetch> FetchManifestAsync(UpdateOptions options, CancellationToken ct = default)
    {
        var url = options.FeedUrlTemplate.Replace(
            "{channel}",
            options.Channel.ToString().ToLowerInvariant(),
            StringComparison.OrdinalIgnoreCase);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // The channel pointer is rewritten in place on every release, so a cached
            // copy is worse than useless — it hides exactly the change being polled for.
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
            };

            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var retryAfter = RetryAfterOf(response);

                _logger.LogWarning(
                    "Update check failed: {Url} returned {Status}. " +
                    "A private repository does not serve release assets anonymously (see D13).",
                    url,
                    (int)response.StatusCode);

                if (retryAfter is { } wait)
                {
                    _logger.LogWarning(
                        "{Url} asked to be left alone for {Wait}. No check will be made until then.",
                        url,
                        wait);
                }

                return new ManifestFetch(null, retryAfter);
            }

            return new ManifestFetch(
                JsonSerializer.Deserialize<UpdateManifest>(await response.Content.ReadAsStringAsync(ct)),
                null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read the update manifest from {Url}.", url);
            return ManifestFetch.Nothing;
        }
    }

    /// <summary>
    /// How long the response asked to be left alone, or null if it did not ask.
    /// </summary>
    /// <remarks>
    /// <c>Retry-After</c> arrives either as a number of seconds or as an HTTP
    /// date. For a date, the subtraction uses the response's <em>own</em>
    /// <c>Date</c> header when it carried one: that cancels clock skew between
    /// this machine and theirs, which a comparison against the local clock would
    /// inherit. Falling back to the local clock is fine — it is a floor on a
    /// wait, not a deadline.
    /// </remarks>
    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var asked = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - (response.Headers.Date ?? DateTimeOffset.UtcNow),
            _ => (TimeSpan?)null,
        };

        // A 429 with no header is still a refusal, and the one status where the
        // meaning is unambiguous enough to act on without being told.
        asked ??= response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
            ? UnspecifiedRetryAfter
            : null;

        if (asked is not { } wait)
        {
            return null;
        }

        return wait < TimeSpan.Zero
            ? TimeSpan.Zero
            : wait > LongestRetryAfter ? LongestRetryAfter : wait;
    }

    /// <summary>
    /// Downloads and unpacks a release into its version directory.
    /// </summary>
    /// <returns>The staged version directory, or null if anything failed.</returns>
    public async Task<string?> StageAsync(UpdateManifest manifest, CancellationToken ct = default)
    {
        var versionDirectory = _layout.VersionDirectory(manifest.Identity);

        if (Directory.Exists(versionDirectory))
        {
            _logger.LogInformation("Build {Identity} is already staged.", manifest.Identity);
            return versionDirectory;
        }

        var staging = versionDirectory + ".staging";
        var archive = Path.Combine(Path.GetTempPath(), $"bedrocksentry-{Guid.NewGuid():N}.zip");

        try
        {
            _logger.LogInformation("Downloading {Identity} from {Url}.", manifest.Identity, manifest.DownloadUrl);

            using (var response = await _http.GetAsync(manifest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var destination = File.Create(archive);
                await source.CopyToAsync(destination, ct);
            }

            if (!await VerifyAsync(archive, manifest.Sha256, ct))
            {
                return null;
            }

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            ZipFile.ExtractToDirectory(archive, staging);

            if (!File.Exists(Path.Combine(staging, "BedrockSentry.Host.exe")))
            {
                _logger.LogError("The downloaded archive has no BedrockSentry.Host.exe; refusing to stage it.");
                Directory.Delete(staging, recursive: true);
                return null;
            }

            // Move only once the contents are complete and checked, so a version
            // directory never exists in a half-populated state — the updater treats
            // its presence as meaning "ready to switch to".
            Directory.Move(staging, versionDirectory);

            _logger.LogInformation("Staged {Identity} at {Directory}.", manifest.Identity, versionDirectory);

            return versionDirectory;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not stage {Identity}.", manifest.Identity);

            TryDeleteDirectory(staging);
            return null;
        }
        finally
        {
            TryDeleteFile(archive);
        }
    }

    private async Task<bool> VerifyAsync(string archivePath, string expected, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            // Refusing beats trusting. An unverifiable download is indistinguishable
            // from a tampered one, and this content is about to become the code that
            // runs as LocalSystem.
            _logger.LogError("The manifest carries no SHA-256; refusing the download.");
            return false;
        }

        await using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();

        if (!string.Equals(actual, expected.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            _logger.LogError(
                "Checksum mismatch: expected {Expected}, got {Actual}. Discarding the download.",
                expected,
                actual);

            return false;
        }

        return true;
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not delete the temporary download {Path}.", path);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not clean up the staging directory {Path}.", path);
        }
    }
}
