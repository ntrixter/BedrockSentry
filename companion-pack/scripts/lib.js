// The companion's pure parts: no game API here, so tests/js can run them under
// Node, and scripts/main.js imports them.

// What a person reads for a dimension id. An id not listed is shown as it is,
// so a dimension added later is named badly rather than wrongly.
export const DIMENSION_NAMES = {
  "minecraft:overworld": "the Overworld",
  "minecraft:nether": "the Nether",
  "minecraft:the_end": "the End",
};

export function dimensionName(id) {
  return DIMENSION_NAMES[id] ?? id;
}

// listcord names the dimension bare after the position, `Steve: 1 64 2 Nether`, as the
// operator asked on 2026-10-09; the join, leave and death sentences keep "in the Nether".
function line(player) {
  return `${player.name}: ${player.x} ${player.y} ${player.z} ${dimensionName(player.dimension).replace(/^the /, "")}`;
}

// The reply to `listcord [player]`. Each player is { name, x, y, z, dimension },
// the coordinates already whole blocks. With a name, only that player, matched
// ignoring case as gamertags are; `found` is false when nobody by that name is on.
export function listcordLines(players, wanted) {
  if (wanted !== undefined && wanted !== null && wanted !== "") {
    const match = players.find((p) => p.name.toLowerCase() === String(wanted).toLowerCase());
    return match === undefined
      ? { found: false, lines: [`No player named ${wanted} is online.`] }
      : { found: true, lines: [line(match)] };
  }

  if (players.length === 0) {
    return { found: true, lines: ["No players are online."] };
  }

  const sorted = [...players].sort((a, b) => a.name.toLowerCase().localeCompare(b.name.toLowerCase()));
  const heading = players.length === 1 ? "1 player online:" : `${players.length} players online:`;
  return { found: true, lines: [heading, ...sorted.map(line)] };
}

// D106. Outlines the wrapper hands over with `scriptevent bedrocksentry:outline`, as
// {"g":group,"p":part,"n":parts,"a":[[dimension,x1,y1,z1,x2,y2,z2,"rrggbb"],...]}: the
// dimension is the name `execute in` takes and the corners are blocks, both included
// (AreaOutlines in BedrockSentry.Core). A message that breaks the layout anywhere is
// refused whole, so a half-read area is never drawn.
const OUTLINE_DIMENSIONS = {
  overworld: "minecraft:overworld",
  nether: "minecraft:nether",
  the_end: "minecraft:the_end",
};

const MAX_PARTS = 64;

export function hexColor(hex) {
  const channel = (at) => Math.round((parseInt(hex.slice(at, at + 2), 16) / 255) * 1000) / 1000;
  return { red: channel(0), green: channel(2), blue: channel(4) };
}

function outlineArea(item) {
  if (!Array.isArray(item) || (item.length !== 8 && item.length !== 9)) return null;
  const [name, x1, y1, z1, x2, y2, z2, color, shape] = item;
  if (item.length === 9 && shape !== "circle") return null;
  const dimension = OUTLINE_DIMENSIONS[name];
  const corners = [x1, y1, z1, x2, y2, z2];
  if (dimension === undefined || !corners.every(Number.isInteger)) return null;
  if (typeof color !== "string" || !/^[0-9a-fA-F]{6}$/.test(color)) return null;

  return {
    dimension,
    min: { x: Math.min(x1, x2), y: Math.min(y1, y2), z: Math.min(z1, z2) },
    max: { x: Math.max(x1, x2), y: Math.max(y1, y2), z: Math.max(z1, z2) },
    color: hexColor(color),
    circle: shape === "circle",
  };
}

export function parseOutline(message) {
  let value;
  try {
    value = JSON.parse(message);
  } catch {
    return null;
  }

  if (value === null || typeof value !== "object") return null;
  const { g: group, p: part, n: parts, a: items } = value;
  if (typeof group !== "string" || group.length === 0 || group.length > 32) return null;
  if (!Number.isInteger(parts) || parts < 1 || parts > MAX_PARTS) return null;
  if (!Number.isInteger(part) || part < 1 || part > parts || !Array.isArray(items)) return null;

  const areas = items.map(outlineArea);
  return areas.includes(null) ? null : { group, part, parts, areas };
}

