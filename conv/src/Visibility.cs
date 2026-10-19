using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using PokeModelExporter.Export;
using PokeModelExporter.Model;

namespace Eggy.Conv
{
    // Mesh switching (the game shows and hides meshes inside a clip: Gengar's long tongue for one attack,
    // alternative eyes) carried as plain joint scale keys, which any glTF player plays:
    //   every joint a switched mesh hangs from gets a twin joint, a child of it placed at the mesh's centre,
    //   and the mesh's vertices move onto the twins (same weights; at scale 1 the twin carries them exactly
    //   as the original joint did). A clip that hides the mesh keys the twins' scale to 0, which folds the
    //   mesh into its centre; showing it keys 1 again, as a step.
    // A clip with no track for the mesh shows it as the battle idle does (or as the model's default).
    // Meshes no battle clip ever shows are left out. Run after ScaleCompensation.Apply (plain glTF nodes).
    public static class Visibility
    {
        const float Step = 0.01f; // frames between the two keys of a switch

        public static JsonObject Carry(ModelData model, IReadOnlyDictionary<string, AnimationData> clips, string idleClip)
        {
            var report = new JsonObject();
            var idle = clips[idleClip];
            var switched = new JsonArray();
            var dropped = new JsonArray();
            var rest = GlbWriter.WorldMatrices(model);
            int nextSkin = model.Nodes.Count == 0 ? 0 : model.Nodes.Max(n => n.SkinIndex) + 1;
            var nodeOfSkin = new Dictionary<int, int>();
            for (int i = 0; i < model.Nodes.Count; i++) if (model.Nodes[i].SkinIndex >= 0) nodeOfSkin[model.Nodes[i].SkinIndex] = i;

            foreach (var mesh in model.Meshes.ToList())
            {
                if (mesh.Positions == null || mesh.Joints == null) continue;
                var idleTrack = idle.VisibilityTracks.FirstOrDefault(v => v.Mesh == mesh.Name);
                bool byDefault = idleTrack?.Fixed ?? (idleTrack != null ? idleTrack.Keys.Values[0] : !mesh.Hidden);
                // Per clip: the keys (frame, shown) its visibility follows.
                var states = new Dictionary<string, List<(float Frame, bool Shown)>>();
                foreach (var kv in clips)
                {
                    var t = kv.Value.VisibilityTracks.FirstOrDefault(v => v.Mesh == mesh.Name);
                    var keys = new List<(float, bool)>();
                    if (t == null) keys.Add((0, byDefault));
                    else if (t.Fixed != null) keys.Add((0, t.Fixed.Value));
                    else for (int i = 0; i < t.Keys.Times.Length; i++) keys.Add((t.Keys.Times[i], t.Keys.Values[i]));
                    states[kv.Key] = keys;
                }
                var all = states.Values.SelectMany(k => k).ToList();
                if (all.All(k => k.Item2)) continue;           // always shown: nothing to carry
                if (all.All(k => !k.Item2))                    // never shown in battle
                {
                    model.Meshes.Remove(mesh);
                    dropped.Add(mesh.Name);
                    continue;
                }

                // The mesh's centre (model space, rest pose) and a twin per joint it hangs from.
                var centre = Vector3.Zero;
                foreach (var p in mesh.Positions) centre += p;
                centre /= mesh.Positions.Length;
                var twinOf = new Dictionary<int, int>();
                var twins = new List<string>();
                for (int i = 0; i < mesh.Joints.Length; i++)
                {
                    int j = mesh.Joints[i];
                    if (mesh.Weights[i] <= 0 || twinOf.ContainsKey(j) || !nodeOfSkin.TryGetValue(j, out int parent)) continue;
                    Matrix4x4.Invert(rest[parent], out var inv);
                    var twin = new NodeData
                    {
                        Name = $"vis_{Short(mesh.Name)}_{model.Nodes[parent].Name}",
                        Parent = parent,
                        Translation = Vector3.Transform(centre, inv),
                        SkinIndex = nextSkin++,
                    };
                    model.Nodes.Add(twin);
                    twinOf[j] = twin.SkinIndex;
                    twins.Add(twin.Name);
                }
                for (int i = 0; i < mesh.Joints.Length; i++)
                    if (mesh.Weights[i] > 0 && twinOf.TryGetValue(mesh.Joints[i], out int k)) mesh.Joints[i] = k;

                foreach (var kv in clips)
                {
                    var keys = Scale(states[kv.Key], kv.Value.FrameCount);
                    foreach (var name in twins) kv.Value.Tracks.Add(new BoneTrackData { Bone = name, Scale = keys });
                }
                mesh.Hidden = false;
                switched.Add(new JsonObject
                {
                    ["mesh"] = mesh.Name,
                    ["joints"] = twins.Count,
                    ["clips"] = new JsonObject(states.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key,
                        string.Join(" ", kv.Value.Select(s => $"{s.Frame}:{(s.Shown ? 1 : 0)}"))))),
                });
            }
            if (switched.Count > 0) report["switched"] = switched;
            if (dropped.Count > 0) report["neverShown"] = dropped;
            return report;
        }

        // Step keys: each state holds until the next one; a change is two keys a hundredth of a frame apart.
        static Keys<Vector3> Scale(List<(float Frame, bool Shown)> states, int frameCount)
        {
            var times = new List<float>();
            var values = new List<Vector3>();
            states.Sort((a, b) => a.Frame.CompareTo(b.Frame));
            Vector3 V(bool shown) => shown ? Vector3.One : Vector3.Zero;
            times.Add(0);
            values.Add(V(states[0].Shown));
            for (int i = 1; i < states.Count; i++)
            {
                if (states[i].Shown == states[i - 1].Shown) continue;
                float f = Math.Max(states[i].Frame, times[times.Count - 1] + 2 * Step);
                times.Add(f - Step);
                values.Add(V(states[i - 1].Shown));
                times.Add(f);
                values.Add(V(states[i].Shown));
            }
            float end = Math.Max(1, frameCount - 1);
            if (times[times.Count - 1] < end)
            {
                times.Add(end);
                values.Add(values[values.Count - 1]);
            }
            return new Keys<Vector3> { Times = times.ToArray(), Values = values.ToArray() };
        }

        static string Short(string mesh)
        {
            var s = mesh.Replace("_mesh_shape", "");
            int cut = s.IndexOf('_', s.IndexOf('_', s.IndexOf('_') + 1) + 1); // after pmDDDD_FF_GG
            return cut > 0 && cut + 1 < s.Length ? s.Substring(cut + 1) : s;
        }
    }
}
