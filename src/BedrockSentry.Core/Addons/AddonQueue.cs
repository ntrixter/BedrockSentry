using BedrockSentry.Core.Instances;

namespace BedrockSentry.Core.Addons;

/// <summary>What a queued add-on change does (<c>D85</c>).</summary>
public enum QueuedAddonKind
{
    /// <summary>Switch a pack on at <see cref="QueuedAddonChange.Version"/>.</summary>
    SwitchOn,

    /// <summary>Switch a pack off.</summary>
    SwitchOff,

    /// <summary>Move a switched-on pack to <see cref="QueuedAddonChange.Version"/>.</summary>
    ChangeVersion,

    /// <summary>Put one list's packs in <see cref="QueuedAddonChange.Order"/>.</summary>
    Arrange,

    /// <summary>Write one pack's <see cref="QueuedAddonChange.Settings"/>.</summary>
    Settings,
}

/// <summary>One setting's value as the queue file keeps it.</summary>
/// <param name="Kind"><c>toggle</c>, <c>number</c> or <c>text</c>.</param>
public sealed record QueuedSetting(string Kind, bool? Toggle = null, double? Number = null, string? Text = null)
{
    /// <summary>The value to write, or null for a kind this does not know.</summary>
    public PackSettingValue? ToValue() => Kind switch
    {
        "toggle" when Toggle is { } t => new PackSettingValue.Toggle(t),
        "number" when Number is { } n => new PackSettingValue.Number(n),
        "text" when Text is { } s => new PackSettingValue.Text(s),
        _ => null,
    };

    /// <summary>The value as kept, or null for one the settings file cannot hold.</summary>
    public static QueuedSetting? From(PackSettingValue value) => value switch
    {
        PackSettingValue.Toggle t => new QueuedSetting("toggle", Toggle: t.Value),
        PackSettingValue.Number n => new QueuedSetting("number", Number: n.Value),
        PackSettingValue.Text s => new QueuedSetting("text", Text: s.Value),
        _ => null,
    };
}

/// <summary>
/// One change to a server's add-ons made while it ran, kept until the process has exited
/// (<c>D85</c>).
/// </summary>
/// <param name="Id">Names it, for Discard.</param>
/// <param name="Name">The pack's name, or the list's, for the panel and the console.</param>
/// <param name="Placement">The list it changes.</param>
/// <param name="Uuid">The pack, for every kind but <see cref="QueuedAddonKind.Arrange"/>.</param>
/// <param name="Version">The version to switch on or move to, as a manifest writes it.</param>
/// <param name="Order">For <see cref="QueuedAddonKind.Arrange"/>, the list's packs by uuid, top first.</param>
/// <param name="Settings">For <see cref="QueuedAddonKind.Settings"/>, every value the pack keeps.</param>
/// <param name="Failure">Why the last attempt to apply it was refused; null until one was.</param>
/// <remarks>
/// <para><strong>An intent, not a file.</strong> Each is planned again against the world as it
/// is when it is applied, through the tab's own planner, so an edit made by hand meanwhile is
/// planned around rather than written over.</para>
///
/// <para><strong>Applying one twice changes nothing the first did not</strong>, because the
/// queue is applied both after a stop and before a start (a crash or a kill skips the first)
/// and the two can meet: switching on what is on, off what is off, to the version it is at,
/// into the order it is in, or writing the settings it holds, is each a no-op.</para>
/// </remarks>
public sealed record QueuedAddonChange(
    string Id,
    QueuedAddonKind Kind,
    string Name,
    DateTimeOffset QueuedUtc,
    PackPlacement Placement,
    string? Uuid = null,
    string? Version = null,
    IReadOnlyList<string>? Order = null,
    IReadOnlyDictionary<string, QueuedSetting>? Settings = null,
    string? Failure = null)
{
    /// <summary>
    /// What a later change replaces: a pack's switch or version, a pack's settings, or one
    /// list's order. One queued change per slot, so the queue says what will happen rather
    /// than the history of clicks.
    /// </summary>
    public string Slot => Kind switch
    {
        QueuedAddonKind.SwitchOn or QueuedAddonKind.SwitchOff or QueuedAddonKind.ChangeVersion =>
            $"pack:{Uuid?.ToLowerInvariant()}",
        QueuedAddonKind.Settings => $"settings:{Uuid?.ToLowerInvariant()}",
        QueuedAddonKind.Arrange => $"order:{Placement}",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "No slot is defined for this kind."),
    };

    /// <summary>The panel's and the console's words for it.</summary>
    public string Describe() => Kind switch
    {
        QueuedAddonKind.SwitchOn => $"Switch on {Name} {Version}",
        QueuedAddonKind.SwitchOff => $"Switch off {Name}",
        QueuedAddonKind.ChangeVersion => $"Move {Name} to {Version}",
        QueuedAddonKind.Arrange => $"New load order for {Name}",
        QueuedAddonKind.Settings => $"Settings for {Name}",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "No words are written for this kind."),
    };

    /// <summary>The settings as values to write; unknown kinds are left out.</summary>
    public IReadOnlyDictionary<string, PackSettingValue> SettingValues() =>
        (Settings ?? new Dictionary<string, QueuedSetting>())
            .Select(p => (p.Key, Value: p.Value.ToValue()))
            .Where(p => p.Value is not null)
            .ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);
}

