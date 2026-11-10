// BedrockSentry companion: reports deaths and player positions to the wrapper
// by printing lines to the server console, which the wrapper reads.
//
// Every line is "[BedrockSentry] <kind>|<field>|...", split on "|". A field
// before the last never contains "|"; the last field, when a line has text, is
// the human sentence and may contain anything but a line break. The wrapper
// ignores kinds it does not know, and a new field goes before the text, with
// PROTOCOL raised.

import {
  CommandPermissionLevel,
  CustomCommandParamType,
  CustomCommandStatus,
  MolangVariableMap,
  Player,
  system,
  world,
} from "@minecraft/server";
import {
  AREA_ACTIONS,
  AREA_VALUES,
  areaRequest,
  commandLine,
  dimensionName,
  listcordLines,
  OutlineStore,
  outlineSegments,
  parseOutline,
  parseReply,
  restoreRequest,
  tickRequest,
} from "./lib.js";

const PROTOCOL = 1;
const PACK_VERSION = "1.7.1";
const MARKER = "[BedrockSentry] ";

// Named here and in the manifest's settings array; a test holds the two together.
const TELL_DEATH_LOCATION = "bedrocksentry:tell_death_location";

// "minecraft:cave_spider" reads "Cave Spider". Formatted rather than looked up,
// so a mob added later still reads as a name.
function mobName(typeId) {
  const bare = typeId.includes(":") ? typeId.slice(typeId.indexOf(":") + 1) : typeId;
  return bare
    .split("_")
    .filter((word) => word.length > 0)
    .map((word) => word[0].toUpperCase() + word.slice(1))
    .join(" ");
}

function withArticle(name) {
  return (/^[AEIOU]/.test(name) ? "an " : "a ") + name;
}

function blockPosition(entity) {
  const { x, y, z } = entity.location;
  return { x: Math.floor(x), y: Math.floor(y), z: Math.floor(z), dimension: entity.dimension.id };
}

// One place builds the location text, so what a player is told and what the
// console shows cannot disagree.
function locationText(position) {
  return `at ${position.x}, ${position.y}, ${position.z} in ${dimensionName(position.dimension)}`;
}

// A field before the text may not carry the delimiter, and no field may carry
// a line break, or one line would be read as two.
function field(value) {
  return String(value ?? "").replace(/[|\r\n]/g, "");
}

function text(value) {
  return String(value).replace(/[\r\n]/g, " ");
}

function relay(kind, fields, sentence) {
  const parts = [kind, ...fields.map(field)];
  if (sentence !== undefined) {
    parts.push(text(sentence));
  }
  console.log(MARKER + parts.join("|"));
}

function positionFields(position) {
  return [position.x, position.y, position.z, position.dimension];
}

// Who or what did it, as a person would say it: a player by name, anything else
// by its type.
function killerOf(source) {
  const entity = source.damagingEntity;
  if (entity === undefined || !entity.isValid) {
    return { type: undefined, player: undefined, said: undefined };
  }

  if (entity instanceof Player) {
    return { type: entity.typeId, player: entity.name, said: entity.name };
  }

  return { type: entity.typeId, player: undefined, said: withArticle(mobName(entity.typeId)) };
}

// A curated few get their own phrase; every other cause falls back to a sentence
// that still names it, so a cause added in a later Bedrock is reported rather
// than dropped.
function deathPhrase(cause, killer) {
  switch (cause) {
    case "fall":
      return "fell from a high place";
    case "entityAttack":
      if (killer.player !== undefined) return `was slain by ${killer.player}`;
      return killer.said !== undefined ? `was killed by ${killer.said}` : "was killed";
    case "projectile":
      return killer.said !== undefined ? `was shot by ${killer.said}` : "was shot";
    case "entityExplosion":
      return killer.said !== undefined ? `was blown up by ${killer.said}` : "blew up";
    case "drowning":
      return "drowned";
    case "lava":
      return "tried to swim in lava";
    case "fire":
      return "went up in flames";
    case "fireTick":
      return "burned to death";
    case "starve":
      return "starved to death";
    case "suffocation":
      return "suffocated in a wall";
    default:
      return `died (${cause})`;
  }
}

