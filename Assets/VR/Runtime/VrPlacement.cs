using System.Collections.Generic;
using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// Where the dance stands in the user's real room (passthrough mixed reality, always on). Owner 2026-11-16: "once the position is determined
    /// where the user is standing, the floor should not move at all. The dancers are going to move around in the room, and the origin point
    /// needs to stay completely fixed ... two pinch only to do floor motion so that we avoid accidental pinch motions":
    ///   - The start: once, when a lesson starts and the head is tracked, the centre of the WHOLE dance (its floor footprint, VrStage) stands
    ///     straight ahead of the user's gaze, just outside the dancers' travel (FootprintMath.ViewDistance, at least MinStartDistance), the
    ///     dancers' front (the start vantage's side) toward the user, life size, the capture's floor on the real floor.
    ///   - After that the floor and the origin are FIXED. Nothing follows the dancers, drifts or re-centres: the couple dances across the room
    ///     on their captured paths.
    ///   - Only TWO hands move the floor: pinch with both (where no panel is, palm menu down) and drag. The floor slides with the hands'
    ///     midpoint and turns with the line between them, about that midpoint (PlacementMath.TwoHands): never scaled, never lifted. A
    ///     one-hand pinch never moves anything.
    ///   - The god's eye: an invisible button about 10 ft above the dancers' heads (VrSkyTarget) lights up when the pointing ray is on it; a
    ///     pinch on it lifts the user above the centre of the dance, looking down (the dance floor goes below the real floor). A short pinch
    ///     aimed at the dance floor brings the user back down to exactly the placement they had.
    /// The dance itself is not moved: the VR rig carries the inverse of the placement (VrRig.ApplyPlacement), so every object of the viewer stays
    /// where the capture put it; the rig is only touched when the placement changes. It runs before VrInput (-300) reads the hands, so the
    /// hands, the pointers and the wrist panel see one rig pose per frame; the pinches it reads are the previous frame's.
    /// </summary>
    [DefaultExecutionOrder(-320)]
    [AddComponentMenu("")]
    public sealed class VrPlacement : MonoBehaviour
    {
        public static VrPlacement Instance { get; private set; }

        public const float MinStartDistance = 2.5f, FallbackDistance = 2.9f;
        public const float TransitionS = 0.7f, TapS = 0.6f, TapTravelM = 0.03f;

        /// <summary>the mapping in force (null when not in a passthrough lesson)</summary>
        public DancePlacement Current { get; private set; }
        public bool Active { get; private set; }
        public bool GodsEyeOn { get; private set; }
        public bool Transitioning => transitionT < 1f;
        public bool TwoHandHold { get; private set; }
        /// <summary>where the capture's origin (world 0,0,0) stands in the real room right now (tracking floor x, z)</summary>
        public Vector3 OriginReal
        {
            get
            {
                if (Current == null) return Vector3.zero;
                Vector2 o = Current.ToReal(Vector2.zero);
                return new Vector3(o.x, Current.Lift, o.y);
            }
        }

        /// <summary>the dance's footprint centre (world floor x, z): what stands in front of the user at the start</summary>
        public Vector2 StageCentre { get; private set; }
        public string OffsetSource { get; private set; } = "none";
        public float StartDistance { get; private set; }

        public int Placements { get; private set; }
        /// <summary>finished two-hand holds that moved the floor; one-hand pinches that never moved it</summary>
        public int Moves { get; private set; }
        public int OneHandPinches { get; private set; }
        public int GodsEyes { get; private set; }
        public int Grounds { get; private set; }
        public int Changes { get; private set; }

        // per hand: did this pinch start where a floor move may start (no panel, no palm menu), where, when
        readonly bool[] eligible = new bool[2];
        readonly Vector2[] downAt = new Vector2[2];
        readonly float[] downTime = new float[2];
        readonly float[] travel = new float[2];
        readonly bool[] wasHeld = new bool[2];
        bool usedInTwoHands;
        DancePlacement hold0, from, to, beforeGodsEye;
        Vector2 holdA, holdB;
        float transitionT = 1f;
        bool dirty;

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

        /// <summary>the lesson ended or passthrough failed: the rig goes back to the floor origin</summary>
        public void Release()
        {
            Active = false;
            GodsEyeOn = false;
            TwoHandHold = false;
            transitionT = 1f;
            Current = null;
            beforeGodsEye = null;
            for (int i = 0; i < 2; i++) eligible[i] = wasHeld[i] = false;
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
                // wait for a real head pose: before the first one the head is the default (0, 1.6, 0) looking along +Z
                if (!input.Source.IsSimulated && rig.HeadStatus != "tracked") return;
                Active = true;
                PlaceOnce();
            }

            UpdateHands(input);
            if (transitionT < 1f)
            {
                transitionT = Mathf.Min(1f, transitionT + Time.unscaledDeltaTime / TransitionS);
                Current = PlacementMath.Lerp(from, to, Mathf.SmoothStep(0f, 1f, transitionT));
                dirty = true;
            }

            if (dirty)
            {
                rig.ApplyPlacement(Current);
                Changes++;
                dirty = false;
            }
        }

        /// <summary>a hand's pinch point on the real floor (x, z of the tracking space)</summary>
        Vector2 Point(VrHand h)
        {
            Vector3 r = rig.ToReal(h.PinchPoint);
            return new Vector2(r.x, r.z);
        }

        void UpdateHands(VrInput input)
        {
            VrPointers ptrs = VrPointers.Instance;
            bool menu = VrMenu.Instance != null && VrMenu.Instance.IsOpen;
            VrHand[] hands = { input.Left, input.Right };
            for (int i = 0; i < 2; i++)
            {
                VrHand h = hands[i];
                bool held = h.Tracked && h.Pinch.Held && !menu;
                if (held && h.Pinch.Down)
                {
                    // decided when the pinch closes: a pinch that began on a panel, on the god's eye button or with the palm menu up never moves the floor
                    eligible[i] = !h.Palm.Facing && (ptrs == null || !ptrs.OverUi(h.Side));
                    downAt[i] = Point(h);
                    downTime[i] = Time.unscaledTime;
                    travel[i] = 0f;
                }

                if (held && eligible[i]) travel[i] = Mathf.Max(travel[i], (Point(h) - downAt[i]).magnitude);
                if (!held && wasHeld[i]) OnRelease(h, i);
                if (!held) eligible[i] = false;
                wasHeld[i] = held;
            }

            bool both = eligible[0] && eligible[1] && wasHeld[0] && wasHeld[1] && !Transitioning;
            if (both && !TwoHandHold)
            {
                TwoHandHold = true;
                usedInTwoHands = true;
                hold0 = Current.Clone();
                holdA = Point(input.Left);
                holdB = Point(input.Right);
            }
            else if (!both && TwoHandHold)
            {
                TwoHandHold = false;
                Moves++;
                Debug.Log($"VrPlacement: floor moved with two hands: origin now at ({OriginReal.x:0.000}, {OriginReal.z:0.000}) in the room, turned {Current.YawDeg:0.0} deg");
            }

            if (TwoHandHold)
            {
                Current = PlacementMath.TwoHands(hold0, holdA, holdB, Point(input.Left), Point(input.Right));
                dirty = true;
            }

            if (!wasHeld[0] && !wasHeld[1]) usedInTwoHands = false;
        }

        void OnRelease(VrHand h, int i)
        {
            if (usedInTwoHands || !eligible[i]) return;
            OneHandPinches++;
            // a short pinch aimed at the dance floor brings the user down from the god's eye; on the ground a one-hand pinch does nothing
            if (GodsEyeOn && Time.unscaledTime - downTime[i] <= TapS && travel[i] < TapTravelM && AimsAtFloor(h)) ReturnToGround();
        }

        static bool AimsAtFloor(VrHand h)
        {
            if (!h.Aim.Valid) return false;
            Vector3 o = h.Aim.Origin, d = h.Aim.Direction;
            if (d.y > -0.1f || o.y <= 0f) return false;
            return -o.y / d.y < 60f; // the dance floor is world y = 0
        }

        void StartTransition(DancePlacement target)
        {
            from = Current.Clone();
            to = target;
            transitionT = 0f;
            TwoHandHold = false;
        }

        /// <summary>up above the centre of the dance, looking down: the eyes at the god's eye button's height above the dance floor</summary>
        public void EnterGodsEye()
        {
            if (!Active || GodsEyeOn) return;
            beforeGodsEye = Current.Clone();
            GodsEyeOn = true;
            GodsEyes++;
            float eye = VrSkyTarget.EyeAboveFloorM;
            StartTransition(PlacementMath.GodsEye(Current, rig.HeadReal, rig.HeadRealYawDeg, eye));
            VrAudio.Whoosh(rig.Head.position);
            Debug.Log($"VrPlacement: god's eye, {eye:0.00} m above the dance floor over the centre of the dance");
        }

        /// <summary>back on the ground, exactly where the floor was before going up</summary>
        public void ReturnToGround()
        {
            if (!Active || !GodsEyeOn) return;
            GodsEyeOn = false;
            Grounds++;
            DancePlacement back = beforeGodsEye ?? Current.Clone();
            back.Lift = 0f;
            StartTransition(back);
            VrAudio.Whoosh(rig.Head.position);
            Debug.Log("VrPlacement: back on the ground, the floor where it was");
        }

        /// <summary>
        /// A named vantage, for the bench (-hmview) and the CLI only (the wrist menu has no viewpoints in passthrough: the floor moves only with
        /// two hands): the dance turned so the user sees it from the viewpoint's side at its distance; "overhead" is the god's eye.
        /// </summary>
        public bool GoTo(Viewpoint vp)
        {
            if (!Active) return false;
            float bearing = vp.YawDeg + 180f; // the viewpoint faces the dance: the dance sees it from the opposite bearing
            float d = Mathf.Clamp(new Vector2(vp.Head.x - StageCentre.x, vp.Head.z - StageCentre.y).magnitude, 1.2f, 8f);
            Vector3 head = rig.HeadReal;
            DancePlacement p = PlacementMath.Default(head, rig.HeadRealYawDeg, Current.Pivot, bearing, d);
            bool overhead = vp.Kind == "overhead" || vp.Id == "overhead";
            if (overhead)
            {
                beforeGodsEye = p.Clone();
                p = PlacementMath.GodsEye(p, head, rig.HeadRealYawDeg, VrSkyTarget.EyeAboveFloorM);
                if (!GodsEyeOn) GodsEyes++;
            }

            GodsEyeOn = overhead;
            StartTransition(p);
            return true;
        }

        void PlaceOnce()
        {
            HeadMovement hm = HeadMovement.Instance;
            DanceFootprint fp = VrStage.Footprint(hm);
            Vector2 centre = fp.Samples > 0 ? fp.Centre : Vector2.zero;
            float distance = fp.Samples > 0 ? Mathf.Max(MinStartDistance, FootprintMath.ViewDistance(fp, 0f)) : FallbackDistance;
            StageCentre = centre;
            StartDistance = distance;
            OffsetSource = fp.Samples > 0 ? $"footprint centre of the whole dance, {distance:0.00} m ahead of the user" : "no footprint: the capture origin";
            Vector3 pivot = new(centre.x, 0f, centre.y);
            // the front: the side the start vantage looks from; that side faces the user
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
            Current = PlacementMath.Default(head, rig.HeadRealYawDeg, pivot, front, distance);
            GodsEyeOn = false;
            transitionT = 1f;
            rig.ApplyPlacement(Current);
            Placements++;
            Vector2 c = Current.Anchor;
            Debug.Log($"VrPlacement: lesson '{VrFilm.Entry?.Id}' placed: the dance's centre ({centre.x:0.000}, {centre.y:0.000}) [{OffsetSource}] stands at ({c.x:0.000}, 0, {c.y:0.000}) "
                + $"in the room, straight ahead of the user at ({head.x:0.00}, {head.z:0.00}) looking {rig.HeadRealYawDeg:0.0} deg; footprint radius {fp.Radius:0.00} m (p98 {fp.RadiusP98:0.00}); "
                + $"turned {Current.YawDeg:0.0} deg; scale {Current.Scale:0.000} {(Mathf.Approximately(Current.Scale, 1f) && Mathf.Approximately(rig.RigScale, 1f) ? "OK (exactly 1:1)" : "NOT 1:1")}; "
                + $"floor y {rig.FloorY:0.000}; fixed from now on (two-hand pinch-drag moves it)");
        }

        public Dictionary<string, object> State()
        {
            Dictionary<string, object> s = new()
            {
                ["active"] = Active, ["placements"] = Placements, ["moves"] = Moves, ["oneHandPinches"] = OneHandPinches, ["twoHandHold"] = TwoHandHold,
                ["godsEye"] = GodsEyeOn, ["godsEyes"] = GodsEyes, ["grounds"] = Grounds, ["transitioning"] = Transitioning, ["changes"] = Changes,
                ["rigScale"] = rig != null ? rig.RigScale : 1f, ["offsetSource"] = OffsetSource, ["startDistance"] = StartDistance,
                ["stageCentre"] = new[] { StageCentre.x, StageCentre.y }
            };
            if (Current != null)
            {
                s["yawDeg"] = Current.YawDeg;
                s["lift"] = Current.Lift;
                s["anchor"] = new[] { Current.Anchor.x, Current.Anchor.y };
                s["originReal"] = new[] { OriginReal.x, OriginReal.z };
            }

            return s;
        }
    }
}
