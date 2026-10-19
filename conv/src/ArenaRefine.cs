using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Eggy.Conv
{
    public static partial class Arenas
    {
        // Masks and second textures the stage shaders multiply in on their own UV set (wall and ground shading
        // masks, height fog, the sea's rock fade) are baked into vertex colours, and a low-poly wall has too
        // few vertices to show them: one long triangle smears a mask across a whole wall. Here such
        // submeshes are refined where the baked value bends between two vertices: an edge is split at its
        // middle when the value sampled there is off the straight line between its ends, every triangle is
        // cut along the edges that split (so neighbours share their new vertices and nothing cracks), and
        // that repeats until the value is straight everywhere or the stage's triangle budget is spent.
        const float RefineTolerance = 0.035f;
        const int RefineLevels = 5;

        static bool NeedsRefine(Look k) => k.Baked.Count > 0;

        static (GfMesh Mesh, int[] Idx) Refine(GfMesh src, int[] idx, Look k, TexStore store, ref int budget)
        {
            if (!NeedsRefine(k) || budget <= 0) return (src, idx);
            // A working copy holding only this submesh's vertices.
            var map = new Dictionary<int, int>();
            var P = new List<Vector3>(); var N = new List<Vector3>(); var C = new List<Vector4>();
            var U = new List<Vector2>[3] { new(), new(), new() };
            int Add(int v)
            {
                if (map.TryGetValue(v, out int nv)) return nv;
                nv = P.Count;
                map[v] = nv;
                P.Add(src.P[v]);
                N.Add(src.N != null ? src.N[v] : Vector3.UnitY);
                C.Add(src.Col != null ? src.Col[v] : Vector4.One);
                for (int s = 0; s < 3; s++) U[s].Add(src.Uv[s] != null ? src.Uv[s][v] : Vector2.Zero);
                return nv;
            }
            var tris = new List<int>();
            foreach (int v in idx)
            {
                if (v < 0 || v >= src.P.Length) return (src, idx);
                tris.Add(Add(v));
            }

            Vector4 Value(int v)
            {
                var acc = new Vector4(1, 1, 1, 1);
                foreach (var (s, mode) in k.Baked)
                {
                    var uv = src.Uv[s.Set] != null ? U[s.Set][v] : U[0][v];
                    var t = Sample(store.Get(s.Tex), Transform(uv, s), s.WrapU, s.WrapV);
                    acc = mode switch
                    {
                        "alpha" => new Vector4(acc.X, acc.Y, acc.Z, acc.W * t.W),
                        "multiply" => new Vector4(acc.X * t.X, acc.Y * t.Y, acc.Z * t.Z, acc.W),
                        "add" => new Vector4(Math.Min(1, acc.X + t.X), Math.Min(1, acc.Y + t.Y), Math.Min(1, acc.Z + t.Z), acc.W),
                        "screen" => new Vector4(1 - (1 - acc.X) * (1 - t.X), 1 - (1 - acc.Y) * (1 - t.Y), 1 - (1 - acc.Z) * (1 - t.Z), acc.W),
                        _ => Vector4.Lerp(acc, new Vector4(t.X, t.Y, t.Z, acc.W), t.W),
                    };
                }
                return acc;
            }
            var values = new List<Vector4>();
            for (int v = 0; v < P.Count; v++) values.Add(Value(v));
            int Mid(int a, int b)
            {
                int v = P.Count;
                P.Add((P[a] + P[b]) / 2);
                var n = N[a] + N[b];
                N.Add(n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : N[a]);
                C.Add((C[a] + C[b]) / 2);
                for (int s = 0; s < 3; s++) U[s].Add((U[s][a] + U[s][b]) / 2);
                values.Add(Value(v));
                return v;
            }
            static float Off(Vector4 a, Vector4 b, Vector4 m)
            {
                var d = Vector4.Abs(m - (a + b) / 2);
                return Math.Max(Math.Max(d.X, d.Y), Math.Max(d.Z, d.W));
            }

            int start = tris.Count / 3;
            for (int level = 0; level < RefineLevels && budget > 0; level++)
            {
                var split = new Dictionary<(int, int), int>();
                bool any = false;
                // Decide per edge, once, so both triangles on an edge cut it the same way.
                for (int t = 0; t < tris.Count; t += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int a = tris[t + e], b = tris[t + (e + 1) % 3];
                        var key = a < b ? (a, b) : (b, a);
                        if (split.ContainsKey(key)) continue;
                        if ((P[a] - P[b]).LengthSquared() < 0.04f) { split[key] = -1; continue; } // under 20 cm
                        int m = Mid(a, b);
                        if (Off(values[a], values[b], values[m]) > RefineTolerance) { split[key] = m; any = true; }
                        else
                        {
                            // Not needed: forget the probe vertex (it is the last one added).
                            P.RemoveAt(m); N.RemoveAt(m); C.RemoveAt(m); values.RemoveAt(m);
                            for (int s = 0; s < 3; s++) U[s].RemoveAt(m);
                            split[key] = -1;
                        }
                    }
                if (!any) break;
                var next = new List<int>(tris.Count * 2);
                int M(int a, int b) => split[a < b ? (a, b) : (b, a)];
                for (int t = 0; t < tris.Count; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    int ab = M(a, b), bc = M(b, c), ca = M(c, a);
                    int n = (ab >= 0 ? 1 : 0) + (bc >= 0 ? 1 : 0) + (ca >= 0 ? 1 : 0);
                    void T(int x, int y, int z) { next.Add(x); next.Add(y); next.Add(z); }
                    if (n == 0) T(a, b, c);
                    else if (n == 3) { T(a, ab, ca); T(ab, b, bc); T(ca, bc, c); T(ab, bc, ca); }
                    else if (n == 1)
                    {
                        if (ab >= 0) { T(a, ab, c); T(ab, b, c); }
                        else if (bc >= 0) { T(a, b, bc); T(a, bc, c); }
                        else { T(a, b, ca); T(ca, b, c); }
                    }
                    else
                    {
                        // Two edges: the corner between them gets its own triangle, the rest a quad split in two.
                        if (ab < 0) { T(c, ca, bc); T(ca, a, b); T(ca, b, bc); }
                        else if (bc < 0) { T(a, ab, ca); T(ab, b, c); T(ab, c, ca); }
                        else { T(b, bc, ab); T(ab, bc, c); T(ab, c, a); }
                    }
                }
                budget -= (next.Count - tris.Count) / 3;
                tris = next;
            }
            if (tris.Count / 3 == start) return (src, idx);
            var outMesh = new GfMesh
            {
                Name = src.Name,
                P = P.ToArray(),
                N = src.N != null ? N.ToArray() : null,
                Col = src.Col != null ? C.ToArray() : null,
            };
            for (int s = 0; s < 3; s++) outMesh.Uv[s] = src.Uv[s] != null ? U[s].ToArray() : null;
            return (outMesh, tris.ToArray());
        }
    }
}
