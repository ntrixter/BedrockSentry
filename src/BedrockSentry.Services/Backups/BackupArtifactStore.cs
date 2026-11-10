using System.Text.Json;
using System.Text.Json.Serialization;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// Per-artifact notes and locks, as a JSON file beside the artifacts themselves
/// (<c>docs/design.md</c> § 8.6, <c>D68</c>).
/// </summary>
/// <remarks>
/// <para><strong>A second file rather than a column on <see cref="BackupRecord"/>.</strong>
/// <c>history.json</c> is in the right directory, already orphans with the
/// artifacts it describes, is never trimmed and has a schema convention — every
/// argument for putting this there except the one that decided it: history records
/// <em>what happened</em>, and a history that gets rewritten when somebody ticks a
/// box has stopped being one.</para>
///
/// <para><strong>Entries outlive their artifacts and that is deliberate.</strong>
/// Retention deletes a file; nothing here notices, and a stale entry costs a line
/// of JSON. Sweeping them would mean this file having an opinion about which
/// artifacts exist, which is the filesystem's job and is exactly what makes a
/// second source of truth go wrong. Locked artifacts are not deleted by retention
/// anyway, so the common case leaves nothing behind.</para>
///
/// <para>Written through the same atomic helper as the history, for the same
/// reason: a crash mid-write must leave the old file rather than half a
/// document.</para>
/// </remarks>
public sealed class BackupArtifactStore
{
    private readonly ISentryPaths _paths;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The file, beside <c>history.json</c> in the instance's own backup folder.</summary>
    public const string FileName = "artifacts.json";

    public BackupArtifactStore(ISentryPaths paths) => _paths = paths;

    private string FileFor(string instanceId) =>
        Path.Combine(_paths.Backups, instanceId, FileName);

    /// <summary>
    /// Everything said about this instance's artifacts, keyed by artifact name.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, because these keys are Windows file and folder names and
    /// the panel, the strategies and a hand edit will not agree on case.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, BackupArtifactState>> ReadAsync(
        string instanceId, CancellationToken cancellationToken = default)
    {
        var file = FileFor(instanceId);

        if (!File.Exists(file))
        {
            return Empty();
        }

        try
        {
            await using var stream = File.OpenRead(file);

            var read = await JsonSerializer
                .DeserializeAsync<Dictionary<string, BackupArtifactState>>(stream, Json, cancellationToken)
                .ConfigureAwait(false);

            if (read is null)
            {
                return Empty();
            }

            var states = Empty();
            foreach (var (name, state) in read)
            {
                states[name] = state;
            }

            return states;
        }
        catch (JsonException)
        {
            // Same rule as the history: a mangled file must not make backups
            // impossible. It is set aside rather than overwritten, so whatever it
            // held can still be recovered by hand — and a lost lock is a backup
            // that becomes prunable, which is why it is kept rather than deleted.
            File.Move(file, $"{file}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false);
            return Empty();
        }
    }

    /// <summary>The names an administrator has locked, for the retention rule.</summary>
    public async Task<IReadOnlySet<string>> LockedAsync(
        string instanceId, CancellationToken cancellationToken = default)
    {
        var states = await ReadAsync(instanceId, cancellationToken).ConfigureAwait(false);

        return states
            .Where(s => s.Value.Locked)
            .Select(s => s.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Replaces what is said about one artifact.</summary>
    /// <remarks>
    /// An empty state removes the entry rather than writing an object saying
    /// nothing, so a file somebody reads by hand lists only artifacts that have
    /// actually been marked.
    /// </remarks>
    public async Task SetAsync(
        string instanceId,
        string artifactName,
        BackupArtifactState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactName);

        var states = new Dictionary<string, BackupArtifactState>(
            await ReadAsync(instanceId, cancellationToken).ConfigureAwait(false),
            StringComparer.OrdinalIgnoreCase);

        if (state.IsEmpty)
        {
            states.Remove(artifactName);
        }
        else
        {
            states[artifactName] = state with { Note = Trimmed(state.Note) };
        }

        var file = FileFor(instanceId);

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        await AtomicFile.WriteAsync(
            file,
            async temp =>
            {
                await using var stream = File.Create(temp);
                await JsonSerializer
                    .SerializeAsync(stream, states, Json, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string? Trimmed(string? note) =>
        string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    private static Dictionary<string, BackupArtifactState> Empty() =>
        new(StringComparer.OrdinalIgnoreCase);
}
