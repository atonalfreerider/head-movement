using System.Collections.Generic;
using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// A flat rectangle in the world: the face of a UI panel. Normal points toward the viewer; Local coordinates are metres from the
    /// centre along Right and Up. Rays only hit the front side.
    /// </summary>
    public struct RectPlane
    {
        public Vector3 Origin, Right, Up, Normal;
        public Vector2 HalfSize;

        public RectPlane(Vector3 origin, Vector3 right, Vector3 up, Vector2 halfSize)
        {
            Origin = origin;
            Right = right.normalized;
            Up = up.normalized;
            Normal = -Vector3.Cross(Right, Up).normalized; // viewed from the front, +X is right and +Y up, so the face looks along -(X x Y)
            HalfSize = halfSize;
        }

        /// <summary>the plane of a transform whose +X is right, +Y up and whose face is viewed from -Z (a world-space canvas: forward points away from the reader)</summary>
        public static RectPlane OfCanvas(Transform t, Vector2 sizeMetres) =>
            new(t.position, t.right, t.up, sizeMetres * 0.5f);

        /// <summary>signed distance of a point in front of the face (negative = behind it)</summary>
        public float Depth(Vector3 p) => Vector3.Dot(p - Origin, Normal);

        public Vector2 Local(Vector3 p)
        {
            Vector3 d = p - Origin;
            return new Vector2(Vector3.Dot(d, Right), Vector3.Dot(d, Up));
        }

        public bool Contains(Vector2 local, float margin = 0f) =>
            Mathf.Abs(local.x) <= HalfSize.x + margin && Mathf.Abs(local.y) <= HalfSize.y + margin;

        /// <summary>intersection of a ray with the plane's front side (the rectangle's bounds are not tested)</summary>
        public bool Raycast(Vector3 origin, Vector3 dir, out float t, out Vector2 local)
        {
            t = 0f;
            local = default;
            float denom = Vector3.Dot(dir, Normal);
            if (denom > -1e-5f) return false; // parallel or from behind
            t = Vector3.Dot(Origin - origin, Normal) / denom;
            if (t < 0f) return false;
            local = Local(origin + dir * t);
            return true;
        }
    }

    public static class TeleportMath
    {
        public const float Speed = 6.5f, Gravity = 9.81f, Step = 0.04f, MaxTime = 2.2f;

        /// <summary>
        /// The teleport arc: a ballistic curve from the hand along the aim, ending where it meets the floor. Writes the sampled points
        /// (up to points.Length) and returns how many; hit = the floor point (valid only when the result is true).
        /// </summary>
        public static bool Arc(Vector3 origin, Vector3 dir, float floorY, Vector3[] points, out int count, out Vector3 hit)
        {
            count = 0;
            hit = origin;
            dir.Normalize();
            Vector3 prev = origin;
            if (points.Length > 0) points[count++] = prev;
            int max = Mathf.Min(points.Length, Mathf.CeilToInt(MaxTime / Step));
            for (int i = 1; i < max; i++)
            {
                float t = i * Step;
                Vector3 p = origin + dir * (Speed * t) + 0.5f * Gravity * t * t * Vector3.down;
                if (p.y <= floorY && prev.y > floorY)
                {
                    float k = (prev.y - floorY) / Mathf.Max(1e-6f, prev.y - p.y);
                    hit = Vector3.Lerp(prev, p, k);
                    hit.y = floorY;
                    points[count++] = hit;
                    return true;
                }

                points[count++] = p;
                prev = p;
            }

            return false;
        }

        /// <summary>a floor point may be teleported to when it is within MaxJump of the user and inside the walkable disc around the dance</summary>
        public static bool Allowed(Vector3 hit, Vector3 from, Vector3 discCentre, float discRadius, float maxJump) =>
            Flat(hit - from).magnitude <= maxJump && Flat(hit - discCentre).magnitude <= discRadius;

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }
    }

    public static class SliderMath
    {
        /// <summary>position along a slider (0..1) from a hit's local x (metres from the centre) and the track's width</summary>
        public static float U(float localX, float trackWidth) => Mathf.Clamp01(localX / Mathf.Max(1e-4f, trackWidth) + 0.5f);

        /// <summary>the tick position (0..1) within radius of u, or -1: dragging near a chapter tick sticks to it</summary>
        public static float Snap(float u, IList<float> ticksU, float radiusU)
        {
            float best = -1f, bd = radiusU;
            if (ticksU == null) return best;
            for (int i = 0; i < ticksU.Count; i++)
            {
                float d = Mathf.Abs(ticksU[i] - u);
                if (d <= bd)
                {
                    bd = d;
                    best = ticksU[i];
                }
            }

            return best;
        }
    }

    public struct Viewpoint
    {
        public string Id, Label, Kind;
        /// <summary>where the user's head ends up (x, z; y is the standing eye height above the rig's floor, or higher for an elevated view)</summary>
        public Vector3 Head;
        /// <summary>the compass bearing the user faces, degrees (0 = +Z, 90 = +X)</summary>
        public float YawDeg;
        /// <summary>the floor level to stand on (0, or higher for an elevated platform)</summary>
        public float FloorY;
    }

    public static class ViewpointMath
    {
        public const float EyeHeight = 1.6f;

        /// <summary>compass bearing from a to b about the vertical axis, degrees (0 = +Z, 90 = +X)</summary>
        public static float Bearing(Vector3 a, Vector3 b) => Mathf.Atan2(b.x - a.x, b.z - a.z) * Mathf.Rad2Deg;

        public static Vector3 Dir(float bearingDeg) => new(Mathf.Sin(bearingDeg * Mathf.Deg2Rad), 0f, Mathf.Cos(bearingDeg * Mathf.Deg2Rad));

        /// <summary>a viewpoint at a distance from the centre, on the given bearing from the centre, facing the centre</summary>
        public static Viewpoint Facing(string id, string label, string kind, Vector3 centre, float bearingFromCentreDeg, float distance, float floorY = 0f, float eye = EyeHeight)
        {
            Vector3 p = centre + Dir(bearingFromCentreDeg) * distance;
            p.y = floorY + eye;
            return new Viewpoint { Id = id, Label = label, Kind = kind, Head = p, YawDeg = Bearing(p, centre), FloorY = floorY };
        }

        /// <summary>
        /// The rig (the XR origin) pose that puts the user's head at head.x / head.z, standing on floorY, facing yawDeg, given where the
        /// head is now inside the rig (its tracked position and yaw in rig space): the user may be standing anywhere in their room,
        /// the world position follows their body.
        /// </summary>
        public static void RigPose(Vector3 headWorldTarget, float yawDeg, float floorY, Vector3 headLocalPos, float headLocalYawDeg, out Vector3 rigPos, out float rigYawDeg)
        {
            rigYawDeg = yawDeg - headLocalYawDeg;
            Quaternion q = Quaternion.Euler(0f, rigYawDeg, 0f);
            Vector3 horiz = q * new Vector3(headLocalPos.x, 0f, headLocalPos.z);
            rigPos = new Vector3(headWorldTarget.x - horiz.x, floorY, headWorldTarget.z - horiz.z);
        }

        /// <summary>wraps an angle to (-180, 180]</summary>
        public static float Wrap(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            if (deg <= -180f) deg += 360f;
            return deg;
        }
    }
}