function tellsDeathLocation() {
  const value = world.getPackSettings()[TELL_DEATH_LOCATION];
  return value === undefined ? true : value === true;
}

world.afterEvents.worldLoad.subscribe(() => {
  relay("hello", [PROTOCOL, PACK_VERSION]);
});

world.afterEvents.entityDie.subscribe(
  (event) => {
    // Filtered to players below, and checked here too: the event's type is any
    // entity, and a name and a message exist only on a player.
    const player = event.deadEntity;
    if (!(player instanceof Player)) {
      return;
    }

    const position = blockPosition(player);
    const cause = event.damageSource.cause;
    const killer = killerOf(event.damageSource);
    const where = locationText(position);

    relay(
      "death",
      [player.name, cause, killer.type, killer.player, ...positionFields(position)],
      `${player.name} ${deathPhrase(cause, killer)} ${where}`);

    if (tellsDeathLocation()) {
      player.sendMessage(`You died ${where}.`);
    }
  },
  { entityTypes: ["minecraft:player"] });

// Fires on every respawn too, so only the first spawn after joining counts.
world.afterEvents.playerSpawn.subscribe((event) => {
  if (!event.initialSpawn) {
    return;
  }

  const position = blockPosition(event.player);
  relay(
    "join",
    [event.player.name, ...positionFields(position)],
    `${event.player.name} joined ${locationText(position)}`);
});

// The after-event carries no player, so this is the only hook with a position
// left to read. It runs restricted, so the position is read here and printed on
// the next tick.
world.beforeEvents.playerLeave.subscribe((event) => {
  const name = event.player.name;
  const position = blockPosition(event.player);

  system.run(() => {
    relay("leave", [name, ...positionFields(position)], `${name} left ${locationText(position)}`);
  });
});

// D106, D109. Outlines the wrapper hands over, drawn to each operator near them as
// bedrocksentry:outline_line, a thin full-bright line the companion's resource pack
// defines, one particle per piece of an edge. Each lives a little longer than the time
// between rounds, so a line never goes out between one round and the next. A player who
// declined the server's resource packs sees nothing. Kept in memory only: the wrapper
// sends them again after every hello.
const OUTLINE_EVENT = "bedrocksentry:outline";
const REPLY_EVENT = "bedrocksentry:reply";
const OUTLINE_PARTICLE = "bedrocksentry:outline_line";
const DRAW_EVERY_TICKS = 20;
const LINE_SECONDS = 1.25;
const DRAW_RADIUS = 128;
const DRAW_LIMIT = 400;

const outlines = new OutlineStore();

// Operators only, as the operator chose: an outline tells where a kept area is. Each
// player sees only the particles spawned for them, so nobody else sees the outline.
function drawOutlinesFor(player) {
  if (player.commandPermissionLevel < CommandPermissionLevel.GameDirectors) {
    return;
  }

  let budget = DRAW_LIMIT;
  for (const area of outlines.areasIn(player.dimension.id)) {
    const segments = outlineSegments(area, player.location, DRAW_RADIUS, budget);
    for (const piece of segments) {
      const molang = new MolangVariableMap();
      molang.setColorRGB("variable.color", area.color);
      molang.setFloat("variable.length", piece.length);
      molang.setFloat("variable.dx", piece.dx);
      molang.setFloat("variable.dy", piece.dy);
      molang.setFloat("variable.dz", piece.dz);
      molang.setFloat("variable.life", LINE_SECONDS);
      try {
        player.spawnParticle(OUTLINE_PARTICLE, piece.center, molang);
      } catch {
        // A piece in a chunk that is not loaded throws LocationInUnloadedChunkError
        // (measured on BDS 1.26.52.3); the rest of the outline is still drawn.
      }
    }

    budget -= segments.length;
    if (budget <= 0) {
      return;
    }
  }
}

