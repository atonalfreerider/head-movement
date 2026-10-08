using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural icon meshes for the dance-graph node shapes of the user's move-graph file (legend confirmed 2026-10-06,
/// VIEWER_SPEC 3.10): Cylinder (standing rest), Hourglass = two cones tip to tip (holds / embraces /
/// isolations), Diamond = octahedron (steps and basics), Plus = 3D cross (open / hand-connection moves),
/// Star = spiky star (turns and spins), Tetra = tetrahedron (head movements and dips), Ball = sphere (special
/// wave moves). All fit in a unit sphere (radius 1); faceted shapes use flat normals. Built once, cached.
/// </summary>
public static class GraphShapes
{
    public static readonly string[] Names = { "Cylinder", "Hourglass", "Diamond", "Plus", "Star", "Tetra", "Ball" };

    static readonly Dictionary<string, Mesh> cache = new();

    public static int IndexOf(string shape)
    {
        for (int i = 0; i < Names.Length; i++)
        {
            if (string.Equals(Names[i], shape, System.StringComparison.OrdinalIgnoreCase)) return i;
        }

        return Names.Length - 1; // unknown -> Ball
    }

    public static Mesh Get(string shape)
    {
        string key = Names[IndexOf(shape)];
        if (cache.TryGetValue(key, out Mesh m) && m != null) return m;
        Builder b = new();
        switch (key)
        {
            case "Cylinder": b.Cylinder(0.62f, 0.8f, 20); break;
            case "Hourglass":
                b.Cone(new Vector3(0, 0.9f, 0), Vector3.zero, 0.72f, 12, true);
                b.Cone(new Vector3(0, -0.9f, 0), Vector3.zero, 0.72f, 12, true);
                break;
            case "Diamond": b.Octahedron(0.75f, 1f); break;
            case "Plus":
                b.Box(new Vector3(0.95f, 0.26f, 0.26f));
                b.Box(new Vector3(0.26f, 0.95f, 0.26f));
                b.Box(new Vector3(0.26f, 0.26f, 0.95f));
                break;
            case "Star": b.Star(0.36f, 1f, 0.17f); break;
            case "Tetra": b.Tetrahedron(1f); break;
            default: b.Sphere(0.85f, 18, 12); break;
        }

        m = b.ToMesh($"Graph {key}");
        cache[key] = m;
        return m;
    }

    class Builder
    {
        readonly List<Vector3> v = new();
        readonly List<Vector3> n = new();
        readonly List<int> t = new();

        public Mesh ToMesh(string name)
        {
            Mesh m = new() { name = name };
            m.SetVertices(v);
            m.SetNormals(n);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>flat-shaded triangle, wound so its normal points away from 'inside'</summary>
        void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 inside)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(normal, (a + b + c) / 3f - inside) < 0)
            {
                (b, c) = (c, b);
                normal = -normal;
            }