// Each group's outlines, replaced whole once the last part of a new set arrives. A
// part out of turn drops the set it belonged to and keeps what was drawn before.
export class OutlineStore {
  constructor() {
    this.groups = new Map();
    this.pending = new Map();
  }

  // Takes a parsed message; returns { group, count } when it completed a set.
  take(message) {
    if (message === null) return undefined;
    const { group, part, parts, areas } = message;

    if (part === 1) {
      this.pending.set(group, { parts, next: 1, areas: [] });
    }

    const set = this.pending.get(group);
    if (set === undefined || set.parts !== parts || set.next !== part) {
      this.pending.delete(group);
      return undefined;
    }

    set.areas.push(...areas);
    set.next += 1;
    if (part < parts) return undefined;

    this.pending.delete(group);
    if (set.areas.length === 0) {
      this.groups.delete(group);
    } else {
      this.groups.set(group, set.areas);
    }

    return { group, count: set.areas.length };
  }

  isEmpty() {
    return this.groups.size === 0;
  }

  areasIn(dimension) {
    return [...this.groups.values()].flat().filter((area) => area.dimension === dimension);
  }
}

// The longest piece an edge is drawn in. A particle far off-screen at one end of a long
// edge would take the whole edge with it, so an edge is cut into pieces of at most this.
export const SEGMENT_BLOCKS = 16;

// The pieces of an area's outline within `radius` blocks of `near` on every axis, at most
// `limit` of them, each { center, dx, dy, dz, length }: a straight line `length` blocks
// long through `center`, along the unit direction (dx, dy, dz). A box's twelve edges run
// on the blocks' outer faces, from the low corner to one past the high corner, so the
// outline encloses both corner blocks.
export function outlineSegments(area, near, radius, limit) {
  const segments = [];

  if (area.circle) {
    circleSegments(area, near, radius, limit, segments);
    return segments;
  }

  const lo = area.min;
  const hi = { x: area.max.x + 1, y: area.max.y + 1, z: area.max.z + 1 };
  const axes = ["x", "y", "z"];

  for (const along of axes) {
    const [a, b] = axes.filter((axis) => axis !== along);
    for (const at of [lo[a], hi[a]]) {
      for (const bt of [lo[b], hi[b]]) {
        const from = { [a]: at, [b]: bt, [along]: lo[along] };
        if (!edgePieces(from, along, hi[along], near, radius, limit, segments)) return segments;
      }
    }
  }

  return segments;
}

function reaches(from, to, near, radius) {
  return ["x", "y", "z"].every((axis) =>
    near[axis] >= Math.min(from[axis], to[axis]) - radius && near[axis] <= Math.max(from[axis], to[axis]) + radius);
}

function segment(from, to) {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const dz = to.z - from.z;
  const length = Math.hypot(dx, dy, dz);
  return {
    center: { x: (from.x + to.x) / 2, y: (from.y + to.y) / 2, z: (from.z + to.z) / 2 },
    dx: dx / length,
    dy: dy / length,
    dz: dz / length,
    length,
  };
}

// The pieces of the edge from `from` to `end` along `along` that reach `near`, cut every
// SEGMENT_BLOCKS from its start; false once `limit` is reached. Only the pieces whose
// stretch along the edge comes within `radius` are visited, so a long edge costs no more
// than a short one.
function edgePieces(from, along, end, near, radius, limit, segments) {
  const start = from[along];
  const count = Math.ceil((end - start) / SEGMENT_BLOCKS);
  const first = Math.max(0, Math.floor((near[along] - radius - start) / SEGMENT_BLOCKS));
  const last = Math.min(count - 1, Math.floor((near[along] + radius - start) / SEGMENT_BLOCKS));

  for (let k = first; k <= last; k++) {
    const a = { ...from, [along]: start + k * SEGMENT_BLOCKS };
    const b = { ...from, [along]: Math.min(end, start + (k + 1) * SEGMENT_BLOCKS) };
    if (!reaches(a, b, near, radius)) continue;
    if (segments.length >= limit) return false;
    segments.push(segment(a, b));
  }

  return true;
}

