using System.Collections.Generic;
using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// Where the dance stands in the user's real room (passthrough mixed reality, DancePlacement / PlacementMath).
    ///   - A lesson starts with the couple 2.2 m in front of the user, life size, on the real floor, the start vantage facing the user.
    ///   - PLACE mode (on at the start of a session until "Lock", and from the hand panel's Place tab): a pinch in the air (not on a panel)
    ///     holds the dance - slide the hand to slide the dance on the floor, twist the hand to turn it; pinch with BOTH hands and spread or
    ///     close them to make it larger or smaller (0.5x to 2x; the line between the hands also turns it). Buttons: turn 15 degrees, size 1:1,
    ///     "in front of me" (back to the start placement), "Lock here" (leaves Place mode; the dance stays put).
    ///   - Nothing is head-locked: the Place panel glides to the gaze like the captions.
    /// The dance itself is not moved: the VR rig carries the inverse of the placement (VrRig.ApplyPlacement), so every object of the
    /// viewer stays where the capture put it. The placement is not saved between sessions (the room's tracking origin can change); when the
    /// headset recentres, the dance recentres with it - docs/QUEST.md.
    /// </summary>
    [DefaultExecutionOrder(120)]
    [AddComponentMenu("")]
    public sealed class VrPlacement : MonoBehaviour
    {
        public static VrPlacement Instance { get; private set; }

        public const float DefaultDistance = 2.2f, TurnStepDeg = 15f, TwistDeadDeg = 14f;

        public bool PlaceMode { get; private set; }
        public DancePlacement Current { get; private set; }
        public bool Active { get; private set; }
        public bool Holding => grab != Grab.None;
        public int Grabs, TwoHandGrabs, Resets;
        public bool PanelShown => panel != null && panel.Alpha > 0.5f;
        public VrPanel Panel => panel;
        public Hand? HoldingHand => grab == Grab.One ? hand : null;

        enum Grab
        {
            None,
            One,
            Two
        }

        VrDisplay display;
        VrRig rig;
        VrPanel panel;
        VrWidget lockBtn, titleLabel;
        readonly LazyFollow follow = new() { Distance = 1.05f, DropM = 0.12f, ReengageDeg = 30f };
        LineRenderer ring;
        Material ringMat;
        readonly Vector3[] ringPoints = new Vector3[49];
        Grab grab;
        Hand hand;
        DancePlacement baseP, target;
        Vector2 a0, b0;
        float handYaw0;
        bool placedOnce, userLocked;

        public void Init(VrRig r, VrDisplay d)
        {
            rig = r;
            display = d;
        }

        void Awake()
        {
            Instance = this;
            panel = VrPanel.Create("VR place panel", new Vector2(560f, 372f), transform);
            BuildPanel();
            panel.Alpha = 0f;
            ringMat = VrDraw.Material(new Color(1f, 0.839f, 0.039f, 0.75f));
            ring = VrDraw.Line(transform, "place ring", ringMat, 0.012f);
            ring.loop = false;
            PlaceMode = true; // a new session: place it first
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (ringMat != null) Destroy(ringMat);
        }

        void BuildPanel()
        {
            float W = 560f, H = 372f;
            titleLabel = panel.AddLabel("title", "Place the dance", VrPanel.R(0f, H * 0.5f - 38f, W - 40f, 52f), 36f, VrStyle.Text, TextAnchor.MiddleCenter, true);
            panel.AddLabel("hint", "Pinch in the air and drag to slide it. Twist your hand to turn it. Pinch with both hands and spread to resize.",
                VrPanel.R(0f, H * 0.5f - 112f, W - 50f, 84f), 21f, VrStyle.Muted, TextAnchor.MiddleCenter, false, true);
            float y1 = 10f, y2 = -62f, y3 = -136f, bw = 250f;
            panel.AddButton("turn left", "Turn left", VrPanel.R(-bw * 0.5f - 10f, y1, bw, 60f), () => TurnBy(-TurnStepDeg), 25f);
            panel.AddButton("turn right", "Turn right", VrPanel.R(bw * 0.5f + 10f, y1, bw, 60f), () => TurnBy(TurnStepDeg), 25f);
            panel.AddButton("size 1:1", "Size 1:1", VrPanel.R(-bw * 0.5f - 10f, y2, bw, 60f), ResetSize, 25f);
            panel.AddButton("in front of me", "In front of me", VrPanel.R(bw * 0.5f + 10f, y2, bw, 60f), () => ResetInFront(), 25f);
            lockBtn = panel.AddButton("lock", "Lock here", VrPanel.R(0f, y3, W - 40f, 66f), () => SetPlaceMode(false), 30f);
            lockBtn.Normal = VrStyle.Accent * 0.9f;
            lockBtn.Hot = VrStyle.Accent;
            lockBtn.Normal.a = 1f;
            lockBtn.Hot.a = 1f;
            lockBtn.Pressed = Color.white;
            lockBtn.LabelNormal = VrStyle.Ink;
        }

        // ------------------------------------------------------------------ the placement

        /// <summary>the lesson's start placement in front of the user (also "In front of me")</summary>
        public void ResetInFront(bool keepScale = false)
        {
            if (rig == null) return;
            HeadMovement hm = HeadMovement.Instance;
            Vector3 centre = VrViewpoints.CoupleCentre(hm);
            float front = 180f;
            VrLocomotion loc = VrLocomotion.Instance;
            if (loc != null)
            {
                foreach (Viewpoint v in loc.Viewpoints)
                {
                    if (v.Id == "start")
                    {
                        front = ViewpointMath.Bearing(centre, v.Head);
                        break;
                    }
                }
            }

            Vector3 head = rig.HeadReal;
            DancePlacement p = PlacementMath.Default(head, rig.HeadRealYawDeg, centre, front, DefaultDistance);
            if (keepScale && Current != null) p.Scale = Current.Scale;
            Set(p);
            placedOnce = true;
            Resets++;
        }

        public void ResetSize()
        {
            if (Current == null) return;
            // 1:1 about the pivot's place in the room
            DancePlacement p = Current.Clone();
            p.Scale = 1f;
            Set(p);
        }

        public void TurnBy(float deg)
        {
            if (Current == null) return;
            Set(PlacementMath.TurnAboutPivot(Current, deg));
        }

        void Set(DancePlacement p)
        {
            Current = p;
            target = p;
            if (rig != null) rig.ApplyPlacement(p);
        }

        public void SetPlaceMode(bool on)
        {
            PlaceMode = on;
            if (!on)
            {
                userLocked = true;
                grab = Grab.None;
            }
        }

        public void TogglePlaceMode() => SetPlaceMode(!PlaceMode);

        /// <summary>the lesson ended or the display left passthrough: the rig goes back to the floor origin</summary>
        public void Release()
        {
            Active = false;
            grab = Grab.None;
            Current = null;
            placedOnce = false;
            if (rig != null) rig.ClearPlacement();
        }

        // ------------------------------------------------------------------ per frame

        void Update()
        {
            VrInput input = VrInput.Instance;
            VrPointers ptrs = VrPointers.Instance;
            bool want = display != null && display.PassthroughActive && VrFilm.InLesson && rig != null && input != null && input.Source != null;
            if (!want)
            {
                if (Active) Release();
                panel.Alpha = Mathf.MoveTowards(panel.Alpha, 0f, Time.unscaledDeltaTime * 6f);
                panel.Interactive = false;
                ring.enabled = false;
                return;
            }

            if (!Active)
            {
                Active = true;
                placedOnce = false;
                PlaceMode = !userLocked;
                follow.Reset();
            }

            if (!placedOnce || Current == null) ResetInFront();
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            UpdateGrab(input, ptrs, dt);
            UpdatePanel(input, dt);
            UpdateRing();
        }

        Vector2 PinchReal(VrHand h)
        {
            Vector3 r = rig.ToReal(h.PinchPoint);
            return new Vector2(r.x, r.z);
        }

        bool HeadingReal(VrHand h, out float yaw)
        {
            Vector3 f = rig.DirToReal(h.State.MiddleKnuckle - h.State.Wrist);
            yaw = 0f;
            if (new Vector2(f.x, f.z).magnitude < 0.04f * rig.RigScale) return false;
            yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            return true;
        }

        void BeginOne(VrHand h)
        {
            grab = Grab.One;
            hand = h.Side;
            baseP = Current.Clone();
            a0 = PinchReal(h);
            if (!HeadingReal(h, out handYaw0)) handYaw0 = 0f;
            Grabs++;
        }

        void BeginTwo(VrHand l, VrHand r)
        {
            grab = Grab.Two;
            baseP = Current.Clone();
            a0 = PinchReal(l);
            b0 = PinchReal(r);
            TwoHandGrabs++;
        }

        void UpdateGrab(VrInput input, VrPointers ptrs, float dt)
        {
            VrHand L = input.Left, R = input.Right;
            bool canStart = PlaceMode && !rig.Blinking;
            bool lFree = L.Tracked && !ptrs.OverUi(Hand.Left), rFree = R.Tracked && !ptrs.OverUi(Hand.Right);
            switch (grab)
            {
                case Grab.None:
                    if (!canStart) break;
                    if (L.Pinch.Down && lFree && R.Pinch.Held && R.Tracked && !ptrs.OverUi(Hand.Right)) BeginTwo(L, R);
                    else if (R.Pinch.Down && rFree && L.Pinch.Held && L.Tracked && !ptrs.OverUi(Hand.Left)) BeginTwo(L, R);
                    else if (R.Pinch.Down && rFree) BeginOne(R);
                    else if (L.Pinch.Down && lFree) BeginOne(L);
                    break;
                case Grab.One:
                {
                    VrHand h = input.Get(hand), o = input.Get(hand.Other());
                    if (!PlaceMode || !h.Pinch.Held || !h.Tracked)
                    {
                        grab = Grab.None;
                        break;
                    }

                    if (o.Pinch.Down && o.Tracked && !ptrs.OverUi(o.Side))
                    {
                        BeginTwo(L, R);
                        break;
                    }

                    Vector2 p = PinchReal(h);
                    float twist = 0f;
                    if (HeadingReal(h, out float yaw))
                    {
                        float d = PlacementMath.Wrap(yaw - handYaw0);
                        // a pinch is never perfectly still: only a deliberate twist (past the dead zone) turns the dance
                        twist = Mathf.Sign(d) * Mathf.Max(0f, Mathf.Abs(d) - TwistDeadDeg);
                    }

                    target = PlacementMath.OneHand(baseP, a0, 0f, p, twist);
                    break;
                }
                case Grab.Two:
                {
                    if (!PlaceMode || !L.Tracked || !R.Tracked || (!L.Pinch.Held && !R.Pinch.Held))
                    {
                        grab = Grab.None;
                        break;
                    }

                    if (!L.Pinch.Held || !R.Pinch.Held)
                    {
                        // one hand let go: keep holding with the other, from where the dance is now
                        VrHand keep = L.Pinch.Held ? L : R;
                        Current = target.Clone();
                        BeginOne(keep);
                        Grabs--;
                        break;
                    }

                    target = PlacementMath.TwoHands(baseP, a0, b0, PinchReal(L), PinchReal(R));
                    break;
                }
            }

            if (grab != Grab.None && Current != null)
            {
                // follow the hands a little smoothed (tracking noise must not shake the dancers)
                float k = 1f - Mathf.Exp(-22f * dt);
                DancePlacement c = Current.Clone();
                c.Anchor = Vector2.Lerp(Current.Anchor, target.Anchor, k);
                c.YawDeg = Mathf.LerpAngle(Current.YawDeg, target.YawDeg, k);
                c.Scale = Mathf.Lerp(Current.Scale, target.Scale, k);
                Current = c;
                rig.ApplyPlacement(c);
            }
        }

        void UpdatePanel(VrInput input, float dt)
        {
            bool show = PlaceMode;
            panel.Alpha = Mathf.MoveTowards(panel.Alpha, show ? 1f : 0f, dt * 6f);
            panel.Interactive = show;
            if (panel.Alpha <= 0.002f) return;
            InputFrame f = input.Frame;
            // off to the left of the gaze: the free pinches that hold the dance are made in front of the body, and must not hit this panel
            Vector3 p = follow.Step(panel.transform.position, f.HeadPosition, f.HeadRotation, f.HeadYaw * Quaternion.Euler(0f, -50f, 0f), dt);
            panel.FaceFrom(p, f.HeadPosition, 0.5f);
            titleLabel.Label.text = grab == Grab.Two ? $"Size {Current.Scale:0.00}x" : grab == Grab.One ? "Holding it" : "Place the dance";
        }

        /// <summary>a ring on the floor around the couple's start while placing: where the dance is and which way it faces</summary>
        void UpdateRing()
        {
            if (!PlaceMode || Current == null)
            {
                ring.enabled = false;
                return;
            }

            Vector3 c = VrViewpoints.CoupleCentre(HeadMovement.Instance);
            float rad = 1.1f;
            for (int i = 0; i < 48; i++)
            {
                float a = i / 48f * Mathf.PI * 2f;
                ringPoints[i] = new Vector3(c.x + Mathf.Sin(a) * rad, 0.012f, c.z + Mathf.Cos(a) * rad);
            }

            ringPoints[48] = ringPoints[0];
            ring.enabled = true;
            ring.positionCount = ringPoints.Length;
            ring.SetPositions(ringPoints);
            ring.widthMultiplier = 0.012f * rig.RigScale;
        }

        public Dictionary<string, object> State()
        {
            Dictionary<string, object> s = new()
            {
                ["active"] = Active, ["placeMode"] = PlaceMode, ["holding"] = grab.ToString().ToLowerInvariant(), ["grabs"] = Grabs, ["twoHandGrabs"] = TwoHandGrabs,
                ["resets"] = Resets, ["panelShown"] = PanelShown, ["rigScale"] = rig != null ? rig.RigScale : 1f
            };
            if (Current != null)
            {
                s["anchor"] = new[] { Current.Anchor.x, Current.Anchor.y };
                s["yawDeg"] = Current.YawDeg;
                s["scale"] = Current.Scale;
            }

            return s;
        }
    }
}
