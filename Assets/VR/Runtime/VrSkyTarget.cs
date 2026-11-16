using System.Collections.Generic;
using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// The god's eye button (owner 2026-11-16: "an invisible button that is above the dancers that lights up only when the raycast points at it
    /// ... at a height of maybe 10 feet above the dancers' heads"). It floats AboveHeadsM (10 ft) above the higher of the two dancers' heads,
    /// over their midpoint, gliding after them. Nothing is drawn until the pointing ray is on it; then a small soft dot lights up. A pinch on it
    /// lifts the user to the god's eye (VrPlacement.EnterGodsEye); up there it is gone (a short pinch aimed at the floor comes back down).
    /// </summary>
    [DefaultExecutionOrder(140)]
    [AddComponentMenu("")]
    public sealed class VrSkyTarget : MonoBehaviour, IRayTarget
    {
        public static VrSkyTarget Instance { get; private set; }

        /// <summary>10 ft above the heads; the god's eye puts the eyes at the button's typical height above the dance floor</summary>
        public const float AboveHeadsM = 3.05f, TypicalHeadM = 1.75f, RadiusM = 0.06f, HitRadiusM = 0.45f;
        public static float EyeAboveFloorM => TypicalHeadM + AboveHeadsM;
        static readonly Color LitColour = new(0.92f, 0.95f, 1f, 0.8f);

        /// <summary>live: the ray can find it (a passthrough lesson on the ground); drawn only while Lit</summary>
        public bool Shown { get; private set; }
        public bool Lit { get; private set; }
        public Vector3 Position { get; private set; }
        public int Presses { get; private set; }

        Transform orb;
        MeshRenderer orbRenderer;
        Material mat;
        float hoverUntil;
        bool pressed, placed;
        Vector3 velocity;

        void Awake()
        {
            Instance = this;
            mat = VrDraw.Material(LitColour, false);
            GameObject g = new("god's eye button", typeof(MeshFilter), typeof(MeshRenderer));
            g.layer = VrStyle.Layer;
            g.transform.SetParent(transform, false);
            g.GetComponent<MeshFilter>().sharedMesh = VrDraw.Sphere;
            orbRenderer = g.GetComponent<MeshRenderer>();
            orbRenderer.sharedMaterial = mat;
            orbRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            orbRenderer.enabled = false;
            orb = g.transform;
            orb.localScale = Vector3.one * (RadiusM * 2f);
        }

        void OnEnable()
        {
            if (!VrPointers.Targets.Contains(this)) VrPointers.Targets.Add(this);
        }

        void OnDisable()
        {
            VrPointers.Targets.Remove(this);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (mat != null) Destroy(mat);
        }

        /// <summary>above the dancers' heads now (world): their pelvis midpoint, the higher head + 10 ft</summary>
        static bool AboveDancers(out Vector3 p)
        {
            p = Vector3.zero;
            HeadMovement hm = HeadMovement.Instance;
            Dancer a = hm != null ? hm.LeadDancer : null, b = hm != null ? hm.FollowDancer : null;
            if (a == null || b == null || a.FrameCount == 0 || b.FrameCount == 0) return false;
            int f = Mathf.Max(0, hm.CurrentFrame);
            Vector3 pa = a.Joint(Mathf.Min(f, a.FrameCount - 1), SmplJoint.Pelvis), pb = b.Joint(Mathf.Min(f, b.FrameCount - 1), SmplJoint.Pelvis);
            Vector3 ha = a.Joint(Mathf.Min(f, a.FrameCount - 1), SmplJoint.Head), hb = b.Joint(Mathf.Min(f, b.FrameCount - 1), SmplJoint.Head);
            bool okA = float.IsFinite(pa.x) && pa.y > -5f, okB = float.IsFinite(pb.x) && pb.y > -5f;
            if (!okA && !okB) return false;
            Vector3 mid = okA && okB ? (pa + pb) * 0.5f : okA ? pa : pb;
            float head = Mathf.Max(okA && float.IsFinite(ha.y) ? ha.y : 0f, okB && float.IsFinite(hb.y) ? hb.y : 0f);
            if (head < 0.5f) head = TypicalHeadM;
            p = new Vector3(mid.x, head + AboveHeadsM, mid.z);
            return true;
        }

        void LateUpdate()
        {
            VrPlacement pl = VrPlacement.Instance;
            bool live = pl != null && pl.Active && !pl.GodsEyeOn && !pl.Transitioning && (VrMenu.Instance == null || !VrMenu.Instance.IsOpen);
            if (live && AboveDancers(out Vector3 target))
            {
                // glide after the couple so the button does not jitter with their steps
                Position = placed ? Vector3.SmoothDamp(Position, target, ref velocity, 0.6f, Mathf.Infinity, Time.unscaledDeltaTime) : target;
                placed = true;
            }
            else
            {
                live = false;
                placed = false;
                velocity = Vector3.zero;
            }

            Shown = live;
            Lit = live && (pressed || Time.unscaledTime < hoverUntil);
            orbRenderer.enabled = Lit; // invisible until the ray is on it
            if (!live) return;
            orb.position = Position;
            orb.localScale = Vector3.one * (RadiusM * 2f * (pressed ? 1.3f : 1f));
        }

        // ------------------------------------------------------------------ IRayTarget

        public bool RayLive => Shown;

        public bool RayHit(Vector3 origin, Vector3 direction, float maxDistance, out float t, out Vector3 point)
        {
            t = 0f;
            point = Vector3.zero;
            if (!Shown) return false;
            Vector3 c = Position;
            float along = Vector3.Dot(c - origin, direction);
            if (along <= 0f || along > maxDistance) return false;
            Vector3 closest = origin + direction * along;
            if ((closest - c).magnitude > HitRadiusM) return false;
            t = along;
            point = closest;
            return true;
        }

        public void RayHover(Hand side, Vector3 point) => hoverUntil = Time.unscaledTime + 0.1f;

        public void RayPress(Hand side, Vector3 point)
        {
            pressed = true;
            VrAudio.Click(Position);
        }

        public void RayRelease(Hand side, Vector3 point, bool stillOver)
        {
            pressed = false;
            if (!stillOver) return;
            Presses++;
            VrAudio.Confirm(Position);
            VrPlacement.Instance?.EnterGodsEye();
        }

        public void RayCancel(Hand side) => pressed = false;

        public Dictionary<string, object> State() => new()
        {
            ["shown"] = Shown, ["lit"] = Lit, ["presses"] = Presses, ["position"] = new[] { Position.x, Position.y, Position.z }
        };
    }
}
