using BedrockSentry.Core.Updates;

namespace BedrockSentry.Core.Configuration;

/// <summary>Which release channel the wrapper tracks for its own updates.</summary>
/// <remarks>
/// <para><strong>Renamed from <c>Prod</c> on 2026-09-15, and the numeric values are
/// unchanged on purpose.</strong> One word was doing four jobs — a git branch, this
/// channel, the release tag <c>channel-prod</c>, and a config value — and "prod"
/// overpromises for a project that is not production-ready and says so in its own
/// README. A channel is a contract with whoever selected it; a branch is repository
/// housekeeping. They are separate now.</para>
///
/// <para>There is deliberately <strong>no <c>Prod</c> alias</strong>. Two names for one
/// thing is this codebase's recurring defect, and an alias would keep the old spelling
/// alive in config files forever. What replaces it is
/// <see cref="UpdateChannelSetting.Parse"/>, which turns an unrecognised value into
/// <see cref="Stable"/> and says so, rather than leaving the binder to throw.</para>
/// </remarks>
public enum UpdateChannel
{
    /// <summary>Tested, tagged releases.</summary>
    Stable = 0,

    /// <summary>Active development — latest in-progress build.</summary>
    Build = 1,
}

/// <summary>
/// Reads <c>Sentry:Update:Channel</c> from whatever is actually in the config file.
/// </summary>
/// <remarks>
/// <para><strong>This exists because renaming an enum member breaks every config that
/// names the old one.</strong> The configuration binder parses an enum from a string by
/// name, and a name no member carries makes it <em>throw</em> — so the shipped default
/// of <c>"Channel": "Prod"</c>, sitting in every install written before 2026-09-15,
/// would stop the service from starting after an update that renamed it. A wrapper that
/// will not start is not a cosmetic failure: the health gate rolls the build back and
/// the operator is left reading a stack trace about an enum.</para>
///
/// <para><strong>Unknown falls back to <see cref="UpdateChannel.Stable"/>, not to
/// Build.</strong> If the wrapper cannot tell which channel was asked for, the safe
/// answer is the tested one — guessing Build would move an install onto in-progress
/// releases on the strength of a typo.</para>
///
/// <para>It reports <em>whether</em> it understood separately from <em>what it
/// chose</em>, so the caller can log the difference. A value silently corrected is a
/// value nobody fixes: <c>D55</c>'s rule that unknown must not be reported as a
/// specific state, applied to a setting rather than a status.</para>
/// </remarks>
public static class UpdateChannelSetting
{
    /// <summary>The key this is read from, so callers do not spell it twice.</summary>
    public const string Key = UpdateOptions.SectionName + ":Channel";

    /// <summary>
    /// Parses a raw configured value. Returns false when the value was present and
    /// meant nothing, which is the only case worth telling anybody about.
    /// </summary>
    public static bool Parse(string? raw, out UpdateChannel channel)
    {
        channel = UpdateChannel.Stable;

        // Absent is not a mistake — it is the default, and the default is Stable.
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        // Case-insensitively, because a hand-edited config says "stable" as often as
        // "Stable" and refusing over capitalisation would be its own small defect.
        if (Enum.TryParse(raw.Trim(), ignoreCase: true, out UpdateChannel parsed)
            && Enum.IsDefined(parsed))
        {
            channel = parsed;
            return true;
        }

        return false;
    }

    /// <summary>What to tell the operator about a value that meant nothing.</summary>
    /// <remarks>
    /// Names the old spelling explicitly. Anyone hitting this is almost certainly
    /// carrying <c>Prod</c> from an install predating the rename, and a message that
    /// says only "unrecognised" leaves them guessing at what to type.
    /// </remarks>
    public static string Unrecognised(string? raw) =>
        $"'{raw}' is not a channel this wrapper knows, so it is using "
        + $"{UpdateChannel.Stable} until {Key} is corrected. Valid values are "
        + $"{UpdateChannel.Stable} and {UpdateChannel.Build}. "
        + $"{UpdateChannel.Stable} was called Prod before 2026-09-15.";
}

/// <summary>How aggressively the wrapper applies updates to itself.</summary>
public enum UpdateMode
{
    /// <summary>Check, download, and apply without asking.</summary>
    Automatic = 0,

    /// <summary>Check and surface a button in the UI, but never apply unprompted.</summary>
    Notify = 1,

    /// <summary>
    /// Never check on a schedule. A manual check from the Settings page still
    /// works, and never applies.
    /// </summary>
    /// <remarks>
    /// This sentence was true of the Bedrock feed and false of the wrapper's own
    /// for as long as it has been written here: there was no manual wrapper check
    /// to stay available (<c>T163</c>). <c>D60</c> resolved it by making the
    /// comment true rather than deleting it — never checking on a schedule is a
    /// different statement from refusing to answer a person, and the person has
    /// usually just changed the channel or watched a release go out.
    /// </remarks>
    Disabled = 2,
}

