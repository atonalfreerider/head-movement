using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pure geometry for the head-worn props of an avatar (Assets/Avatar/HeadProps.cs; VIEWER_SPEC 3.2b): no scene access, no
/// allocations per frame - everything here runs once when a capture loads.
///
/// The props are laid on the avatar's ACTUAL head: the skin the viewer loaded (after the take's face-shape edit), cut to the
/// head and upper neck (<see cref="HeadSurface"/>, rest pose, metres, relative to the head joint, so the geometry can be a rigid
/// child of the head bone). Anchors are SMPL-X topology landmarks (vertex ids, so they follow the edited mesh): the outline of
/// the ear and the mouth corner of the prop's side. The headset boom is a curve that FOLLOWS THE SURFACE between the ear and the
/// mouth corner - rays from the middle of the head through the chord, hit point + the surface normal times a standoff - then
/// smoothed and pushed out until every sample clears the skin, so it never sinks into the cheekbone and never floats off the
/// face; the capsule is checked the same way.
///
/// Left / right is the dancer's OWN side. Head-local axes: +x = the dancer's left, +y up, -z forward (Unity mirror of SMPL-X).
/// </summary>
public static class HeadPropGeometry
{
    public const int SmplxVertexCount = 10475;
    /// <summary>SMPL-X vertices from here on are the two eyeballs (inside the head: never a skin surface)</summary>
    const int FirstEyeballVertex = 9383;

    // SMPL-X vertex ids (topology landmarks; the FLAME ear masks and the iBUG-68 mouth corners 48 / 54 of the SMPL-X model). The ear
    // outline runs from the front of the ear over its top to the back (angles 45..150 deg about the ear centre in the side view).
    public static readonly int[] EarRimLeft = { 213, 214, 166, 305, 186, 450, 61, 203 };
    public static readonly int[] EarRimRight = { 920, 921, 845, 1100, 870, 1315, 611, 900 };
    static readonly int[] MouthLeftIds = { 1730, 1578, 1579 };
    static readonly float[] MouthLeftWeights = { 0.47118f, 0.02309f, 0.50573f };
    static readonly int[] MouthRightIds = { 2845, 2715, 2714 };
    static readonly float[] MouthRightWeights = { 0.47118f, 0.50573f, 0.02309f };
    const int EarVertexLeft = 6, EarVertexRight = 616;

    /// <summary>the head and upper neck of a skin in the rest pose, head-joint-local metres</summary>
    public sealed class HeadSurface
    {
        public Vector3[] Pos;
        public Vector3[] Nrm; // outward, unit
        public int[] Tris;    // region triangles (indices into Pos)
        /// <summary>a point inside the head the surface is star-shaped around (for the cheek: midline, a little above the head joint)</summary>
        public Vector3 Centre;
        public Vector3 HeadJoint;
        readonly Dictionary<int, int> byId = new();

        public bool TryVertex(int smplxId, out Vector3 p, out Vector3 n)
        {
            if (byId.TryGetValue(smplxId, out int i))
            {
                p = Pos[i];
                n = Nrm[i];
                return true;
            }

            p = n = Vector3.zero;
            return false;
        }

        /// <param name="skin">the avatar's skin (SmplxData.ReadSkin): 10475 SMPL-X vertices, or a seam-split skin with vertex_ids</param>
        /// <param name="belowHeadM">the region reaches this far below the head joint (the jaw line)</param>
        public static HeadSurface Build(SmplxData.Skin skin, int headJoint, float belowHeadM = 0.07f)
        {
            int n = skin.RestVertices.Length;
            int[] ids = skin.VertexIds;
            if (ids == null && n != SmplxVertexCount) return null; // not the SMPL-X topology: no landmarks
            HeadSurface s = new() { HeadJoint = skin.RestJoints[headJoint] };
            int[] map = new int[n];
            List<Vector3> pos = new(n / 2);
            List<int> regionId = new(n / 2);
            for (int v = 0; v < n; v++)
            {
                Vector3 p = skin.RestVertices[v] - s.HeadJoint;
                int id = ids != null ? ids[v] : v;
                if (p.y >= -belowHeadM && id < FirstEyeballVertex)
                {
                    map[v] = pos.Count;
                    pos.Add(p);
                    regionId.Add(id);
                }
                else map[v] = -1;
            }

            List<int> tris = new(pos.Count * 4);
            int[] t = skin.Triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = map[t[i]], b = map[t[i + 1]], c = map[t[i + 2]];
                if (a < 0 || b < 0 || c < 0) continue;
                tris.Add(a);
                tris.Add(b);
                tris.Add(c);
            }