            normal.Normalize();
            int i = v.Count;
            v.Add(a);
            v.Add(b);
            v.Add(c);
            n.Add(normal);
            n.Add(normal);
            n.Add(normal);
            t.Add(i);
            t.Add(i + 1);
            t.Add(i + 2);
        }

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 inside)
        {
            Tri(a, b, c, inside);
            Tri(a, c, d, inside);
        }

        public void Cylinder(float r, float halfHeight, int sides)
        {
            Vector3 top = Vector3.up * halfHeight, bottom = -top;
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * 2 * Mathf.PI / sides, a1 = (i + 1) * 2 * Mathf.PI / sides;
                Vector3 d0 = new(Mathf.Cos(a0) * r, 0, Mathf.Sin(a0) * r), d1 = new(Mathf.Cos(a1) * r, 0, Mathf.Sin(a1) * r);
                // smooth side normals
                int k = v.Count;
                v.Add(bottom + d0);
                v.Add(top + d0);
                v.Add(top + d1);
                v.Add(bottom + d1);
                n.Add(d0.normalized);
                n.Add(d0.normalized);
                n.Add(d1.normalized);
                n.Add(d1.normalized);
                // clockwise seen from outside
                t.Add(k);
                t.Add(k + 1);
                t.Add(k + 2);
                t.Add(k);
                t.Add(k + 2);
                t.Add(k + 3);
                Tri(top, top + d0, top + d1, Vector3.zero);
                Tri(bottom, bottom + d1, bottom + d0, Vector3.zero);
            }

            FixSideWinding(sides);
        }

        void FixSideWinding(int sides)
        {
            // the smooth side quads were added with a fixed order; flip any whose face normal points inwards
            for (int q = 0; q < t.Count; q += 3)
            {
                Vector3 a = v[t[q]], b = v[t[q + 1]], c = v[t[q + 2]];
                Vector3 normal = Vector3.Cross(b - a, c - a);
                Vector3 centre = (a + b + c) / 3f;
                Vector3 outward = new(centre.x, 0, centre.z);
                if (Mathf.Abs(normal.normalized.y) > 0.5f) continue; // caps are already oriented
                if (Vector3.Dot(normal, outward) < 0) (t[q + 1], t[q + 2]) = (t[q + 2], t[q + 1]);
            }
        }

        /// <summary>cone from a base centre (radius r) to a tip; closed base</summary>
        public void Cone(Vector3 baseCentre, Vector3 tip, float r, int sides, bool closed)
        {
            Vector3 axis = (tip - baseCentre).normalized;
            Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 w = Vector3.Cross(axis, u);
            Vector3 inside = Vector3.Lerp(baseCentre, tip, 0.3f);
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * 2 * Mathf.PI / sides, a1 = (i + 1) * 2 * Mathf.PI / sides;
                Vector3 p0 = baseCentre + (u * Mathf.Cos(a0) + w * Mathf.Sin(a0)) * r;
                Vector3 p1 = baseCentre + (u * Mathf.Cos(a1) + w * Mathf.Sin(a1)) * r;
                Tri(tip, p0, p1, inside);
                if (closed) Tri(baseCentre, p1, p0, inside);
            }
        }

        public void Octahedron(float r, float h)
        {
            Vector3[] eq = { new(r, 0, 0), new(0, 0, r), new(-r, 0, 0), new(0, 0, -r) };
            Vector3 top = Vector3.up * h, bottom = Vector3.down * h;
            for (int i = 0; i < 4; i++)
            {
                Tri(top, eq[i], eq[(i + 1) % 4], Vector3.zero);
                Tri(bottom, eq[i], eq[(i + 1) % 4], Vector3.zero);
            }
        }

        public void Box(Vector3 half)
        {
            Vector3 C(int sx, int sy, int sz) => new(sx * half.x, sy * half.y, sz * half.z);
            Quad(C(-1, -1, -1), C(-1, 1, -1), C(1, 1, -1), C(1, -1, -1), Vector3.zero);
            Quad(C(-1, -1, 1), C(1, -1, 1), C(1, 1, 1), C(-1, 1, 1), Vector3.zero);
            Quad(C(-1, -1, -1), C(-1, -1, 1), C(-1, 1, 1), C(-1, 1, -1), Vector3.zero);
            Quad(C(1, -1, -1), C(1, 1, -1), C(1, 1, 1), C(1, -1, 1), Vector3.zero);
            Quad(C(-1, 1, -1), C(-1, 1, 1), C(1, 1, 1), C(1, 1, -1), Vector3.zero);
            Quad(C(-1, -1, -1), C(1, -1, -1), C(1, -1, 1), C(-1, -1, 1), Vector3.zero);
        }

        public void Tetrahedron(float r)
        {
            Vector3 a = new Vector3(1, 1, 1).normalized * r, b = new Vector3(1, -1, -1).normalized * r;
            Vector3 c = new Vector3(-1, 1, -1).normalized * r, d = new Vector3(-1, -1, 1).normalized * r;
            Tri(a, b, c, Vector3.zero);
            Tri(a, c, d, Vector3.zero);
            Tri(a, d, b, Vector3.zero);
            Tri(b, d, c, Vector3.zero);
        }

        /// <summary>small core octahedron with 14 spikes (6 axes + 8 diagonals)</summary>
        public void Star(float core, float spike, float spikeRadius)
        {
            Octahedron(core, core);
            List<Vector3> dirs = new()
            {
                Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back
            };
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    for (int sz = -1; sz <= 1; sz += 2) dirs.Add(new Vector3(sx, sy, sz).normalized);
                }
            }

            foreach (Vector3 d in dirs)
            {
                float len = d.y != 0 && Mathf.Abs(d.y) > 0.99f ? spike : spike * 0.82f;
                Cone(d * (core * 0.55f), d * len, spikeRadius, 4, false);
            }
        }

        public void Sphere(float r, int slices, int stacks)
        {
            int first = v.Count;
            for (int i = 0; i <= stacks; i++)
            {
                float phi = Mathf.PI * i / stacks;
                for (int j = 0; j <= slices; j++)
                {
                    float theta = 2 * Mathf.PI * j / slices;
                    Vector3 d = new(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta));
                    v.Add(d * r);
                    n.Add(d);
                }
            }

            for (int i = 0; i < stacks; i++)
            {
                for (int j = 0; j < slices; j++)
                {
                    int a = first + i * (slices + 1) + j, b = a + slices + 1;
                    AddOutward(a, b, a + 1);
                    AddOutward(a + 1, b, b + 1);
                }
            }
        }

        void AddOutward(int a, int b, int c)
        {
            Vector3 normal = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
            if (normal.sqrMagnitude < 1e-12f) return; // degenerate at the poles
            if (Vector3.Dot(normal, v[a] + v[b] + v[c]) < 0) (b, c) = (c, b);
            t.Add(a);
            t.Add(b);
            t.Add(c);
        }
    }
}
