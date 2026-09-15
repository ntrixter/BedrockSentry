using System.Collections.Immutable;

namespace BedrockSentry.Core.Instances;

/// <summary>How much of a reserved directory is off limits.</summary>
public enum ReservedScope
{
    /// <summary>The folder and everything beneath it.</summary>
    Subtree,

    /// <summary>Exactly this folder. What is inside it is somebody else's business.</summary>
    FolderItself,

    /// <summary>
    /// This folder and each folder one level down, and nothing deeper.
    /// </summary>
    /// <remarks>
    /// For the container that holds every account's own folder. Refusing the whole
    /// subtree would forbid <c>C:\Users\Alice\Documents\Servers</c>, which is an
    /// ordinary place to keep a server — blocking a reasonable setup to prevent an
    /// unlikely one.
    /// </remarks>
    FolderAndItsImmediateChildren,
}

/// <summary>One directory a server may not live in, and the phrase that says why.</summary>
/// <param name="Path">
/// An absolute Windows path. Never empty — <see cref="ReservedSystemDirectories.Resolve"/>
/// drops rules it could not resolve rather than carrying a blank one.
/// </param>
/// <param name="Rule">
/// What this folder <em>is</em>, in the words the refusal uses: "the Windows
/// directory", "where Windows installs applications". The decision requires a
/// refusal to name the rule it broke, and this is that name.
/// </param>
public sealed record ReservedSystemDirectory(string Path, ReservedScope Scope, string Rule);

/// <summary>
/// The directories a server's working directory may never be (<c>D43</c>, <c>D52</c>).
/// </summary>
/// <remarks>
/// <para><strong>Rules as data, matched by a pure function</strong> — the
/// <see cref="Diagnostics.LoopbackOnlyPaths"/> shape, and for the same reason: a
/// path rule stated inside a method the test project can only reach through a real
/// filesystem is a rule tested on the happy path and nowhere else.</para>
///
/// <para><strong>Resolved at runtime, never written down.</strong> The system drive
/// is not always <c>C:</c> and Program Files is redirectable per machine, so a list
/// of literals is exactly the staleness this exists to avoid. <see cref="Resolve"/>
/// is separated from <see cref="Refuse"/> so the matching half can be tested off
/// Windows, where <c>Environment.GetFolderPath</c> answers with empty strings.</para>
///
/// <para><strong>The matcher does its own separator and case handling, and uses no
/// <c>Path</c> API at all.</strong> These are Windows paths whether or not the host is
/// one, so <c>Path.Combine</c>, <c>Path.GetFullPath</c> or
/// <c>Path.TrimEndingDirectorySeparator</c> in here would make the Linux suite assert
/// semantics the product does not have — and quietly:
/// <c>Path.TrimEndingDirectorySeparator(@"C:\Users\Alice\")</c> returns the string
/// <em>unchanged</em> on Linux, so a trailing-separator case would pass while checking
/// nothing.</para>
///
/// <para><strong>Both <c>'\'</c> and <c>'/'</c> count as separators</strong>, because
/// Windows accepts both: <c>C:/Windows/System32</c> and <c>//fileserver/share</c> are
/// paths Windows resolves, and a guard that only understood backslashes would refuse
/// the first spelling of each and wave the second through. Matching one separator too
/// many is the cheap direction here — <c>C:/Windows</c> is not a folder anybody has a
/// use for — and matching one too few is a hole.</para>
///
/// <para><strong>There is no carve-out branch for the wrapper's own instances
/// root.</strong> <c>&lt;dataRoot&gt;\instances\&lt;id&gt;</c> is allowed because the
/// ProgramData rule is <see cref="ReservedScope.FolderItself"/>, and a moved root is
/// allowed for the same reason. An explicit exception would be dead code today, and
/// tomorrow it would let <c>C:\Windows\instances\x</c> through. Its enforcement is a
/// test, not a branch.</para>
/// </remarks>
public static class ReservedSystemDirectories
{
    private const StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

    private static bool IsSeparator(char c) => c is '\\' or '/';

    /// <summary>
    /// One separator spelling for comparison. Never for display — every message
    /// quotes the path as it was typed.
    /// </summary>
    /// <remarks>
    /// Comparing separator-<em>agnostically</em> rather than merely accepting either
    /// character at a boundary. The first attempt did the latter, and
    /// <c>"C:/Windows/x".StartsWith(@"C:\Windows")</c> is false, so every rule went on
    /// matching backslashes only and the tests said so.
    /// </remarks>
    private static string Normalise(string path) => path.Replace('/', '\\');

