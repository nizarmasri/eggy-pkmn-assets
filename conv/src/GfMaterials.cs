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
    //   NormalMapTex (Sword/Shield; Let's Go has none) is an object-space normal map: its three channels
    //   are the surface normal in the model's own axes, not a tangent-space perturbation. It is turned into a
    //   glTF tangent-space map against the mesh's own frame (BakeNormal), with the channel order and signs
    //   the data agrees with best (measured against the vertex normals and reported).
    //   AmbientTex is not occlusion: its channels are flat shader masks (gloss spots on claws and eyes, a
    //   constant ambient level), so there is no ambient occlusion to carry; it is left out.
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
            var normals = new JsonObject();
            Bntx.Texture Load(string name)
            {
                if (name == null || name == "dummy_col" || name.StartsWith("projection_effect") || name.StartsWith("viewport_mask")) return null;
                if (decoded.TryGetValue(name, out var t)) return t;
                var bytes = read(name + ".bntx") ?? read(name + ".png") ?? read(name);
                if (bytes == null) { formats[name] = "missing"; return decoded[name] = null; }
                try
                {
                    t = PngRead.Is(bytes) ? PngRead.Decode(bytes) : Bntx.Read(bytes);
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
                // Brilliant Diamond / Shining Pearl (Bdsp.cs): Unity's blend and cull settings, and a tangent-space
                // normal map used as it is where it covers the mesh's UVs once.
                if (m.ShaderGroup == Bdsp.Shader)
                {
                    map.AlphaBlend = m.Values.TryGetValue("BlendMode", out var bm) && bm >= 1;
                    map.DoubleSided = m.Values.TryGetValue("CullMode", out var cm) && cm == 0;
                }
                var nor = On("NormalMapEnable") ? Slot(m, "NormalMapTex", "NormalMap") : null;
                var norName = m.Maps.FirstOrDefault(x => x.Sampler == "NormalMapTex").Texture;
                if (nor != null && norName != null && norName.EndsWith("_nor_ts"))
                {
                    if (Whole(nor.Scale.X) == 1 && Whole(nor.Scale.Y) == 1 && nor.Scale == Vector2.One && nor.Translate == Vector2.Zero)
                    {
                        string key2 = norName + ".png";
                        if (!result.Textures.ContainsKey(key2)) result.Textures[key2] = Png.Encode(Bdsp.TangentNormal(nor.Tex), nor.Tex.Width, nor.Tex.Height);
                        map.NormalTexture = key2;
                        normals[m.Name] = new JsonObject { ["texture"] = norName, ["tangentSpace"] = true, ["used"] = true };
                    }
                }
                else if (nor != null)
                {
                    var how = BakeNormal(gm, m, nor, bottomOrigin, result, map);
                    if (how != null) normals[m.Name] = how;
                }
            }
            if (normals.Count > 0) report["gfNormalMaps"] = normals;

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

        // The 48 ways to read a normal from three channels: which channel is X, Y and Z, and each one's sign.
        static readonly (int[] Axis, Vector3 Sign)[] Decodes = (
            from perm in new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } }
            from sx in new[] { 1f, -1f }
            from sy in new[] { 1f, -1f }
            from sz in new[] { 1f, -1f }
            select (perm, new Vector3(sx, sy, sz))).ToArray();

        static Vector3 Decode(Vector4 c, (int[] Axis, Vector3 Sign) d)
        {
            Span<float> ch = stackalloc float[] { c.X * 2 - 1, c.Y * 2 - 1, c.Z * 2 - 1 };
            return new Vector3(ch[d.Axis[0]] * d.Sign.X, ch[d.Axis[1]] * d.Sign.Y, ch[d.Axis[2]] * d.Sign.Z);
        }

        // Sword/Shield's object-space normal map as a glTF tangent-space one, in the same space as the baked
        // colour (mesh UVs, V from the top). Each texel the material's triangles cover takes its triangle's
        // frame: T along +U and B toward the image's top (-V in glTF), both laid flat against the interpolated
        // vertex normal N, which is how a viewer without stored tangents (three.js) rebuilds it per pixel. The
        // map's normal n becomes the m with T·m.x + B·m.y + N·m.z along n. The channel order and signs are the
        // ones under which the map agrees best with the vertex normals (it must: a normal map's large-scale
        // shape is the surface's); a map that agrees with none (score under 0.6) is left out.
        static JsonObject BakeNormal(GfModel gm, GfMaterial m, Sampler nor, bool bottomOrigin, Result result, MaterialMapping map)
        {
            int W = nor.Tex.Width * Whole(nor.Scale.X), H = nor.Tex.Height * Whole(nor.Scale.Y);
            float shrink = Math.Min(1f, 2048f / Math.Max(W, H));
            W = Math.Max(1, (int)(W * shrink));
            H = Math.Max(1, (int)(H * shrink));
            var tri = new int[W * H];
            Array.Fill(tri, -1);
            var bary = new Vector3[W * H];
            var list = new List<(MeshData Mesh, int A, int B, int C)>();
            foreach (var mesh in gm.Model.Meshes)
            {
                if (mesh.Positions == null || mesh.Normals == null || mesh.TexCoords.Count == 0 || mesh.TexCoords[0] == null) continue;
                foreach (var sub in mesh.SubMeshes.Where(x => x.Material == m.Name))
                    for (int i = 0; i + 2 < sub.Indices.Length; i += 3) list.Add((mesh, sub.Indices[i], sub.Indices[i + 1], sub.Indices[i + 2]));
            }
            if (list.Count == 0) return null;
            // Output pixel of a mesh UV (V from the top when the game's runs from the bottom).
            Vector2 Px(Vector2 uv) => new Vector2(uv.X * W, (bottomOrigin ? 1 - uv.Y : uv.Y) * H);
            for (int t = 0; t < list.Count; t++)
            {
                var (mesh, a, b, c) = list[t];
                var uvs = mesh.TexCoords[0];
                var shift = new Vector2((float)Math.Floor((uvs[a].X + uvs[b].X + uvs[c].X) / 3), (float)Math.Floor((uvs[a].Y + uvs[b].Y + uvs[c].Y) / 3));
                Vector2 p0 = Px(uvs[a] - shift), p1 = Px(uvs[b] - shift), p2 = Px(uvs[c] - shift);
                float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
                if (Math.Abs(area) < 1e-9f) continue;
                int x0 = Math.Max(0, (int)Math.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X)))), x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))));
                int y0 = Math.Max(0, (int)Math.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y)))), y1 = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))));
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var q = new Vector2(x + 0.5f, y + 0.5f);
                    float w0 = ((p1.X - q.X) * (p2.Y - q.Y) - (p2.X - q.X) * (p1.Y - q.Y)) / area;
                    float w1 = ((p2.X - q.X) * (p0.Y - q.Y) - (p0.X - q.X) * (p2.Y - q.Y)) / area;
                    float w2 = 1 - w0 - w1;
                    const float e = -0.02f; // a hair past the edges, so seams have no gaps
                    if (w0 < e || w1 < e || w2 < e) continue;
                    int p = y * W + x;
                    tri[p] = t;
                    bary[p] = new Vector3(w0, w1, w2);
                }
            }
            Vector2 UvAt(int p)
            {
                var (mesh, a, b, c) = list[tri[p]];
                var uvs = mesh.TexCoords[0];
                var w = bary[p];
                return uvs[a] * w.X + uvs[b] * w.Y + uvs[c] * w.Z;
            }
            Vector3 NormalAt(int p)
            {
                var (mesh, a, b, c) = list[tri[p]];
                var w = bary[p];
                return Vector3.Normalize(mesh.Normals[a] * w.X + mesh.Normals[b] * w.Y + mesh.Normals[c] * w.Z);
            }
            // Which reading of the channels the vertex normals agree with.
            var sampleAt = new List<int>();
            for (int p = 0; p < W * H; p += 7) if (tri[p] >= 0) sampleAt.Add(p);
            if (sampleAt.Count == 0) return null;
            var texel = sampleAt.Select(p => Sample(nor, UvAt(p), bottomOrigin, ColorForm)).ToArray();
            var vn = sampleAt.Select(NormalAt).ToArray();
            var scores = Decodes.Select(d =>
            {
                double sum = 0;
                for (int i = 0; i < texel.Length; i++)
                {
                    var n = Decode(texel[i], d);
                    float len = n.Length();
                    if (len > 1e-4f) sum += Vector3.Dot(n / len, vn[i]);
                }
                return sum / texel.Length;
            }).ToArray();
            int best = Array.IndexOf(scores, scores.Max());
            var decode = Decodes[best];
            var report = new JsonObject
            {
                ["texture"] = m.Maps.First(x => x.Sampler == "NormalMapTex").Texture,
                ["channels"] = string.Concat(decode.Axis.Select((ax, i) => (new[] { decode.Sign.X, decode.Sign.Y, decode.Sign.Z }[i] < 0 ? "-" : "+") + "RGB"[ax])),
                ["agreement"] = Math.Round(scores[best], 3),
                ["nextBest"] = Math.Round(scores.Where((_, i) => i != best).Max(), 3),
                ["size"] = $"{W}x{H}",
            };
            if (scores[best] < 0.6) { report["used"] = false; return report; }

            var rgba = new byte[W * H * 4];
            var have = new bool[W * H];
            for (int p = 0; p < W * H; p++)
            {
                if (tri[p] < 0) continue;
                var (mesh, a, b, c) = list[tri[p]];
                var uvs = mesh.TexCoords[0];
                var n = Decode(Sample(nor, UvAt(p), bottomOrigin, ColorForm), decode);
                if (n.LengthSquared() < 1e-8f) continue;
                n = Vector3.Normalize(n);
                var N = NormalAt(p);
                // dP/du and dP/dv of the triangle, V as glTF has it (from the top).
                Vector2 G(Vector2 uv) => new Vector2(uv.X, bottomOrigin ? 1 - uv.Y : uv.Y);
                Vector3 e1 = mesh.Positions[b] - mesh.Positions[a], e2 = mesh.Positions[c] - mesh.Positions[a];
                Vector2 d1 = G(uvs[b]) - G(uvs[a]), d2 = G(uvs[c]) - G(uvs[a]);
                float det = d1.X * d2.Y - d2.X * d1.Y;
                if (Math.Abs(det) < 1e-12f) continue;
                var dPdu = (e1 * d2.Y - e2 * d1.Y) / det;
                var dPdv = (e2 * d1.X - e1 * d2.X) / det;
                var T = dPdu - N * Vector3.Dot(N, dPdu);
                var B = -(dPdv - N * Vector3.Dot(N, dPdv)); // toward the image's top: glTF's +Y
                if (T.LengthSquared() < 1e-12f || B.LengthSquared() < 1e-12f) continue;
                T = Vector3.Normalize(T);
                B = Vector3.Normalize(B);
                // Solve [T B N] m = n.
                var frame = new Matrix4x4(T.X, T.Y, T.Z, 0, B.X, B.Y, B.Z, 0, N.X, N.Y, N.Z, 0, 0, 0, 0, 1);
                if (!Matrix4x4.Invert(frame, out var inv)) continue;
                var mt = Vector3.Transform(n, inv);
                if (mt.Z < 0.05f) mt.Z = 0.05f; // never facing into the surface
                mt = Vector3.Normalize(mt);
                Put(rgba, p * 4, new Vector4(mt.X * 0.5f + 0.5f, mt.Y * 0.5f + 0.5f, mt.Z * 0.5f + 0.5f, 1));
                have[p] = true;
            }
            // Grow the islands a few texels so filtering at their edges never reaches the flat fill.
            for (int pass = 0; pass < 6; pass++)
            {
                var grow = new List<(int P, Vector4 C)>();
                for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int p = y * W + x;
                    if (have[p]) continue;
                    var sum = Vector4.Zero;
                    int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= W || yy >= H || !have[yy * W + xx]) continue;
                        int q = (yy * W + xx) * 4;
                        sum += new Vector4(rgba[q], rgba[q + 1], rgba[q + 2], 255) / 255f;
                        n++;
                    }
                    if (n > 0) grow.Add((p, sum / n));
                }
                foreach (var (p, c) in grow) { Put(rgba, p * 4, c); have[p] = true; }
            }
            for (int p = 0; p < W * H; p++)
                if (!have[p]) Put(rgba, p * 4, new Vector4(0.5f, 0.5f, 1, 1));
            string key = m.Name + "_normal.png";
            result.Textures[key] = Png.Encode(rgba, W, H);
            map.NormalTexture = key;
            report["used"] = true;
            return report;
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
