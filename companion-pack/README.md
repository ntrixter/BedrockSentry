# Companion Pack

Source for the BedrockSentry companion behavior pack — the Minecraft add-on
that gives the wrapper visibility the console alone doesn't provide, relaying
in-game events back through the server's content log. Written in JavaScript
against the Bedrock Scripting API, so it is not a .NET project and is not part
of `BedrockSentry.sln`.

This folder is deliberately **not** named `behavior_packs/` — that is what BDS
calls the runtime folder it loads packs from, and this pack is source that has
to be committed. Keeping the two names apart keeps source from reading as
runtime data.

Note that `behavior_packs/` is **not** ignored by name. `.gitignore`'s
runtime-data rules are rooted — `/instances/`, `/bedrock-server/`, `/bds/`,
`/backups/` — precisely so that nothing under `companion-pack/` or `src/` is
caught by a folder-name match. Real pack data is still ignored, because it lives
under those instance roots.

Built in milestone 12, starting with death tracking; ships and updates
alongside the wrapper's own binaries from milestone 13.5 onward. See `docs/design.md`
section 9 for the full design.
