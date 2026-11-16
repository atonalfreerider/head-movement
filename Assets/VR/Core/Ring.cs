using System.Collections.Generic;
using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// The timeline ring: a horizontal circle (owner: radius 3.5 m, 3.5 m above the floor) around the dance centre that carries the whole
    /// lesson, 360 degrees for the full duration. The current-position marker sits on the ring directly opposite the user across the dance
    /// centre, and the timeline moves through it as the film plays.
    /// Convention (used by every function here): bearings are compass bearings about the vertical axis as ViewpointMath.Bearing gives
    /// them (0 = +Z, 90 = +X, growing clockwise seen from above). LATER times sit at LARGER bearings (clockwise of the marker), so while the
    /// film plays every point of the timeline slides counter-clockwise and the upcoming chapters approach the marker from the clockwise side.
    /// </summary>
    public static class RingMath
    {
        /// <summary>the bearing (degrees, from the centre) of the point on the ring opposite the user: from the centre away from the head</summary>
        public static float MarkerBearing(Vector3 head, Vector3 centre) => ViewpointMath.Wrap(ViewpointMath.Bearing(head, centre));

        /// <summary>
        /// where time t sits on the ring (bearing from the centre, degrees in (-180, 180]): 'now' at the marker, later times clockwise
        /// (larger bearings), one full turn per duration
        /// </summary>
        public static float BearingOfTime(float t, float now, float duration, float markerBearingDeg)
        {
            if (duration <= 0f) return ViewpointMath.Wrap(markerBearingDeg);
            return ViewpointMath.Wrap(markerBearingDeg + (t - now) / duration * 360f);
        }

        /// <summary>the inverse of BearingOfTime: the time shown at a bearing, in [0, duration) (the ring wraps from the end to the start)</summary>
        public static float TimeAtBearing(float bearingDeg, float now, float duration, float markerBearingDeg)
        {
            if (duration <= 0f) return 0f;
            float t = now + ViewpointMath.Wrap(bearingDeg - markerBearingDeg) / 360f * duration;
            t %= duration;
            if (t < 0f) t += duration;
            return t >= duration ? 0f : t;
        }

        /// <summary>
        /// Aiming at the ring: the ray first meets the ring's plane (y = height; only a ray going up can) and counts when that point is within
        /// bandM of the ring's circle; otherwise the nearest forward hit on the vertical cylinder of the ring's radius between height - bandM
        /// and height + bandM (a ray skimming the ring's height). bearingDeg = the hit's bearing from the centre, t = distance along the ray.
        /// </summary>
        public static bool Raycast(Vector3 origin, Vector3 dir, Vector3 centre, float radius, float height, float bandM, out float bearingDeg,
            out Vector3 point, out float t)
        {
            bearingDeg = 0f;
            point = origin;
            t = 0f;
            if (dir.sqrMagnitude < 1e-12f) return false;
            dir.Normalize();

            // the plane of the ring
            if (dir.y > 1e-5f)
            {
                float tp = (height - origin.y) / dir.y;
                if (tp > 0f)
                {
                    Vector3 p = origin + dir * tp;
                    float flat = new Vector2(p.x - centre.x, p.z - centre.z).magnitude;
                    if (Mathf.Abs(flat - radius) <= bandM)
                    {
                        point = p;
                        t = tp;
                        bearingDeg = ViewpointMath.Bearing(centre, p);
                        return true;
                    }
                }
            }

            // the band of the cylinder: |o + t d - c| = radius in x, z
            float ox = origin.x - centre.x, oz = origin.z - centre.z;
            float a = dir.x * dir.x + dir.z * dir.z;
            if (a < 1e-10f) return false;
            float b = 2f * (ox * dir.x + oz * dir.z), c = ox * ox + oz * oz - radius * radius;
            float disc = b * b - 4f * a * c;
            if (disc < 0f) return false;
            float sq = Mathf.Sqrt(disc);
            float t0 = (-b - sq) / (2f * a), t1 = (-b + sq) / (2f * a);
            for (int k = 0; k < 2; k++) // ascending: the nearest forward hit inside the band wins (no array: this runs every frame)
            {
                float tc = k == 0 ? t0 : t1;
                if (tc <= 0f) continue;
                Vector3 p = origin + dir * tc;
                if (Mathf.Abs(p.y - height) > bandM) continue;
                point = p;
                t = tc;
                bearingDeg = ViewpointMath.Bearing(centre, p);
                return true;
            }

            return false;
        }

        /// <summary>the chapter start nearest to t when it is within snapS (measured around the ring, so the end wraps to the start), else t</summary>
        public static float SnapTime(float t, IList<float> chapterStarts, float duration, float snapS)
        {
            if (chapterStarts == null) return t;
            float best = t, bd = snapS;
            for (int i = 0; i < chapterStarts.Count; i++)
            {
                float d = Mathf.Abs(chapterStarts[i] - t);
                if (duration > 0f) d = Mathf.Min(d % duration, duration - d % duration);
                if (d <= bd)
                {
                    bd = d;
                    best = chapterStarts[i];
                }
            }

            return best;
        }

        /// <summary>a bearing on the ring can be picked when it lies within maxDeg of the marker (the arc the user faces)</summary>
        public static bool Reachable(float targetBearing, float markerBearing, float maxDeg) =>
            Mathf.Abs(ViewpointMath.Wrap(targetBearing - markerBearing)) <= maxDeg;
    }
}
