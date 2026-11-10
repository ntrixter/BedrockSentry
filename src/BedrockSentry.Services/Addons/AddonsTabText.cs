using BedrockSentry.Core.Addons;

namespace BedrockSentry.Services.Addons;

/// <summary>The four views of an instance's Add-ons tab.</summary>
public enum AddonsView
{
    /// <summary>On this server: every add-on, switched on or not.</summary>
    List,

    /// <summary>Each add-on's own settings, and the companion's recording options.</summary>
    Settings,

    /// <summary>The order that decides which add-on wins.</summary>
    Order,

    /// <summary>Why any of them is doing nothing.</summary>
    Issues,
}

/// <summary>What the Add-ons tab says that is not about any one add-on.</summary>
/// <remarks>
/// Here rather than in the markup, so a view added later is a switch arm a test
/// walks rather than a sentence nobody wrote (<c>AddonsTabTextTests</c>).
/// </remarks>
public static class AddonsTabText
{
    /// <summary>The note behind the heading's information mark.</summary>
    public const string HeadingNote =
        "Switched-on add-ons are copied into this world's folder, so a world backup carries them. "
        + "Bedrock also loads packs from the server's own folders, which is where the ones it ships with live.";

    /// <summary>Why a change waits while the server has a process (<c>D85</c>), behind the banner.</summary>
    public const string WaitReason =
        "Bedrock rewrites a world's files when it shuts down, so a change written while it runs would be "
        + "undone by its own exit. A change made now is kept and written once the process has exited.";

    /// <summary>On a pack's Remove while the server has a process: a delete is never queued (<c>D83</c>).</summary>
    public const string RemoveWaits = "Stop the server to remove an add-on.";

    /// <summary>Beside a pack's settings when a save of them is queued (<c>D85</c>).</summary>
    public const string SettingsQueued = "Queued. Written once the server has stopped.";

    /// <summary>Under the list of add-ons with settings.</summary>
    public const string SettingsNote =
        "Only add-ons that declare settings are listed. Pack settings are stored with this world, so "
        + "updating an add-on keeps them.";

    /// <summary>After a pack's description, above its settings.</summary>
    public const string SettingsApply = "Changes here take effect the next time the world loads.";

    /// <summary>The auto-update switch above the list (<c>D112</c>).</summary>
    public const string AutoUpdateLabel = "Keep add-ons up to date";

    /// <summary>Under the auto-update switch.</summary>
    public const string AutoUpdateDetail =
        "Each time this server starts, switched-on add-ons move to the newest version in the library. "
        + "A running server picks it up at its next restart.";

    /// <summary>
    /// Under a switched-on pack held at the version somebody chose, while auto-update is on (<c>D112</c>).
    /// </summary>
    public const string HeldNote = "Held at this version. Choose the newest to let it update.";

    /// <summary>Above the two load order lists.</summary>
    public const string OrderNote =
        "When two switched-on add-ons of the same type change the same thing, the one higher in its list "
        + "wins. Each type is its own list.";

    /// <summary>
    /// The banner over a view while the server has a process (<c>D85</c>), or null for a view
    /// whose changes do not wait: the Issues view's one button writes <c>server.properties</c>,
    /// which BDS reads at its next start, as the Server properties tab does.
    /// </summary>
    public static (string Headline, string Detail)? Waiting(AddonsView view, string server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);

        return view switch
        {
            AddonsView.List => ($"Changes wait until {server} stops.",
                "Switching add-ons on or off and changing a version are queued and written once it has shut down. "
                + RemoveWaits),
            AddonsView.Settings => ($"Pack settings wait until {server} stops.",
                "A save is queued and written once it has shut down. The companion's recording options still apply straight away."),
            AddonsView.Order => ($"Changes to the order wait until {server} stops.",
                "Each move is queued and written once it has shut down."),
            AddonsView.Issues => null,

            _ => throw new ArgumentOutOfRangeException(nameof(view), view, "No banner is written for this view."),
        };
    }

    /// <summary>The heading over a server's queued changes (<c>D85</c>).</summary>
    public static string QueueHeading(int count) =>
        count == 1 ? "1 change waiting" : $"{count} changes waiting";

    /// <summary>Under that heading: when they are written.</summary>
    /// <param name="waits">Whether the server has a process now (<see cref="AddonQueue.MustWait"/>).</param>
    public static string QueueDetail(string server, bool waits) =>
        waits
            ? $"Written once {server} has stopped, or before it next starts."
            : $"Tried again before {server} next starts.";

    /// <summary>The badge under a pack's name while a change to it waits; the change itself is its tooltip.</summary>
    public const string RowQueued = "Waiting";

    /// <summary>A queued change that could not be applied, and why.</summary>
    public static string Failed(string reason) => $"Not applied: {reason}";

    /// <summary>
    /// Why nothing more can be queued: the queue file is there and could not be read, and
    /// writing over it would discard changes somebody is waiting to see applied.
    /// </summary>
    public static string QueueUnreadable(string reason) =>
        $"The changes waiting for this server to stop could not be read, so nothing more can be queued: {reason}";

    /// <summary>
    /// The letters on a pack's tile when it has no icon: the first letter of each
    /// of its first two words, or its first two characters when it is one word.
    /// </summary>
    /// <remarks>
    /// Words are split at spaces only, so a name like <c>BTRP-432378</c> is one word
    /// and reads <c>BT</c> rather than a letter and a digit.
    /// </remarks>
    public static string Initials(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var words = name
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string([.. w.Where(char.IsLetterOrDigit)]))
            .Where(w => w.Length > 0)
            .ToList();

        var letters = words.Count switch
        {
            0 => "?",
            1 => words[0].Length > 1 ? words[0][..2] : words[0],
            _ => $"{words[0][0]}{words[1][0]}",
        };

        return letters.ToUpperInvariant();
    }
}
