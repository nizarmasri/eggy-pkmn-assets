// One whole file of the PokéModels MEGA folder (a .7z needs all of it), fetched in 8 MB ranged
// segments into <cache dir>. A run that stops (quota, network, a cancelled job) leaves <name>.part and
// <name>.state behind; the workflow caches them and the next run carries on from the last good byte.
// The archive's own CRCs (7z t) verify the result.
//
//   node pipeline/arenas/get.mjs <name> <cache dir>     prints the finished file's path
import fs from 'node:fs';
import path from 'node:path';
import { openFolder, readRange, QuotaError, stats } from '../lib/mega.mjs';

const [name, dir] = process.argv.slice(2);
const SEGMENT = 8 << 20;

fs.mkdirSync(dir, { recursive: true });
const files = await openFolder();
const node = files.get(name);
if (!node) {
  const near = [...files.keys()].filter((n) => /battle|map|stage|field/i.test(n));
  console.log(`::error::${name} is not in the MEGA folder; similar names: ${near.join(', ') || 'none'}`);
  process.exit(1);
}
const size = node.size;
const final = path.join(dir, name);
const part = `${final}.part`;
const stateFile = `${final}.state`;
const dirty = () => fs.writeFileSync(path.join(dir, '.dirty'), '');

if (fs.existsSync(final) && fs.statSync(final).size === size) {
  console.log(`${name}: ${(size / 1e6).toFixed(1)} MB, already here`);
} else {
  let done = 0;
  try {
    const st = JSON.parse(fs.readFileSync(stateFile, 'utf8'));
    if (st.size === size && fs.existsSync(part)) done = Math.min(st.done, fs.statSync(part).size);
  } catch {}
  const fd = fs.openSync(part, done > 0 ? 'r+' : 'w');
  if (done > 0) console.log(`${name}: resuming at ${(done / 1e6).toFixed(1)} of ${(size / 1e6).toFixed(1)} MB`);
  const t0 = Date.now();
  try {
    while (done < size) {
      const end = Math.min(size, done + SEGMENT) - 1;
      const buf = await readRange(node, done, end, name);
      fs.writeSync(fd, buf, 0, buf.length, done);
      done += buf.length;
      fs.writeFileSync(stateFile, JSON.stringify({ size, done }));
      dirty();
      console.log(`  ${(done / 1e6).toFixed(1)} / ${(size / 1e6).toFixed(1)} MB`);
    }
  } catch (e) {
    fs.closeSync(fd);
    if (e instanceof QuotaError) {
      console.log(`::error::${e.message}. ${(done / 1e6).toFixed(1)} MB are kept in the cache; run the workflow again later (a new runner has a new IP).`);
      process.exit(3);
    }
    throw e;
  }
  fs.closeSync(fd);
  fs.renameSync(part, final);
  fs.rmSync(stateFile, { force: true });
  dirty();
  console.log(`${name}: ${(size / 1e6).toFixed(1)} MB in ${((Date.now() - t0) / 1000).toFixed(0)} s, ${stats.requests} requests`);
}
if (process.env.GITHUB_OUTPUT) fs.appendFileSync(process.env.GITHUB_OUTPUT, `file=${final}\n`);
