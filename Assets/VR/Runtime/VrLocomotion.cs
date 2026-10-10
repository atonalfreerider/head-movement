using System.Collections.Generic;
using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// Moving the user, with hands only and never smoothly (no artificial motion):
    ///  - TELEPORT: pinch with the dominant hand where no panel is, hold to see the arc and its landing disc, open the pinch to go.
    ///    A tiny pinch (under a sixth of a second) or a landing that is not allowed does nothing.
    ///  - SNAP TURN: pinch with the other hand and move it sideways: 45 degrees per flick (SnapTurnGesture); the user turns about their own head.
    ///  - VIEWPOINT PRESETS: jump to a named place (VrViewpoints) with a blink.
    /// Only while a lesson is playing.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    [AddComponentMenu("")]
    public sealed class VrLocomotion : MonoBehaviour
    {
        public static VrLocomotion Instance { get; private set; }

        public const float SnapDegrees = 45f, MinAimSeconds = 0.16f, MaxJumpM = 9f, WalkableRadiusM = 14f;

        public bool Enabled;
        public Vector3 WalkableCentre = Vector3.zero;
        public readonly List<Viewpoint> Viewpoints = new();
        public string CurrentViewpoint { get; private set; } = "";
        public bool TeleportAiming { get; private set; }
        public bool AimValid { get; private set; }
        public Vector3 AimTarget { get; private set; }
        public int Teleports, Turns;
        public Vector3 LastTeleport;
        public bool Cancelled;

        readonly SnapTurnGesture snap = new();
        readonly Vector3[] arc = new Vector3[80];
        LineRenderer arcLine;
        Transform marker;
        MeshRenderer markerRenderer;
        Material arcMat, markerMat, badMat;
        Hand aimHand;
        float aimStart;

        void Awake()
        {
            Instance = this;
            arcMat = VrDraw.Material(new Color(0.55f, 0.95f, 0.7f, 0.9f), true);
            markerMat = VrDraw.Material(new Color(0.55f, 0.95f, 0.7f, 0.55f), true);
            badMat = VrDraw.Material(new Color(1f, 0.4f, 0.35f, 0.6f), true);
            arcLine = VrDraw.Line(transform, "teleport arc", arcMat, 0.006f);
            GameObject m = new("teleport landing", typeof(MeshFilter), typeof(MeshRenderer));
            m.layer = VrStyle.Layer;
            m.transform.SetParent(transform, false);
            m.GetComponent<MeshFilter>().sharedMesh = VrDraw.Sphere;
            markerRenderer = m.GetComponent<MeshRenderer>();
            markerRenderer.sharedMaterial = markerMat;
            markerRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            markerRenderer.sortingOrder = 31000;
            markerRenderer.enabled = false;
            marker = m.transform;
            marker.localScale = new Vector3(0.5f, 0.012f, 0.5f);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (arcMat != null) Destroy(arcMat);
            if (markerMat != null) Destroy(markerMat);
            if (badMat != null) Destroy(badMat);
        }

        public bool Aiming(Hand side) => TeleportAiming && aimHand == side;

        void Update()
        {
            VrInput input = VrInput.Instance;
            VrRig rig = VrRig.Instance;
            VrPointers ptrs = VrPointers.Instance;
            if (input == null || rig == null || ptrs == null || input.Source == null) return;
            float dt = Time.unscaledDeltaTime;
            if (!Enabled)
            {
                CancelAim();
                snap.Reset();
                return;
            }

            UpdateTeleport(input, rig, ptrs);
            UpdateSnapTurn(input, rig, ptrs, dt);
        }

        void CancelAim()
        {
            TeleportAiming = false;
            arcLine.enabled = false;
            markerRenderer.enabled = false;
        }

        void UpdateTeleport(VrInput input, VrRig rig, VrPointers ptrs)
        {
            VrHand hand = input.DominantHand;
            if (!TeleportAiming)
            {
                // the first teleport pinch goes to a panel when the ray is on one; a hand panel's own pinch never starts a teleport
                if (hand.Pinch.Down && hand.Tracked && hand.Aim.Valid && !ptrs.OverUi(hand.Side) && !rig.Blinking)
                {
                    TeleportAiming = true;
                    aimHand = hand.Side;
                    aimStart = Time.unscaledTime;
                    Cancelled = false;
                }

                return;
            }

            VrHand h = input.Get(aimHand);
            if (!h.Tracked || !h.Aim.Valid)
            {
                Cancelled = true;
                CancelAim();
                return;
            }

            // the arc from the knuckle along the aim, to the floor the user stands on
            float floor = rig.FloorY;
            bool hit = TeleportMath.Arc(h.Aim.Origin, h.Aim.Direction, floor, arc, out int n, out Vector3 landing);
            Vector3 head = rig.Head.position;
            bool allowed = hit && TeleportMath.Allowed(landing, head, WalkableCentre, WalkableRadiusM, MaxJumpM);
            AimValid = allowed;
            AimTarget = landing;
            arcLine.enabled = true;
            arcLine.positionCount = n;
            for (int i = 0; i < n; i++) arcLine.SetPosition(i, arc[i]);
            arcLine.sharedMaterial = allowed || !hit ? arcMat : badMat;
            markerRenderer.enabled = hit;
            if (hit)
            {
                marker.position = landing + Vector3.up * 0.01f;
                markerRenderer.sharedMaterial = allowed ? markerMat : badMat;
            }

            if (!h.Pinch.Held)
            {
                bool long_enough = Time.unscaledTime - aimStart >= MinAimSeconds;
                CancelAim();
                if (allowed && long_enough)
                {
                    LastTeleport = landing;
                    Teleports++;
                    VrAudio.Whoosh(landing);
                    rig.PlaceHead(new Vector3(landing.x, 0f, landing.z), rig.HeadWorldYawDeg, floor);
                    CurrentViewpoint = "";
                }
                else
                {
                    Cancelled = true;
                }
            }
        }

        void UpdateSnapTurn(VrInput input, VrRig rig, VrPointers ptrs, float dt)
        {
            VrHand hand = input.OffHand;
            bool gate = hand.Tracked && hand.Pinch.Held && !hand.Palm.Facing && !ptrs.OverUi(hand.Side) && !TeleportAiming;
            Vector3 local = Quaternion.Inverse(input.Frame.HeadYaw) * (hand.PinchPoint - input.Frame.HeadPosition);
            int step = snap.Update(gate, local.x, dt);
            if (step == 0) return;
            Turns++;
            rig.SnapTurn(step * SnapDegrees);
            VrAudio.Whoosh(rig.Head.position);
        }

        // ------------------------------------------------------------------ viewpoints

        public void SetViewpoints(List<Viewpoint> list)
        {
            Viewpoints.Clear();
            if (list != null) Viewpoints.AddRange(list);
            CurrentViewpoint = "";
        }

        public bool GoTo(string id, bool blink = true)
        {
            VrRig rig = VrRig.Instance;
            if (rig == null) return false;
            foreach (Viewpoint vp in Viewpoints)
            {
                if (!string.Equals(vp.Id, id, System.StringComparison.OrdinalIgnoreCase)) continue;
                rig.PlaceAt(vp, blink);
                CurrentViewpoint = vp.Id;
                VrAudio.Whoosh(vp.Head);
                return true;
            }

            return false;
        }

        public void TurnBy(float degrees)
        {
            VrRig rig = VrRig.Instance;
            if (rig == null) return;
            rig.SnapTurn(degrees);
            Turns++;
        }
    }
}
