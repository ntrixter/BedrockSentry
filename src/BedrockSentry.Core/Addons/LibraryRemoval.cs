using BedrockSentry.Core.Instances;

namespace BedrockSentry.Core.Addons;

/// <summary>
/// How one server stands with a pack the library is asked to remove (<c>D83</c>).
/// </summary>
/// <param name="InstanceId">The server's id, which the removal acts on.</param>
/// <param name="Server">The server's name, which the sentences use.</param>
/// <param name="Stopped">
/// No process, and none about to start by itself (<see cref="LibraryRemoval.IsStopped"/>).
/// </param>
/// <param name="SwitchedOn">A world list on this server names the pack.</param>
/// <param name="HasCopy">
/// A copy of the pack sits in a folder this wrapper writes to: the world's own pack
/// folders or the server root's. The development folders are the administrator's,
/// and are neither counted nor touched (<c>T279</c>).
/// </param>
public sealed record PackHolder(
    string InstanceId, string Server, bool Stopped, bool SwitchedOn, bool HasCopy)
{
    /// <summary>Whether this server has the pack in any form.</summary>
    public bool Holds => SwitchedOn || HasCopy;
}

/// <summary>What the library's Remove does with one pack (<c>D83</c>).</summary>
public enum LibraryRemovalVerdict
{
    /// <summary>No server has it: it goes when asked.</summary>
    Remove,

    /// <summary>
    /// Stopped servers have it, or a server's add-ons could not be read. It goes only
    /// once the administrator confirms, and then comes off those servers too.
    /// </summary>
    Confirm,

    /// <summary>It stays: the wrapper ships it, or a server that is not stopped has it.</summary>
    Refuse,
}

/// <summary>A library removal, decided before anything is touched.</summary>
/// <param name="Verdict">What happens.</param>
/// <param name="Sentence">
/// The refusal, or the warning a confirmation shows. Null for a plain removal.
/// </param>
/// <param name="RemoveFrom">
/// The servers the pack comes off when the removal is confirmed, by instance id.
/// </param>
public sealed record LibraryRemovalPlan(
    LibraryRemovalVerdict Verdict, string? Sentence, IReadOnlyList<string> RemoveFrom);

/// <summary>Whether a pack may be taken off the add-on library, and what else goes with it.</summary>
public static class LibraryRemoval
{
    /// <summary>
    /// True for a pack the wrapper itself puts on the shelf, which the shelf's
    /// Remove can never take off.
    /// </summary>
    public static bool ShipsWithTheWrapper(string uuid) => CompanionPack.Is(uuid);

    /// <summary>
    /// Whether a server in this state can have its world's files changed.
    /// </summary>
    /// <remarks>
    /// Stopped, or faulted and waiting for a person. Every other state has a
    /// process or will start one by itself, and Bedrock rewrites a world's files
    /// when its process exits, so a change made now would be undone (<c>D85</c>).
    /// </remarks>
    public static bool IsStopped(InstanceState state) =>
        state is InstanceState.Stopped or InstanceState.Faulted;

    /// <summary>
    /// Decides a removal (<c>D83</c>, answered by the operator 2026-09-25).
    /// </summary>
    /// <param name="uuid">The pack's uuid.</param>
    /// <param name="name">What the page calls the pack.</param>
    /// <param name="servers">How every server stands with it.</param>
    /// <param name="unreadable">
    /// Servers whose add-ons could not be read, by name. Whether they have the pack is
    /// not known, and nothing is changed on them.
    /// </param>
    /// <remarks>
    /// <para><strong>The companion pack first, whoever uses it.</strong> It is put
    /// back on the shelf at every start (<c>D93</c>), so a removal would report
    /// success and be undone by the next restart.</para>
    ///
    /// <para><strong>Then any server that is not stopped and has it</strong>,
    /// switched on or not: taking it off that server would be a write to a world
    /// its own exit rewrites.</para>
    ///
    /// <para><strong>Then stopped servers that have it, which need a
    /// confirmation.</strong> The warning names them, because the removal changes
    /// servers nobody is looking at: the pack is switched off there, its copies are
    /// deleted and its history and settings entries go. Leaving those behind would
    /// leave each world naming a pack the library no longer has.</para>
    /// </remarks>
    public static LibraryRemovalPlan Plan(
        string uuid,
        string name,
        IReadOnlyCollection<PackHolder> servers,
        IReadOnlyCollection<string> unreadable)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(unreadable);

        if (ShipsWithTheWrapper(uuid))
        {
            return new LibraryRemovalPlan(
                LibraryRemovalVerdict.Refuse,
                $"{name} is not removed: BedrockSentry ships it and puts it back at every start. "
                + "Switch it off on a server's Add-ons tab instead.",
                []);
        }

        var holders = servers.Where(s => s.Holds).ToList();
        var live = holders.Where(s => !s.Stopped).Select(s => s.Server).ToList();

        if (live.Count > 0)
        {
            return new LibraryRemovalPlan(
                LibraryRemovalVerdict.Refuse,
                $"{name} is not removed: {List(live)} {(live.Count == 1 ? "is" : "are")} running and "
                + $"{(live.Count == 1 ? "has" : "have")} it. Stop {(live.Count == 1 ? "it" : "them")} "
                + "first, because a running server rewrites its world's files when it stops.",
                []);
        }

        if (holders.Count == 0 && unreadable.Count == 0)
        {
            return new LibraryRemovalPlan(LibraryRemovalVerdict.Remove, null, []);
        }

        var parts = new List<string>();

        if (holders.Count > 0)
        {
            var names = holders.Select(s => s.Server).ToList();
            var one = names.Count == 1;

            parts.Add(
                $"{name} is still on {List(names)}, although {(one ? "it is" : "they are")} stopped. "
                + $"Removing it takes it off {(one ? "that server" : "those servers")} too: it is switched "
                + "off, its copies are deleted, and its history and settings entries are cleared.");
        }

        if (unreadable.Count > 0)
        {
            var one = unreadable.Count == 1;

            parts.Add(
                $"The add-ons of {List([.. unreadable])} could not be read, so whether "
                + $"{(one ? "it has" : "they have")} {name} is not known; "
                + $"{(one ? "it is" : "they are")} left as {(one ? "it is" : "they are")}.");
        }

        return new LibraryRemovalPlan(
            LibraryRemovalVerdict.Confirm,
            string.Join(" ", parts),
            holders.Select(s => s.InstanceId).ToList());
    }

    /// <summary>What the library's In use column says about one pack.</summary>
    /// <remarks>
    /// A switched-off copy is named too, because Remove now takes it off that
    /// server, and a column that left it out would be a list the confirmation
    /// contradicts.
    /// </remarks>
    public static string InUse(IReadOnlyCollection<PackHolder> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var on = servers.Where(s => s.SwitchedOn).Select(s => s.Server).ToList();
        var copies = servers.Where(s => !s.SwitchedOn && s.HasCopy).Select(s => s.Server).ToList();

        return (on.Count, copies.Count) switch
        {
            (0, 0) => "Not on any server",
            (_, 0) => List(on),
            (0, _) => $"Switched off on {List(copies)}",
            _ => $"{List(on)}; switched off on {List(copies)}",
        };
    }

    private static string List(IReadOnlyList<string> names) => string.Join(", ", names);
}
