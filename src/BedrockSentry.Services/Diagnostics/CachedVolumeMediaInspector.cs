using BedrockSentry.Core.Diagnostics;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// <see cref="IVolumeMediaInspector"/> with each volume's answer kept, so the Storage
/// table does not ask Windows again on every Status page open.
/// </summary>
/// <remarks>
/// <para>The reading behind it is three CIM queries in
/// <c>root\Microsoft\Windows\Storage</c>, the slowest call on the Status page, and the
/// page reads its volumes on every open. A drive's type does not change while it is
/// mounted, so a known answer is kept for <see cref="KnownFor"/>.</para>
/// <para><see cref="DriveMedia.Unknown"/> is kept for the shorter
/// <see cref="UnknownFor"/>: a failed read is answered that way too, and a machine
/// that cannot place a volume (a virtual disk) would otherwise pay for the queries on
/// every open for an answer that never changes.</para>
/// <para>One root missing or stale asks the inner inspector for every root at once,
/// since the queries read the whole machine either way.</para>
/// </remarks>
public sealed class CachedVolumeMediaInspector(IVolumeMediaInspector inner, TimeProvider time) : IVolumeMediaInspector
{
    /// <summary>How long a placed volume's answer is shown before Windows is asked again.</summary>
    public static readonly TimeSpan KnownFor = TimeSpan.FromHours(1);

    /// <summary>How long an <see cref="DriveMedia.Unknown"/> answer is shown before Windows is asked again.</summary>
    public static readonly TimeSpan UnknownFor = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, (DriveMedia Media, DateTimeOffset At)> _kept =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, DriveMedia> Inspect(IReadOnlyCollection<string> volumeRoots)
    {
        var now = time.GetUtcNow();

        lock (_gate)
        {
            if (volumeRoots.All(root => _kept.TryGetValue(root, out var kept) && Fresh(kept, now)))
            {
                return volumeRoots
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(root => root, root => _kept[root].Media, StringComparer.OrdinalIgnoreCase);
            }
        }

        // Outside the lock: the read is slow, and a second caller waiting on it here
        // would wait just as long as asking itself.
        var read = inner.Inspect(volumeRoots);
        var at = time.GetUtcNow();

        lock (_gate)
        {
            foreach (var (root, media) in read)
            {
                _kept[root] = (media, at);
            }
        }

        return read;
    }

    private static bool Fresh((DriveMedia Media, DateTimeOffset At) kept, DateTimeOffset now) =>
        now - kept.At < (kept.Media == DriveMedia.Unknown ? UnknownFor : KnownFor);
}