    /// <summary>
    /// Folder names refused as the first segment beneath any volume root, on every
    /// volume.
    /// </summary>
    /// <remarks>
    /// A name rule rather than resolved paths on purpose. Turning these into paths
    /// would mean enumerating <c>DriveInfo.GetDrives()</c>, which goes stale the
    /// moment a drive is attached and which blocks for the connect timeout on an
    /// unreachable network drive — on the UI thread, since the panel is the service.
    /// One name covers every volume, present and future.
    ///
    /// <para>Only the first segment: <c>D:\srv\System Volume Information</c> is a
    /// folder somebody named themselves, and theirs.</para>
    /// </remarks>
    public static readonly ImmutableArray<string> VolumeFolders =
        ["$Recycle.Bin", "System Volume Information"];

    /// <summary>The rules for this machine. Empty off Windows.</summary>
    public static ImmutableArray<ReservedSystemDirectory> All { get; } = Resolve();

    /// <summary>
    /// Where every account's own folder lives on this machine, or null when it cannot
    /// be resolved (always off Windows).
    /// </summary>
    /// <remarks>
    /// Public because a second place in <c>Services</c> needs this answer — the
    /// warning about running a server out of somebody's profile. Two spellings of one
    /// path question is the defect this codebase keeps producing; one lookup with two
    /// readers is not.
    /// </remarks>
    public static string? AccountsFolder { get; } =
        OperatingSystem.IsWindows() ? UsersContainer() : null;

    /// <summary>
    /// Reads the rules off this machine. Public so the resolution itself can be
    /// asserted rather than only its effects.
    /// </summary>
    public static ImmutableArray<ReservedSystemDirectory> Resolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // The first of three guards against the arm that can refuse every path on
        // the machine: an empty rule path. Off Windows GetFolderPath answers "",
        // Path.GetPathRoot("") is "", and a subtree rule of "" matched the obvious
        // way matches everything.
        if (string.IsNullOrWhiteSpace(windows))
        {
            return [];
        }

        // The drive Windows is on, rather than %SystemDrive%. One derivation, and it
        // is literally the question being asked.
        var systemDrive = System.IO.Path.GetPathRoot(windows) ?? string.Empty;

        var rules = new List<ReservedSystemDirectory>
        {
            new(windows, ReservedScope.Subtree, "the Windows directory"),
        };

        Add(Environment.SpecialFolder.ProgramFiles, "where Windows installs applications");
        Add(Environment.SpecialFolder.ProgramFilesX86, "where Windows installs applications");

        OnSystemDrive("Windows.old",
            "the previous Windows installation, which Windows deletes on its own schedule");
        OnSystemDrive("PerfLogs", "one of Windows' own folders on the system drive");
        OnSystemDrive("Recovery", "one of Windows' own folders on the system drive");
        OnSystemDrive("Boot", "one of Windows' own folders on the system drive");
        OnSystemDrive("EFI", "one of Windows' own folders on the system drive");

        if (UsersContainer() is { } users)
        {
            rules.Add(new ReservedSystemDirectory(
                users,
                ReservedScope.FolderAndItsImmediateChildren,
                "the folder that holds every account's own folder"));
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            rules.Add(new ReservedSystemDirectory(
                programData,
                ReservedScope.FolderItself,
                "the root every application's data sits under"));
        }

        // The second guard. A rule that is empty, or relative because Path.Combine
        // was handed an empty root, is dropped rather than carried.
        return
        [
            .. rules
                .Where(r => !string.IsNullOrWhiteSpace(r.Path) && IsRooted(r.Path))
                .DistinctBy(r => TrimTrailingSeparator(r.Path), StringComparer.OrdinalIgnoreCase),
        ];

