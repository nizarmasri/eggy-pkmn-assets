using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using PokeModelExporter.Formats;
using PokeModelExporter.Model;
using PokeModelExporter.Scarlet;

namespace Eggy.Conv
{
    // A model's material table (.trmmt, PokeDocs model/trmmt.fbs). Arceus keeps its types there: one material
    // property ("color") whose embedded curve sets layer colours per frame, a frame per type in the games'
    // type order. A type's look is the curve sampled at that type's frame, written over the materials'
    // colours before they are baked.
    public static class MaterialTable
    {
        // The games' type order (frame of each type in Arceus's colour curve).
        public static readonly string[] Types =
        {
            "normal", "fighting", "flying", "poison", "ground", "rock", "bug", "ghost", "steel",
            "fire", "water", "grass", "electric", "psychic", "ice", "dragon", "dark", "fairy",
        };

        // TRMMT { 2 material: [MMT { 0 name, 3 material_properties: [{ 0 name, 4 tracm { 0 bytes } }] }] }
        public static JsonObject Apply(ModelData model, byte[] trmmt, string variant)
        {
            int frame = Array.IndexOf(Types, variant);
            if (frame < 0) throw new ArgumentException($"no type {variant}");
            var root = FbTable.Root(trmmt);
            var sets = root.Vector(2);
            var anim = new AnimationData();
            for (int i = 0; i < sets.Length && anim.MaterialTracks.Count == 0; i++)
            {
                var props = sets.Table(i).Vector(3);
                for (int k = 0; k < props.Length; k++)
                {
                    var tr = props.Table(k).Table(4);
                    if (!tr.IsNull) TrinityCurveReader.Read(tr.Vector(0).Bytes().ToArray(), anim);
                }
            }
            if (anim.MaterialTracks.Count == 0) throw new InvalidOperationException("the material table has no colour curves");
            var set = new JsonObject();
            foreach (var t in anim.MaterialTracks)
            {
                var mat = model.Materials.FirstOrDefault(m => m.Name == t.Material);
                if (mat == null) continue;
                var old = mat.Colors.TryGetValue(t.Parameter, out var o) ? o : Vector4.One;
                var v = new Vector4(At(t.R, frame, old.X), At(t.G, frame, old.Y), At(t.B, frame, old.Z), At(t.A, frame, old.W));
                mat.Colors[t.Parameter] = v;
                set[$"{t.Material}/{t.Parameter}"] = new JsonArray(Math.Round(v.X, 3), Math.Round(v.Y, 3), Math.Round(v.Z, 3));
            }
            var first = anim.MaterialTracks[0].R;
            return new JsonObject
            {
                ["type"] = variant, ["frame"] = frame, ["colors"] = set,
                ["keyTimes"] = first?.Times != null ? string.Join(" ", first.Times.Select(x => x.ToString("0.###"))) : null,
            };
        }

        // Linear between keys, held outside them.
        static float At(Keys<float> k, float frame, float fallback)
        {
            if (k?.Times == null || k.Times.Length == 0) return fallback;
            if (frame <= k.Times[0]) return k.Values[0];
            int n = k.Times.Length;
            if (frame >= k.Times[n - 1]) return k.Values[n - 1];
            int i = 1;
            while (k.Times[i] < frame) i++;
            float u = (frame - k.Times[i - 1]) / (k.Times[i] - k.Times[i - 1]);
            return k.Values[i - 1] + (k.Values[i] - k.Values[i - 1]) * u;
        }
    }
}