/// <summary>The rules for a server's queue of add-on changes (<c>D85</c>).</summary>
public static class AddonQueue
{
    /// <summary>
    /// Whether a change made now has to wait: whenever a process exists that will rewrite the
    /// world's files when it exits. Starting and Stopping count, as Running does.
    /// </summary>
    /// <remarks>
    /// Backoff and Faulted have no process, so a change then is written at once, and the
    /// next launch reads it.
    /// </remarks>
    public static bool MustWait(InstanceState state) => state switch
    {
        InstanceState.Starting or InstanceState.Running or InstanceState.Stopping => true,
        InstanceState.Stopped or InstanceState.Backoff or InstanceState.Faulted => false,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No rule is written for this state."),
    };

    /// <summary>The queue with <paramref name="change"/> added, replacing any in its slot.</summary>
    /// <remarks>
    /// The new one goes last: it is the latest intent, and the order changes are applied in
    /// is the order they were made in.
    /// </remarks>
    public static IReadOnlyList<QueuedAddonChange> With(IReadOnlyList<QueuedAddonChange> queue, QueuedAddonChange change)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(change);

        return [.. queue.Where(q => q.Slot != change.Slot), change];
    }

    /// <summary>The queue without the change <paramref name="id"/> names.</summary>
    public static IReadOnlyList<QueuedAddonChange> Without(IReadOnlyList<QueuedAddonChange> queue, string id)
    {
        ArgumentNullException.ThrowIfNull(queue);

        return [.. queue.Where(q => q.Id != id)];
    }

    /// <summary>The change queued for a pack's switch or version, or null.</summary>
    public static QueuedAddonChange? ForPack(IReadOnlyList<QueuedAddonChange> queue, string uuid) =>
        queue.FirstOrDefault(q => q.Slot == $"pack:{uuid.ToLowerInvariant()}");

    /// <summary>The settings queued for a pack, or null.</summary>
    public static QueuedAddonChange? SettingsFor(IReadOnlyList<QueuedAddonChange> queue, string uuid) =>
        queue.FirstOrDefault(q => q.Slot == $"settings:{uuid.ToLowerInvariant()}");

    /// <summary>The order queued for a list, or null.</summary>
    public static QueuedAddonChange? OrderFor(IReadOnlyList<QueuedAddonChange> queue, PackPlacement placement) =>
        queue.FirstOrDefault(q => q.Slot == $"order:{placement}");
}

/// <summary>
/// What a change from the Add-ons tab did (<c>D85</c>): three states, because a queued change
/// is not a saved one, and a <c>bool</c> meaning saved would report a waiting edit as applied.
/// </summary>
public enum AddonWriteOutcome
{
    /// <summary>Written to the world now.</summary>
    Written,

    /// <summary>Kept, to be written once the server's process has exited.</summary>
    Queued,

    /// <summary>Not written and not kept; <see cref="AddonWrite.Reason"/> says why.</summary>
    Refused,
}

/// <summary>What a change from the Add-ons tab did, and why when it did nothing.</summary>
public readonly record struct AddonWrite(AddonWriteOutcome Outcome, string? Reason = null)
{
    public static AddonWrite Written => new(AddonWriteOutcome.Written);

    public static AddonWrite Queued => new(AddonWriteOutcome.Queued);

    public static AddonWrite Refused(string reason) => new(AddonWriteOutcome.Refused, reason);
}

/// <summary>What became of one queued change when the queue was applied (<c>D85</c>).</summary>
public enum QueuedChangeOutcome
{
    /// <summary>Written, or already true, and taken off the queue.</summary>
    Applied,

    /// <summary>Refused this time; kept with its reason, shown on the tab, and tried again at the next start.</summary>
    Failed,

    /// <summary>
    /// Its server was removed before it could be applied, so it was thrown away: the next
    /// server given the same id must not inherit a change nobody made to it.
    /// </summary>
    Discarded,
}
