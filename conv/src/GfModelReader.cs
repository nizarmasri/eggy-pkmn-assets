using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using PokeModelExporter.Formats;
using PokeModelExporter.Model;

namespace Eggy.Conv
{
    // A Sword/Shield or Let's Go model (.gfbmdl) and what its materials reference.
    public sealed class GfModel
    {
        public ModelData Model;
        public List<string> TextureNames = new List<string>();
        public List<GfMaterial> Materials = new List<GfMaterial>();
    }

    public sealed class GfMaterial
    {
        public string Name, ShaderGroup, ShaderName;
        public int RenderLayer;
        public List<(string Sampler, string Texture, uint WrapU, uint WrapV)> Maps = new List<(string, string, uint, uint)>();
        public Dictionary<string, bool> Switches = new Dictionary<string, bool>();
        public Dictionary<string, float> Values = new Dictionary<string, float>();
        public Dictionary<string, Vector3> Colors = new Dictionary<string, Vector3>();
    }

    // Reads GFBMDL, a FlatBuffer (field numbers from the GFLib schema the community add-ons use,
    // github.com/ChicoEevee/Pokemon-Switch-Model-Importer-Blender GFLib/Model):
    //   Model { 0 version, 1 bounds, 2 texture names, 3 shader names, 4 ?, 5 material names, 6 materials,
    //           7 groups (bone + mesh pairs), 8 meshes, 9 bones, 10 collision groups }
    //   Bone { 0 name, 1 type, 2 parent, 3 ?, 4 visible, 5 scale, 6 rotation (Euler XYZ, radians), 7 translation,
    //          8, 9 radii, 10 rigid-check (present on bones vertices do not index) }
    //   Mesh { 0 polygons [material index, u16 triangle list], 1 attributes [type, format, count], 2 interleaved vertex bytes }
    //   Material { 0 name, 1 shader group, 2 render layer, 8 shader index, 11 texture maps [sampler, texture index,
    //              params {1 wrap U, 2 wrap V}], 12 switches, 13 floats, 14 colours, 20 common {switches, ints, colours} }
    // The skeleton is Maya's: every joint compensates its parent's scale, like Trinity's.
    public static class GfModelReader
    {
        // The Let's Go / Sword / Shield model files are in centimetres.
        public const double UnitsToMetres = 0.01;

        enum VertexType { Position = 0, Normal = 1, Tangent = 2, Uv1 = 3, Uv2 = 4, Uv3 = 5, Uv4 = 6, Color1 = 7, Color2 = 8, BoneId = 11, BoneWeight = 12 }

        public static GfModel Read(string file, Func<string, byte[]> read, JsonObject report)
        {
            var data = read(file) ?? throw new FileNotFoundException("missing model " + file);
            var root = FbTable.Root(data);
            var gm = new GfModel { Model = new ModelData { Name = Path.GetFileNameWithoutExtension(file) } };
            var model = gm.Model;
            var info = new JsonObject { ["version"] = root.U32(0) };
            report["gf"] = info;

            var tex = root.Vector(2);
            for (int i = 0; i < tex.Length; i++) gm.TextureNames.Add(tex.String(i));
            var shaders = new List<string>();
            var sv = root.Vector(3);
            for (int i = 0; i < sv.Length; i++) shaders.Add(sv.String(i));

            // ---- Skeleton ----
            var bones = root.Vector(9);
            var skinList = new List<int>();
            var typeOne = new List<int>();
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones.Table(i);
                int parent = b.I32(2, 0);
                if (parent == i || parent >= bones.Length) parent = -1;
                bool rigidCheck = b.Has(10);
                if (!rigidCheck) skinList.Add(i);
                if (b.U32(1) == 1) typeOne.Add(i);
                model.Nodes.Add(new NodeData
                {
                    Name = b.String(0) ?? $"bone{i}",
                    Parent = parent,
                    Scale = Vec3(b, 5, Vector3.One),
                    Rotation = EulerXyz(Vec3(b, 6, Vector3.Zero)),
                    Translation = Vec3(b, 7, Vector3.Zero),
                    ScaleCompensate = true,
                });
            }
            // Vertex bone indices count only the bones without rigid-check data (as the add-on maps them).
            for (int k = 0; k < skinList.Count; k++) model.Nodes[skinList[k]].SkinIndex = k;
            info["bones"] = bones.Length;
            info["skinBones"] = skinList.Count;
            info["typeOneBones"] = typeOne.Count;
            info["rootParent"] = bones.Length > 0 ? bones.Table(0).I32(2, 0) : 0;