/// <summary>
/// How each <see cref="UpdateMode"/> reads on a page.
/// </summary>
/// <remarks>
/// <para>Here rather than in the two pages that show it. The Settings page already
/// had a phrase per mode; the Status page rendered the bare enum, so the same setting
/// read <em>"when told"</em> on one page and <em>"Notify"</em> on the other in the same
/// session — one of them not English. This codebase's recurring defect in its mildest
/// form; filed as the <c>T126</c> shape.</para>
///
/// <para><strong>The catch-all says it does not know, rather than guessing.</strong>
/// Settings used <c>_ => "never — not checking"</c>, so a mode added later would have
/// been labelled as disabled — the wrong answer, silently, on the page people go to in
/// order to find out what the wrapper will do. It is reachable without a code change
/// too: the config binder will take <c>"Mode": 7</c> and hand over a value no member
/// names. So the fallback names the number instead, which is <c>D55</c>'s rule that
/// <em>unknown</em> must not be reported as a specific state. A <c>throw</c> was the
/// alternative and is worse here: it would take the Settings page down over a
/// mistyped digit.</para>
/// </remarks>
public static class UpdateModeText
{
    /// <summary>What the wrapper will do with an update it finds.</summary>
    /// <param name="mode">The configured mode.</param>
    /// <param name="ledger">
    /// The update ledger, so an Automatic mode that repeated failures have paused does not
    /// read as applying (<c>D101</c>).
    /// </param>
    public static string Applying(UpdateMode mode, UpdateLedger? ledger) => mode switch
    {
        UpdateMode.Automatic => Paused(mode, ledger) is null
            ? "automatically"
            : "automatically, but paused after repeated failed updates",
        UpdateMode.Notify => "when told",
        UpdateMode.Disabled => "never — not checking",
        _ => $"unrecognised setting ({(int)mode}) — check Sentry:Update:Mode",
    };

    /// <summary>
    /// Why automatic updates are held, or null when nothing holds them (<c>D101</c>).
    /// </summary>
    /// <remarks>
    /// Only in <see cref="UpdateMode.Automatic"/>: <see cref="UpdateLedger.CanAttempt"/> is
    /// asked on the automatic path alone, so in any other mode the pause holds nothing back.
    /// </remarks>
    public static string? Paused(UpdateMode mode, UpdateLedger? ledger) =>
        mode is UpdateMode.Automatic && ledger is { AutoUpdateDisabled: true }
            ? "Repeated updates failed and were rolled back, so none is applied automatically until "
              + "automatic updates are resumed. A build that failed stays blocked."
            : null;
}

