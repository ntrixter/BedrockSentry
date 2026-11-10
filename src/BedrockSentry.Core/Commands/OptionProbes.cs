using System.Text.RegularExpressions;

namespace BedrockSentry.Core.Commands;

/// <summary>What one probe of one value said.</summary>
public enum ProbeOutcome
{
    /// <summary>The server parsed the value; whatever it answered after that is about the world.</summary>
    Accepted,

    /// <summary>The server's syntax error names the value.</summary>
    Refused,

    /// <summary>
    /// The probe did not reach the value: a syntax error names another word, or the
    /// command is unknown. Nothing is learned about the value.
    /// </summary>
    Inconclusive,
}

/// <summary>A server option list, with a command that puts each candidate where the list is read.</summary>
/// <param name="Type">The type name the help forms use: <c>GameMode</c>.</param>
/// <param name="Template">The command, with <c>{v}</c> where the value goes.</param>
/// <param name="Candidates">Every value tried, in the order completion offers them.</param>
/// <param name="RefusedWhenMeasured">
/// The candidates BDS <see cref="OptionProbes.MeasuredOn"/> refused. The others are the
/// list used when a probe cannot be read on another version.
/// </param>
public sealed record OptionProbe(
    string Type, string Template, IReadOnlyList<string> Candidates, IReadOnlyList<string> RefusedWhenMeasured)
{
    /// <summary>The command that tries <paramref name="value"/>.</summary>
    public string CommandFor(string value) => Template.Replace("{v}", value, StringComparison.Ordinal);

    /// <summary>The values the server took when the list was measured.</summary>
    public IReadOnlyList<string> Measured =>
        [.. Candidates.Where(c => !RefusedWhenMeasured.Contains(c, StringComparer.Ordinal))];
}

/// <summary>One option list after the probes, or after falling back to the measured list.</summary>
/// <param name="Checked">
/// False when a probe could not be read on this version, so <paramref name="Values"/> is
/// the list measured on <see cref="OptionProbes.MeasuredOn"/> rather than this server's.
/// </param>
public sealed record OptionList(string Type, IReadOnlyList<string> Values, bool Checked);

/// <summary>The server's option lists, and how a run of the server checks them.</summary>
/// <remarks>
/// <para><strong>Nothing the server prints lists these values.</strong> <c>help</c> names
/// a form's option list, <c>&lt;mode: GameMode&gt;</c>, and never its contents. So the
/// candidates ship here, and a throwaway copy of each new version is sent every one of
/// them: a value it refuses is dropped (<c>D105</c>). A value a new version adds is not
/// found this way; that is the cost of having nothing to read.</para>
///
/// <para><strong>A refusal is a syntax error that names the value.</strong> Every other
/// answer means the parser took it: <c>No targets matched selector</c>, <c>Source and
/// destination can not overlap</c>, or nothing at all. A syntax error naming another word
/// says the probe never got there, as <c>packstack behavior verbose</c> did when
/// <c>behavior</c> was the word refused, and is
/// <see cref="ProbeOutcome.Inconclusive"/>.</para>
///
/// <para>Every template below was run on BDS 1.26.52.3, and every candidate it took or
/// refused is in the transcript Services ships. <c>verbose</c> once probed through
/// <c>packstack behavior verbose</c>, which stopped at <c>behavior</c>; its template now
/// names a stack the server takes.</para>
/// </remarks>
public static partial class OptionProbes
{
    /// <summary>The version the candidates and refusals were measured on.</summary>
    public const string MeasuredOn = "1.26.52.3";

