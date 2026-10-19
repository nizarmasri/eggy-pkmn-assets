using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using PokeModelExporter.Export;
using PokeModelExporter.Model;

namespace Eggy.Conv
{
    // Poses the model the way a glTF viewer will (node TRS from the clip's keys, linear / slerp between
    // them, rest pose where a clip has no key) and skins its vertices on the CPU, to measure it.
    // Use after ScaleCompensation.Apply: from then on the node list has plain glTF semantics.
    public sealed class Skinner
    {
        readonly ModelData _model;
        readonly int[] _jointNode;
        readonly Matrix4x4[] _inverseBind;

        public Skinner(ModelData model)
        {
            _model = model;
            var rest = GlbWriter.WorldMatrices(model);
            int maxJoint = model.Nodes.Count == 0 ? -1 : model.Nodes.Max(n => n.SkinIndex);
            _jointNode = new int[maxJoint + 1];
            for (int i = 0; i < model.Nodes.Count; i++)
                if (model.Nodes[i].SkinIndex >= 0) _jointNode[model.Nodes[i].SkinIndex] = i;
            _inverseBind = new Matrix4x4[maxJoint + 1];
            for (int k = 0; k <= maxJoint; k++) Matrix4x4.Invert(rest[_jointNode[k]], out _inverseBind[k]);
        }

        public Matrix4x4[] World(AnimationData anim, float frame)
        {
            var tracks = new Dictionary<string, BoneTrackData>();
            if (anim != null)
                foreach (var t in anim.Tracks)
                    if (t.Bone != null && !tracks.ContainsKey(t.Bone)) tracks[t.Bone] = t;
            var nodes = _model.Nodes;
            var world = new Matrix4x4[nodes.Count];
            var done = new bool[nodes.Count];
            Matrix4x4 Get(int i, int depth)
            {
                if (done[i]) return world[i];
                var n = nodes[i];
                Vector3 t = n.Translation, s = n.Scale;
                Quaternion r = n.Rotation;
                if (n.Name != null && tracks.TryGetValue(n.Name, out var track))
                {
                    if (track.Translation != null) t = Sample(track.Translation, frame, Vector3.Lerp);
                    if (track.Rotation != null) r = Sample(track.Rotation, frame, Quaternion.Slerp);
                    if (track.Scale != null) s = Sample(track.Scale, frame, Vector3.Lerp);
                }
                var local = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(r)) * Matrix4x4.CreateTranslation(t);
                world[i] = n.Parent >= 0 && n.Parent < nodes.Count && n.Parent != i && depth < 512 ? local * Get(n.Parent, depth + 1) : local;
                done[i] = true;
                return world[i];
            }
            for (int i = 0; i < nodes.Count; i++) Get(i, 0);
            return world;
        }

        // The skinned vertices of one frame (every step-th vertex), as an axis-aligned box.
        public (Vector3 Min, Vector3 Max) Bounds(AnimationData anim, float frame, int step = 1)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in Points(anim, frame, step, null))
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            return (min, max);
        }

        IEnumerable<Vector3> Points(AnimationData anim, float frame, int step, Func<MeshData, bool> meshFilter)
        {
            var world = World(anim, frame);
            var skin = new Matrix4x4[_inverseBind.Length];
            for (int k = 0; k < skin.Length; k++) skin[k] = _inverseBind[k] * world[_jointNode[k]];
            foreach (var mesh in _model.Meshes)
            {
                if (mesh.Positions == null || (meshFilter != null && !meshFilter(mesh))) continue;
                bool skinned = mesh.Joints != null && mesh.Weights != null && skin.Length > 0;
                for (int v = 0; v < mesh.Positions.Length; v += step)
                {
                    var pos = mesh.Positions[v];
                    if (!skinned) { yield return pos; continue; }
                    var p = Vector3.Zero;
                    float sum = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        float w = mesh.Weights[v * 4 + i];
                        int j = mesh.Joints[v * 4 + i];
                        if (w <= 0 || j < 0 || j >= skin.Length) continue;
                        p += Vector3.Transform(pos, skin[j]) * w;
                        sum += w;
                    }
                    yield return sum > 0 ? p / sum : pos;
                }
            }
        }

        // The box the idle loop sweeps (sampled at up to 24 frames).
        public (Vector3 Min, Vector3 Max) Envelope(AnimationData idle)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            int frames = Math.Max(1, idle.FrameCount);
            int every = Math.Max(1, frames / 24);
            for (int f = 0; f < frames; f += every)
            {
                var (a, b) = Bounds(idle, f);
                min = Vector3.Min(min, a);
                max = Vector3.Max(max, b);
            }
            return (min, max);
        }

        // Which way the face looks, on the ground plane: from the middle of the body to the eyes
        // (meshes or materials named "eye"). Zero when the model has none.
        public Vector3 Facing(AnimationData idle)
        {
            bool IsEye(MeshData m) => m.Name.IndexOf("eye", StringComparison.OrdinalIgnoreCase) >= 0
                || m.SubMeshes.Any(s => s.Material != null && s.Material.IndexOf("eye", StringComparison.OrdinalIgnoreCase) >= 0);
            var eyes = Points(idle, 0, 1, IsEye).ToList();
            if (eyes.Count == 0) return Vector3.Zero;
            var (min, max) = Bounds(idle, 0);
            var centre = (min + max) / 2;
            var e = eyes.Aggregate(Vector3.Zero, (acc, p) => acc + p) / eyes.Count;
            var d = new Vector3(e.X - centre.X, 0, e.Z - centre.Z);
            return d.Length() > 1e-6f ? Vector3.Normalize(d) : Vector3.Zero;
        }

        // Linear between keys, held before the first and after the last (glTF LINEAR sampling).
        public static T Sample<T>(Keys<T> keys, float frame, Func<T, T, float, T> lerp)
        {
            var t = keys.Times;
            if (t.Length == 1 || frame <= t[0]) return keys.Values[0];
            if (frame >= t[t.Length - 1]) return keys.Values[t.Length - 1];
            int i = Array.BinarySearch(t, frame);
            if (i >= 0) return keys.Values[i];
            i = ~i;
            float u = (frame - t[i - 1]) / (t[i] - t[i - 1]);
            return lerp(keys.Values[i - 1], keys.Values[i], u);
        }
    }
}
