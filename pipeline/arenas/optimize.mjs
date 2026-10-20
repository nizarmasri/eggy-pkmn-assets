// Raw arena GLBs -> the published arenas: one small GLB per arena plus arenas.json.
//
//   node pipeline/arenas/optimize.mjs <raw dir> <out dir> <list.json>
//
// Per arena: the converter's material plan applied (alpha masks, wraps, vertex colours as COLOR_0),
// meshes joined per material, then prune, dedup, WebP textures (the near stage at most 1024 px, the
// far ring and sky at most 512 px), meshopt reorder + quantisation, EXT_meshopt_compression.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { NodeIO } from '@gltf-transform/core';
import { ALL_EXTENSIONS, EXTMeshoptCompression } from '@gltf-transform/extensions';
import { dedup, flatten, join, prune, quantize, reorder, textureCompress } from '@gltf-transform/functions';
import { MeshoptDecoder, MeshoptEncoder } from 'meshoptimizer';
import sharp from 'sharp';

const [rawDir, outDir, listPath] = process.argv.slice(2);
const list = JSON.parse(fs.readFileSync(listPath, 'utf8'));

await MeshoptEncoder.ready;
await MeshoptDecoder.ready;
const io = new NodeIO().registerExtensions(ALL_EXTENSIONS).registerDependencies({
  'meshopt.encoder': MeshoptEncoder,
  'meshopt.decoder': MeshoptDecoder,
});

// A mask is an image no material slot uses, so the writer gives it no glTF texture: here it gets one (a sampler
// with the mask's wraps), and each material's extras.mask names that texture's index in place of the file.
function injectMasks(glb) {
  const jsonLen = glb.readUInt32LE(12);
  const json = JSON.parse(glb.toString('utf8', 20, 20 + jsonLen));
  const rest = glb.subarray(20 + jsonLen);
  const masked = (json.materials ?? []).filter((m) => m.extras?.mask?.file);
  if (masked.length === 0) return glb;
  json.textures ??= [];
  json.samplers ??= [];
  // The masks are WebP, as every other texture of the arena is (EXT_texture_webp).
  json.extensionsUsed ??= [];
  if (!json.extensionsUsed.includes('EXT_texture_webp')) json.extensionsUsed.push('EXT_texture_webp');
  const made = new Map();
  for (const m of masked) {
    const { file, wrap, ...mask } = m.extras.mask;
    const key = `${file} ${wrap?.join(',')}`;
    if (!made.has(key)) {
      const image = (json.images ?? []).findIndex((im) => im.name === `mask:${file}`);
      if (image < 0) throw new Error(`mask image ${file} is not in the GLB`);
      json.samplers.push({ magFilter: 9729, minFilter: 9987, wrapS: wrap?.[0] ?? 10497, wrapT: wrap?.[1] ?? 10497 });
      json.textures.push({ sampler: json.samplers.length - 1, name: `mask:${file}`, extensions: { EXT_texture_webp: { source: image } } });
      made.set(key, json.textures.length - 1);
    }
    m.extras.mask = { texture: made.get(key), ...mask };
  }
  let text = JSON.stringify(json);
  while (Buffer.byteLength(text) % 4) text += ' ';
  const head = Buffer.alloc(20);
  const body = Buffer.from(text);
  head.writeUInt32LE(0x46546c67, 0);
  head.writeUInt32LE(2, 4);
  head.writeUInt32LE(20 + body.length + rest.length, 8);
  head.writeUInt32LE(body.length, 12);
  head.writeUInt32LE(0x4e4f534a, 16);
  return Buffer.concat([head, body, rest]);
}

fs.mkdirSync(outDir, { recursive: true });
const arenas = {};
const report = {};
const failures = [];

