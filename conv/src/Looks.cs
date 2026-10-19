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

        static bool IsGlow(MaterialData m) => m.Shader == "Unlit" && (Hdr(m) || GlowName.IsMatch(m.Name));

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
