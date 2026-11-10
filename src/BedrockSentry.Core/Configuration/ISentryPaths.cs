namespace BedrockSentry.Core.Configuration;

/// <summary>
/// Every mutable path BedrockSentry owns, resolved from a single data root.
/// </summary>
/// <remarks>
/// This exists because of one hard constraint: milestone 3.5 replaces the
/// install directory wholesale when the wrapper updates itself. Anything
/// derived from <c>AppContext.BaseDirectory</c> therefore lives on borrowed
/// time — it survives until the next update and then silently disappears.
/// The failure is delayed and misattributed (an admin gets logged out two
/// updates later and it looks like an auth bug), so the rule is absolute:
/// nothing mutable is ever resolved relative to the running assembly.
/// Everything comes from here, rooted outside the install tree.
/// <para>
/// Keeping it an interface also keeps the platform split from D9 honest —
/// Core stays on plain <c>net10.0</c> and never learns what
/// <c>%ProgramData%</c> is.
/// </para>
/// </remarks>
public interface ISentryPaths
{
    /// <summary>Root of everything that must outlive a wrapper update.</summary>
    string DataRoot { get; }

    /// <summary>Configuration files, including the credential file.</summary>
    string Config { get; }

    /// <summary>TLS certificates and their protected key material.</summary>
    string Certificates { get; }

    /// <summary>ASP.NET Core DataProtection key ring.</summary>
    /// <remarks>
    /// If this ever moves back inside the install tree, every auth cookie and
    /// antiforgery token is invalidated on the next self-update.
    /// </remarks>
    string DataProtectionKeys { get; }

    /// <summary>SQLite player data (milestone 6).</summary>
    string Data { get; }

    /// <summary>Backup artifacts and history, one folder per instance.</summary>
    string Backups { get; }

    /// <summary>Self-update staging, ledger, and crash journal (milestone 3.5).</summary>
    string Update { get; }

    /// <summary>Wrapper logs.</summary>
    string Logs { get; }

    /// <summary>
    /// Per-instance console transcripts, one folder each (<c>docs/design.md</c> § 5.4).
    /// </summary>
    string InstanceLogs { get; }

    /// <summary>
    /// Default root for instance working directories (milestone 4).
    /// </summary>
    /// <remarks>
    /// Only the default. The administrator picks the real root during first-run
    /// setup, and any instance may override its own directory outright — so this
    /// is where instances land if nobody said otherwise, not where they are.
    /// </remarks>
    string Instances { get; }

    /// <summary>
    /// Bedrock server packages awaiting installation into an instance
    /// (<c>D19</c>). Filled by hand or by the BDS updater.
    /// </summary>
    string ServerPackages { get; }

    /// <summary>
    /// The shared add-on library, one folder per pack uuid with a folder per
    /// version under it (<c>D77</c>, <c>D79</c>).
    /// </summary>
    /// <remarks>
    /// Shared by every instance, so it is under the data root rather than
    /// inside any one server: a pack reaches an instance by being copied into
    /// that instance's own folders, and the copy is what survives a re-home or
    /// a move to another machine.
    /// </remarks>
    string AddonLibrary { get; }

    /// <summary>
    /// Console completion's data: the commands each Bedrock version listed, Mojang's
    /// vanilla data, each instance's add-on commands, and the folders of throwaway runs
    /// while they last (<c>D105</c>).
    /// </summary>
    /// <remarks>
    /// Everything here can be rebuilt by running the server again or fetching again, so
    /// nothing in it needs backing up; deleting the folder costs one run per version in
    /// use.
    /// </remarks>
    string Completion { get; }

    /// <summary>Players' faces fetched by XUID, one small PNG each (<c>D47</c>).</summary>
    /// <remarks>
    /// A cache: deleting the folder costs one request per player the next time a
    /// list shows them, and only while faces are switched on.
    /// </remarks>
    string PlayerFaces { get; }

    /// <summary>
    /// Creates any missing directories. Safe to call repeatedly.
    /// </summary>
    void EnsureCreated();
}
