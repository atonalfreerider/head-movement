using System.Collections.Generic;
using UnityEngine;

namespace HmFilm
{
    /// <summary>
    /// Geometry of the face sidecar ("hm film faces" v1, dancecap docs/RECAP_FILM.md "The face sidecar"): the face orientation relative to the line
    /// of sight, a frustum surface rectangle clipped against the near plane, point-in-polygon, and the capsule occlusion test of the visibility
    /// samples. Pure functions (edit-mode tests: Assets/Film/Flow/Tests).
    /// </summary>
    public static class FaceSidecarMath
    {
        /// <summary>
        /// yaw / pitch / roll (deg) of a face relative to the line of sight: z' = -normalize(head centre - camera position), x' = camera right made
        /// orthogonal to z', y' = camera up made orthogonal to both (Gram-Schmidt: Unity is left-handed, no cross products). With the face forward f and
        /// up u: yaw = atan2(f.x', f.z') (0 = into the lens, +90 = turned to the image's right, +-180 = back of the head), pitch = asin(f.y')
        /// (+ = face up), roll = atan2(u.x', u.y').
        /// </summary>
        public static void FaceAngles(Vector3 headCentre, Vector3 camPos, Vector3 camRight, Vector3 camUp, Vector3 fwd, Vector3 up,
            out float yaw, out float pitch, out float roll)
        {
            Vector3 z = -(headCentre - camPos).normalized;
            Vector3 x = (camRight - Vector3.Dot(camRight, z) * z).normalized;
            Vector3 y = camUp - Vector3.Dot(camUp, z) * z;
            y = (y - Vector3.Dot(y, x) * x).normalized;
            Vector3 f = fwd.normalized, u = up.normalized;
            float fx = Vector3.Dot(f, x), fy = Vector3.Dot(f, y), fz = Vector3.Dot(f, z);
            yaw = Mathf.Atan2(fx, fz) * Mathf.Rad2Deg;
            pitch = Mathf.Asin(Mathf.Clamp(fy, -1f, 1f)) * Mathf.Rad2Deg;
            roll = Mathf.Atan2(Vector3.Dot(u, x), Vector3.Dot(u, y)) * Mathf.Rad2Deg;
        }

        /// <summary>Sutherland-Hodgman: the polygon (camera-space points, z = depth in front of the camera) clipped to z &gt;= near</summary>
        public static List<Vector3> ClipNear(IList<Vector3> poly, float near)
        {
            List<Vector3> o = new();
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 a = poly[i], b = poly[(i + 1) % n];
                bool ina = a.z >= near, inb = b.z >= near;
                if (ina) o.Add(a);
                if (ina != inb)
                {
                    float t = (near - a.z) / (b.z - a.z);
                    o.Add(Vector3.Lerp(a, b, t));
                }
            }

            return o;
        }

        /// <summary>even-odd point in polygon (any winding)</summary>
        public static bool Inside(Vector2 p, IList<Vector2> poly)
        {
            bool c = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector2 a = poly[i], b = poly[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) c = !c;
            }

            return c;
        }

        /// <summary>the smallest distance between the segments [p1, q1] and [p2, q2] (Ericson, Real-Time Collision Detection 5.1.9)</summary>
        public static float SegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s, t;
            if (a <= 1e-9f && e <= 1e-9f) return r.magnitude;
            if (a <= 1e-9f)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-9f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2), denom = a * e - b * b;
                    s = denom > 1e-9f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((b - c) / a);
                    }
                }
            }

            return Vector3.Distance(p1 + d1 * s, p2 + d2 * t);
        }

        /// <summary>a ray from the camera to a sample is blocked by a capsule (axis a-b, radius r) before it reaches the sample (1 cm short of it)</summary>
        public static bool Blocks(Vector3 cam, Vector3 sample, Vector3 a, Vector3 b, float r)
        {
            Vector3 d = sample - cam;
            float len = d.magnitude;
            if (len < 0.02f) return false;
            Vector3 end = cam + d * ((len - 0.01f) / len);
            return SegmentDistance(cam, end, a, b) < r;
        }
    }
}
