// Contact sheets of decoded textures, one JPEG per folder, each texture labelled with its name and size,
// so an inventory can be looked at (which are lightmaps, which tile, which are skies).
//
//   node pipeline/arenas/texsheet.mjs <png dir> <out dir>
import fs from 'node:fs';
import path from 'node:path';
import sharp from 'sharp';

const [pngDir, outDir] = process.argv.slice(2);
const CELL = 200, LABEL = 30, COLS = 8, PAGE = 48;
fs.mkdirSync(outDir, { recursive: true });

const groups = new Map();
(function walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p);
    else if (e.name.endsWith('.png')) {
      const key = path.relative(pngDir, dir) || 'root';
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(p);
    }
  }
})(pngDir);

const esc = (s) => s.replace(/[<>&"]/g, (c) => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;', '"': '&quot;' })[c]);
for (const [key, list] of groups) {
  list.sort();
  for (let page = 0; page * PAGE < list.length; page++) {
    const items = list.slice(page * PAGE, (page + 1) * PAGE);
    const rows = Math.ceil(items.length / COLS);
    const parts = [];
    for (let i = 0; i < items.length; i++) {
      const meta = await sharp(items[i]).metadata();
      // On a checkerboard, so alpha shows.
      const thumb = await sharp(items[i]).resize(CELL, CELL, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } }).png().toBuffer();
      const x = (i % COLS) * CELL, y = Math.floor(i / COLS) * (CELL + LABEL);
      parts.push({ input: thumb, left: x, top: y });
      const label = `${path.basename(items[i], '.png')} ${meta.width}x${meta.height}`;
      const svg = `<svg width="${CELL}" height="${LABEL}" xmlns="http://www.w3.org/2000/svg"><rect width="100%" height="100%" fill="#111"/><text x="4" y="12" font-family="DejaVu Sans, sans-serif" font-size="11" fill="#eee">${esc(label.slice(0, 34))}</text><text x="4" y="25" font-family="DejaVu Sans, sans-serif" font-size="11" fill="#eee">${esc(label.slice(34, 68))}</text></svg>`;
      parts.push({ input: Buffer.from(svg), left: x, top: y + CELL });
    }
    const checker = Buffer.from(`<svg width="${COLS * CELL}" height="${rows * (CELL + LABEL)}" xmlns="http://www.w3.org/2000/svg"><defs><pattern id="c" width="16" height="16" patternUnits="userSpaceOnUse"><rect width="16" height="16" fill="#777"/><rect width="8" height="8" fill="#999"/><rect x="8" y="8" width="8" height="8" fill="#999"/></pattern></defs><rect width="100%" height="100%" fill="url(#c)"/></svg>`);
    const name = `tex-${key.replace(/[\/\\]/g, '_')}${page ? `-${page + 1}` : ''}.jpg`;
    await sharp(checker).composite(parts).jpeg({ quality: 80 }).toFile(path.join(outDir, name));
    console.log(`${name}: ${items.length} textures`);
  }
}
