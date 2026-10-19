using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PokeModelExporter.Model;

namespace Eggy.Conv
{
    // A GLB (FBX2glTF's, from the Unity path) read back into the converter's model: every node with its rest
    // transform, every kept mesh in its rest pose (skinned vertices put through their bind so the skeleton's
    // rest pose is the bind pose; unskinned ones ride their node as a joint), one joint per node any skin
    // uses, and every animation as bone tracks in frames (30 per second). UVs come back with V from the
    // bottom (Unity's way), as the Sword/Shield baker reads them. Everything is scaled by `unit` (node and key
    // translations, vertex positions) so the model is in metres: the optimiser's key tolerances are in metres,
    // and FBX2glTF's centimetre-on-centimetre output (a Ledyba 0.01 tall) lost its idle bob to them.
    public static class GltfRead
    {
        public const float Fps = 30;

        public sealed class Result
        {
            public ModelData Model;
            public List<AnimationData> Clips = new();
            public JsonObject Report = new();
        }

        static JsonNode R3(float v) => MathF.Round(v, 3);

        public static Result Read(string path, Func<string, string, bool> keepPrimitive, float unit = 1)
        {
            var glb = File.ReadAllBytes(path);
            if (BitConverter.ToUInt32(glb, 0) != 0x46546C67) throw new InvalidDataException($"{path}: not a GLB");
            int jsonLen = BitConverter.ToInt32(glb, 12);
            var doc = JsonDocument.Parse(Encoding.UTF8.GetString(glb, 20, jsonLen)).RootElement;
            int binAt = 20 + jsonLen + 8;
            var res = new Result { Model = new ModelData { Name = Path.GetFileNameWithoutExtension(path) } };
            var model = res.Model;

            JsonElement Arr(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v : default;
            int Count(JsonElement a) => a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0;
            var accessors = Arr(doc, "accessors");
            var views = Arr(doc, "bufferViews");

            float[] Floats(int ai, out int comps)
            {
                var a = accessors[ai];
                int count = a.GetProperty("count").GetInt32();
                comps = a.GetProperty("type").GetString() switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, var t => throw new InvalidDataException("accessor type " + t) };
                int ct = a.GetProperty("componentType").GetInt32();
                bool norm = a.TryGetProperty("normalized", out var nv) && nv.GetBoolean();
                var outv = new float[count * comps];
                if (!a.TryGetProperty("bufferView", out var bvi)) return outv;
                var bv = views[bvi.GetInt32()];
                int size = ct switch { 5126 => 4, 5125 => 4, 5123 => 2, 5122 => 2, 5121 => 1, 5120 => 1, _ => 4 };
                int stride = bv.TryGetProperty("byteStride", out var st) ? st.GetInt32() : size * comps;
                int start = binAt + (bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0) + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
                for (int i = 0; i < count; i++)
                    for (int c = 0; c < comps; c++)
                    {
                        int at = start + i * stride + c * size;
                        outv[i * comps + c] = ct switch
                        {
                            5126 => BitConverter.ToSingle(glb, at),
                            5125 => BitConverter.ToUInt32(glb, at),
                            5123 => norm ? BitConverter.ToUInt16(glb, at) / 65535f : BitConverter.ToUInt16(glb, at),
                            5122 => norm ? Math.Max(-1, BitConverter.ToInt16(glb, at) / 32767f) : BitConverter.ToInt16(glb, at),
                            5121 => norm ? glb[at] / 255f : glb[at],
                            5120 => norm ? Math.Max(-1, (sbyte)glb[at] / 127f) : (sbyte)glb[at],
                            _ => 0,
                        };
                    }
                return outv;
            }

            // ---- Nodes ----
            var nodes = Arr(doc, "nodes");
            int nn = Count(nodes);
            var parent = Enumerable.Repeat(-1, nn).ToArray();
            var names = new HashSet<string>();
            for (int i = 0; i < nn; i++)
            {
                var n = nodes[i];
                string name = n.TryGetProperty("name", out var nm) ? nm.GetString() : $"node{i}";
                while (!names.Add(name)) name += "_" + i;
                var nd = new NodeData { Name = name };
                if (n.TryGetProperty("matrix", out var mx))
                {
                    var f = mx.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                    var m = new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
                    Matrix4x4.Decompose(m, out var s, out var r, out var t);
                    nd.Scale = s; nd.Rotation = r; nd.Translation = t;
                }
                else
                {
                    if (n.TryGetProperty("translation", out var t)) nd.Translation = V3(t);
                    if (n.TryGetProperty("rotation", out var r)) { var q = r.EnumerateArray().Select(x => x.GetSingle()).ToArray(); nd.Rotation = new Quaternion(q[0], q[1], q[2], q[3]); }
                    if (n.TryGetProperty("scale", out var s)) nd.Scale = V3(s);
                }
                model.Nodes.Add(nd);
                if (n.TryGetProperty("children", out var ch)) foreach (var c in ch.EnumerateArray()) parent[c.GetInt32()] = i;
            }
            for (int i = 0; i < nn; i++) model.Nodes[i].Parent = parent[i];
            var world = new Matrix4x4[nn];
            var done = new bool[nn];
            Matrix4x4 World(int i)
            {
                if (done[i]) return world[i];
                var nd = model.Nodes[i];
                var local = Matrix4x4.CreateScale(nd.Scale) * Matrix4x4.CreateFromQuaternion(nd.Rotation) * Matrix4x4.CreateTranslation(nd.Translation);
                world[i] = parent[i] >= 0 ? local * World(parent[i]) : local;
                done[i] = true;
                return world[i];
            }
            int nextJoint = 0;
            int JointOf(int node)
            {
                if (model.Nodes[node].SkinIndex < 0) model.Nodes[node].SkinIndex = nextJoint++;
                return model.Nodes[node].SkinIndex;
            }

            // ---- Meshes, in their rest pose ----
            var meshes = Arr(doc, "meshes");
            var materials = Arr(doc, "materials");
            var skins = Arr(doc, "skins");
            var dropped = new JsonArray();
            var prims = new JsonArray();
            for (int ni = 0; ni < nn; ni++)
            {
                var n = nodes[ni];
                if (!n.TryGetProperty("mesh", out var mi)) continue;
                var gm = meshes[mi.GetInt32()];
                string meshName = gm.TryGetProperty("name", out var mn) ? mn.GetString() : model.Nodes[ni].Name;
                int[] skinJoints = null;
                Matrix4x4[] skinMats = null;
                if (n.TryGetProperty("skin", out var si))
                {
                    var skin = skins[si.GetInt32()];
                    skinJoints = skin.GetProperty("joints").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    var ibm = skin.TryGetProperty("inverseBindMatrices", out var ib) ? Floats(ib.GetInt32(), out _) : null;
                    skinMats = new Matrix4x4[skinJoints.Length];
                    for (int j = 0; j < skinJoints.Length; j++)
                    {
                        var inv = ibm == null ? Matrix4x4.Identity : new Matrix4x4(ibm[j * 16], ibm[j * 16 + 1], ibm[j * 16 + 2], ibm[j * 16 + 3], ibm[j * 16 + 4], ibm[j * 16 + 5], ibm[j * 16 + 6], ibm[j * 16 + 7],
                            ibm[j * 16 + 8], ibm[j * 16 + 9], ibm[j * 16 + 10], ibm[j * 16 + 11], ibm[j * 16 + 12], ibm[j * 16 + 13], ibm[j * 16 + 14], ibm[j * 16 + 15]);
                        skinMats[j] = inv * World(skinJoints[j]);
                    }
                }
                var P = new List<Vector3>(); var N = new List<Vector3>(); var U0 = new List<Vector2>(); var U1 = new List<Vector2>();
                var J = new List<int>(); var W = new List<float>();
                var subs = new List<SubMeshData>();
                bool hasUv1 = false;
                foreach (var prim in gm.GetProperty("primitives").EnumerateArray())
                {
                    if (prim.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4) continue;
                    string mat = prim.TryGetProperty("material", out var pm) && Count(materials) > pm.GetInt32()
                        ? (materials[pm.GetInt32()].TryGetProperty("name", out var mnm) ? mnm.GetString() : $"material{pm.GetInt32()}") : "DefaultMaterial";
                    if (!keepPrimitive(meshName, mat)) { dropped.Add($"{meshName}/{mat}"); continue; }
                    var attrs = prim.GetProperty("attributes");
                    var pos = Floats(attrs.GetProperty("POSITION").GetInt32(), out _);
                    int vc = pos.Length / 3, b = P.Count;
                    var nor = attrs.TryGetProperty("NORMAL", out var na) ? Floats(na.GetInt32(), out _) : null;
                    var uv0 = attrs.TryGetProperty("TEXCOORD_0", out var t0) ? Floats(t0.GetInt32(), out _) : null;
                    var uv1 = attrs.TryGetProperty("TEXCOORD_1", out var t1) ? Floats(t1.GetInt32(), out _) : null;
                    var jn = skinJoints != null && attrs.TryGetProperty("JOINTS_0", out var ja) ? Floats(ja.GetInt32(), out _) : null;
                    var wt = skinJoints != null && attrs.TryGetProperty("WEIGHTS_0", out var wa) ? Floats(wa.GetInt32(), out _) : null;
                    hasUv1 |= uv1 != null;
                    for (int v = 0; v < vc; v++)
                    {
                        var p = new Vector3(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
                        var q = nor != null ? new Vector3(nor[v * 3], nor[v * 3 + 1], nor[v * 3 + 2]) : Vector3.UnitY;
                        if (jn != null && wt != null)
                        {
                            var acc = Vector3.Zero; var accN = Vector3.Zero; float sum = 0;
                            for (int k = 0; k < 4; k++)
                            {
                                float w = wt[v * 4 + k];
                                if (w <= 0) continue;
                                int sj = (int)jn[v * 4 + k];
                                acc += Vector3.Transform(p, skinMats[sj]) * w;
                                accN += Vector3.TransformNormal(q, skinMats[sj]) * w;
                                sum += w;
                                J.Add(JointOf(skinJoints[sj])); W.Add(w);
                            }
                            for (int k = J.Count - (P.Count * 4); k < 4; k++) { J.Add(0); W.Add(0); }
                            if (sum > 0) { p = acc / sum; q = accN; }
                            float tot = 0;
                            for (int k = 0; k < 4; k++) tot += W[W.Count - 4 + k];
                            if (tot > 0) for (int k = 0; k < 4; k++) W[W.Count - 4 + k] /= tot;
                        }
                        else
                        {
                            p = Vector3.Transform(p, World(ni));
                            q = Vector3.TransformNormal(q, World(ni));
                            J.Add(JointOf(ni)); J.Add(0); J.Add(0); J.Add(0);
                            W.Add(1); W.Add(0); W.Add(0); W.Add(0);
                        }
                        P.Add(p * unit);
                        N.Add(q.LengthSquared() > 1e-12f ? Vector3.Normalize(q) : Vector3.UnitY);
                        U0.Add(uv0 != null ? new Vector2(uv0[v * 2], 1 - uv0[v * 2 + 1]) : Vector2.Zero);
                        U1.Add(uv1 != null ? new Vector2(uv1[v * 2], 1 - uv1[v * 2 + 1]) : Vector2.Zero);
                    }
                    int[] idx;
                    if (prim.TryGetProperty("indices", out var ii)) idx = Floats(ii.GetInt32(), out _).Select(x => (int)x + b).ToArray();
                    else idx = Enumerable.Range(b, vc).ToArray();
                    subs.Add(new SubMeshData { Material = mat, Indices = idx });
                    // What each part is, for reading a model: its size and place at rest, and its vertex colours
                    // (which the converter does not use).
                    var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
                    for (int v = b; v < P.Count; v++) { lo = Vector3.Min(lo, P[v]); hi = Vector3.Max(hi, P[v]); }
                    var info = new JsonObject
                    {
                        ["mesh"] = meshName, ["material"] = mat, ["tris"] = idx.Length / 3,
                        ["box"] = new JsonArray(R3(lo.X), R3(lo.Y), R3(lo.Z), R3(hi.X), R3(hi.Y), R3(hi.Z)),
                    };
                    if (attrs.TryGetProperty("COLOR_0", out var ca))
                    {
                        var col = Floats(ca.GetInt32(), out int cc);
                        double r0 = 0, g0 = 0, b0 = 0, a0 = 0; float amin = 1, amax = 0; int under = 0;
                        for (int v = 0; v < vc; v++)
                        {
                            r0 += col[v * cc]; g0 += col[v * cc + 1]; b0 += col[v * cc + 2];
                            float a = cc == 4 ? col[v * cc + 3] : 1;
                            a0 += a; amin = Math.Min(amin, a); amax = Math.Max(amax, a); if (a < 0.5f) under++;
                        }
                        info["color"] = new JsonArray(R3((float)(r0 / vc)), R3((float)(g0 / vc)), R3((float)(b0 / vc)), R3((float)(a0 / vc)));
                        info["alpha"] = $"{amin:F2}..{amax:F2}, {100.0 * under / vc:F0}% under half";
                    }
                    prims.Add(info);
                }
                if (subs.Count == 0) continue;
                var md = new MeshData { Name = meshName, Positions = P.ToArray(), Normals = N.ToArray(), Joints = J.ToArray(), Weights = W.ToArray() };
                md.TexCoords.Add(U0.ToArray());
                if (hasUv1) md.TexCoords.Add(U1.ToArray());
                md.SubMeshes.AddRange(subs);
                model.Meshes.Add(md);
            }
            // The skeleton in metres too (after the meshes, which were put through it as it was).
            foreach (var nd in model.Nodes) nd.Translation *= unit;
            res.Report["dropped"] = dropped;
            res.Report["primitives"] = prims;
            res.Report["joints"] = nextJoint;

            // ---- Animations, as bone tracks in frames ----
            var anims = Arr(doc, "animations");
            for (int ai = 0; ai < Count(anims); ai++)
            {
                var a = anims[ai];
                var clip = new AnimationData { Name = a.TryGetProperty("name", out var an) ? an.GetString() : $"clip{ai}", FramesPerSecond = Fps };
                var samplers = a.GetProperty("samplers");
                var tracks = new Dictionary<int, BoneTrackData>();
                float end = 0;
                foreach (var ch in a.GetProperty("channels").EnumerateArray())
                {
                    var target = ch.GetProperty("target");
                    if (!target.TryGetProperty("node", out var tn)) continue;
                    int node = tn.GetInt32();
                    string pathName = target.GetProperty("path").GetString();
                    var smp = samplers[ch.GetProperty("sampler").GetInt32()];
                    var times = Floats(smp.GetProperty("input").GetInt32(), out _);
                    var vals = Floats(smp.GetProperty("output").GetInt32(), out int comps);
                    bool cubic = smp.TryGetProperty("interpolation", out var ip) && ip.GetString() == "CUBICSPLINE";
                    int stride = cubic ? 3 : 1, off = cubic ? 1 : 0;
                    if (times.Length > 0) end = Math.Max(end, times[^1]);
                    if (!tracks.TryGetValue(node, out var tr)) tracks[node] = tr = new BoneTrackData { Bone = model.Nodes[node].Name };
                    var frames = times.Select(t => t * Fps).ToArray();
                    switch (pathName)
                    {
                        case "translation":
                            tr.Translation = new Keys<Vector3> { Times = frames, Values = frames.Select((_, k) => unit * new Vector3(vals[(k * stride + off) * comps], vals[(k * stride + off) * comps + 1], vals[(k * stride + off) * comps + 2])).ToArray() };
                            break;
                        case "scale":
                            tr.Scale = new Keys<Vector3> { Times = frames, Values = frames.Select((_, k) => new Vector3(vals[(k * stride + off) * comps], vals[(k * stride + off) * comps + 1], vals[(k * stride + off) * comps + 2])).ToArray() };
                            break;
                        case "rotation":
                            tr.Rotation = new Keys<Quaternion> { Times = frames, Values = frames.Select((_, k) => Quaternion.Normalize(new Quaternion(vals[(k * stride + off) * comps], vals[(k * stride + off) * comps + 1], vals[(k * stride + off) * comps + 2], vals[(k * stride + off) * comps + 3]))).ToArray() };
                            break;
                    }
                }
                clip.FrameCount = (int)Math.Round(end * Fps) + 1;
                clip.Tracks.AddRange(tracks.Values);
                res.Clips.Add(clip);
            }
            return res;
        }

        static Vector3 V3(JsonElement e)
        {
            var f = e.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            return new Vector3(f[0], f[1], f[2]);
        }
    }
}
