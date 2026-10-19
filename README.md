# eggy-pkmn-assets

Art, sound and 3D packs for Eggy's PokéBattle, published as releases and fetched (sha256-checked) by
the Eggy build.

| Release | What |
|---|---|
| `p1-676c10ec` | Sprite sheets, cries, Gen 4 battle themes, icons and move-effect images from Pokémon Showdown |
| `m1-<hash>` | 3D battle models (one GLB per Showdown sprite id, shiny companions) and battle arenas |
| `m1-<hash>-review` | Review sheets of that pack |

## The 3D pipeline

Everything heavy runs in GitHub Actions; nothing is downloaded or converted on a laptop.

- `pipeline/ids.mjs`: every model id: the p1 pack's sprite ids (`species/p1-ids.json`), Showdown forms
  it has no sheet for, and a female model wherever it has a female sheet.
- `pipeline/plan.mjs`: picks one game rip per id from the PokéModels MEGA archive (Scarlet/Violet,
  Legends Z-A, Legends Arceus, Sword/Shield, Let's Go) and lists the zip entries it needs.
- `pipeline/fetch-entries.mjs`: ranged, decrypted MEGA reads of only those entries, CRC-checked and
  cached between runs.
- `conv/`: the converter (C#, built on [PokeModelExporter](https://github.com/GoldenCarrotMLP/PokeModelExporter)
  at a pinned commit): game models and battle clips to raw GLBs plus a report per model.
- `pipeline/optimize.mjs`: normalised, WebP, meshopt GLBs and `models.json`.
- `pipeline/arenas/`: Let's Go's battle stages as arena GLBs and `arenas.json`.
- `pipeline/render.mjs`, `pipeline/arenas/render.mjs`: review sheets drawn by three.js in headless Chrome.

Jobs that run third-party code (MEGA, 7z, the converters, Chrome) only get read access to this repo;
a separate publish job, running only `gh` on their artifacts, writes the releases.

## Fetching on a slow connection

`node fetch.mjs --tag <tag> --out <dir> [--only <regex>]` downloads release assets in Range segments,
resumes after any failure or restart, and checks each file against the release's `SHA256SUMS`.
