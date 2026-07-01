using System;
using System.Collections.Generic;
using SharpDX;
using CodeWalker.GameFiles;

namespace App.Geometry;

/// <summary>One renderable piece of a collision bound: flat-shaded triangles with a palette
/// colour (so composite parts are distinguishable when vertex colours are on).</summary>
public sealed class BoundsSubMesh
{
    public float[] Positions = Array.Empty<float>();   // 3 floats / vertex (3 verts / triangle, non-shared)
    public float[] Normals = Array.Empty<float>();     // 3 floats / vertex (flat face normals)
    public float[] Colors = Array.Empty<float>();      // 4 floats / vertex (RGBA 0..1)
    public uint[] Indices = Array.Empty<uint>();       // sequential
    public int VertexCount;
    public string Material = "";
    public Vector3 Min, Max;
}

/// <summary>
/// Turns a GTA collision bound (<c>.ybn</c> / fragment physics) into renderable triangle meshes
/// so it can open in the 3D viewer instead of only as XML. Walks the bound tree (composite →
/// children, applying each child's transform), emitting triangles for geometry/BVH bounds (and
/// box polygons), and a box for primitive bounds (sphere/capsule/etc. as their AABB). Reuses
/// CodeWalker's <c>BoundGeometry.GetVertexPos</c> so the vertex maths matches the engine.
/// </summary>
public static class BoundsMesh
{
    private const int MaxTriangles = 2_000_000;   // guard against giant world-collision files

    public static List<BoundsSubMesh> Build(Bounds? root)
    {
        var outp = new List<BoundsSubMesh>();
        if (root != null) { int tris = 0; Walk(root, Xf.Identity, outp, ref tris); }
        return outp;
    }

    private static void Walk(Bounds b, Xf t, List<BoundsSubMesh> outp, ref int tris)
    {
        if (tris >= MaxTriangles) return;
        switch (b)
        {
            case BoundComposite comp:
                var kids = comp.Children?.data_items;
                var tfs = comp.ChildrenTransformation1 ?? comp.ChildrenTransformation2;
                if (kids != null)
                    for (int i = 0; i < kids.Length; i++)
                    {
                        if (kids[i] == null) continue;
                        Xf ct = (tfs != null && i < tfs.Length) ? Xf.From(tfs[i]) : Xf.Identity;
                        Walk(kids[i], Xf.Compose(t, ct), outp, ref tris);
                        if (tris >= MaxTriangles) break;
                    }
                break;
            case BoundGeometry geom:   // includes BoundBVH
                EmitGeometry(geom, t, outp, ref tris);
                break;
            default:                   // primitive bound — show its AABB
                EmitAabbBox(b.BoxMin, b.BoxMax, t, b.Type.ToString(), outp, ref tris);
                break;
        }
    }

    private static void EmitGeometry(BoundGeometry g, Xf t, List<BoundsSubMesh> outp, ref int tris)
    {
        if (g.Polygons == null || g.Vertices == null) { EmitAabbBox(g.BoxMin, g.BoxMax, t, g.Type.ToString(), outp, ref tris); return; }
        var pos = new List<float>();
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        Vector3 V(int i) => t.Point(g.GetVertexPos(i));

        foreach (var p in g.Polygons)
        {
            if (tris >= MaxTriangles) break;
            switch (p)
            {
                case BoundPolygonTriangle tri:
                    AddTri(pos, V(tri.vertIndex1), V(tri.vertIndex2), V(tri.vertIndex3), ref mn, ref mx); tris++;
                    break;
                case BoundPolygonBox box:
                    EmitBoxPoly(g, t, box, pos, ref mn, ref mx); tris += 12;
                    break;
                // sphere / capsule / cylinder polygons are primitive volumes — skipped for v1
            }
        }
        if (pos.Count > 0) outp.Add(Finish(pos, mn, mx, g.Type.ToString(), outp.Count));
    }

    // A box polygon: corner p1 with edge vectors a1/a2/a3 (same maths as CodeWalker's collision).
    private static void EmitBoxPoly(BoundGeometry g, Xf t, BoundPolygonBox box, List<float> pos, ref Vector3 mn, ref Vector3 mx)
    {
        Vector3 p1 = g.GetVertexPos(box.boxIndex1), p2 = g.GetVertexPos(box.boxIndex2),
                p3 = g.GetVertexPos(box.boxIndex3), p4 = g.GetVertexPos(box.boxIndex4);
        Vector3 a1 = ((p3 + p4) - (p1 + p2)) * 0.5f, a2 = p3 - (p1 + a1), a3 = p4 - (p1 + a1);
        Vector3 C(int i, int j, int k) => t.Point(p1 + a1 * i + a2 * j + a3 * k);
        BoxTris(C(0, 0, 0), C(1, 0, 0), C(0, 1, 0), C(1, 1, 0), C(0, 0, 1), C(1, 0, 1), C(0, 1, 1), C(1, 1, 1), pos, ref mn, ref mx);
    }

