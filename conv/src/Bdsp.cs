using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PokeModelExporter.Model;

namespace Eggy.Conv
{
    // Brilliant Diamond / Shining Pearl battle models, from what the workflow's Unity path leaves in a folder
    // (pipeline/bdsp/extract.sh): <folder>.glb from FBX2glTF (meshes, skeleton, every battle clip), the PNG
    // textures, and each Unity material dumped as text (materials/<name>.txt). BDSP's Pokémon shader is Sword/
    // Shield's PokeDefaultShader ported to Unity, with the same properties (_Col0Tex at a (2, 1) scale with the
    // U axis mirrored, _ColorBaseU, _L1Col0Tex, _EmissionMaskTex...), so the materials become GfMaterials and
    // are baked by GfMaterials like Sword/Shield's.
    //
    // A texture is found by the name its slot gives it in the game's convention, <prefix>_<Part>_<kind>, the
    // part being the longest texture part the material's name starts with (pm0351_12_00-BodyADodo -> BodyA):
    // _Col0Tex col (col_rare in the shiny bundle), _NormalMapTex nor_ts (tangent space here), _AmbientTex amb,
    // _EmissionMaskTex emi. FBX2glTF's "(Depth)" copies (a depth pre-pass, DefaultMaterial) are left out.
    public static class Bdsp
    {
        public const string Shader = "BdspPokeShader";
        static readonly Regex Float = new(@"string first = ""(_\w+)""\s+float second = ([-\d.eE+]+)");
        static readonly Regex Color = new(@"string first = ""(_\w+)""\s+ColorRGBA second\s+float r = ([-\d.eE+]+)\s+float g = ([-\d.eE+]+)\s+float b = ([-\d.eE+]+)\s+float a = ([-\d.eE+]+)");
        static readonly Regex Tex = new(@"string first = ""(_\w+)""\s+UnityTexEnv second\s+PPtr<Texture> m_Texture\s+int m_FileID = (-?\d+)\s+SInt64 m_PathID = (-?\d+)\s+Vector2f m_Scale\s+float x = ([-\d.eE+]+)\s+float y = ([-\d.eE+]+)\s+Vector2f m_Offset\s+float x = ([-\d.eE+]+)\s+float y = ([-\d.eE+]+)");
        static readonly Regex NameRx = new(@"string m_Name = ""([^""]*)""");

        public static bool KeepPrimitive(string mesh, string material) => !mesh.Contains("(Depth)") && material != "DefaultMaterial";

