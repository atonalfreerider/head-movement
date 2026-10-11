using System;
using System.Collections.Generic;
using Unity.XR.CompositionLayers;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace HmVr
{
    public enum DisplayMode
    {
        Immersive,
        Passthrough
    }

    /// <summary>
    /// Passthrough mixed reality vs immersive VR (docs/QUEST.md). In PASSTHROUGH the headset's cameras show the real room behind the
    /// viewer's picture: the head camera clears to alpha 0, the Meta passthrough layer (AR Foundation's camera manager on the head
    /// camera, "Meta Quest: Camera (Passthrough)") is composited under the eye buffer, and the viewer leaves out everything that would hide
    /// the room (MixedReality.Passthrough: no room mesh, no floor fill, only faint floor marks, a soft contact disc under the couple). In
    /// IMMERSIVE the camera clears to the viewer's dark background, the passthrough layer is paused and the room and floor are as before.
    ///
    /// The default on a headset is passthrough; the choice is remembered (PlayerPrefs) and switched from the menu. If passthrough cannot
    /// start (the runtime has no support, the feature is off, the session never delivers the camera) the viewer falls back to immersive,
    /// says so in the menu text and the log, and keeps the wish for the next start.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [AddComponentMenu("")]
    public sealed class VrDisplay : MonoBehaviour
    {
        public static VrDisplay Instance { get; private set; }
        public const string PrefKey = "hm_vr_display";

        public enum State
        {
            Immersive,
            Starting,
            Passthrough,
            Failed
        }

        public DisplayMode Wanted { get; private set; }
        public State Current { get; private set; } = State.Immersive;
        public string Failure { get; private set; } = "";
        public string Detail { get; private set; } = "";
        public bool Xr { get; private set; }
        /// <summary>the viewer draws as a mixed-reality overlay (the real room is the background), real passthrough or simulated</summary>
        public bool PassthroughActive => Current == State.Passthrough || (Current == State.Starting && !Xr);
        public bool Switching => Current == State.Starting;
        public event Action Changed;

        VrRig rig;
        GameObject sessionGo;
        ARSession session;
        ARCameraManager cameraManager;
        float startedAt;
        // lighting
        bool ambientSaved;
        UnityEngine.Rendering.AmbientMode ambientMode;
        Color ambientLight;
        // contact disc
        GameObject disc;
        Material discMat;
        Shader discShader;

        public static string StatusLine
        {
            get
            {
                VrDisplay d = Instance;
                if (d == null) return "";
                if (d.Current == State.Failed) return "Passthrough unavailable (" + d.Failure + "): showing immersive";
                if (d.Current == State.Starting) return "Starting passthrough";
                return d.Current == State.Passthrough ? "Passthrough: your room with the dancers in it" : "Immersive: a virtual studio";
            }
        }

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            // the session ends: the scene goes back to what the desktop / immersive viewer expects
            if (ambientSaved)
            {
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientLight = ambientLight;
                ambientSaved = false;
            }

            MixedReality.Set(false);
            if (disc != null) Destroy(disc);
            if (discMat != null) Destroy(discMat);
        }

        /// <summary>build for a rig; decides the starting mode (the remembered choice, else the settings file, else passthrough on a headset)</summary>
        public void Init(VrRig r, bool xr)
        {
            rig = r;
            Xr = xr;
            Wanted = ReadWanted(xr);
            Debug.Log($"VrDisplay: wanted {Wanted.ToString().ToLowerInvariant()} (xr={xr})");
            Apply(Wanted);
        }

        static DisplayMode ReadWanted(bool xr)
        {
            string want = Environment.GetEnvironmentVariable("HM_VR_DISPLAY");
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-hmvr-display", StringComparison.OrdinalIgnoreCase)) want = args[i + 1];
            }

            if (string.IsNullOrEmpty(want))
            {
                try
                {
                    want = PlayerPrefs.GetString(PrefKey, "");
                }
                catch (Exception)
                {
                    want = "";
                }
            }

            if (string.IsNullOrEmpty(want) || want == "auto") want = VrSettings.Load().Display;
            want = (want ?? "auto").Trim().ToLowerInvariant();
            if (want == "passthrough") return DisplayMode.Passthrough;
            if (want == "immersive") return DisplayMode.Immersive;
            return xr ? DisplayMode.Passthrough : DisplayMode.Immersive; // the headset starts in the room; the desktop simulation in the studio
        }

        public void Toggle() => Set(Wanted == DisplayMode.Passthrough ? DisplayMode.Immersive : DisplayMode.Passthrough);

        /// <summary>switch (and remember the choice)</summary>
        public void Set(DisplayMode mode, bool remember = true)
        {
            Wanted = mode;
            if (remember)
            {
                try
                {
                    PlayerPrefs.SetString(PrefKey, mode.ToString().ToLowerInvariant());
                    PlayerPrefs.Save();
                }
                catch (Exception)
                {
                    // no preferences store
                }
            }

            Apply(mode);
        }

        void Apply(DisplayMode mode)
        {
            if (rig == null) return;
            Failure = "";
            if (mode == DisplayMode.Passthrough)
            {
                try
                {
                    if (Xr) StartPassthrough();
                    else EnterSimulated();
                }
                catch (Exception e)
                {
                    Fail("exception: " + e.Message);
                }
            }
            else
            {
                StopPassthrough();
            }
        }

        // ------------------------------------------------------------------ passthrough on

        void EnterSimulated()
        {
            Current = State.Passthrough;
            Detail = "simulated (no headset)";
            rig.SetPassthroughBackground(true);
            ApplyScene(true);
            Debug.Log("VrDisplay: passthrough (simulated)");
            Changed?.Invoke();
        }

        void StartPassthrough()
        {
            Current = State.Starting;
            startedAt = Time.unscaledTime;
            if (cameraManager == null)
            {
                // created inactive so its settings are in place before AR Foundation starts the subsystems
                sessionGo = new GameObject("AR Session");
                sessionGo.SetActive(false);
                sessionGo.transform.SetParent(rig.transform, false);
                session = sessionGo.AddComponent<ARSession>();
                session.matchFrameRateRequested = false;
                sessionGo.SetActive(true);
                cameraManager = rig.Cam.gameObject.AddComponent<ARCameraManager>();
            }

            rig.SetPassthroughBackground(true);
            cameraManager.enabled = true;
            ApplyScene(true);
            Debug.Log("VrDisplay: passthrough requested (AR camera manager on, camera background alpha 0)");
            Changed?.Invoke();
        }

        void Update()
        {
            if (Current != State.Starting || !Xr) return;
            bool running = cameraManager != null && cameraManager.subsystem != null && cameraManager.subsystem.running;
            if (running)
            {
                Current = State.Passthrough;
                Detail = DescribeLayer();
                Debug.Log("VrDisplay: passthrough running (" + Detail + ")");
                Changed?.Invoke();
                return;
            }

            if (Time.unscaledTime - startedAt > 6f)
            {
                string why = cameraManager == null ? "no camera manager" : cameraManager.subsystem == null ? "the runtime provides no camera subsystem (is 'Meta Quest: Camera (Passthrough)' on?)" : "the camera subsystem did not start";
                Fail(why);
            }
        }

        string DescribeLayer()
        {
            try
            {
                string s = cameraManager.subsystem.GetType().Name;
                foreach (CompositionLayer l in FindObjectsByType<CompositionLayer>(FindObjectsInactive.Include))
                {
                    s += $", layer '{l.gameObject.name}' order {l.Order} {(l.enabled ? "on" : "off")} blend {l.LayerData?.BlendType}";
                }

                return s;
            }
            catch (Exception e)
            {
                return "camera subsystem running (layer not inspected: " + e.Message + ")";
            }
        }

        void Fail(string why)
        {
            Failure = why;
            Debug.LogWarning("VrDisplay: passthrough failed: " + why + "; showing immersive");
            StopPassthroughInternal();
            Current = State.Failed;
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ passthrough off

        void StopPassthrough()
        {
            StopPassthroughInternal();
            Current = State.Immersive;
            Detail = "";
            Debug.Log("VrDisplay: immersive");
            Changed?.Invoke();
        }

        void StopPassthroughInternal()
        {
            if (cameraManager != null) cameraManager.enabled = false; // pauses the passthrough layer
            if (rig != null) rig.SetPassthroughBackground(false);
            ApplyScene(false);
        }

        // ------------------------------------------------------------------ the scene for the mode

        void ApplyScene(bool passthrough)
        {
            MixedReality.Set(passthrough);
            if (passthrough)
            {
                if (!ambientSaved)
                {
                    ambientSaved = true;
                    ambientMode = RenderSettings.ambientMode;
                    ambientLight = RenderSettings.ambientLight;
                }

                // a soft flat fill so the shaded side of a dancer does not go black against the bright room
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.34f, 0.34f, 0.36f);
            }
            else if (ambientSaved)
            {
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientLight = ambientLight;
                ambientSaved = false;
            }

            if (disc != null) disc.SetActive(passthrough);
        }

        // ------------------------------------------------------------------ the soft contact disc under the couple

        void LateUpdate()
        {
            bool want = PassthroughActive && VrFilm.InLesson;
            if (!want)
            {
                if (disc != null && disc.activeSelf) disc.SetActive(false);
                return;
            }

            if (disc == null) BuildDisc();
            if (disc == null) return;
            if (!disc.activeSelf) disc.SetActive(true);
            Vector3 c = VrViewpoints.CoupleCentre(HeadMovement.Instance);
            disc.transform.position = new Vector3(c.x, 0.004f, c.z);
        }

        void BuildDisc()
        {
            discShader ??= Shader.Find("HeadMovement/ContactDisc");
            if (discShader == null) return;
            disc = new GameObject("Contact disc");
            DontDestroyOnLoad(disc);
            Mesh m = new() { name = "Contact disc" };
            m.vertices = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(-0.5f, 0, 0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(0.5f, 0, -0.5f) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateBounds();
            disc.AddComponent<MeshFilter>().sharedMesh = m;
            MeshRenderer r = disc.AddComponent<MeshRenderer>();
            discMat = new Material(discShader) { name = "Contact disc", hideFlags = HideFlags.DontSave };
            r.sharedMaterial = discMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            disc.transform.localScale = new Vector3(2.4f, 1f, 2.4f);
        }

        public Dictionary<string, object> State_() => new()
        {
            ["wanted"] = Wanted.ToString().ToLowerInvariant(), ["state"] = Current.ToString().ToLowerInvariant(), ["active"] = PassthroughActive,
            ["failure"] = Failure, ["detail"] = Detail, ["mixedReality"] = MixedReality.Passthrough, ["status"] = StatusLine,
            ["cameraManager"] = cameraManager != null ? (cameraManager.enabled ? "on" : "off") : "none",
            ["subsystemRunning"] = cameraManager != null && cameraManager.subsystem != null && cameraManager.subsystem.running,
            ["clearAlpha"] = rig != null && rig.Cam != null ? rig.Cam.backgroundColor.a : -1f, ["disc"] = disc != null && disc.activeSelf
        };
    }
}
