// The arena pack, on its own so the arenas can change without the models downloading again: one tarball whose
// files unpack at its root (arenas.json, <id>.glb), named by its own hash (a1-<first 8 hex of its sha256>), cut in
// 10 MiB parts, with the lock eggy reads (arenas.lock.json, the shape of models.lock.json). Also lays out the
// releases and the review set, with the list of what moves in each arena and which cards fade at their edges.
//
//   node pipeline/arenas/pack.mjs --arenas <optimised dir> --renders <renders dir> --work <dir> --rel <dir>
//     <rel>/main    the parts, arenas.lock.json, arenas.json and every <id>.glb on its own, SHA256SUMS
//     <rel>/review  arena-<id>.jpg, arena-<id>-cards.jpg, motion.json, motion.md, report.json, checks.json, summary.json
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';

const arg = (name) => { const i = process.argv.indexOf(`--${name}`); return i > 0 ? process.argv[i + 1] : null; };
const arenasDir = arg('arenas'), rendersDir = arg('renders'), work = arg('work'), rel = arg('rel');
const repo = process.env.GITHUB_REPOSITORY ?? 'nizarmasri/eggy-pkmn-assets';
const PART = 10 << 20;
const MAX_GLB = 3 << 20;
const sha = (buf) => crypto.createHash('sha256').update(buf).digest('hex');
const shaFile = (f) => sha(fs.readFileSync(f));
for (const d of [work, path.join(rel, 'main'), path.join(rel, 'review')]) fs.mkdirSync(d, { recursive: true });

const index = JSON.parse(fs.readFileSync(path.join(arenasDir, 'arenas.json'), 'utf8'));
const report = JSON.parse(fs.readFileSync(path.join(arenasDir, 'report.json'), 'utf8'));
const ids = Object.keys(index.arenas).sort();
const problems = [];

// ---- The tarball: same files, same bytes, every run ----
const root = path.join(work, 'root');
fs.rmSync(root, { recursive: true, force: true });
fs.mkdirSync(root, { recursive: true });
const files = ['arenas.json'];
fs.copyFileSync(path.join(arenasDir, 'arenas.json'), path.join(root, 'arenas.json'));
for (const id of ids) {
  const f = index.arenas[id].file;
  const p = path.join(arenasDir, f);
  if (fs.statSync(p).size > MAX_GLB) problems.push(`${f}: ${(fs.statSync(p).size / 1e6).toFixed(2)} MB, over 3 MB`);
  if (shaFile(p) !== index.arenas[id].sha256) problems.push(`${f}: its sha256 is not the one arenas.json gives`);
  fs.copyFileSync(p, path.join(root, f));
  files.push(f);
}
files.sort();
fs.writeFileSync(path.join(work, 'files.txt'), files.join('\n') + '\n');
const tmpTar = path.join(work, 'pack.tar');
execFileSync('tar', ['--create', '--file', tmpTar, '--directory', root, '--format=gnu', '--owner=0', '--group=0', '--numeric-owner',
  '--mtime=@0', '--mode=a+rX,u+w,go-w', '--no-recursion', '--files-from', path.join(work, 'files.txt')]);
const tarSha = shaFile(tmpTar);
const pack = `a1-${tarSha.slice(0, 8)}`;
const file = `pkmn-${pack}.tar`;
const tarPath = path.join(work, file);
fs.renameSync(tmpTar, tarPath);
const bytes = fs.statSync(tarPath).size;

// ---- Parts, the lock, every file on its own ----
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
fs.writeFileSync(path.join(rel, 'main', 'arenas.lock.json'), JSON.stringify(lock, null, 2) + '\n');
for (const f of files) fs.copyFileSync(path.join(root, f), path.join(rel, 'main', f));
const sums = (dir) => {
  const names = fs.readdirSync(dir).filter((f) => f !== 'SHA256SUMS').sort();
  fs.writeFileSync(path.join(dir, 'SHA256SUMS'), names.map((f) => `${shaFile(path.join(dir, f))}  ${f}`).join('\n') + '\n');
  return names.length + 1;
};
const mainAssets = sums(path.join(rel, 'main'));
if (mainAssets > 1000) problems.push(`${mainAssets} assets in the main release: over GitHub's 1000`);