/// <summary>
/// Wrapper self-update settings, bound from <c>Sentry:Update</c>.
/// </summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Sentry:Update";

    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;

    public UpdateMode Mode { get; set; } = UpdateMode.Notify;

    /// <summary>How often to check the channel feed for a newer build.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How much warning players get before the wrapper updates itself and takes
    /// every server with it.
    /// </summary>
    /// <remarks>
    /// Install-wide, unlike a Bedrock build's per-instance delay, because this
    /// outage is not per instance — the Windows Service stops and every server on
    /// the box goes down together. One number for one event.
    ///
    /// A minute by default. A wrapper update is quick — the service stops, files
    /// are swapped, the service starts and the servers come back — so this is
    /// notice rather than a window to plan around, which is what the fifteen
    /// minutes of a scheduled restart is for. Zero stops without warning, and is
    /// the right answer for an install nobody plays on.
    ///
    /// Whatever this says, the countdown runs <em>before</em> the updater helper
    /// is launched, so it is never racing the service control manager's patience
    /// (<c>docs/design.md</c> § 5.2). That is the whole reason it can be longer than the
    /// five seconds a service stop allows.
    /// </remarks>
    public TimeSpan PlayerWarning { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Where the channel manifest lives. <c>{channel}</c> is replaced with
    /// <c>stable</c> or <c>build</c>.
    /// </summary>
    /// <remarks>
    /// <para><strong>Lowercase, and that is the substitution rather than a spelling
    /// choice here.</strong> <c>UpdateClient</c> writes
    /// <c>Channel.ToString().ToLowerInvariant()</c>, so the member names decide these
    /// two strings and nothing else does. The release tags are <c>channel-stable</c>
    /// and <c>channel-build</c> to match.</para>
    ///
    /// <para>This said <c>prod</c> until <c>T193</c> — ten lines below the enum whose
    /// rename made <c>prod</c> impossible, in the very file the rename was written in.
    /// The recurring class landing inside the fix for a naming defect, which is the
    /// third consecutive fix to carry one. A doc comment naming the values of a
    /// substitution is as load-bearing as the substitution (<c>T145</c>), and the way
    /// to keep it honest is to derive it: <c>UpdateChannelSettingTests</c> now pins
    /// the rendered feed URL for both members, so renaming one and leaving this
    /// sentence alone fails the suite rather than the next reader.</para>
    /// </remarks>
    /// <remarks>
    /// A fixed per-channel address that always describes the newest build, so the
    /// wrapper polls one stable URL instead of enumerating releases and working out
    /// which is newest.
    /// <para>
    /// Configurable rather than hard-coded because where this feed can live is still
    /// an open question (D13): the download has to work anonymously — an installed
    /// service cannot carry a credential — and a private repository serves nothing
    /// anonymously. The default assumes the repository is public by the time
    /// self-update ships.
    /// </para>
    /// </remarks>
    public string FeedUrlTemplate { get; set; } =
        "https://github.com/ntrixter/BedrockSentry/releases/download/channel-{channel}/version.json";

    /// <summary>
    /// Loopback-only plaintext port serving the health endpoint the updater polls.
    /// </summary>
    /// <remarks>
    /// <strong>This is a frozen compatibility contract, not an implementation
    /// detail.</strong> The health check crosses a version boundary: the build
    /// being replaced writes this port into the update plan, and the helper then
    /// polls the build that replaced it. If a future version changes the port or
    /// the path, the probe hits nothing, reports unhealthy, and every update
    /// rolls back. It lives in configuration rather than as a constant so an
    /// operator can move it off a conflicting port without a code change.
    /// </remarks>
    public int HealthPort { get; set; } = 47324;

    /// <summary>Path the health endpoint is served at. Part of the same frozen contract.</summary>
    public const string HealthPath = "/healthz";

    /// <summary>
    /// Path an installer older than the admin pipe posts to before stopping the
    /// service (<c>docs/design.md</c> § 12.4). This build serves nothing there.
    /// </summary>
    /// <remarks>
    /// The drain moved to <see cref="Broker.BrokerNames.AdminPipe"/> (<c>D62</c>).
    /// <c>install.ps1</c> still posts here to a wrapper with no pipe, and a wrapper
    /// that does not serve the path redirects to its login page, which the installer
    /// reads as "no drain available" and carries on (<c>T111</c>). Kept among the
    /// loopback-only paths so nothing is ever served here off the machine.
    /// </remarks>
    public const string DrainPath = "/prepare-shutdown";

    /// <summary>
    /// The URL the updater polls after switching versions.
    /// </summary>
    /// <remarks>
    /// Built here rather than at the call site so the host and the updater cannot
    /// drift apart. It is an IPv4 literal deliberately: <c>localhost</c> may resolve
    /// to <c>::1</c> first, and a bracketed IPv6 literal with a port is not something
    /// every part of the stack can parse — ASP.NET Core's own host matcher throws on
    /// <c>[::1]:47324</c>, and because the routing table is built on the first request
    /// rather than at startup, that surfaces as every route in the application
    /// returning 500 while the service otherwise looks perfectly healthy.
    /// </remarks>
    public string BuildHealthUrl() => $"http://127.0.0.1:{HealthPort}{HealthPath}";

    /// <summary>
    /// How long the helper waits for a freshly-swapped build to report healthy
    /// before rolling back.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. This has to cover a cold start of a ~100 MB
    /// self-contained ASP.NET Core app while Defender scans files it has never
    /// seen before. A timeout that is too tight turns every update into a
    /// rollback — and presents identically to a genuinely broken build, which
    /// makes it expensive to diagnose.
    /// </remarks>
    public TimeSpan HealthTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long the helper waits for the service to stop before escalating to a
    /// forced kill.
    /// </summary>
    /// <remarks>
    /// Invariant: this must exceed the host's own shutdown timeout, which must in
    /// turn exceed the total drain budget of all managed BDS instances
    /// (milestone 4). Violating that ordering means the helper kills a service
    /// that is legitimately mid-save, taking every <c>bedrock_server.exe</c> child
    /// down with it.
    /// </remarks>
    public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Stop checking automatically after this many consecutive failed updates.</summary>
    /// <remarks>
    /// A per-version block list alone does not catch a CI stream shipping three
    /// bad builds in a row; this does.
    /// </remarks>
    public int MaxConsecutiveFailures { get; set; } = 3;

    /// <summary>How long to wait after a rollback before trying any update again.</summary>
    public TimeSpan CooldownAfterRollback { get; set; } = TimeSpan.FromHours(24);
}
