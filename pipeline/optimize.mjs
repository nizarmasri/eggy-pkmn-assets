// Raw converter output -> the published pack: one small GLB per Pokémon form plus models.json.
//
//   node pipeline/optimize.mjs <raw dir> <out dir> [<review dir>]
//
// Per model: normalise (a scale and offset on the root node, measured by the converter over the idle
// loop: height 1, feet on y = 0, centred on x and z), glTF materials (alpha masks, unlit flames as
// emissive), drop game-only data, then resample keys, prune, dedup, WebP textures of at most 1024 px,
// meshopt reorder + quantisation, EXT_meshopt_compression.
// Raw files named "<id>--<variant>.glb" are review copies: optimised into <review dir>, not published.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { NodeIO } from '@gltf-transform/core';
import { ALL_EXTENSIONS, EXTMeshoptCompression } from '@gltf-transform/extensions';
import { dedup, prune, quantize, reorder, resample, textureCompress } from '@gltf-transform/functions';
import { MeshoptDecoder, MeshoptEncoder } from 'meshoptimizer';
import sharp from 'sharp';

const [rawDir, outDir, reviewDir = path.join(outDir, '..', 'review')] = process.argv.slice(2);
const CLIPS = ['idle', 'appear', 'cry', 'physical', 'special', 'hit', 'faint', 'blink'];

await MeshoptEncoder.ready;
await MeshoptDecoder.ready;
const io = new NodeIO().registerExtensions(ALL_EXTENSIONS).registerDependencies({
  'meshopt.encoder': MeshoptEncoder,
  'meshopt.decoder': MeshoptDecoder,
});

fs.mkdirSync(outDir, { recursive: true });
fs.mkdirSync(reviewDir, { recursive: true });
const reports = fs.readdirSync(rawDir).filter((f) => f.endsWith('.json') && !f.endsWith('.shiny.json')).sort();

// glTF materials from the converter's: alpha tests, roles, unlit flames. Shared by models and their
// shiny companions, so a companion's material is exactly what the base's would be with its shiny look.
function materials(root) {
  for (const mat of root.listMaterials()) {
    const x = mat.getExtras() ?? {};
    const options = x.options ?? {};
    const floats = x.floats ?? {};
    const alphaType = String(x.alphaType ?? '');
    // Cut-out parts (fur cards, wing edges): the game tests alpha against a threshold.
    if (options.EnableAlphaTest === 'True' || /mask|cutout|clip|alphatest/i.test(alphaType)) {
      mat.setAlphaMode('MASK').setAlphaCutoff(floats.AlphaTestThreshold ?? floats.AlphaTestRef ?? 0.5);
    }
    const role = options.EggyRole;
    if (role === 'glow') {
      // A flame or glow core: unlit, and the game draws it additively without writing depth.
      mat.setAlphaMode('BLEND').setDoubleSided(true).setEmissiveFactor([0, 0, 0]).setEmissiveTexture(null);
      mat.setExtras({ role });
      continue;
    }
    // Other parts the game draws unlit: emissive in standard PBR, so they glow under any light.
    if (mat.getExtension('KHR_materials_unlit')) {
      mat.setExtension('KHR_materials_unlit', null);
      if (mat.getBaseColorTexture() && !mat.getEmissiveTexture()) mat.setEmissiveTexture(mat.getBaseColorTexture());
      mat.setEmissiveFactor([1, 1, 1]);
    }
    mat.setExtras({});
  }
  if (!root.listMaterials().some((m) => m.getExtension('KHR_materials_unlit')))
    root.listExtensionsUsed().find((e) => e.extensionName === 'KHR_materials_unlit')?.dispose();
}
const models = {};
const report = {};
const failures = [];

for (const file of reports) {
  const r = JSON.parse(fs.readFileSync(path.join(rawDir, file), 'utf8'));
  const name = file.replace(/\.json$/, '');
  const review = name.includes('--');
  if (r.error) {
    failures.push(`${name}: ${r.error.split('\n')[0]}`);
    report[name] = r;
    continue;
  }
  const doc = await io.read(path.join(rawDir, `${name}.glb`));
  const root = doc.getRoot();

  // The converter's root node holds the skeleton and the meshes: normalising it moves everything.
  const scene = root.getDefaultScene() ?? root.listScenes()[0];
  const [top, ...others] = scene.listChildren();
  if (!top || others.length) throw new Error(`${name}: expected one root node`);
  const s = r.normalize.scale;
  top.setName(r.id).setScale([s, s, s]).setTranslation(r.normalize.translation);

  materials(root);
  root.listExtensionsUsed().find((e) => e.extensionName === 'KHR_node_visibility')?.dispose();
  for (const mesh of root.listMeshes())
    for (const prim of mesh.listPrimitives())
      for (const sem of prim.listSemantics()) if (sem.startsWith('_')) prim.setAttribute(sem, null);
  for (const list of [root.listAnimations(), root.listNodes(), root.listTextures(), root.listMeshes()])
    for (const item of list) item.setExtras({});
  const animNames = root.listAnimations().map((a) => a.getName());
  if (animNames.some((n) => !CLIPS.includes(n))) throw new Error(`${name}: unexpected clips ${animNames}`);

  await doc.transform(
    resample({ tolerance: 1e-4 }),
    prune({ keepLeaves: false }),
    // Materials keep their names (a shiny companion swaps them in by name), even where two look the same.
    dedup({ keepUniqueNames: true }),
    textureCompress({ encoder: sharp, targetFormat: 'webp', resize: [1024, 1024], quality: 88, effort: 90 }),
    reorder({ encoder: MeshoptEncoder }),
    quantize(),
  );
  doc.createExtension(EXTMeshoptCompression).setRequired(true).setEncoderOptions({
    method: EXTMeshoptCompression.EncoderMethod.QUANTIZE,
  });
  const glb = Buffer.from(await io.writeBinary(doc));
  fs.writeFileSync(path.join(review ? reviewDir : outDir, `${name}.glb`), glb);
  if (review) continue;

  const sha256 = crypto.createHash('sha256').update(glb).digest('hex');
  const shiny = r.shiny?.file ? await companion(r.id, new Set(root.listMaterials().map((m) => m.getName()))) : null;
  models[r.id] = {
    file: `${r.id}.glb`,
    bytes: glb.length,
    sha256,
    src: r.src,
    dex: r.dex,
    height: r.height,
    clips: r.clips,
    hit: r.hit,
    ...(shiny ? { shiny } : {}),
  };
  const textures = root.listTextures().map((t) => {
    const size = t.getSize();
    return `${t.getMimeType()} ${size?.join('x')} ${(t.getImage()?.byteLength / 1024).toFixed(0)} KB`;
  });
  report[r.id] = { ...r, bytes: glb.length, textures };
  console.log(`${r.id}: ${(r.rawBytes / 1e6).toFixed(1)} MB raw -> ${(glb.length / 1e6).toFixed(2)} MB, ${r.tris} tris, ${r.bones} bones`);
}

