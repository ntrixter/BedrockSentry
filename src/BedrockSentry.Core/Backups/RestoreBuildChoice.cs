using BedrockSentry.Core.Packages;

namespace BedrockSentry.Core.Backups;

/// <summary>What the wrapper knows about the Bedrock build an instance is on.</summary>
/// <param name="Version">As the server itself reported it.</param>
/// <param name="WasAnUpdate">
/// True when this build replaced a different one. A first sighting is an
/// observation rather than an update — the wrapper started watching, the server
/// did not change — and reading the two the same way is what would make
/// <see cref="RestoreBuildState.MatchedAfterAnUpdate"/> fire on every instance
/// whose first backup predates the wrapper noticing it.
/// </param>
/// <param name="FirstSeenUtc">When the wrapper first saw this build.</param>
public readonly record struct RecordedBuild(
    string Version, bool WasAnUpdate, DateTimeOffset FirstSeenUtc);

/// <summary>
/// What the wrapper knows about the Bedrock build a backup was taken on, for a
/// restore-as-a-new-server (<c>D40</c>, <c>D41</c>).
/// </summary>
/// <remarks>
/// <para>Five states rather than a nullable file name, because the four that
/// offer no build of the backup's own are not one state: an empty package store makes the whole
/// operation impossible, a build nobody recorded is a gap in what the wrapper
/// knows, and a build it knows but has no package for is a gap in what the
/// operator has downloaded. Those want three different sentences and one of them
/// wants the button turned off.</para>
///
/// <para><strong>The newest build in the store is picked</strong>, whatever these say
/// (<c>D41</c>, changed by the operator 2026-10-16): most copies want today's Bedrock, and
/// the backup's own build is often not in the store. The backup's build is an option the
/// dialog offers when the store has it, and named when it does not.</para>
/// </remarks>
public enum RestoreBuildState
{
    /// <summary>Nothing in the package store, so no new server can be made at all.</summary>
    NoPackages,

    /// <summary>Nothing records the build the backup was taken on.</summary>
    Unknown,

    /// <summary>A build is recorded and the store has no package for it.</summary>
    NotInTheStore,

    /// <summary>In the store, and the instance was already on it when the backup was taken.</summary>
    Matched,

    /// <summary>
    /// The instance's build, which it was updated onto <em>after</em> this backup was
    /// taken — so the world in the backup was last opened by an older build nothing recorded.
    /// </summary>
    MatchedAfterAnUpdate,
}

