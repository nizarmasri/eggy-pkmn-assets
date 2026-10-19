// Where each game's Pokémon live inside the PokéModels archive, and which files a battle model needs.
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { readEnd, readCentralDirectory } from './zip.mjs';
import { readRange } from './mega.mjs';

// Source priority: the newest, most detailed rips first.
export const PRIORITY = ['sv', 'za', 'la', 'swsh', 'lgpe'];

// Model and animation zips per game. Later zips in a list override earlier ones file by file
// (Scarlet/Violet's DLC packs carry patched copies of base-game files).
export const GAMES = {
  sv: {
    format: 'trinity',
    model: ['SV-Poke.zip', 'SV-PokeDLC1.zip', 'SV-PokeDLC2.zip'],
    anim: ['SV-PokeAnim.zip', 'SV-PokeAnimDLC1.zip', 'SV-PokeAnimDLC2.zip'],
    // pokemon/data/pm0025/pm0025_00_00/<file>
    modelRe: /^pokemon\/data\/pm(\d{4})\/(pm\d{4}_\d{2}_\d{2})\/([^/]+)$/,
    animRe: /^pokemon\/data\/pm(\d{4})\/(pm\d{4}_\d{2}_\d{2})\/([^/]+)$/,
  },
  za: {
    format: 'trinity',
    model: ['ZA-Poke.zip', 'ZA-PokeDLC.zip'],
    anim: ['ZA-PokeAnim.zip', 'ZA-PokeAnimDLC.zip'],
    modelRe: /^ik_pokemon\/data\/pm(\d{4})\/(pm\d{4}_\d{2}_\d{2})\/([^/]+)$/,
    animRe: /^ik_pokemon\/data\/pm(\d{4})\/(pm\d{4}_\d{2}_\d{2})\/([^/]+)$/,
  },
  la: {
    format: 'trinity',
    model: ['LA-Poke.zip'],
    anim: ['LA-PokeAnim.zip'],
    // bin/pokemon/pm0201/pm0201_11_00/mdl/<file>, .../anm/<file>
    modelRe: /^bin\/pokemon\/pm(\d{4})\/(pm\d{4}_\d{2}_\d{2})\/mdl\/([^/]+)$/,
    animRe: /^bin\/pokemon\/pm(\d{4})\/(pm\d{4}_\d{2}_\d{2})\/anm\/([^/]+)$/,
  },
  swsh: {
    format: 'gf',
    model: ['SwSh-PokeGen1.zip', 'SwSh-PokeGen2-3.zip', 'SwSh-PokeGen4-5.zip', 'SwSh-PokeGen6-7.zip', 'SwSh-PokeGen8.zip'],
    anim: ['SwSh-PokeAnim.zip'],
    // bin/pokemon/pm0006_00/mdl/<file>, .../anm/<file>
    modelRe: /^bin\/pokemon\/(pm(\d{4})_\d{2}(?:_\d{2})?)\/mdl\/([^/]+)$/,
    animRe: /^bin\/pokemon\/(pm(\d{4})_\d{2}(?:_\d{2})?)\/anm\/([^/]+)$/,
    gfLayout: true,
  },
  lgpe: {
    format: 'gf',
    model: ['LGPE-Poke.zip'],
    anim: ['LGPE-PokeAnim.zip'],
    modelRe: /^bin\/pokemon\/(pm(\d{4})_\d{2}(?:_\d{2})?)\/mdl\/([^/]+)$/,
    animRe: /^bin\/pokemon\/(pm(\d{4})_\d{2}(?:_\d{2})?)\/anm\/([^/]+)$/,
    gfLayout: true,
  },
};