    private static void EmitAabbBox(Vector3 lo, Vector3 hi, Xf t, string mat, List<BoundsSubMesh> outp, ref int tris)
    {
        if (lo == hi) return;
        var pos = new List<float>();
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        Vector3 C(float x, float y, float z) => t.Point(new Vector3(x, y, z));
        BoxTris(C(lo.X, lo.Y, lo.Z), C(hi.X, lo.Y, lo.Z), C(lo.X, hi.Y, lo.Z), C(hi.X, hi.Y, lo.Z),
                C(lo.X, lo.Y, hi.Z), C(hi.X, lo.Y, hi.Z), C(lo.X, hi.Y, hi.Z), C(hi.X, hi.Y, hi.Z), pos, ref mn, ref mx);
        tris += 12;
        if (pos.Count > 0) outp.Add(Finish(pos, mn, mx, mat, outp.Count));
    }

    // 8 corners (c{i}{j}{k}) -> 12 triangles (6 quads).
    private static void BoxTris(Vector3 c000, Vector3 c100, Vector3 c010, Vector3 c110,
                                Vector3 c001, Vector3 c101, Vector3 c011, Vector3 c111,
                                List<float> pos, ref Vector3 mn, ref Vector3 mx)
    {
        Quad(c000, c100, c110, c010, pos, ref mn, ref mx);  // -Z
        Quad(c001, c011, c111, c101, pos, ref mn, ref mx);  // +Z
        Quad(c000, c010, c011, c001, pos, ref mn, ref mx);  // -X
        Quad(c100, c101, c111, c110, pos, ref mn, ref mx);  // +X
        Quad(c000, c001, c101, c100, pos, ref mn, ref mx);  // -Y
        Quad(c010, c110, c111, c011, pos, ref mn, ref mx);  // +Y
    }

    private static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, List<float> pos, ref Vector3 mn, ref Vector3 mx)
    {
        AddTri(pos, a, b, c, ref mn, ref mx);
        AddTri(pos, a, c, d, ref mn, ref mx);
    }

    private static void AddTri(List<float> pos, Vector3 a, Vector3 b, Vector3 c, ref Vector3 mn, ref Vector3 mx)
    {
        Push(pos, a, ref mn, ref mx); Push(pos, b, ref mn, ref mx); Push(pos, c, ref mn, ref mx);
    }

    private static void Push(List<float> pos, Vector3 v, ref Vector3 mn, ref Vector3 mx)
    {
        pos.Add(v.X); pos.Add(v.Y); pos.Add(v.Z);
        mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v);
    }

    private static BoundsSubMesh Finish(List<float> pos, Vector3 mn, Vector3 mx, string mat, int colorIdx)
    {
        var positions = pos.ToArray();
        int vcount = positions.Length / 3;
        var normals = new float[positions.Length];
        var indices = new uint[vcount];
        var colors = new float[vcount * 4];
        var (cr, cg, cb) = Palette(colorIdx);

        for (int tri = 0; tri + 3 <= vcount; tri += 3)
        {
            Vector3 a = At(positions, tri), b = At(positions, tri + 1), c = At(positions, tri + 2);
            Vector3 n = Vector3.Cross(b - a, c - a);
            float len = n.Length();
            n = len > 1e-12f ? n / len : Vector3.UnitZ;
            for (int k = 0; k < 3; k++) { int vi = tri + k; normals[vi * 3] = n.X; normals[vi * 3 + 1] = n.Y; normals[vi * 3 + 2] = n.Z; }
        }
        for (int i = 0; i < vcount; i++)
        {
            indices[i] = (uint)i;
            colors[i * 4] = cr; colors[i * 4 + 1] = cg; colors[i * 4 + 2] = cb; colors[i * 4 + 3] = 1f;
        }
        return new BoundsSubMesh { Positions = positions, Normals = normals, Colors = colors, Indices = indices, VertexCount = vcount, Material = mat, Min = mn, Max = mx };
    }

    private static Vector3 At(float[] a, int v) => new(a[v * 3], a[v * 3 + 1], a[v * 3 + 2]);

    private static (float, float, float) Palette(int i)
    {
        var p = new (float, float, float)[]
        {
            (0.55f, 0.78f, 0.95f), (0.95f, 0.62f, 0.50f), (0.60f, 0.88f, 0.60f),
            (0.92f, 0.85f, 0.50f), (0.80f, 0.62f, 0.92f), (0.50f, 0.86f, 0.86f),
        };
        return p[((i % p.Length) + p.Length) % p.Length];
    }

    // Column-major transform: world = C1*x + C2*y + C3*z + C4 (matches Matrix4F_s columns).
    private readonly struct Xf
    {
        public readonly Vector3 C1, C2, C3, C4;
        private Xf(Vector3 c1, Vector3 c2, Vector3 c3, Vector3 c4) { C1 = c1; C2 = c2; C3 = c3; C4 = c4; }
        public static Xf Identity => new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Zero);
        public static Xf From(Matrix4F_s m) => new(m.Column1, m.Column2, m.Column3, m.Column4);
        public Vector3 Point(Vector3 p) => C1 * p.X + C2 * p.Y + C3 * p.Z + C4;
        public Vector3 Dir(Vector3 d) => C1 * d.X + C2 * d.Y + C3 * d.Z;
        // Apply child m in parent t's space: result.Point(p) == t.Point(m.Point(p)).
        public static Xf Compose(Xf t, Xf m) => new(t.Dir(m.C1), t.Dir(m.C2), t.Dir(m.C3), t.Point(m.C4));
    }
}
