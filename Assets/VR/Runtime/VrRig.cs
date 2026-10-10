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
        public Transform Head => Cam.transform;
        public bool Xr { get; private set; }
        public SimInputSource Sim { get; private set; }
        public float FloorY => transform.position.y;
        public int Teleports { get; private set; }
        public int Turns { get; private set; }
        public float TotalTurnDeg { get; private set; }
        public bool Blinking { get; private set; }
        public string HeadStatus { get; private set; } = "";

        Material fadeMat;
        MeshRenderer fadeRenderer;
        Coroutine blink;
        readonly System.Collections.Generic.List<XRInputSubsystem> inputSubsystems = new();
        bool originModeSet;
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
            GameObject head = new("VR Head");
            head.transform.SetParent(transform, false);
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
            foreach (XRInputSubsystem s in inputSubsystems)
            {
                if (s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor)) originModeSet = true;
            }
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
