using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Players;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BedrockSentry.Services.Players;

/// <summary>
/// A player's face for the player lists (<c>D47</c>): one set by hand for that
/// player (<c>D116</c>), else fetched by XUID from GeyserMC when switched on and
/// kept on disk, else generated from the XUID.
/// </summary>
/// <remarks>
/// <para>
/// The lists show <see cref="Route"/> as an image, so the browser asks for each
/// face on its own and no page render ever waits on a third party.
/// </para>
/// <para>
/// GeyserMC answers <c>GET /v2/skin/{xuid}</c> with the skin it last converted for
/// that player, or an empty object if it has none; the skin itself is a Java-layout
/// PNG at <c>textures.minecraft.net/texture/{texture_id}</c>. A player Geyser has
/// never seen is a player who has not joined a Geyser server since changing skin,
/// so some rows show the generated face whatever is configured.
/// </para>
/// <para>
/// Every failure answers the generated face, or the face kept from an earlier
/// fetch if there is one: <see cref="GetAsync"/> never throws.
/// </para>
/// <para>
/// A face set by hand lives under <c>set/</c> beside the cache, never expires and
/// outranks the switch, since setting it contacted nobody but whoever the person
/// setting it chose: nobody for an uploaded skin, Mojang once for a Java name.
/// </para>
/// </remarks>
public sealed partial class PlayerFaces
{
    public const string HttpClientName = "player-faces";

    /// <summary>The image's address, signed-in only like every other route.</summary>
    public const string Route = "/player-faces/{xuid}";

    public const string SkinEndpoint = "https://api.geysermc.org/v2/skin/";

    public const string TextureEndpoint = "https://textures.minecraft.net/texture/";

    /// <summary>A Java username to its profile id; 404 for a name nobody holds.</summary>
    public const string JavaNameEndpoint = "https://api.minecraftservices.com/minecraft/profile/lookup/name/";

    /// <summary>A Java profile id to its textures, base64 JSON naming the skin's URL.</summary>
    public const string JavaProfileEndpoint = "https://sessionserver.mojang.com/session/minecraft/profile/";

    /// <summary>How long a "no skin" answer stands before the player is asked about again.</summary>
    public static readonly TimeSpan RetryMissAfter = TimeSpan.FromDays(1);

    /// <summary>How long nothing is asked after the source could not be reached.</summary>
    public static readonly TimeSpan RetryUnreachableAfter = TimeSpan.FromMinutes(5);

    /// <summary>A skin is a few kilobytes; anything past this is not one.</summary>
    public const int MaxSkinBytes = 256 * 1024;

    private readonly IHttpClientFactory _clients;
    private readonly IOptionsMonitor<PlayerFacesOptions> _options;
    private readonly ISentryPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<PlayerFaces> _log;

    /// <summary>One fetch per XUID however many rows and viewers ask at once.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>A roster of fifty is fifty requests; four at a time is polite.</summary>
    private readonly SemaphoreSlim _fetchSlots = new(4, 4);

    private DateTimeOffset _unreachableUntil;