    [GeneratedRegex("Syntax error: Unexpected \"(?<word>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex SyntaxErrorPattern { get; }

    /// <summary>Every list that is probed, in the order run.</summary>
    public static IReadOnlyList<OptionProbe> All { get; } =
    [
        Probe("AimAssistTargetMode", "aimassist @a set 0 0 0 {v}", "angle distance"),
        Probe("AllowListAction", "allowlist {v} zzprobe", "add remove list on off reload"),
        Probe("CameraShakeType", "camerashake add @a 1 1 {v}", "positional rotational"),
        Probe("CloneMode", "clone 0 -64 0 0 -64 0 0 -64 0 replace {v}", "force move normal"),
        Probe("controlscheme", "controlscheme @a set {v}",
            "camera_relative camera_relative_strafe locked_player_relative_strafe player_relative player_relative_strafe"),
        Probe("DamageCause", "damage @a 1 {v}",
            "all anvil block_explosion campfire charging contact drowning entity_attack entity_explosion fall "
            + "falling_block fire fire_tick fireworks fly_into_wall freezing lava lightning mace_smash magic magma "
            + "none override piston projectile ram_attack self_destruct sonic_boom soul_campfire stalactite "
            + "stalagmite starve suffocation suicide temperature thorns void wither",
            refused: "all suicide"),
        Probe("default", "camera @a set minecraft:free {v}", "default"),
        Probe("delete", "fog @a {v} zzprobe", "pop remove"),
        Probe("Difficulty", "difficulty {v}", "peaceful p easy e normal n hard h"),
        Probe("Dimension", "execute in {v} run list", "overworld nether the_end"),
        Probe("Easing", "camera @a fov_clear 1 {v}",
            "linear spring in_quad out_quad in_out_quad in_cubic out_cubic in_out_cubic in_quart out_quart "
            + "in_out_quart in_quint out_quint in_out_quint in_sine out_sine in_out_sine in_expo out_expo "
            + "in_out_expo in_circ out_circ in_out_circ in_bounce out_bounce in_out_bounce in_back out_back "
            + "in_out_back in_elastic out_elastic in_out_elastic"),
        Probe("EntityEquipmentSlot", "replaceitem entity @a {v} 0 dirt",
            "slot.armor slot.armor.body slot.armor.chest slot.armor.feet slot.armor.head slot.armor.legs "
            + "slot.chest slot.container slot.enderchest slot.equippable slot.hotbar slot.inventory slot.saddle "
            + "slot.weapon.mainhand slot.weapon.offhand",
            refused: "slot.container"),
        Probe("FillMode", "fill 0 -64 0 0 -64 0 bedrock {v}", "destroy hollow keep outline replace"),
        Probe("FillType", "ride @a start_riding @a teleport_rider {v}", "if_group_fits until_full"),
        Probe("GameMode", "gamemode {v} @a", "survival s creative c adventure a spectator sp default d",
            refused: "sp"),
        Probe("HudVisibility", "hud @a {v}", "hide reset"),
        Probe("HudElement", "hud @a hide {v}",
            "all paperdoll armor tooltips touch_controls crosshair hotbar health progress_bar hunger "
            + "air_bubbles horse_health status_effects item_text"),
        Probe("LiquidSettings", "place structure zzprobe 0 0 0 false false false {v}",
            "apply_waterlogging ignore_waterlogging"),
        Probe("MaskMode", "clone 0 -64 0 0 -64 0 0 -64 0 {v}", "masked replace"),
        Probe("MobEvent", "mobevent {v}",
            "minecraft:pillager_patrols_event minecraft:wandering_trader_event minecraft:ender_dragon_event "
            + "events_enabled"),
        Probe("MusicRepeatMode", "music play zzprobe 1 0 {v}", "loop play_once"),
        Probe("operator", "scoreboard players operation @a zz {v} @a zz", "%= *= += -= /= < = > ><"),
        Probe("compare operator", "execute if score @a zz {v} @a zz run list", "< <= = >= >"),
        Probe("Option_If_Unless", "execute {v} entity @a", "if unless"),
        Probe("permission", "inputpermission query @a {v}",
            "camera movement lateral_movement sneak jump mount dismount move_forward move_backward "
            + "move_left move_right"),
        Probe("state", "inputpermission query @a camera {v}", "enabled disabled"),
        Probe("PermissionsAction", "permission {v}", "list reload set"),
        Probe("PermissionLevel", "permission set @a {v}", "visitor member operator custom", refused: "custom"),
        Probe("playerwaypointsValues", "gamerule playerwaypoints {v}",
            "disabled enabled everyone operators operator off on none all teams",
            refused: "disabled enabled operators operator on none all teams"),
        Probe("RedirectLocation", "sendshowstoreoffer @a {v} zzprobe",
            "servertab server_tab store marketplace home offer",
            refused: "servertab server_tab store home offer"),
        Probe("reload_all", "reload {v}", "all"),
        Probe("ReplaceMode", "replaceitem entity @a slot.weapon.mainhand 0 {v} dirt", "destroy keep"),
        Probe("RideRules", "ride @a summon_ride pig {v}", "no_ride_change reassign_rides skip_riders"),
        Probe("Rotation", "structure load zzprobe 0 0 0 {v}", "0_degrees 90_degrees 180_degrees 270_degrees"),
        Probe("Mirror", "structure load zzprobe 0 0 0 0_degrees {v}", "none x z xz"),
        Probe("StructureAnimationMode", "structure load zzprobe 0 0 0 0_degrees none {v}",
            "block_by_block layer_by_layer"),
        Probe("StructureSaveMode", "structure save zzprobe 0 -64 0 0 -64 0 {v}", "disk memory"),
        Probe("TeleportRules", "ride @a start_riding @a {v}", "teleport_ride teleport_rider"),
        Probe("TimeQuery", "time query {v}", "daytime gametime day"),
        Probe("TimeSpec", "time set {v}", "day night noon midnight sunrise sunset"),
        Probe("TitleRawSet", "titleraw @a {v} {\"rawtext\":[]}", "title subtitle actionbar"),
        Probe("AllDimensions", "tickingarea list {v}", "all-dimensions"),
        Probe("BlocksScanMode", "execute if blocks 0 -64 0 0 -64 0 0 -64 0 {v}", "all masked"),
        Probe("stackType", "packstack {v}", "client server behavior resource behavior_packs resource_packs all",
            refused: "behavior resource behavior_packs resource_packs all"),
        Probe("verbose", "packstack server {v}", "verbose"),
        Probe("exclude-vanilla", "packstack server verbose {v}", "exclude-vanilla"),
    ];

    /// <summary>Lists that are never probed, each for its reason.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>Boolean</c> is the parser's own <c>true</c> and <c>false</c>.</item>
    /// <item><c>SaveMode</c> includes <c>hold</c>, and holding the save is not a probe.</item>
    /// <item><c>ServerIdentityAction</c> writes or deletes an identity key; <c>save</c> is the
    /// action the panel offers (<c>D73</c>) and <c>delete</c> was the one other value taken.</item>
    /// <item><c>DamageCauseSuicide</c> took no candidate at all, <c>suicide</c> included, so it
    /// offers nothing rather than a guess.</item>
    /// </list>
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Fixed { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Boolean"] = ["true", "false"],
            ["SaveMode"] = ["hold", "query", "resume"],
            ["ServerIdentityAction"] = ["save", "delete"],
            ["DamageCauseSuicide"] = [],
        };

    /// <summary>What a probe's reply says about <paramref name="value"/>.</summary>
    /// <param name="reply">The reply's lines without their <c>[timestamp LEVEL]</c> prefix.</param>
    public static ProbeOutcome Judge(IEnumerable<string> reply, string value)
    {
        ArgumentNullException.ThrowIfNull(reply);

        foreach (var line in reply)
        {
            if (line.StartsWith("Unknown command", StringComparison.Ordinal))
            {
                return ProbeOutcome.Inconclusive;
            }

            var error = SyntaxErrorPattern.Match(line);
            if (error.Success)
            {
                return string.Equals(error.Groups["word"].Value, value, StringComparison.OrdinalIgnoreCase)
                    ? ProbeOutcome.Refused
                    : ProbeOutcome.Inconclusive;
            }
        }

        return ProbeOutcome.Accepted;
    }

    /// <summary>One list from its candidates' outcomes.</summary>
    /// <remarks>
    /// <strong>One inconclusive probe sets the whole list aside.</strong> It means the
    /// template no longer reaches the value on this version, so a refusal elsewhere in the
    /// same list could be the template's failure too. The measured list is used instead,
    /// and says it was not checked.
    /// </remarks>
    public static OptionList Settle(OptionProbe probe, IReadOnlyDictionary<string, ProbeOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(outcomes);

        var checkedAll = probe.Candidates.All(c =>
            outcomes.TryGetValue(c, out var outcome) && outcome is not ProbeOutcome.Inconclusive);

        return checkedAll
            ? new OptionList(probe.Type, [.. probe.Candidates.Where(c => outcomes[c] is ProbeOutcome.Accepted)], true)
            : new OptionList(probe.Type, probe.Measured, false);
    }

    private static OptionProbe Probe(string type, string template, string candidates, string refused = "") =>
        new(type, template, candidates.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            refused.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
