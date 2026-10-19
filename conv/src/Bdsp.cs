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
        // FBX2glTF's units to metres: measured on Ledyba (0.0104 tall, 1.0 m in the Pokédex) and Castform
        // (0.0035, 0.3 m).
        public const float Unit = 100;
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

        // See-through shells over a body, as BDSP draws its Castform forms: Unity sorts its see-through parts by
        // distance, and the face inside the shells comes out on top of them; three.js sorts them by where their
        // objects stand (all at the model's root), so a shell can cover the face. Here, so that no order matters:
        //   a rim shell (a copy of a part a few per cent larger, coloured by a tiny flat texture, which the game's
        //   shader shows only along the silhouette) is left out;
        //   of two shells in the same place with the same texture, the second is left out;
        //   a shell with a body inside it that fills most of it (a gel or water hood over a face) is a veil:
        //   its alpha is thinned (GfMaterials), and the body drawn solid shows through it.
        // Returns what it did.
        public const float VeilAlpha = 0.45f;

        public static List<string> Shells(string dir, ModelData model, Dictionary<string, GfMaterial> mats)
        {
            var parts = new List<(MeshData Mesh, SubMeshData Sub, Vector3 Lo, Vector3 Hi)>();
            foreach (var mesh in model.Meshes)
                foreach (var sub in mesh.SubMeshes)
                {
                    var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
                    foreach (int i in sub.Indices) { lo = Vector3.Min(lo, mesh.Positions[i]); hi = Vector3.Max(hi, mesh.Positions[i]); }
                    if (sub.Indices.Length > 0) parts.Add((mesh, sub, lo, hi));
                }
            const float e = 0.001f;
            static bool Inside((MeshData, SubMeshData, Vector3 Lo, Vector3 Hi) q, (MeshData, SubMeshData, Vector3 Lo, Vector3 Hi) p) =>
                p.Lo.X <= q.Lo.X + e && p.Lo.Y <= q.Lo.Y + e && p.Lo.Z <= q.Lo.Z + e && p.Hi.X >= q.Hi.X - e && p.Hi.Y >= q.Hi.Y - e && p.Hi.Z >= q.Hi.Z - e;
            static Vector3 Ratio((MeshData, SubMeshData, Vector3 Lo, Vector3 Hi) outer, (MeshData, SubMeshData, Vector3 Lo, Vector3 Hi) inner) =>
                (outer.Hi - outer.Lo) / Vector3.Max(inner.Hi - inner.Lo, new Vector3(1e-4f));
            bool Blended(string mat) => mats.TryGetValue(mat, out var g) && g.Values.TryGetValue("BlendMode", out var bm) && bm >= 1;
            string Colour(string mat) => mats.TryGetValue(mat, out var g) ? g.Maps.FirstOrDefault(m => m.Sampler == "Col0Tex").Texture : null;

            var notes = new List<string>();
            var gone = new HashSet<SubMeshData>();
            foreach (var p in parts)
            {
                if (!Blended(p.Sub.Material)) continue;
                var col = Colour(p.Sub.Material);
                int width = col != null ? PngWidth(Path.Combine(dir, col + ".png")) : 0;
                bool rim = width > 0 && width <= 64 && parts.Any(q => q.Sub != p.Sub && q.Sub.Material != p.Sub.Material && !gone.Contains(q.Sub)
                    && Inside(q, p) && MaxOf(Ratio(p, q)) <= 1.1f);
                // (Only shells over a body: two coincident copies of anything else, like a wing's two sides, stay.)
                bool overBody = parts.Any(q => q.Sub != p.Sub && Inside(q, p) && MaxOf(Ratio(p, q)) > 1.1f && MaxOf(Ratio(p, q)) <= 2f);
                bool twin = !rim && overBody && parts.Any(q => q.Sub != p.Sub && !gone.Contains(q.Sub) && parts.IndexOf(q) < parts.IndexOf(p) && Blended(q.Sub.Material)
                    && Colour(q.Sub.Material) == col && Vector3.Distance(q.Lo, p.Lo) < e && Vector3.Distance(q.Hi, p.Hi) < e);
                if (!rim && !twin) continue;
                gone.Add(p.Sub);
                p.Mesh.SubMeshes.Remove(p.Sub);
                notes.Add($"{p.Mesh.Name}/{p.Sub.Material}: {(rim ? "rim shell" : "second copy of a shell")}, left out");
            }
            model.Meshes.RemoveAll(m => m.SubMeshes.Count == 0);
            foreach (var p in parts)
            {
                if (gone.Contains(p.Sub) || !Blended(p.Sub.Material) || !mats.TryGetValue(p.Sub.Material, out var gm)) continue;
                var body = parts.FirstOrDefault(q => q.Sub != p.Sub && !gone.Contains(q.Sub) && Inside(q, p) && MaxOf(Ratio(p, q)) > 1.1f && MaxOf(Ratio(p, q)) <= 2f);
                if (body.Sub == null) continue;
                gm.Values["Veil"] = VeilAlpha;
                notes.Add($"{p.Mesh.Name}/{p.Sub.Material}: a veil over {body.Sub.Material}, alpha x{VeilAlpha}");
            }
            return notes;
        }

        static float MaxOf(Vector3 v) => Math.Max(v.X, Math.Max(v.Y, v.Z));

        static int PngWidth(string file)
        {
            if (!File.Exists(file)) return 0;
            var head = new byte[24];
            using (var f = File.OpenRead(file)) if (f.Read(head, 0, 24) < 24) return 0;
            return head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G' ? (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19] : 0;
        }

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
