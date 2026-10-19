// What a glTF from the Unity path holds: nodes, skins, meshes, materials, textures and clips.
//
//   node pipeline/bdsp/inspect.mjs <file.glb|gltf>...
import { NodeIO } from '@gltf-transform/core';
import { ALL_EXTENSIONS } from '@gltf-transform/extensions';

const io = new NodeIO().registerExtensions(ALL_EXTENSIONS);
for (const file of process.argv.slice(2)) {
  const doc = await io.read(file);
  const root = doc.getRoot();
  const tris = root.listMeshes().reduce((n, m) => n + m.listPrimitives().reduce((k, p) => k + (p.getIndices()?.getCount() ?? 0) / 3, 0), 0);
  console.log(`== ${file}`);
  console.log(`nodes ${root.listNodes().length}, skins ${root.listSkins().map((s) => s.listJoints().length).join('/')}, meshes ${root.listMeshes().length}, tris ${tris}`);
  for (const m of root.listMeshes()) console.log(`  mesh ${m.getName()}: ${m.listPrimitives().map((p) => `${p.getMaterial()?.getName()} [${p.listSemantics().join(' ')}]`).join('; ')}`);
  for (const m of root.listMaterials()) console.log(`  material ${m.getName()} ${m.getAlphaMode()} base ${m.getBaseColorTexture()?.getName() ?? '-'} ${m.getBaseColorTexture()?.getSize()?.join('x') ?? ''}`);
  for (const t of root.listTextures()) console.log(`  texture ${t.getName()} ${t.getURI()} ${t.getMimeType()} ${t.getSize()?.join('x')}`);
  for (const a of root.listAnimations()) {
    const end = Math.max(0, ...a.listSamplers().map((s) => s.getInput()?.getMax([])[0] ?? 0));
    console.log(`  clip ${a.getName()} ${end.toFixed(2)} s, ${a.listChannels().length} channels`);
  }
  const top = root.listScenes()[0]?.listChildren() ?? [];
  const walk = (n, d) => { if (d > 3) return; console.log(`  ${'  '.repeat(d)}${n.getName()} t ${n.getTranslation().map((x) => x.toFixed(3))} s ${n.getScale().map((x) => x.toFixed(3))}`); for (const c of n.listChildren()) walk(c, d + 1); };
  for (const n of top) walk(n, 0);
}