            s.Pos = pos.ToArray();
            s.Tris = tris.ToArray();
            for (int i = 0; i < regionId.Count; i++) s.byId.TryAdd(regionId[i], i);

            // the point inside the head: the midline between the ears, a little above the head joint, mid-depth of the skull
            float midX = 0f;
            if (s.byId.TryGetValue(EarVertexLeft, out int el) && s.byId.TryGetValue(EarVertexRight, out int er)) midX = 0.5f * (s.Pos[el].x + s.Pos[er].x);
            float zMin = float.MaxValue, zMax = float.MinValue;
            foreach (Vector3 p in s.Pos)
            {
                if (p.y < 0f) continue;
                zMin = Mathf.Min(zMin, p.z);
                zMax = Mathf.Max(zMax, p.z);
            }

            s.Centre = new Vector3(midX, 0.02f, zMin <= zMax ? 0.5f * (zMin + zMax) : 0f);

            // area-weighted normals, turned outward (the sign of the winding convention is not assumed)
            Vector3[] nrm = new Vector3[s.Pos.Length];
            for (int i = 0; i < s.Tris.Length; i += 3)
            {
                int a = s.Tris[i], b = s.Tris[i + 1], c = s.Tris[i + 2];
                // in square millimetres: Vector3.normalized returns zero below 1e-5, which a ~1 mm triangle's cross product (1e-6 m^2) is
                Vector3 fn = Vector3.Cross(s.Pos[b] - s.Pos[a], s.Pos[c] - s.Pos[a]) * 1e6f;
                nrm[a] += fn;
                nrm[b] += fn;
                nrm[c] += fn;
            }

            float outward = 0f;
            for (int i = 0; i < nrm.Length; i++) outward += Vector3.Dot(nrm[i], s.Pos[i] - s.Centre);
            if (outward < 0f)
            {
                for (int i = 0; i < nrm.Length; i++) nrm[i] = -nrm[i];
            }

            for (int i = 0; i < nrm.Length; i++)
            {
                nrm[i] = nrm[i].sqrMagnitude > 1e-6f ? nrm[i].normalized : (s.Pos[i] - s.Centre).normalized;
            }

            s.Nrm = nrm;
            return s;
        }

        Vector3 NormalAt(int tri, float w0, float w1, float w2)
        {
            Vector3 n = Nrm[Tris[tri]] * w0 + Nrm[Tris[tri + 1]] * w1 + Nrm[Tris[tri + 2]] * w2;
            return n.sqrMagnitude > 1e-12f ? n.normalized : Nrm[Tris[tri]];
        }

        /// <summary>the OUTERMOST surface point along a ray from inside the head (the ear's pocket and the nostrils are not stopped at)</summary>
        public bool RayOuter(Vector3 origin, Vector3 dir, out Vector3 hit, out Vector3 normal)
        {
            float best = 0f;
            int bestTri = -1;
            float bw0 = 0, bw1 = 0, bw2 = 0;
            for (int i = 0; i < Tris.Length; i += 3)
            {
                Vector3 a = Pos[Tris[i]], b = Pos[Tris[i + 1]], c = Pos[Tris[i + 2]];
                Vector3 e1 = b - a, e2 = c - a;
                Vector3 p = Vector3.Cross(dir, e2);
                float det = Vector3.Dot(e1, p);
                if (Mathf.Abs(det) < 1e-14f) continue;
                float inv = 1f / det;
                Vector3 sv = origin - a;
                float u = Vector3.Dot(sv, p) * inv;
                if (u < 0f || u > 1f) continue;
                Vector3 q = Vector3.Cross(sv, e1);
                float v = Vector3.Dot(dir, q) * inv;
                if (v < 0f || u + v > 1f) continue;
                float tt = Vector3.Dot(e2, q) * inv;
                if (tt > best)
                {
                    best = tt;
                    bestTri = i;
                    bw0 = 1f - u - v;
                    bw1 = u;
                    bw2 = v;
                }
            }

            if (bestTri < 0)
            {
                hit = origin;
                normal = dir;
                return false;
            }

            hit = origin + dir * best;
            normal = NormalAt(bestTri, bw0, bw1, bw2);
            return true;
        }

