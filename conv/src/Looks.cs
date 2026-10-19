using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using PokeModelExporter.Export;
using PokeModelExporter.Formats;
using PokeModelExporter.Model;

namespace Eggy.Conv
{
    // How the game should draw a material, decided once for a model and its shiny alike. The role travels in
    // the material's options as EggyRole and the optimiser turns it into glTF material extras.role:
    //   glow  a flame or glow core: unlit, drawn additively without writing depth (Charizard's tail flame,
    //         Rapidash's mane): Trinity's Unlit layered materials with over-bright (HDR) layer colours, or
    //         named for fire, flame or glow.
    public static class Looks
    {
        static readonly Regex GlowName = new Regex("fire|flame|glow|burn|plasma|aura", RegexOptions.IgnoreCase);

        public static MaterialMapping Trinity(Job job, MaterialData m, MaterialMapping map)
        {
            if (IsGlow(m))
            {
                m.ShaderOptions["EggyRole"] = "glow";
                map.Unlit = true;
                map.DoubleSided = true;
            }
            return map;
        }

        // Also a gas drawn with NonDirectional (Gastly's smoke): a sphere around the body that the game renders as a
        // haze; drawn as a surface it hid Gastly's face. Added on as a glow, the face shows through it. And what the
        // Transparent shader adds onto the scene (alphaType Add: Elekid's thunder, Cresselia's rings), which drawn as
        // a surface showed the dark colour of its empty texels.
        static bool IsGlow(MaterialData m) => (m.Shader == "Unlit" && (Hdr(m) || GlowName.IsMatch(m.Name)))
            || (m.Shader == "NonDirectional" && SmokeName.IsMatch(m.Name))
            || (m.Shader == "Transparent" && m.AlphaType == "Add");
        static readonly System.Text.RegularExpressions.Regex SmokeName = new(@"smoke|gas", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // A glow's colour, baked again for additive drawing. PME's bake spills a layer's over-bright excess into
        // the other channels (bloom), which turns Charizard's 5.0 red tip and 4.0 orange base into salmon and
        // pale yellow; added over a stage they wash out to cream. Here each channel rolls off on its own (linear
        // up to 0.5, then easing toward 1), so the red stays red and the orange orange, the way the flame reads
        // in the game. Layers are mixed by their share of the mask as PME does.
        public static int RebakeGlows(ModelData model, Dictionary<string, byte[]> textures, Func<string, byte[]> read)
        {
            int n = 0;
            foreach (var m in model.Materials)
            {
                if (!IsGlow(m) || !m.Textures.TryGetValue("LayerMaskMap", out var maskFile)) continue;
                string name = m.Name + "_color.png";
                if (!textures.ContainsKey(name)) continue;
                var maskBytes = read(maskFile);
                if (maskBytes == null) continue;
                var mask = Bntx.Read(maskBytes, false);
                Bntx.Texture baseTex = m.Textures.TryGetValue("BaseColorMap", out var bf) && read(bf) is byte[] bb ? Bntx.Read(bb, false) : null;
                var layers = new Vector3?[4];
                var scales = new float[4];
                for (int i = 0; i < 4; i++)
                {
                    if (m.Colors.TryGetValue("BaseColorLayer" + (i + 1), out var c)) layers[i] = new Vector3(c.X, c.Y, c.Z);
                    scales[i] = m.Floats.TryGetValue("LayerMaskScale" + (i + 1), out var s) ? s : 1f;
                }
                int w = mask.Width, h = mask.Height;
                var rgba = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 4;
                    var b = Sample(baseTex, (x + 0.5f) / w, (y + 0.5f) / h);
                    var mix = Vector3.Zero;
                    float total = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        float k = Math.Min(1f, mask.Rgba[p + i] / 255f * scales[i]);
                        if (k <= 0 || layers[i] is not Vector3 l) continue;
                        mix += l * k;
                        total += k;
                    }
                    var rgb = new Vector3(b.X, b.Y, b.Z);
                    if (total > 0) rgb = Vector3.Lerp(rgb, rgb * (mix / total), Math.Min(1f, total));
                    rgba[p] = Byte(Roll(rgb.X)); rgba[p + 1] = Byte(Roll(rgb.Y)); rgba[p + 2] = Byte(Roll(rgb.Z)); rgba[p + 3] = Byte(b.W);
                }
                textures[name] = Png.Encode(rgba, w, h);
                n++;
            }
            return n;
        }

