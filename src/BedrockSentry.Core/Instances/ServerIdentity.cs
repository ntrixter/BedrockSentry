using BedrockSentry.Core.Diagnostics;

namespace BedrockSentry.Core.Instances;

/// <summary>Whether a server keeps the same identity across restarts.</summary>
public enum ServerIdentityState
{
    /// <summary>The key file is there, so the identity survives a restart.</summary>
    Saved,

    /// <summary>
    /// No key file. BDS generates one in memory at every start, so the identity
    /// changes each time and players are asked to trust the server again.
    /// </summary>
    InMemory,

    /// <summary>
    /// The instance directory could not be read, so nothing is known either way.
    /// </summary>
    Unknown,
}

/// <summary>
/// The server identity key, and what its absence costs (<c>D73</c>).
/// </summary>
/// <remarks>
/// <para><strong>The wrapper is the reason this matters.</strong> A key generated
/// in memory is harmless on a server somebody restarts by hand twice a month. This
/// one restarts servers on a schedule, on a BDS update, on its own update and
/// whenever the watchdog decides — so on a default install it is the thing making
/// every player re-accept trust, repeatedly, from a cause that is invisible from
/// the panel.</para>
///
/// <para><strong>The wrapper does not write the key</strong> (<c>D73</c>, the
/// operator's decision, narrower than this project first recommended).
/// <c>serveridentity save</c> is BDS's own command, so letting the server author
/// its own identity keeps the wrapper out of a key it has no business writing —
/// the reasoning <c>D31</c> already applies to the access files. The button is the
/// keystroke, not the decision.</para>
///
/// <para><strong>The key is already out of every backup, by construction rather
/// than by a rule.</strong> <see cref="Backups.ServerConfigFiles"/> carries four
/// named flat files and nothing else, and its entry reader refuses anything
/// nested, so nothing under <c>keys/</c> can reach an archive even by accident.
/// <c>D73</c>'s opt-in-to-include half is deliberately unbuilt: a private key in a
/// file that gets copied and emailed is a different class of thing from a settings
/// file, and adding that option is a decision rather than a convenience.</para>
/// </remarks>
public static class ServerIdentity
{
    /// <summary>Where BDS keeps it, relative to the instance directory.</summary>
    /// <remarks>
    /// The path BDS names in its own answer when there is no key:
    /// <c>keys/server_identity_key.pem</c>. Forward slash because that is how the
    /// server writes it; composed through <see cref="Path.Combine(string[])"/>
    /// rather than concatenated, so the probe works on either separator.
    /// </remarks>
    public static readonly string[] RelativePath = ["keys", "server_identity_key.pem"];

    /// <summary>The console command that saves one.</summary>
    public const string SaveCommand = "serveridentity save";

    /// <summary>The console command that reports on one.</summary>
    /// <remarks>
    /// <strong>Nothing calls this.</strong> It was named as the probe that would
    /// answer <em>does this build understand the command</em> and no caller was
    /// written, so the fix was offered on builds that reject it (<c>T235</c>).
    /// That question is answered from <see cref="SaveCommand"/>'s own reply now,
    /// which costs no extra console round-trip; this stays for a caller that wants
    /// to ask without writing anything, and is dead until one exists.
    /// </remarks>
    public const string StatusCommand = "serveridentity status";

    /// <summary>
    /// Whether a console line is this server saying it does not know the command.
    /// </summary>
    /// <remarks>
    /// <para><strong>The fix reported a rejected command as a success.</strong> On
    /// a build that does not have it, BDS answers
    /// <c>"Unknown command: serveridentity."</c>, and the fix returned
    /// <c>Succeeded: true</c> regardless — so a refusal rendered in the success
    /// style, the row could never go green, and the only record of what happened
    /// was that instance's console ring.</para>
    ///
    /// <para><strong>Both terms are required, and that is the point of the
    /// predicate rather than a substring test.</strong> A console carries whatever
    /// anybody typed into it, so <em>unknown command</em> alone would match a
    /// mistyped command from a person and report this fix as refused when it was
    /// never answered.</para>
    ///
    /// <para><strong>No version is asserted anywhere.</strong> 1.26.45.1 rejects it
    /// and 1.26.51.1 answers it; where between them it arrived is not known here,
    /// and a cut written from a bracket is a number somebody will later rely on.
    /// Reading the server's own answer needs no such claim.</para>
    /// </remarks>
    public static bool IsRefusal(string? message) =>
        message is { Length: > 0 }
        && message.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
        && message.Contains("serveridentity", StringComparison.OrdinalIgnoreCase);