// ---- What moves, and which cards fade at their edges ----
const motion = {};
for (const id of ids) {
  const r = report[id];
  const mats = r.materials ?? [];
  const vc = r.vertexColors ?? {};
  const range = (name) => vc[name]?.alphaRange ?? 'none';
  motion[id] = {
    animations: [],
    scrolls: mats.filter((m) => m.scroll || m.mask?.scroll).map((m) => ({
      material: m.name, role: m.role ?? null, scroll: m.scroll ?? null, mask: m.mask ? { tex: m.mask.tex, scroll: m.mask.scroll ?? null } : null, how: m.motion,
    })),
    water: mats.filter((m) => m.water).map((m) => ({ material: m.name, ...m.water })),
    cards: mats.filter((m) => m.role === 'fog' || m.role === 'glow').map((m) => {
      const a = range(m.name);
      const [lo, hi] = a.split('..').map(Number);
      return { material: m.name, role: m.role, vertexAlpha: a, edges: lo < hi ? 'from the file (vertex alpha)' : 'none in the file (uniform vertex alpha)' };
    }),
    foliage: mats.filter((m) => m.role === 'foliage').map((m) => m.name),
    sky: mats.filter((m) => m.role === 'sky').map((m) => m.name),
  };
}
fs.writeFileSync(path.join(rel, 'review', 'motion.json'), JSON.stringify(motion, null, 1) + '\n');
const md = [`# ${pack}: what moves`, '', 'No arena has a glTF animation: the rip holds no animation clips (see the README). Scrolls are glTF UV units per second (V down).', ''];
for (const id of ids) {
  const m = motion[id];
  md.push(`## ${id}`, '');
  for (const s of m.scrolls) md.push(`- scroll ${s.material} (${s.role ?? 'no role'}): ${s.scroll ? `[${s.scroll.join(', ')}]` : 'still'}${s.mask ? `, mask ${s.mask.tex} ${s.mask.scroll ? `[${s.mask.scroll.join(', ')}]` : 'still'}` : ''} (${s.how})`);
  for (const w of m.water) md.push(`- water ${w.material}: flow ${w.flow ? `[${w.flow.join(', ')}] m/s` : 'none'}; the file's layers ${JSON.stringify(w.layers)}`);
  for (const c of m.cards) md.push(`- ${c.role} card ${c.material}: vertex alpha ${c.vertexAlpha}, edges ${c.edges}`);
  if (m.foliage.length) md.push(`- foliage: ${m.foliage.join(', ')}`);
  if (m.sky.length) md.push(`- sky: ${m.sky.join(', ')}`);
  if (!m.scrolls.length && !m.water.length && !m.cards.length && !m.foliage.length && !m.sky.length) md.push('- nothing moves');
  md.push('');
}
fs.writeFileSync(path.join(rel, 'review', 'motion.md'), md.join('\n'));

// ---- The review set ----
if (rendersDir && fs.existsSync(rendersDir)) {
  for (const f of fs.readdirSync(rendersDir)) {
    if (f.endsWith('.jpg')) fs.copyFileSync(path.join(rendersDir, f), path.join(rel, 'review', `arena-${f}`));
    if (f === 'checks.json') fs.copyFileSync(path.join(rendersDir, f), path.join(rel, 'review', 'checks.json'));
  }
}
fs.copyFileSync(path.join(arenasDir, 'report.json'), path.join(rel, 'review', 'report.json'));
const glbBytes = ids.reduce((n, id) => n + index.arenas[id].bytes, 0);
const summary = {
  pack, bytes, parts: parts.length, arenas: ids.length, glbBytes,
  largest: ids.map((id) => [id, index.arenas[id].bytes]).sort((a, b) => b[1] - a[1])[0],
  scrolling: ids.reduce((n, id) => n + motion[id].scrolls.length, 0),
  softCards: ids.reduce((n, id) => n + motion[id].cards.filter((c) => c.edges.startsWith('from')).length, 0),
  hardCards: ids.flatMap((id) => motion[id].cards.filter((c) => !c.edges.startsWith('from')).map((c) => `${id}/${c.material}`)),
  foliage: ids.reduce((n, id) => n + motion[id].foliage.length, 0),
  sky: ids.reduce((n, id) => n + motion[id].sky.length, 0),
  problems,
};
fs.writeFileSync(path.join(rel, 'review', 'summary.json'), JSON.stringify(summary, null, 1) + '\n');
sums(path.join(rel, 'review'));
console.log(`${pack}: ${(bytes / 1e6).toFixed(1)} MB in ${parts.length} parts; ${ids.length} arenas, ${summary.scrolling} scrolling materials, ${summary.softCards} soft cards, ${summary.foliage} foliage, ${summary.sky} sky`);
for (const p of problems) console.log(`::error::${p}`);
if (process.env.GITHUB_OUTPUT) fs.appendFileSync(process.env.GITHUB_OUTPUT, `pack=${pack}\n`);
if (process.env.GITHUB_STEP_SUMMARY) fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, `### ${pack}\n\n\`\`\`\n${JSON.stringify(summary, null, 1)}\n\`\`\`\n`);
if (problems.length) process.exitCode = 1;