// The clips a battle uses (see conv/src/Battle.cs for how they become idle/appear/cry/...).
const TRINITY_CLIP = /^pm\d{4}_\d{2}_\d{2}_\d{5}_(battlewait01_loop|defaultwait01_loop|land0[12]|roar01|attack01(_start|_loop|_end)?|rangeattack01(_start|_loop|_end)?|damage01(_start|_loop|_end)?|down01(_start|_loop|_end)?|eye01|mouth01)\.(tranm|traef|tracm)$/;
const GF_CLIP = /^pm\d{4}_\d{2}(?:_\d{2})?_(ba01_land\w*|ba02_roar\w*|ba10_wait\w*|ba20_buturi\w*|ba21_tokusyu\w*|ba30_damage\w*|ba41_down\w*|eye01|mouth01)\.gfbanm$|\.gfbanmcfg$/;

// Model files a Trinity model needs: the full-detail mesh, skeleton, materials (the shiny _rare set too)
// and their textures, and the material table (Arceus keeps its types there). Not the lower LODs,
// physics (.hkx) or effect locators.
const trinityModelFile = (f) => /\.(trmdl|trmsh|trmbf|trskl|trmtr|trmmt|trpokecfg|bntx)$/.test(f) && !/_lod\d|_oybn/.test(f);
// Sword/Shield and Let's Go: the model, its shiny copy (<folder>_rare.gfbmdl) and every texture.
const gfModelFile = (f) => /\.(gfbmdl|bntx)$/.test(f);

export function wantsFile(game, kind, file) {
  const g = GAMES[game];
  if (kind === 'model') return g.format === 'gf' ? gfModelFile(file) : trinityModelFile(file);
  return g.format === 'gf' ? GF_CLIP.test(file) : TRINITY_CLIP.test(file);
}

// ---- Central directories, cached on disk (gzipped JSON) ----

export async function centralDirectory(files, zip, cacheDir) {
  const node = files.get(zip);
  if (!node) throw new Error(`${zip} is not in the MEGA folder`);
  const cachePath = path.join(cacheDir, 'cd', `${zip}.${node.size}.json.gz`);
  if (fs.existsSync(cachePath)) return JSON.parse(zlib.gunzipSync(fs.readFileSync(cachePath)));
  const size = node.size;
  const tailLen = Math.min(size, 70000);
  const tail = await readRange(node, size - tailLen, size - 1, `${zip} tail`);
  const { count, cdSize, cdOffset } = readEnd(tail, size);
  const cd = await readRange(node, cdOffset, cdOffset + cdSize - 1, `${zip} directory`);
  const entries = readCentralDirectory(cd, count);
  console.log(`  ${zip}: ${entries.length} files, directory ${(cdSize / 1e6).toFixed(1)} MB`);
  fs.mkdirSync(path.dirname(cachePath), { recursive: true });
  fs.writeFileSync(cachePath, zlib.gzipSync(JSON.stringify(entries)));
  fs.writeFileSync(path.join(cacheDir, '.dirty'), ''); // the workflow saves the cache when this exists
  return entries;
}

// Every species folder of a game: folder -> { dex, model: Map(file -> {zip, entry}), anim: Map }.
// Files under "[Pre-Patch]" are the archive's copies of superseded files and are skipped.
export function indexGame(game, cds) {
  const g = GAMES[game];
  const folders = new Map();
  const get = (folder, dex) => {
    let f = folders.get(folder);
    if (!f) folders.set(folder, (f = { folder, dex, model: new Map(), anim: new Map() }));
    return f;
  };
  for (const [kind, zips, re] of [['model', g.model, g.modelRe], ['anim', g.anim, g.animRe]]) {
    for (const zip of zips) {
      for (const entry of cds[zip] ?? []) {
        if (entry.name.includes('[Pre-Patch]')) continue;
        const m = re.exec(entry.name);
        if (!m) continue;
        const [folder, dex, file] = g.gfLayout ? [m[1], Number(m[2]), m[3]] : [m[2], Number(m[1]), m[3]];
        get(folder, dex)[kind].set(file, { zip, entry });
      }
    }
  }
  return folders;
}