        // The Transparent shader's blended materials (Spiritomb's vortex, Flygon's eye lenses, Venomoth's eyes).
        // PME blends only an alphaType naming Transparent, and these say BlendPreMultiAlpha or Blend, so they came
        // out solid: Spiritomb's vortex an opaque disc in front of its face. The shader fades with the view angle,
        // from FresnelAlphaMin face on to FresnelAlphaMax edge on, past FresnelAngleBias; the game draws one alpha
        // for the whole material, so it gets that fade averaged over a sphere's visible surface (Spiritomb's 0 to
        // 0.5 past 0.3: 0.27). A material stays solid when neither the fade nor its texture leaves anything to see
        // through (Deoxys's crystal, 1 to 1 over a solid texture), since a blended part writes no depth.
        public const float SeeThroughFade = 0.98f, SolidTexture = 0.97f;
        public static int Translucent(ModelData model, Func<MaterialData, MaterialMapping> mapping, Dictionary<string, byte[]> textures)
        {
            int n = 0;
            foreach (var m in model.Materials)
            {
                if (m.Shader != "Transparent" || m.AlphaType == null || m.AlphaType.IndexOf("Blend", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var map = mapping(m);
                if (map == null || map.AlphaBlend) continue;
                float fade = FresnelAlpha(m);
                bool textureSees = map.BaseColorTexture != null && textures.TryGetValue(map.BaseColorTexture, out var png)
                    && PngRead.Is(png) && SolidShare(PngRead.Decode(png)) < SolidTexture;
                if (fade >= SeeThroughFade && !textureSees) continue;
                map.AlphaBlend = true;
                if (fade < SeeThroughFade)
                {
                    var f = map.BaseColorFactor ?? new float[] { 1, 1, 1, 1 };
                    map.BaseColorFactor = new[] { f[0], f[1], f[2], f[3] * fade };
                }
                n++;
            }
            return n;
        }

        // (1 - n.v) over a sphere's visible surface averages 1/3.
        static float FresnelAlpha(MaterialData m)
        {
            if (!m.Floats.TryGetValue("FresnelAlphaMin", out var lo) || !m.Floats.TryGetValue("FresnelAlphaMax", out var hi)) return 1;
            float bias = m.Floats.TryGetValue("FresnelAngleBias", out var b) ? Math.Clamp(b, 0f, 1f) : 0f;
            return Math.Clamp(lo + (hi - lo) * (bias + (1 - bias) / 3f), 0f, 1f);
        }

        static float SolidShare(Bntx.Texture t)
        {
            int solid = 0, n = t.Width * t.Height;
            for (int i = 0; i < n; i++) if (t.Rgba[i * 4 + 3] >= 250) solid++;
            return n == 0 ? 1 : solid / (float)n;
        }

        // Legends Z-A (IkCharacter) layers, baked again one over the other. PME mixes the layers by their share of
        // the mask (right for Scarlet/Violet, whose masks split a texel between layers); Z-A's masks overlap, a
        // later layer painted over an earlier one (Lucario's black face mask and cream chest lie over its blue
        // layer, which covers the whole body), so shared they came out dark blue and pale blue. Here each layer
        // is laid over what is under it by its own mask, in order: base x colour, mixed in linear light.
        public static int RebakeLayeredZa(ModelData model, Dictionary<string, byte[]> textures, Func<string, byte[]> read) =>
            RebakeLayered(model, textures, read, m => m.Shader == "IkCharacter", gain: 1, shareMix: false);

        // Legends: Arceus's layer colours are linear and half scale: twice the colour, mixed with the base in
        // linear space, is the game's (Pikachu's 0.48, 0.346, 0.01 is its yellow, 0.98, 0.86, 0.15 in sRGB;
        // Togetic's 0.39 grey is white). PME's bake used them as they are, which left Togetic grey, Ponyta
        // olive, Silcoon black and the Spheal line navy and olive. Mixed by their share of the mask, as PME does.
        public static int RebakeLayeredLa(ModelData model, Dictionary<string, byte[]> textures, Func<string, byte[]> read) =>
            RebakeLayered(model, textures, read, m => m.Shader != "Unlit" && !IsGlow(m), gain: 2, shareMix: true);

        static int RebakeLayered(ModelData model, Dictionary<string, byte[]> textures, Func<string, byte[]> read, Func<MaterialData, bool> applies, float gain, bool shareMix)
        {
            int n = 0;
            foreach (var m in model.Materials)
            {
                // Eyes (a highlight layer of their own) keep PME's bake; their layers do not overlap.
                if (!applies(m) || !m.Textures.TryGetValue("LayerMaskMap", out var maskFile) || m.Textures.ContainsKey("HighlightMaskMap")) continue;
                string name = m.Name + "_color.png";
                if (!textures.ContainsKey(name)) continue;
                var layers = new Vector3?[4];
                var scales = new float[4];
                for (int i = 0; i < 4; i++)
                {
                    if (m.Colors.TryGetValue("BaseColorLayer" + (i + 1), out var c)) layers[i] = new Vector3(c.X, c.Y, c.Z) * gain;
                    scales[i] = m.Floats.TryGetValue("LayerMaskScale" + (i + 1), out var s) ? s : 1f;
                }
                if (layers.All(l => l == null)) continue;
                Bntx.Texture View(string slot, string file)
                {
                    var bytes = file == null ? null : read(file);
                    if (bytes == null) return null;
                    var t = Bntx.Read(bytes, false);
                    var (sx, sy) = Unfold(m);
                    return sx == 1 && sy == 1 ? t : PokeModelExporter.Scarlet.SvMaterialBaker.Unfold(t, sx, sy, m.Wraps.TryGetValue(slot, out var w) ? w : (0u, 0u));
                }
                var mask = View("LayerMaskMap", maskFile);
                if (mask == null) continue;
                var baseTex = View("BaseColorMap", m.Textures.TryGetValue("BaseColorMap", out var bf) ? bf : null);
                int W = mask.Width, H = mask.Height;
                var rgba = new byte[W * H * 4];
                for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int p = (y * W + x) * 4;
                    var b = Sample(baseTex, (x + 0.5f) / W, (y + 0.5f) / H);
                    var lin = new Vector3(Lin(b.X), Lin(b.Y), Lin(b.Z));
                    var c = lin;
                    if (shareMix)
                    {
                        var mix = Vector3.Zero;
                        float total = 0;
                        for (int i = 0; i < 4; i++)
                        {
                            if (layers[i] is not Vector3 l) continue;
                            float k = Math.Min(1f, mask.Rgba[p + i] / 255f * scales[i]);
                            if (k > 0) { mix += l * k; total += k; }
                        }
                        if (total > 0) c = Vector3.Lerp(lin, lin * (mix / total), Math.Min(1f, total));
                    }
                    else
                        for (int i = 0; i < 4; i++)
                        {
                            if (layers[i] is not Vector3 l) continue;
                            float k = Math.Min(1f, mask.Rgba[p + i] / 255f * scales[i]);
                            if (k > 0) c = Vector3.Lerp(c, lin * l, k);
                        }
                    rgba[p] = Byte(Srgb(c.X)); rgba[p + 1] = Byte(Srgb(c.Y)); rgba[p + 2] = Byte(Srgb(c.Z)); rgba[p + 3] = Byte(b.W);
                }
                textures[name] = Png.Encode(rgba, W, H);
                n++;
            }
            return n;
        }

        // The whole-number UV scale a Z-A body samples its textures at (as PME's SvMaterialBaker unfolds them).
        static (int, int) Unfold(MaterialData m)
        {
            if (!m.Colors.TryGetValue("UVScaleOffset", out var s) || Math.Abs(s.Z) > 1e-4f || Math.Abs(s.W) > 1e-4f) return (1, 1);
            static int Whole(float v) => v >= 1 && v <= 4 && Math.Abs(v - Math.Round(v)) < 1e-4f ? (int)Math.Round(v) : 1;
            return (Whole(s.X), Whole(s.Y));
        }

        static float Lin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        static float Srgb(float c) { c = Math.Clamp(c, 0, 1); return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f; }

        static float Roll(float v) => v <= 0.5f ? Math.Max(0, v) : 1f - 0.5f * MathF.Exp(-(v - 0.5f) / 0.5f);
        static byte Byte(float v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        static Vector4 Sample(Bntx.Texture t, float u, float v)
        {
            if (t == null) return Vector4.One;
            int x = Math.Min(t.Width - 1, (int)(u * t.Width)), y = Math.Min(t.Height - 1, (int)(v * t.Height));
            int p = (y * t.Width + x) * 4;
            return new Vector4(t.Rgba[p], t.Rgba[p + 1], t.Rgba[p + 2], t.Rgba[p + 3]) / 255f;
        }

        static bool Hdr(MaterialData m) =>
            m.Colors.Where(kv => kv.Key.StartsWith("BaseColorLayer") || kv.Key.StartsWith("EmissionColor"))
                .Any(kv => kv.Value.X > 1.01f || kv.Value.Y > 1.01f || kv.Value.Z > 1.01f)
            || m.Floats.Any(kv => kv.Key.StartsWith("EmissionIntensity") && kv.Value > 1.01f);
    }
}
