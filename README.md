# eggy-pkmn-assets

Art, sound and 3D packs for Eggy's PokéBattle, published as releases and fetched (sha256-checked) by
the Eggy build.

| Release | What |
|---|---|
| `p1-676c10ec` | Sprite sheets, cries, Gen 4 battle themes, icons and move-effect images from Pokémon Showdown |
| `m1-<hash>` | The 3D pack: `pkmn-m1-<hash>.tar` in 10 MiB parts (`.01`, `.02`…), `models.lock.json` for eggy, and every file on its own (`<id>.glb`, `models.json`, `arenas.json`, `arena-<id>.glb`) |
| `m1-<hash>-shiny` | That pack's shiny companions (`<id>.shiny.glb`), also inside its tarball |
| `m1-<hash>-review` | Review sheets of that pack: contact sheets of every model, close-ups, every arena from the battle camera |
| `m1-diag` | Diagnostics the pipeline was built from (not part of any pack) |

## The 3D pack

The tarball unpacks at its root: `models.json`, `<id>.glb`, `<id>.shiny.glb`, `arenas/arenas.json` and
`arenas/<id>.glb`. Model ids are Showdown sprite ids with no shiny or gender suffix: every species of
Generations 1–4, the forms Showdown can put on the field (Rotom, Deoxys, Giratina, Shaymin, Burmy,
Wormadam, Shellos, Gastrodon, Cherrim, Castform, Unown, every Arceus type). `models.json` (v 1) gives each
model's file, size, sha256, source game, height in metres, clip lengths, attack hit times and, where the
game has one, `shiny: { file, bytes, sha256 }`: a companion holding only the materials the shiny changes,
named as the base model's.

Materials the game draws in a special way carry glTF material `extras.role`: `glow` on a Pokémon's flames
and glow cores and an arena's light shafts and lamps (additive, unlit, no depth write), `water`, `fog` and
`decal` on arenas.

## The pipeline

Everything heavy runs in GitHub Actions (`.github/workflows/m1.yml`); nothing is downloaded or converted on
a laptop.

- `pipeline/ids.mjs`: every model id, from the p1 pack's sprite ids (`species/p1-ids.json`) and the forms
  it draws with their base sprite.
- `pipeline/plan.mjs`: picks one game rip per id from the PokéModels MEGA archive (Scarlet/Violet, Legends
  Z-A, Legends Arceus, Sword/Shield, Let's Go) and lists the zip entries it needs; the species only
  Brilliant Diamond / Shining Pearl has go down the Unity path.
- `pipeline/fetch-entries.mjs`: ranged, decrypted MEGA reads of only those entries, CRC-checked and cached
  between runs, 16 shards.
- `conv/`: the converter (C#, on [PokeModelExporter](https://github.com/GoldenCarrotMLP/PokeModelExporter)
  at a pinned commit): game models and battle clips to raw GLBs and a report each, shiny companions,
  Arceus's types from its material table, mesh visibility as joint scale keys, Sword/Shield's
  object-space normal maps as tangent-space ones; Let's Go's battle stages as arenas.
- `pipeline/bdsp/`: Brilliant Diamond / Shining Pearl's Unity bundles through AssetStudio (built from its
  tag with animation clips in animator mode) and FBX2glTF, then the same converter.
- `pipeline/optimize.mjs`, `pipeline/arenas/optimize.mjs`: normalised, WebP (1024 px at most), meshopt
  GLBs of 3 MB at most, `models.json`, `arenas.json`.
- `pipeline/render.mjs`, `pipeline/arenas/render.mjs`, `pipeline/contact.mjs`: review sheets drawn by
  three.js in headless Chrome.
- `pipeline/pack.mjs`: the tarball (sorted, no owners or times, so the same files give the same hash), its
  parts and lock, the release layout, coverage and height checks.

Jobs that run third-party code (MEGA, 7z, the converters, AssetStudio, FBX2glTF, Chrome) only get read
access to this repo; a separate publish job, running only `gh` on their artifacts, writes the releases.

## Fetching on a slow connection

`node fetch.mjs --tag <tag> --out <dir> [--only <regex>]` downloads release assets in Range segments,
resumes after any failure or restart, and checks each file against the release's `SHA256SUMS`.
