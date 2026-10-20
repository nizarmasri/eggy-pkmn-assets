// What the PokéModels MEGA folder holds beside the battle maps, read without downloading it: every file's name
// and size, every zip's central directory (two ranged reads), and every 7z's headers (its 32-byte start header
// and the tail where 7-Zip keeps the rest, written into a sparse file of the archive's size that `7z l` reads).
// Then searched for battle cameras and for anything about the Let's Go battle maps (animations, effects).
//
//   node pipeline/arenas/research.mjs <out dir> <cache dir>
// Writes <out>/mega-files.tsv, <out>/list-<archive>.txt.gz (every entry), <out>/camera.tsv and
// <out>/battlemaps.tsv (the matches), <out>/archives.tsv (each archive's top folders and extensions).
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { execFileSync } from 'node:child_process';
import { openFolder, readRange } from '../lib/mega.mjs';
import { centralDirectory } from '../lib/sources.mjs';

const [out, cacheDir] = process.argv.slice(2);
fs.mkdirSync(out, { recursive: true });
const files = await openFolder();
const all = [...files.values()].sort((a, b) => a.name.localeCompare(b.name));
fs.writeFileSync(path.join(out, 'mega-files.tsv'), all.map((n) => `${n.name}\t${n.size}`).join('\n') + '\n');
console.log(`${all.length} files in the MEGA folder`);

// 7z: the start header says where the next header lies (offset from byte 32, size). An encoded header's packed
// stream sits just before it, so a generous tail holds both; the rest of the sparse file reads as zeros.
async function list7z(node) {
  const head = await readRange(node, 0, 31, `${node.name} start header`);
  if (head.toString('latin1', 0, 2) !== '7z') throw new Error('not a 7z archive');
  const nextOffset = Number(head.readBigUInt64LE(12));
  const at = 32 + nextOffset;
  for (const extra of [1 << 20, 8 << 20, 48 << 20]) {
    const from = Math.max(32, at - extra);
    const parts = [];
    for (let p = from; p < node.size; p += 8 << 20) parts.push(await readRange(node, p, Math.min(node.size, p + (8 << 20)) - 1, `${node.name} tail`));
    const tail = Buffer.concat(parts);
    const sparse = path.join(cacheDir, 'sparse.7z');
    fs.mkdirSync(cacheDir, { recursive: true });
    const fd = fs.openSync(sparse, 'w');
    fs.ftruncateSync(fd, node.size);
    fs.writeSync(fd, head, 0, 32, 0);
    fs.writeSync(fd, tail, 0, tail.length, from);
    fs.closeSync(fd);
    try {
      const text = execFileSync('7z', ['l', '-slt', '-ba', sparse], { maxBuffer: 1 << 30 }).toString();
      fs.rmSync(sparse);
      const names = [];
      let name = null, size = 0, folder = false;
      for (const line of text.split('\n')) {
        if (line.startsWith('Path = ')) { if (name && !folder) names.push({ name, size }); name = line.slice(7); size = 0; folder = false; }
        else if (line.startsWith('Size = ')) size = Number(line.slice(7)) || 0;
        else if (line.startsWith('Folder = +') || line.startsWith('Attributes = D')) folder = true;
      }
      if (name && !folder) names.push({ name, size });
      if (names.length > 0) return names;
    } catch (e) {
      fs.rmSync(sparse, { force: true });
      console.log(`  ${node.name}: the headers need more than the last ${(extra >> 20)} MB (${String(e.message).split('\n')[0]})`);
    }
  }
  throw new Error('could not read the headers');
}

const CAMERA = /camera|(^|[\/_.\-])cam([\/_.\-\d]|$)|btlcam|\.(gfbcam|trcam|trcm|bcam)$|cam_?(anim|data|table|param)/i;
const BATTLEMAP = /btl_[NF]_|btl_sky|battle_?(bg|field|map|stage)|background\/btl|\.gfbanm$|\.gfbanmcfg$|\.gfbpmcatalog$|\.gfbmtt$/i;
const camera = [];
const battlemaps = [];
const summary = [];
for (const node of all) {
  const ext = node.name.split('.').pop().toLowerCase();
  if (!['zip', '7z'].includes(ext)) continue;
  let entries;
  try {
    entries = ext === 'zip'
      ? (await centralDirectory(files, node.name, cacheDir)).map((e) => ({ name: e.name, size: e.size ?? e.usize ?? 0 }))
      : await list7z(node);
  } catch (e) {
    console.log(`${node.name}: unreadable (${e.message})`);
    summary.push(`${node.name}\t${node.size}\tunreadable: ${e.message}`);
    continue;
  }
  fs.writeFileSync(path.join(out, `list-${node.name}.txt.gz`), zlib.gzipSync(entries.map((e) => `${e.name}\t${e.size}`).join('\n') + '\n'));
  const tops = new Map();
  const exts = new Map();
  for (const e of entries) {
    const top = e.name.split('/').slice(0, 2).join('/');
    tops.set(top, (tops.get(top) ?? 0) + 1);
    const x = e.name.includes('.') ? e.name.split('.').pop().toLowerCase() : '(none)';
    exts.set(x, (exts.get(x) ?? 0) + 1);
    // Camerupt (pm0323) is not a camera.
    if (CAMERA.test(e.name) && !/camerupt|pm0323/i.test(e.name)) camera.push(`${node.name}\t${e.name}\t${e.size}`);
    if (BATTLEMAP.test(e.name) && !/^.*pokemon\//i.test(e.name)) battlemaps.push(`${node.name}\t${e.name}\t${e.size}`);
  }
  const top = [...tops].sort((a, b) => b[1] - a[1]).slice(0, 12).map(([k, v]) => `${k} ${v}`).join('; ');
  const ex = [...exts].sort((a, b) => b[1] - a[1]).slice(0, 12).map(([k, v]) => `${k} ${v}`).join('; ');
  summary.push(`${node.name}\t${node.size}\t${entries.length} files\t${ex}\t${top}`);
  console.log(`${node.name}: ${entries.length} files (${ex})`);
}
fs.writeFileSync(path.join(out, 'archives.tsv'), summary.join('\n') + '\n');
fs.writeFileSync(path.join(out, 'camera.tsv'), camera.join('\n') + '\n');
fs.writeFileSync(path.join(out, 'battlemaps.tsv'), battlemaps.join('\n') + '\n');
console.log(`${camera.length} camera matches, ${battlemaps.length} battle map matches`);
