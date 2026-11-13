using System.Text.Json;
using System.Text.Json.Serialization;

namespace BedrockSentry.Core.Broker;

/// <summary>
/// What the broker itself created, kept where only it can write, so the operations
/// that undo or continue something act only on what it made.
/// </summary>
/// <remarks>
/// <para><see cref="BrokerOperation.ReleaseShadowCopy"/> releases only an id listed
/// here: a shadow copy the broker did not take is somebody else's backup, and
/// releasing it on request would let the main service delete any snapshot on the
/// machine.</para>
/// <para><see cref="BrokerOperation.ApplyUpdate"/> launches only the build named in
/// <see cref="StagedBuild"/>, the one <see cref="BrokerOperation.StageUpdate"/>
/// verified and unpacked.</para>
/// <para>On disk because shadow copies outlive a broker restart, and a copy left by a
/// crash is still the broker's to release.</para>
/// <para>Read again from disk before every question and change, because a second
/// process writes it too: <c>install.ps1 -GrantFolder</c> adds a folder while the
/// broker runs, and a broker holding only what it read at start refused that folder's
/// Defender exclusion until restarted (<c>T391</c> clause 2).</para>
/// </remarks>
public sealed class BrokerRecord
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private State _state;

    private BrokerRecord(string path, State state)
    {
        _path = path;
        _state = state;
    }

    /// <summary>The shadow copies taken and not yet released, by id.</summary>
    public IReadOnlyCollection<string> ShadowCopies
    {
        get
        {
            lock (_gate)
            {
                Refresh();
                return [.. _state.ShadowCopies];
            }
        }
    }

    /// <summary>The build staged last, or null.</summary>
    public string? StagedBuild
    {
        get
        {
            lock (_gate)
            {
                Refresh();
                return _state.StagedBuild;
            }
        }
    }

    /// <summary>
    /// Reads the record at <paramref name="path"/>. A missing file is an empty record;
    /// an unreadable one is too, and the copies it named are no longer the broker's
    /// to release, which costs disk space and never a backup.
    /// </summary>
    public static BrokerRecord Load(string path) => new(path, Read(path) ?? new State());

    /// <summary>The record on disk, or null when there is none or it cannot be read.</summary>
    private static State? Read(string path)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<State>(File.ReadAllText(path)) is { } state)
            {
                state.ShadowCopies ??= [];
                state.GrantedFolders ??= [];
                return state;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// Takes up what another process wrote since. A file that is gone or unreadable
    /// keeps what this one holds, so a torn read never forgets a shadow copy.
    /// </summary>
    private void Refresh()
    {
        if (Read(_path) is { } state)
        {
            _state = state;
        }
    }

    /// <summary>Records a shadow copy the broker took.</summary>
    public void AddShadowCopy(string shadowId) => Change(s => s.ShadowCopies.Add(Key(shadowId)));

    /// <summary>Whether the broker took <paramref name="shadowId"/> and has not released it.</summary>
    public bool OwnsShadowCopy(string shadowId)
    {
        lock (_gate)
        {
            Refresh();
            return _state.ShadowCopies.Contains(Key(shadowId));
        }
    }

    /// <summary>Forgets a shadow copy once it is released.</summary>
    public void RemoveShadowCopy(string shadowId) => Change(s => s.ShadowCopies.Remove(Key(shadowId)));

    /// <summary>Records a folder the broker granted, which may then be excluded from scanning (<c>D115</c>).</summary>
    public void AddGrantedFolder(string folder) => Change(s =>
    {
        s.GrantedFolders.RemoveWhere(Gone);
        s.GrantedFolders.Add(FolderKey(folder));
    });

    /// <summary>
    /// Drops each granted folder that no longer exists (<c>T445</c>): a removed server's
    /// folder stayed on the list, so a folder made later at that path read as granted.
    /// Run at the broker's start, before each grant and before each request it answers.
    /// </summary>
    /// <returns>How many were dropped.</returns>
    public int DropGoneFolders()
    {
        var dropped = 0;

        lock (_gate)
        {
            Refresh();
            if (!_state.GrantedFolders.Any(Gone))
            {
                return 0;
            }
        }

        Change(s => dropped = s.GrantedFolders.RemoveWhere(Gone));
        return dropped;
    }

    /// <summary>A seam for <see cref="Directory.Exists"/>, so a test can say what is on disk.</summary>
    internal Func<string, bool> FolderExists { get; set; } = Directory.Exists;

    private bool Gone(string key) => !FolderExists(key);

    /// <summary>Whether the broker granted <paramref name="folder"/> itself.</summary>
    public bool Granted(string folder)
    {
        lock (_gate)
        {
            Refresh();
            return _state.GrantedFolders.Contains(FolderKey(folder));
        }
    }

    /// <summary>Records the build staged last, replacing any before it.</summary>
    public void SetStagedBuild(string? identity) => Change(s => s.StagedBuild = identity);

    /// <summary>
    /// One spelling per id: Windows prints a shadow id with braces and in either case,
    /// and a record that compared spellings would refuse to release its own copy.
    /// </summary>
    private static string Key(string shadowId) =>
        Guid.TryParse(shadowId.Trim('{', '}'), out var id) ? id.ToString("D") : shadowId;

    /// <summary>One spelling per folder: full, without a trailing separator, in one case, as Windows compares them.</summary>
    private static string FolderKey(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)).ToUpperInvariant();

    private void Change(Action<State> change)
    {
        lock (_gate)
        {
            Refresh();
            change(_state);

            var directory = Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Written beside and moved over, so a crash mid-write leaves the previous
            // record rather than a truncated one that reads as empty.
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state, Json));
            File.Move(temporary, _path, overwrite: true);
        }
    }

    private sealed class State
    {
        [JsonPropertyName("shadowCopies")]
        public HashSet<string> ShadowCopies { get; set; } = [];

        [JsonPropertyName("grantedFolders")]
        public HashSet<string> GrantedFolders { get; set; } = [];

        [JsonPropertyName("stagedBuild")]
        public string? StagedBuild { get; set; }
    }
}
