using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PokeModelExporter.Export;
using PokeModelExporter.Model;
using PokeModelExporter.Scarlet;

namespace Eggy.Conv
{
    // One species from its game files to a raw battle GLB plus a report:
    //   read the model (Trinity: PME's reader; SwSh/LGPE: GfModelReader) and the battle clips,
    //   drop meshes the battle idle hides, rebuild Maya's segment scale compensation (PME),
    //   turn the clips into the contract's idle/appear/cry/physical/special/hit/faint (+ blink),
    //   measure the idle pose for normalisation, find when the attacks connect, bake materials.
    public static class Converter
    {
        public static JsonObject Convert(Job job, bool gfFlipV, string outPath, string debugDir = null, string diagDir = null)
        {
            var r = new JsonObject { ["id"] = job.Id, ["src"] = job.Src, ["dex"] = job.Dex, ["folder"] = job.Folder };
            var notes = new JsonArray();
            r["notes"] = notes;
            var readModel = Loose(job.ModelDir);
            var readAnim = Loose(job.AnimDir);

            // ---- Model and materials ----
            ModelData model;
            Dictionary<string, byte[]> textures;
            Func<MaterialData, MaterialMapping> mapping;
            List<AnimationData> bdspClips = null;
            if (job.Src == "bdsp")
            {
                // Brilliant Diamond / Shining Pearl: the Unity path's GLB, its Unity materials baked as Sword/Shield's.
                var g = GltfRead.Read(Path.Combine(job.ModelDir, job.Folder + ".glb"), Bdsp.KeepPrimitive);
                model = g.Model;
                r["gltf"] = g.Report;
                bdspClips = g.Clips;
                var gm = BdspModel(job.ModelDir, model, r);
                var baked = GfMaterials.Bake(gm, readModel, r, bottomOrigin: true, debugDir);
                textures = baked.Textures;
                mapping = m => baked.Mappings.TryGetValue(m.Name, out var mm) ? mm : MaterialMapping.Default(m);
                foreach (var mesh in model.Meshes)
                    for (int t = 0; t < mesh.TexCoords.Count; t++)
                        if (mesh.TexCoords[t] != null)
                            mesh.TexCoords[t] = mesh.TexCoords[t].Select(v => new Vector2(v.X, 1 - v.Y)).ToArray();
            }
            else if (job.IsGf)
            {
                var gf = GfModelReader.Read(job.Folder + ".gfbmdl", readModel, r);
                model = gf.Model;
                r["uvFlipV"] = gfFlipV;
                var baked = GfMaterials.Bake(gf, readModel, r, bottomOrigin: gfFlipV, debugDir);
                textures = baked.Textures;
                mapping = m => baked.Mappings.TryGetValue(m.Name, out var mm) ? mm : MaterialMapping.Default(m);
                // Textures are baked in glTF's space; the mesh UVs follow (V from the top).
                if (gfFlipV)
                    foreach (var mesh in model.Meshes)
                        for (int t = 0; t < mesh.TexCoords.Count; t++)
                            if (mesh.TexCoords[t] != null)
                                mesh.TexCoords[t] = mesh.TexCoords[t].Select(v => new Vector2(v.X, 1 - v.Y)).ToArray();
                // Parts the game draws in a separate effect pass only (FireMask) are left out.
                foreach (var mesh in model.Meshes) mesh.SubMeshes.RemoveAll(s => baked.Hidden.Contains(s.Material));
                var gone = model.Meshes.Where(m => m.SubMeshes.Count == 0).Select(m => m.Name).ToList();
                model.Meshes.RemoveAll(m => m.SubMeshes.Count == 0);
                if (gone.Count > 0) r["droppedEffectMeshes"] = new JsonArray(gone.Select(g => (JsonNode)g).ToArray());
            }
            else
            {
                model = TrinityModelReader.Read(job.Folder + ".trmdl", readModel);
                // Arceus-<Type>: the type's colours from the material table, over the base materials.
                if (job.Variant != null) r["variant"] = MaterialTable.Apply(model, Require(readModel, job.Folder + ".trmmt"), job.Variant);
                var baked = SvMaterialBaker.Bake(model, readModel);
                textures = baked.Textures;
                int glows = Looks.RebakeGlows(model, textures, readModel);
                if (glows > 0) r["glowsRebaked"] = glows;
                mapping = m => Looks.Trinity(job, m, baked.Mappings[m.Name]);
            }

            // ---- Clips ----
            // BDSP's clips are named after the model they were made for (pm0165_00_00_ba10_waitA01): the Sword/
            // Shield names once that is taken off.
            var bdspByName = bdspClips?.GroupBy(c => Regex.Replace(c.Name, @"^pm\d{4}_\d{2}_\d{2}_", "")).ToDictionary(g => g.Key, g => g.First());
            var available = bdspByName != null ? bdspByName.Keys.OrderBy(c => c, StringComparer.Ordinal).ToList() : Clips.List(job.AnimDir, job.Folder, job.IsGf ? ".gfbanm" : ".tranm");
            r["clipFiles"] = new JsonArray(available.Select(c => (JsonNode)c).ToArray());
            var plan = job.IsGf || bdspByName != null ? Battle.PlanGf(available) : Battle.PlanTrinity(available);
            if (plan.Set != null) r["clipSet"] = plan.Set;
            if (!plan.Sources.ContainsKey("idle")) throw new InvalidDataException("no idle clip among " + string.Join(", ", available));
            var loaded = new Dictionary<string, AnimationData>();
            foreach (var clip in plan.AllClips)
            {
                if (bdspByName != null) { loaded[clip] = bdspByName[clip]; loaded[clip].Name = clip; }
                else loaded[clip] = Clips.Load(job.Folder, clip, job.IsGf, readAnim, notes);
            }
            if (diagDir != null) Diag.Write(job, model, readModel, loaded, diagDir);

            if (loaded.Values.Any(a => a.MaterialTracks.Count > 0))
                notes.Add("material tracks (UV/colour animation, e.g. eye movement) are not carried into glTF: " +
                          string.Join(", ", loaded.Where(kv => kv.Value.MaterialTracks.Count > 0).Select(kv => kv.Key)));

            foreach (var a in loaded.Values) SvExporter.PinRootMotion(a);
            model.Animations.Clear();
            model.Animations.AddRange(loaded.Values);
            int helpers = ScaleCompensation.Apply(model);
            r["sscHelpers"] = helpers;
            // Meshes the clips show and hide: switched by joint scale keys (Visibility).
            var vis = Visibility.Carry(model, loaded, plan.Sources["idle"][0]);
            if (vis.Count > 0) r["visibility"] = vis;

            var skinner = new Skinner(model);
            Battle.Assemble(model, plan, loaded, skinner, r);

            // ---- Normalisation: measured over the idle loop, in the game's units (metres) ----
            var idle = model.Animations.First(a => a.Name == "idle");
            var (min, max) = skinner.Envelope(idle);
            float height = max.Y - min.Y;
            if (!(height > 1e-4f)) throw new InvalidDataException($"idle pose has no height ({min} .. {max})");
            var rest = skinner.Bounds(null, 0);
            float scale = 1f / height;
            r["height"] = Math.Round(height * Units(job), 4);
            r["nativeBounds"] = Box(min, max);
            r["restBounds"] = Box(rest.Min, rest.Max);
            r["normalize"] = new JsonObject
            {
                ["scale"] = scale,
                ["translation"] = new JsonArray(-(min.X + max.X) / 2 * scale, -min.Y * scale, -(min.Z + max.Z) / 2 * scale),
            };
            var face = skinner.Facing(idle);
            r["facing"] = new JsonArray(Math.Round(face.X, 3), Math.Round(face.Z, 3));

            // ---- When each attack connects ----
            r["hit"] = Battle.HitTimes(model, skinner, height, r);

            // Diagnostics: the baked colour of every material as the GLB gets it.
            if (diagDir != null)
                foreach (var m in model.Materials)
                {
                    var tex = mapping(m).BaseColorTexture;
                    if (tex != null && textures.TryGetValue(tex, out var png) && png.Length > 8 && png[1] == (byte)'P')
                        File.WriteAllBytes(Path.Combine(diagDir, $"{job.Id}_baked_{m.Name}.png"), png);
                }

            // ---- The GLB ----
            r["materials"] = MaterialReport(model, mapping);
            var glb = GlbWriter.Write(model, textures, mapping, null);
            File.WriteAllBytes(outPath, glb);

            // ---- Counts ----
            var used = model.Meshes.Where(m => m.Positions != null).ToList();
            r["tris"] = used.Sum(m => m.SubMeshes.Sum(s => s.Indices.Length / 3));
            r["vertices"] = used.Sum(m => m.Positions.Length);
            r["bones"] = model.Nodes.Count(n => n.SkinIndex >= 0);
            r["nodes"] = model.Nodes.Count;
            r["meshes"] = new JsonArray(used.Select(m => (JsonNode)new JsonObject
            {
                ["name"] = m.Name,
                ["tris"] = m.SubMeshes.Sum(s => s.Indices.Length / 3),
                ["materials"] = new JsonArray(m.SubMeshes.Select(s => (JsonNode)s.Material).ToArray()),
                ["skinned"] = m.Joints != null,
            }).ToArray());
            r["rawBytes"] = glb.Length;

            // ---- The shiny companion: the materials its shiny set changes, under the same names ----
            if (job.Shiny)
            {
                try { r["shiny"] = ShinyLook(job, gfFlipV, model, textures, mapping, readModel, outPath); }
                catch (Exception e) { r["shiny"] = new JsonObject { ["error"] = e.Message }; }
            }
            return r;
        }