system.runInterval(() => {
  if (outlines.isEmpty()) {
    return;
  }

  for (const player of world.getAllPlayers()) {
    try {
      drawOutlinesFor(player);
    } catch {
      // A player who left between the list and the draw; the next round skips them.
    }
  }
}, DRAW_EVERY_TICKS);

// The wrapper's side of the channel: a probe that proves the console reaches
// this pack, the D35 exchange that asks where everybody is before a stop, and the
// outlines to draw.
system.afterEvents.scriptEventReceive.subscribe((event) => {
  // The wrapper's answer to a syncarea, for the player who asked; red when it refused.
  if (event.id === REPLY_EVENT) {
    const reply = parseReply(event.message);
    const player = reply === null
      ? undefined
      : world.getAllPlayers().find((p) => p.name.toLowerCase() === reply.to.toLowerCase());
    if (player !== undefined) {
      player.sendMessage(reply.lines.map((line) => (reply.ok ? line : "§c" + line)).join("\n"));
    }
    return;
  }

  if (event.id === OUTLINE_EVENT) {
    const taken = outlines.take(parseOutline(event.message));
    if (taken !== undefined) {
      relay("outline", [taken.group, taken.count]);
    }
    return;
  }

  if (event.id === "bedrocksentry:probe") {
    relay("probe", [PROTOCOL]);
    return;
  }

  if (event.id === "bedrocksentry:where") {
    const players = world.getAllPlayers();
    for (const player of players) {
      relay("where", [player.name, ...positionFields(blockPosition(player))]);
    }
    relay("where-end", [players.length]);
  }
});

// D106. A player who runs one of these commands is named on the console, which prints
// nothing of its own for a command typed in game; the console's own commands it shows
// as typed, so they are not relayed.
function ranBy(origin, line) {
  if (origin.sourceEntity instanceof Player) {
    relay("cmd", [origin.sourceEntity.name], line);
  }
}