        void Add(Environment.SpecialFolder folder, string rule)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path))
            {
                rules.Add(new ReservedSystemDirectory(path, ReservedScope.Subtree, rule));
            }
        }

        void OnSystemDrive(string name, string rule)
        {
            if (!string.IsNullOrWhiteSpace(systemDrive))
            {
                rules.Add(new ReservedSystemDirectory(
                    systemDrive.TrimEnd('\\') + '\\' + name, ReservedScope.Subtree, rule));
            }
        }
    }

    /// <summary>
    /// Where every account's own folder lives, or null when it cannot be resolved.
    /// </summary>
    /// <remarks>
    /// <para><strong>Deliberately not
    /// <see cref="Environment.SpecialFolder.UserProfile"/>.</strong> This process runs
    /// as LocalSystem, whose profile is
    /// <c>C:\Windows\System32\config\systemprofile</c> — so that route resolves a rule
    /// <em>inside</em> the Windows directory, which looks correct, is already covered
    /// by the Windows rule, and leaves <c>C:\Users</c> guarded by nothing.</para>
    ///
    /// <para>The grandparent of a per-machine known folder answers correctly under
    /// LocalSystem. If neither resolves, the rule is <strong>absent</strong> — never a
    /// fallback to a <c>C:\Users</c> literal, which is the staleness the decision
    /// refuses. The authoritative answer is a registry value; a two-level walk off a
    /// known folder is still a lookup rather than a literal, and the decision names
    /// <c>GetFolderPath</c>.</para>
    /// </remarks>
    private static string? UsersContainer()
    {
        foreach (var folder in new[]
        {
            Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.CommonDocuments,
        })
        {
            var known = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(known))
            {
                continue;
            }

            // <users>\Public\Desktop -> <users>
            var publicFolder = System.IO.Path.GetDirectoryName(known);
            var container = publicFolder is null ? null : System.IO.Path.GetDirectoryName(publicFolder);

            if (!string.IsNullOrWhiteSpace(container))
            {
                return container;
            }
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="full"/> is one of the operating system's own directories,
    /// or null when it is not.
    /// </summary>
    public static string? Refuse(string full, ImmutableArray<ReservedSystemDirectory> rules)
    {
        if (string.IsNullOrWhiteSpace(full))
        {
            return null;
        }

        var shown = TrimTrailingSeparator(full.Trim());
        var probe = Normalise(shown);

        foreach (var rule in rules)
        {
            // The third guard. Even a rule that got this far empty matches nothing.
            if (string.IsNullOrWhiteSpace(rule.Path))
            {
                continue;
            }

            var shownAt = TrimTrailingSeparator(rule.Path);
            var at = Normalise(shownAt);
            if (at.Length == 0)
            {
                continue;
            }

            var isTheFolder = probe.Equals(at, Cmp);

            // Boundary, not prefix: 'C:\Windows Servers' is an ordinary folder.
            var isBeneath = probe.Length > at.Length
                && probe.StartsWith(at, Cmp)
                && probe[at.Length] == '\\';

            switch (rule.Scope)
            {
                case ReservedScope.Subtree when isTheFolder:
                    return $"'{shown}' is {rule.Rule} — a server cannot live there. Pick a folder "
                        + "the operating system does not own, such as 'C:\\ExampleServer' or "
                        + "'D:\\minecraft\\survival'.";

                case ReservedScope.Subtree when isBeneath:
                    return $"'{shown}' is inside '{shownAt}', which is {rule.Rule} — a server cannot "
                        + "live there. Pick a folder the operating system does not own, such as "
                        + "'C:\\ExampleServer' or 'D:\\minecraft\\survival'.";

                case ReservedScope.FolderItself when isTheFolder:
                    return $"'{shown}' is {rule.Rule}, not one application's folder. Pick a folder "
                        + $"inside it, such as '{shownAt}\\Servers'.";

                case ReservedScope.FolderAndItsImmediateChildren when isTheFolder:
                    return $"'{shown}' is {rule.Rule}, not a place for a server. Pick somewhere "
                        + $"inside one of them, such as '{shownAt}\\Alice\\Documents\\Servers'.";

                case ReservedScope.FolderAndItsImmediateChildren when isBeneath
                    && probe.IndexOf('\\', at.Length + 1) < 0:
                    return $"'{shown}' is a user's own folder. Pick somewhere inside it, such as "
                        + $"'{shown}\\Documents\\Servers'.";
            }
        }

        return null;
    }

    /// <summary>Why <paramref name="full"/> is a bare volume root, or null.</summary>
    public static string? RefuseAVolumeRoot(string full)
    {
        if (!IsVolumeRoot(full))
        {
            return null;
        }

        var probe = TrimTrailingSeparator(full.Trim());

        // "is the root of a drive" is word for word what InstanceDirectoryRemoval
        // says, so the create side and the delete side say the same thing about the
        // same rule rather than two sentences that can drift.
        return $"'{probe}' is the root of a drive. A server there spreads its worlds, behaviour "
            + "packs and configuration across the whole volume, and removing it with its files "
            + $"would take the volume with them. Pick a folder on it, such as '{probe}\\ExampleServer'.";
    }

    /// <summary>
    /// Why <paramref name="full"/> is inside one of Windows' per-volume folders, or
    /// null.
    /// </summary>
    public static string? RefuseAVolumeFolder(string full)
    {
        if (string.IsNullOrWhiteSpace(full))
        {
            return null;
        }

        var shown = TrimTrailingSeparator(full.Trim());
        var probe = Normalise(shown);
        var root = DriveRootOf(probe);

        if (root is null)
        {
            return null;
        }

        var rest = probe[root.Length..];
        if (rest.Length == 0)
        {
            return null;
        }

        var separator = rest.IndexOf('\\');
        var first = separator < 0 ? rest : rest[..separator];

        foreach (var name in VolumeFolders)
        {
            if (first.Equals(name, Cmp))
            {
                var what = name == "$Recycle.Bin"
                    ? "the recycle bin"
                    : "where Windows keeps its shadow copies";

                return $"'{shown}' is inside '{root}{name}', which is {what} — a server cannot "
                    + $"live there. Pick an ordinary folder on that drive, such as "
                    + $"'{root}ExampleServer'.";
            }
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="requested"/> is a path form no server may sit on, or null.
    /// </summary>
    /// <remarks>
    /// <para>Takes the <strong>raw</strong> input rather than a normalised path, and
    /// runs before <c>Path.GetFullPath</c>, for two reasons. On Linux
    /// <c>Path.GetFullPath(@"\\fileserver\x")</c> mangles the string, so a test of the
    /// normalised value would have to be Windows-only. And an extended path has to be
    /// caught in its original spelling — see below.</para>
    ///
    /// <para><strong>The extended-path arm is checked before the share arm and is not
    /// cosmetic.</strong> <c>\\?\</c> starts with two backslashes, so it would
    /// otherwise read as a share — and, worse, it <em>bypasses every rule in
    /// <see cref="Refuse"/></em>, because <c>Path.GetFullPath</c> leaves extended
    /// paths alone and <c>\\?\C:\Windows</c> does not equal <c>C:\Windows</c>. One
    /// line, and without it the whole guard has a trivial hole.</para>
    /// </remarks>
    public static string? RefuseAnUnsupportedPathForm(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        var probe = requested.Trim();

        // Form detection on a backslash-only copy; every message quotes what they
        // actually typed. Windows does not accept '/' inside an extended path, so the
        // '?/UNC/' spellings this folds together are refusals of something that was
        // never going to work anyway — over-gating, in the direction the sixth and
        // seventh defects in this codebase say to err.
        var form = probe.Replace('/', '\\');

        if (form.StartsWith(@"\\?\UNC\", Cmp))
        {
            return NetworkShare(probe);
        }

        if (form.StartsWith(@"\\?\", Cmp) || form.StartsWith(@"\\.\", Cmp))
        {
            return $"'{probe}' is an extended-length or device path, which BedrockSentry does not "
                + "accept for a server's folder. Give the ordinary path, like 'C:\\ExampleServer'.";
        }

        if (form.StartsWith(@"\\", Cmp))
        {
            return NetworkShare(probe);
        }

        return null;
    }

    /// <summary>
    /// Why a drive-rooted path is on a drive this service cannot reach, or null
    /// (<c>D52</c>).
    /// </summary>
    /// <param name="networkDrive">
    /// True for <c>DriveType.Network</c>, false for <c>NoRootDirectory</c>. The
    /// caller does the <c>DriveInfo</c> read; this decides what to say about it.
    /// </param>
    /// <remarks>
    /// <para>Separated from the read for the reason the whole class exists: a rule
    /// inside a method that needs a real Windows drive to reach is a rule tested on
    /// a real Windows drive or nowhere. Before this was split out <strong>no test
    /// anywhere pinned either sentence</strong>, so a single manual observation was
    /// the only coverage that switch had. <c>ReservedSystemDirectoryTests</c> pins
    /// both now.</para>
    ///
    /// <para><strong><c>NoRootDirectory</c> is the operative answer and
    /// <c>Network</c> is the backstop, which is the opposite way round from how it
    /// reads.</strong> Drive mappings live in the logon session's device namespace
    /// and this service is LocalSystem, whose session has no <c>Z:</c> — so Windows
    /// answers <c>DRIVE_NO_ROOT_DIR</c> and the service cannot tell "the drive you
    /// mapped in Explorer" from "a letter nobody ever assigned". It does not need
    /// to: both are equally unusable, and the message says both. **Confirmed on a
    /// real box** with <c>Z:</c> genuinely mapped to a real share — the operator's
    /// own session saw <c>DriveType.Network</c> and the service still refused it
    /// with the not-present sentence.</para>
    /// </remarks>
    public static string UnreachableDrive(string full, string root, bool networkDrive) =>
        networkDrive
            ? NetworkShare(full)
            // TrimEnd rather than TrimTrailingSeparator: that one deliberately never
            // trims a bare X:\, which is right for path comparison and wrong here —
            // it would read "on drive Z:\, which this machine does not have". The
            // test for this arm caught it the moment the rule moved into this class.
            : $"'{full}' is on drive {root.TrimEnd('\\', '/')}, which this machine does not "
                + "have. Drives you map in Explorer belong to your own sign-in; this service runs "
                + "as LocalSystem and never sees them. If that drive is a network share, "
                + "BedrockSentry does not support one for a server's folder either — put the "
                + "server on a disk attached to this machine.";

    /// <summary>The sentence for a network share, wherever it was recognised.</summary>
    public static string NetworkShare(string path) =>
        $"'{path}' is on a network share, which BedrockSentry does not support for a server's "
        + "folder. The service runs as LocalSystem and presents the machine account on the "
        + "network rather than your account, so it cannot reliably reach a share — and a share "
        + "cannot be shadow-copied for backups. Put the server on a disk attached to this machine.";

    /// <summary>
    /// Equal to a volume root, in the pure Windows sense or in this host's own.
    /// </summary>
    /// <remarks>
    /// Two arms on purpose. The first lets a test on Linux assert <c>C:\</c> and
    /// <c>D:\</c>, which is the product's real case; the second keeps this host's own
    /// answer, so lifting <c>InstanceDirectoryRemoval</c> onto this does not change
    /// what that method says about <c>/</c> on a Linux test runner.
    /// </remarks>
    public static bool IsVolumeRoot(string? full)
    {
        if (string.IsNullOrWhiteSpace(full))
        {
            return false;
        }

        var probe = full.Trim();

        if (probe.Length is 2 or 3
            && char.IsLetter(probe[0])
            && probe[1] == ':'
            && (probe.Length == 2 || IsSeparator(probe[2])))
        {
            return true;
        }

        var root = System.IO.Path.GetPathRoot(probe);

        return !string.IsNullOrEmpty(root)
            && System.IO.Path.TrimEndingDirectorySeparator(root)
                .Equals(System.IO.Path.TrimEndingDirectorySeparator(probe), HostComparison);
    }

    /// <summary>
    /// Whether <paramref name="full"/> is <paramref name="folder"/> or sits somewhere
    /// inside it. False when either is blank, so an unresolved folder matches nothing.
    /// </summary>
    /// <remarks>
    /// Boundary, not prefix — <c>C:\UsersData</c> is not inside <c>C:\Users</c>. The
    /// same rule <see cref="Refuse"/> applies, exposed because the profile warning in
    /// <c>Services</c> needs to ask it about <see cref="AccountsFolder"/>.
    /// </remarks>
    public static bool IsAtOrUnder(string? full, string? folder)
    {
        if (string.IsNullOrWhiteSpace(full) || string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        var probe = Normalise(TrimTrailingSeparator(full.Trim()));
        var at = Normalise(TrimTrailingSeparator(folder.Trim()));

        if (at.Length == 0)
        {
            return false;
        }

        return probe.Equals(at, Cmp)
            || (probe.Length > at.Length
                && probe.StartsWith(at, Cmp)
                && probe[at.Length] == '\\');
    }

    /// <summary>Every rule in a line, for a message or a disclosure.</summary>
    public static IEnumerable<string> Explain(ImmutableArray<ReservedSystemDirectory> rules)
    {
        foreach (var rule in rules)
        {
            yield return rule.Scope switch
            {
                ReservedScope.Subtree => $"{rule.Path} and everything in it — {rule.Rule}",
                ReservedScope.FolderItself => $"{rule.Path} itself, but not what is under it",
                _ => $"{rule.Path} and each account's folder in it, but nothing deeper",
            };
        }

        yield return "the root of any drive";
        yield return "a network share, and any drive letter this machine does not have";
    }

    private static StringComparison HostComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>"X:\" when this is a drive-rooted Windows path, else null.</summary>
    private static string? DriveRootOf(string probe) =>
        probe.Length >= 3 && char.IsLetter(probe[0]) && probe[1] == ':' && IsSeparator(probe[2])
            ? probe[..3]
            : null;

    private static bool IsRooted(string path) =>
        DriveRootOf(path) is not null
        || (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]));

    /// <summary>
    /// <c>Path.TrimEndingDirectorySeparator</c>'s documented behaviour, written out so
    /// it holds on a Linux test runner too — including that it never trims a bare
    /// <c>X:\</c>.
    /// </summary>
    private static string TrimTrailingSeparator(string path)
    {
        if (path.Length < 2 || !IsSeparator(path[^1]))
        {
            return path;
        }

        if (path.Length == 3 && path[1] == ':')
        {
            return path;
        }

        return path[..^1];
    }
}
