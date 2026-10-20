# eggy-pkmn-assets

Art, sound and 3D packs for Eggy's PokéBattle, published as releases and fetched (sha256-checked) by
the Eggy build.

| Release | What |
|---|---|
| `p1-676c10ec` | Sprite sheets, cries, Gen 4 battle themes, icons and move-effect images from Pokémon Showdown |
| `m1-<hash>` | The 3D pack: `pkmn-m1-<hash>.tar` in 10 MiB parts (`.01`, `.02`…), `models.lock.json` for eggy, and every file on its own (`<id>.glb`, `models.json`, `arenas.json`, `arena-<id>.glb`) |
| `m1-<hash>-shiny` | That pack's shiny companions (`<id>.shiny.glb`), also inside its tarball |
| `m1-<hash>-review` | Review sheets of that pack: contact sheets of every model, close-ups, every arena from the battle camera |
| `a1-<hash>` | The arena pack: `pkmn-a1-<hash>.tar` in 10 MiB parts, `arenas.lock.json` for eggy, `arenas.json` and every `<id>.glb` on its own |
| `a1-<hash>-review` | Each arena from the battle camera, its fog and glow cards alone over black at t = 0 and 3 s, `motion.md` (what moves, which cards fade at their edges) |
| `a1-research` | What the PokéModels folder holds beside the battle maps (battle cameras included) and an inventory of the maps from their bytes |
| `m1-diag` | Diagnostics the pipeline was built from (not part of any pack) |

## The 3D pack

The tarball unpacks at its root: `models.json`, `<id>.glb`, `<id>.shiny.glb`, `arenas/arenas.json` and
`arenas/<id>.glb`. Model ids are Showdown sprite ids with no shiny or gender suffix: every species of
Generations 1–4, the forms Showdown can put on the field (Rotom, Deoxys, Giratina, Shaymin, Burmy,
Wormadam, Shellos, Gastrodon, Cherrim, Castform, Unown, every Arceus type). `models.json` (v 1) gives each
model's file, size, sha256, source game, height in metres, clip lengths, attack hit times and, where the
game has one, `shiny: { file, bytes, sha256 }`: a companion holding only the materials the shiny changes,
named as the base model's.

Materials the game draws in a special way carry glTF material `extras.role`: `glow` on a Pokémon's flames,
glow cores and what its game adds onto the scene (Gastly's gas, Elekid's thunder, Cresselia's rings) and on an
arena's light shafts and lamps (additive, unlit, no depth write), `water`, `fog` and `decal` on arenas. Parts
the games draw see-through (Spiritomb's vortex, Castform's hoods) are glTF `BLEND` materials.

## The arena pack

Let's Go's battle maps, on their own so they can change without the models downloading again
(`.github/workflows/a1.yml`). The tarball unpacks at its root: `arenas.json` and `<id>.glb`, laid out as the m1
pack's `arenas/` folder. `arenas.lock.json` has the shape of `models.lock.json` (`pack`, `file`, `sha256`, `bytes`,
`url`, `parts`). `arenas.json` keeps its v 1 fields: per arena its file, size, sha256, name, kind, `lit`, the
singles and doubles spots and the floor radius, in metres with the floor at the centre at y = 0.

What an arena's materials carry (glTF material `extras`, which three's GLTFLoader puts in `material.userData`):

| Field | Meaning | Where |
|---|---|---|
| `role: "water"` | The sea or a pool: the game draws its own water | sea, beach, bridge, jetty, deck, the ponds, Cerulean Gym |
| `role: "glow"` | Light added onto the scene: unlit, additive, no depth write | the forest's light shafts, Agatha's torch flames, the Champion's and the stadium's light streaks, the blue-rock cave's crystals, Saffron Gym's floor lights, the beach's surf line, the deck's lights |
| `role: "fog"` | A fog card or ring | the caves, the Elite Four's rooms, the stadium, the mansion, the tower, Saffron Gym |
| `role: "decal"` | Painted on the ground: drawn over it | gym floor markings, the fields' second ground layer |
| `role: "foliage"` | Vegetation cards and leaf or grass clusters, up to 8 m tall: the game sways them | the routes' grass, bushes and trees (grass, road, town, mountain, bridge, jetty), the forest's plants, Celadon Gym's planters |
| `role: "sky"` | Sky domes and cloud layers | every open-air arena's `btl_sky` dome, the deck's, Cerulean's, Celadon's and the Champion's skies |
| `scroll: [du, dv]` | The colour texture is sampled at `uv + scroll × t` (TEXCOORD_0, glTF UV units per second, V down) | fog, wave sheets, flames, light shafts and streaks |
| `mask: { texture, texCoord, scroll? }` | A second texture (a glTF texture index) multiplied into alpha, sampled at `TEXCOORD_<texCoord> + scroll × t` with its sampler's wraps; its value is in every channel | the second layer of a fog ring, a wave sheet, a light streak; a flame's shape |
| `water: { flow: [x, z] }` | Which way the water drifts, in the arena's metres per second (from the file's layer offsets) | the sea, the beach, the deck, the blue-rock pond |

`COLOR_0` is RGBA. On a blended (`BLEND`) material its alpha is the surface's opacity, from the game's vertex
colours: fog rings and light shafts fade to nothing at their edges, grass cards at their tips, wave sheets lie
at 0.35, the sea thins out at the shore. On the fields' second ground layer it is the blend weight.

No arena has glTF animations and none uses `frames` (a flipbook): the rip has models and textures only. The
maps' rigs name what the game animates (the fog rings, Agatha's 28 torch flames, Cinnabar Gym's statue, the
stadium crowd, Cerulean's water) but their clips are not in it. The scrolls' directions, and which layers run
against each other, are the game's own: each texture layer's `TexNN_Translate` is where its animation had
brought it when the file was written (`±1.001`, `-4.004`: offsets that only mean something as motion). The
speeds are chosen, in metres per second over each material's own UV density: fog 0.25, waves 0.35, flames 0.8,
light shafts 0.2, other light 0.5; the water's drift 0.15. `motion.md` on the review release lists them all.

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
  parts and lock, the release layout, coverage and height checks. `pipeline/arenas/pack.mjs` does the same for
  the arena pack, with its motion list.
- `pipeline/arenas/research.mjs`, `pipeline/arenas/cameras.mjs`: every archive's directory read with ranged
  requests (a 7z's headers through a sparse file), searched for battle cameras and the maps' other files.

Jobs that run third-party code (MEGA, 7z, the converters, AssetStudio, FBX2glTF, Chrome) only get read
access to this repo; a separate publish job, running only `gh` on their artifacts, writes the releases.

## Fetching on a slow connection

`node fetch.mjs --tag <tag> --out <dir> [--only <regex>]` downloads release assets in Range segments,
resumes after any failure or restart, and checks each file against the release's `SHA256SUMS`.
