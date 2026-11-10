# Companion Pack

Source for the BedrockSentry companion behavior pack: a Bedrock add-on that
reports deaths and player positions to the wrapper by printing lines to the
server's console. Written in JavaScript against the stable `@minecraft/server`
2.8.0 module, so it is not a .NET project and is not part of
`BedrockSentry.sln`.

This folder is deliberately **not** named `behavior_packs/` — that is what BDS
calls the runtime folder it loads packs from, and this pack is source that has
to be committed. Keeping the two names apart keeps source from reading as
runtime data.

Note that `behavior_packs/` is **not** ignored by name. `.gitignore`'s
runtime-data rules are rooted — `/instances/`, `/bedrock-server/`, `/bds/`,
`/backups/` — precisely so that nothing under `companion-pack/` or `src/` is
caught by a folder-name match. Real pack data is still ignored, because it lives
under those instance roots.

## Files

- `manifest.json` — `format_version` 3. The header uuid is the pack's identity.
  One setting, `bedrocksentry:tell_death_location`, decides whether a player is
  told where they died.
- `scripts/main.js` — the script.
- `scripts/lib.js` — the parts with no game API, kept apart so they can run under Node.

## What it prints

Every line is `[BedrockSentry] <kind>|<field>|...`, split on `|`. A field before
the last never contains `|`; where a line carries a sentence, the sentence is the
last field and may contain anything but a line break.

| Kind | Fields | When |
|---|---|---|
| `hello` | protocol, pack version | the world has loaded |
| `probe` | protocol | the console sent `scriptevent bedrocksentry:probe` |
| `death` | name, cause, killer type, killer name, x, y, z, dimension, sentence | a player died |
| `join` | name, x, y, z, dimension, sentence | a player's first spawn after joining |
| `leave` | name, x, y, z, dimension, sentence | a player left |
| `where`, `where-end` | name, x, y, z, dimension; count | the console sent `scriptevent bedrocksentry:where` |
| `outline` | group, count | the last part of a set of outlines arrived |
| `area` | player, action, corner, show, x, y, z, dimension, name | an operator typed `syncarea` |
| `tickshow` | player, show, dimension, name | an operator typed `showtickingarea` |
| `restore` | player, backup, dimension, x1, y1, z1, x2, y2, z2, name | an operator typed `restorearea` |
| `cmd` | player, command line | a player ran one of the commands below; the console's own are not relayed |

A name is `Player.name` as the Script API returns it. The pack cannot read an
XUID, so the wrapper matches the name against the one printed on that player's
connect line. Coordinates are whole blocks; the dimension is the id the Script
API returns, such as `minecraft:nether`.

BDS prints none of this unless `content-log-console-output-enabled=true` in the
server's `server.properties`, and it ships that key `false`.

## Commands

Registered at startup through `customCommandRegistry`, operator level only, and run
from the server console too (`D106`):

| Command | Answers |
|---|---|
| `listcord [player]` | every online player's whole-block x y z and dimension, or that one player's |
| `syncarea <new\|update\|show\|list> [name] [pos1\|pos2\|true\|false] [x y z]` | relayed to the wrapper, which edits the server's Sync Areas and answers |
| `showtickingarea <name> <true\|false>` | relayed to the wrapper, which reads the world's ticking areas, outlines or stops outlining that one, and answers (`D107`) |
| `restorearea <name\|pos> [backup] [x1 y1 z1] [x2 y2 z2]` | relayed to the wrapper, which reads the area out of a backup and restarts the server to put it back (`D108`) |

`syncarea new <name> [pos1|pos2] [x y z]` adds an area with both corners at one block, and on an
area that exists sets the corner named, so `new … pos1` then `new … pos2` makes the box;
`syncarea update <name> <pos1|pos2> [x y z]` moves a corner; `syncarea show <name>
<true|false>` ticks or unticks Show in game; `syncarea list` lists them. With no x y z a
corner is the block under the player. A corner is the word `pos1` or `pos2` because BDS
refuses a bare number in a `String` or `Enum` parameter, and the pack checks every word
itself because the console does not hold a registered enum to its values (BDS 1.26.52.3).
The `area` line is relayed only when the request is whole; a refusal answers at once.
From the console a corner needs x y z, and `~` counts from 0 0 0.

`restorearea <name> [backup]` restores one of the server's Sync Areas;
`restorearea pos <backup> <x1> <y1> <z1> <x2> <y2> <z2>` restores the blocks between two
corners in the player's dimension, or the overworld from the console. The backup is
`latest` or a backup's name on disk, and comes before the corners because a `String`
parameter refuses a bare number. There is no confirmation: the wrapper reads the backup
while the server runs, then restarts it with 30 seconds' warning.

The wrapper answers a player with `scriptevent bedrocksentry:reply
{"to":"Steve","ok":true,"lines":[…]}`, which the pack sends to that player, in red when
`ok` is false. The console is answered on the wrapper's console page.

## Outlines

`scriptevent bedrocksentry:outline` hands the pack a group of areas to outline (`D106`):
`{"g":"sync","p":1,"n":1,"a":[["nether",0,60,0,15,80,15,"ffaa00"]]}`, the group, this
part and how many parts, then each area's dimension as `execute in` names it, its two
corner blocks and its colour. A set longer than BDS's 2,048-character message is sent
in parts, and the group's outlines are replaced once the last part arrives; an empty
list clears them. The wrapper sends the Sync Areas with Show in game ticked after
every `hello` and every change. Every second the pack draws each area's edges within
128 blocks of each operator as `bedrocksentry:outline_line`, a thin full-bright line
in the area's colour, one particle per piece of an edge of at most 16 blocks, at most
400 pieces per operator; only that operator sees them (`D109`). Each piece lives 1.25
seconds, so the line does not go out between rounds. The particle is defined in
`companion-resources/`, a resource pack the wrapper switches on with this one; a player
who declines the server's resource packs sees no outline.

From 1.4.0 an area may carry `"circle"` as a ninth element: the pack draws the circle
inside its bounds instead, rings of chords about 4 blocks long on their bottom and top
faces and posts at eight compass points between them, so a player near its edge sees it. The wrapper sends the
world's ticking areas with Show in game ticked as the group `tick`, each the full height
of its dimension over the whole chunks it keeps loaded (`D107`).

## How it reaches a server

The Host project copies this folder, without this README, beside the wrapper's
executable, and a release archive without `manifest.json` and `scripts/main.js`
fails `scripts/ReleaseContents.ps1`. At every start the wrapper imports the
folder into its add-on library through the same path an upload takes, so a
version the library already holds is left as it is (`D93`). A server gets it
the way it gets any library pack (`D77`).

## Changing it

A change to any file here needs a new version in `manifest.json` and in
`PACK_VERSION` in `scripts/main.js`. The wrapper's add-on library keeps a pack by
uuid and version and never overwrites a version it holds, so a change under the
same version would never reach it. `CompanionPackTests` fails until the version
moves, and holds the uuid, protocol, setting name and dimension names to the
values in `BedrockSentry.Core`.
