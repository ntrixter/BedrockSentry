using System.Net;
using BedrockSentry.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// Asks a third-party echo service what this machine's public address is
/// (<c>D20</c>), and remembers the answer.
/// </summary>
/// <remarks>
/// <para>
/// The caching is not an optimisation, it is a correctness requirement. This
/// feeds a page that re-renders once a second; without a cache the wrapper would
/// make a request per second, for the life of the process, to somebody else's
/// free service. A public address changes rarely enough that half an hour is
/// generous.
/// </para>
/// <para>
/// A failure is remembered too, for a shorter time. Retrying every render when
/// the machine is offline is the same problem wearing a different hat, and the
/// page has something honest to show either way.
/// </para>
/// <para>
/// <strong>Which makes the registration part of the type</strong>, and it was
/// wrong (<c>T217</c>). A field cache only caches for as long as there is one
/// instance, and <c>AddHttpClient&lt;T&gt;()</c> registers the typed client
/// transient — so this was a correctness requirement enforced by nothing but the
/// accident of a single singleton consumer. Registered singleton against a named
/// client now, and taking <see cref="IHttpClientFactory"/> rather than holding one
/// <see cref="HttpClient"/> for the life of the process: a captured handler holds
/// its DNS answers for just as long, which is the cost the other half of that
/// pattern exists to avoid.
/// </para>
/// </remarks>
public sealed class PublicAddressService
{
    /// <summary>The named client, so its traffic stays separable in diagnosis.</summary>
    public const string HttpClientName = "public-address";

    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(2);

    private readonly IHttpClientFactory _clients;
    private readonly IOptionsMonitor<HostDetailsOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PublicAddressService> _log;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _address;
    private string? _note;
    private DateTimeOffset _goodUntil;

    public PublicAddressService(
        IHttpClientFactory clients,
        IOptionsMonitor<HostDetailsOptions> options,
        TimeProvider time,
        ILogger<PublicAddressService> log)
    {
        _clients = clients;
        _options = options;
        _time = time;
        _log = log;
    }

    private HttpClient NewClient()
    {
        var http = _clients.CreateClient(HttpClientName);

        // The echo services are tiny and the page is waiting on this; a long
        // default timeout would leave the status page blank for a minute and a
        // half on a machine with no route out.
        http.Timeout = TimeSpan.FromSeconds(8);
        return http;
    }

    /// <summary>
    /// The cached address, and a note explaining any absence. Never throws:
    /// a status page must render whatever the internet is doing.
    /// </summary>
    public async Task<(string? Address, string? Note)> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;

        if (!options.DiscoverPublicAddress)
        {
            return (null, "Public address lookup is turned off.");
        }

        var now = _time.GetUtcNow();
        if (now < _goodUntil)
        {
            return (_address, _note);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-checked inside the lock: several page renders can arrive at an
            // expired cache together, and only the first should go out.
            now = _time.GetUtcNow();
            if (now < _goodUntil)
            {
                return (_address, _note);
            }

            try
            {
                using var http = NewClient();

                var text = await http.GetStringAsync(options.PublicAddressEndpoint, cancellationToken)
                    .ConfigureAwait(false);

                _address = IPAddress.TryParse(text.Trim(), out var parsed) ? parsed.ToString() : null;

                _note = _address is null
                    ? $"{options.PublicAddressEndpoint} answered something that is not an address."
                    : null;

                _goodUntil = now + (_address is null ? RetryAfterFailure : options.PublicAddressCacheFor);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Not an error worth a stack trace in the log every half hour.
                // The machine may simply have no route out, which is a fine way
                // for a Minecraft server on a LAN to be configured.
                _log.LogDebug(ex, "Could not reach {Endpoint}", options.PublicAddressEndpoint);

                _address = null;
                _note = $"Could not reach {options.PublicAddressEndpoint}.";
                _goodUntil = now + RetryAfterFailure;
            }

            return (_address, _note);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the cache so the next read goes out again.</summary>
    public void Forget() => _goodUntil = default;
}
