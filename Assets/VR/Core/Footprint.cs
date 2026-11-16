using System.Collections.Generic;
using UnityEngine;

namespace HmVr
{
    /// <summary>Where the dance happens on the floor (world x, z in metres).</summary>
    public struct DanceFootprint
    {
        /// <summary>the centre of the smallest circle around the floor points (after the farthest few are dropped)</summary>
        public Vector2 Centre;
        /// <summary>the largest distance of any floor point from Centre</summary>
        public float Radius;
        /// <summary>the 98th percentile of the floor points' distances from Centre</summary>
        public float RadiusP98;
        /// <summary>the bounding box of all the floor points (x, z)</summary>
        public Rect Box;
        /// <summary>how many finite floor points went in (0 = no footprint)</summary>
        public int Samples;
    }

    public static class FootprintMath
    {
        /// <summary>
        /// The footprint of a set of floor points (NaN / infinite ones skipped). The centre is the centre of the minimum enclosing circle
        /// (Welzl, iterative, in a fixed shuffled order so the result never changes between runs) of the points left after dropping the
        /// farthest (1 - keep) fraction from their mean, so a few tracking spikes do not drag it. Radius, RadiusP98 and Box use all points.
        /// </summary>
        public static DanceFootprint Compute(IList<Vector2> floorPoints, float keep = 0.98f)
        {
            List<Vector2> pts = new();
            if (floorPoints != null)
                for (int i = 0; i < floorPoints.Count; i++)
                {
                    Vector2 p = floorPoints[i];
                    if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.x) || float.IsInfinity(p.y)) continue;
                    pts.Add(p);
                }

            int n = pts.Count;
            if (n == 0) return default;

            Vector2 mean = Vector2.zero, lo = pts[0], hi = pts[0];
            foreach (Vector2 p in pts)
            {
                mean += p;
                lo = Vector2.Min(lo, p);
                hi = Vector2.Max(hi, p);
            }

            mean /= n;

            // drop the farthest fraction from the mean (stable order: by distance, then by index)
            int drop = Mathf.FloorToInt((1f - Mathf.Clamp01(keep)) * n + 1e-4f);
            int kept = Mathf.Max(1, n - drop);
            int[] order = new int[n];
            float[] dm = new float[n];
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
                dm[i] = (pts[i] - mean).sqrMagnitude;
            }

            System.Array.Sort(order, (a, b) => dm[a] != dm[b] ? dm[a].CompareTo(dm[b]) : a.CompareTo(b));
            Vector2[] core = new Vector2[kept];
            for (int i = 0; i < kept; i++) core[i] = pts[order[i]];
            Shuffle(core);
            Vector2 centre = EnclosingCircle(core, out _);

            float[] d = new float[n];
            for (int i = 0; i < n; i++) d[i] = (pts[i] - centre).magnitude;
            System.Array.Sort(d);
            return new DanceFootprint
            {
                Centre = centre,
                Radius = d[n - 1],
                RadiusP98 = Percentile(d, 0.98f),
                Box = Rect.MinMaxRect(lo.x, lo.y, hi.x, hi.y),
                Samples = n
            };
        }

        /// <summary>
        /// how far from the dance centre to stand to see all of it: outside a ring of ringRadius (0 = none) and outside the dancers' 98% circle (plus
        /// 0.3 m of body), plus clearance; never closer than ringRadius + clearance, never farther than max
        /// </summary>
        public static float ViewDistance(DanceFootprint fp, float ringRadius = 3.5f, float clearance = 0.6f, float max = 6f) =>
            Mathf.Clamp(Mathf.Max(ringRadius, fp.RadiusP98 + 0.3f) + clearance, ringRadius + clearance, max);

        /// <summary>the smallest circle containing every point (iterative Welzl; expected linear time on a shuffled input)</summary>
        static Vector2 EnclosingCircle(Vector2[] p, out float r)
        {
            Vector2 c = p[0];
            r = 0f;
            for (int i = 1; i < p.Length; i++)
            {
                if (Inside(p[i], c, r)) continue;
                c = p[i];
                r = 0f;
                for (int j = 0; j < i; j++)
                {
                    if (Inside(p[j], c, r)) continue;
                    c = (p[i] + p[j]) * 0.5f;
                    r = (p[i] - c).magnitude;
                    for (int k = 0; k < j; k++)
                    {
                        if (Inside(p[k], c, r)) continue;
                        Circumcircle(p[i], p[j], p[k], out c, out r);
                    }
                }
            }

            return c;
        }

        static bool Inside(Vector2 q, Vector2 c, float r) => (q - c).magnitude <= r + 1e-5f + r * 1e-6f;

        /// <summary>the circle through three points; for (nearly) collinear points the circle on the farthest pair</summary>
        static void Circumcircle(Vector2 a, Vector2 b, Vector2 c, out Vector2 centre, out float r)
        {
            double bx = b.x - a.x, by = b.y - a.y, cx = c.x - a.x, cy = c.y - a.y;
            double den = 2.0 * (bx * cy - by * cx);
            if (System.Math.Abs(den) < 1e-12)
            {
                Vector2 u = a, v = b;
                if ((c - a).sqrMagnitude > (v - u).sqrMagnitude) v = c;
                if ((c - b).sqrMagnitude > (v - u).sqrMagnitude) { u = b; v = c; }
                centre = (u + v) * 0.5f;
                r = (u - centre).magnitude;
                return;
            }

            double b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
            double ux = (cy * b2 - by * c2) / den, uy = (bx * c2 - cx * b2) / den;
            centre = new Vector2(a.x + (float)ux, a.y + (float)uy);
            r = Mathf.Max((a - centre).magnitude, Mathf.Max((b - centre).magnitude, (c - centre).magnitude));
        }

        /// <summary>a Fisher-Yates shuffle with a fixed-seed xorshift: the same input always gives the same order</summary>
        static void Shuffle(Vector2[] p)
        {
            uint s = 2463534242u;
            for (int i = p.Length - 1; i > 0; i--)
            {
                s ^= s << 13;
                s ^= s >> 17;
                s ^= s << 5;
                int j = (int)(s % (uint)(i + 1));
                (p[i], p[j]) = (p[j], p[i]);
            }
        }

        /// <summary>linear-interpolated percentile of sorted values</summary>
        static float Percentile(float[] sorted, float q)
        {
            if (sorted.Length == 1) return sorted[0];
            float x = q * (sorted.Length - 1);
            int i = Mathf.Clamp(Mathf.FloorToInt(x), 0, sorted.Length - 2);
            return Mathf.Lerp(sorted[i], sorted[i + 1], x - i);
        }
    }
}
