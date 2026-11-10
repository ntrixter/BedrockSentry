namespace BedrockSentry.Core.Instances;

/// <summary>
/// Whether an instance's folder holds a Bedrock server (<c>T246</c>).
/// </summary>
/// <remarks>
/// <para>Counted rather than guessed, which is <c>T176</c>'s thesis and the same
/// answer <c>ServerProperties.ReadFile</c> reached for a file's contents. The
/// states a <see langword="bool"/> cannot express are the ones that produce a
/// false sentence — see <see cref="MissingServerText"/>.</para>
///
/// <para><strong>The fifth arrived last and is <c>T252</c>.</strong> A folder
/// that does not exist was folded into <see cref="Unknown"/>, because
/// <see cref="DirectoryNotFoundException"/> is an <see cref="IOException"/> and
/// one filter caught both — so a launch refused for a folder that is <em>gone</em>
/// closed with <em>"give the account read access to that folder"</em>, which is
/// advice for the other state entirely. The repairs differ, so the states do.</para>
/// </remarks>
public enum ServerPresenceState
{
    /// <summary>The executable is there.</summary>
    Installed,

    /// <summary>It is not there, and that was established rather than assumed.</summary>
    Missing,

    /// <summary>
    /// The executable IS there and this service is not allowed to see it. Never
    /// quietly <see cref="Missing"/>: absent carries a measurement and this
    /// carries the opposite one (`T210`).
    /// </summary>
    Unreadable,

    /// <summary>
    /// Nothing was established at all, because the folder itself could not be
    /// listed. Separate from <see cref="Unreadable"/> because the sentence
    /// differs: one says a file is there and refused, the other says this service
    /// cannot see into the folder to find out, and an operator fixes them in
    /// different places.
    /// </summary>
    Unknown,

    /// <summary>
    /// The folder is not there at all (<c>T252</c>).
    /// </summary>
    /// <remarks>
    /// Never quietly <see cref="Unknown"/>, for that state's own reason one step
    /// further along: the repair is to create the folder or to point the instance
    /// somewhere else, and no access right will help. It is also not
    /// <see cref="Missing"/>, which says a folder was read and the executable was
    /// not in it — a claim about a folder that was never read.
    /// </remarks>
    NoDirectory,
}

/// <summary>
/// What was found, and Windows's own reason when it refused.
/// </summary>
/// <param name="Reason">
/// What else is known, where anything is: Windows's own message from a refusal,
/// or — from the launch's own check — what the folder holds instead of the server
/// (<c>T250</c>). Null wherever nothing was established, including on an
/// <see cref="ServerPresenceState.Unreadable"/> deduced from the parent
/// directory's listing rather than from a thrown exception: that arm has no
/// message to carry, and inventing one would be a claim nothing made.
/// <c>MissingServerText</c> renders it, so a reason supplied here reaches every
/// sentence built from this.
/// </param>
public sealed record ServerPresence(ServerPresenceState State, string? Reason = null)
{
    /// <summary>Whether a launch could actually happen here.</summary>
    /// <remarks>
    /// <para>Only <see cref="ServerPresenceState.Installed"/> is true, and the
    /// two refused states are false on purpose: <c>File.Exists</c> already
    /// answered no, so this service cannot stat the file, so it cannot start it
    /// either. What the extra states change is what is <em>said</em>, not what is
    /// attempted.</para>
    ///
    /// <para><strong>That is the opposite posture to the orphan and port
    /// pre-flights</strong>, which say in as many words that not being able to ask
    /// is no reason to refuse a start that would otherwise work — and the
    /// difference is what is being asked about. Those are questions about the rest
    /// of the machine, which a launch does not depend on. This is a question about
    /// the instance's own executable, read by the same account that would launch
    /// it, so a refusal here is the launch's own answer arriving early.</para>
    /// </remarks>
    public bool CanLaunch => State == ServerPresenceState.Installed;

    /// <summary>
    /// Whether the panel may offer to unpack a server here (<c>T252</c>).
    /// </summary>
    /// <remarks>
    /// <para>True for the two states where there is provably nothing to overwrite:
    /// a folder that was read and holds no executable, and a folder that is not
    /// there — <c>ServerPackageStore.ExtractAsync</c> creates its target, so the
    /// button works on both. False for the refused states, where one would
    /// overwrite a server that exists and the other cannot see into the folder to
    /// know (<c>T246</c>).</para>
    ///
    /// <para><strong>Here rather than in the panel</strong>, because the markup
    /// decides which panel renders and its code decides whether to load the package
    /// list — two sites, one rule, and the test project cannot reach either of
    /// them. Adding the fifth state meant editing both by hand, which is the
    /// arrangement this codebase keeps paying for (<c>T145</c>).</para>
    /// </remarks>
    public bool OffersInstall =>
        State is ServerPresenceState.Missing or ServerPresenceState.NoDirectory;

    /// <summary>
    /// Whether the answer is that this service could not look (<c>T252</c>).
    /// </summary>
    /// <remarks>
    /// <para>The third of three, and together they partition every state: a server
    /// that can be launched, a folder it is safe to unpack into, and a folder
    /// nothing here was able to read. <c>PresencePartitionIsTotalAndDisjoint</c>
    /// pins that over the enum, so a state added later cannot fall between them.</para>
    ///
    /// <para><strong>The partition was the hazard, not the state.</strong> The
    /// Status tab listed these two by name beside the predicate above, so a new
    /// state would have matched no arm and rendered as an ordinary installed
    /// server — silently, since there is no else to fail. A page the suite cannot
    /// reach must ask rather than list (<c>T145</c>).</para>
    /// </remarks>
    public bool CouldNotBeRead =>
        State is ServerPresenceState.Unreadable or ServerPresenceState.Unknown;

    /// <summary>
    /// The state a failed read of the instance's folder leaves behind, or
    /// <see langword="null"/> when the exception is not one such a read produces
    /// (<c>T252</c>).
    /// </summary>
    /// <remarks>
    /// <para><strong>It is the catch filter as well as the mapping.</strong> Two
    /// readers ask this question — the launch guard by listing the parent, the
    /// launch itself by opening the file — and a <c>when</c> clause listing the
    /// types beside a <c>switch</c> mapping them would be the same rule written
    /// twice in two files. Returning null for anything else leaves that exception
    /// to propagate, which is right: a read failure has a sentence and a bug has a
    /// stack.</para>
    ///
    /// <para><strong>Order is load-bearing and measured.</strong>
    /// <see cref="DirectoryNotFoundException"/> IS an <see cref="IOException"/>, so
    /// a wider arm placed first swallows it — which is exactly how a folder that is
    /// gone came to be reported as one that could not be read.
    /// <see cref="UnauthorizedAccessException"/> is not an
    /// <see cref="IOException"/>, which is why it is named separately rather than
    /// covered by it.</para>
    ///
    /// <para><strong>And it is here so the refused arm can be tested at all.</strong>
    /// The build runs as root, so a rig that denies itself a folder and then lists
    /// it <em>succeeds</em>. A pure function over the exception can be exercised,
    /// and it is the same function both readers use rather than a double of it
    /// (<c>T176</c>, <c>T174</c>).</para>
    /// </remarks>
    public static ServerPresence? From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            // No Reason: .NET's message for this restates the path the sentence
            // already names, so carrying it would render the folder twice.
            DirectoryNotFoundException => new ServerPresence(ServerPresenceState.NoDirectory),

            IOException or UnauthorizedAccessException =>
                new ServerPresence(ServerPresenceState.Unknown, exception.Message),

            _ => null,
        };
    }
}
