# Companion Resources

The resource pack that goes with `companion-pack/`. It defines one particle,
`bedrocksentry:outline_line`: a thin, full-bright line in the colour the
companion gives it, which the companion draws along the edges of the Sync Areas
and ticking areas an operator asked to see (`D109`). Each line lives 1.25
seconds and the companion draws it again every second, so it does not flicker.

The wrapper switches this pack on and off with the companion. The companion does
not name it as a dependency, so a server without it still reports deaths and
positions and only the outlines are missing.

A player's game downloads it from the server when they join. A player who
declines the download sees no outlines. The wrapper does not change
`texturepack-required` in `server.properties`.

## Files

- `manifest.json`: `format_version` 3, one `resources` module. The header uuid is
  `CompanionPack.ResourceUuid` in `BedrockSentry.Core`.
- `particles/outline_line.json`: the particle. The script sets
  `variable.length`, `variable.dx`, `variable.dy`, `variable.dz`,
  `variable.life` and `variable.color`. Its width grows with distance from the
  camera, so a far line stays visible.
- `textures/particle/bedrocksentry_line.png`: 16 by 16 white pixels, tinted by
  the particle.

## Changing it

As with the companion, a change here needs a new version in `manifest.json`.
The add-on library never overwrites a version it holds, and
`CompanionPackTests` fails until the version moves.
