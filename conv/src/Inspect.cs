using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using PokeModelExporter.Export;
using PokeModelExporter.Formats;

namespace Eggy.Conv
{
    //   eggyconv --inspect <dir> <inventory.json> [<texture png dir>]
    // An inventory of an extracted game folder, from the bytes: every file's size and magic; for GFBMDL
    // its bones (with world positions: locators), meshes (vertex layout, bounds) and materials; for BNTX
    // each texture's size, format and average colour (PNGs too, to look at); for anything else its
    // strings and a schema-less FlatBuffer guess (battle tables are FlatBuffers in these games).
    public static class Inspect
    {
        public static int Run(string dir, string outPath, string pngDir)
        {
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
            var list = new JsonArray();
            foreach (var f in files)
            {
                string rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
                var data = File.ReadAllBytes(f);
                var e = new JsonObject { ["path"] = rel, ["bytes"] = data.Length, ["magic"] = Magic(data) };
                string ext = Path.GetExtension(f).ToLowerInvariant();
                try
                {
                    if (ext == ".gfbmdl") e["model"] = Model(data, rel);
                    else if (data.Length >= 4 && Encoding.ASCII.GetString(data, 0, 4) == "BNTX") e["textures"] = Textures(data, rel, pngDir);
                    else if (data.Length >= 4 && Encoding.ASCII.GetString(data, 0, 4) == "DDS ") e["texture"] = DdsTexture(data, rel, pngDir);
                    else if (data.Length < 4 << 20)
                    {
                        e["strings"] = Strings(data, 120);
                        var g = FbGuess(data);
                        if (g != null) e["flatbuffer"] = g;
                    }
                }
                catch (Exception x) { e["error"] = x.GetType().Name + ": " + x.Message; }
                list.Add(e);
            }
            var root = new JsonObject { ["dir"] = dir, ["files"] = list };
            File.WriteAllText(outPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
            Console.WriteLine($"{files.Count} files inventoried -> {outPath}");
            return 0;
        }

        static string Magic(byte[] d)
        {
            int n = Math.Min(16, d.Length);
            var hex = BitConverter.ToString(d, 0, n).Replace("-", " ");
            var ascii = new string(d.Take(n).Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray());
            return hex + "  " + ascii;
        }

        // ---- GFBMDL ----
        static JsonObject Model(byte[] data, string rel)
        {
            var root = FbTable.Root(data);
            var o = new JsonObject { ["version"] = root.U32(0) };
            int bp = root.InlineStruct(1);
            if (bp != 0) o["bounds"] = Floats(data, bp, 6);
            else if (!root.Table(1).IsNull) o["boundsTable"] = Guess(data, root.Table(1), 2);
            o["textureNames"] = Strs(root.Vector(2));
            o["shaderNames"] = Strs(root.Vector(3));
            o["materialNames"] = Strs(root.Vector(5));

            var bones = root.Vector(9);
            var local = new Matrix4x4[bones.Length];
            var world = new Matrix4x4[bones.Length];
            var parents = new int[bones.Length];
            var bl = new JsonArray();
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones.Table(i);
                int parent = b.I32(2, 0);
                if (parent == i || parent >= bones.Length || parent < 0) parent = -1;
                parents[i] = parent;
                var s = V3(b, 5, Vector3.One); var r = V3(b, 6, Vector3.Zero); var t = V3(b, 7, Vector3.Zero);
                var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, r.Z) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Y) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, r.X);
                local[i] = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(t);
                world[i] = parent >= 0 && parent < i ? local[i] * world[parent] : local[i];
                var w = world[i].Translation;
                var forward = Vector3.TransformNormal(Vector3.UnitZ, world[i]);
                bl.Add(new JsonObject
                {
                    ["i"] = i, ["name"] = b.String(0), ["type"] = b.U32(1), ["parent"] = b.I32(2, 0), ["f3"] = b.Has(3) ? (JsonNode)b.U32(3) : null,
                    ["visible"] = b.Has(4) ? (JsonNode)b.Bool(4) : null,
                    ["s"] = A(s), ["r"] = A(r), ["t"] = A(t), ["world"] = A(w), ["worldZ"] = A(forward),
                    ["rigidCheck"] = b.Has(10), ["fields"] = FieldList(b),
                });
            }
            o["bones"] = bl;

            var mats = root.Vector(6);
            var ml = new JsonArray();
            var shaders = Strs(root.Vector(3));
            var texNames = Strs(root.Vector(2));
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats.Table(i);
                var mo = new JsonObject
                {
                    ["name"] = m.String(0), ["shaderGroup"] = m.String(1), ["renderLayer"] = m.I32(2),
                    ["shader"] = m.I32(8) >= 0 && m.I32(8) < shaders.Count ? shaders[m.I32(8)]?.ToString() : null,
                    ["fields"] = FieldList(m),
                };
                var maps = new JsonArray();
                var mv = m.Vector(11);
                for (int k = 0; k < mv.Length; k++)
                {
                    var tm = mv.Table(k);
                    int ti = tm.I32(1);
                    var prm = tm.Table(2);
                    maps.Add(new JsonObject
                    {
                        ["sampler"] = tm.String(0), ["texture"] = ti >= 0 && ti < texNames.Count ? texNames[ti]?.ToString() : $"#{ti}",
                        ["params"] = prm.IsNull ? null : Guess(data, prm, 0),
                    });
                }
                mo["maps"] = maps;
                mo["switches"] = Params(m.Vector(12), "bool");
                mo["floats"] = Params(m.Vector(13), "f32");
                mo["colors"] = Colors(m.Vector(14));
                var common = m.Table(20);
                if (!common.IsNull)
                    mo["common"] = new JsonObject { ["switches"] = Params(common.Vector(0), "bool"), ["ints"] = Params(common.Vector(1), "i32"), ["colors"] = Colors(common.Vector(2)) };
                ml.Add(mo);
            }
            o["materials"] = ml;

            // Meshes, in model units, at rest (rigid meshes moved by their group bone).
            var groups = root.Vector(7);
            var meshBone = new Dictionary<int, int>();
            var gl = new JsonArray();
            for (int g = 0; g < groups.Length; g++)
            {
                var gr = groups.Table(g);
                meshBone[(int)gr.U32(1)] = (int)gr.U32(0);
                gl.Add(Guess(data, gr, 0));
            }
            o["groups"] = gl;
            var meshes = root.Vector(8);
            var mlist = new JsonArray();
            for (int mi = 0; mi < meshes.Length; mi++)
            {
                var mt = meshes.Table(mi);
                meshBone.TryGetValue(mi, out int bone);
                var attrs = mt.Vector(1);
                int stride = 0, posOff = -1, posFmt = 0;
                var layout = new List<string>();
                for (int a = 0; a < attrs.Length; a++)
                {
                    var at = attrs.Table(a);
                    uint type = at.U32(0), fmt = at.U32(1), count = at.U32(2);
                    if (type == 0) { posOff = stride; posFmt = (int)fmt; }
                    layout.Add($"{type}:{fmt}x{count}");
                    stride += (fmt switch { 0 => 4, 1 => 2, 3 => 1, 5 => 2, 8 => 1, _ => 4 }) * (int)count;
                }
                var raw = mt.Vector(2).Bytes();
                int verts = stride > 0 ? raw.Count / stride : 0;
                var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
                var wmin = min; var wmax = max;
                if (posOff >= 0 && posFmt == 0)
                    for (int v = 0; v < verts; v++)
                    {
                        int p = raw.Offset + v * stride + posOff;
                        var pos = new Vector3(BitConverter.ToSingle(raw.Array, p), BitConverter.ToSingle(raw.Array, p + 4), BitConverter.ToSingle(raw.Array, p + 8));
                        min = Vector3.Min(min, pos); max = Vector3.Max(max, pos);
                        var wp = bone >= 0 && bone < world.Length ? Vector3.Transform(pos, world[bone]) : pos;
                        wmin = Vector3.Min(wmin, wp); wmax = Vector3.Max(wmax, wp);
                    }
                var polys = mt.Vector(0);
                var pl = new JsonArray();
                int tris = 0;
                for (int p = 0; p < polys.Length; p++)
                {
                    var poly = polys.Table(p);
                    int n = poly.Vector(1).Length / 3;
                    tris += n;
                    int mat = (int)poly.U32(0);
                    pl.Add($"{(mat >= 0 && mat < ml.Count ? ml[mat]["name"]?.ToString() : "#" + mat)} {n}");
                }
                mlist.Add(new JsonObject
                {
                    ["i"] = mi, ["bone"] = bone, ["boneName"] = bone >= 0 && bone < bones.Length ? bones.Table(bone).String(0) : null,
                    ["vertices"] = verts, ["stride"] = stride, ["layout"] = string.Join(" ", layout), ["tris"] = tris, ["polys"] = pl,
                    ["min"] = verts > 0 ? A(min) : null, ["max"] = verts > 0 ? A(max) : null,
                    ["worldMin"] = verts > 0 ? A(wmin) : null, ["worldMax"] = verts > 0 ? A(wmax) : null,
                    ["fields"] = FieldList(mt),
                });
            }
            o["meshes"] = mlist;
            o["tris"] = mlist.Sum(m => (int)m["tris"]);
            o["rootFields"] = FieldList(root);
            return o;
        }

        static JsonArray Strs(FbVector v)
        {
            var a = new JsonArray();
            for (int i = 0; i < v.Length; i++) a.Add(v.String(i));
            return a;
        }

        static JsonObject Params(FbVector v, string kind)
        {
            var o = new JsonObject();
            for (int i = 0; i < v.Length; i++)
            {
                var t = v.Table(i);
                string n = t.String(0) ?? $"#{i}";
                o[n] = kind switch { "bool" => (JsonNode)t.Bool(1), "f32" => R(t.F32(1), 5), _ => (JsonNode)t.I32(1) };
            }
            return o;
        }

        static JsonObject Colors(FbVector v)
        {
            var o = new JsonObject();
            for (int i = 0; i < v.Length; i++)
            {
                var t = v.Table(i);
                int p = t.InlineStruct(1);
                o[t.String(0) ?? $"#{i}"] = p == 0 ? null : Floats(t.Buffer, p, 3);
            }
            return o;
        }

        static JsonArray Floats(byte[] b, int p, int n)
        {
            var a = new JsonArray();
            for (int i = 0; i < n; i++) a.Add(R(BitConverter.ToSingle(b, p + 4 * i), 4));
            return a;
        }

        static JsonArray A(Vector3 v) => new JsonArray(R(v.X, 4), R(v.Y, 4), R(v.Z, 4));

        // A rounded number, or null when the bytes are not a finite float.
        static JsonNode R(double v, int digits) => double.IsFinite(v) ? JsonValue.Create(Math.Round(v, digits)) : null;

        static Vector3 V3(FbTable t, int field, Vector3 fallback)
        {
            int p = t.InlineStruct(field);
            if (p == 0) return fallback;
            return new Vector3(BitConverter.ToSingle(t.Buffer, p), BitConverter.ToSingle(t.Buffer, p + 4), BitConverter.ToSingle(t.Buffer, p + 8));
        }

        // Which field slots a table has (to spot data the reader does not know about).
        static string FieldList(FbTable t)
        {
            var have = new List<int>();
            for (int i = 0; i < 40; i++) if (t.Has(i)) have.Add(i);
            return string.Join(",", have);
        }

        // ---- BNTX ----
        static JsonArray Textures(byte[] data, string rel, string pngDir)
        {
            var a = new JsonArray();
            int count = Bntx.Count(data);
            for (int i = 0; i < count; i++)
            {
                string name = Bntx.Name(data, i);
                var o = new JsonObject { ["name"] = name };
                try
                {
                    var t = Bntx.Read(data, i);
                    var px = Bntx.Mapped(t);
                    o["w"] = t.Width; o["h"] = t.Height; o["format"] = $"0x{t.Format:X4}";
                    o["channels"] = string.Join("", t.Channels.Select(c => c.ToString()));
                    double r = 0, g = 0, bl = 0; int amin = 255, amax = 0;
                    int n = t.Width * t.Height;
                    for (int k = 0; k < n; k++)
                    {
                        r += px[k * 4]; g += px[k * 4 + 1]; bl += px[k * 4 + 2];
                        amin = Math.Min(amin, px[k * 4 + 3]); amax = Math.Max(amax, px[k * 4 + 3]);
                    }
                    o["mean"] = new JsonArray(Math.Round(r / n), Math.Round(g / n), Math.Round(bl / n));
                    o["alpha"] = $"{amin}..{amax}";
                    if (pngDir != null)
                    {
                        string p = Path.Combine(pngDir, Path.GetDirectoryName(rel) ?? "", (name ?? $"tex{i}") + ".png");
                        Directory.CreateDirectory(Path.GetDirectoryName(p));
                        File.WriteAllBytes(p, Png.Encode(px, t.Width, t.Height));
                    }
                }
                catch (Exception x) { o["error"] = x.Message; }
                a.Add(o);
            }
            return a;
        }

        static JsonObject DdsTexture(byte[] data, string rel, string pngDir)
        {
            var t = Dds.Read(data);
            var px = t.Rgba;
            double r = 0, g = 0, b = 0; int amin = 255, amax = 0, n = t.Width * t.Height;
            for (int k = 0; k < n; k++)
            {
                r += px[k * 4]; g += px[k * 4 + 1]; b += px[k * 4 + 2];
                amin = Math.Min(amin, px[k * 4 + 3]); amax = Math.Max(amax, px[k * 4 + 3]);
            }
            if (pngDir != null)
            {
                var (small, w, h) = Dds.Shrink(px, t.Width, t.Height, 256);
                string p = Path.Combine(pngDir, Path.ChangeExtension(rel, ".png"));
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllBytes(p, Png.Encode(small, w, h));
            }
            return new JsonObject
            {
                ["w"] = t.Width, ["h"] = t.Height, ["format"] = t.Format, ["srgb"] = t.Srgb, ["mips"] = t.Mips, ["cube"] = t.Cube,
                ["mean"] = new JsonArray(Math.Round(r / n), Math.Round(g / n), Math.Round(b / n)), ["alpha"] = $"{amin}..{amax}",
            };
        }

        // ---- Anything else ----
        static JsonArray Strings(byte[] d, int max)
        {
            var a = new JsonArray();
            var sb = new StringBuilder();
            for (int i = 0; i <= d.Length && a.Count < max; i++)
            {
                byte b = i < d.Length ? d[i] : (byte)0;
                if (b >= 32 && b < 127) sb.Append((char)b);
                else
                {
                    if (sb.Length >= 4) a.Add(sb.ToString());
                    sb.Clear();
                }
            }
            return a;
        }

        // A FlatBuffer read without its schema: each field shown as whatever it plausibly is
        // (a string, a vector, a sub-table, or a scalar as int and float).
        static JsonNode FbGuess(byte[] d)
        {
            if (d.Length < 12) return null;
            int rootPos = BitConverter.ToInt32(d, 0);
            if (rootPos <= 0 || rootPos >= d.Length - 4 || !IsTable(d, rootPos)) return null;
            return Guess(d, new FbTable(d, rootPos), 4);
        }

        static bool IsTable(byte[] d, int pos)
        {
            if (pos < 0 || pos + 4 > d.Length) return false;
            long vt = pos - (long)BitConverter.ToInt32(d, pos);
            if (vt < 0 || vt + 4 > d.Length) return false;
            int vsize = BitConverter.ToUInt16(d, (int)vt), tsize = BitConverter.ToUInt16(d, (int)vt + 2);
            if (vsize < 4 || vsize % 2 != 0 || vsize > 200 || vt + vsize > d.Length || pos + tsize > d.Length) return false;
            for (int e = 4; e < vsize; e += 2) if (BitConverter.ToUInt16(d, (int)vt + e) >= Math.Max(tsize, 4)) return false;
            return true;
        }

        static JsonNode Guess(byte[] d, FbTable t, int depth)
        {
            var o = new JsonObject();
            for (int i = 0; i < 40; i++)
            {
                int p = t.FieldPosition(i);
                if (p == 0) continue;
                o[$"f{i}"] = Field(d, p, depth);
            }
            return o;
        }

        static JsonNode Field(byte[] d, int p, int depth)
        {
            if (p + 4 > d.Length) return p < d.Length ? $"u8 {d[p]}" : null;
            uint u = BitConverter.ToUInt32(d, p);
            float f = BitConverter.ToSingle(d, p);
            long target = p + (long)u;
            var o = new JsonObject { ["u32"] = u, ["f32"] = float.IsFinite(f) ? (JsonNode)Math.Round(f, 5) : null };
            if (u > 0 && u < d.Length && target + 4 <= d.Length)
            {
                int len = BitConverter.ToInt32(d, (int)target);
                // A string: length, bytes, a terminating zero.
                if (len > 0 && len < 512 && target + 4 + len < d.Length && d[target + 4 + len] == 0)
                {
                    var s = Encoding.UTF8.GetString(d, (int)target + 4, len);
                    if (s.All(c => c >= 32 && c < 0x7f || c > 0xa0)) return new JsonObject { ["str"] = s };
                }
                if (depth > 0 && IsTable(d, (int)target))
                    o["table"] = Guess(d, new FbTable(d, (int)target), depth - 1);
                // A vector: length then elements (shown as offsets to tables, or as floats).
                if (len > 0 && len < 100000 && target + 4 + 4L * len <= d.Length)
                {
                    var v = new JsonObject { ["n"] = len };
                    int first = (int)target + 4;
                    long et = first + (long)BitConverter.ToUInt32(d, first);
                    if (depth > 0 && et < d.Length && IsTable(d, (int)et))
                    {
                        var items = new JsonArray();
                        for (int k = 0; k < Math.Min(len, 6); k++)
                        {
                            int ep = first + 4 * k;
                            long tp = ep + (long)BitConverter.ToUInt32(d, ep);
                            items.Add(tp < d.Length && IsTable(d, (int)tp) ? Guess(d, new FbTable(d, (int)tp), depth - 1) : null);
                        }
                        v["tables"] = items;
                    }
                    else
                    {
                        var fl = new JsonArray();
                        for (int k = 0; k < Math.Min(len, 12); k++) fl.Add(R(BitConverter.ToSingle(d, first + 4 * k), 4));
                        v["f32"] = fl;
                    }
                    o["vec"] = v;
                }
            }
            return o;
        }
    }
}
