using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using PokeModelExporter.Export;
using PokeModelExporter.Formats;
using PokeModelExporter.Model;

namespace Eggy.Conv
{
    // Sword/Shield and Let's Go materials as glTF PBR, baked from what the game's PokeDefaultShader does:
    //   Col0Tex (colour) is sampled at uv x ColorUVScale + ColorUVTranslate with the slot's wrap per axis:
    //   bodies are half textures, scale (2, 1) with U mirrored, unfolded here into the full image;
    //   eyes pick one cell of an expression sheet the same way. LyCol0Tex (an iris or other layer) is
    //   drawn over it at its own Layer1UV transform where its alpha says so.
    //   EmissionMaskTex glows (base colour x mask) only when EmissionMaskUse is on.
    //   AmbientTex and NormalMapTex hold packed shader data, not glTF occlusion or tangent normals, and
    //   are left out (vertex normals shade the model).
    //   FireCore flames glow from BaseColor0 to LayerColor0 by their blend mask; FireMask meshes belong
    //   to a separate effect pass (SkipMainRendering) and are not drawn.
    public static class GfMaterials
    {
        public sealed class Result
        {
            public readonly Dictionary<string, byte[]> Textures = new Dictionary<string, byte[]>();
            public readonly Dictionary<string, MaterialMapping> Mappings = new Dictionary<string, MaterialMapping>();
            public readonly HashSet<string> Hidden = new HashSet<string>(); // materials the game does not draw
        }

        // How a GF shader places a texture: uv x scale + translate + base (form 4 of Transform), worked out on
        // the eye sheets: it is the one form under which both eyes show the same expression cell, mirrored,
        // for Charizard (base -0.5, islands side by side) and Rattata (translate 1, islands at both ends)
        // alike, and it centres the iris layer in Charizard's eyes.
        public const int ColorForm = 4;
        public const int LayerForm = 4;

        sealed class Sampler
        {
            public Bntx.Texture Tex;
            public byte[] Px;
            public uint WrapU, WrapV;
            public Vector2 Scale = Vector2.One, Translate, Base;
        }

