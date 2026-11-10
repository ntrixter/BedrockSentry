namespace BedrockSentry.Core.Backups;

/// <summary>
/// Whether a backup could be opened at all, and what came back when it was
/// (<c>T251</c>).
/// </summary>
/// <remarks>
/// Three states because the world has three and the repairs are different: it
/// opened, this service is not allowed to open it, or it opened and is not usable.
/// The last two were one value — <c>0</c> configuration files — and zero is also
/// what an ordinary backup carrying no configuration returns, so the one that
/// should refuse rendered as the one that is fine.
/// </remarks>
public enum BackupReadState
{
    /// <summary>It opened, and <c>ConfigFiles</c> is a count rather than a default.</summary>
    Read,

    /// <summary>
    /// This service is not allowed to read it. The backup is there and the repair
    /// is an access right, so the sentence must not send somebody looking for a
    /// missing file.
    /// </summary>
    Refused,

    /// <summary>
    /// It is there, access was allowed, and it could not be opened — a truncated
    /// archive, a file something else holds, a folder that is no longer one. A
    /// different repair from <see cref="Refused"/>: no permission change helps,
    /// and the answer is another backup.
    /// </summary>
    Unreadable,
}

/// <summary>
/// What was found inside one backup.
/// </summary>
/// <param name="State">Whether it could be opened.</param>
/// <param name="ConfigFiles">
/// How many of the server's configuration files it carries. Meaningful only on
/// <see cref="BackupReadState.Read"/>, and zero elsewhere — which is exactly the
/// ambiguity this type exists to remove, so read <see cref="CanRestore"/> first
/// rather than treating the count as the answer.
/// </param>
/// <param name="Reason">Windows's own message, where the failure carried one.</param>
/// <param name="AddonFiles">
/// How many of its world files belong to add-ons
/// (<see cref="Addons.WorldAddonFiles"/>). Meaningful only on
/// <see cref="BackupReadState.Read"/>, as <paramref name="ConfigFiles"/> is.
/// </param>
/// <param name="BdsVersion">
/// The Bedrock build its <see cref="NewServerWorld.VersionFile"/> names (<c>D110</c>), or null
/// when it carries none.
/// </param>
/// <param name="Origin">
/// The server its <see cref="NewServerWorld.OriginFile"/> names (<c>D89</c>), or null when it
/// carries none.
/// </param>
public readonly record struct BackupContents(
    BackupReadState State,
    int ConfigFiles = 0,
    string? Reason = null,
    int AddonFiles = 0,
    string? BdsVersion = null,
    BackupOrigin? Origin = null)
{
    /// <summary>It opened, so a restore built from it can go ahead.</summary>
    public bool CanRestore => State is BackupReadState.Read;

    /// <summary>
    /// Whether to offer the configuration tickbox.
    /// </summary>
    /// <remarks>
    /// Separate from <c>ConfigFiles &gt; 0</c> at the call site on purpose: the
    /// panel cannot be reached by the test project, so a predicate written there is
    /// verified on a real page or not at all (<c>T145</c>).
    /// </remarks>
    public bool OffersConfig => CanRestore && ConfigFiles > 0;

    /// <summary>
    /// Whether to offer the add-ons tickbox (<c>D97</c>).
    /// </summary>
    /// <remarks>
    /// Only when the backup carries add-ons. A backup with none has nothing to put
    /// back, and a restore from it keeps the add-ons the server has; the dialog says
    /// so in a sentence rather than with a box that could do nothing.
    /// </remarks>
    public bool OffersAddons => CanRestore && AddonFiles > 0;

    /// <summary>A backup that opened and carries no configuration.</summary>
    /// <remarks>
    /// Named rather than written as <c>new(BackupReadState.Read)</c> at four call
    /// sites, because that spelling is one word away from the thing that was wrong.
    /// </remarks>
    public static BackupContents Empty => new(BackupReadState.Read);

    /// <summary>
    /// The state a failed read leaves behind, or <see langword="null"/> when the
    /// exception is not one a read failure produces.
    /// </summary>
    /// <remarks>
    /// <para><strong>It is the catch filter as well as the mapping</strong>, which
    /// is what keeps this one statement rather than two. A <c>when</c> clause
    /// listing the types and a <c>switch</c> mapping them would be the same rule
    /// written twice, and the one that goes stale is whichever is edited second.
    /// Returning null for anything else leaves that exception to propagate, which
    /// is right: a read failure has a sentence and a bug has a stack.</para>
    ///
    /// <para><strong>And it is here so it can be tested at all.</strong> The build
    /// runs as root, so a rig that denies itself a file and then opens it
    /// <em>succeeds</em> — the <see cref="BackupReadState.Refused"/> arm, which is
    /// the one this whole entry is about, cannot be produced where the code is
    /// written. A pure function over the exception can be, and it is the same
    /// function the real reader uses rather than a double of it (<c>T176</c>,
    /// <c>T174</c>).</para>
    /// </remarks>
    public static BackupContents? From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            UnauthorizedAccessException =>
                new BackupContents(BackupReadState.Refused, Reason: exception.Message),

            IOException or InvalidDataException =>
                new BackupContents(BackupReadState.Unreadable, Reason: exception.Message),

            _ => null,
        };
    }

    /// <summary>
    /// What to say instead of restoring it.
    /// </summary>
    /// <remarks>
    /// <para>The consequence clause has two arms for <see cref="DamagedBackups"/>'s
    /// reason and not because a flag was to hand: restoring in place would have
    /// taken a live server down to find this out, and restoring into a new server
    /// (<c>D40</c>) would have left a server standing with nothing in it. Both are
    /// worth refusing and only one of them is an outage.</para>
    ///
    /// <para>Throws on <see cref="BackupReadState.Read"/> rather than returning
    /// something harmless: a caller that reaches here with a readable backup has a
    /// defect, and a bland sentence would hide it — the same posture
    /// <c>MissingServerText.RefusalReason</c> takes.</para>
    /// </remarks>
    /// <param name="artifactName">The backup's file or folder name.</param>
    /// <param name="asNewServer">
    /// True when this would have been restored into a newly created server rather
    /// than over an existing world.
    /// </param>
    public string Refusal(string artifactName, bool asNewServer = false)
    {
        var cost = asNewServer
            ? "No server has been created."
            : "Nothing has been stopped and this world is untouched.";

        return State switch
        {
            BackupReadState.Refused =>
                $"'{artifactName}' is there and this service is not allowed to read it. "
                + $"{cost} This is an access problem rather than a damaged backup: give "
                + "the account the service runs as read access to the backup folder, then "
                + "try again.",

            BackupReadState.Unreadable =>
                $"'{artifactName}' could not be opened{Because()}. {cost} Nothing here can "
                + "read it, so no permission change will help — pick a backup taken before "
                + "or after it.",

            _ => throw new ArgumentOutOfRangeException(
                nameof(State), State, "A backup that opened has no refusal sentence."),
        };
    }

    /// <summary>
    /// Windows's own message, where there is one to carry.
    /// </summary>
    /// <remarks>
    /// Composed rather than interpolated inline because a state may have nothing to
    /// add, and a sentence rendering <c>" ()"</c> when it does not is the arm that
    /// is right by luck (<c>T187</c>).
    /// </remarks>
    private string Because() =>
        string.IsNullOrWhiteSpace(Reason) ? string.Empty : $" ({Reason})";
}
