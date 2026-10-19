// Picks one source per model id (Scarlet/Violet > Legends Z-A > Legends Arceus > Sword/Shield > Let's Go)
// and lists exactly the zip entries its battle model and its shiny need. Ids no Switch game has a
// battle model for go to the Brilliant Diamond / Shining Pearl path (src "bdsp") or are reported missing.
//
//   node pipeline/plan.mjs <ids.json> <out plan.json> [--shards 16] [--only <regex>] [--release m1]
import fs from 'node:fs';
import path from 'node:path';
import { openFolder } from './lib/mega.mjs';
import { GAMES, PRIORITY, centralDirectory, indexGame, wantsFile } from './lib/sources.mjs';

const CACHE = process.env.MEGA_CACHE ?? 'cache';
const [idsPath, outPath] = process.argv.slice(2);
const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : fallback;
};
const shards = Number(arg('shards', 16));
const only = arg('only', '') ? new RegExp(arg('only')) : null;
const release = arg('release', 'm1');

// Species Brilliant Diamond / Shining Pearl has and no Switch battle rip does (measured: no folder with a
// model and a battle idle in SV, Z-A, LA, SwSh or LGPE). Their models come from BDSP's Unity bundles.
export const BDSP_ONLY = new Set([165, 166, 276, 277, 300, 301, 327, 351, 366, 367, 368]);

const ids = JSON.parse(fs.readFileSync(idsPath, 'utf8')).filter((e) => !only || only.test(e.id));
const files = await openFolder();
const cds = {};
for (const game of PRIORITY)
  for (const zip of [...GAMES[game].model, ...GAMES[game].anim])
    if (files.has(zip)) cds[zip] = await centralDirectory(files, zip, CACHE);
const index = Object.fromEntries(PRIORITY.map((g) => [g, indexGame(g, cds)]));

const hasModel = (g, f) => f.model.has(`${f.folder}.${GAMES[g].format === 'gf' ? 'gfbmdl' : 'trmdl'}`);
const hasIdle = (g, f) =>
  [...f.anim.keys()].some((n) => (GAMES[g].format === 'gf' ? /_ba10_wait\w*\.gfbanm$/ : /_(battle|default)wait01_loop\.tranm$/).test(n));
const folderName = (g, dex, ff) => `pm${String(dex).padStart(4, '0')}_${ff}${GAMES[g].format === 'gf' ? '' : '_00'}`;

// The folder for an id in one game: the exact form folder; a base id ("00") of a species whose only
// folders are numbered forms takes its first form.
function pickFolder(game, e) {
  const usable = [...index[game].values()].filter((f) => f.dex === e.dex && hasModel(game, f) && hasIdle(game, f));
  const exact = usable.find((f) => f.folder === folderName(game, e.dex, e.ff));
  if (exact) return exact;
  if (e.ff === '00') {
    const forms = usable.filter((f) => /^pm\d{4}_1\d(_00)?$/.test(f.folder)).sort((a, b) => a.folder.localeCompare(b.folder));
    return forms[0] ?? null;
  }
  return null;
}

const chosen = new Map(); // base id -> game, so a female or a form comes from the same game when it can
function plan(e) {
  const base = e.female ?? e.id.split('-')[0];
  const prefer = chosen.get(base);
  const order = prefer ? [prefer, ...PRIORITY.filter((g) => g !== prefer)] : PRIORITY;
  for (const game of order) {
    const f = pickFolder(game, e);
    if (!f) continue;
    const take = (kind) =>
      [...f[kind].entries()]
        .filter(([file]) => wantsFile(game, kind, file))
        .map(([file, { zip, entry }]) => ({ kind, file, zip, ...entry }));
    const entries = [...take('model'), ...take('anim')];
    const shiny = entries.some((x) => /_rare[._]/.test(x.file));
    if (!e.female && !e.id.includes('-')) chosen.set(e.id, game);
    return { ...e, src: game, folder: f.folder, shiny, entries };
  }
  if (BDSP_ONLY.has(e.dex)) return { ...e, src: 'bdsp' };
  return { ...e, src: null, error: e.female ? 'no female model in any Switch game' : 'no Switch game has this form with battle clips' };
}

// Base ids first, so their game is known when their forms and females are planned.
const order = [...ids].sort((a, b) => Number(Boolean(a.female || a.id.includes('-'))) - Number(Boolean(b.female || b.id.includes('-'))));
const planned = new Map(order.map((e) => [e.id, plan(e)]));
const jobs = ids.map((e) => planned.get(e.id)).map((j) => ({ ...j, shard: j.src === 'bdsp' ? 'bdsp' : j.dex % shards }));

const bySrc = {};
for (const j of jobs) {
  const k = j.src ?? 'missing';
  bySrc[k] ??= { ids: 0, mb: 0 };
  bySrc[k].ids++;
  bySrc[k].mb += (j.entries ?? []).reduce((n, x) => n + x.csize, 0) / 1e6;
}
for (const v of Object.values(bySrc)) v.mb = Math.round(v.mb);
const perShard = {};
for (const j of jobs) if (j.entries) perShard[j.shard] = (perShard[j.shard] ?? 0) + 1;
const missing = jobs.filter((j) => !j.src).map((j) => `${j.id}: ${j.error}`);
console.log(`${jobs.length} ids: ${JSON.stringify(bySrc)}`);
console.log(`per shard: ${JSON.stringify(perShard)}`);
for (const m of missing) console.log(`  missing ${m}`);
fs.mkdirSync(path.dirname(path.resolve(outPath)), { recursive: true });
fs.writeFileSync(outPath, JSON.stringify({ release, shards, bySrc, missing, jobs }, null, 1));
if (process.env.GITHUB_OUTPUT) {
  const matrix = [...new Set(jobs.filter((j) => j.entries).map((j) => j.shard))].sort((a, b) => a - b);
  fs.appendFileSync(process.env.GITHUB_OUTPUT, `shards=${JSON.stringify(matrix)}\nbdsp=${jobs.some((j) => j.src === 'bdsp')}\n`);
}
if (process.env.GITHUB_STEP_SUMMARY)
  fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, [`### Plan: ${jobs.length} ids`, '', '```', JSON.stringify(bySrc), ...missing, '```', ''].join('\n'));