        public static Result Bake(GfModel gm, Func<string, byte[]> read, JsonObject report, bool bottomOrigin, string debugDir)
        {
            var result = new Result();
            var decoded = new Dictionary<string, Bntx.Texture>();
            var formats = new JsonObject();
            Bntx.Texture Load(string name)
            {
                if (name == null || name == "dummy_col" || name.StartsWith("projection_effect") || name.StartsWith("viewport_mask")) return null;
                if (decoded.TryGetValue(name, out var t)) return t;
                var bytes = read(name + ".bntx") ?? read(name);
                if (bytes == null) { formats[name] = "missing"; return decoded[name] = null; }
                try
                {
                    t = Bntx.Read(bytes);
                    formats[name] = $"0x{t.Format:X4} {t.Width}x{t.Height}";
                    if (debugDir != null) File.WriteAllBytes(Path.Combine(debugDir, $"tex_{name}.png"), Png.Encode(Bntx.Mapped(t), t.Width, t.Height));
                    return decoded[name] = t;
                }
                catch (Exception e)
                {
                    formats[name] = "unreadable: " + e.Message;
                    return decoded[name] = null;
                }
            }
            Sampler Slot(GfMaterial m, string sampler, string prefix)
            {
                var map = m.Maps.FirstOrDefault(x => x.Sampler == sampler);
                var t = Load(map.Texture);
                if (t == null) return null;
                float V(string k, float d) => m.Values.TryGetValue(k, out var v) ? v : d;
                return new Sampler
                {
                    Tex = t,
                    Px = Bntx.Mapped(t),
                    WrapU = map.WrapU,
                    WrapV = map.WrapV,
                    Scale = prefix == null ? Vector2.One : new Vector2(V(prefix + "UVScaleU", 1), V(prefix + "UVScaleV", 1)),
                    Translate = prefix == null ? Vector2.Zero : new Vector2(V(prefix + "UVTranslateU", 0), V(prefix + "UVTranslateV", 0)),
                    Base = prefix == null ? Vector2.Zero : new Vector2(V(prefix + "BaseU", 0), V(prefix + "BaseV", 0)),
                };
            }

            foreach (var m in gm.Materials)
            {
                bool On(string s) => m.Switches.TryGetValue(s, out var v) && v;
                var map = new MaterialMapping { Roughness = 0.75f, Metallic = 0f };
                string shader = (m.ShaderGroup ?? "") + " " + (m.ShaderName ?? "");
                result.Mappings[m.Name] = map;

                if (On("SkipMainRendering"))
                {
                    result.Hidden.Add(m.Name);
                    continue;
                }
                if (shader.IndexOf("FireCore", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    BakeFire(m, Slot(m, "Blend0Tex", "Blend0"), map, result);
                    continue;
                }

                var col = Slot(m, "Col0Tex", "Color");
                var layer = On("Layer1Enable") ? Slot(m, "LyCol0Tex", "Layer1") : null;
                var emi = On("EmissionMaskUse") ? Slot(m, "EmissionMaskTex", "EmissionMask") : null;
                if (col == null)
                {
                    var c = m.Colors.TryGetValue("ConstantColor0", out var cc) ? cc : Vector3.One;
                    map.BaseColorFactor = new[] { c.X, c.Y, c.Z, 1f };
                    continue;
                }
                // Output size: the colour texture unfolded by its whole-number scale, at most 2048 a side.
                int w = col.Tex.Width * Whole(col.Scale.X), h = col.Tex.Height * Whole(col.Scale.Y);
                float shrink = Math.Min(1f, 2048f / Math.Max(w, h));
                w = Math.Max(1, (int)(w * shrink));
                h = Math.Max(1, (int)(h * shrink));
                var color = new byte[w * h * 4];
                byte[] glow = emi != null ? new byte[w * h * 4] : null;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var uv = new Vector2((x + 0.5f) / w, (y + 0.5f) / h);
                    if (bottomOrigin) uv.Y = 1 - uv.Y; // the game's V runs bottom to top
                    var c = Sample(col, uv, bottomOrigin, ColorForm);
                    if (layer != null)
                    {
                        // The colour texture is drawn over the layer: its transparent holes show the eyeball.
                        var l = Sample(layer, uv, bottomOrigin, LayerForm);
                        c = new Vector4(Vector3.Lerp(new Vector3(l.X, l.Y, l.Z), new Vector3(c.X, c.Y, c.Z), c.W), 1);
                    }
                    int p = (y * w + x) * 4;
                    Put(color, p, c);
                    if (glow != null)
                    {
                        var e = Sample(emi, uv, bottomOrigin, ColorForm);
                        float k = Math.Max(e.X, e.Y);
                        Put(glow, p, new Vector4(c.X * k, c.Y * k, c.Z * k, 1));
                    }
                }
                string key = m.Name + "_color.png";
                result.Textures[key] = Png.Encode(color, w, h);
                map.BaseColorTexture = key;
                if (glow != null)
                {
                    result.Textures[m.Name + "_emission.png"] = Png.Encode(glow, w, h);
                    map.EmissiveTexture = m.Name + "_emission.png";
                }
                if (debugDir != null) DebugSheet(gm, m, col, layer, debugDir);
            }

            report["gfMaterials"] = new JsonArray(gm.Materials.Select(m => (JsonNode)new JsonObject
            {
                ["name"] = m.Name,
                ["shaderGroup"] = m.ShaderGroup,
                ["shader"] = m.ShaderName,
                ["maps"] = new JsonArray(m.Maps.Select(x => (JsonNode)$"{x.Sampler}={x.Texture} wrap {x.WrapU},{x.WrapV}").ToArray()),
                ["switchesOn"] = new JsonArray(m.Switches.Where(kv => kv.Value).Select(kv => (JsonNode)kv.Key).ToArray()),
                ["values"] = new JsonObject(m.Values.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, Math.Round(kv.Value, 4)))),
                ["colors"] = new JsonObject(m.Colors.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key,
                    new JsonArray(Math.Round(kv.Value.X, 4), Math.Round(kv.Value.Y, 4), Math.Round(kv.Value.Z, 4))))),
            }).ToArray());
            report["textureFormats"] = formats;
            return result;
        }

        // A flame: BaseColor0 where its blend mask is dark, LayerColor0 where it is bright; it glows.
        static void BakeFire(GfMaterial m, Sampler blend, MaterialMapping map, Result result)
        {
            var a = m.Colors.TryGetValue("BaseColor0", out var b0) ? b0 : new Vector3(1, 0.2f, 0.05f);
            var b = m.Colors.TryGetValue("LayerColor0", out var l0) ? l0 : new Vector3(1, 0.8f, 0.2f);
            map.DoubleSided = true;
            if (blend == null)
            {
                map.BaseColorFactor = new[] { a.X, a.Y, a.Z, 1f };
                return;
            }
            int w = blend.Tex.Width, h = blend.Tex.Height;
            var rgba = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                float k = blend.Px[i * 4] / 255f;
                Put(rgba, i * 4, new Vector4(Vector3.Lerp(a, b, k), 1));
            }
            string key = m.Name + "_fire.png";
            result.Textures[key] = Png.Encode(rgba, w, h);
            map.BaseColorTexture = key;
            map.EmissiveTexture = key;
        }

        static int Whole(float s) => s >= 1 && s <= 4 && Math.Abs(s - Math.Round(s)) < 1e-4 ? (int)Math.Round(s) : 1;

        // Texture placement forms tried on the eye sheets: 1: uv * scale + translate / scale + base,
        // 2: (uv + base) * scale + translate,  3: uv * scale + translate / scale,  4: uv * scale + translate + base.
        static Vector2 Transform(Sampler s, Vector2 uv, int form) => form switch
        {
            2 => (uv + s.Base) * s.Scale + s.Translate,
            3 => uv * s.Scale + s.Translate / s.Scale,
            4 => uv * s.Scale + s.Translate + s.Base,
            _ => uv * s.Scale + s.Translate / s.Scale + s.Base,
        };

        static Vector4 Sample(Sampler s, Vector2 uv, bool bottomOrigin, int form)
        {
            var t = Transform(s, uv, form);
            float u = Wrap(t.X, s.WrapU), v = Wrap(t.Y, s.WrapV);
            if (bottomOrigin) v = 1 - v;
            int x = Math.Min(s.Tex.Width - 1, Math.Max(0, (int)(u * s.Tex.Width)));
            int y = Math.Min(s.Tex.Height - 1, Math.Max(0, (int)(v * s.Tex.Height)));
            int p = (y * s.Tex.Width + x) * 4;
            return new Vector4(s.Px[p], s.Px[p + 1], s.Px[p + 2], s.Px[p + 3]) / 255f;
        }

        // GF sampler wrap modes: 0 repeat, 1 clamp, 2 mirror.
        static float Wrap(float c, uint mode)
        {
            switch (mode)
            {
                case 1: return Math.Min(1f, Math.Max(0f, c));
                case 2:
                    float f = c - 2 * (float)Math.Floor(c / 2);
                    return f > 1 ? 2 - f : f;
                default: return c - (float)Math.Floor(c);
            }
        }

        static void Put(byte[] dst, int p, Vector4 c)
        {
            dst[p] = B(c.X); dst[p + 1] = B(c.Y); dst[p + 2] = B(c.Z); dst[p + 3] = B(c.W);
        }

        static byte B(float v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v * 255)));

        // A sheet to check how the iris layer is placed: colour in ColorForm, the layer in forms 1-4
        // (columns): row 1 close on the material's UV island, row 2 the whole 0..1 square with the island in red.
        static void DebugSheet(GfModel gm, GfMaterial m, Sampler col, Sampler layer, string dir)
        {
            const int T = 384;
            int W = T * 4;
            var sheet = new byte[W * T * 2 * 4];
            // The island: this material's triangles, each moved by whole tiles into 0..1.
            var tris = new List<(Vector2, Vector2, Vector2)>();
            foreach (var mesh in gm.Model.Meshes)
            {
                if (mesh.TexCoords.Count == 0 || mesh.TexCoords[0] == null) continue;
                var uvs = mesh.TexCoords[0];
                foreach (var sub in mesh.SubMeshes.Where(x => x.Material == m.Name))
                    for (int i = 0; i + 2 < sub.Indices.Length; i += 3)
                    {
                        Vector2 a = uvs[sub.Indices[i]], b = uvs[sub.Indices[i + 1]], c = uvs[sub.Indices[i + 2]];
                        var shift = new Vector2((float)Math.Floor((a.X + b.X + c.X) / 3), (float)Math.Floor((a.Y + b.Y + c.Y) / 3));
                        tris.Add((a - shift, b - shift, c - shift));
                    }
            }
            var lo = new Vector2(float.MaxValue);
            var hi = new Vector2(float.MinValue);
            foreach (var (a, b, c) in tris)
            {
                lo = Vector2.Min(lo, Vector2.Min(a, Vector2.Min(b, c)));
                hi = Vector2.Max(hi, Vector2.Max(a, Vector2.Max(b, c)));
            }
            if (tris.Count == 0) { lo = Vector2.Zero; hi = Vector2.One; }
            var pad = (hi - lo) * 0.08f;
            lo -= pad;
            hi += pad;
            float side = Math.Max(hi.X - lo.X, hi.Y - lo.Y);
            for (int row = 0; row < 2; row++)
            for (int form = 1; form <= 4; form++)
            {
                int ox = (form - 1) * T, oy = row * T;
                for (int y = 0; y < T; y++)
                for (int x = 0; x < T; x++)
                {
                    // game UV (V from the bottom) under this pixel
                    var uv = row == 0
                        ? new Vector2(lo.X + (x + 0.5f) / T * side, lo.Y + side - (y + 0.5f) / T * side)
                        : new Vector2((x + 0.5f) / T, 1 - (y + 0.5f) / T);
                    var c = Sample(col, uv, true, ColorForm);
                    if (layer != null)
                    {
                        var l = Sample(layer, uv, true, form);
                        c = new Vector4(Vector3.Lerp(new Vector3(l.X, l.Y, l.Z), new Vector3(c.X, c.Y, c.Z), c.W), 1);
                    }
                    c.W = 1;
                    Put(sheet, ((oy + y) * W + ox + x) * 4, c);
                }
                if (row == 0) continue;
                foreach (var (a, b, c) in tris)
                    foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                        Line(sheet, W, ox, oy, T, p.X * T, (1 - p.Y) * T, q.X * T, (1 - q.Y) * T);
            }
            File.WriteAllBytes(Path.Combine(dir, $"sheet_{gm.Model.Name}_{m.Name}.png"), Png.Encode(sheet, W, T * 2));
        }

        static void Blit(byte[] dst, int W, int ox, int oy, int T, Sampler s)
        {
            for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                int sx = x * s.Tex.Width / T, sy = y * s.Tex.Height / T;
                int p = (sy * s.Tex.Width + sx) * 4;
                float a = s.Px[p + 3] / 255f;
                bool check = ((x / 16) + (y / 16)) % 2 == 0;
                float bg = check ? 0.8f : 0.6f;
                var c = new Vector4(s.Px[p] / 255f * a + bg * (1 - a), s.Px[p + 1] / 255f * a + bg * (1 - a), s.Px[p + 2] / 255f * a + bg * (1 - a), 1);
                Put(dst, ((oy + y) * W + ox + x) * 4, c);
            }
        }

        static void Line(byte[] img, int W, int ox, int oy, int T, float x0, float y0, float x1, float y1)
        {
            int steps = (int)Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) + 1;
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                int x = (int)(x0 + (x1 - x0) * t), y = (int)(y0 + (y1 - y0) * t);
                if (x < 0 || y < 0 || x >= T || y >= T) continue;
                int p = ((oy + y) * W + ox + x) * 4;
                img[p] = 255; img[p + 1] = 0; img[p + 2] = 0; img[p + 3] = 255;
            }
        }
    }
}
