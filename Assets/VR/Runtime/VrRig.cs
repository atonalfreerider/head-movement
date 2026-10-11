using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace HmVr
{
    /// <summary>
    /// The user's body in the world: an origin on the floor with the head camera under it. The head camera is the ONLY camera of the VR
    /// mode (the film's directed camera never moves it). Moving the user moves the origin: a teleport puts the head at a spot (a short
    /// blink hides the jump), a snap turn rotates the origin about the head. With a headset the camera's pose comes from the tracked
    /// head; without one (the editor preview, the tests) from the simulated head.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class VrRig : MonoBehaviour
    {
        public static VrRig Instance { get; private set; }

        public Camera Cam { get; private set; }
        public Transform TrackingSpace { get; private set; }
        public Transform Head => Cam.transform;
        public bool Xr { get; private set; }
        public SimInputSource Sim { get; private set; }
        public float FloorY => transform.position.y;
        public int Teleports { get; private set; }
        public int Turns { get; private set; }
        public float TotalTurnDeg { get; private set; }
        public bool Blinking { get; private set; }
        public string HeadStatus { get; private set; } = "";
        public bool FloorTracked => floorTracked;

        Material fadeMat;
        MeshRenderer fadeRenderer;
        Coroutine blink;
        readonly System.Collections.Generic.List<XRInputSubsystem> inputSubsystems = new();
        bool originModeSet;
        bool floorTracked;
        float nextOriginTry;
        // the head in tracking space, the last good reading
        Vector3 lastLocalPos = new(0f, 1.6f, 0f);
        Quaternion lastLocalRot = Quaternion.identity;

        public static VrRig Create(bool xr, Camera template)
        {
            GameObject go = new("VR Rig");
            VrRig rig = go.AddComponent<VrRig>();
            rig.Build(xr, template);
            return rig;
        }

        void Build(bool xr, Camera template)
        {
            Instance = this;
            Xr = xr;
            // the tracked space (head and hands live in it): at the rig's floor origin, raised by the eye height when the runtime tracks from the eyes (no floor)
            TrackingSpace = new GameObject("VR Tracking Space").transform;
            TrackingSpace.SetParent(transform, false);
            GameObject head = new("VR Head");
            head.transform.SetParent(TrackingSpace, false);
            head.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            head.tag = "MainCamera";
            Cam = head.AddComponent<Camera>();
            Cam.nearClipPlane = 0.06f;
            Cam.farClipPlane = template != null ? Mathf.Max(100f, template.farClipPlane) : 300f;
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = new Color(0.02f, 0.025f, 0.04f);
            Cam.allowHDR = true;
            Cam.allowMSAA = true;
            Cam.fieldOfView = 90f;
            Cam.depth = 10f;
            UniversalAdditionalCameraData data = Cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = false; // no bloom / depth of field in VR
            data.antialiasing = AntialiasingMode.None; // MSAA comes from the pipeline asset
            data.renderShadows = true;
            head.AddComponent<AudioListener>();
            if (!xr)
            {
                Sim = new SimInputSource(transform);
            }

            BuildFade(head.transform);
            DontDestroyOnLoad(gameObject);
        }

        void BuildFade(Transform head)
        {
            GameObject f = GameObject.CreatePrimitive(PrimitiveType.Quad);
            f.name = "Comfort fade";
            Destroy(f.GetComponent<Collider>());
            f.layer = VrStyle.Layer;
            f.transform.SetParent(head, false);
            f.transform.localPosition = new Vector3(0f, 0f, 0.2f);
            f.transform.localScale = new Vector3(1.4f, 1.4f, 1f);
            fadeRenderer = f.GetComponent<MeshRenderer>();
            fadeMat = VrDraw.Material(new Color(0f, 0f, 0f, 0f), true);
            fadeMat.renderQueue = 4000;
            fadeRenderer.sharedMaterial = fadeMat;
            fadeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            fadeRenderer.receiveShadows = false;
            fadeRenderer.sortingOrder = 32000;
            fadeRenderer.enabled = false;
        }

        void OnDestroy()
        {
            Application.onBeforeRender -= ReadHead;
            if (Instance == this) Instance = null;
            if (fadeMat != null) Destroy(fadeMat);
        }

        void OnEnable()
        {
            Application.onBeforeRender += ReadHead;
        }

        void OnDisable()
        {
            Application.onBeforeRender -= ReadHead;
        }

        void Update()
        {
            if (Xr && !originModeSet && Time.unscaledTime >= nextOriginTry) TrySetFloorOrigin();
            ReadHead();
        }

        void TrySetFloorOrigin()
        {
            nextOriginTry = Time.unscaledTime + 1f;
            inputSubsystems.Clear();
            SubsystemManager.GetSubsystems(inputSubsystems);
            bool floor = false;
            foreach (XRInputSubsystem s in inputSubsystems)
            {
                if (s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor)) originModeSet = true;
                floor |= (s.GetTrackingOriginMode() & TrackingOriginModeFlags.Floor) != 0;
            }

            // no floor from the runtime: the origin is the eyes' start pose, so the user stands 1.6 m above the rig's floor
            if (originModeSet || inputSubsystems.Count > 0) TrackingSpace.localPosition = floor ? Vector3.zero : new Vector3(0f, ViewpointMath.EyeHeight, 0f);
            floorTracked = floor;
        }

        void ReadHead()
        {
            if (Cam == null) return;
            if (Sim != null)
            {
                Cam.transform.localPosition = Sim.HeadLocalPos;
                Cam.transform.localRotation = Sim.HeadLocalRot;
                return;
            }

            if (!Xr) return;
            InputDevice dev = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            Vector3 p = default;
            Quaternion r = default;
            bool got = dev.isValid && dev.TryGetFeatureValue(CommonUsages.centerEyePosition, out p) && dev.TryGetFeatureValue(CommonUsages.centerEyeRotation, out r);
            if (!got)
            {
                dev = InputDevices.GetDeviceAtXRNode(XRNode.Head); // a runtime that reports the head device only
                got = dev.isValid && dev.TryGetFeatureValue(CommonUsages.devicePosition, out p) && dev.TryGetFeatureValue(CommonUsages.deviceRotation, out r);
            }

            if (got)
            {
                lastLocalPos = p;
                lastLocalRot = r;
                HeadStatus = "tracked";
            }
            else
            {
                HeadStatus = "no head pose";
            }

            Cam.transform.localPosition = lastLocalPos;
            Cam.transform.localRotation = lastLocalRot;
        }

        // ------------------------------------------------------------------ passthrough: the real room is the background

        Color immersiveBackground = new(0.02f, 0.025f, 0.04f, 1f);
        bool passthroughBackground;

        /// <summary>the colour behind everything in immersive mode (the viewer's own background)</summary>
        public void SetImmersiveBackground(Color c)
        {
            c.a = 1f;
            immersiveBackground = c;
            if (!passthroughBackground && Cam != null) Cam.backgroundColor = c;
        }

        /// <summary>
        /// Passthrough needs a transparent camera background (alpha 0, solid colour): the headset composites the real room BEHIND the eye
        /// buffer wherever it is transparent. Immersive mode puts the opaque background back.
        /// </summary>
        public void SetPassthroughBackground(bool on)
        {
            passthroughBackground = on;
            if (Cam == null) return;
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = on ? new Color(0f, 0f, 0f, 0f) : immersiveBackground;
        }

        // ------------------------------------------------------------------ placing the dance in the real room (passthrough)

        /// <summary>the head in real floor coordinates (the tracking floor, metres): x, z and the eye height above the floor</summary>
        public Vector3 HeadReal => transform.InverseTransformPoint(Head.position);

        /// <summary>the heading of the head on the real floor, degrees (0 = +Z of the tracking space)</summary>
        public float HeadRealYawDeg
        {
            get
            {
                Vector3 f = Quaternion.Inverse(transform.rotation) * Head.forward;
                return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            }
        }

        public Vector3 ToReal(Vector3 world) => transform.InverseTransformPoint(world);

        /// <summary>a floor direction in world space as a real-floor direction (no scale)</summary>
        public Vector3 DirToReal(Vector3 worldDir) => transform.InverseTransformDirection(worldDir);

        /// <summary>the rig carries the inverse of the dance's placement: the user moves, the dance (a world of its own objects) stays</summary>
        public void ApplyPlacement(DancePlacement p)
        {
            p.RigPose(out Vector3 pos, out Quaternion rot, out float k);
            Vector3 headBefore = Head.position;
            Quaternion rotBefore = transform.rotation;
            transform.SetPositionAndRotation(pos, rot);
            transform.localScale = Vector3.one * k;
            HandState.WorldScale = k;
            CarryPanels(headBefore, Head.position, transform.rotation * Quaternion.Inverse(rotBefore));
        }

        /// <summary>the rig on the floor origin, unrotated, unscaled (the lobby, immersive mode)</summary>
        public void ClearPlacement()
        {
            Vector3 headBefore = Head.position;
            Quaternion rotBefore = transform.rotation;
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            transform.localScale = Vector3.one;
            HandState.WorldScale = 1f;
            CarryPanels(headBefore, Head.position, transform.rotation * Quaternion.Inverse(rotBefore));
        }

        /// <summary>
        /// The panels hang in the user's space (the Place panel, the captions, the HUD, the hand panel glide to the gaze): when the rig moves
        /// under the user to place the dance, they move with the head - the same offset from it, turned with the rig - so they stay where the
        /// user sees them (their sizes and distances in world metres stay as they are).
        /// </summary>
        static void CarryPanels(Vector3 headBefore, Vector3 headAfter, Quaternion turn)
        {
            if ((headBefore - headAfter).sqrMagnitude < 1e-10f && Quaternion.Angle(turn, Quaternion.identity) < 1e-3f) return;
            foreach (VrPanel panel in VrPanel.All)
            {
                Transform t = panel.transform;
                t.SetPositionAndRotation(headAfter + turn * (t.position - headBefore), turn * t.rotation);
            }
        }

        public float RigScale => transform.localScale.x;

        // ------------------------------------------------------------------ moving the user

        /// <summary>the yaw of the head inside the rig, degrees</summary>
        public float HeadLocalYawDeg
        {
            get
            {
                Vector3 f = Cam.transform.localRotation * Vector3.forward;
                return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            }
        }

        public float HeadWorldYawDeg
        {
            get
            {
                Vector3 f = Cam.transform.forward;
                return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            }
        }

        /// <summary>put the head (horizontally) at the point, standing on floorY and facing yawDeg; blink hides the jump</summary>
        public void PlaceHead(Vector3 headWorld, float yawDeg, float floorY, bool blinkFirst = true)
        {
            void Apply()
            {
                ViewpointMath.RigPose(headWorld, yawDeg, floorY, Cam.transform.localPosition, HeadLocalYawDeg, out Vector3 pos, out float rigYaw);
                transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, rigYaw, 0f));
                Teleports++;
            }

            if (blinkFirst && !(Sim != null && Sim.Instant)) Blink(Apply);
            else Apply();
        }

        public void PlaceAt(Viewpoint vp, bool blinkFirst = true) => PlaceHead(vp.Head, vp.YawDeg, vp.FloorY, blinkFirst);

        /// <summary>rotate the user about their head (they stay where they are, the world turns)</summary>
        public void SnapTurn(float deg)
        {
            Vector3 pivot = Cam.transform.position;
            transform.RotateAround(pivot, Vector3.up, deg);
            Turns++;
            TotalTurnDeg += deg;
        }

        public void Blink(Action midway)
        {
            if (blink != null)
            {
                StopCoroutine(blink);
                midway?.Invoke();
                SetFade(0f);
            }

            blink = StartCoroutine(BlinkRoutine(midway));
        }

        IEnumerator BlinkRoutine(Action midway)
        {
            Blinking = true;
            for (float t = 0f; t < 0.09f; t += Time.unscaledDeltaTime)
            {
                SetFade(t / 0.09f);
                yield return null;
            }

            SetFade(1f);
            midway?.Invoke();
            yield return null;
            for (float t = 0f; t < 0.16f; t += Time.unscaledDeltaTime)
            {
                SetFade(1f - t / 0.16f);
                yield return null;
            }

            SetFade(0f);
            Blinking = false;
            blink = null;
        }

        void SetFade(float a)
        {
            if (fadeRenderer == null) return;
            fadeRenderer.enabled = a > 0.002f;
            fadeMat.SetColor("_Tint", new Color(0f, 0f, 0.0f, Mathf.Clamp01(a)));
        }
    }

    /// <summary>materials and primitives for the VR helpers (hands, rays, reticles)</summary>
    public static class VrDraw
    {
        static Shader shader;

        public static Material Material(Color colour, bool onTop = false)
        {
            shader ??= Shader.Find("HeadMovement/VrUnlit");
            Material m = new(shader) { name = "VR unlit", hideFlags = HideFlags.DontSave };
            m.SetColor("_Tint", colour);
            m.SetFloat("_ZTest", onTop ? (float)UnityEngine.Rendering.CompareFunction.Always : (float)UnityEngine.Rendering.CompareFunction.LessEqual);
            m.renderQueue = 3950; // after the translucent avatars and the panels
            return m;
        }

        public static void SetColour(Material m, Color c) => m.SetColor("_Tint", c);

        static Mesh sphere;

        public static Mesh Sphere
        {
            get
            {
                if (sphere != null) return sphere;
                GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere = tmp.GetComponent<MeshFilter>().sharedMesh;
                UnityEngine.Object.Destroy(tmp);
                return sphere;
            }
        }

        public static LineRenderer Line(Transform parent, string name, Material m, float width)
        {
            GameObject go = new(name);
            go.layer = VrStyle.Layer;
            go.transform.SetParent(parent, false);
            LineRenderer l = go.AddComponent<LineRenderer>();
            l.useWorldSpace = true;
            l.sharedMaterial = m;
            l.widthMultiplier = width;
            l.numCapVertices = 4;
            l.numCornerVertices = 2;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            l.alignment = LineAlignment.View;
            l.sortingOrder = 31000;
            l.positionCount = 0;
            l.enabled = false;
            return l;
        }
    }
}