    /// <summary>The full path of the key file for an instance.</summary>
    public static string PathFor(string instanceDirectory) =>
        Path.Combine([instanceDirectory, .. RelativePath]);

    /// <summary>Whether <paramref name="instanceDirectory"/> has a saved identity.</summary>
    /// <remarks>
    /// An unreadable directory is <see cref="ServerIdentityState.Unknown"/> rather
    /// than <see cref="ServerIdentityState.InMemory"/>. Those two produce opposite
    /// advice — one says press this button, the other says nothing is known — and
    /// the panel telling somebody to run a command against a server it could not
    /// look at is the arrangement where a check invents work.
    /// </remarks>
    public static ServerIdentityState StateOf(string instanceDirectory)
    {
        try
        {
            return File.Exists(PathFor(instanceDirectory))
                ? ServerIdentityState.Saved
                : ServerIdentityState.InMemory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ServerIdentityState.Unknown;
        }
    }

    /// <summary>The row's title, which names the instance rather than the file.</summary>
    public static string CheckTitle(string instanceName) =>
        $"Server identity — {instanceName}";

    /// <summary>
    /// One instance's identity row, including whether the fix can act right now.
    /// </summary>
    /// <param name="state">What <see cref="StateOf"/> found.</param>
    /// <param name="serverIsRunning">
    /// Whether the command has somewhere to go. The fix is a console write, so a
    /// stopped server can be <em>reported</em> and cannot be <em>fixed</em> —
    /// three states rather than the two a <c>CanFix</c> alone would carry, and the
    /// difference is whether an administrator is being told to press something or
    /// to start something.
    /// </param>
    /// <remarks>
    /// The sentence names the <strong>consequence</strong> rather than the file,
    /// because <c>keys/server_identity_key.pem</c> means nothing to somebody who
    /// has not read BDS's release notes, and <em>your players keep being asked to
    /// trust this server</em> is the thing they have already noticed.
    /// <c>ServerIdentityTests</c> pins one case each.
    ///
    /// <para><strong>Every arm titles itself through <see cref="CheckTitle"/>, and
    /// `T232` is what the four hardcoded literals cost.</strong> The panel sets a
    /// row's loading title from <c>CheckTitle</c> and then renders the title on the
    /// <see cref="SystemCheck"/> the read returns — so a bare literal here meant the
    /// row named its instance for as long as it was checking and dropped the name
    /// the instant it had an answer. With six instances that is six identical rows,
    /// one of which carries a button that sends a console command to a server the
    /// row does not name. The sibling inspectors all compose their title inside
    /// <c>Check</c>; this one took the name nowhere near it.</para>
    /// </remarks>
    public static SystemCheck Check(ServerIdentityState state, bool serverIsRunning, string instanceName) => state switch
    {
        ServerIdentityState.Saved => new SystemCheck(
            CheckTitle(instanceName),
            SystemCheckState.Good,
            "This server keeps the same identity across restarts, so players are "
            + "not asked to trust it again."),

        ServerIdentityState.Unknown => new SystemCheck(
            CheckTitle(instanceName),
            SystemCheckState.Unknown,
            "This server's folder could not be read, so whether it has a saved "
            + "identity is not known."),

        _ when serverIsRunning => new SystemCheck(
            CheckTitle(instanceName),
            SystemCheckState.Missing,
            "This server has no saved identity, so it gets a new one every time it "
            + "starts and every player is asked to trust it again. BedrockSentry "
            + "restarts servers far more often than a person does — on a schedule, "
            + "on a Bedrock update, and after a crash — so this is most of the "
            + "reason it keeps happening.",
            CanFix: true,
            FixDescription:
                $"Sends {SaveCommand} on this server's console. Bedrock writes the "
                + "key itself; BedrockSentry only sends the command."),

        _ => new SystemCheck(
            CheckTitle(instanceName),
            SystemCheckState.Missing,
            "This server has no saved identity, so it gets a new one every time it "
            + "starts and every player is asked to trust it again. Start the server "
            + $"and this offers to send {SaveCommand} on its console."),
    };
}