        /// <summary>nearest surface point to p (all region triangles) and the surface normal there; the signed distance is
        /// Dot(p - closest, normal): negative inside the skin</summary>
        public float Closest(Vector3 p, out Vector3 closest, out Vector3 normal)
        {
            float best = float.MaxValue;
            int bestTri = -1;
            Vector3 bestPoint = p;
            float bw0 = 1, bw1 = 0, bw2 = 0;
            for (int i = 0; i < Tris.Length; i += 3)
            {
                Vector3 a = Pos[Tris[i]], b = Pos[Tris[i + 1]], c = Pos[Tris[i + 2]];
                // cheap reject: the triangle's bounding sphere is farther than the best so far
                Vector3 m = (a + b + c) * (1f / 3f);
                float r = Mathf.Max((a - m).sqrMagnitude, Mathf.Max((b - m).sqrMagnitude, (c - m).sqrMagnitude));
                float dm = (p - m).magnitude - Mathf.Sqrt(r);
                if (dm > 0f && dm * dm > best) continue;
                Vector3 q = ClosestOnTriangle(p, a, b, c, out float w0, out float w1, out float w2);
                float d2 = (p - q).sqrMagnitude;
                if (d2 < best)
                {
                    best = d2;
                    bestTri = i;
                    bestPoint = q;
                    bw0 = w0;
                    bw1 = w1;
                    bw2 = w2;
                }
            }

            closest = bestPoint;
            normal = bestTri >= 0 ? NormalAt(bestTri, bw0, bw1, bw2) : (p - Centre).normalized;
            return Vector3.Dot(p - closest, normal);
        }

