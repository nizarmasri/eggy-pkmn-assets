using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PokeModelExporter.Export;
using PokeModelExporter.Model;

namespace Eggy.Conv
{
    // A shiny companion (<id>.shiny.glb): only the materials whose shiny look differs from the base model's,
    // under the base's material names (the game swaps them in by name), with their textures. Each material
    // rides a one-triangle carrier mesh of its own, so a loader that walks the scene finds it too.
    public static class Shiny
    {
        public sealed class Look
        {
            public List<MaterialData> Materials;
            public Dictionary<string, byte[]> Textures;
            public Func<MaterialData, MaterialMapping> Mapping;
        }

        public static JsonObject Write(Look base_, Look shiny, IEnumerable<string> used, string outPath)
        {
            var report = new JsonObject();
            var differ = new List<MaterialData>();
            var same = new JsonArray();
            foreach (var name in used.Distinct())
            {
                var a = base_.Materials.FirstOrDefault(m => m.Name == name);
                var b = shiny.Materials.FirstOrDefault(m => m.Name == name);
                if (a == null || b == null) continue;
                if (Signature(a, base_) == Signature(b, shiny)) { same.Add(name); continue; }
                differ.Add(b);
            }
            report["materials"] = new JsonArray(differ.Select(m => (JsonNode)m.Name).ToArray());
            report["unchanged"] = same;
            if (differ.Count == 0) { report["file"] = null; return report; }

            var model = new ModelData { Name = Path.GetFileNameWithoutExtension(outPath) };
            var textures = new Dictionary<string, byte[]>();
            foreach (var m in differ)
            {
                model.Materials.Add(m);
                var map = shiny.Mapping(m);
                foreach (var t in Referenced(map))
                    if (t != null && shiny.Textures.TryGetValue(t, out var bytes)) textures[t] = bytes;
                model.Meshes.Add(new MeshData
                {
                    Name = "shiny " + m.Name,
                    Positions = new[] { Vector3.Zero, new Vector3(1e-4f, 0, 0), new Vector3(0, 1e-4f, 0) },
                    Normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
                    TexCoords = { new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY } },
                    SubMeshes = { new SubMeshData { Material = m.Name, Indices = new[] { 0, 1, 2 } } },
                });
            }
            var glb = GlbWriter.Write(model, textures, shiny.Mapping, null);
            File.WriteAllBytes(outPath, glb);
            report["file"] = Path.GetFileName(outPath);
            report["rawBytes"] = glb.Length;
            return report;
        }

        static IEnumerable<string> Referenced(MaterialMapping map) =>
            new[] { map.BaseColorTexture, map.NormalTexture, map.EmissiveTexture, map.MetallicRoughnessTexture, map.OcclusionTexture }.Concat(map.Maps.Values);

        // What a material looks like once written: its glTF factors and the bytes of every texture it uses.
        static string Signature(MaterialData m, Look look)
        {
            var map = look.Mapping(m);
            string Tex(string name) => name != null && look.Textures.TryGetValue(name, out var b) ? Convert.ToHexString(SHA256.HashData(b)).Substring(0, 16) : "-";
            var parts = new List<string>
            {
                Tex(map.BaseColorTexture), Tex(map.NormalTexture), Tex(map.EmissiveTexture), Tex(map.MetallicRoughnessTexture), Tex(map.OcclusionTexture),
                string.Join(",", map.BaseColorFactor ?? Array.Empty<float>()), map.Roughness.ToString("R"), map.Metallic.ToString("R"),
                map.AlphaBlend.ToString(), map.DoubleSided.ToString(), map.Unlit.ToString(),
            };
            parts.AddRange(map.Maps.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + Tex(kv.Value)));
            return string.Join("|", parts);
        }
    }
}
