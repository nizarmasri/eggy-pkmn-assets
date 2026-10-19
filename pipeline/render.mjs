// Review sheets of the published GLBs, drawn by three.js in headless Chrome (the renderer the game
// uses), with meshopt and WebP decoded the way a phone will. One JPEG per model plus measurements.
//
//   node pipeline/render.mjs <out dir> <review dir> <renders dir> [<raw dir with reports>]
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const here = path.dirname(fileURLToPath(import.meta.url));
const [outDir, reviewDir, rendersDir, rawDir] = process.argv.slice(2);
const roots = { '/render/': path.join(here, 'render'), '/three/': path.join(here, 'node_modules', 'three'), '/out/': outDir, '/review/': reviewDir };
const types = { '.html': 'text/html', '.js': 'text/javascript', '.glb': 'model/gltf-binary', '.json': 'application/json', '.wasm': 'application/wasm' };

const server = http.createServer((req, res) => {
  const url = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  if (url === '/favicon.ico') { res.writeHead(204).end(); return; }
  const prefix = Object.keys(roots).find((p) => url.startsWith(p));
  const file = prefix && path.join(roots[prefix], url.slice(prefix.length));
  if (!file || !file.startsWith(roots[prefix]) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
    res.writeHead(404).end();
    return;
  }
  res.writeHead(200, { 'content-type': types[path.extname(file)] ?? 'application/octet-stream' });
  fs.createReadStream(file).pipe(res);
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;

const browser = await chromium.launch({
  executablePath: process.env.CHROME ?? '/usr/bin/google-chrome',
  args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'],
});
fs.mkdirSync(rendersDir, { recursive: true });
// RENDER_CLOSEUPS=<regex>: every model gets the compact strip the contact sheets are made of
// (render/index.html ?compact=1, saved as strip-<id>.jpg), and only the ids it matches the full sheet
// with the variant rows. Without it, every model gets the full sheet.
const closeups = process.env.RENDER_CLOSEUPS ? new RegExp(process.env.RENDER_CLOSEUPS) : null;
const jobs = [
  ...fs.readdirSync(outDir).filter((f) => f.endsWith('.glb') && !f.endsWith('.shiny.glb')).map((f) => ['out', f]),
  ...(fs.existsSync(reviewDir) ? fs.readdirSync(reviewDir).filter((f) => f.endsWith('.glb')).map((f) => ['review', f]) : []),
].flatMap(([dir, file]) => {
  const name = file.replace(/\.glb$/, '');
  if (!closeups) return [[dir, file, false]];
  return [[dir, file, true], ...(closeups.test(name) ? [[dir, file, false]] : [])];
});
const checks = {};
let bad = 0;
for (const [dir, file, compact] of jobs) {
  const name = file.replace(/\.glb$/, '');
  const reportPath = rawDir && path.join(rawDir, `${name}.json`);
  const r = reportPath && fs.existsSync(reportPath) ? JSON.parse(fs.readFileSync(reportPath, 'utf8')) : {};
  const meta = { id: name, src: r.src, folder: r.folder, hit: r.hit };
  // RENDER_VARIANTS=1: four more face rows (see render/index.html) to tell what a look comes from.
  const variants = process.env.RENDER_VARIANTS === '1' || (closeups && !compact);
  const shiny = dir === 'out' && fs.existsSync(path.join(outDir, `${name}.shiny.glb`)) ? `/out/${name}.shiny.glb` : null;
  const rowCount = 3 + (shiny ? 1 : 0) + (variants ? 4 : 0);
  const page = await browser.newPage({ viewport: compact ? { width: 960, height: 240 } : { width: 2400, height: 300 * rowCount } });
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
  await page.goto(`http://127.0.0.1:${port}/render/index.html?model=/${dir}/${file}&meta=${encodeURIComponent(JSON.stringify(meta))}${variants ? '&variants=1' : ''}${compact ? '&compact=1' : ''}${shiny ? `&shiny=${encodeURIComponent(shiny)}` : ''}`);
  try {
    await page.waitForFunction(() => window.result, null, { timeout: 180000 });
    const result = await page.evaluate(() => window.result);
    await page.locator('#sheet').screenshot({ path: path.join(rendersDir, `${compact ? 'strip-' : ''}${name}.jpg`), type: 'jpeg', quality: 82 });
    // The idle loop must stand on the floor (y = 0), 1 tall, centred on x and z, as three.js poses it.
    const env = result.idleEnvelope;
    const problems = [];
    if (Math.abs(env.min[1]) > 0.02) problems.push(`idle min y ${env.min[1]}`);
    if (Math.abs(env.max[1] - env.min[1] - 1) > 0.03) problems.push(`idle height ${(env.max[1] - env.min[1]).toFixed(3)}`);
    for (const axis of [0, 2]) if (Math.abs(env.min[axis] + env.max[axis]) / 2 > 0.03) problems.push(`idle off centre on ${'xyz'[axis]}`);
    for (const [clip, c] of Object.entries(result.clips)) if (!c.finite) problems.push(`${clip} has non-finite positions`);
    if (compact || !closeups) checks[name] = { ...result, errors, problems };
    if (problems.length || errors.length) bad++;
    console.log(`${name}: ${problems.length || errors.length ? `PROBLEMS ${[...problems, ...errors].join('; ')}` : 'ok'} (${Object.keys(result.clips).join(' ')})`);
  } catch (e) {
    bad++;
    checks[name] = { errors: [...errors, e.message] };
    console.log(`${name}: FAILED ${[...errors, e.message].join('; ')}`);
  }
  await page.close();
}
fs.writeFileSync(path.join(rendersDir, 'checks.json'), JSON.stringify(checks, null, 1));
await browser.close();
server.close();
if (bad) process.exitCode = 1;