            // ---- Materials ----
            var mats = root.Vector(6);
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats.Table(i);
                var gmat = new GfMaterial { Name = m.String(0) ?? $"material{i}", ShaderGroup = m.String(1), RenderLayer = m.I32(2) };
                int si = m.I32(8);
                if (si >= 0 && si < shaders.Count) gmat.ShaderName = shaders[si];
                var maps = m.Vector(11);
                for (int k = 0; k < maps.Length; k++)
                {
                    var tm = maps.Table(k);
                    int index = tm.I32(1);
                    var prm = tm.Table(2);
                    gmat.Maps.Add((tm.String(0), index >= 0 && index < gm.TextureNames.Count ? gm.TextureNames[index] : null,
                        prm.IsNull ? 0u : prm.U32(1), prm.IsNull ? 0u : prm.U32(2)));
                }
                ReadParams(m.Vector(12), m.Vector(13), m.Vector(14), gmat, floats: true);
                var common = m.Table(20);
                if (!common.IsNull) ReadParams(common.Vector(0), common.Vector(1), common.Vector(2), gmat, floats: false);
                gm.Materials.Add(gmat);
                model.Materials.Add(ToMaterialData(gmat));
            }

            // ---- Meshes: each one belongs to a group, which names the bone it hangs from ----
            var groups = root.Vector(7);
            var meshBone = new Dictionary<int, int>();
            for (int g = 0; g < groups.Length; g++)
            {
                var gr = groups.Table(g);
                meshBone[(int)gr.U32(1)] = (int)gr.U32(0);
            }
            var meshes = root.Vector(8);
            var meshInfo = new JsonArray();
            int nextSkin = skinList.Count;
            for (int mi = 0; mi < meshes.Length; mi++)
            {
                var mt = meshes.Table(mi);
                meshBone.TryGetValue(mi, out int boneIndex);
                string name = boneIndex >= 0 && boneIndex < model.Nodes.Count ? model.Nodes[boneIndex].Name : $"mesh{mi}";
                var mesh = new MeshData { Name = name };
                var attrs = mt.Vector(1);
                var layout = new List<(VertexType Type, uint Format, int Count, int Offset)>();
                int stride = 0;
                for (int a = 0; a < attrs.Length; a++)
                {
                    var at = attrs.Table(a);
                    var vt = (VertexType)at.U32(0);
                    uint fmt = at.U32(1);
                    int count = (int)at.U32(2);
                    layout.Add((vt, fmt, count, stride));
                    stride += FormatSize(fmt) * count;
                }
                var raw = mt.Vector(2).Bytes();
                int vertexCount = stride > 0 ? raw.Count / stride : 0;
                int maxBone = -1;
                foreach (var (type, fmt, count, offset) in layout)
                {
                    var values = new Vector4[vertexCount];
                    for (int v = 0; v < vertexCount; v++) values[v] = Decode(raw.Array, raw.Offset + v * stride + offset, fmt, count);
                    switch (type)
                    {
                        case VertexType.Position: mesh.Positions = values.Select(x => new Vector3(x.X, x.Y, x.Z)).ToArray(); break;
                        case VertexType.Normal: mesh.Normals = values.Select(x => SafeNormal(new Vector3(x.X, x.Y, x.Z))).ToArray(); break;
                        case VertexType.Uv1:
                        case VertexType.Uv2:
                            int layer = type == VertexType.Uv1 ? 0 : 1;
                            while (mesh.TexCoords.Count <= layer) mesh.TexCoords.Add(null);
                            mesh.TexCoords[layer] = values.Select(x => new Vector2(x.X, x.Y)).ToArray(); // as stored; see GfMaterials
                            break;
                        case VertexType.BoneId:
                            mesh.Joints = new int[vertexCount * 4];
                            for (int v = 0; v < vertexCount; v++)
                            {
                                mesh.Joints[v * 4] = (int)values[v].X; mesh.Joints[v * 4 + 1] = (int)values[v].Y;
                                mesh.Joints[v * 4 + 2] = (int)values[v].Z; mesh.Joints[v * 4 + 3] = (int)values[v].W;
                            }
                            break;
                        case VertexType.BoneWeight:
                            mesh.Weights = new float[vertexCount * 4];
                            for (int v = 0; v < vertexCount; v++)
                            {
                                var w = values[v];
                                if (count < 4) w.W = 0;
                                if (count < 3) w.Z = 0;
                                if (count < 2) w.Y = 0;
                                float sum = w.X + w.Y + w.Z + w.W;
                                if (sum > 0) w /= sum;
                                mesh.Weights[v * 4] = w.X; mesh.Weights[v * 4 + 1] = w.Y;
                                mesh.Weights[v * 4 + 2] = w.Z; mesh.Weights[v * 4 + 3] = w.W;
                            }
                            break;
                    }
                }
                if (mesh.Joints != null && mesh.Weights == null)
                {
                    // Indices without weights: the first index takes it all.
                    mesh.Weights = new float[vertexCount * 4];
                    for (int v = 0; v < vertexCount; v++) mesh.Weights[v * 4] = 1;
                }
                if (mesh.Joints != null)
                {
                    for (int i = 0; i < mesh.Joints.Length; i++)
                        if (mesh.Weights[i] > 0) maxBone = Math.Max(maxBone, mesh.Joints[i]);
                }
                else if (mesh.Positions != null && boneIndex >= 0 && boneIndex < model.Nodes.Count)
                {
                    // Not skinned: it rides its group's bone rigidly.
                    var node = model.Nodes[boneIndex];
                    if (node.SkinIndex < 0) node.SkinIndex = nextSkin++;
                    mesh.Joints = new int[vertexCount * 4];
                    mesh.Weights = new float[vertexCount * 4];
                    for (int v = 0; v < vertexCount; v++) { mesh.Joints[v * 4] = node.SkinIndex; mesh.Weights[v * 4] = 1; }
                }

                var polys = mt.Vector(0);
                for (int p = 0; p < polys.Length; p++)
                {
                    var poly = polys.Table(p);
                    int matIndex = (int)poly.U32(0);
                    var faces = poly.Vector(1);
                    var idx = new int[faces.Length - faces.Length % 3];
                    for (int k = 0; k < idx.Length; k++) idx[k] = faces.U16(k);
                    mesh.SubMeshes.Add(new SubMeshData { Material = matIndex >= 0 && matIndex < gm.Materials.Count ? gm.Materials[matIndex].Name : null, Indices = idx });
                }
                model.Meshes.Add(mesh);
                meshInfo.Add(new JsonObject
                {
                    ["name"] = name,
                    ["vertices"] = vertexCount,
                    ["stride"] = stride,
                    ["layout"] = string.Join(" ", layout.Select(l => $"{l.Type}:{l.Format}x{l.Count}")),
                    ["maxBoneIndex"] = maxBone,
                    ["uvRange"] = mesh.TexCoords.Count > 0 && mesh.TexCoords[0] != null && vertexCount > 0
                        ? $"u {mesh.TexCoords[0].Min(t => t.X):F3}..{mesh.TexCoords[0].Max(t => t.X):F3} v {mesh.TexCoords[0].Min(t => t.Y):F3}..{mesh.TexCoords[0].Max(t => t.Y):F3}"
                        : null,
                    ["rigid"] = maxBone < 0,
                });
                if (maxBone >= skinList.Count)
                    throw new InvalidDataException($"{name}: vertex bone index {maxBone} but only {skinList.Count} skinning bones (mapping is wrong)");
            }
            info["meshes"] = meshInfo;
            return gm;
        }

        static void ReadParams(FbVector switches, FbVector values, FbVector colors, GfMaterial m, bool floats)
        {
            for (int i = 0; i < switches.Length; i++)
            {
                var s = switches.Table(i);
                string n = s.String(0);
                if (n != null && !m.Switches.ContainsKey(n)) m.Switches[n] = s.Bool(1);
            }
            for (int i = 0; i < values.Length; i++)
            {
                var v = values.Table(i);
                string n = v.String(0);
                if (n != null && !m.Values.ContainsKey(n)) m.Values[n] = floats ? v.F32(1) : v.I32(1);
            }
            for (int i = 0; i < colors.Length; i++)
            {
                var c = colors.Table(i);
                string n = c.String(0);
                int p = c.InlineStruct(1);
                if (n != null && p != 0 && !m.Colors.ContainsKey(n))
                    m.Colors[n] = new Vector3(BitConverter.ToSingle(c.Buffer, p), BitConverter.ToSingle(c.Buffer, p + 4), BitConverter.ToSingle(c.Buffer, p + 8));
            }
        }

        // The game material as PME's MaterialData, so its parameters travel in the GLB's material extras.
        static MaterialData ToMaterialData(GfMaterial g)
        {
            var m = new MaterialData { Name = g.Name, Shader = g.ShaderName ?? g.ShaderGroup };
            foreach (var map in g.Maps)
                if (map.Sampler != null && map.Texture != null) m.Textures[map.Sampler] = map.Texture;
            foreach (var kv in g.Values) m.Floats[kv.Key] = kv.Value;
            foreach (var kv in g.Colors) m.Colors[kv.Key] = new Vector4(kv.Value, 1);
            foreach (var kv in g.Switches) m.ShaderOptions[kv.Key] = kv.Value ? "True" : "False";
            m.ShaderOptions["ShaderGroup"] = g.ShaderGroup ?? "";
            m.ShaderOptions["RenderLayer"] = g.RenderLayer.ToString();
            return m;
        }

        // Buffer formats: 0 float, 1 half, 3 unsigned byte, 5 unsigned short, 8 unsigned byte as 0..1.
        static int FormatSize(uint f) => f switch { 0 => 4, 1 => 2, 3 => 1, 5 => 2, 8 => 1, _ => throw new NotSupportedException($"GFBMDL buffer format {f}") };

        static Vector4 Decode(byte[] b, int p, uint fmt, int count)
        {
            float C(int i)
            {
                if (i >= count) return 0;
                switch (fmt)
                {
                    case 0: return BitConverter.ToSingle(b, p + 4 * i);
                    case 1: return (float)BitConverter.ToHalf(b, p + 2 * i);
                    case 3: return b[p + i];
                    case 5: return BitConverter.ToUInt16(b, p + 2 * i);
                    case 8: return b[p + i] / 255f;
                    default: return 0;
                }
            }
            return new Vector4(C(0), C(1), C(2), C(3));
        }

        static Vector3 SafeNormal(Vector3 n) => n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;

        static Vector3 Vec3(FbTable table, int field, Vector3 fallback)
        {
            int p = table.InlineStruct(field);
            if (p == 0) return fallback;
            var b = table.Buffer;
            return new Vector3(BitConverter.ToSingle(b, p), BitConverter.ToSingle(b, p + 4), BitConverter.ToSingle(b, p + 8));
        }

        // X, then Y, then Z (the same Euler order as Trinity's skeletons).
        static Quaternion EulerXyz(Vector3 e) =>
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, e.Z) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, e.Y) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, e.X);
    }
}
