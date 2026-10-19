using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using PokeModelExporter.Export;
using PokeModelExporter.Formats;
using PokeModelExporter.Model;
using PokeModelExporter.Scarlet;

namespace Eggy.Conv
{
    // What a model holds beyond what the GLB shows, written while it converts (--diag <dir>), to decide
    // how to carry it: every material's parameters and textures (format, size, each channel as a sheet),
    // which joints each mesh hangs from, every clip's mesh-visibility and material tracks, and a
    // material table (.trmmt: Arceus's types) with the colours its embedded curves set.
    public static class Diag
    {
        public static void Write(Job job, ModelData model, Func<string, byte[]> read, Dictionary<string, AnimationData> clips, string dir)
        {
            Directory.CreateDirectory(dir);
            var d = new JsonObject { ["id"] = job.Id, ["src"] = job.Src, ["folder"] = job.Folder };

            // ---- Materials ----
            var mats = new JsonArray();
            var sheets = new HashSet<string>();
            foreach (var m in model.Materials)
            {
                var tex = new JsonObject();
                foreach (var kv in m.Textures)
                {
                    string info = "missing";
                    var bytes = read(kv.Value) ?? read(kv.Value + ".bntx");
                    if (bytes != null)
                    {
                        try
                        {
                            var t = Bntx.Read(bytes);
                            info = $"0x{t.Format:X4} {t.Width}x{t.Height} ch {string.Join("", t.Channels.Select(c => "01RGBA"[Math.Min(5, (int)c)]))}";
                            string key = Path.GetFileNameWithoutExtension(kv.Value);
                            if (sheets.Add(key)) File.WriteAllBytes(Path.Combine(dir, $"{job.Id}_ch_{key}.png"), Channels(t));
                        }
                        catch (Exception e) { info = "unreadable: " + e.Message; }
                    }
                    var wrap = m.Wraps.TryGetValue(kv.Key, out var w) ? $" wrap {w.U},{w.V}" : "";
                    tex[kv.Key] = $"{kv.Value} [{info}]{wrap}";
                }
                mats.Add(new JsonObject
                {
                    ["name"] = m.Name,
                    ["shader"] = m.Shader,
                    ["alphaType"] = m.AlphaType,
                    ["textures"] = tex,
                    ["floats"] = new JsonObject(m.Floats.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, Math.Round(kv.Value, 4)))),
                    ["colors"] = new JsonObject(m.Colors.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, V4(kv.Value)))),
                    ["options"] = new JsonObject(m.ShaderOptions.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value))),
                });
            }
            d["materials"] = mats;

            // ---- Meshes: which joints carry them ----
            var names = model.Nodes.Where(n => n.SkinIndex >= 0).ToDictionary(n => n.SkinIndex, n => n.Name);
            var meshes = new JsonArray();
            foreach (var mesh in model.Meshes)
            {
                var weight = new Dictionary<int, float>();
                if (mesh.Joints != null && mesh.Weights != null)
                    for (int i = 0; i < mesh.Joints.Length; i++)
                        if (mesh.Weights[i] > 0.001f) weight[mesh.Joints[i]] = (weight.TryGetValue(mesh.Joints[i], out var x) ? x : 0) + mesh.Weights[i];
                meshes.Add(new JsonObject
                {
                    ["name"] = mesh.Name,
                    ["hidden"] = mesh.Hidden,
                    ["vertices"] = mesh.Positions?.Length ?? 0,
                    ["materials"] = new JsonArray(mesh.SubMeshes.Select(s => (JsonNode)$"{s.Material} {s.Indices.Length / 3}").ToArray()),
                    ["joints"] = new JsonArray(weight.OrderByDescending(kv => kv.Value).Take(8)
                        .Select(kv => (JsonNode)$"{(names.TryGetValue(kv.Key, out var n) ? n : kv.Key.ToString())} {kv.Value:F0}").ToArray()),
                    ["jointCount"] = weight.Count,
                });
            }
            d["meshes"] = meshes;

            // ---- Clips: visibility and material tracks ----
            var cl = new JsonObject();
            foreach (var kv in clips)
            {
                var a = kv.Value;
                cl[kv.Key] = new JsonObject
                {
                    ["frames"] = a.FrameCount,
                    ["visibility"] = new JsonArray(a.VisibilityTracks.Select(v => (JsonNode)(v.Fixed != null
                        ? $"{v.Mesh}: {(v.Fixed.Value ? "shown" : "hidden")}"
                        : $"{v.Mesh}: {string.Join(" ", v.Keys.Times.Zip(v.Keys.Values, (t, b) => $"{t}:{(b ? 1 : 0)}"))}")).ToArray()),
                    ["material"] = new JsonArray(a.MaterialTracks.Select(t => (JsonNode)$"{t.Mesh}/{t.Material}/{t.Parameter} {Keys(t.R)} {Keys(t.G)} {Keys(t.B)} {Keys(t.A)}").ToArray()),
                    ["morph"] = new JsonArray(a.MorphTracks.Select(t => (JsonNode)$"{t.Mesh}/{t.Shape} {Keys(t.Weight)}").ToArray()),
                };
            }
            d["clips"] = cl;

            // ---- Material table ----
            var trmmt = read(job.Folder + ".trmmt");
            if (trmmt != null)
            {
                try { d["trmmt"] = MaterialTable(trmmt); }
                catch (Exception e) { d["trmmt"] = "unreadable: " + e.Message; }
            }
            File.WriteAllText(Path.Combine(dir, $"{job.Id}_diag.json"), d.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
        }

        // TRMMT { 0 res, 1 res, 2 material: [MMT { 0 name, 1 material_name: [string], 2 material_switches: [{ 0 name, 1 flag }],
        //         3 material_properties: [{ 0 name, 1 mappers: [{ 0 mesh, 1 material, 2 layer }], 2, 3, 4 tracm { 0 bytes }, 5 }] }] }
        public static JsonArray MaterialTable(byte[] data)
        {
            var root = FbTable.Root(data);
            var list = root.Vector(2);
            var outList = new JsonArray();
            for (int i = 0; i < list.Length; i++)
            {
                var mmt = list.Table(i);
                var names = mmt.Vector(1);
                var sw = mmt.Vector(2);
                var props = mmt.Vector(3);
                var o = new JsonObject
                {
                    ["name"] = mmt.String(0),
                    ["materialNames"] = new JsonArray(Enumerable.Range(0, names.Length).Select(k => (JsonNode)names.String(k)).ToArray()),
                    ["switches"] = new JsonArray(Enumerable.Range(0, sw.Length).Select(k => (JsonNode)$"{sw.Table(k).String(0)}={sw.Table(k).U8(1)}").ToArray()),
                };
                var pl = new JsonArray();
                for (int k = 0; k < props.Length; k++)
                {
                    var p = props.Table(k);
                    var mappers = p.Vector(1);
                    var po = new JsonObject
                    {
                        ["name"] = p.String(0),
                        ["mappers"] = new JsonArray(Enumerable.Range(0, mappers.Length).Select(x => (JsonNode)$"{mappers.Table(x).String(0)} / {mappers.Table(x).String(1)} / {mappers.Table(x).String(2)}").ToArray()),
                        ["res2"] = p.U32(2),
                        ["unk3"] = p.U32(3),
                    };
                    var tr = p.Table(4);
                    if (!tr.IsNull)
                    {
                        var bytes = tr.Vector(0).Bytes().ToArray();
                        var anim = new AnimationData();
                        try
                        {
                            TrinityCurveReader.Read(bytes, anim);
                            po["tracks"] = new JsonArray(anim.MaterialTracks.Select(t => (JsonNode)$"{t.Mesh}/{t.Material}/{t.Parameter} {Keys(t.R)} {Keys(t.G)} {Keys(t.B)} {Keys(t.A)}").ToArray());
                        }
                        catch (Exception e) { po["tracks"] = "unreadable: " + e.Message; }
                    }
                    pl.Add(po);
                }
                o["properties"] = pl;
                outList.Add(o);
            }
            return outList;
        }

        static string Keys(Keys<float> k)
        {
            if (k == null || k.Values == null || k.Values.Length == 0) return "-";
            if (k.Values.Length == 1) return $"{k.Values[0]:0.###}";
            return $"[{k.Values.Length}: {k.Values[0]:0.###}..{k.Values.Max():0.###}]";
        }

        static JsonArray V4(Vector4 v) => new JsonArray(Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4), Math.Round(v.W, 4));

        // R, G, B and A side by side, each as grey, at most 256 px a side.
        public static byte[] Channels(Bntx.Texture t)
        {
            int s = Math.Max(1, Math.Max(t.Width, t.Height) / 256);
            int w = Math.Max(1, t.Width / s), h = Math.Max(1, t.Height / s);
            var px = new byte[w * 4 * h * 4];
            for (int c = 0; c < 4; c++)
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = t.Rgba[((y * s) * t.Width + x * s) * 4 + c];
                int p = (y * w * 4 + c * w + x) * 4;
                px[p] = px[p + 1] = px[p + 2] = v;
                px[p + 3] = 255;
            }
            return Png.Encode(px, w * 4, h);
        }
    }
}