    public PlayerFaces(
        IHttpClientFactory clients,
        IOptionsMonitor<PlayerFacesOptions> options,
        ISentryPaths paths,
        TimeProvider time,
        ILogger<PlayerFaces> log)
    {
        _clients = clients;
        _options = options;
        _paths = paths;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// The address a list puts in an image for one player.
    /// </summary>
    /// <remarks>
    /// Carries whether faces are on, so switching them on or off is a new address
    /// and the browser does not keep showing what it cached under the other.
    /// Relative, like every link in the panel, so it resolves against the base.
    /// </remarks>
    /// <remarks>
    /// A face set by hand adds when it was set, so setting or replacing one is a
    /// new address too.
    /// </remarks>
    public string UrlFor(string xuid)
    {
        var url = $"player-faces/{Uri.EscapeDataString(xuid)}?on={(_options.CurrentValue.Enabled ? 1 : 0)}";
        if (PlayerFace.IsXuid(xuid) && File.Exists(SetPath(xuid)))
        {
            url += $"&set={File.GetLastWriteTimeUtc(SetPath(xuid)).Ticks}";
        }

        return url;
    }

    /// <summary>Whether this player has a face set by hand.</summary>
    public bool HasSetFace(string xuid) => PlayerFace.IsXuid(xuid) && File.Exists(SetPath(xuid));

    /// <summary>
    /// Sets the player's face from a skin PNG the person at the panel supplied.
    /// Contacts nobody.
    /// </summary>
    public SetFaceOutcome SetFromSkin(string xuid, ReadOnlySpan<byte> skinPng)
    {
        if (!PlayerFace.IsXuid(xuid))
        {
            return SetFaceOutcome.NotAXuid;
        }

        if (skinPng.Length > MaxSkinBytes || PlayerFace.FromSkin(skinPng) is not { } face)
        {
            return SetFaceOutcome.NotASkin;
        }

        Write(SetPath(xuid), face);
        return SetFaceOutcome.Set;
    }

    /// <summary>
    /// Sets the player's face from the skin a Java account wears: asks Mojang for
    /// the name's profile, its textures, then the skin, once, and keeps the face.
    /// </summary>
    /// <remarks>
    /// Only ever on a click with a name the person typed: a gamertag is not a Java
    /// name, and the account holding the same name is often somebody else. Every
    /// value from Mojang is checked before it reaches a URL; the skin is fetched
    /// from <see cref="TextureEndpoint"/> by its hex id, never from the URL given.
    /// </remarks>
    public async Task<SetFaceOutcome> SetFromJavaNameAsync(
        string xuid, string javaName, CancellationToken cancellationToken = default)
    {
        if (!PlayerFace.IsXuid(xuid))
        {
            return SetFaceOutcome.NotAXuid;
        }

        javaName = javaName.Trim();
        if (!JavaName().IsMatch(javaName))
        {
            return SetFaceOutcome.NotAJavaName;
        }

        try
        {
            using var http = _clients.CreateClient(HttpClientName);
            http.Timeout = TimeSpan.FromSeconds(10);

            using var named = await http.GetAsync(JavaNameEndpoint + javaName, cancellationToken).ConfigureAwait(false);
            if (named.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
            {
                return SetFaceOutcome.NoSuchJavaPlayer;
            }

            named.EnsureSuccessStatusCode();
            var profile = await named.Content.ReadFromJsonAsync<JavaNameAnswer>(cancellationToken).ConfigureAwait(false);
            if (profile?.Id is not { } id || !ProfileId().IsMatch(id))
            {
                return SetFaceOutcome.NoSuchJavaPlayer;
            }

            var textures = await http
                .GetFromJsonAsync<JavaProfile>(JavaProfileEndpoint + id, cancellationToken)
                .ConfigureAwait(false);
            if (TextureIdOf(textures) is not { } texture)
            {
                return SetFaceOutcome.NoJavaSkin;
            }

            using var response = await http
                .GetAsync(TextureEndpoint + texture, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var skin = await ReadBoundedAsync(response.Content).ConfigureAwait(false);
            if (skin is null || PlayerFace.FromSkin(skin) is not { } face)
            {
                return SetFaceOutcome.NotASkin;
            }

            Write(SetPath(xuid), face);
            return SetFaceOutcome.Set;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            _log.LogInformation(ex, "Could not fetch the Java skin for {Xuid}", xuid);
            return SetFaceOutcome.Unreachable;
        }
    }

    /// <summary>Removes the face set by hand; the player goes back to GeyserMC's or the generated one.</summary>
    public void ClearSetFace(string xuid)
    {
        if (PlayerFace.IsXuid(xuid))
        {
            File.Delete(SetPath(xuid));
        }
    }

    /// <summary>
    /// Deletes every face file kept for the player: the one set by hand, GeyserMC's
    /// and the "no skin" marker. Part of erasing the player, so a failure throws
    /// rather than leaving a face behind unsaid.
    /// </summary>
    public void Forget(string xuid)
    {
        if (!PlayerFace.IsXuid(xuid))
        {
            return;
        }

        File.Delete(SetPath(xuid));
        File.Delete(FacePath(xuid));
        File.Delete(MissPath(xuid));
    }

    /// <summary>The sentence the panel shows for an outcome.</summary>
    public static string Describe(SetFaceOutcome outcome) => outcome switch
    {
        SetFaceOutcome.Set => "Face set.",
        SetFaceOutcome.NotAXuid => "This player has no XUID, so no face can be kept for them.",
        SetFaceOutcome.NotASkin =>
            "That is not a skin a face can be cut from: a PNG 64 pixels wide (or 128, 256, 512), square or half as tall, under 256 KB.",
        SetFaceOutcome.NotAJavaName => "A Java username is 1 to 16 letters, digits or underscores.",
        SetFaceOutcome.NoSuchJavaPlayer => "Mojang has no Java account by that name.",
        SetFaceOutcome.NoJavaSkin => "That Java account wears one of the default skins, so there is no skin to take a face from.",
        SetFaceOutcome.Unreachable => "Mojang could not be reached. Nothing was changed.",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    /// <summary>A PNG for the player; null only for something that is not a XUID.</summary>
    public async Task<byte[]?> GetAsync(string xuid, CancellationToken cancellationToken = default)
    {
        if (!PlayerFace.IsXuid(xuid))
        {
            return null;
        }

        if (TryRead(SetPath(xuid)) is { } set)
        {
            return set;
        }

        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return PlayerFace.Generated(xuid);
        }

        var now = _time.GetUtcNow();
        var face = FacePath(xuid);
        var miss = MissPath(xuid);

        var kept = TryRead(face);
        if (kept is not null && File.GetLastWriteTimeUtc(face) + options.CacheFor > now.UtcDateTime)
        {
            return kept;
        }

        if (kept is null && File.Exists(miss) && File.GetLastWriteTimeUtc(miss) + RetryMissAfter > now.UtcDateTime)
        {
            return PlayerFace.Generated(xuid);
        }

        if (now < _unreachableUntil)
        {
            return kept ?? PlayerFace.Generated(xuid);
        }

        var fetch = _inFlight.GetOrAdd(xuid, key => new Lazy<Task<byte[]?>>(() => RefreshAsync(key)));
        try
        {
            // Not the caller's token: one viewer navigating away must not cancel a
            // fetch other rows are waiting on, and the file is worth keeping anyway.
            var fetched = await fetch.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            return fetched ?? kept ?? PlayerFace.Generated(xuid);
        }
        finally
        {
            if (fetch.IsValueCreated && fetch.Value.IsCompleted)
            {
                _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]?>>>(xuid, fetch));
            }
        }
    }

    /// <summary>
    /// Asks the source, writes what it answered, and returns the face; null when
    /// there is none to show, which the caller turns into the kept or generated one.
    /// </summary>
    private async Task<byte[]?> RefreshAsync(string xuid)
    {
        await _fetchSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            using var http = _clients.CreateClient(HttpClientName);
            http.Timeout = TimeSpan.FromSeconds(10);

            var answer = await http.GetFromJsonAsync<SkinAnswer>(SkinEndpoint + xuid).ConfigureAwait(false);

            if (answer?.TextureId is not { } texture || !TextureId().IsMatch(texture))
            {
                RecordMiss(xuid);
                return null;
            }

            using var response = await http
                .GetAsync(TextureEndpoint + texture, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var skin = await ReadBoundedAsync(response.Content).ConfigureAwait(false);
            if (skin is null || PlayerFace.FromSkin(skin) is not { } face)
            {
                _log.LogInformation("The skin GeyserMC holds for {Xuid} is not one a face can be cut from", xuid);
                RecordMiss(xuid);
                return null;
            }

            Write(FacePath(xuid), face);
            TryDelete(MissPath(xuid));
            return face;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Not worth a stack trace per player: a server with no route out is a
            // fine way to run, and the rows already show a face either way.
            _log.LogDebug(ex, "Could not fetch the face for {Xuid}", xuid);
            _unreachableUntil = _time.GetUtcNow() + RetryUnreachableAfter;
            return null;
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Could not keep the face for {Xuid} under {Folder}", xuid, _paths.PlayerFaces);
            return null;
        }
        finally
        {
            _fetchSlots.Release();
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content)
    {
        if (content.Headers.ContentLength > MaxSkinBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxSkinBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private void RecordMiss(string xuid) => Write(MissPath(xuid), []);

    private void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Whole or not at all: a half-written face read by the next request would
        // be a broken image until the cache expired.
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
        File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
    }

    private static byte[]? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A stale "no skin" marker under a face that exists is never read.
        }
    }

    private string FacePath(string xuid) => Path.Combine(_paths.PlayerFaces, xuid + ".png");

    private string MissPath(string xuid) => Path.Combine(_paths.PlayerFaces, xuid + ".none");

    private string SetPath(string xuid) => Path.Combine(_paths.PlayerFaces, "set", xuid + ".png");

    /// <summary>
    /// The skin's hex texture id from a Java profile's <c>textures</c> property;
    /// null when the account wears a default skin or the answer is not one.
    /// </summary>
    private static string? TextureIdOf(JavaProfile? profile)
    {
        var encoded = profile?.Properties?.FirstOrDefault(p => p.Name == "textures")?.Value;
        if (encoded is null)
        {
            return null;
        }

        using var textures = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        if (!textures.RootElement.TryGetProperty("textures", out var all)
            || !all.TryGetProperty("SKIN", out var skin)
            || !skin.TryGetProperty("url", out var url)
            || url.GetString() is not { } address)
        {
            return null;
        }

        var texture = address[(address.LastIndexOf('/') + 1)..];
        return address.Contains("://textures.minecraft.net/texture/", StringComparison.Ordinal)
            && TextureId().IsMatch(texture)
            ? texture
            : null;
    }

    /// <summary>A Mojang texture id is hex; anything else is not put in a URL.</summary>
    [GeneratedRegex("^[0-9a-f]{1,128}$")]
    private static partial Regex TextureId();

    /// <summary>What Mojang accepts as a Java username, and all that goes in its URL.</summary>
    [GeneratedRegex("^[A-Za-z0-9_]{1,16}$")]
    private static partial Regex JavaName();

    /// <summary>A Java profile id: 32 hex digits, no dashes.</summary>
    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ProfileId();

    private sealed class JavaNameAnswer
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private sealed class JavaProfile
    {
        [JsonPropertyName("properties")]
        public List<JavaProperty>? Properties { get; set; }
    }

    private sealed class JavaProperty
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }

    private sealed class SkinAnswer
    {
        [JsonPropertyName("texture_id")]
        public string? TextureId { get; set; }
    }
}
