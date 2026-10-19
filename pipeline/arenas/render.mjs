// Review sheets of the published arenas, drawn by three.js in headless Chrome (the game's renderer):
// the game's battle shot large, an overview and a top view, materials drawn by their roles, with two Pokémon on the
// singles spots at their real heights for scale. One JPEG per arena plus load checks.
//
//   node pipeline/arenas/render.mjs <out dir> <renders dir> <pokemon dir with models.json>
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const here = path.dirname(fileURLToPath(import.meta.url));
const [outDir, rendersDir, pkmnDir] = process.argv.slice(2);
const roots = {
  '/render/': path.join(here, '..', 'render'),
  '/three/': path.join(here, '..', 'node_modules', 'three'),
  '/out/': path.resolve(outDir),
  '/pkmn/': path.resolve(pkmnDir),
};
const types = { '.html': 'text/html', '.js': 'text/javascript', '.glb': 'model/gltf-binary', '.json': 'application/json', '.wasm': 'application/wasm' };
const server = http.createServer((req, res) => {
  const url = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  if (url === '/favicon.ico') { res.writeHead(204).end(); return; }
  const prefix = Object.keys(roots).find((p) => url.startsWith(p));
  const file = prefix && path.join(roots[prefix], url.slice(prefix.length));
  if (!file || !file.startsWith(roots[prefix]) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404).end(); return; }
  res.writeHead(200, { 'content-type': types[path.extname(file)] ?? 'application/octet-stream' });
  fs.createReadStream(file).pipe(res);
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;

const arenas = JSON.parse(fs.readFileSync(path.join(outDir, 'arenas.json'), 'utf8')).arenas;
const pkmn = JSON.parse(fs.readFileSync(path.join(pkmnDir, 'models.json'), 'utf8')).models;
const who = (id) => (pkmn[id] ? `/pkmn/${pkmn[id].file}@${pkmn[id].height}` : '');

const browser = await chromium.launch({
  executablePath: process.env.CHROME ?? '/usr/bin/google-chrome',
  args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'],
});
fs.mkdirSync(rendersDir, { recursive: true });
const checks = {};
let bad = 0;
for (const [id, a] of Object.entries(arenas)) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1350 } });
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
  const meta = { id, name: a.name, spots: a.spots, radius: a.radius };
  await page.goto(`http://127.0.0.1:${port}/render/arena.html?model=/out/${a.file}&near=${encodeURIComponent(who('pikachu'))}&far=${encodeURIComponent(who('snorlax'))}&meta=${encodeURIComponent(JSON.stringify(meta))}`);
  try {
    await page.waitForFunction(() => window.result, null, { timeout: 240000 });
    const result = await page.evaluate(() => window.result);
    await page.locator('#sheet').screenshot({ path: path.join(rendersDir, `${id}.jpg`), type: 'jpeg', quality: 80 });
    checks[id] = { ...result, errors };
    if (errors.length) bad++;
    console.log(`${id}: ${errors.length ? `PROBLEMS ${errors.join('; ')}` : 'ok'} (${result.tris} tris)`);
  } catch (e) {
    bad++;
    checks[id] = { errors: [...errors, e.message] };
    console.log(`${id}: FAILED ${[...errors, e.message].join('; ')}`);
  }
  await page.close();
}
fs.writeFileSync(path.join(rendersDir, 'checks.json'), JSON.stringify(checks, null, 1));
await browser.close();
server.close();
if (bad) process.exitCode = 1;
