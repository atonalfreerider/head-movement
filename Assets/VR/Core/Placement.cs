using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// Where the dance stands in the REAL room (passthrough mixed reality): a rigid map on the floor from the viewer's world (the capture's
    /// floor, y = 0, the couple around the origin) to real floor coordinates (the headset's tracking floor, metres):
    ///     real = Anchor + R(YawDeg) * (world - Pivot)      (x, z only)
    /// R(a) turns by a degrees about +Y the way Unity does (a bearing grows by a: clockwise seen from above). Always life size (owner
    /// 2026-11-15: "no scaling ever"): there is no scale in the map. Its height, Lift, is 0 (the capture's floor on the real floor) except in
    /// the god's eye view (owner 2026-11-16: "a pinch point in the sky above the dancers to get a god's eye view"), where the dance floor
    /// lies below the real floor so the user looks down on the couple. The VR rig carries the INVERSE of this map (the user moves instead of
    /// the dance), so nothing in the viewer needs a transform of its own:
    /// rig.rotation = R(-Yaw), rig.localScale = 1, rig.position = Pivot - rig.rotation * Anchor - Lift * up.
    /// </summary>
    public sealed class DancePlacement
    {
        /// <summary>where the world point Pivot stands in the real room (x, z in metres, on the tracking floor)</summary>
        public Vector2 Anchor;
        /// <summary>how far the dance is turned about the vertical axis through the pivot, degrees (Unity bearing sense)</summary>
        public float YawDeg;
        /// <summary>always 1: the dance is life size, never scaled</summary>
        public float Scale => 1f;
        /// <summary>the world point the dance turns about and that stands at Anchor: the dance's footprint centre, floor-projected</summary>
        public Vector3 Pivot;
        /// <summary>the height of the capture's floor in the real room, metres: 0 on the ground, negative in the god's eye view</summary>
        public float Lift;

        public DancePlacement Clone() => (DancePlacement)MemberwiseClone();

        /// <summary>the rig transform that realises this placement (rig position, rotation, uniform scale)</summary>
        public void RigPose(out Vector3 position, out Quaternion rotation, out float rigScale)
        {
            rigScale = 1f;
            rotation = Quaternion.Euler(0f, -YawDeg, 0f);
            position = new Vector3(Pivot.x, -Lift, Pivot.z) - rotation * new Vector3(Anchor.x, 0f, Anchor.y);
        }

        /// <summary>a world point (x, z) to real floor coordinates</summary>
        public Vector2 ToReal(Vector2 world) => Anchor + PlacementMath.Rot(world - new Vector2(Pivot.x, Pivot.z), YawDeg);

        /// <summary>a real floor point to world (x, z)</summary>
        public Vector2 ToWorld(Vector2 real) => new Vector2(Pivot.x, Pivot.z) + PlacementMath.Rot(real - Anchor, -YawDeg);
    }

    public static class PlacementMath
    {
        /// <summary>turn a floor vector (x, z) by deg degrees the way Quaternion.Euler(0, deg, 0) does</summary>
        public static Vector2 Rot(Vector2 v, float deg)
        {
            float c = Mathf.Cos(deg * Mathf.Deg2Rad), s = Mathf.Sin(deg * Mathf.Deg2Rad);
            return new Vector2(v.x * c + v.y * s, -v.x * s + v.y * c);
        }

        /// <summary>bearing of a floor vector (x, z), degrees: 0 = +Z, 90 = +X</summary>
        public static float Bearing(Vector2 v) => Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg;

        /// <summary>
        /// The placement a lesson starts with: the couple's pivot stands `distance` metres in front of the user (along the head's heading),
        /// turned so that the start vantage (the world bearing `frontBearingDeg` seen from the pivot: where the immersive view puts the
        /// viewer) faces the user. Life size.
        /// </summary>
        public static DancePlacement Default(Vector3 headReal, float headYawDeg, Vector3 pivotWorld, float frontBearingDeg, float distance = 2.2f)
        {
            Vector2 fwd = new(Mathf.Sin(headYawDeg * Mathf.Deg2Rad), Mathf.Cos(headYawDeg * Mathf.Deg2Rad));
            return new DancePlacement
            {
                Anchor = new Vector2(headReal.x, headReal.z) + fwd * distance,
                YawDeg = Wrap(headYawDeg + 180f - frontBearingDeg),
                Pivot = new Vector3(pivotWorld.x, 0f, pivotWorld.z)
            };
        }

        /// <summary>
        /// TWO hands pinch and drag (owner 2026-11-16: "two drag points should allow floor translation and rotation at fixed scale always"):
        /// the floor turns with the line between the pinch points, about the midpoint where the hold began, and slides with the midpoint. Never
        /// scaled (the spread of the hands is ignored), never lifted. a0, b0: the pinch points when the hold began; p0: the placement then.
        /// </summary>
        public static DancePlacement TwoHands(DancePlacement p0, Vector2 a0, Vector2 b0, Vector2 a, Vector2 b)
        {
            Vector2 v0 = b0 - a0, v = b - a;
            float d = v0.magnitude < 0.02f || v.magnitude < 0.02f ? 0f : Wrap(Bearing(v) - Bearing(v0));
            Vector2 m0 = (a0 + b0) * 0.5f, m = (a + b) * 0.5f;
            DancePlacement p = p0.Clone();
            p.Anchor = m + Rot(p0.Anchor - m0, d);
            p.YawDeg = Wrap(p0.YawDeg + d);
            return p;
        }

        /// <summary>
        /// The god's eye view: the dance's centre (its pivot) straight below the user, `ahead` metres along the gaze (so it sits low in the view, not
        /// under the chin), the dance floor `eyeAbove` metres below the eyes. Turned as before.
        /// </summary>
        public static DancePlacement GodsEye(DancePlacement p0, Vector3 headReal, float headYawDeg, float eyeAbove, float ahead = 0.35f)
        {
            Vector2 fwd = new(Mathf.Sin(headYawDeg * Mathf.Deg2Rad), Mathf.Cos(headYawDeg * Mathf.Deg2Rad));
            DancePlacement p = p0.Clone();
            p.Anchor = new Vector2(headReal.x, headReal.z) + fwd * ahead;
            p.Lift = headReal.y - eyeAbove;
            return p;
        }

        /// <summary>a placement part of the way from a to b (anchor and lift linear, yaw the short way round); u in 0..1</summary>
        public static DancePlacement Lerp(DancePlacement a, DancePlacement b, float u)
        {
            DancePlacement p = b.Clone();
            p.Anchor = Vector2.Lerp(a.Anchor, b.Anchor, u);
            p.Lift = Mathf.Lerp(a.Lift, b.Lift, u);
            p.YawDeg = Wrap(a.YawDeg + Wrap(b.YawDeg - a.YawDeg) * u);
            return p;
        }

        /// <summary>slide the dance on the floor (real metres)</summary>
        public static DancePlacement Slide(DancePlacement p0, Vector2 delta)
        {
            DancePlacement p = p0.Clone();
            p.Anchor += delta;
            return p;
        }

        /// <summary>the heading of the fingers (wrist to middle knuckle) on the floor, or false when the hand points so steeply up or down that it has none</summary>
        public static bool HandHeading(HandState h, out float yawDeg)
        {
            Vector3 f = h.MiddleKnuckle - h.Wrist;
            yawDeg = 0f;
            if (new Vector2(f.x, f.z).magnitude < 0.04f) return false;
            yawDeg = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            return true;
        }

        public static float Wrap(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            if (deg < -180f) deg += 360f;
            return deg;
        }
    }
}
