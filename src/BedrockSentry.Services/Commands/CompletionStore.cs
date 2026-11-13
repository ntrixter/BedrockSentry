using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using BedrockSentry.Core.Commands;
using BedrockSentry.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Commands;

/// <summary>A run that did not finish, and why.</summary>
public sealed record CaptureFailure(string Reason, DateTimeOffset AtUtc);

/// <summary>One release of Mojang's vanilla data, as completion uses it.</summary>
/// <param name="Version">The release, <c>1.26.52</c>.</param>
/// <param name="Lists">Names by the type the help forms use: <c>Block</c>, <c>Item</c>, ….</param>
public sealed record VanillaData(string Version, DateTimeOffset FetchedUtc, IReadOnlyDictionary<string, IReadOnlyList<string>> Lists)
{
    /// <summary>One list, or none.</summary>
    public IReadOnlyList<string> Of(string type) => Lists.TryGetValue(type, out var names) ? names : [];
}

/// <summary>What the last look at Mojang's releases found.</summary>
/// <param name="CheckedUtc">When the registry was last asked, whatever it answered.</param>
/// <param name="Published">Every release it listed the last time it answered.</param>
/// <param name="Failure">Why the last ask failed, or null when it worked.</param>
public sealed record VanillaState(DateTimeOffset? CheckedUtc, IReadOnlyList<string> Published, CaptureFailure? Failure);

/// <summary>What a run with an instance's add-ons read.</summary>
/// <param name="Signature">The script packs it ran with (<see cref="AddonRun.Signature"/>).</param>
/// <param name="BdsVersion">The package it ran.</param>
/// <param name="Packs">Their names, for the Commands view.</param>
/// <param name="Commands">The commands the version's own server does not have.</param>
/// <param name="Failure">Why the run failed, when it did; then nothing above was read.</param>
public sealed record AddonCapture(
    string Signature,
    string BdsVersion,
    DateTimeOffset CapturedUtc,
    IReadOnlyList<string> Packs,
    IReadOnlyList<GrammarCommand> Commands,
    CaptureFailure? Failure = null);