for (const spec of list.arenas) {
  const jsonPath = path.join(rawDir, `${spec.id}.json`);
  if (!fs.existsSync(jsonPath)) { failures.push(`${spec.id}: no converter report`); continue; }
  const r = JSON.parse(fs.readFileSync(jsonPath, 'utf8'));
  if (r.error) { failures.push(`${spec.id}: ${r.error.split('\n')[0]}`); report[spec.id] = r; continue; }
  const doc = await io.read(path.join(rawDir, `${spec.id}.glb`));
  const root = doc.getRoot();
  const plan = Object.fromEntries(r.materials.map((m) => [m.name, m]));

  // Material plan.
  const farOnly = new Set();
  const nearTex = new Set();
  for (const mat of root.listMaterials()) {
    const p = plan[mat.getName().replace(/^hull /, '')];
    const tex = mat.getBaseColorTexture();
    // The converter's writer gives meshes that look like inverted-hull outlines a single-sided copy of
    // their material ("hull <name>"); arena meshes are not outlines (the sea plane trips it), so the
    // copy takes back the material's own sidedness and name.
    if (mat.getName().startsWith('hull ')) mat.setName(mat.getName().slice(5)).setDoubleSided(!!p?.doubleSided);
    if (p) {
      if (p.alpha === 'MASK') mat.setAlphaMode('MASK').setAlphaCutoff(p.cutoff ?? 0.5);
      if (p.alpha === 'BLEND') mat.setAlphaMode('BLEND');
      const info = mat.getBaseColorTextureInfo();
      if (info && p.wrap) info.setWrapS(p.wrap[0]).setWrapT(p.wrap[1]);
    }
    if (tex) (mat.getName().replace(/^hull /, '').startsWith('N_') ? nearTex : farOnly).add(tex);
    // How the game draws it (material.userData): its role (water, glow, fog, decal, foliage, sky), how its colour
    // texture scrolls, the mask multiplied into its alpha (its texture index is filled in once the GLB is
    // written: see injectMasks) and the water's drift. Nothing for the rest.
    const x = {};
    if (p?.role) x.role = p.role;
    if (p?.scroll) x.scroll = p.scroll;
    if (p?.mask) x.mask = { file: p.mask.file, texCoord: p.mask.texCoord, ...(p.mask.scroll ? { scroll: p.mask.scroll } : {}), wrap: p.mask.wrap };
    if (p?.water?.flow) x.water = { flow: p.water.flow };
    mat.setExtras(x);
  }
  for (const t of nearTex) farOnly.delete(t);
  // Vertex colours: the converter keeps them under an application name; they are plain COLOR_0 here.
  for (const mesh of root.listMeshes())
    for (const prim of mesh.listPrimitives()) {
      const c = prim.getAttribute('_TRINITY_COLOR');
      if (c) { prim.setAttribute('COLOR_0', c); prim.setAttribute('_TRINITY_COLOR', null); }
    }
  for (const list of [root.listNodes(), root.listMeshes(), root.listTextures()]) for (const item of list) item.setExtras({});
  root.listExtensionsUsed().find((e) => e.extensionName === 'KHR_node_visibility')?.dispose();

  // Far-ring and sky textures are seen small and far away: half the near stage's size limit.
  const farNames = [...farOnly].map((t) => t.getName()).filter(Boolean);
  const esc = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await doc.transform(
    flatten(),
    join({ keepNamed: false }),
    prune({ keepLeaves: false }),
    dedup(),
    ...(farNames.length ? [textureCompress({ encoder: sharp, targetFormat: 'webp', resize: [512, 512], quality: 85, effort: 90, pattern: new RegExp(`^(${farNames.map(esc).join('|')})$`) })] : []),
    textureCompress({ encoder: sharp, targetFormat: 'webp', resize: [1024, 1024], quality: 85, effort: 90, formats: /png/ }),
    reorder({ encoder: MeshoptEncoder }),
    // One quantisation volume for the whole arena: per-mesh volumes round shared edges of two materials
    // differently, and the floor shows hairline cracks along them. 16 bits over ~160 m is 2.4 mm.
    quantize({ quantizationVolume: 'scene', quantizePosition: 16 }),
  );
  doc.createExtension(EXTMeshoptCompression).setRequired(true).setEncoderOptions({ method: EXTMeshoptCompression.EncoderMethod.QUANTIZE });
  // The masks' images, added after prune (no material slot holds them, so prune would drop them).
  const maskFiles = [...new Set(root.listMaterials().map((m) => m.getExtras()?.mask?.file).filter(Boolean))];
  for (const file of maskFiles) {
    const png = fs.readFileSync(path.join(rawDir, `${spec.id}--${file}`));
    const webp = await sharp(png).resize({ width: 512, height: 512, fit: 'inside', withoutEnlargement: true }).webp({ quality: 85, effort: 6 }).toBuffer();
    doc.createTexture(`mask:${file}`).setImage(webp).setMimeType('image/webp');
  }
  const glb = injectMasks(Buffer.from(await io.writeBinary(doc)));
  fs.writeFileSync(path.join(outDir, `${spec.id}.glb`), glb);

  const sha256 = crypto.createHash('sha256').update(glb).digest('hex');
  arenas[spec.id] = {
    file: `${spec.id}.glb`,
    bytes: glb.length,
    sha256,
    name: spec.name,
    src: 'lgpe',
    kind: spec.kind,
    lit: r.lit,
    spots: r.spots,
    radius: r.radius,
  };
  const textures = root.listTextures().map((t) => `${t.getName()} ${t.getMimeType()} ${t.getSize()?.join('x')} ${(t.getImage()?.byteLength / 1024).toFixed(0)} KB`);
  const draws = root.listMeshes().reduce((n, m) => n + m.listPrimitives().length, 0);
  report[spec.id] = { ...r, bytes: glb.length, textures, drawCalls: draws };
  console.log(`${spec.id}: ${(r.rawBytes / 1e6).toFixed(1)} MB raw -> ${(glb.length / 1e6).toFixed(2)} MB, ${r.tris} tris, ${draws} draws, ${textures.length} textures`);
}

const ids = Object.keys(arenas);
fs.writeFileSync(path.join(outDir, 'arenas.json'), JSON.stringify({ v: 1, arenas }, null, 1) + '\n');
fs.writeFileSync(path.join(outDir, 'report.json'), JSON.stringify(report, null, 1) + '\n');
const published = [...ids.map((id) => arenas[id].file), 'arenas.json', 'report.json'];
const sums = published.map((f) => `${crypto.createHash('sha256').update(fs.readFileSync(path.join(outDir, f))).digest('hex')}  ${f}`);
fs.writeFileSync(path.join(outDir, 'SHA256SUMS'), sums.join('\n') + '\n');

const total = ids.reduce((n, id) => n + arenas[id].bytes, 0);
const line = `${ids.length} arenas, ${(total / 1e6).toFixed(1)} MB in all${failures.length ? `; ${failures.length} failed` : ''}`;
console.log(line);
if (process.env.GITHUB_STEP_SUMMARY) {
  const rows = ids.map((id) => `| ${id} | ${arenas[id].name} | ${report[id].parts.join(' + ')} | ${(arenas[id].bytes / 1e6).toFixed(2)} MB | ${report[id].tris} | ${report[id].drawCalls} | ${arenas[id].radius} |`);
  fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, [`### ${line}`, '', '| id | name | parts | size | tris | draws | radius m |', '|---|---|---|---|---|---|---|', ...rows, '', ...failures.map((f) => `- FAILED ${f}`), ''].join('\n'));
}
if (failures.length) {
  for (const f of failures) console.log(`::error::${f}`);
  process.exitCode = 1;
}