        static JsonObject ShinyLook(Job job, bool gfFlipV, ModelData model, Dictionary<string, byte[]> textures,
            Func<MaterialData, MaterialMapping> mapping, Func<string, byte[]> readModel, string outPath)
        {
            var mine = new Shiny.Look { Materials = model.Materials, Textures = textures, Mapping = mapping };
            Shiny.Look shiny;
            if (job.Src == "bdsp")
            {
                // The shiny bundle's materials and its _rare colour textures, over the base model's meshes.
                var dir = job.ShinyDir ?? throw new InvalidDataException("no shiny folder");
                var shinyRead = Loose(dir);
                Func<string, byte[]> read = n => shinyRead(n) ?? readModel(n);
                var gm = BdspModel(dir, model, new JsonObject());
                var baked = GfMaterials.Bake(gm, read, new JsonObject(), bottomOrigin: true, null);
                shiny = new Shiny.Look
                {
                    Materials = model.Materials, Textures = baked.Textures,
                    Mapping = m => baked.Mappings.TryGetValue(m.Name, out var mm) ? mm : MaterialMapping.Default(m),
                };
            }
            else if (job.IsGf)
            {
                var gf = GfModelReader.Read(job.Folder + "_rare.gfbmdl", readModel, new JsonObject());
                var baked = GfMaterials.Bake(gf, readModel, new JsonObject(), bottomOrigin: gfFlipV, null);
                shiny = new Shiny.Look
                {
                    Materials = gf.Model.Materials, Textures = baked.Textures,
                    Mapping = m => baked.Mappings.TryGetValue(m.Name, out var mm) ? mm : MaterialMapping.Default(m),
                };
            }
            else
            {
                var sm = TrinityModelReader.Read(job.Folder + ".trmdl", readModel, shiny: true);
                if (job.Variant != null) MaterialTable.Apply(sm, Require(readModel, job.Folder + ".trmmt"), job.Variant);
                var baked = SvMaterialBaker.Bake(sm, readModel);
                Looks.RebakeGlows(sm, baked.Textures, readModel);
                shiny = new Shiny.Look { Materials = sm.Materials, Textures = baked.Textures, Mapping = m => Looks.Trinity(job, m, baked.Mappings[m.Name]) };
            }
            var used = model.Meshes.SelectMany(m => m.SubMeshes).Select(s => s.Material).Where(n => n != null);
            return Shiny.Write(mine, shiny, used, Path.Combine(Path.GetDirectoryName(outPath), job.Id + ".shiny.glb"));
        }

