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
        // middle when the value sampled there is off the straight line between its ends, and that repeats
        // until the value is straight everywhere or the stage's triangle budget is spent.
        //
        // Splits are decided per edge by position, for a whole part (every mesh, every material) at once, and
        // every triangle on a split edge is cut there, whatever its material and whether or not it needs the
        // refinement itself. A triangle across a UV seam (same corners, other vertices) or on the far side of
        // a material boundary cuts the same edge at the same point, so no T-junction opens a crack.
        const float RefineTolerance = 0.035f;
        const int RefineLevels = 5;

        static bool NeedsRefine(Look k) => k.Baked.Count > 0;

        sealed class RefineWork
        {
            public GfMesh Src;
            public List<Vector3> P = new(), N = new();
            public List<Vector4> C = new();
            public List<Vector2>[] U = { new(), new(), new() };
            public List<int[]> Polys = new();
            public List<Look> Looks = new();
            public bool Changed;
        }

        static (long, long, long) PosKey(Vector3 p) => ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4), (long)Math.Round(p.Z * 1e4));

        static ((long, long, long), (long, long, long)) EdgeKey(Vector3 a, Vector3 b)
        {
            var ka = PosKey(a); var kb = PosKey(b);
            return ka.CompareTo(kb) <= 0 ? (ka, kb) : (kb, ka);
        }

        // The refined meshes of a part: per mesh, the mesh to emit (the source when nothing changed) and each
        // of its polys' indices, in the order of GfMesh.Polys. Dropped materials (null looks) stay as they are.
        static List<(GfMesh Mesh, int[][] Idx)> RefinePart(List<GfMesh> meshes, List<Look> looks, TexStore store, ref int budget, Dictionary<string, int> report)
        {
            var works = new List<RefineWork>();
            foreach (var src in meshes)
            {
                var w = new RefineWork { Src = src };
                for (int v = 0; v < src.P.Length; v++)
                {
                    w.P.Add(src.P[v]);
                    w.N.Add(src.N != null ? src.N[v] : Vector3.UnitY);
                    w.C.Add(src.Col != null ? src.Col[v] : Vector4.One);
                    for (int s = 0; s < 3; s++) w.U[s].Add(src.Uv[s] != null ? src.Uv[s][v] : Vector2.Zero);
                }
                foreach (var (mat, idx) in src.Polys)
                {
                    var k = mat >= 0 && mat < looks.Count ? looks[mat] : null;
                    bool ok = idx.All(v => v >= 0 && v < src.P.Length);
                    w.Polys.Add(idx);
                    w.Looks.Add(ok && k != null && !k.Drop ? k : null);
                }
                works.Add(w);
            }

            Vector4 Value(RefineWork w, Look k, Func<int, Vector2> uvOf)
            {
                var acc = new Vector4(1, 1, 1, 1);
                foreach (var (s, mode) in k.Baked)
                {
                    var uv = uvOf(w.Src.Uv[s.Set] != null ? s.Set : 0);
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
            static float Off(Vector4 a, Vector4 b, Vector4 m)
            {
                var d = Vector4.Abs(m - (a + b) / 2);
                return Math.Max(Math.Max(d.X, d.Y), Math.Max(d.Z, d.W));
            }

            for (int level = 0; level < RefineLevels && budget > 0; level++)
            {
                // Decide: an edge splits when any refined material on it bends there.
                var split = new HashSet<((long, long, long), (long, long, long))>();
                var looked = new HashSet<((long, long, long), (long, long, long), Look)>();
                foreach (var w in works)
                    for (int pi = 0; pi < w.Polys.Count; pi++)
                    {
                        var k = w.Looks[pi];
                        if (k == null || !NeedsRefine(k)) continue;
                        var tris = w.Polys[pi];
                        var cache = new Dictionary<int, Vector4>();
                        Vector4 At(int v) => cache.TryGetValue(v, out var x) ? x : cache[v] = Value(w, k, s => w.U[s][v]);
                        for (int t = 0; t + 2 < tris.Length; t += 3)
                            for (int e = 0; e < 3; e++)
                            {
                                int a = tris[t + e], b = tris[t + (e + 1) % 3];
                                var key = EdgeKey(w.P[a], w.P[b]);
                                if (split.Contains(key) || !looked.Add((key.Item1, key.Item2, k))) continue;
                                if ((w.P[a] - w.P[b]).LengthSquared() < 0.04f) continue; // under 20 cm
                                var mid = Value(w, k, s => (w.U[s][a] + w.U[s][b]) / 2);
                                if (Off(At(a), At(b), mid) > RefineTolerance) split.Add(key);
                            }
                    }
                if (split.Count == 0) break;

                // Cut: every triangle on a split edge, in every mesh and material of the part.
                int added = 0;
                foreach (var w in works)
                {
                    var mids = new Dictionary<(int, int), int>();
                    int Mid(int a, int b)
                    {
                        var key = a < b ? (a, b) : (b, a);
                        if (mids.TryGetValue(key, out int v)) return v;
                        v = w.P.Count;
                        w.P.Add((w.P[key.Item1] + w.P[key.Item2]) / 2);
                        var n = w.N[a] + w.N[b];
                        w.N.Add(n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : w.N[a]);
                        w.C.Add((w.C[a] + w.C[b]) / 2);
                        for (int s = 0; s < 3; s++) w.U[s].Add((w.U[s][a] + w.U[s][b]) / 2);
                        return mids[key] = v;
                    }
                    int M(int a, int b) => split.Contains(EdgeKey(w.P[a], w.P[b])) ? Mid(a, b) : -1;
                    for (int pi = 0; pi < w.Polys.Count; pi++)
                    {
                        if (w.Looks[pi] == null) continue;
                        var tris = w.Polys[pi];
                        var next = new List<int>(tris.Length * 2);
                        void T(int x, int y, int z) { next.Add(x); next.Add(y); next.Add(z); }
                        for (int t = 0; t + 2 < tris.Length; t += 3)
                        {
                            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                            int ab = M(a, b), bc = M(b, c), ca = M(c, a);
                            int n = (ab >= 0 ? 1 : 0) + (bc >= 0 ? 1 : 0) + (ca >= 0 ? 1 : 0);
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
                        if (next.Count != tris.Length)
                        {
                            added += (next.Count - tris.Length) / 3;
                            string name = $"{w.Src.Name}:{pi}";
                            report[name] = (report.TryGetValue(name, out int had) ? had : 0) + (next.Count - tris.Length) / 3;
                            w.Polys[pi] = next.ToArray();
                            w.Changed = true;
                        }
                    }
                }
                budget -= added;
            }

            return works.Select(w =>
            {
                if (!w.Changed) return (w.Src, w.Polys.ToArray());
                var mesh = new GfMesh
                {
                    Name = w.Src.Name,
                    P = w.P.ToArray(),
                    N = w.Src.N != null ? w.N.ToArray() : null,
                    Col = w.Src.Col != null ? w.C.ToArray() : null,
                };
                for (int s = 0; s < 3; s++) mesh.Uv[s] = w.Src.Uv[s] != null ? w.U[s].ToArray() : null;
                return (mesh, w.Polys.ToArray());
            }).ToList();
        }

        // Let's Go's far islands and rocks carry their own reflection: a mirrored copy of the mesh under the
        // sea, see-through, meant to be seen through the game's water. Eggy draws its water first and without
        // depth (so what stands in it blends over it), and the copies hang under every island as dark green
        // stripes; its water mirrors the sky itself. So in a part with a sea, see-through materials lose what
        // lies under the water: triangles wholly under it go, triangles across it are cut at it. Returns the
        // meshes as RefinePart does, and how many triangles each material lost.
        static List<(GfMesh Mesh, int[][] Idx)> ClipUnderWater(List<GfMesh> src, List<(GfMesh Mesh, int[][] Idx)> meshes, Func<int, bool> clips, float level, Dictionary<string, int> report)
        {
            var outp = new List<(GfMesh, int[][])>();
            for (int si = 0; si < meshes.Count; si++)
            {
                var (mesh, polys) = meshes[si];
                List<Vector3> P = null, N = null; List<Vector4> C = null; List<Vector2>[] U = null;
                var cuts = new Dictionary<(int, int), int>();
                int Cut(int a, int b)
                {
                    var key = a < b ? (a, b) : (b, a);
                    if (cuts.TryGetValue(key, out int v)) return v;
                    float t = (level - P[key.Item1].Y) / (P[key.Item2].Y - P[key.Item1].Y);
                    v = P.Count;
                    var p = Vector3.Lerp(P[key.Item1], P[key.Item2], t);
                    P.Add(new Vector3(p.X, level, p.Z));
                    if (N != null) { var n = Vector3.Lerp(N[key.Item1], N[key.Item2], t); N.Add(n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : N[key.Item1]); }
                    if (C != null) C.Add(Vector4.Lerp(C[key.Item1], C[key.Item2], t));
                    for (int s = 0; s < 3; s++) if (U[s] != null) U[s].Add(Vector2.Lerp(U[s][key.Item1], U[s][key.Item2], t));
                    return cuts[key] = v;
                }
                var next = (int[][])polys.Clone();
                for (int pi = 0; pi < polys.Length; pi++)
                {
                    int mat = src[si].Polys[pi].Mat;
                    var tris = polys[pi];
                    if (!clips(mat) || tris.Any(v => v < 0 || v >= (P?.Count ?? mesh.P.Length))) continue;
                    if (P == null)
                    {
                        P = mesh.P.ToList(); N = mesh.N?.ToList(); C = mesh.Col?.ToList();
                        U = mesh.Uv.Select(u => u?.ToList()).ToArray();
                    }
                    var keep = new List<int>(tris.Length);
                    int lost = 0;
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        int[] c = { tris[t], tris[t + 1], tris[t + 2] };
                        bool[] under = c.Select(v => P[v].Y < level).ToArray();
                        int n = under.Count(u => u);
                        if (n == 0) { keep.AddRange(c); continue; }
                        lost++;
                        if (n == 3) continue;
                        // Walk the corners in order (Sutherland-Hodgman against the plane), keeping the winding.
                        var poly = new List<int>(4);
                        for (int e = 0; e < 3; e++)
                        {
                            int a = c[e], b = c[(e + 1) % 3];
                            if (!under[e]) poly.Add(a);
                            if (under[e] != under[(e + 1) % 3]) poly.Add(Cut(a, b));
                        }
                        for (int k = 1; k + 1 < poly.Count; k++) { keep.Add(poly[0]); keep.Add(poly[k]); keep.Add(poly[k + 1]); }
                    }
                    if (lost == 0) continue;
                    next[pi] = keep.ToArray();
                    string name = $"{mesh.Name}:{pi}";
                    report[name] = (report.TryGetValue(name, out int had) ? had : 0) + lost;
                }
                if (P == null || P.Count == mesh.P.Length && next.SequenceEqual(polys)) { outp.Add((mesh, next)); continue; }
                var m = new GfMesh { Name = mesh.Name, P = P.ToArray(), N = N?.ToArray(), Col = C?.ToArray() };
                for (int s = 0; s < 3; s++) m.Uv[s] = U[s]?.ToArray();
                outp.Add((m, next));
            }
            return outp;
        }
    }
}
