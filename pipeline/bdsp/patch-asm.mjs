// Two changes to AssetStudio (at the pinned commit) before the workflow builds its CLI; each fails loudly if
// the code it changes is not there any more.
//
// 1. Animator mode reads only Animator, Mesh and Texture2D assets: AnimationClip joins them, so
//    "--fbx-animation all" has the battle clips to bind.
// 2. A Mecanim clip's Euler rotation curves (binding attribute 4) are in Unity's order (z, then x, then y)
//    and AssetStudio passed them to the FBX as they were, which reads them in x, y, z order: any bone
//    turned on two axes at once came out pointing elsewhere (Castform's forms wore their faces on their
//    sides). They go through a quaternion here, as attribute 2's rotations do.
//
//   node pipeline/bdsp/patch-asm.mjs <AssetStudio source dir>
import fs from 'node:fs';
import path from 'node:path';

const src = process.argv[2];
function patch(rel, from, to) {
  const file = path.join(src, rel);
  const text = fs.readFileSync(file, 'utf8');
  const at = text.search(from);
  if (at < 0) throw new Error(`${rel}: the code to patch is not there (${from})`);
  fs.writeFileSync(file, text.replace(from, to));
  console.log(`${rel}: patched`);
}

patch(
  'AssetStudioCLI/Options/CLIOptions.cs',
  /(ClassIDType\.Animator,\s*ClassIDType\.Mesh,\s*ClassIDType\.Texture2D,)/,
  '$1\n                            ClassIDType.AnimationClip,',
);

patch(
  'AssetStudioUtility/ModelConverter.cs',
  /case 4:\s*track\.Rotations\.Add\(new ImportedKeyframe<Vector3>\(time, new Vector3\s*\(\s*data\[curveIndex\+\+ \+ offset\],\s*-data\[curveIndex\+\+ \+ offset\],\s*-data\[curveIndex\+\+ \+ offset\]\s*\)\)\);\s*break;/,
  `case 4:
                    {
                        // Unity's Euler angles: z first, then x, then y (q = qy * qx * qz), in degrees.
                        double ex = data[curveIndex++ + offset] * Math.PI / 360, ey = data[curveIndex++ + offset] * Math.PI / 360, ez = data[curveIndex++ + offset] * Math.PI / 360;
                        double cx = Math.Cos(ex), sx = Math.Sin(ex), cy = Math.Cos(ey), sy = Math.Sin(ey), cz = Math.Cos(ez), sz = Math.Sin(ez);
                        // qy * qx = (cy*sx, sy*cx, -sy*sx, cy*cx) as (x, y, z, w); then times qz = (0, 0, sz, cz).
                        double ax = cy * sx, ay = sy * cx, az = -sy * sx, aw = cy * cx;
                        double qw = aw * cz - az * sz, qx = ax * cz + ay * sz, qy = ay * cz - ax * sz, qz = aw * sz + az * cz;
                        track.Rotations.Add(new ImportedKeyframe<Vector3>(time, Fbx.QuaternionToEuler(new Quaternion((float)qx, (float)-qy, (float)-qz, (float)qw))));
                        break;
                    }`,
);