        // The Unity materials of a folder, by name (the shiny bundle's "_rare" names as the base's).
        public static Dictionary<string, GfMaterial> Materials(string dir, JsonObject report)
        {
            var outm = new Dictionary<string, GfMaterial>();
            var pngs = Directory.GetFiles(dir, "*.png").Select(Path.GetFileNameWithoutExtension).ToHashSet();
            var notes = new JsonArray();
            string matDir = Path.Combine(dir, "materials");
            if (!Directory.Exists(matDir)) return outm;
            foreach (var file in Directory.GetFiles(matDir, "*.txt"))
            {
                var text = File.ReadAllText(file);
                var nm = NameRx.Match(text);
                if (!nm.Success) continue;
                string unityName = nm.Groups[1].Value;
                string name = unityName.EndsWith("_rare") ? unityName[..^5] : unityName;
                int dash = name.IndexOf('-');
                string prefix = dash > 0 ? name[..dash] : name, local = dash > 0 ? name[(dash + 1)..] : name;
                var floats = Float.Matches(text).ToDictionary(m => m.Groups[1].Value, m => F(m.Groups[2].Value), StringComparer.Ordinal);
                var colors = Color.Matches(text).ToDictionary(m => m.Groups[1].Value, m => new Vector3(F(m.Groups[2].Value), F(m.Groups[3].Value), F(m.Groups[4].Value)), StringComparer.Ordinal);
                var tex = Tex.Matches(text).ToDictionary(m => m.Groups[1].Value, m => (Path: m.Groups[3].Value, Scale: new Vector2(F(m.Groups[4].Value), F(m.Groups[5].Value)), Offset: new Vector2(F(m.Groups[6].Value), F(m.Groups[7].Value))), StringComparer.Ordinal);
                // The texture part: the longest <prefix>_<Part>_... file part the material's local name starts with.
                var parts = pngs.Where(p => p.StartsWith(prefix + "_", StringComparison.Ordinal))
                    .Select(p => p[(prefix.Length + 1)..]).Select(p => p.Contains('_') ? p[..p.IndexOf('_')] : p).Distinct()
                    .Where(p => local.StartsWith(p, StringComparison.Ordinal)).OrderByDescending(p => p.Length).ToList();
                string part = parts.FirstOrDefault();
                var gm = new GfMaterial { Name = name, ShaderGroup = Shader, ShaderName = local };
                string Find(params string[] kinds)
                {
                    foreach (var k in kinds)
                        if (part != null && pngs.Contains($"{prefix}_{part}_{k}")) return $"{prefix}_{part}_{k}";
                    return null;
                }
                void Map(string slot, string sampler, string prefixKey, params string[] kinds)
                {
                    if (!tex.TryGetValue(slot, out var t) || t.Path == "0") return;
                    var file = Find(kinds);
                    if (file == null) { notes.Add($"{unityName}.{slot}: no {prefix}_{part ?? "?"}_{string.Join("|", kinds)}.png"); return; }
                    // Sword/Shield's samplers wrap U mirrored and V repeated; BDSP keeps the same half textures.
                    gm.Maps.Add((sampler, file, 2u, 0u));
                    if (prefixKey != null)
                    {
                        gm.Values[prefixKey + "UVScaleU"] = t.Scale.X; gm.Values[prefixKey + "UVScaleV"] = t.Scale.Y;
                        gm.Values[prefixKey + "UVTranslateU"] = t.Offset.X; gm.Values[prefixKey + "UVTranslateV"] = t.Offset.Y;
                    }
                }
                bool rare = unityName.EndsWith("_rare");
                Map("_Col0Tex", "Col0Tex", "Color", rare ? new[] { "col_rare", "col" } : new[] { "col" });
                Map("_L1Col0Tex", "LyCol0Tex", "Layer1", rare ? new[] { "lyc_rare", "lyc" } : new[] { "lyc" });
                Map("_EmissionMaskTex", "EmissionMaskTex", "EmissionMask", "emi");
                Map("_NormalMapTex", "NormalMapTex", "NormalMap", "nor_ts", "nor");
                Map("_AmbientTex", "AmbientTex", null, "amb");
                float Fl(string k, float d) => floats.TryGetValue(k, out var v) ? v : d;
                gm.Values["ColorBaseU"] = Fl("_ColorBaseU", 0); gm.Values["ColorBaseV"] = Fl("_ColorBaseV", 0);
                gm.Values["Layer1BaseU"] = Fl("_Layer1BaseU", 0); gm.Values["Layer1BaseV"] = Fl("_Layer1BaseV", 0);
                gm.Values["BlendMode"] = Fl("_BlendMode", 0);
                gm.Values["CullMode"] = Fl("_CullMode", 2);
                gm.Switches["Layer1Enable"] = Fl("_Layer1Enable", 0) > 0.5f;
                gm.Switches["EmissionMaskUse"] = Fl("_EmissionMaskUse", 0) > 0.5f;
                gm.Switches["NormalMapEnable"] = gm.Maps.Any(m => m.Sampler == "NormalMapTex");
                foreach (var (k, v) in colors) gm.Colors[k.TrimStart('_')] = v;
                outm[name] = gm;
            }
            if (notes.Count > 0) report["bdspMaterialNotes"] = notes;
            return outm;
        }

        static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

        // A tangent-space normal map as glTF wants it (RGB, +Y up, which is Unity's convention too): BC5 exports
        // keep only X and Y (Z rebuilt), DXT5nm ones X in alpha and Y in green.
        public static byte[] TangentNormal(PokeModelExporter.Formats.Bntx.Texture t)
        {
            int n = t.Width * t.Height;
            double r = 0, b = 0, aVar = 0, aMean = 0;
            for (int i = 0; i < n; i++) { r += t.Rgba[i * 4]; b += t.Rgba[i * 4 + 2]; aMean += t.Rgba[i * 4 + 3]; }
            r /= n * 255.0; b /= n * 255.0; aMean /= n * 255.0;
            for (int i = 0; i < n; i++) { double d = t.Rgba[i * 4 + 3] / 255.0 - aMean; aVar += d * d; }
            aVar /= n;
            bool dxt5nm = r > 0.95 && aVar > 1e-4;
            bool rebuild = dxt5nm || b < 0.1;
            var outp = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                float x = (dxt5nm ? t.Rgba[i * 4 + 3] : t.Rgba[i * 4]) / 255f * 2 - 1;
                float y = t.Rgba[i * 4 + 1] / 255f * 2 - 1;
                float z = rebuild ? MathF.Sqrt(Math.Max(0, 1 - x * x - y * y)) : t.Rgba[i * 4 + 2] / 255f * 2 - 1;
                var v = Vector3.Normalize(new Vector3(x, y, Math.Max(z, 0.01f)));
                outp[i * 4] = (byte)Math.Clamp((int)Math.Round((v.X * 0.5f + 0.5f) * 255), 0, 255);
                outp[i * 4 + 1] = (byte)Math.Clamp((int)Math.Round((v.Y * 0.5f + 0.5f) * 255), 0, 255);
                outp[i * 4 + 2] = (byte)Math.Clamp((int)Math.Round((v.Z * 0.5f + 0.5f) * 255), 0, 255);
                outp[i * 4 + 3] = 255;
            }
            return outp;
        }
    }
}
