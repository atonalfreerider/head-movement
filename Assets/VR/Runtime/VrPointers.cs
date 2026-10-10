using System;
using System.Collections.Generic;
using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// Hands acting on panels, two ways, with the same widgets:
    ///  - POKE: the index fingertip pushes into a widget (PokeDetector: a press on the frame the tip reaches the surface); a slider is
    ///    dragged by keeping the tip pressed in.
    ///  - FAR RAY + PINCH: the pointing ray (AimRay) hovers a widget; a pinch presses it; a button clicks when the pinch opens with the ray
    ///    still on it (move away to cancel); a slider follows the ray while pinched.
    /// A hand that is near a panel (within poking distance) shows no ray. Hover is lit on the widget, clicks and hover changes play a
    /// sound. The pointers read hit-testing straight from the panels' geometry: no colliders, no event system.
    /// </summary>
    [DefaultExecutionOrder(-250)]
    [AddComponentMenu("")]
    public sealed class VrPointers : MonoBehaviour
    {
        public static VrPointers Instance { get; private set; }

        public const float PokeNear = 0.10f, RayMaxDistance = 8f, RayMarginM = 0.025f, PokeMarginM = 0.008f, WidgetMarginMm = 7f;

        public sealed class Ptr
        {
            public VrHand Hand;
            public readonly PokeDetector Poke = new();
            public VrWidget Hover, PokeHover, Pressed, Drag;
            public VrPanel HitPanel;
            public Vector3 HitPoint;
            public bool NearPanel, RayHit, PokePressed;
            public float HitDistance;
            public LineRenderer Line;
            public Transform Reticle;
            public MeshRenderer ReticleRenderer;
            public string LastClick;
            public float LastPokePress = -10f;
            public bool PressedFar;
        }

        public readonly Ptr[] Pointers = new Ptr[2];
        public int Clicks { get; private set; }
        public readonly System.Collections.Generic.List<string> ClickLog = new();
        public string LastClick { get; private set; }
        public event Action<VrWidget, Hand, bool> Clicked;
        public bool SuppressRays;

        Material rayMat, reticleMat;
        readonly List<VrWidget> live = new();
        readonly Dictionary<VrWidget, float> lastClickTime = new();

        void Awake()
        {
            Instance = this;
            rayMat = VrDraw.Material(new Color(0.8f, 0.9f, 1f, 0.75f), true);
            reticleMat = VrDraw.Material(new Color(1f, 1f, 1f, 0.95f), true);
            for (int i = 0; i < 2; i++)
            {
                Ptr p = new();
                p.Line = VrDraw.Line(transform, "pointer ray " + (Hand)i, rayMat, 0.004f);
                GameObject r = new("reticle " + (Hand)i, typeof(MeshFilter), typeof(MeshRenderer));
                r.layer = VrStyle.Layer;
                r.transform.SetParent(transform, false);
                r.GetComponent<MeshFilter>().sharedMesh = VrDraw.Sphere;
                p.ReticleRenderer = r.GetComponent<MeshRenderer>();
                p.ReticleRenderer.sharedMaterial = reticleMat;
                p.ReticleRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                p.ReticleRenderer.sortingOrder = 31000;
                p.Reticle = r.transform;
                p.Reticle.localScale = Vector3.one * 0.014f;
                p.ReticleRenderer.enabled = false;
                Pointers[i] = p;
            }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (rayMat != null) Destroy(rayMat);
            if (reticleMat != null) Destroy(reticleMat);
        }

        public Ptr Get(Hand h) => Pointers[(int)h];

        /// <summary>the pointer of this hand is over a panel or near one: pinching must not teleport or turn</summary>
        public bool OverUi(Hand h)
        {
            Ptr p = Pointers[(int)h];
            return p.HitPanel != null || p.NearPanel || p.Drag != null || p.Pressed != null;
        }

        void Update()
        {
            VrInput input = VrInput.Instance;
            if (input == null || input.Source == null) return;
            live.Clear();
            foreach (VrPanel panel in VrPanel.All)
            {
                if (!panel.IsLive) continue;
                foreach (VrWidget w in panel.Widgets)
                {
                    if (w.Kind == WidgetKind.Static) continue;
                    w.Hovered = false;
                    if (!(Pointers[0].Drag == w || Pointers[1].Drag == w)) w.Pushed = false;
                    live.Add(w);
                }
            }

            for (int i = 0; i < 2; i++)
            {
                Ptr p = Pointers[i];
                p.Hand = input.Get((Hand)i);
                Process(p, Time.unscaledDeltaTime);
            }
        }

        void Process(Ptr p, float dt)
        {
            VrHand hand = p.Hand;
            Hand side = hand.Side;
            bool tracked = hand.Tracked;
            p.RayHit = false;
            p.HitPanel = null;
            p.NearPanel = false;
            VrWidget prevHover = p.Hover;
            p.Hover = null;

            // a lost hand lets go of whatever it held
            if (!tracked && (p.Drag != null || p.Pressed != null))
            {
                EndDrag(p);
                p.Pressed = null;
            }

            // ---- poke: the nearest panel face the fingertip is over
            VrPanel pokePanel = null;
            float pokeDepth = 1f;
            Vector2 pokeLocal = default;
            if (tracked)
            {
                Vector3 tip = hand.IndexTip;
                foreach (VrPanel panel in VrPanel.All)
                {
                    if (!panel.IsLive || panel.AttachedTo == side) continue;
                    RectPlane plane = panel.Plane;
                    float d = plane.Depth(tip);
                    if (d > 0.12f || d < -0.07f) continue;
                    Vector2 local = plane.Local(tip);
                    if (!plane.Contains(local, PokeMarginM + 0.01f)) continue;
                    if (pokePanel == null || Mathf.Abs(d) < Mathf.Abs(pokeDepth))
                    {
                        pokePanel = panel;
                        pokeDepth = d;
                        pokeLocal = local;
                    }
                }
            }

            bool near = pokePanel != null && pokeDepth <= PokeNear && !p.PressedFar;
            p.NearPanel = near;
            VrWidget pokeWidget = null;
            if (pokePanel != null)
            {
                pokeWidget = WidgetAt(pokePanel, pokeLocal / pokePanel.MetersPerMm, 4f);
                if (pokeWidget != null && !pokeWidget.Live) pokeWidget = null;
            }

            if (pokeWidget != p.PokeHover)
            {
                p.Poke.Reset();
                if (p.Drag != null && p.PokePressed) EndDrag(p);
                p.PokeHover = pokeWidget;
            }

            if (tracked && pokeWidget != null)
            {
                // only a finger that points into the surface presses it (a hand sweeping past, a knuckle brushing it, does not)
                Vector3 fingerDir = (hand.State.IndexTip - hand.State.Joints[HandJoint.IndexIntermediate]).normalized;
                bool pointing = Vector3.Dot(fingerDir, -pokePanel.Plane.Normal) > 0.25f;
                bool started = p.Poke.Update(pokeDepth, pointing, true);
                if (started && Time.unscaledTime - p.LastPokePress < 0.35f) started = false; // a bouncing or sweeping fingertip presses once
                if (started) p.LastPokePress = Time.unscaledTime;
                if (pokeDepth <= 0.07f) pokeWidget.Hovered = true;
                p.Hover = pokeWidget;
                if (started) BeginPress(p, pokeWidget, true);
                if (p.Poke.Pressed)
                {
                    pokeWidget.Pushed = true;
                    if (p.Drag == pokeWidget) DragTo(pokeWidget, pokeLocal.x / pokeWidget.Panel.MetersPerMm);
                }
                else if (p.Drag != null && p.PokePressed)
                {
                    EndDrag(p);
                }
            }
            else
            {
                p.Poke.Update(1f, false, tracked);
                if (p.Drag != null && p.PokePressed) EndDrag(p);
            }

            if (!p.Poke.Pressed) p.PokePressed = false;

            // ---- far ray
            bool rayOn = tracked && hand.Aim.Valid && !near && !SuppressRays;
            Vector3 end = default;
            if (rayOn)
            {
                Vector3 o = hand.Aim.Origin, dir = hand.Aim.Direction;
                float bestT = RayMaxDistance;
                VrPanel bestPanel = null;
                Vector2 bestLocal = default;
                foreach (VrPanel panel in VrPanel.All)
                {
                    if (!panel.IsLive || panel.AttachedTo == side) continue;
                    RectPlane plane = panel.Plane;
                    if (!plane.Raycast(o, dir, out float t, out Vector2 local) || t > bestT) continue;
                    if (!plane.Contains(local, RayMarginM)) continue;
                    bestT = t;
                    bestPanel = panel;
                    bestLocal = local;
                }

                if (bestPanel != null)
                {
                    p.HitPanel = bestPanel;
                    p.HitPoint = o + dir * bestT;
                    p.HitDistance = bestT;
                    p.RayHit = true;
                    end = p.HitPoint;
                    VrWidget w = WidgetAt(bestPanel, bestLocal / bestPanel.MetersPerMm, WidgetMarginMm);
                    if (w != null && !w.Live) w = null;
                    p.Hover = w;
                    if (w != null) w.Hovered = true;
                }
                else
                {
                    end = o + dir * 1.2f;
                }

                // pinch: press on the way down, click on the way up
                if (hand.Pinch.Down && p.Hover != null)
                {
                    BeginPress(p, p.Hover, false);
                    p.PressedFar = true;
                }

                if (p.Pressed != null && !p.PokePressed)
                {
                    p.Pressed.Pushed = true;
                    if (p.Drag != null && p.Drag == p.Pressed) DragRay(p, o, dir);
                    if (hand.Pinch.Up || !hand.Pinch.Held)
                    {
                        VrWidget w2 = p.Pressed;
                        bool over = p.Hover == w2;
                        if (p.Drag != null) EndDrag(p);
                        else if (w2.Kind == WidgetKind.Button && over) Click(w2, side, false);
                        p.Pressed = null;
                        p.PressedFar = false;
                    }
                }
            }
            else if (p.PressedFar)
            {
                // the ray went away (hand near a panel or lost) while pinching: cancel
                if (p.Drag != null) EndDrag(p);
                p.Pressed = null;
                p.PressedFar = false;
            }

            if (p.Hover != prevHover && p.Hover != null) VrAudio.Tick(p.Hover.WorldCentre);
            DrawRay(p, rayOn, end, hand);
        }

        void DrawRay(Ptr p, bool rayOn, Vector3 end, VrHand hand)
        {
            bool show = rayOn && !SuppressRays && (p.RayHit || p.Pressed != null);
            if (show && VrLocomotion.Instance != null && VrLocomotion.Instance.Aiming(hand.Side)) show = false;
            p.Line.enabled = show;
            p.ReticleRenderer.enabled = show && p.RayHit;
            if (!show) return;
            Vector3 o = hand.Aim.Origin;
            p.Line.positionCount = 2;
            p.Line.SetPosition(0, o);
            p.Line.SetPosition(1, p.RayHit ? p.HitPoint : end);
            if (p.RayHit)
            {
                p.Reticle.position = p.HitPoint;
                float s = 0.012f + 0.004f * p.HitDistance * 0.3f;
                p.Reticle.localScale = Vector3.one * (hand.Pinch.Held ? s * 0.7f : s);
            }

            VrDraw.SetColour(rayMat, hand.Pinch.Held ? VrStyle.Accent : new Color(0.8f, 0.9f, 1f, 0.7f));
        }

        // ------------------------------------------------------------------ pressing, clicking, dragging

        void BeginPress(Ptr p, VrWidget w, bool poke)
        {
            if (w.Kind == WidgetKind.Slider)
            {
                p.Pressed = w;
                p.Drag = w;
                p.PokePressed = poke;
                w.OnDragBegin?.Invoke();
                VrAudio.Click(w.WorldCentre);
                return;
            }

            if (poke)
            {
                p.PokePressed = true;
                Click(w, p.Hand.Side, true);
            }
            else
            {
                p.Pressed = w;
            }
        }

        void Click(VrWidget w, Hand side, bool poke)
        {
            // one click per widget per quarter second: a bouncing fingertip is one press
            if (lastClickTime.TryGetValue(w, out float last) && Time.unscaledTime - last < 0.25f) return;
            lastClickTime[w] = Time.unscaledTime;
            Clicks++;
            LastClick = w.Id;
            ClickLog.Add($"{Time.unscaledTime:0.00} {w.Id} {side} {(poke ? "poke" : "ray")}");
            if (ClickLog.Count > 12) ClickLog.RemoveAt(0);
            Pointers[(int)side].LastClick = w.Id;
            VrAudio.Click(w.WorldCentre);
            w.Click();
            Clicked?.Invoke(w, side, poke);
        }

        void DragTo(VrWidget w, float localXmm)
        {
            float u = SliderMath.U(localXmm - w.RectMm.center.x, w.RectMm.width);
            w.OnDrag?.Invoke(u);
        }

        void DragRay(Ptr p, Vector3 o, Vector3 dir)
        {
            VrWidget w = p.Drag;
            RectPlane plane = w.Panel.Plane;
            if (!plane.Raycast(o, dir, out _, out Vector2 local)) return;
            DragTo(w, local.x / w.Panel.MetersPerMm);
        }

        void EndDrag(Ptr p)
        {
            VrWidget w = p.Drag;
            p.Drag = null;
            p.Pressed = null;
            p.PokePressed = false;
            p.PressedFar = false;
            if (w != null)
            {
                w.OnDragEnd?.Invoke();
                VrAudio.Tick(w.WorldCentre);
            }
        }

        static VrWidget WidgetAt(VrPanel panel, Vector2 localMm, float marginMm)
        {
            VrWidget best = null;
            float bestD = float.MaxValue;
            foreach (VrWidget w in panel.Widgets)
            {
                if (w.Kind == WidgetKind.Static || !w.HitTest(localMm, marginMm)) continue;
                float d = (w.RectMm.center - localMm).sqrMagnitude;
                bool inside = w.HitTest(localMm, 0f);
                if (!inside) d += 1e6f; // a widget the point is really in beats one only close to it
                if (d < bestD)
                {
                    bestD = d;
                    best = w;
                }
            }

            return best;
        }

        public Dictionary<string, object> State()
        {
            Dictionary<string, object> s = new() { ["clicks"] = Clicks, ["lastClick"] = LastClick, ["clickLog"] = ClickLog, ["rays"] = SuppressRays ? "off" : "on" };
            foreach (Ptr p in Pointers)
            {
                if (p.Hand == null) continue;
                s[p.Hand.Side.ToString().ToLowerInvariant()] = new Dictionary<string, object>
                {
                    ["hover"] = p.Hover?.Id, ["pressed"] = p.Pressed?.Id, ["drag"] = p.Drag?.Id, ["near"] = p.NearPanel, ["rayHit"] = p.RayHit,
                    ["panel"] = p.HitPanel != null ? p.HitPanel.name : null, ["pinch"] = p.Hand.Pinch.Held, ["tracked"] = p.Hand.Tracked, ["aimValid"] = p.Hand.Aim.Valid,
                    ["aimDir"] = new[] { Mathf.Round(p.Hand.Aim.Direction.x * 100f) / 100f, Mathf.Round(p.Hand.Aim.Direction.y * 100f) / 100f, Mathf.Round(p.Hand.Aim.Direction.z * 100f) / 100f },
                    ["tip"] = p.Hand.Tracked ? new[] { Mathf.Round(p.Hand.IndexTip.x * 100f) / 100f, Mathf.Round(p.Hand.IndexTip.y * 100f) / 100f, Mathf.Round(p.Hand.IndexTip.z * 100f) / 100f } : null
                };
            }

            return s;
        }
    }
}
