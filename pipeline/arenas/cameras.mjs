// A sample of the only camera animations in the PokéModels folder (Legends Z-A's cutscene cameras, .trcma in
// ZA-Demo.zip), each read with one ranged request, CRC-checked, for the inventory to decode their layout.
//
//   node pipeline/arenas/cameras.mjs <out dir> <cache dir> [<regex of entries>]
import fs from 'node:fs';
import path from 'node:path';
import { openFolder, readRange } from '../lib/mega.mjs';
import { centralDirectory } from '../lib/sources.mjs';
import { LOCAL_SLACK, dataStart, extract } from '../lib/zip.mjs';

const [out, cacheDir, pattern] = process.argv.slice(2);
const want = new RegExp(pattern ?? '(hokaku|m_shinka_tr|m_bousou_down|ee700|sd9171_rankup_up)/[^/]*\\.trcma$');
const files = await openFolder();
const zip = 'ZA-Demo.zip';
const entries = (await centralDirectory(files, zip, cacheDir)).filter((e) => want.test(e.name));
const node = files.get(zip);
fs.mkdirSync(out, { recursive: true });
const picked = [];
const seen = new Set();
for (const e of entries) {
  const dir = path.dirname(e.name);
  if (seen.has(dir) && picked.length > 4) continue; // one or two per scene is enough
  seen.add(dir);
  const buf = await readRange(node, e.lho, Math.min(node.size - 1, e.lho + LOCAL_SLACK + e.csize), e.name);
  const from = dataStart(buf, 0);
  const data = extract(e, buf.subarray(from, from + e.csize));
  const name = e.name.replace(/\//g, '~');
  fs.writeFileSync(path.join(out, name), data);
  picked.push(`${e.name}\t${data.length}`);
  if (picked.length >= 12) break;
}
fs.writeFileSync(path.join(out, '..', 'za-cameras-sample.tsv'), picked.join('\n') + '\n');
console.log(`${picked.length} camera animations from ${zip}`);
