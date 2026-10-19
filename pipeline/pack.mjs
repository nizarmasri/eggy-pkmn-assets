// The production pack, from the shards' optimised models and the arenas: one tarball whose files unpack
// at the root (models.json, <id>.glb, <id>.shiny.glb, arenas/arenas.json, arenas/<id>.glb), named by its
// own hash (m1-<first 8 hex of its sha256>), cut in 10 MiB parts, with the lock eggy's Dockerfile reads
// (the shape of games/pokebattle/data/assets.lock.json). Also lays out what the releases carry:
//   <rel>/main   the parts, models.lock.json, models.json, report.json, arenas.json, arena-<id>.glb and every
//                <id>.glb, with SHA256SUMS
//   <rel>/shiny  every <id>.shiny.glb, with SHA256SUMS (a release holds at most 1000 assets)
// and checks the budgets: every GLB at most 3 MB.
//
//   node pipeline/pack.mjs --models <dir>... --arenas <dir> --plan <plan.json> --work <dir> --rel <dir>
//                          [--dex <Showdown pokedex.json>] [--repo nizarmasri/eggy-pkmn-assets]
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';

const argv = process.argv.slice(2);
const many = (name) => {
  const out = [];
  for (let i = argv.indexOf(`--${name}`) + 1; i > 0 && i < argv.length && !argv[i].startsWith('--'); i++) out.push(argv[i]);
  return out;
};
const one = (name, fallback) => many(name)[0] ?? fallback;
const modelDirs = many('models');
const arenasDir = one('arenas');
const plan = JSON.parse(fs.readFileSync(one('plan'), 'utf8'));
const work = path.resolve(one('work', 'work/pack'));
const rel = path.resolve(one('rel', 'rel'));
const repo = one('repo', 'nizarmasri/eggy-pkmn-assets');
const PART = 10 * 1024 * 1024;
const MAX_GLB = 3 * 1024 * 1024;

const sha = (buf) => crypto.createHash('sha256').update(buf).digest('hex');
const shaFile = (f) => {
  const h = crypto.createHash('sha256');
  const fd = fs.openSync(f, 'r');
  const buf = Buffer.alloc(1 << 20);
  let n;
  while ((n = fs.readSync(fd, buf, 0, buf.length, null)) > 0) h.update(buf.subarray(0, n));
  fs.closeSync(fd);
  return h.digest('hex');
};

const root = path.join(work, 'root');
fs.rmSync(work, { recursive: true, force: true });
fs.mkdirSync(path.join(root, 'arenas'), { recursive: true });
for (const d of ['main', 'shiny']) fs.mkdirSync(path.join(rel, d), { recursive: true });

// ---- Models ----
const models = {};
const report = {};
const problems = [];
for (const dir of modelDirs) {
  const idx = path.join(dir, 'models.json');
  if (!fs.existsSync(idx)) { problems.push(`${dir}: no models.json`); continue; }
  const part = JSON.parse(fs.readFileSync(idx, 'utf8')).models;
  const rep = fs.existsSync(path.join(dir, 'report.json')) ? JSON.parse(fs.readFileSync(path.join(dir, 'report.json'), 'utf8')) : {};
  for (const [id, m] of Object.entries(part)) {
    if (models[id]) { problems.push(`${id}: in two shards`); continue; }
    for (const f of [m.file, ...(m.shiny ? [m.shiny.file] : [])]) {
      const src = path.join(dir, f);
      const buf = fs.readFileSync(src);
      const want = f === m.file ? m.sha256 : m.shiny.sha256;
      if (sha(buf) !== want) problems.push(`${f}: sha256 does not match models.json`);
      if (buf.length > MAX_GLB) problems.push(`${f}: ${(buf.length / 1e6).toFixed(2)} MB, over 3 MB`);
      fs.copyFileSync(src, path.join(root, f));
    }
    models[id] = m;
    if (rep[id]) report[id] = rep[id];
  }
}
const ids = Object.keys(models).sort();

// Heights against the Pokédex (Showdown's pokedex.json, --dex): a model whose height is off by more than
// half or double is listed (a wrong unit or a folder that is another species).
const dexPath = one('dex');
const heights = [];
if (dexPath && fs.existsSync(dexPath)) {
  const dex = JSON.parse(fs.readFileSync(dexPath, 'utf8'));
  for (const id of ids) {
    const want = dex[id.replace(/-/g, '')]?.heightm;
    const got = models[id].height;
    if (!want || !got) continue;
    const ratio = got / want;
    if (ratio < 0.5 || ratio > 2) heights.push({ id, height: got, pokedex: want, ratio: Math.round(ratio * 100) / 100 });
  }
}
fs.writeFileSync(path.join(root, 'models.json'), JSON.stringify({ v: 1, models: Object.fromEntries(ids.map((id) => [id, models[id]])) }, null, 1) + '\n');

// Coverage: every planned id is here, or the plan or the conversion says why not.
const planned = plan.jobs.filter((j) => !j.id.includes('@'));
const missing = planned.filter((j) => !models[j.id]).map((j) => ({ id: j.id, dex: j.dex, why: j.error ?? (report[j.id]?.error ? `conversion failed: ${report[j.id].error.split('\n')[0]}` : `not converted (${j.src ?? 'no source'})`) }));

// ---- Arenas ----
const arenas = JSON.parse(fs.readFileSync(path.join(arenasDir, 'arenas.json'), 'utf8'));
for (const a of Object.values(arenas.arenas)) {
  const buf = fs.readFileSync(path.join(arenasDir, a.file));
  if (buf.length > MAX_GLB) problems.push(`arenas/${a.file}: ${(buf.length / 1e6).toFixed(2)} MB, over 3 MB`);
  fs.copyFileSync(path.join(arenasDir, a.file), path.join(root, 'arenas', a.file));
}
fs.copyFileSync(path.join(arenasDir, 'arenas.json'), path.join(root, 'arenas', 'arenas.json'));