// The shiny companion: its materials as the base's are made, WebP textures, named after the base's.
// A base material the converter split off for an inverted hull ("hull <name>") gets its shiny copy too.
async function companion(id, baseNames) {
  const doc = await io.read(path.join(rawDir, `${id}.shiny.glb`));
  const root = doc.getRoot();
  materials(root);
  for (const mat of [...root.listMaterials()]) {
    const hull = `hull ${mat.getName()}`;
    if (!baseNames.has(hull)) continue;
    const copy = mat.clone().setName(hull).setDoubleSided(false);
    const node = doc.createNode(`shiny ${hull}`).setMesh(doc.createMesh(`shiny ${hull}`).addPrimitive(
      root.listMeshes()[0].listPrimitives()[0].clone().setMaterial(copy)));
    root.listScenes()[0].addChild(node);
  }
  const missing = root.listMaterials().map((m) => m.getName()).filter((n) => !baseNames.has(n));
  if (missing.length) throw new Error(`${id}.shiny.glb: materials not in the base model: ${missing.join(', ')}`);
  for (const list of [root.listNodes(), root.listMeshes(), root.listTextures()]) for (const item of list) item.setExtras({});
  await doc.transform(
    prune({ keepLeaves: true }),
    dedup({ keepUniqueNames: true }),
    textureCompress({ encoder: sharp, targetFormat: 'webp', resize: [1024, 1024], quality: 88, effort: 90 }),
  );
  doc.createExtension(EXTMeshoptCompression).setRequired(true).setEncoderOptions({ method: EXTMeshoptCompression.EncoderMethod.QUANTIZE });
  const glb = Buffer.from(await io.writeBinary(doc));
  const file = `${id}.shiny.glb`;
  fs.writeFileSync(path.join(outDir, file), glb);
  return { file, bytes: glb.length, sha256: crypto.createHash('sha256').update(glb).digest('hex') };
}

const ids = Object.keys(models).sort();
const sorted = Object.fromEntries(ids.map((id) => [id, models[id]]));
fs.writeFileSync(path.join(outDir, 'models.json'), JSON.stringify({ v: 1, models: sorted }, null, 1) + '\n');
fs.writeFileSync(path.join(outDir, 'report.json'), JSON.stringify(report, null, 1) + '\n');

// SHA256SUMS lists every published file, models.json and the report included; fetch.mjs follows it.
const published = [...ids.flatMap((id) => [models[id].file, ...(models[id].shiny ? [models[id].shiny.file] : [])]), 'models.json', 'report.json'];
const sums = published.map((f) => `${crypto.createHash('sha256').update(fs.readFileSync(path.join(outDir, f))).digest('hex')}  ${f}`);
fs.writeFileSync(path.join(outDir, 'SHA256SUMS'), sums.join('\n') + '\n');

const total = ids.reduce((n, id) => n + models[id].bytes, 0);
const line = `${ids.length} models, ${(total / 1e6).toFixed(2)} MB in all${failures.length ? `; ${failures.length} failed` : ''}`;
console.log(line);
if (process.env.GITHUB_STEP_SUMMARY) {
  const rows = ids.map((id) => {
    const m = models[id], x = report[id];
    return `| ${id} | ${m.src} ${x.folder} | ${(m.bytes / 1e3).toFixed(0)} KB | ${x.tris} | ${x.bones} | ${Object.entries(m.clips).map(([k, v]) => `${k} ${v}`).join(', ')} |`;
  });
  fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, [`### ${line}`, '', '| id | source | size | tris | bones | clips (s) |', '|---|---|---|---|---|---|', ...rows, '', ...failures.map((f) => `- FAILED ${f}`), ''].join('\n'));
}
if (failures.length) {
  for (const f of failures) console.log(`::error::${f}`);
  process.exitCode = 1;
}