/// <summary>Completion's files under <see cref="ISentryPaths.Completion"/> (<c>D105</c>).</summary>
/// <remarks>
/// <para>One JSON file per version, per release and per instance, each written whole
/// through <see cref="AtomicFile"/>, so a reader never sees half of one. A run's transcript
/// is kept beside what was read from it, so what the server said can be read back.</para>
///
/// <para>Every reader returns null for a file that is missing or does not parse: all of it
/// can be made again by running the server or fetching again, and the coordinator does
/// exactly that for a null.</para>
/// </remarks>
public sealed class CompletionStore(ISentryPaths paths, ILogger<CompletionStore> log)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private string Grammars => Path.Combine(paths.Completion, "grammar");

    private string Vanilla => Path.Combine(paths.Completion, "vanilla");


    /// <summary>The commands a version's run read, or null when none has finished.</summary>
    public CommandGrammar? ReadGrammar(string version) => Read<CommandGrammar>(GrammarFile(version));

    /// <summary>Keeps a version's commands and the transcript they came from, and clears its failure.</summary>
    public void WriteGrammar(CommandGrammar grammar, IReadOnlyList<ConsoleExchange> transcript)
    {
        ArgumentNullException.ThrowIfNull(grammar);
        Write(GrammarFile(grammar.BdsVersion), grammar);
        WriteTranscript(Path.ChangeExtension(GrammarFile(grammar.BdsVersion), ".log"), transcript);
        Delete(FailureFile(grammar.BdsVersion));
    }

    /// <summary>Every version a run has read here.</summary>
    public IReadOnlyList<string> GrammarVersions() =>
        Directory.Exists(Grammars)
            ? [.. Directory.EnumerateFiles(Grammars, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null && !n.EndsWith(".failed", StringComparison.Ordinal))
                .Select(n => n!)]
            : [];

    /// <summary>The last failed run for a version, or null.</summary>
    public CaptureFailure? ReadGrammarFailure(string version) => Read<CaptureFailure>(FailureFile(version));

    /// <summary>Records a version's failed run.</summary>
    public void WriteGrammarFailure(string version, CaptureFailure failure) => Write(FailureFile(version), failure);

    /// <summary>A release of Mojang's data, or null when it has not been fetched.</summary>
    public VanillaData? ReadVanilla(string release) => Read<VanillaData>(Path.Combine(Vanilla, Safe(release) + ".json"));

    /// <summary>
    /// The newest fetched release that is not after <paramref name="release"/>, or the
    /// newest of all when it is null; null when nothing has been fetched.
    /// </summary>
    public VanillaData? NewestVanillaNotAfter(string? release)
    {
        if (!Directory.Exists(Vanilla))
        {
            return null;
        }

        var limit = release is null ? null : NpmVersion.Parse(release);

        return Directory.EnumerateFiles(Vanilla, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(n => (Name: n!, Version: NpmVersion.Parse(n)))
            .Where(v => v.Version is not null && (limit is null || v.Version.Value.CompareTo(limit.Value) <= 0))
            .OrderByDescending(v => v.Version!.Value)
            .Select(v => ReadVanilla(v.Name))
            .FirstOrDefault(d => d is not null);
    }

    /// <summary>Keeps a fetched release.</summary>
    public void WriteVanilla(VanillaData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Write(Path.Combine(Vanilla, Safe(data.Version) + ".json"), data);
    }

    /// <summary>What the last look at the registry found.</summary>
    public VanillaState ReadVanillaState() =>
        Read<VanillaState>(Path.Combine(Vanilla, "state.json")) ?? new VanillaState(null, [], null);

    /// <summary>Records a look at the registry.</summary>
    public void WriteVanillaState(VanillaState state) => Write(Path.Combine(Vanilla, "state.json"), state);

    /// <summary>What an instance's add-on run read, or null when none has run.</summary>
    public AddonCapture? ReadAddons(string instanceId) => Read<AddonCapture>(InstanceFile(instanceId));

    /// <summary>Keeps an instance's add-on run, and its transcript when it has one.</summary>
    public void WriteAddons(string instanceId, AddonCapture capture, IReadOnlyList<ConsoleExchange>? transcript)
    {
        ArgumentNullException.ThrowIfNull(capture);
        Write(InstanceFile(instanceId), capture);
        if (transcript is not null)
        {
            WriteTranscript(Path.ChangeExtension(InstanceFile(instanceId), ".log"), transcript);
        }
    }

    /// <summary>Forgets an instance: no add-on runs, or the instance is gone.</summary>
    public void ForgetAddons(string instanceId)
    {
        Delete(InstanceFile(instanceId));
        Delete(Path.ChangeExtension(InstanceFile(instanceId), ".log"));
    }

    private string GrammarFile(string version) => Path.Combine(Grammars, Safe(version) + ".json");

    private string FailureFile(string version) => Path.Combine(Grammars, Safe(version) + ".failed.json");

    private string InstanceFile(string instanceId) => InstanceFiles(paths, instanceId)[0];

    /// <summary>
    /// The files an instance's add-on run is kept in, the capture first and then its
    /// transcript. Static so a removal can name them without a store (<c>T460</c>).
    /// </summary>
    public static IReadOnlyList<string> InstanceFiles(ISentryPaths paths, string instanceId)
    {
        var capture = Path.Combine(paths.Completion, "instances", Safe(instanceId) + ".json");
        return [capture, Path.ChangeExtension(capture, ".log")];
    }

    // Versions and instance ids are already file-safe; this keeps a hand-edited one from
    // naming a path outside the folder.
    private static string Safe(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        var safe = new string(chars).Trim('.');
        return safe.Length == 0 ? "_" : safe;
    }

    // A version's commands are about 380 KB of JSON and the console asks for them every few
    // seconds, so a file is parsed again only when it has been written since.
    private readonly ConcurrentDictionary<string, (DateTime Written, object? Value)> _read = new(StringComparer.Ordinal);

    private T? Read<T>(string path)
        where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                _read.TryRemove(path, out _);
                return null;
            }

            var written = File.GetLastWriteTimeUtc(path);
            if (_read.TryGetValue(path, out var cached) && cached.Written == written && cached.Value is T hit)
            {
                return hit;
            }

            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
            _read[path] = (written, value);
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log.LogWarning(ex, "Could not read {Path}; it is made again", path);
            return null;
        }
    }

    private void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not write {Path}", path);
        }
    }

    private void WriteTranscript(string path, IReadOnlyList<ConsoleExchange> transcript)
    {
        try
        {
            using var writer = new StringWriter();
            CaptureTranscript.Write(writer, transcript);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, writer.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not write {Path}", path);
        }
    }

    private void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}