        static byte[] Require(Func<string, byte[]> read, string name) => read(name) ?? throw new FileNotFoundException("missing " + name);

        // SwSh/LGPE models are authored in centimetres; Trinity models in metres.
        static double Units(Job job) => job.IsGf ? GfModelReader.UnitsToMetres : job.Src == "bdsp" ? BdspUnits : 1.0;
        // FBX2glTF's metres from BDSP's Unity scene; checked against the Pokédex heights (Ledyba 1.0 m).
        const double BdspUnits = 1.0;

        static JsonObject Box(Vector3 min, Vector3 max) => new JsonObject
        {
            ["min"] = new JsonArray(Math.Round(min.X, 4), Math.Round(min.Y, 4), Math.Round(min.Z, 4)),
            ["max"] = new JsonArray(Math.Round(max.X, 4), Math.Round(max.Y, 4), Math.Round(max.Z, 4)),
        };

        static JsonArray MaterialReport(ModelData model, Func<MaterialData, MaterialMapping> mapping)
        {
            var used = model.Meshes.SelectMany(m => m.SubMeshes).Select(s => s.Material).ToHashSet();
            var list = new JsonArray();
            foreach (var m in model.Materials.Where(m => used.Contains(m.Name)))
            {
                var map = mapping(m);
                var tex = new JsonObject();
                foreach (var kv in m.Textures) tex[kv.Key] = kv.Value;
                list.Add(new JsonObject
                {
                    ["name"] = m.Name,
                    ["shader"] = m.Shader,
                    ["alphaType"] = m.AlphaType,
                    ["textures"] = tex,
                    ["base"] = map.BaseColorTexture,
                    ["normal"] = map.NormalTexture,
                    ["emissive"] = map.EmissiveTexture,
                    ["metallicRoughness"] = map.MetallicRoughnessTexture,
                    ["occlusion"] = map.OcclusionTexture,
                    ["blend"] = map.AlphaBlend,
                    ["unlit"] = map.Unlit,
                    ["doubleSided"] = map.DoubleSided,
                });
            }
            return list;
        }

