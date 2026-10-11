using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// Where the dance stands in the REAL room (passthrough mixed reality): a similarity map from the viewer's world (the capture's floor,
    /// y = 0, the couple around the origin) to real floor coordinates (the headset's tracking floor, metres):
    ///     real = Anchor + Scale * R(YawDeg) * (world - Pivot)      (x, z; heights scale too)
    /// R(a) turns by a degrees about +Y the way Unity does (a bearing grows by a: clockwise seen from above). Scale 1 is life size, 2 makes
    /// the dancers twice as large. The VR rig carries the INVERSE of this map (the user moves instead of the dance), so nothing in the
    /// viewer needs a transform of its own: rig.rotation = R(-Yaw), rig.localScale = 1/Scale, rig.position = Pivot - rig.rotation * (Anchor / Scale).
    /// </summary>
    public sealed class DancePlacement
    {
        public const float MinScale = 0.5f, MaxScale = 2f;

        /// <summary>where the world point Pivot stands in the real room (x, z in metres, on the tracking floor)</summary>
        public Vector2 Anchor;
        /// <summary>how far the dance is turned about the vertical axis through the pivot, degrees (Unity bearing sense)</summary>
        public float YawDeg;
        /// <summary>size factor: 1 = life size</summary>
        public float Scale = 1f;
        /// <summary>the world point the dance turns and scales about: the couple's start (floor-projected)</summary>
        public Vector3 Pivot;

        public DancePlacement Clone() => (DancePlacement)MemberwiseClone();

        /// <summary>the rig transform that realises this placement (rig position, rotation, uniform scale)</summary>
        public void RigPose(out Vector3 position, out Quaternion rotation, out float rigScale)
        {
            float s = Mathf.Clamp(Scale, MinScale, MaxScale);
            rigScale = 1f / s;
            rotation = Quaternion.Euler(0f, -YawDeg, 0f);
            position = new Vector3(Pivot.x, 0f, Pivot.z) - rotation * (new Vector3(Anchor.x, 0f, Anchor.y) * rigScale);
        }

        /// <summary>a world point (x, z) to real floor coordinates</summary>
        public Vector2 ToReal(Vector2 world) => Anchor + Scale * PlacementMath.Rot(world - new Vector2(Pivot.x, Pivot.z), YawDeg);

        /// <summary>a real floor point to world (x, z)</summary>
        public Vector2 ToWorld(Vector2 real) => new Vector2(Pivot.x, Pivot.z) + PlacementMath.Rot(real - Anchor, -YawDeg) / Scale;
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
                Scale = 1f,
                Pivot = new Vector3(pivotWorld.x, 0f, pivotWorld.z)
            };
        }

        /// <summary>
        /// ONE hand holds the dance (pinch and drag): the dance follows the hand as if it were fixed to it - it slides with the hand on the
        /// floor and turns with the hand's heading (twist) about the hand. p0 is the placement when the pinch began.
        /// </summary>
        public static DancePlacement OneHand(DancePlacement p0, Vector2 hand0, float handYaw0Deg, Vector2 hand, float handYawDeg)
        {
            float d = Wrap(handYawDeg - handYaw0Deg);
            DancePlacement p = p0.Clone();
            p.Anchor = hand + Rot(p0.Anchor - hand0, d);
            p.YawDeg = Wrap(p0.YawDeg + d);
            return p;
        }

        /// <summary>
        /// TWO hands hold the dance (both pinching): the dance follows the line between the hands - it slides with their midpoint, turns with
        /// the line, and grows or shrinks with the spread. Size is clamped to MinScale..MaxScale.
        /// </summary>
        public static DancePlacement TwoHands(DancePlacement p0, Vector2 a0, Vector2 b0, Vector2 a, Vector2 b)
        {
            Vector2 v0 = b0 - a0, v = b - a;
            float k = v0.magnitude > 1e-3f && v.magnitude > 1e-3f ? v.magnitude / v0.magnitude : 1f;
            float s = Mathf.Clamp(p0.Scale * k, DancePlacement.MinScale, DancePlacement.MaxScale);
            k = s / p0.Scale;
            float d = v0.magnitude > 1e-3f && v.magnitude > 1e-3f ? Wrap(Bearing(v) - Bearing(v0)) : 0f;
            Vector2 m0 = (a0 + b0) * 0.5f, m = (a + b) * 0.5f;
            DancePlacement p = p0.Clone();
            p.Anchor = m + k * Rot(p0.Anchor - m0, d);
            p.YawDeg = Wrap(p0.YawDeg + d);
            p.Scale = s;
            return p;
        }

        /// <summary>turn the dance about the pivot's real position (a button)</summary>
        public static DancePlacement TurnAboutPivot(DancePlacement p0, float deg)
        {
            DancePlacement p = p0.Clone();
            p.YawDeg = Wrap(p0.YawDeg + deg);
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