// D106. `syncarea <new|update|show|list> [name] [pos1|pos2|true|false] [x y z]`, checked
// here before anything reaches the wrapper: the console does not hold a registered enum
// to its values (measured on BDS 1.26.52.3), and a bare number is refused in a String or
// Enum slot, so a corner is the word pos1 or pos2.
export const AREA_ACTIONS = ["new", "update", "show", "list"];
export const AREA_VALUES = ["pos1", "pos2", "true", "false"];

// origin is { player, location, dimension } for a player and {} for the console. The
// result is { error } to answer at once, or { fields } to relay. A position arrives as
// the game parsed it, x and z centred on their block, so every coordinate is floored;
// with none, a player's corner is the block under their feet.
export function areaRequest(request, origin) {
  const action = String(request.action ?? "").toLowerCase();
  if (!AREA_ACTIONS.includes(action)) {
    return { error: "syncarea takes new, update, show or list." };
  }

  const fields = { player: origin.player ?? "", action, corner: "", show: "", x: "", y: "", z: "", dimension: "", name: "" };
  if (action === "list") {
    return { fields };
  }

  const name = String(request.name ?? "").trim();
  if (name.length === 0) {
    return { error: `Name the Sync Area: syncarea ${action} <name>.` };
  }
  if (/[|\r\n]/.test(name)) {
    return { error: "A Sync Area's name cannot hold |." };
  }
  fields.name = name;

  const value = String(request.value ?? "").toLowerCase();
  if (action === "show") {
    if (value !== "true" && value !== "false") {
      return { error: `syncarea show takes true or false: syncarea show ${name} true.` };
    }
    fields.show = value;
    return { fields };
  }

  if (action === "update" ? value !== "pos1" && value !== "pos2" : value !== "" && value !== "pos1" && value !== "pos2") {
    return {
      error: action === "update"
        ? `Say which corner to move: syncarea update ${name} pos1, or pos2.`
        : `syncarea new takes pos1 or pos2 before a position: syncarea new ${name} pos1 <x> <y> <z>.`,
    };
  }
  // No word on `new` leaves the corner empty: a new area starts as one block, and an area
  // that exists is refused rather than having a corner moved that nobody named.
  fields.corner = value === "pos2" ? "2" : value === "pos1" ? "1" : "";

  let at = request.position;
  if (at === undefined || at === null) {
    if (origin.location === undefined) {
      return { error: `From the console, give the corner's x y z: syncarea ${action} ${name} ${value || "pos1"} <x> <y> <z>.` };
    }
    at = { x: origin.location.x, y: origin.location.y - 1, z: origin.location.z };
  }

  fields.x = Math.floor(at.x);
  fields.y = Math.floor(at.y);
  fields.z = Math.floor(at.z);
  fields.dimension = origin.dimension ?? "";
  return { fields };
}

// The wrapper's answer, `scriptevent bedrocksentry:reply {"to":name,"ok":bool,"lines":[...]}`,
// or null when it breaks that layout.
export function parseReply(message) {
  let value;
  try {
    value = JSON.parse(message);
  } catch {
    return null;
  }

  if (value === null || typeof value !== "object") return null;
  const { to, ok, lines } = value;
  if (typeof to !== "string" || to.length === 0 || typeof ok !== "boolean") return null;
  if (!Array.isArray(lines) || !lines.every((line) => typeof line === "string")) return null;
  return { to, ok, lines };
}

// D107. A circle drawn inside its box, as a ticking area added with `tickingarea add circle`
// keeps the chunks within its radius: rings on its bottom and top faces, as chords about
// CHORD_BLOCKS long, and posts at eight compass points between them so that a player near
// the edge, far from both rings, still sees where it runs.
export const CHORD_BLOCKS = 4;

