using System.Collections.Generic;
using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// Where the dance stands in the user's real room (passthrough mixed reality). Placed once at the start, then moved only by the user's
    /// pinches (owner 2026-11-15): ONE pinch and drag slides the floor (x, z), TWO pinches turn it about the hands' midpoint; never a scale,
    /// never a height change (DancePlacement has neither). A pinch that starts over a panel or with the palm menu up is not a grab.
    /// The start placement:
    ///   - The capture's world origin is mapped ONCE, when the lesson starts, to the origin (0, 0, 0) of the headset's tracking floor
    ///     (the Floor tracking origin: the centre of the Meta world floor), the capture's floor (y = 0) on the real floor, scale exactly 1.0.
    ///   - Nothing but the user's pinches moves it: it never drifts, re-centres or follows anything. The dancers keep their own
    ///     captured motion and travel across the floor around that origin (nothing cancels or re-centres their root motion).
    ///   - The one static offset allowed: a constant per lesson that puts the dance around the floor centre - the dancers' mean floor position
    ///     over the whole take (computed once at load) for a capture without its own origin, zero for a capture whose own origin (capture.json
    ///     "origin") already is that mean (the newer takes).
    ///   - A constant turn about the vertical axis is applied with it so the dancers' front (the start vantage's side) faces where the user
    ///     stands at the start.
    ///   - Only a teleport moves the origin relative to the user (immersive mode); in passthrough teleport is OFF (the user walks), so the
    ///     origin never moves at all.
    /// The dance itself is not moved: the VR rig carries the inverse of the placement (VrRig.ApplyPlacement), so every object of the viewer
    /// stays where the capture put it. When the headset recentres its tracking space the dance recentres with it (docs/QUEST.md).
    /// </summary>
    [DefaultExecutionOrder(120)]
    [AddComponentMenu("")]
    public sealed class VrPlacement : MonoBehaviour
    {
        public static VrPlacement Instance { get; private set; }

        /// <summary>the fixed mapping (null when not in a passthrough lesson)</summary>
        public DancePlacement Current { get; private set; }
        public bool Active { get; private set; }
        /// <summary>the static offset: the capture-world point (x, z) that stands at the tracking floor origin</summary>
        public Vector2 StaticOffset { get; private set; }
        public string OffsetSource { get; private set; } = "none";
        /// <summary>where the capture's origin stands in the real room right now (tracking floor coordinates of world (0,0,0)); constant while placed</summary>
        public Vector3 OriginReal
        {
            get
            {
                if (Current == null) return Vector3.zero;
                Vector2 o = Current.ToReal(Vector2.zero);
                return new Vector3(o.x, 0f, o.y);
            }
        }

        public int Placements { get; private set; }
        /// <summary>finished one-hand drags and two-hand turns</summary>
        public int Drags { get; private set; }
        public int Turns { get; private set; }

        readonly bool[] grabbed = new bool[2];
        int holding = -1;                 // bit mask of the hands holding the floor when the current grab began (-1 = none)
        DancePlacement grab0;
        Vector2 grabA, grabB;

        VrDisplay display;
        VrRig rig;

        public void Init(VrRig r, VrDisplay d)
        {
            rig = r;
            display = d;
        }

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>the lesson ended or the display left passthrough: the rig goes back to the floor origin</summary>
        public void Release()
        {
            Active = false;
            holding = -1;
            grabbed[0] = grabbed[1] = false;
            Current = null;
            if (rig != null) rig.ClearPlacement();
        }

        void Update()
        {
            VrInput input = VrInput.Instance;
            bool want = display != null && display.PassthroughActive && VrFilm.InLesson && rig != null && input != null && input.Source != null;
            if (!want)
            {
                if (Active) Release();
                return;
            }

            if (!Active)
            {
                Active = true;
                PlaceOnce();
            }

            UpdateGrab(input);
        }

        /// <summary>a hand's pinch point on the real floor (x, z of the tracking space)</summary>
        Vector2 Point(VrHand h)
        {
            Vector3 r = rig.ToReal(h.PinchPoint);
            return new Vector2(r.x, r.z);
        }

        void UpdateGrab(VrInput input)
        {
            VrPointers ptrs = VrPointers.Instance;
            bool menu = VrMenu.Instance != null && VrMenu.Instance.IsOpen;
            int mask = 0;
            VrHand[] hands = { input.Left, input.Right };
            for (int i = 0; i < 2; i++)
            {
                VrHand h = hands[i];
                if (!h.Tracked || !h.Pinch.Held || menu)
                {
                    grabbed[i] = false;
                    continue;
                }

                if (h.Pinch.Down) grabbed[i] = !h.Palm.Facing && (ptrs == null || !ptrs.OverUi(h.Side));
                if (grabbed[i]) mask |= 1 << i;
            }

            if (mask != holding)
            {
                if (holding == 3) Turns++;
                else if (holding > 0) Drags++;
                holding = mask == 0 ? -1 : mask;
                grab0 = Current.Clone();
                if (mask == 3)
                {
                    grabA = Point(input.Left);
                    grabB = Point(input.Right);
                }
                else if (mask != 0)
                {
                    grabA = Point(mask == 1 ? input.Left : input.Right);
                }

                return;
            }

            if (mask == 0) return;
            DancePlacement p = mask == 3
                ? PlacementMath.TwoHands(grab0, grabA, grabB, Point(input.Left), Point(input.Right))
                : PlacementMath.OneHand(grab0, grabA, Point(mask == 1 ? input.Left : input.Right));
            Current = p;
            rig.ApplyPlacement(p);
        }

        /// <summary>the dancers' mean floor position over the whole take (world, after the capture's own origin shift), computed once</summary>
        static bool MeanFloorPosition(HeadMovement hm, out Vector2 mean)
        {
            mean = Vector2.zero;
            Dancer lead = hm != null ? hm.LeadDancer : null, follow = hm != null ? hm.FollowDancer : null;
            if (lead == null || follow == null) return false;
            int n = Mathf.Min(lead.FrameCount, follow.FrameCount);
            double sx = 0, sz = 0;
            int count = 0;
            for (int f = 0; f < n; f += 3)
            {
                foreach (Dancer d in new[] { lead, follow })
                {
                    Vector3 p = d.Joint(f, SmplJoint.Pelvis);
                    if (float.IsNaN(p.x) || float.IsNaN(p.z)) continue;
                    sx += p.x;
                    sz += p.z;
                    count++;
                }
            }

            if (count == 0) return false;
            mean = new Vector2((float)(sx / count), (float)(sz / count));
            return true;
        }

        void PlaceOnce()
        {
            HeadMovement hm = HeadMovement.Instance;
            Vector2 offset = Vector2.zero;
            string source = "none";
            if (string.Equals(DanceOrigin.Source, "capture.json origin"))
            {
                source = "the capture's own origin is the dancers' mean floor position";
            }
            else if (MeanFloorPosition(hm, out Vector2 mean))
            {
                offset = mean;
                source = "mean floor position of the dancers over the whole take";
            }

            StaticOffset = offset;
            OffsetSource = source;
            // the front: the side the start vantage looks from; the dance is turned once so that side faces where the user stands now
            Vector3 pivot = new(offset.x, 0f, offset.y);
            float front = 180f;
            VrLocomotion loc = VrLocomotion.Instance;
            if (loc != null)
            {
                foreach (Viewpoint v in loc.Viewpoints)
                {
                    if (v.Id == "start")
                    {
                        front = ViewpointMath.Bearing(pivot, v.Head);
                        break;
                    }
                }
            }

            Vector3 head = rig.HeadReal;
            Vector2 toUser = new(head.x, head.z);
            float userBearing = toUser.magnitude > 0.7f ? PlacementMath.Bearing(toUser) : PlacementMath.Wrap(rig.HeadRealYawDeg + 180f);
            DancePlacement p = new() { Anchor = Vector2.zero, Pivot = pivot, YawDeg = PlacementMath.Wrap(userBearing - front) };
            Current = p;
            rig.ApplyPlacement(p);
            Placements++;
            Vector2 o = p.ToReal(Vector2.zero);
            Debug.Log($"VrPlacement: lesson '{VrFilm.Entry?.Id}' origin LOCKED: static offset ({offset.x:0.000}, {offset.y:0.000}) m [{source}] stands at the tracking floor origin (0, 0, 0); "
                + $"the capture's world origin (0,0,0) stands at ({o.x:0.000}, 0, {o.y:0.000}) in the room; turned {p.YawDeg:0.0} deg (front faces the user at {head.x:0.00}, {head.z:0.00}); "
                + $"scale {p.Scale:0.000} {(Mathf.Approximately(p.Scale, 1f) && Mathf.Approximately(rig.RigScale, 1f) ? "OK (exactly 1:1)" : "NOT 1:1")}; floor y {rig.FloorY:0.000}");
        }

        public Dictionary<string, object> State()
        {
            Dictionary<string, object> s = new()
            {
                ["active"] = Active, ["placements"] = Placements, ["drags"] = Drags, ["turns"] = Turns, ["holding"] = holding, ["rigScale"] = rig != null ? rig.RigScale : 1f, ["offsetSource"] = OffsetSource,
                ["staticOffset"] = new[] { StaticOffset.x, StaticOffset.y }
            };
            if (Current != null)
            {
                s["yawDeg"] = Current.YawDeg;
                s["scale"] = Current.Scale;
                s["originReal"] = new[] { OriginReal.x, OriginReal.z };
            }

            return s;
        }
    }
}