// ---- The tarball: same files, same bytes, every run (sorted names, no owners, no times) ----
const files = [];
(function walk(rel) {
  for (const e of fs.readdirSync(path.join(root, rel), { withFileTypes: true })) {
    const p = rel ? `${rel}/${e.name}` : e.name;
    if (e.isDirectory()) walk(p);
    else files.push(p);
  }
})('');
files.sort();
fs.writeFileSync(path.join(work, 'files.txt'), files.join('\n') + '\n');
const tmpTar = path.join(work, 'pack.tar');
execFileSync('tar', ['--create', '--file', tmpTar, '--directory', root, '--format=gnu', '--owner=0', '--group=0', '--numeric-owner',
  '--mtime=@0', '--mode=a+rX,u+w,go-w', '--no-recursion', '--files-from', path.join(work, 'files.txt')]);
const tarSha = shaFile(tmpTar);
const pack = `m1-${tarSha.slice(0, 8)}`;
const file = `pkmn-${pack}.tar`;
const tarPath = path.join(work, file);
fs.renameSync(tmpTar, tarPath);
const bytes = fs.statSync(tarPath).size;

// ---- Parts and the lock ----
const parts = [];
{
  const fd = fs.openSync(tarPath, 'r');
  const buf = Buffer.alloc(PART);
  for (let i = 0, off = 0; off < bytes; i++, off += PART) {
    const n = fs.readSync(fd, buf, 0, Math.min(PART, bytes - off), off);
    const name = `${file}.${String(i + 1).padStart(2, '0')}`;
    fs.writeFileSync(path.join(rel, 'main', name), buf.subarray(0, n));
    parts.push({ name, bytes: n, sha256: sha(buf.subarray(0, n)) });
  }
  fs.closeSync(fd);
}
if (parts.length > 99) problems.push(`${parts.length} parts: the lock's two-digit part names run out at 99`);
const lock = { pack, file, sha256: tarSha, bytes, url: `https://github.com/${repo}/releases/download/${pack}/`, parts };
fs.writeFileSync(path.join(rel, 'main', 'models.lock.json'), JSON.stringify(lock, null, 2) + '\n');

// ---- The rest of the release assets ----
const main = path.join(rel, 'main'), shiny = path.join(rel, 'shiny');
fs.copyFileSync(path.join(root, 'models.json'), path.join(main, 'models.json'));
fs.copyFileSync(path.join(root, 'arenas', 'arenas.json'), path.join(main, 'arenas.json'));
for (const a of Object.values(arenas.arenas)) fs.copyFileSync(path.join(root, 'arenas', a.file), path.join(main, `arena-${a.file}`));
for (const id of ids) {
  fs.copyFileSync(path.join(root, models[id].file), path.join(main, models[id].file));
  if (models[id].shiny) fs.copyFileSync(path.join(root, models[id].shiny.file), path.join(shiny, models[id].shiny.file));
}
const summary = {
  pack,
  bytes,
  parts: parts.length,
  models: ids.length,
  shinies: ids.filter((id) => models[id].shiny).length,
  arenas: Object.keys(arenas.arenas).length,
  modelBytes: ids.reduce((n, id) => n + models[id].bytes, 0),
  shinyBytes: ids.reduce((n, id) => n + (models[id].shiny?.bytes ?? 0), 0),
  bySrc: ids.reduce((o, id) => ({ ...o, [models[id].src]: (o[models[id].src] ?? 0) + 1 }), {}),
  missing,
  heights,
  problems,
};
fs.writeFileSync(path.join(main, 'report.json'), JSON.stringify({ summary, models: report }, null, 1) + '\n');
for (const d of [main, shiny]) {
  const names = fs.readdirSync(d).filter((f) => f !== 'SHA256SUMS').sort();
  fs.writeFileSync(path.join(d, 'SHA256SUMS'), names.map((f) => `${shaFile(path.join(d, f))}  ${f}`).join('\n') + '\n');
}
for (const d of [main, shiny]) {
  const n = fs.readdirSync(d).length;
  if (n > 1000) problems.push(`${path.basename(d)}: ${n} assets, over a release's 1000`);
}
fs.writeFileSync(path.join(work, 'summary.json'), JSON.stringify(summary, null, 1) + '\n');

const mb = (n) => (n / 1e6).toFixed(1);
console.log(`${pack}: ${mb(bytes)} MB in ${parts.length} parts; ${ids.length} models (${mb(summary.modelBytes)} MB), ${summary.shinies} shinies (${mb(summary.shinyBytes)} MB), ${summary.arenas} arenas`);
console.log(`by source: ${JSON.stringify(summary.bySrc)}`);
for (const m of missing) console.log(`  missing ${m.id}: ${m.why}`);
for (const h of heights) console.log(`  height ${h.id}: ${h.height} m, the Pokédex says ${h.pokedex} m`);
for (const p of problems) console.log(`::error::${p}`);
if (process.env.GITHUB_OUTPUT) fs.appendFileSync(process.env.GITHUB_OUTPUT, `pack=${pack}\n`);
if (process.env.GITHUB_STEP_SUMMARY)
  fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, [`### ${pack}`, '', '```', JSON.stringify({ ...summary, missing: missing.map((m) => `${m.id}: ${m.why}`) }, null, 1), '```', ''].join('\n'));
if (problems.length) process.exitCode = 1;
