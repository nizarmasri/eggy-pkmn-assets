// Fetches the zip entries a plan lists, by ranged MEGA reads: only those entries, never whole zips.
// Each entry is inflated, checked against its CRC32 and kept in the cache, so a rerun (or a run after
// MEGA's quota stopped this one) starts from what is already there.
//
//   node pipeline/fetch-entries.mjs <plan.json> <work dir> [--shard <k>] [--only <regex>]
// then each id's files are at <work dir>/in/<id>/{model,anim}/<file> and <work dir>/jobs.json lists them
// for the converter. --shard keeps the plan's jobs of one shard, --only the ids matching a pattern.
import fs from 'node:fs';
import path from 'node:path';
import { openFolder, readRange, stats, QuotaError } from './lib/mega.mjs';
import { LOCAL_SLACK, crc32, dataStart, extract } from './lib/zip.mjs';

const CACHE = process.env.MEGA_CACHE ?? 'cache';
const GAP = 512 * 1024; // read through holes up to this size rather than open a new request
const MAX_SEGMENT = 32 * 1024 * 1024;
const [planPath, workDir] = process.argv.slice(2);
const argOf = (name) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : null;
};
const shard = argOf('shard');
const only = argOf('only') ? new RegExp(argOf('only')) : null;
const jobs = JSON.parse(fs.readFileSync(planPath, 'utf8')).jobs
  .filter((j) => j.entries && (shard == null || String(j.shard) === shard) && (!only || only.test(j.id)));
console.log(`${jobs.length} ids${shard != null ? ` in shard ${shard}` : ''}`);

const cachePath = (e) => path.join(CACHE, 'entries', e.zip, e.name);
function cached(e) {
  const p = cachePath(e);
  try {
    if (fs.statSync(p).size !== e.usize) return false;
    return crc32(fs.readFileSync(p)) === e.crc;
  } catch {
    return false;
  }
}
function store(e, data) {
  const p = cachePath(e);
  fs.mkdirSync(path.dirname(p), { recursive: true });
  fs.writeFileSync(p + '.tmp', data);
  fs.renameSync(p + '.tmp', p);
  fs.writeFileSync(path.join(CACHE, '.dirty'), ''); // the workflow saves the cache when this exists
}

const byZip = new Map();
let hits = 0;
for (const j of jobs) {
  for (const e of j.entries ?? []) {
    if (cached(e)) { hits++; continue; }
    if (!byZip.has(e.zip)) byZip.set(e.zip, new Map());
    byZip.get(e.zip).set(e.name, e);
  }
}
const todo = [...byZip.values()].reduce((n, m) => n + m.size, 0);
const todoBytes = [...byZip.values()].flatMap((m) => [...m.values()]).reduce((n, e) => n + e.csize, 0);
console.log(`${hits} entries already cached; ${todo} to fetch (${(todoBytes / 1e6).toFixed(1)} MB compressed)`);

let fetched = 0;
const started = Date.now();
try {
  if (todo > 0) {
    const files = await openFolder();
    for (const [zip, entries] of byZip) {
      const node = files.get(zip);
      const list = [...entries.values()].sort((a, b) => a.lho - b.lho);
      // Coalesce neighbouring entries into one ranged read each.
      const segments = [];
      for (const e of list) {
        const end = Math.min(node.size - 1, e.lho + LOCAL_SLACK + e.csize);
        const last = segments.at(-1);
        if (last && e.lho - last.end <= GAP && end - last.start <= MAX_SEGMENT) {
          last.end = Math.max(last.end, end);
          last.entries.push(e);
        } else segments.push({ start: e.lho, end, entries: [e] });
      }
      for (const [i, seg] of segments.entries()) {
        const buf = await readRange(node, seg.start, seg.end, zip);
        for (const e of seg.entries) {
          const at = e.lho - seg.start;
          const from = dataStart(buf, at);
          let compressed = buf.subarray(from, from + e.csize);
          if (compressed.length < e.csize) {
            // The local header's extra field was longer than the slack allowed for: read the rest.
            const abs = seg.start + from;
            compressed = await readRange(node, abs, abs + e.csize - 1, zip);
          }
          store(e, extract(e, compressed));
          fetched++;
        }
        const mb = (seg.end - seg.start + 1) / 1e6;
        console.log(`  ${zip} ${i + 1}/${segments.length}: ${seg.entries.length} files in ${mb.toFixed(1)} MB (${(stats.bytes / 1e6).toFixed(1)} MB this run)`);
      }
    }
  }
} catch (e) {
  report(e);
  if (e instanceof QuotaError) {
    console.log(`::error::${e.message}. ${fetched} entries were fetched and are cached; run the workflow again later and it resumes from them.`);
    process.exit(3);
  }
  throw e;
}
report();

// Each species' inputs as plain folders for the converter.
for (const j of jobs) {
  if (!j.entries) continue;
  for (const e of j.entries) {
    const dest = path.join(workDir, 'in', j.id, e.kind, e.file);
    fs.mkdirSync(path.dirname(dest), { recursive: true });
    fs.rmSync(dest, { force: true });
    try { fs.linkSync(cachePath(e), dest); } catch { fs.copyFileSync(cachePath(e), dest); }
  }
}
const convJobs = jobs.filter((j) => j.entries).map((j) => ({
  id: j.id, dex: j.dex, src: j.src, folder: j.folder, shiny: j.shiny, variant: j.variant ?? null, female: j.female ?? null,
  modelDir: path.resolve(workDir, 'in', j.id, 'model'),
  animDir: path.resolve(workDir, 'in', j.id, 'anim'),
}));
fs.writeFileSync(path.join(workDir, 'jobs.json'), JSON.stringify(convJobs, null, 1));

function report(error) {
  const secs = ((Date.now() - started) / 1000).toFixed(0);
  const line = `MEGA: ${fetched}/${todo} entries fetched, ${(stats.bytes / 1e6).toFixed(1)} MB over ${stats.requests} ranged reads in ${secs} s; ${hits} came from the cache.${error ? ` Stopped: ${error.message}` : ''}`;
  console.log(line);
  if (process.env.GITHUB_STEP_SUMMARY) fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, line + '\n\n');
  if (process.env.GITHUB_OUTPUT) fs.appendFileSync(process.env.GITHUB_OUTPUT, `fetched=${fetched}\n`);
}