        // Files of one folder by name, ignoring case and any directory part of the reference.
        // The model's materials as Sword/Shield ones, from the Unity material dumps in dir (Bdsp.cs). The UVs are
        // still V-from-the-bottom here (GltfRead), as the baker wants them.
        static GfModel BdspModel(string dir, ModelData model, JsonObject report)
        {
            var mats = Bdsp.Materials(dir, report);
            var names = model.Meshes.SelectMany(m => m.SubMeshes).Select(x => x.Material).Distinct().ToList();
            model.Materials.Clear();
            model.Materials.AddRange(names.Select(n => new MaterialData { Name = n, Shader = Bdsp.Shader }));
            var missing = names.Where(n => !mats.ContainsKey(n)).ToList();
            if (missing.Count > 0) report["bdspMaterialsMissing"] = new JsonArray(missing.Select(n => (JsonNode)n).ToArray());
            return new GfModel { Model = model, Materials = names.Select(n => mats.TryGetValue(n, out var x) ? x : new GfMaterial { Name = n }).ToList() };
        }

        public static Func<string, byte[]> Loose(string dir)
        {
            var files = new Dictionary<string, string>();
            if (dir != null && Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir)) files[Path.GetFileName(f).ToLowerInvariant()] = f;
            return name =>
            {
                if (string.IsNullOrEmpty(name)) return null;
                string key = Path.GetFileName(name.Replace('\\', '/')).ToLowerInvariant();
                return files.TryGetValue(key, out var p) ? File.ReadAllBytes(p) : null;
            };
        }
    }
}
