using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>What became of an access change.</summary>
/// <param name="Changed">Anything actually happened — false is "already so".</param>
/// <param name="ConfirmedOnDisk">
/// The file agrees. Always true on the stopped path, where the wrapper wrote
/// the file itself; on the running path it means BDS's own rewrite was seen.
/// </param>
/// <param name="Detail">One sentence for the page.</param>
public sealed record AccessChange(bool Changed, bool ConfirmedOnDisk, string Detail);

/// <summary>
/// Edits an instance's allowlist and operator overrides (<c>docs/design.md</c> § 7.6).
/// </summary>
/// <remarks>
/// <c>D31</c>: the files are the source of truth and this class is an editor
/// over them, with one editor per server state. A <em>running</em> server owns
/// its own files — it rewrites them on every in-game change, so a direct edit
/// underneath it would be overwritten or ignored. Changes go through the
/// console (<c>allowlist add</c>/<c>remove</c>, <c>op</c>/<c>deop</c>) and BDS
/// writes its own file. A <em>stopped</em> server's files are static, and the
/// wrapper edits them directly.
///
/// The running path is confirmed by re-reading the file until BDS's rewrite
/// appears, not by parsing the command's console reply. The reply wording has
/// never been measured, and this repo has been burned twice by building
/// against output BDS turned out not to print — the file is the source of
/// truth, so the file is what gets checked. A change that never appears is
/// reported as unconfirmed rather than assumed: <c>op</c> on a player who is
/// offline, for instance, is known to be refused by some BDS builds, and the
/// honest answer is "the server did not take it", not success.
/// </remarks>
public sealed class ServerAccessControl
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// What to say when an allowlist entry is removed.
    /// </summary>
    /// <remarks>
    /// `T153`: removing an entry takes its <c>ignoresPlayerLimit</c> with it,
    /// because the flag lives on the row and the row is gone. There is nowhere
    /// else to keep it - <c>D31</c> makes the file the source of truth, and a
    /// removed entry has no file to be in. So the flag genuinely cannot survive
    /// the round trip, and the defect worth fixing is that nothing said so:
    /// untick, re-tick, and "Join if full" was quietly off. It cost an operator
    /// a hand-repair after a test run.
    /// <para>
    /// Here rather than in <c>PlayersTab.razor</c>, and shared by the running
    /// and stopped paths, so there is one sentence rather than three - the
    /// class of defect this codebase keeps rediscovering, and the page cannot
    /// be reached by the suite at all.
    /// </para>
    /// </remarks>
    public static string RemovalDetail(string name, bool clearedIgnoresPlayerLimit) =>
        clearedIgnoresPlayerLimit
            ? $"{name} removed from the allowlist. Their 'Join if full' went with the "
              + "entry — adding them back starts it off again."
            : $"{name} removed from the allowlist.";

    private readonly TimeProvider _time;
    private readonly ILogger<ServerAccessControl> _log;
    private readonly TimeSpan _confirmWithin;

    public ServerAccessControl(
        TimeProvider time, ILogger<ServerAccessControl> log, TimeSpan? confirmWithin = null)
    {
        _time = time;
        _log = log;
        _confirmWithin = confirmWithin ?? TimeSpan.FromSeconds(4);
    }

    public async Task<AccessChange> AllowAsync(
        InstanceSupervisor supervisor,
        string name,
        bool ignoresPlayerLimit = false,
        CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        var path = AllowlistFile.PathFor(supervisor.WorkingDirectory);

        if (Settled(() => AllowlistFile.Load(path).Contains(name)))
        {
            return new AccessChange(false, true, $"{name} is already on the allowlist.");
        }

        if (IsRunning(supervisor))
        {
            // The console command has no flag for ignoresPlayerLimit; the entry
            // arrives with Bedrock's default and the toggle is a stopped-server
            // edit. The UI says so rather than silently dropping the tick.
            await supervisor.SendCommandAsync(Quote("allowlist add", name), cancellationToken)
                .ConfigureAwait(false);

            var confirmed = await ConfirmAsync(
                    () => AllowlistFile.Load(path).Contains(name), cancellationToken)
                .ConfigureAwait(false);

            return new AccessChange(
                true,
                confirmed,
                confirmed
                    ? $"{name} added to the allowlist."
                    : $"'allowlist add' was sent, but {name} has not appeared in "
                      + $"{AllowlistFile.FileName} — check the console for what the server said.");
        }

        var file = AllowlistFile.Load(path);
        file.Add(name, ignoresPlayerLimit);
        file.Save(path);

        return new AccessChange(true, true, $"{name} added to the allowlist.");
    }

    public async Task<AccessChange> DisallowAsync(
        InstanceSupervisor supervisor, string name, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        var path = AllowlistFile.PathFor(supervisor.WorkingDirectory);

        if (!Settled(() => AllowlistFile.Load(path).Contains(name)))
        {
            return new AccessChange(false, true, $"{name} is not on the allowlist.");
        }

        // Read before the removal, on both paths: afterwards there is no entry
        // to read it from, and on the running path BDS does the removing.
        var clearing = AllowlistFile.Load(path).IgnoresPlayerLimit(name);

        if (IsRunning(supervisor))
        {
            await supervisor.SendCommandAsync(Quote("allowlist remove", name), cancellationToken)
                .ConfigureAwait(false);

            var confirmed = await ConfirmAsync(
                    () => !AllowlistFile.Load(path).Contains(name), cancellationToken)
                .ConfigureAwait(false);

            return new AccessChange(
                true,
                confirmed,
                confirmed
                    ? RemovalDetail(name, clearing)
                    // Unconfirmed means the entry is still there, so nothing was
                    // cleared yet and saying otherwise would be a second lie.
                    : $"'allowlist remove' was sent, but {name} is still in "
                      + $"{AllowlistFile.FileName} — check the console for what the server said.");
        }

        var file = AllowlistFile.Load(path);
        file.Remove(name);
        file.Save(path);

        return new AccessChange(true, true, RemovalDetail(name, clearing));
    }

    /// <summary>
    /// Grants or revokes operator. Revoking writes an explicit
    /// <see cref="PermissionTier.Member"/> rather than removing the override:
    /// "stop being an operator" must hold even on a server whose
    /// <c>default-player-permission-level</c> is <c>operator</c>, where removal
    /// would hand the tier straight back.
    /// </summary>
    public async Task<AccessChange> SetOperatorAsync(
        InstanceSupervisor supervisor,
        string xuid,
        string gamertag,
        bool makeOperator,
        CancellationToken cancellationToken = default)
    {
        var path = PermissionsFile.PathFor(supervisor.WorkingDirectory);
        var wanted = makeOperator ? PermissionTier.Operator : PermissionTier.Member;
        var verb = makeOperator ? "op" : "deop";

        if (Settled(() => PermissionsFile.Load(path).TierOf(xuid) == wanted))
        {
            return new AccessChange(false, true, $"{gamertag} is already {wanted}.");
        }

        if (IsRunning(supervisor))
        {
            await supervisor.SendCommandAsync(Quote(verb, gamertag), cancellationToken)
                .ConfigureAwait(false);

            var confirmed = await ConfirmAsync(
                    () => PermissionsFile.Load(path).TierOf(xuid) == wanted, cancellationToken)
                .ConfigureAwait(false);

            return new AccessChange(
                true,
                confirmed,
                confirmed
                    ? $"{gamertag} is now {wanted}."
                    : $"'{verb}' was sent, but {PermissionsFile.FileName} has not changed — "
                      + "a player who is not online may be refused; check the console.");
        }

        var file = PermissionsFile.Load(path);
        file.Set(xuid, wanted);
        file.Save(path);

        return new AccessChange(true, true, $"{gamertag} is now {wanted}.");
    }

    /// <summary>
    /// Reads a file a running server may be part-way through rewriting.
    /// </summary>
    /// <remarks>
    /// BDS rewrites both files open-write-close on every in-game change, so a
    /// read that lands inside that window sees a locked or half-written file.
    /// <see cref="ConfirmAsync"/> already tolerates that because it polls; these
    /// checks run once and would otherwise surface a torn moment as an error.
    ///
    /// A few quick retries rather than a swallow: if the file is genuinely
    /// unreadable the exception still escapes, because treating an unreadable
    /// access-control file as an empty one would report "nobody is allowlisted"
    /// over a list that exists.
    /// </remarks>
    private static bool Settled(Func<bool> read)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (attempt < 4 && ex is IOException or System.Text.Json.JsonException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static bool IsRunning(InstanceSupervisor supervisor) =>
        supervisor.Status.State is InstanceState.Running or InstanceState.Starting;

    /// <summary>
    /// Gamertags carry spaces ("Some Player 99"), so the argument is always
    /// quoted rather than quoted when it looks like it needs it.
    /// </summary>
    private static string Quote(string command, string argument) =>
        $"{command} \"{argument}\"";

    private async Task<bool> ConfirmAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + _confirmWithin;

        while (_time.GetUtcNow() < deadline)
        {
            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // Mid-rewrite by BDS. The next poll reads the settled file.
            }
            catch (System.Text.Json.JsonException)
            {
                // Same: a half-written JSON file is a moment, not a state.
            }

            await Task.Delay(PollEvery, _time, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
