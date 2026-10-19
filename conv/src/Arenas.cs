using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using PokeModelExporter.Export;
using PokeModelExporter.Formats;
using PokeModelExporter.Model;

namespace Eggy.Conv
{
    //   eggyconv --arenas <background dir> <out dir> <arenas.json>
    // Let's Go battle stages (background/btl_*/btl_*.gfbmdl + .dds) as static arena GLBs, one per arena:
    // its near model (btl_N_*), the far ring it is drawn with (btl_F_*), and the sky dome when the
    // stage is open above. Geometry is moved into place by its bones and written in metres. Materials:
    // the shader's main colour texture at its UV set and transform, times the vertex colours, into
    // which the low-frequency masks/light textures the shaders multiply in (on the second UV set) are
    // baked per vertex. Ground shaders that blend two tiled layers get the second layer as an overlay
    // copy of the mesh whose per-vertex alpha is the blend weight. Unlit by default: these stages are
    // lit by baked colour.
    // Writes <id>.glb (raw) and <id>.json (the report: spots, radius, material plan for the optimiser).
    public static class Arenas
    {
        const float Cm = 0.01f; // the files are in centimetres

        public sealed class Spec
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Kind { get; set; }
            public List<string> Parts { get; set; }
            public bool? Sky { get; set; }     // null: add the sky dome when the stage is open above the centre
            public bool? Lit { get; set; }
            public string Pairing { get; set; } // how the far part was chosen, for the report
        }

        public sealed class ListFile
        {
            public string Release { get; set; }
            public float SinglesHalfGap { get; set; } = 4f;
            public float DoublesHalfWidth { get; set; } = 1.6f;
            public List<Spec> Arenas { get; set; }
        }