        /// <summary>Ericson, Real-Time Collision Detection 5.1.5; barycentric weights of the returned point</summary>
        static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float wa, out float wb, out float wc)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { wa = 1; wb = 0; wc = 0; return a; }
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { wa = 0; wb = 1; wc = 0; return b; }
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                wa = 1 - v; wb = v; wc = 0;
                return a + ab * v;
            }

            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { wa = 0; wb = 0; wc = 1; return c; }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                wa = 1 - w; wb = 0; wc = w;
                return a + ac * w;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                float w = (d4 - d3) / (d4 - d3 + (d5 - d6));
                wa = 0; wb = 1 - w; wc = w;
                return b + (c - b) * w;
            }

            float denom = 1f / (va + vb + vc);
            float vv = vb * denom, ww = vc * denom;
            wa = 1 - vv - ww; wb = vv; wc = ww;
            return a + ab * vv + ac * ww;
        }

        /// <summary>mouth corner of a side (iBUG landmark 48 right / 54 left, barycentric on SMPL-X triangles)</summary>
        public bool TryMouthCorner(bool right, out Vector3 p)
        {
            int[] ids = right ? MouthRightIds : MouthLeftIds;
            float[] w = right ? MouthRightWeights : MouthLeftWeights;
            p = Vector3.zero;
            for (int i = 0; i < 3; i++)
            {
                if (!TryVertex(ids[i], out Vector3 v, out _)) return false;
                p += v * w[i];
            }

            return true;
        }

        /// <summary>the ear outline of a side as (position, normal) from the front of the ear over its top to the back</summary>
        public bool TryEarRim(bool right, out Vector3[] pos, out Vector3[] nrm)
        {
            int[] ids = right ? EarRimRight : EarRimLeft;
            pos = new Vector3[ids.Length];
            nrm = new Vector3[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                if (!TryVertex(ids[i], out pos[i], out nrm[i])) return false;
            }

            return true;
        }
    }

    // ------------------------------------------------------------------------------------------------ the headset

    /// <summary>what the geometry stage makes: the curves, the capsule, the clearances (metres, signed: skin to the prop's
    /// SURFACE, i.e. the axis distance minus the tube radius) and the combined mesh data</summary>
    public sealed class HeadsetBuild
    {
        public Vector3[] Boom = Array.Empty<Vector3>();  // boom axis, from the ear hook to the capsule's rear end
        public Vector3[] Hook = Array.Empty<Vector3>();  // ear hook axis (empty without a hook)
        public Vector3 CapsuleCentre, CapsuleAxis;       // the axis points along the boom, toward the lips
        public Vector3 MouthCorner, EarFront, CornerNormal;
        public float BoomLengthM;                        // arc length from the ear hook to the capsule centre
        public float MinBoomClearanceM, MaxBoomClearanceM, MinHookClearanceM, MinCapsuleClearanceM;
        public string Warning;
        public MeshData Mesh;
    }

    /// <summary>combined triangle soup with per-vertex colours (sRGB here; ToMesh writes them linear)</summary>
    public sealed class MeshData
    {
        public readonly List<Vector3> V = new(), N = new();
        public readonly List<Color> C = new();
        public readonly List<int> T = new();

        public Mesh ToMesh(string name)
        {
            Mesh m = new() { name = name };
            m.indexFormat = V.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(V);
            m.SetNormals(N);
            List<Color> linear = new(C.Count); // the shader multiplies vertex colours into a linear albedo
            foreach (Color c in C) linear.Add(c.linear);
            m.SetColors(linear);
            m.SetTriangles(T, 0);
            m.RecalculateBounds();
            return m;
        }

        void Tri(int a, int b, int c)
        {
            // front faces are the ones whose geometric normal agrees with the vertex normals (Unity: clockwise seen from the front)
            Vector3 g = Vector3.Cross(V[b] - V[a], V[c] - V[a]);
            if (Vector3.Dot(g, N[a] + N[b] + N[c]) >= 0f)
            {
                T.Add(a); T.Add(b); T.Add(c);
            }
            else
            {
                T.Add(a); T.Add(c); T.Add(b);
            }
        }

        /// <summary>a round tube along a polyline (parallel-transported rings), closed with a flat cap at each end</summary>
        public void Tube(IList<Vector3> pts, float radius, Color colour, int seg = 8)
        {
            int n = pts.Count;
            if (n < 2) return;
            int first = V.Count;
            Vector3 prevT = (pts[1] - pts[0]).normalized;
            Vector3 u = Vector3.Cross(prevT, Mathf.Abs(prevT.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            for (int i = 0; i < n; i++)
            {
                Vector3 t = i == 0 ? (pts[1] - pts[0]) : i == n - 1 ? (pts[n - 1] - pts[n - 2]) : (pts[i + 1] - pts[i - 1]);
                t = t.sqrMagnitude > 1e-14f ? t.normalized : prevT;
                // parallel transport: the previous ring normal, projected into the new ring's plane
                u = u - Vector3.Dot(u, t) * t;
                u = u.sqrMagnitude > 1e-12f ? u.normalized : Vector3.Cross(t, Mathf.Abs(t.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                prevT = t;
                Vector3 w = Vector3.Cross(t, u);
                for (int k = 0; k < seg; k++)
                {
                    float a = 2f * Mathf.PI * k / seg;
                    Vector3 nrm = Mathf.Cos(a) * u + Mathf.Sin(a) * w;
                    V.Add(pts[i] + nrm * radius);
                    N.Add(nrm);
                    C.Add(colour);
                }
            }

            for (int i = 0; i < n - 1; i++)
            {
                for (int k = 0; k < seg; k++)
                {
                    int a = first + i * seg + k, b = first + i * seg + (k + 1) % seg;
                    int c = first + (i + 1) * seg + k, d = first + (i + 1) * seg + (k + 1) % seg;
                    Tri(a, b, c);
                    Tri(b, d, c);
                }
            }

            for (int end = 0; end < 2; end++)
            {
                int ring = first + (end == 0 ? 0 : (n - 1) * seg);
                Vector3 t = end == 0 ? (pts[1] - pts[0]).normalized : (pts[n - 1] - pts[n - 2]).normalized;
                Vector3 outDir = end == 0 ? -t : t;
                int centre = V.Count;
                V.Add(pts[end == 0 ? 0 : n - 1]);
                N.Add(outDir);
                C.Add(colour);
                for (int k = 0; k < seg; k++)
                {
                    int i0 = V.Count;
                    V.Add(V[ring + k]); N.Add(outDir); C.Add(colour);
                    V.Add(V[ring + (k + 1) % seg]); N.Add(outDir); C.Add(colour);
                    Tri(centre, i0, i0 + 1);
                }
            }
        }

        /// <summary>a pill (cylinder with rounded ends) of the given overall length about a centre, along an axis</summary>
        public void Capsule(Vector3 centre, Vector3 axis, float length, float radius, Color colour, int seg = 14, int rings = 6)
        {
            axis = axis.normalized;
            Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 w = Vector3.Cross(axis, u);
            float r = Mathf.Min(radius, 0.5f * length);
            float cyl = Mathf.Max(0f, length - 2f * r);
            // profile rings from the rear pole to the front pole: (offset along the axis, radius, normal along the axis, normal radial)
            List<Vector4> prof = new();
            for (int i = 0; i <= rings; i++)
            {
                float a = 0.5f * Mathf.PI * i / rings; // 0 = the rear pole ... pi/2 = the equator
                prof.Add(new Vector4(-0.5f * cyl - r * Mathf.Cos(a), r * Mathf.Sin(a), -Mathf.Cos(a), Mathf.Sin(a)));
            }

            for (int i = rings; i >= 0; i--)
            {
                float a = 0.5f * Mathf.PI * i / rings;
                prof.Add(new Vector4(0.5f * cyl + r * Mathf.Cos(a), r * Mathf.Sin(a), Mathf.Cos(a), Mathf.Sin(a)));
            }

            int first = V.Count;
            foreach (Vector4 pr in prof)
            {
                for (int k = 0; k < seg; k++)
                {
                    float a = 2f * Mathf.PI * k / seg;
                    Vector3 radial = Mathf.Cos(a) * u + Mathf.Sin(a) * w;
                    V.Add(centre + axis * pr.x + radial * pr.y);
                    N.Add((axis * pr.z + radial * pr.w).normalized);
                    C.Add(colour);
                }
            }

            for (int i = 0; i < prof.Count - 1; i++)
            {
                for (int k = 0; k < seg; k++)
                {
                    int a = first + i * seg + k, b = first + i * seg + (k + 1) % seg;
                    int c = first + (i + 1) * seg + k, d = first + (i + 1) * seg + (k + 1) % seg;
                    Tri(a, b, c);
                    Tri(b, d, c);
                }
            }
        }
    }

    static float Smooth01(float x)
    {
        x = Mathf.Clamp01(x);
        return x * x * (3f - 2f * x);
    }

    static float Length(IList<Vector3> p)
    {
        float l = 0f;
        for (int i = 1; i < p.Count; i++) l += (p[i] - p[i - 1]).magnitude;
        return l;
    }

    static Vector3[] Smooth(Vector3[] p, int passes)
    {
        Vector3[] a = (Vector3[])p.Clone();
        for (int it = 0; it < passes; it++)
        {
            Vector3[] b = (Vector3[])a.Clone();
            for (int i = 1; i < a.Length - 1; i++) b[i] = 0.25f * a[i - 1] + 0.5f * a[i] + 0.25f * a[i + 1];
            a = b;
        }

        return a;
    }

    /// <summary>equal arc-length samples along a polyline</summary>
    static Vector3[] Resample(IList<Vector3> p, int count)
    {
        int n = p.Count;
        float[] cum = new float[n];
        for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + (p[i] - p[i - 1]).magnitude;
        Vector3[] o = new Vector3[count];
        int seg = 1;
        for (int k = 0; k < count; k++)
        {
            float s = cum[n - 1] * k / (count - 1);
            while (seg < n - 1 && cum[seg] < s) seg++;
            float span = Mathf.Max(1e-9f, cum[seg] - cum[seg - 1]);
            o[k] = Vector3.Lerp(p[seg - 1], p[seg], Mathf.Clamp01((s - cum[seg - 1]) / span));
        }

        return o;
    }

    /// <summary>a Catmull-Rom curve through control points, `per` samples per span</summary>
    static Vector3[] Spline(IList<Vector3> c, int per)
    {
        List<Vector3> o = new();
        int n = c.Count;
        for (int i = 0; i < n - 1; i++)
        {
            Vector3 p0 = c[Mathf.Max(0, i - 1)], p1 = c[i], p2 = c[i + 1], p3 = c[Mathf.Min(n - 1, i + 2)];
            for (int k = 0; k < per; k++)
            {
                float t = k / (float)per, t2 = t * t, t3 = t2 * t;
                o.Add(0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3));
            }
        }

        o.Add(c[n - 1]);
        return o.ToArray();
    }

    /// <summary>push every sample out of the skin until its axis is `required(i)` above it (inside points are brought out)</summary>
    static void Clear(HeadSurface s, Vector3[] p, Func<int, float> required)
    {
        for (int i = 0; i < p.Length; i++)
        {
            float d = s.Closest(p[i], out Vector3 q, out Vector3 n);
            float req = required(i);
            if (d < req) p[i] = q + n * req;
        }
    }

    static float MinClearance(HeadSurface s, IList<Vector3> p, float radius, out float max)
    {
        float min = float.MaxValue;
        max = float.MinValue;
        foreach (Vector3 v in p)
        {
            float d = s.Closest(v, out _, out _) - radius;
            min = Mathf.Min(min, d);
            max = Mathf.Max(max, d);
        }

        return min;
    }

    /// <summary>the point at arc length `s` along a polyline and the unit tangent there (clamped to the ends)</summary>
    static Vector3 PointAt(IList<Vector3> p, float s, out Vector3 tangent)
    {
        float acc = 0f;
        for (int i = 1; i < p.Count; i++)
        {
            float l = (p[i] - p[i - 1]).magnitude;
            if (acc + l >= s || i == p.Count - 1)
            {
                tangent = l > 1e-9f ? (p[i] - p[i - 1]) / l : Vector3.forward;
                return Vector3.Lerp(p[i - 1], p[i], l > 1e-9f ? Mathf.Clamp01((s - acc) / l) : 0f);
            }

            acc += l;
        }

        tangent = Vector3.forward;
        return p.Count > 0 ? p[0] : Vector3.zero;
    }

    /// <summary>
    /// The headset on the head surface `s`: an ear hook over the top of the ear, a thin boom that follows the cheek from the ear to
    /// the mouth corner, a capsule at the end. Null when there is no head; a result with Warning set (and no mesh) when the head has
    /// no SMPL-X landmarks.
    /// </summary>
    public static HeadsetBuild BuildHeadset(HeadSurface s, HeadPropSpec spec)
    {
        if (s == null) return null;
        bool right = spec.Right;
        HeadsetBuild b = new();
        if (!s.TryEarRim(right, out Vector3[] rim, out Vector3[] rimN) || !s.TryMouthCorner(right, out Vector3 mouth))
        {
            b.Warning = "head landmarks (ear outline / mouth corner) missing on this skin";
            return b;
        }

        float boomR = 0.5f * spec.BoomThickness, hookR = 0.5f * spec.HookThickness, capR = spec.CapsuleRadius;
        const float skinGap = 0.0015f; // the wire never rests closer than this to the skin
        b.MouthCorner = mouth;
        b.EarFront = rim[0];

        // ---- ear hook: the ear outline pushed out by the wire's radius, a spline through it, cleared of the skin
        Vector3[] hook = Array.Empty<Vector3>();
        float startStandoff = spec.Hook ? hookR + skinGap : boomR + skinGap;
        if (spec.Hook)
        {
            Vector3[] ctrl = new Vector3[rim.Length];
            for (int i = 0; i < rim.Length; i++) ctrl[i] = rim[i] + rimN[i] * (hookR + skinGap);
            hook = Spline(ctrl, 4);
            Clear(s, hook, _ => hookR + 0.001f);
            hook = Smooth(hook, 1);
            Clear(s, hook, _ => hookR + 0.001f);
            hook = Resample(hook, 28);
        }

        // ---- boom axis: along the surface from the front of the ear to the mouth corner
        float endStandoff = Mathf.Max(spec.Standoff, capR + 0.002f);
        const int NRay = 24, NEnd = 6, N = NRay + NEnd;
        Vector3[] path;
        if (spec.BoomPath != null && spec.BoomPath.Length >= 2)
        {
            // explicit control points, relative to the front of the ear: x outward (the dancer's own side), y up, z forward
            Vector3[] ctrl = new Vector3[spec.BoomPath.Length];
            for (int i = 0; i < ctrl.Length; i++)
            {
                Vector3 q = spec.BoomPath[i];
                ctrl[i] = rim[0] + new Vector3((right ? -1f : 1f) * q.x, q.y, -q.z);
            }

            path = Resample(Spline(ctrl, 6), N);
        }
        else
        {
            // the cheek: rays from the middle of the head through the chord ear -> mouth corner, the first 80 % of it (the lips stand
            // forward of the corner, so a ray there would run past it); the last 20 % is a straight blend to the capsule's end, a
            // standoff above the corner along the surface normal there
            path = new Vector3[N];
            for (int i = 0; i < NRay; i++)
            {
                float u = i / (NRay - 1f);
                Vector3 chord = Vector3.Lerp(rim[0], mouth, 0.8f * u);
                Vector3 dir = chord - s.Centre;
                if (dir.sqrMagnitude < 1e-8f) dir = right ? Vector3.left : Vector3.right;
                dir.Normalize();
                if (!s.RayOuter(s.Centre, dir, out Vector3 hit, out Vector3 nrm)) s.Closest(chord, out hit, out nrm);
                float so = Mathf.Lerp(startStandoff, spec.Standoff, Smooth01(u / 0.35f)) + spec.Bow * Mathf.Sin(Mathf.PI * u);
                path[i] = hit + nrm * so;
            }

            s.Closest(mouth, out _, out Vector3 cornerN);
            // the capsule floats beside the corner (to the side and a little forward), not up it: the normal there leans up over the
            // upper lip, and straight ahead it would hang in front of the lips
            Vector3 lateral = right ? Vector3.left : Vector3.right;
            cornerN = (0.75f * lateral + 0.65f * new Vector3(cornerN.x, cornerN.y * 0.25f, cornerN.z).normalized).normalized;
            b.CornerNormal = cornerN;
            Vector3 target = mouth + cornerN * endStandoff;
            for (int e = 1; e <= NEnd; e++) path[NRay - 1 + e] = Vector3.Lerp(path[NRay - 1], target, e / (float)NEnd);
            path[0] = rim[0] + rimN[0] * startStandoff; // joins the hook exactly
        }

        float Required(int i)
        {
            float t = i / (N - 1f);
            return Mathf.Lerp(boomR + skinGap, capR + 0.003f, Smooth01((t - 0.7f) / 0.3f));
        }

        path = Smooth(path, 3);
        Clear(s, path, Required);
        path = Smooth(path, 1);
        Clear(s, path, Required);
        path = Resample(path, N);

        // ---- length: to the mouth corner, or the configured arc length from the ear hook to the capsule centre
        float pathLen = Length(path);
        float capLen = spec.CapsuleLength;
        float centreS = spec.BoomLength > 0f ? spec.BoomLength : pathLen;
        List<Vector3> full = new(path);
        if (centreS > pathLen)
        {
            Vector3 tail = (path[N - 1] - path[N - 3]).normalized;
            int extra = Mathf.CeilToInt((centreS - pathLen) / 0.004f);
            for (int i = 1; i <= extra; i++) full.Add(path[N - 1] + tail * ((centreS - pathLen) * (i / (float)extra)));
        }

        // ---- the capsule at the end of the boom: centre on the axis, pointing along it; nudged out until it clears the skin
        Vector3 centre = Vector3.zero, axis = Vector3.forward;
        for (int iter = 0; iter < 5; iter++)
        {
            centre = PointAt(full, centreS, out axis);
            float worst = 0f;
            Vector3 push = Vector3.zero;
            for (int k = -2; k <= 2; k++)
            {
                float d = s.Closest(centre + axis * (0.25f * capLen * k), out _, out Vector3 n) - (capR + 0.0015f);
                if (d < worst)
                {
                    worst = d;
                    push = n * -d;
                }
            }

            if (worst >= -0.0002f) break;
            // lift the last part of the boom with it
            Vector3[] arr = full.ToArray();
            float total = Length(arr), acc = 0f;
            for (int i = 1; i < arr.Length; i++)
            {
                acc += (arr[i] - arr[i - 1]).magnitude;
                arr[i] += push * Smooth01((acc / Mathf.Max(total, 1e-6f) - 0.55f) / 0.45f);
            }

            full = new List<Vector3>(arr);
        }

        // the capsule points at the lips: the boom's last direction turned toward the middle of the mouth
        if (spec.BoomPath == null || spec.BoomPath.Length < 2)
        {
            Vector3 mouthMid = new Vector3(s.Centre.x, mouth.y, mouth.z);
            Vector3 toMouth = mouthMid - centre;
            if (toMouth.sqrMagnitude > 1e-8f) axis = (axis + 0.6f * toMouth.normalized).normalized;
        }

        // the boom ends inside the capsule's rear cap
        float boomEndS = Mathf.Max(0.01f, centreS - 0.5f * capLen + 0.004f);
        List<Vector3> boom = new() { full[0] };
        float accum = 0f;
        for (int i = 1; i < full.Count; i++)
        {
            float seg = (full[i] - full[i - 1]).magnitude;
            if (accum + seg >= boomEndS)
            {
                boom.Add(Vector3.Lerp(full[i - 1], full[i], seg > 1e-9f ? (boomEndS - accum) / seg : 0f));
                break;
            }

            accum += seg;
            boom.Add(full[i]);
        }

        if (boom.Count < 2) boom.Add(boom[0] + axis * 0.004f);
        Vector3[] boomArr = Resample(boom, 32);

        b.Boom = boomArr;
        b.Hook = hook;
        b.CapsuleCentre = centre;
        b.CapsuleAxis = axis;
        b.BoomLengthM = centreS;
        b.MinBoomClearanceM = MinClearance(s, boomArr, boomR, out float maxClear);
        b.MaxBoomClearanceM = maxClear;
        b.MinHookClearanceM = hook.Length > 0 ? MinClearance(s, hook, hookR, out _) : float.NaN;
        float capMin = float.MaxValue;
        for (int k = -2; k <= 2; k++) capMin = Mathf.Min(capMin, s.Closest(centre + axis * (0.25f * capLen * k), out _, out _) - capR);
        b.MinCapsuleClearanceM = capMin;

        // ---- mesh
        MeshData m = new();
        m.Tube(boomArr, boomR, spec.BoomColour, 8);
        if (hook.Length > 1) m.Tube(hook, hookR, spec.HookColour, 8);
        m.Capsule(centre, axis, capLen, capR, spec.CapsuleColour, 14, 6);
        b.Mesh = m;
        return b;
    }
}