/// <summary>Which package a restore-as-a-new-server picks and offers, and why.</summary>
/// <param name="PackageFileName">The package of the build <paramref name="State"/> is about, when the store has it.</param>
/// <param name="NewestFileName">The newest package in the store, which is picked (<c>D41</c>).</param>
/// <param name="NewestVersion">Its version as the picker shows it.</param>
/// <param name="FromBackup">
/// True when <paramref name="RecordedVersion"/> is the build the backup itself names
/// (<c>D89</c>, the file is <c>D110</c>'s) rather than the source instance's version log.
/// </param>
public readonly record struct RestoreBuildChoice(
    RestoreBuildState State,
    string? PackageFileName = null,
    string? RecordedVersion = null,
    bool FromBackup = false,
    string? NewestFileName = null,
    string? NewestVersion = null)
{
    /// <summary>The package the dialog starts on: the newest in the store.</summary>
    public string? Preselected => NewestFileName;

    /// <summary>
    /// The package of the build this backup was taken on, offered as an option when the store
    /// has it and it is not already picked; null otherwise.
    /// </summary>
    /// <remarks>
    /// Never for <see cref="RestoreBuildState.MatchedAfterAnUpdate"/>: that build is the one the
    /// instance moved onto after the backup, not the backup's.
    /// </remarks>
    public string? OwnBuild =>
        State == RestoreBuildState.Matched && PackageFileName != NewestFileName ? PackageFileName : null;

    /// <summary>Whether a new server can be made at all.</summary>
    public bool CanCreate => State is not RestoreBuildState.NoPackages;

    /// <summary>Whether the sentence is said in the warning accent: only when nothing can be made.</summary>
    /// <remarks>
    /// Here rather than in the page, because the page cannot be reached by the suite at all
    /// (<c>T126</c>, <c>T136</c>). Picking the newest build is the ordinary case, not a warning.
    /// </remarks>
    public bool Unsettled => State is RestoreBuildState.NoPackages;

    /// <summary>
    /// The choice for one backup of one instance.
    /// </summary>
    /// <param name="packages">The package store's own list.</param>
    /// <param name="recorded">What the version log holds, or null when it holds nothing.</param>
    /// <param name="backupTakenUtc">When the backup being restored was taken.</param>
    /// <param name="stamped">
    /// The build the backup names in its own <c>bds_version.txt</c>, or null when it names
    /// none (<c>D89</c>).
    /// </param>
    /// <remarks>
    /// <para><strong>The backup's own build first.</strong> It was written when the world was
    /// saved, where the version log is read now, so it cannot be overtaken by an update since:
    /// it lands in <see cref="RestoreBuildState.Matched"/> or
    /// <see cref="RestoreBuildState.NotInTheStore"/>, never
    /// <see cref="RestoreBuildState.MatchedAfterAnUpdate"/>. It is still a claim: a server
    /// updated by hand and not restarted reported the old build.</para>
    ///
    /// <para>A backup that names none, every one taken before <c>D110</c> among them, is read
    /// from the log as before. No state refuses for the lack of one.</para>
    /// </remarks>
    public static RestoreBuildChoice For(
        IReadOnlyList<ServerPackage> packages,
        RecordedBuild? recorded,
        DateTimeOffset backupTakenUtc,
        string? stamped = null)
    {
        if (packages.Count == 0)
        {
            return new RestoreBuildChoice(RestoreBuildState.NoPackages);
        }

        var newest = Newest(packages);

        return About(packages, recorded, backupTakenUtc, stamped) with
        {
            NewestFileName = newest.FileName,
            NewestVersion = newest.Label,
        };
    }

    /// <summary>
    /// The newest Release build in the store, or the newest of any channel when it holds only
    /// Previews; a package whose name gives no version is picked only when none does.
    /// </summary>
    public static ServerPackage Newest(IReadOnlyList<ServerPackage> packages)
    {
        ArgumentOutOfRangeException.ThrowIfZero(packages.Count);

        var versioned = packages.Where(p => p.Version is not null).ToList();

        return versioned.Where(p => p.Channel != BdsChannel.Preview).MaxBy(p => p.Version)
            ?? versioned.MaxBy(p => p.Version)
            ?? packages[0];
    }

    private static RestoreBuildChoice About(
        IReadOnlyList<ServerPackage> packages,
        RecordedBuild? recorded,
        DateTimeOffset backupTakenUtc,
        string? stamped)
    {
        if (!string.IsNullOrWhiteSpace(stamped))
        {
            return Match(packages, stamped) is { } own
                ? new RestoreBuildChoice(RestoreBuildState.Matched, own.FileName, stamped, FromBackup: true)
                : new RestoreBuildChoice(RestoreBuildState.NotInTheStore, null, stamped, FromBackup: true);
        }

        if (recorded is not { } build || string.IsNullOrWhiteSpace(build.Version))
        {
            return new RestoreBuildChoice(RestoreBuildState.Unknown);
        }

        if (Match(packages, build.Version) is not { } package)
        {
            return new RestoreBuildChoice(
                RestoreBuildState.NotInTheStore, PackageFileName: null, build.Version);
        }

        var updatedSince = build.WasAnUpdate && build.FirstSeenUtc > backupTakenUtc;

        return new RestoreBuildChoice(
            updatedSince ? RestoreBuildState.MatchedAfterAnUpdate : RestoreBuildState.Matched,
            package.FileName,
            build.Version);
    }

    /// <summary>
    /// The package whose build the instance reported, or null.
    /// </summary>
    /// <remarks>
    /// The spelling first and the parsed number second, which is <c>D53</c>'s rule
    /// rather than a preference. A preview package displays as
    /// <c>1.26.60.21 (preview)</c> while its four numbers equal a stable build's,
    /// so comparing numbers alone can hand back the wrong one of two packages that
    /// are genuinely different servers. An exact spelling cannot. Neither matching
    /// is a guess about what the server is: a build this cannot parse yields no
    /// match rather than a near one, because the failure mode of a clever version
    /// parser here is a panel that quietly picks the wrong build (<c>T146</c>).
    /// </remarks>
    private static ServerPackage? Match(IReadOnlyList<ServerPackage> packages, string text)
    {
        var exact = packages.FirstOrDefault(
            p => string.Equals(p.VersionText, text, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return exact;
        }

        return Version.TryParse(text, out var parsed)
            ? packages.FirstOrDefault(p => p.Version == parsed)
            : null;
    }

    /// <summary>
    /// What the dialog says about the build, in one place so the page and a test
    /// read the same sentence.
    /// </summary>
    /// <remarks>
    /// Every arm that can make a server names the consequence: <strong>opening a world on a
    /// newer Bedrock build upgrades it, and that cannot be undone.</strong> The newest build
    /// is picked, so the sentence says which build the backup was taken on whenever that is
    /// known, and how to get it when the store lacks it.
    /// </remarks>
    /// <param name="chosen">
    /// The package the picker shows now, when somebody has changed it; null for the one picked.
    /// </param>
    public string Explanation(string serverName, string packageDirectory, string? chosen = null)
    {
        if (State == RestoreBuildState.NoPackages)
        {
            return "There is no Bedrock server in the package store, so there is nothing to "
                + $"build a copy out of. Put a Bedrock server zip in {packageDirectory} — the "
                + "one you downloaded from Mojang, unchanged — and this will be offered.";
        }

        if (OwnBuild is { } own && chosen == own)
        {
            return $"Bedrock {RecordedVersion} is picked, the build this backup was taken on, so the world "
                + "is not upgraded.";
        }

        // Another build chosen by hand: what is known about the backup's, without claiming the
        // newest is the one picked.
        var picked = chosen is null || chosen == NewestFileName
            ? $"Bedrock {NewestVersion ?? "(the newest)"} is picked, the newest in the package store."
            : chosen.Length == 0 ? "No build is picked." : "Another build is picked.";
        const string Upgrade = " Opening a world on a newer build upgrades it, and that cannot be undone.";

        return State switch
        {
            RestoreBuildState.Unknown =>
                $"{picked} Nothing here records which build {serverName} was on when this backup was taken."
                + Upgrade,

            RestoreBuildState.NotInTheStore when FromBackup =>
                $"{picked} This backup was taken on Bedrock {RecordedVersion}, which the package store does "
                + $"not have; to restore on that build, put its zip in {packageDirectory} and open this again."
                + Upgrade,

            RestoreBuildState.NotInTheStore =>
                $"{picked} {serverName} is on Bedrock {RecordedVersion}, which the package store does not "
                + $"have; to restore on that build, put its zip in {packageDirectory} and open this again."
                + Upgrade,

            RestoreBuildState.Matched when OwnBuild is null =>
                $"{picked} It is also the build this backup was taken on, so the world is not upgraded.",

            RestoreBuildState.Matched when FromBackup =>
                $"{picked} This backup was taken on Bedrock {RecordedVersion}; use that build to restore "
                + "the world as it was." + Upgrade,

            RestoreBuildState.Matched =>
                $"{picked} {serverName} was on Bedrock {RecordedVersion} when this backup was taken; use "
                + "that build to restore the world as it was." + Upgrade,

            RestoreBuildState.MatchedAfterAnUpdate =>
                $"{picked} {serverName} was updated onto Bedrock {RecordedVersion} after this backup was "
                + "taken, so this world was last opened by an older build that nothing here recorded."
                + Upgrade,

            _ => throw new ArgumentOutOfRangeException(nameof(State), State, "Unhandled build state."),
        };
    }
}
