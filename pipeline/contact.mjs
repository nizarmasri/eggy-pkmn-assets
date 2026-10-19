// Contact sheets: every model's review strip (strip-<id>.jpg from render.mjs with RENDER_CLOSEUPS), in dex
// order, two strips across and twelve down per sheet, so the whole pack can be looked over at a glance.
//
//   node pipeline/contact.mjs <models.json> <dir with strip-*.jpg> <out dir>
import fs from 'node:fs';
import path from 'node:path';
import sharp from 'sharp';

const [modelsPath, stripsDir, outDir] = process.argv.slice(2);
const models = JSON.parse(fs.readFileSync(modelsPath, 'utf8')).models;
const W = 960, H = 240, COLS = 2, ROWS = 12;
const ids = Object.keys(models).sort((a, b) => models[a].dex - models[b].dex || a.localeCompare(b));
const have = ids.filter((id) => fs.existsSync(path.join(stripsDir, `strip-${id}.jpg`)));
const without = ids.filter((id) => !have.includes(id));
fs.mkdirSync(outDir, { recursive: true });
const per = COLS * ROWS;
const pages = Math.ceil(have.length / per);
for (let p = 0; p < pages; p++) {
  const chunk = have.slice(p * per, (p + 1) * per);
  const composite = chunk.map((id, i) => ({ input: path.join(stripsDir, `strip-${id}.jpg`), left: (i % COLS) * W, top: Math.floor(i / COLS) * H }));
  const first = models[chunk[0]].dex, last = models[chunk.at(-1)].dex;
  const name = `contact-${String(p + 1).padStart(2, '0')}-dex${first}-${last}.jpg`;
  await sharp({ create: { width: W * COLS, height: H * Math.ceil(chunk.length / COLS), channels: 3, background: '#2b2f36' } })
    .composite(composite)
    .jpeg({ quality: 80 })
    .toFile(path.join(outDir, name));
  console.log(`${name}: ${chunk.length} models`);
}
if (without.length) console.log(`no strip for: ${without.join(', ')}`);
