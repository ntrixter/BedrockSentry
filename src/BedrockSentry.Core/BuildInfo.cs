using System.Reflection;

namespace BedrockSentry.Core;

/// <summary>
/// Identity of the running build: version, commit, and release channel.
/// </summary>
/// <remarks>
/// <para>
/// Two different identities matter here and conflating them causes real bugs.
/// </para>
/// <para>
/// <see cref="CommitSha"/> answers <em>"is there an update?"</em>. On the
/// <c>build</c> channel the version prefix never moves — only the CI run number
/// and the commit advance — so comparing version strings would miss every update.
/// </para>
/// <para>
/// <see cref="Identity"/> (<c>version+shortsha</c>) answers <em>"which build is
/// this?"</em>. It names the install directory, keys the update ledger, and is
/// what the health endpoint reports. It has to be unique per build rather than
/// per commit, because re-running CI on the same commit produces a genuinely
/// different artifact that needs its own directory.
/// </para>
/// </remarks>
public static class BuildInfo
{
    private static readonly Lazy<(string Version, string Sha, string Channel, int Epoch, string Branch)> Parsed = new(Load);

    /// <summary>Semantic version, without build metadata. E.g. <c>0.3.0-build.57</c>.</summary>
    public static string Version => Parsed.Value.Version;

    /// <summary>Full 40-character commit SHA, or empty if unavailable.</summary>
    /// <remarks>
    /// Supplied for free by SourceLink, which the SDK enables by default — there
    /// is no package reference to add.
    /// </remarks>
    public static string CommitSha => Parsed.Value.Sha;

    /// <summary>First 7 characters of <see cref="CommitSha"/>.</summary>
    public static string ShortSha =>
        CommitSha.Length >= 7 ? CommitSha[..7] : CommitSha;

    /// <summary>Release channel this build came from: <c>stable</c>, <c>build</c>, or <c>local</c>.</summary>
    public static string Channel => Parsed.Value.Channel;

    /// <summary>
    /// The install epoch this build was cut at (<c>D114</c>): <c>0</c> unless a build
    /// needed a reinstall rather than an update to reach.
    /// </summary>
    public static int InstallEpoch => Parsed.Value.Epoch;

    /// <summary>
    /// The git branch the build came from, e.g. <c>dev</c>, or empty when the build
    /// could not tell (a detached checkout, no git). Display only: nothing compares it.
    /// </summary>
    public static string Branch => Parsed.Value.Branch;

    /// <summary>
    /// What the panel shows for the running build: <c>dev \ 0.3.0-local+123245c</c>,
    /// or the identity alone when the branch is unknown.
    /// </summary>
    public static string Display => FormatDisplay(Branch, Identity);

    /// <summary>Joins a branch and an identity the way <see cref="Display"/> shows them.</summary>
    public static string FormatDisplay(string? branch, string identity) =>
        string.IsNullOrWhiteSpace(branch) ? identity : $"{branch.Trim()} \\ {identity}";

    /// <summary>
    /// Composite build identity, e.g. <c>0.3.0-build.57+f0cb4a6</c>. Used as the
    /// versioned install directory name, the update ledger key, and the value the
    /// health endpoint reports.
    /// </summary>
    public static string Identity =>
        string.IsNullOrEmpty(ShortSha) ? Version : $"{Version}+{ShortSha}";

    /// <summary>True when this build did not come from CI.</summary>
    public static bool IsLocalBuild =>
        string.Equals(Channel, "local", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits an <c>AssemblyInformationalVersion</c> into its version and commit parts.
    /// </summary>
    /// <remarks>
    /// The SDK appends <c>+&lt;40-character sha&gt;</c> when SourceLink is active, which
    /// it is by default. The suffix is only treated as a commit when it actually looks
    /// like one — a hand-set version carrying ordinary SemVer build metadata
    /// (<c>1.0.0+nightly</c>) must not be mistaken for a commit, or the updater would
    /// compare a meaningless string and never see an update.
    /// </remarks>
    public static (string Version, string CommitSha) ParseInformationalVersion(string informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return ("0.0.0", string.Empty);
        }

        // A version can legitimately contain '+' only as this separator, but split on
        // the last one anyway so a malformed value degrades predictably.
        var plus = informational.LastIndexOf('+');

        if (plus <= 0)
        {
            return (informational, string.Empty);
        }

        var version = informational[..plus];
        var sha = informational[(plus + 1)..];

        return sha.Length == 40 && sha.All(Uri.IsHexDigit)
            ? (version, sha)
            : (informational, string.Empty);
    }

    /// <summary>
    /// Reads the <c>InstallEpoch</c> assembly metadata. Missing or unreadable is <c>0</c>,
    /// which every build cut before the value existed was.
    /// </summary>
    public static int ParseInstallEpoch(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var epoch) ? epoch : 0;

    private static (string, string, string, int, string) Load()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;

        var informational = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "0.0.0";

        var (version, sha) = ParseInformationalVersion(informational);

        var channel = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "Channel")?.Value ?? "local";

        var epoch = ParseInstallEpoch(asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "InstallEpoch")?.Value);

        var branch = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "Branch")?.Value ?? string.Empty;

        return (version, sha, channel, epoch, branch);
    }
}