// D106. Commands an operator types in game or on the console. Operator level
// only: listcord tells where everybody is. The console counts as an operator
// (measured on BDS 1.26.52.3), and its reply prints there.
system.beforeEvents.startup.subscribe((init) => {
  init.customCommandRegistry.registerEnum("bedrocksentry:areaaction", AREA_ACTIONS);
  init.customCommandRegistry.registerEnum("bedrocksentry:areavalue", AREA_VALUES);

  init.customCommandRegistry.registerCommand(
    {
      name: "bedrocksentry:listcord",
      description: "Lists where online players are, or one player.",
      permissionLevel: CommandPermissionLevel.GameDirectors,
      cheatsRequired: false,
      optionalParameters: [{ type: CustomCommandParamType.String, name: "player" }],
    },
    (origin, wanted) => {
      ranBy(origin, commandLine("listcord", [wanted]));
      const players = world.getAllPlayers().map((player) => {
        const position = blockPosition(player);
        return { name: player.name, x: position.x, y: position.y, z: position.z, dimension: position.dimension };
      });
      const reply = listcordLines(players, wanted);
      return {
        status: reply.found ? CustomCommandStatus.Success : CustomCommandStatus.Failure,
        message: reply.lines.join("\n"),
      };
    });

  // The wrapper writes the Sync Areas, so the pack relays the request and the wrapper
  // answers: a player through REPLY_EVENT, the console on its own page.
  init.customCommandRegistry.registerCommand(
    {
      name: "bedrocksentry:syncarea",
      description: "Adds, moves, shows or lists Sync Areas.",
      permissionLevel: CommandPermissionLevel.GameDirectors,
      cheatsRequired: false,
      mandatoryParameters: [{ type: CustomCommandParamType.Enum, name: "bedrocksentry:areaaction" }],
      optionalParameters: [
        { type: CustomCommandParamType.String, name: "name" },
        { type: CustomCommandParamType.Enum, name: "bedrocksentry:areavalue" },
        { type: CustomCommandParamType.Location, name: "position" },
      ],
    },
    (origin, action, name, value, position) => {
      ranBy(origin, commandLine("syncarea", [action, name, value, position]));
      const player = origin.sourceEntity instanceof Player ? origin.sourceEntity : undefined;
      const from = player === undefined
        ? {}
        : { player: player.name, location: player.location, dimension: player.dimension.id };

      const request = areaRequest({ action, name, value, position }, from);
      if (request.error !== undefined) {
        return { status: CustomCommandStatus.Failure, message: request.error };
      }

      const f = request.fields;
      relay("area", [f.player, f.action, f.corner, f.show, f.x, f.y, f.z, f.dimension], f.name);
      return player === undefined
        ? { status: CustomCommandStatus.Success, message: "Asked BedrockSentry." }
        : { status: CustomCommandStatus.Success };
    });

  // D107. Shows or hides a ticking area's outline; the wrapper reads the world's ticking
  // areas, so it finds the area and answers, as it does for syncarea.
  init.customCommandRegistry.registerCommand(
    {
      name: "bedrocksentry:showtickingarea",
      description: "Shows or hides a ticking area's outline for operators.",
      permissionLevel: CommandPermissionLevel.GameDirectors,
      cheatsRequired: false,
      mandatoryParameters: [
        { type: CustomCommandParamType.String, name: "name" },
        { type: CustomCommandParamType.Boolean, name: "show" },
      ],
    },
    (origin, name, show) => {
      ranBy(origin, commandLine("showtickingarea", [name, show]));
      const player = origin.sourceEntity instanceof Player ? origin.sourceEntity : undefined;
      const from = player === undefined ? {} : { player: player.name, dimension: player.dimension.id };

      const request = tickRequest({ name, show }, from);
      if (request.error !== undefined) {
        return { status: CustomCommandStatus.Failure, message: request.error };
      }

      const f = request.fields;
      relay("tickshow", [f.player, f.show, f.dimension], f.name);
      return player === undefined
        ? { status: CustomCommandStatus.Success, message: "Asked BedrockSentry." }
        : { status: CustomCommandStatus.Success };
    });

  // D108. Puts one area back from a backup. The wrapper reads the backup on a copy, then
  // restarts the server with 30 seconds' warning; there is no confirmation, and the
  // wrapper answers at once whether it started.
  init.customCommandRegistry.registerCommand(
    {
      name: "bedrocksentry:restorearea",
      description: "Restores a Sync Area, or pos and two corners, from a backup.",
      permissionLevel: CommandPermissionLevel.GameDirectors,
      cheatsRequired: false,
      mandatoryParameters: [{ type: CustomCommandParamType.String, name: "area" }],
      optionalParameters: [
        { type: CustomCommandParamType.String, name: "backup" },
        { type: CustomCommandParamType.Location, name: "from" },
        { type: CustomCommandParamType.Location, name: "to" },
      ],
    },
    (origin, area, backup, from, to) => {
      ranBy(origin, commandLine("restorearea", [area, backup, from, to]));
      const player = origin.sourceEntity instanceof Player ? origin.sourceEntity : undefined;
      const by = player === undefined ? {} : { player: player.name, dimension: player.dimension.id };

      const request = restoreRequest({ area, backup, from, to }, by);
      if (request.error !== undefined) {
        return { status: CustomCommandStatus.Failure, message: request.error };
      }

      const f = request.fields;
      relay("restore", [f.player, f.backup, f.dimension, f.x1, f.y1, f.z1, f.x2, f.y2, f.z2], f.name);
      return player === undefined
        ? { status: CustomCommandStatus.Success, message: "Asked BedrockSentry." }
        : { status: CustomCommandStatus.Success };
    });
});