        public static int Run(string src, string outDir, string[] rest)
        {
            var list = JsonSerializer.Deserialize<ListFile>(File.ReadAllText(rest[0]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Directory.CreateDirectory(outDir);
            var pretty = new JsonSerializerOptions
            {
                WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            };
            foreach (var spec in list.Arenas)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                JsonObject r;
                try { r = Build(spec, list, src, Path.Combine(outDir, spec.Id + ".glb")); }
                catch (Exception e) { r = new JsonObject { ["id"] = spec.Id, ["error"] = e.ToString() }; }
                r["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
                File.WriteAllText(Path.Combine(outDir, spec.Id + ".json"), r.ToJsonString(pretty));
                Console.WriteLine(r.ContainsKey("error")
                    ? $"{spec.Id}: FAILED {r["error"].ToString().Split('\n')[0]}"
                    : $"{spec.Id}: {r["tris"]} tris, {r["materialCount"]} materials, spots {r["spots"]?["singles"]?.ToJsonString()}, radius {r["radius"]} ({clock.Elapsed.TotalSeconds:F1} s)");
            }
            return 0;
        }

        // ---------------------------------------------------------------- reading a stage model

        sealed class GfMat
        {
            public string Name, Group, Shader;
            public Dictionary<string, (string Tex, uint WrapU, uint WrapV)> Maps = new();
            public Dictionary<string, bool> On = new();
            public Dictionary<string, float> F = new();
            public Dictionary<string, int> I = new();
            public Dictionary<string, Vector3> C = new();
            public float Fl(string k, float d = 0) => F.TryGetValue(k, out var v) ? v : d;
            public int In(string k, int d = 0) => I.TryGetValue(k, out var v) ? v : d;
            public bool Sw(string k) => On.TryGetValue(k, out var v) && v;
        }

        sealed class GfMesh
        {
            public string Name;
            public Vector3[] P, N;
            public Vector2[][] Uv = new Vector2[3][];
            public Vector4[] Col;
            public List<(int Mat, int[] Idx)> Polys = new();
        }

        sealed class Stage
        {
            public string Name;
            public List<GfMat> Mats = new();
            public List<GfMesh> Meshes = new();
        }

        static Stage ReadStage(string file)
        {
            var data = File.ReadAllBytes(file);
            var root = FbTable.Root(data);
            var st = new Stage { Name = Path.GetFileNameWithoutExtension(file) };
            var texNames = new List<string>();
            var tv = root.Vector(2);
            for (int i = 0; i < tv.Length; i++) texNames.Add(tv.String(i));
            var shaders = new List<string>();
            var sv = root.Vector(3);
            for (int i = 0; i < sv.Length; i++) shaders.Add(sv.String(i));

            // Bones, with Maya's segment scale compensation: world = S R (parent S)^-1 T parentWorld.
            var bones = root.Vector(9);
            var world = new Matrix4x4[bones.Length];
            var scale = new Vector3[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones.Table(i);
                int parent = b.I32(2, 0);
                if (parent == i || parent < 0 || parent >= i) parent = -1;
                var s = V3(b, 5, Vector3.One); var e = V3(b, 6, Vector3.Zero); var t = V3(b, 7, Vector3.Zero);
                scale[i] = s;
                var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, e.Z) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, e.Y) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, e.X);
                var m = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q);
                if (parent >= 0)
                {
                    var ps = scale[parent];
                    m *= Matrix4x4.CreateScale(new Vector3(1 / Safe(ps.X), 1 / Safe(ps.Y), 1 / Safe(ps.Z)));
                    world[i] = m * Matrix4x4.CreateTranslation(t) * world[parent];
                }
                else world[i] = m * Matrix4x4.CreateTranslation(t);
            }

            var mats = root.Vector(6);
            for (int i = 0; i < mats.Length; i++)
            {
                var mt = mats.Table(i);
                var g = new GfMat { Name = mt.String(0) ?? $"material{i}", Group = mt.String(1) };
                int si = mt.I32(8);
                if (si >= 0 && si < shaders.Count) g.Shader = shaders[si];
                var maps = mt.Vector(11);
                for (int k = 0; k < maps.Length; k++)
                {
                    var tm = maps.Table(k);
                    int ti = tm.I32(1);
                    var prm = tm.Table(2);
                    string sampler = tm.String(0);
                    if (sampler != null && ti >= 0 && ti < texNames.Count)
                        g.Maps[sampler] = (texNames[ti], prm.IsNull ? 0u : prm.U32(1), prm.IsNull ? 0u : prm.U32(2));
                }
                ReadParams(mt.Vector(12), mt.Vector(13), mt.Vector(14), g, true);
                var common = mt.Table(20);
                if (!common.IsNull) ReadParams(common.Vector(0), common.Vector(1), common.Vector(2), g, false);
                st.Mats.Add(g);
            }

            var groups = root.Vector(7);
            var meshBone = new Dictionary<int, int>();
            for (int gi = 0; gi < groups.Length; gi++) meshBone[(int)groups.Table(gi).U32(1)] = (int)groups.Table(gi).U32(0);
            var meshes = root.Vector(8);
            for (int mi = 0; mi < meshes.Length; mi++)
            {
                var mt = meshes.Table(mi);
                int bone = meshBone.TryGetValue(mi, out var bi) ? bi : -1;
                var xf = bone >= 0 && bone < world.Length ? world[bone] : Matrix4x4.Identity;
                var gm = new GfMesh { Name = bone >= 0 && bone < bones.Length ? bones.Table(bone).String(0) : $"mesh{mi}" };
                var attrs = mt.Vector(1);
                var layout = new List<(int Type, uint Fmt, int Count, int Off)>();
                int stride = 0;
                for (int a = 0; a < attrs.Length; a++)
                {
                    var at = attrs.Table(a);
                    int type = (int)at.U32(0); uint fmt = at.U32(1); int count = (int)at.U32(2);
                    layout.Add((type, fmt, count, stride));
                    stride += FormatSize(fmt) * count;
                }
                var raw = mt.Vector(2).Bytes();
                int n = stride > 0 ? raw.Count / stride : 0;
                foreach (var (type, fmt, count, off) in layout)
                {
                    var vals = new Vector4[n];
                    for (int v = 0; v < n; v++) vals[v] = Decode(raw.Array, raw.Offset + v * stride + off, fmt, count);
                    switch (type)
                    {
                        case 0: gm.P = vals.Select(x => Vector3.Transform(new Vector3(x.X, x.Y, x.Z), xf) * Cm).ToArray(); break;
                        case 1: gm.N = vals.Select(x => Norm(Vector3.TransformNormal(new Vector3(x.X, x.Y, x.Z), xf))).ToArray(); break;
                        case 3: case 4: case 5: gm.Uv[type - 3] = vals.Select(x => new Vector2(x.X, x.Y)).ToArray(); break;
                        case 7: gm.Col = vals.Select(x => fmt == 3 ? x / 255f : x).ToArray(); break;
                    }
                }
                if (gm.P == null) continue;
                var polys = mt.Vector(0);
                for (int p = 0; p < polys.Length; p++)
                {
                    var poly = polys.Table(p);
                    var faces = poly.Vector(1);
                    var idx = new int[faces.Length - faces.Length % 3];
                    for (int k = 0; k < idx.Length; k++) idx[k] = faces.U16(k);
                    gm.Polys.Add(((int)poly.U32(0), idx));
                }
                st.Meshes.Add(gm);
            }
            return st;
        }

        static float Safe(float s) => Math.Abs(s) < 1e-6f ? 1 : s;
        static Vector3 Norm(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitY;

        static void ReadParams(FbVector sw, FbVector vals, FbVector cols, GfMat m, bool floats)
        {
            for (int i = 0; i < sw.Length; i++) { var t = sw.Table(i); var n = t.String(0); if (n != null) m.On[n] = t.Bool(1); }
            for (int i = 0; i < vals.Length; i++)
            {
                var t = vals.Table(i); var n = t.String(0);
                if (n == null) continue;
                if (floats) m.F[n] = t.F32(1); else m.I[n] = t.I32(1);
            }
            for (int i = 0; i < cols.Length; i++)
            {
                var t = cols.Table(i); var n = t.String(0); int p = t.InlineStruct(1);
                if (n != null && p != 0) m.C[n] = new Vector3(BitConverter.ToSingle(t.Buffer, p), BitConverter.ToSingle(t.Buffer, p + 4), BitConverter.ToSingle(t.Buffer, p + 8));
            }
        }

        static int FormatSize(uint f) => f switch { 0 => 4, 1 => 2, 3 => 1, 5 => 2, 8 => 1, _ => throw new NotSupportedException($"GFBMDL buffer format {f}") };

        static Vector4 Decode(byte[] b, int p, uint fmt, int count)
        {
            float C(int i)
            {
                if (i >= count) return i == 3 ? (fmt == 3 ? 255 : 1) : 0;
                return fmt switch
                {
                    0 => BitConverter.ToSingle(b, p + 4 * i),
                    1 => (float)BitConverter.ToHalf(b, p + 2 * i),
                    3 => b[p + i],
                    5 => BitConverter.ToUInt16(b, p + 2 * i),
                    8 => b[p + i] / 255f,
                    _ => 0,
                };
            }
            return new Vector4(C(0), C(1), C(2), C(3));
        }

        static Vector3 V3(FbTable t, int field, Vector3 fallback)
        {
            int p = t.InlineStruct(field);
            if (p == 0) return fallback;
            return new Vector3(BitConverter.ToSingle(t.Buffer, p), BitConverter.ToSingle(t.Buffer, p + 4), BitConverter.ToSingle(t.Buffer, p + 8));
        }

        // ---------------------------------------------------------------- textures

        sealed class Tex
        {
            public string Name;
            public int W, H;
            public byte[] Px;
            public bool HasAlpha;
        }

        sealed class TexStore
        {
            readonly string _dir;
            readonly Dictionary<string, Tex> _cache = new();
            public TexStore(string dir) { _dir = dir; }
            public Tex Get(string name)
            {
                if (name == null) return null;
                if (_cache.TryGetValue(name, out var t)) return t;
                string p = Path.Combine(_dir, name + ".dds");
                if (!File.Exists(p)) return _cache[name] = null;
                var img = Dds.Read(File.ReadAllBytes(p));
                t = new Tex { Name = name, W = img.Width, H = img.Height, Px = img.Rgba };
                for (int i = 3; i < t.Px.Length; i += 4) if (t.Px[i] < 250) { t.HasAlpha = true; break; }
                return _cache[name] = t;
            }
        }

        // Bilinear sample at GF uv (v from the bottom), wrap 0 repeat / 1 clamp / 2 mirror.
        static Vector4 Sample(Tex t, Vector2 uv, uint wrapU = 0, uint wrapV = 0)
        {
            float x = Wrap(uv.X, wrapU) * t.W - 0.5f, y = (1 - Wrap(uv.Y, wrapV)) * t.H - 0.5f;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = x - x0, fy = y - y0;
            Vector4 P(int xx, int yy)
            {
                xx = Math.Clamp(((xx % t.W) + t.W) % t.W, 0, t.W - 1);
                yy = Math.Clamp(((yy % t.H) + t.H) % t.H, 0, t.H - 1);
                int o = (yy * t.W + xx) * 4;
                return new Vector4(t.Px[o], t.Px[o + 1], t.Px[o + 2], t.Px[o + 3]) / 255f;
            }
            return Vector4.Lerp(Vector4.Lerp(P(x0, y0), P(x0 + 1, y0), fx), Vector4.Lerp(P(x0, y0 + 1), P(x0 + 1, y0 + 1), fx), fy);
        }

        static float Wrap(float v, uint mode)
        {
            switch (mode)
            {
                case 1: return Math.Clamp(v, 0, 1);
                case 2: { float f = v - 2 * MathF.Floor(v / 2); return f > 1 ? 2 - f : f; }
                default: return v - MathF.Floor(v);
            }
        }

        // ---------------------------------------------------------------- what each shader draws

        // A texture slot as glTF will draw it: which texture, from which UV set, under which transform.
        sealed class Slot
        {
            public string Sampler, Tex;
            public int Set;
            public Vector2 Scale = Vector2.One, Translate;
            public float Rotate;
            public uint WrapU, WrapV;
        }

        sealed class Look
        {
            public Slot Base;
            public Slot Overlay;          // a second tiled layer blended over Base (overlay copy of the mesh)
            public Slot Product;          // a second texture on Base's UV set, multiplied into Base's image
            public Func<GfMesh, int, float> OverlayWeight;
            public List<(Slot Slot, string Mode)> Baked = new(); // textures folded into the vertex colours
            public Vector4 Factor = Vector4.One;
            public bool UseVertexColor = true, Drop, DoubleSided, AlphaOnly;
            public string Alpha = "OPAQUE";
            public float Cutoff = 0.5f;
            public string Note;
        }

        static Slot MakeSlot(GfMat m, string sampler, int set, string xf)
        {
            if (!m.Maps.TryGetValue(sampler, out var map)) return null;
            var s = new Slot { Sampler = sampler, Tex = map.Tex, Set = Math.Clamp(set, 0, 2), WrapU = map.WrapU, WrapV = map.WrapV };
            if (xf != null)
            {
                s.Scale = new Vector2(m.Fl(xf + "_Scale_U", 1), m.Fl(xf + "_Scale_V", 1));
                s.Translate = new Vector2(m.Fl(xf + "_Translate_U"), m.Fl(xf + "_Translate_V"));
                s.Rotate = m.Fl(xf + "_Rotate");
            }
            return s;
        }

        static Look Decide(GfMat m, TexStore store)
        {
            var k = new Look();
            string g = m.Group ?? "";
            int cull = m.In("CullMode");
            k.DoubleSided = cull == 2;
            if (g == "FieldShadowOnlyShader") { k.Drop = true; k.Note = "shadow caster only"; return k; }

            if (g == "FieldTreeShader04")
            {
                // Leaf cards: Texture01 is the cut-out (black, its alpha is the shape); the colour is the
                // Texture02 gradient on the second UV set, sampled per vertex.
                k.Base = MakeSlot(m, "Texture01", m.In("UV_tex01"), null);
                k.AlphaOnly = true;
                k.UseVertexColor = false; // these cards' vertex colours are not colour (near black)
                k.Baked.Add((MakeSlot(m, "Texture02", m.In("UVSet01", 1), null), "multiply"));
            }
            else if (g == "FieldDefaultShader")
            {
                // TexBlendType 0: Texture01 alone; 1: Texture01 x Texture02; 2: Texture02 multiplied in.
                // TexAlpha 2: the alpha comes from Texture02 (cliff tops and grass edges fade out with it).
                var t1 = MakeSlot(m, "Texture01", m.In("Tex01_UV"), "Tex01");
                var t2 = MakeSlot(m, "Texture02", m.In("Tex02_UV"), "Tex02");
                int blend = m.In("TexBlendType");
                k.Base = t1;
                if (t1 != null && t2 != null)
                {
                    if (blend == 1)
                    {
                        if (t2.Set == t1.Set && t2.Scale == t1.Scale && t2.Translate == t1.Translate) k.Product = t2;
                        else if (t1.Set != 0 && t2.Set == 0)
                        {
                            // The colour is the tiled Texture02; Texture01 on the second UV set is a height
                            // gradient (sea rocks) whose role is unclear: multiplied in it turns the rocks
                            // black, so it is left out and the rocks keep their texture's own transparency.
                            k.Base = t2;
                            if (store.Get(t2.Tex)?.HasAlpha == true) k.Alpha = "BLEND";
                            k.Note = $"Texture01 ({t1.Tex}, a mask on UV set {t1.Set}) left out";
                        }
                        else k.Baked.Add((t2, "multiply"));
                    }
                    else if (blend == 2) k.Baked.Add((t2, "multiply"));
                    if (m.In("TexAlpha") == 2) { k.Baked.Add((t2, "alpha")); if (m.In("BlendMode") == 5) k.Alpha = "BLEND"; }
                }
            }
            else if (g.StartsWith("FieldObjectShader") || g == "FieldWaterShader02" || g.StartsWith("FieldTreeShader") || g == "FieldGrassShader04")
            {
                int set = m.I.ContainsKey("Tex01_UV") ? m.In("Tex01_UV") : m.In("UV_tex01");
                k.Base = MakeSlot(m, "Texture01", set, "Tex01");
                // The second texture on its own UV set: masks multiply, light maps add, decals lay over.
                if (m.Maps.ContainsKey("Texture02") && (m.Sw("TextureBlend") || g == "FieldDefaultShader"))
                {
                    int blend = m.I.ContainsKey("TextureBlendType") ? m.In("TextureBlendType") : m.In("TexBlendType");
                    var t2 = MakeSlot(m, "Texture02", m.In("Tex02_UV", 1), "Tex02");
                    k.Baked.Add((t2, blend switch { 2 => "multiply", 1 => "add", _ => "over" }));
                }
                if (m.Maps.ContainsKey("LightMask_uv1")) k.Baked.Add((MakeSlot(m, "LightMask_uv1", 1, null), "multiply"));
            }
            else if (g.StartsWith("FieldGrassShader") || g == "FieldWindowShader")
            {
                int set = m.I.ContainsKey("UVSet0") ? m.In("UVSet0") : m.I.ContainsKey("UV_tex0") ? m.In("UV_tex0") : m.In("UV_tex03");
                k.Base = MakeSlot(m, "TextureMap01", set, null);
            }
            else if (g == "FieldBattleShader01")
            {
                k.Base = MakeSlot(m, "Ground00Tex01", m.In("Ground00UVset"), null);
                k.Overlay = MakeSlot(m, "Ground01Tex01", m.In("Ground01UVset"), null);
                k.OverlayWeight = (mesh, v) => mesh.Col != null ? mesh.Col[v].W : 0;
            }
            else if (g.StartsWith("FieldCliffShader"))
            {
                // Ground on the tops, cliff on the faces: the vertex alpha is always 1 here, so the slope
                // decides (the game blends the border with BorderTex/BlendTex noise).
                k.Base = MakeSlot(m, "CliffTex01", m.In("Tex00_UV"), "Tex00");
                k.Overlay = MakeSlot(m, "GroundTex01", m.In("Tex01_UV", 1), "Tex01");
                k.OverlayWeight = (mesh, v) => mesh.N != null ? Smooth(0.55f, 0.8f, mesh.N[v].Y) : 1;
            }
            else if (g.StartsWith("FieldRockShader"))
            {
                k.Base = MakeSlot(m, "Rock_tex", m.In("Tex00_UV"), "Tex00");
                k.Overlay = MakeSlot(m, "GroundTex01", m.In("Tex01_UV", 1), "Tex01");
                k.OverlayWeight = (mesh, v) => (mesh.N != null ? Smooth(0.55f, 0.8f, mesh.N[v].Y) : 1) * (mesh.Col != null ? mesh.Col[v].W : 1);
            }
            else if (g == "FieldGroundShader01")
            {
                k.Base = MakeSlot(m, "GroundTex01", m.In("UV_tex0"), null);
                k.Overlay = MakeSlot(m, "GrassTex01", m.In("UV_tex0"), null);
                k.OverlayWeight = (mesh, v) => mesh.Col != null ? mesh.Col[v].W : 0;
            }
            else if (g == "FieldWaterShader01")
            {
                var c = m.C.TryGetValue("Base_Color1", out var c1) ? c1 : new Vector3(0.2f, 0.45f, 0.6f);
                k.Factor = new Vector4(c, 0.85f);
                k.Alpha = "BLEND";
                k.UseVertexColor = false;
                k.DoubleSided = true;
                k.Note = "water: flat Base_Color1, translucent";
                return k;
            }
            else { k.Base = MakeSlot(m, "Texture01", 0, "Tex01"); k.Note = "unknown shader group, drawn with Texture01"; }

            var bt = k.Base != null ? store.Get(k.Base.Tex) : null;
            if (bt == null) { k.Drop = true; k.Note = $"no colour texture ({k.Base?.Tex})"; return k; }
            if (k.Overlay != null && store.Get(k.Overlay.Tex) == null) k.Overlay = null;
            if (k.Product != null && store.Get(k.Product.Tex) == null) k.Product = null;
            k.Baked.RemoveAll(b => b.Slot == null || store.Get(b.Slot.Tex) == null);
            if (m.Sw("DiscardEnable")) { k.Alpha = "MASK"; float d = m.Fl("DiscardValuie"); k.Cutoff = d > 0.01f && d < 0.99f ? d : 0.5f; }
            else if (k.Alpha != "BLEND" && bt.HasAlpha && (m.In("BlendMode") == 5 || g == "FieldWaterShader02")) k.Alpha = "BLEND";
            return k;
        }

        // ---------------------------------------------------------------- building an arena

        sealed class Built
        {
            public ModelData Model = new ModelData();
            public Dictionary<string, byte[]> Textures = new();
            public Dictionary<string, MaterialMapping> Maps = new();
            public JsonArray Plan = new();
            public List<(Vector3 A, Vector3 B, Vector3 C, int Kind, string Part)> Tris = new();
        }

        static JsonObject Build(Spec spec, ListFile list, string src, string outPath)
        {
            var r = new JsonObject { ["id"] = spec.Id, ["name"] = spec.Name, ["kind"] = spec.Kind, ["parts"] = new JsonArray(spec.Parts.Select(p => (JsonNode)p).ToArray()) };
            if (spec.Pairing != null) r["pairing"] = spec.Pairing;
            bool unlit = !(spec.Lit ?? false);
            var b = new Built { Model = new ModelData { Name = spec.Id } };
            var notes = new JsonArray();
            var parts = new List<string>(spec.Parts);
            var built = new HashSet<string>();
            var vstats = new JsonObject();

            void AddPart(string part)
            {
                string dir = Path.Combine(src, part);
                var stage = ReadStage(Path.Combine(dir, part + ".gfbmdl"));
                var store = new TexStore(dir);
                var looks = stage.Mats.Select(m => Decide(m, store)).ToList();
                bool near = part.StartsWith("btl_N_");
                for (int mi = 0; mi < stage.Mats.Count; mi++)
                {
                    var m = stage.Mats[mi]; var k = looks[mi];
                    if (k.Drop) { notes.Add($"{part}/{m.Name} ({m.Group}) left out: {k.Note}"); continue; }
                    if (k.Note != null) notes.Add($"{part}/{m.Name} ({m.Group}): {k.Note}");
                }
                foreach (var mesh in stage.Meshes)
                {
                    foreach (var (matIndex, idx) in mesh.Polys)
                    {
                        if (matIndex < 0 || matIndex >= stage.Mats.Count || idx.Length == 0) continue;
                        var m = stage.Mats[matIndex]; var k = looks[matIndex];
                        if (k.Drop) continue;
                        string matName = $"{part.Substring(4)}:{m.Name}";
                        if (k.Overlay == null) { Emit(b, store, mesh, idx, m, k, matName, false, false, part, near, vstats); continue; }
                        // Two-layer ground: triangles all of the second layer are drawn with it, opaque; triangles
                        // without it keep the first; only the transitions get a blended copy (so few transparent
                        // sheets are left to sort against water and fog).
                        var full = new List<int>(); var none = new List<int>(); var mixed = new List<int>();
                        for (int t = 0; t + 2 < idx.Length; t += 3)
                        {
                            float w0 = W(k, mesh, idx[t]), w1 = W(k, mesh, idx[t + 1]), w2 = W(k, mesh, idx[t + 2]);
                            var list = Math.Min(w0, Math.Min(w1, w2)) >= 0.98f ? full : Math.Max(w0, Math.Max(w1, w2)) <= 0.02f ? none : mixed;
                            list.Add(idx[t]); list.Add(idx[t + 1]); list.Add(idx[t + 2]);
                        }
                        var below = none.Concat(mixed).ToArray();
                        if (below.Length > 0) Emit(b, store, mesh, below, m, k, matName, false, false, part, near, vstats);
                        if (full.Count > 0) Emit(b, store, mesh, full.ToArray(), m, k, matName + "+ground", false, true, part, near, vstats);
                        if (mixed.Count > 0) Emit(b, store, mesh, mixed.ToArray(), m, k, matName + "+layer", true, false, part, near, vstats);
                    }
                }
                built.Add(part);
            }

            foreach (var part in parts) AddPart(part);

            // The sky dome, when nothing covers the stage's centre.
            bool open = !b.Tris.Any(t => t.Kind == 2 && Covers(t, 0, 0, 2.5f));
            bool sky = spec.Sky ?? false;
            r["openAbove"] = open;
            if (sky && !built.Contains("btl_sky")) { AddPart("btl_sky"); parts.Add("btl_sky"); }
            r["parts"] = new JsonArray(parts.Select(p => (JsonNode)p).ToArray());

            // ---- Spots and the floor ----
            var grid = new HeightGrid(b.Tris.Where(t => t.Part.StartsWith("btl_N_")).ToList());
            float h0 = grid.FloorAt(0, 0, 0) ?? 0;
            float d = list.SinglesHalfGap, w = list.DoublesHalfWidth;
            var spotNotes = new JsonArray();
            // Pull the spots in until both sides stand on clear floor at the centre's height.
            for (; d > 1.5f; d -= 0.5f)
                if (Clear(grid, 0, -d, h0) && Clear(grid, 0, d, h0)) break;
            if (d < list.SinglesHalfGap) spotNotes.Add($"singles pulled in to ±{d} m to stay on clear floor");
            float H(float x, float z) => grid.FloorAt(x, z, h0) ?? h0;
            JsonArray P(float x, float z) => new JsonArray(R(x), R(H(x, z) - h0), R(z));
            float dw = w;
            for (; dw > 0.6f; dw -= 0.2f)
                if (Clear(grid, -dw, -d, h0) && Clear(grid, dw, -d, h0) && Clear(grid, -dw, d, h0) && Clear(grid, dw, d, h0)) break;
            if (dw < w) spotNotes.Add($"doubles narrowed to ±{dw:F1} m");
            r["spots"] = new JsonObject
            {
                ["singles"] = new JsonObject { ["near"] = P(0, -d), ["far"] = P(0, d) },
                ["doubles"] = new JsonObject
                {
                    ["near"] = new JsonArray(P(-dw, -d), P(dw, -d)),
                    ["far"] = new JsonArray(P(-dw, d), P(dw, d)),
                },
            };
            r["spotNotes"] = spotNotes;
            r["floorHeight"] = R(h0);
            // The playable floor: from the centre, how far each of 32 directions stays on floor at the
            // centre's height without running into anything; the radius is the 25th percentile.
            var reach = new List<float>();
            for (int i = 0; i < 32; i++)
            {
                float a = i * MathF.PI / 16, dist = 0;
                for (float s = 0.25f; s < 60; s += 0.25f)
                {
                    float x = MathF.Sin(a) * s, z = MathF.Cos(a) * s;
                    var f = grid.FloorAt(x, z, h0);
                    if (f == null || Math.Abs(f.Value - h0) > 0.4f || grid.Blocked(x, z, f.Value)) break;
                    dist = s;
                }
                reach.Add(dist);
            }
            var sorted = reach.OrderBy(x => x).ToList();
            r["radius"] = R(sorted[sorted.Count / 4]);
            r["reach"] = new JsonArray(reach.Select(x => (JsonNode)R(x)).ToArray());

            // Shift so the floor at the centre is y = 0.
            if (Math.Abs(h0) > 1e-4f)
                foreach (var mesh in b.Model.Meshes) for (int i = 0; i < mesh.Positions.Length; i++) mesh.Positions[i].Y -= h0;

            // ---- The GLB ----
            foreach (var m in b.Model.Materials) b.Maps[m.Name].Unlit = unlit;
            var glb = GlbWriter.Write(b.Model, b.Textures, m => b.Maps[m.Name], null);
            File.WriteAllBytes(outPath, glb);
            r["lit"] = !unlit;
            r["tris"] = b.Model.Meshes.Sum(m => m.SubMeshes.Sum(s => s.Indices.Length / 3));
            r["materialCount"] = b.Model.Materials.Count;
            r["textureCount"] = b.Textures.Count;
            r["rawBytes"] = glb.Length;
            var all = b.Model.Meshes.SelectMany(m => m.Positions).ToList();
            r["bounds"] = new JsonObject
            {
                ["min"] = new JsonArray(R(all.Min(p => p.X)), R(all.Min(p => p.Y)), R(all.Min(p => p.Z))),
                ["max"] = new JsonArray(R(all.Max(p => p.X)), R(all.Max(p => p.Y)), R(all.Max(p => p.Z))),
            };
            r["materials"] = b.Plan;
            r["vertexColors"] = vstats;
            r["notes"] = notes;
            return r;
        }

        static float R(float v) => MathF.Round(v, 3);

        // One submesh (a GF mesh's polygons of one material) as its own glTF mesh, compacted, with its
        // texture coordinates already in glTF's space (transform applied, V from the top).
        static float W(Look k, GfMesh mesh, int v) => v >= 0 && v < mesh.P.Length ? Math.Clamp(k.OverlayWeight(mesh, v), 0, 1) : 0;
        static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

        // overlay: the blended copy of a two-layer ground's transitions; ground: its second layer drawn opaque.
        static void Emit(Built b, TexStore store, GfMesh mesh, int[] idx, GfMat m, Look k, string matName, bool overlay, bool ground, string part, bool near, JsonObject vstats)
        {
            var slot = overlay || ground ? k.Overlay : k.Base;
            var map = new Dictionary<int, int>();
            var order = new List<int>();
            var indices = new int[idx.Length];
            for (int i = 0; i < idx.Length; i++)
            {
                int v = idx[i];
                if (v < 0 || v >= mesh.P.Length) { indices = null; break; }
                if (!map.TryGetValue(v, out int nv)) { nv = map[v] = order.Count; order.Add(v); }
                indices[i] = nv;
            }
            if (indices == null) return;
            int n = order.Count;
            var md = new MeshData { Name = $"{part.Substring(4)}:{mesh.Name}{(overlay ? "+layer" : "")}" };
            md.Positions = order.Select(v => mesh.P[v]).ToArray();
            if (mesh.N != null) md.Normals = order.Select(v => mesh.N[v]).ToArray();
            if (slot != null)
            {
                var uvs = mesh.Uv[slot.Set] ?? mesh.Uv[0];
                if (uvs == null) return;
                md.TexCoords.Add(order.Select(v => ToGl(uvs[v], slot)).ToArray());
            }

            // Vertex colours: the mesh's own, times what the shader multiplies in from other textures.
            var col = new Vector4[n];
            var stats = new float[8];
            for (int i = 0; i < n; i++)
            {
                int v = order[i];
                var c = k.UseVertexColor && mesh.Col != null ? mesh.Col[v] : Vector4.One;
                stats[0] += c.X; stats[1] += c.Y; stats[2] += c.Z; stats[3] += c.W;
                var rgb = new Vector3(c.X, c.Y, c.Z);
                float alpha = overlay ? Math.Clamp(k.OverlayWeight(mesh, v), 0, 1) : 1;
                foreach (var (s, mode) in k.Baked)
                {
                    var uv = mesh.Uv[s.Set] ?? mesh.Uv[0];
                    if (uv == null) continue;
                    var t = Sample(store.Get(s.Tex), Transform(uv[v], s), s.WrapU, s.WrapV);
                    var trgb = new Vector3(t.X, t.Y, t.Z);
                    if (mode == "alpha") { alpha *= t.W; continue; }
                    rgb = mode switch
                    {
                        "multiply" => rgb * trgb,
                        "add" => Vector3.Min(Vector3.One, rgb + trgb),
                        _ => Vector3.Lerp(rgb, trgb, t.W),
                    };
                }
                if (!k.UseVertexColor && k.Baked.Count == 0) rgb = Vector3.One;
                col[i] = new Vector4(rgb, alpha);
            }
            if (!vstats.ContainsKey(matName) && n > 0)
            {
                float mn = float.MaxValue, mx = float.MinValue;
                if (mesh.Col != null) foreach (int v in order) { mn = Math.Min(mn, mesh.Col[v].W); mx = Math.Max(mx, mesh.Col[v].W); }
                vstats[matName] = new JsonObject
                {
                    ["group"] = m.Group,
                    ["meanRGBA"] = new JsonArray(R(stats[0] / n), R(stats[1] / n), R(stats[2] / n), R(stats[3] / n)),
                    ["alphaRange"] = mesh.Col != null ? $"{mn:F2}..{mx:F2}" : "none",
                    ["uvSets"] = string.Join(",", Enumerable.Range(0, 3).Where(s => mesh.Uv[s] != null)),
                };
            }
            if (overlay && col.All(c => c.W < 0.02f)) return; // nothing of the second layer shows here
            md.Colors = col;
            md.SubMeshes.Add(new SubMeshData { Material = matName, Indices = indices });
            b.Model.Meshes.Add(md);

            foreach (int t in Enumerable.Range(0, indices.Length / 3))
            {
                var A = md.Positions[indices[t * 3]]; var B = md.Positions[indices[t * 3 + 1]]; var C = md.Positions[indices[t * 3 + 2]];
                // 2: solid; 1: see-through but walkable (glass tiles); 0: not ground (the second ground layer,
                // fog and wave sheets drawn with FieldWaterShader02).
                int kind = overlay || m.Group == "FieldWaterShader02" ? 0 : k.Alpha != "BLEND" || m.Group == "FieldWaterShader01" ? 2 : 1;
                b.Tris.Add((A, B, C, kind, part));
            }

            if (b.Maps.ContainsKey(matName)) return;
            var md2 = new MaterialData { Name = matName, Shader = m.Group };
            b.Model.Materials.Add(md2);
            var mapping = new MaterialMapping { Roughness = 1, DoubleSided = k.DoubleSided, AlphaBlend = (k.Alpha == "BLEND" && !ground) || overlay };
            if (k.Factor != Vector4.One) mapping.BaseColorFactor = new[] { k.Factor.X, k.Factor.Y, k.Factor.Z, k.Factor.W };
            if (slot != null)
            {
                bool alphaOnly = k.AlphaOnly && !overlay && !ground;
                var product = overlay || ground ? null : k.Product;
                string key = slot.Tex + (alphaOnly ? "_shape" : "") + (product != null ? "_x_" + product.Tex : "") + ".png";
                if (!b.Textures.ContainsKey(key))
                {
                    var tex = store.Get(slot.Tex);
                    var px = tex.Px;
                    if (alphaOnly)
                    {
                        px = (byte[])px.Clone();
                        for (int i = 0; i < px.Length; i += 4) px[i] = px[i + 1] = px[i + 2] = 255;
                    }
                    if (product != null)
                    {
                        // Both on the same UV set under the same transform: one image, their product.
                        var t2 = store.Get(product.Tex);
                        px = (byte[])px.Clone();
                        for (int y = 0; y < tex.H; y++)
                            for (int x = 0; x < tex.W; x++)
                            {
                                var p2 = Sample(t2, new Vector2((x + 0.5f) / tex.W, 1 - (y + 0.5f) / tex.H), product.WrapU, product.WrapV);
                                int o = (y * tex.W + x) * 4;
                                px[o] = (byte)(px[o] * p2.X); px[o + 1] = (byte)(px[o + 1] * p2.Y); px[o + 2] = (byte)(px[o + 2] * p2.Z);
                            }
                    }
                    b.Textures[key] = Png.Encode(px, tex.W, tex.H);
                }
                mapping.BaseColorTexture = key;
            }
            b.Maps[matName] = mapping;
            b.Plan.Add(new JsonObject
            {
                ["name"] = matName,
                ["group"] = m.Group,
                ["shader"] = m.Shader,
                ["base"] = slot?.Tex,
                ["uvSet"] = slot?.Set,
                ["wrap"] = slot != null ? new JsonArray(GlWrap(slot.WrapU), GlWrap(slot.WrapV)) : null,
                ["alpha"] = overlay ? "BLEND" : ground && k.Alpha == "BLEND" ? "OPAQUE" : k.Alpha,
                ["cutoff"] = k.Alpha == "MASK" ? (JsonNode)k.Cutoff : null,
                ["overlay"] = overlay,
                ["ground"] = ground,
                ["baked"] = new JsonArray(k.Baked.Select(x => (JsonNode)$"{x.Mode} {x.Slot.Tex} (uv{x.Slot.Set})").ToArray()),
                ["product"] = overlay || ground ? null : k.Product?.Tex,
                ["doubleSided"] = k.DoubleSided,
                ["rotate"] = slot != null && Math.Abs(slot.Rotate) > 1e-4f ? (JsonNode)slot.Rotate : null,
            });
        }

        // glTF sampler wrap for a GF wrap mode.
        static int GlWrap(uint mode) => mode switch { 1 => 33071, 2 => 33648, _ => 10497 };

        static Vector2 Transform(Vector2 uv, Slot s) => uv * s.Scale + s.Translate;

        // GF samples at uv x scale + translate with V from the bottom; glTF's V runs from the top.
        static Vector2 ToGl(Vector2 uv, Slot s)
        {
            var t = Transform(uv, s);
            return new Vector2(t.X, 1 - t.Y);
        }

        // ---------------------------------------------------------------- floor queries

        static bool Covers((Vector3 A, Vector3 B, Vector3 C, int Kind, string Part) t, float x, float z, float above)
        {
            var y = HeightGrid.YAt(t.A, t.B, t.C, x, z);
            return y != null && y.Value > above;
        }

        static bool Clear(HeightGrid g, float x, float z, float h0)
        {
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI / 4;
                foreach (float rr in new[] { 0f, 0.5f })
                {
                    float px = x + MathF.Sin(a) * rr, pz = z + MathF.Cos(a) * rr;
                    var f = g.FloorAt(px, pz, h0);
                    if (f == null || Math.Abs(f.Value - h0) > 0.4f || g.Blocked(px, pz, f.Value)) return false;
                }
            }
            return true;
        }

        // Vertical queries against the near model's triangles: the floor under a point (the highest
        // walkable surface no more than 1.5 m above the reference height) and whether anything solid
        // stands between 0.3 m and 2 m above it.
        sealed class HeightGrid
        {
            const float Cell = 1f;
            readonly Dictionary<(int, int), List<int>> _cells = new();
            readonly List<(Vector3 A, Vector3 B, Vector3 C, int Kind, string Part)> _t;

            public HeightGrid(List<(Vector3 A, Vector3 B, Vector3 C, int Kind, string Part)> tris)
            {
                _t = tris;
                for (int i = 0; i < tris.Count; i++)
                {
                    var (a, b, c, _, _) = tris[i];
                    int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Cell), x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Cell);
                    int z0 = (int)MathF.Floor(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) / Cell), z1 = (int)MathF.Floor(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) / Cell);
                    if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 40000) continue; // huge backdrop planes
                    for (int x = x0; x <= x1; x++)
                        for (int z = z0; z <= z1; z++)
                        {
                            if (!_cells.TryGetValue((x, z), out var l)) _cells[(x, z)] = l = new List<int>();
                            l.Add(i);
                        }
                }
            }

            IEnumerable<(float Y, int Kind, bool Up)> Hits(float x, float z)
            {
                if (!_cells.TryGetValue(((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell)), out var l)) yield break;
                foreach (int i in l)
                {
                    var t = _t[i];
                    var y = YAt(t.A, t.B, t.C, x, z);
                    if (y == null) continue;
                    var n = Vector3.Cross(t.B - t.A, t.C - t.A);
                    bool up = n.LengthSquared() > 1e-12f && Math.Abs(Vector3.Normalize(n).Y) > 0.6f;
                    yield return (y.Value, t.Kind, up);
                }
            }

            // The floor under a 0.6 m footprint: the highest walkable (solid, facing up) surface at the
            // point and four around it, so gaps between planks or tiles don't drop it to what lies below.
            public float? FloorAt(float x, float z, float reference)
            {
                float? best = null;
                foreach (var (dx, dz) in new[] { (0f, 0f), (0.3f, 0f), (-0.3f, 0f), (0f, 0.3f), (0f, -0.3f) })
                    foreach (var (y, kind, up) in Hits(x + dx, z + dz))
                        if (kind >= 1 && up && y <= reference + 1.5f && (best == null || y > best)) best = y;
                return best;
            }

            public bool Blocked(float x, float z, float floor) =>
                Hits(x, z).Any(h => h.Kind == 2 && h.Up && h.Y > floor + 0.3f && h.Y < floor + 2f);

            public static float? YAt(Vector3 a, Vector3 b, Vector3 c, float x, float z)
            {
                float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                if (Math.Abs(d) < 1e-9f) return null;
                float l1 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d;
                float l2 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
                float l3 = 1 - l1 - l2;
                if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) return null;
                return l1 * a.Y + l2 * b.Y + l3 * c.Y;
            }
        }
    }
}