function circleSegments(area, near, radius, limit, segments) {
  const cx = (area.min.x + area.max.x + 1) / 2;
  const cz = (area.min.z + area.max.z + 1) / 2;
  const r = (area.max.x + 1 - area.min.x) / 2;
  const bottom = area.min.y;
  const top = area.max.y + 1;
  const at = (angle, y) => ({ x: cx + r * Math.cos(angle), y, z: cz + r * Math.sin(angle) });

  const steps = Math.max(24, Math.ceil((2 * Math.PI * r) / CHORD_BLOCKS));
  for (const y of [bottom, top]) {
    for (let k = 0; k < steps; k++) {
      const from = at((2 * Math.PI * k) / steps, y);
      const to = at((2 * Math.PI * (k + 1)) / steps, y);
      if (!reaches(from, to, near, radius)) continue;
      if (segments.length >= limit) return;
      segments.push(segment(from, to));
    }
  }

  for (let k = 0; k < 8; k++) {
    if (!edgePieces(at((Math.PI * k) / 4, bottom), "y", top, near, radius, limit, segments)) return;
  }
}

// D107. `showtickingarea <name> <true|false>`. The game holds the Boolean to true or false,
// from the console too (measured on BDS 1.26.52.3), so only the name is checked here.
export function tickRequest(request, origin) {
  const name = String(request.name ?? "").trim();
  if (name.length === 0) {
    return { error: "Name the ticking area: showtickingarea <name> true." };
  }
  if (/[|\r\n]/.test(name)) {
    return { error: "A ticking area's name cannot hold |." };
  }

  return {
    fields: {
      player: origin.player ?? "",
      show: request.show === true ? "true" : "false",
      dimension: origin.dimension ?? "",
      name,
    },
  };
}

// D108. restorearea <area> [backup] [from] [to]: a Sync Area by name with the backup
// optional, or `pos`, a backup and two corners in the player's dimension. A String
// parameter refuses a bare number (BDS 1.26.52.3), so corners come after the backup.
// Returns { fields } for the relay, or { error } for the person who typed it.
export function restoreRequest(request, origin) {
  const area = String(request.area ?? "").trim();
  const backup = String(request.backup ?? "").trim();
  if (area.length === 0) {
    return { error: "Name a Sync Area, or give corners: restorearea pos latest <x1> <y1> <z1> <x2> <y2> <z2>." };
  }
  if (/[|\r\n]/.test(area) || /[|\r\n]/.test(backup)) {
    return { error: "A name cannot hold |." };
  }

  const fields = {
    player: origin.player ?? "",
    backup,
    dimension: origin.dimension ?? "",
    x1: "", y1: "", z1: "", x2: "", y2: "", z2: "",
    name: "",
  };

  const from = request.from ?? undefined;
  const to = request.to ?? undefined;
  if ((from === undefined) !== (to === undefined)) {
    return { error: `Give both corners: restorearea pos ${backup || "latest"} <x1> <y1> <z1> <x2> <y2> <z2>.` };
  }

  if (from === undefined) {
    fields.name = area;
    return { fields };
  }

  if (area.toLowerCase() !== "pos") {
    return { error: `Corners follow pos: restorearea pos ${backup || "latest"} <x1> <y1> <z1> <x2> <y2> <z2>.` };
  }

  // A Location arrives with x and z at the block's centre; the block is its floor.
  fields.x1 = Math.floor(from.x);
  fields.y1 = Math.floor(from.y);
  fields.z1 = Math.floor(from.z);
  fields.x2 = Math.floor(to.x);
  fields.y2 = Math.floor(to.y);
  fields.z2 = Math.floor(to.z);
  return { fields };
}

// A command as a player typed it, for the `cmd|player|command line` relay (D106): the
// name, then each argument given, a Location as its whole block, and a word with a space
// quoted. Nothing typed after the last argument given is shown.
export function commandLine(name, args) {
  const words = [name];
  for (const arg of args) {
    if (arg === undefined || arg === null || arg === "") {
      continue;
    }
    if (typeof arg === "object" && "x" in arg) {
      words.push(`${Math.floor(arg.x)} ${Math.floor(arg.y)} ${Math.floor(arg.z)}`);
      continue;
    }
    const text = String(arg).replace(/[|\r\n]/g, " ");
    words.push(/\s/.test(text) ? `"${text.replace(/"/g, "'")}"` : text);
  }
  return words.join(" ");
}
