using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using UnityEngine.XR.Management;

namespace HmVr
{
    /// <summary>What the machine offers for VR, found without starting anything.</summary>
    public struct RuntimeProbe
    {
        public bool Present;
        public string Name, Path, Note;
    }

    /// <summary>
    /// VR mode of the viewer (docs/QUEST.md, VIEWER_SPEC VR section). In VR mode the user's head is the only camera ("POV only": the film's
    /// directed camera never takes over), the lessons are chosen from a menu in the world and controlled with the hands alone. The
    /// desktop mode is the unchanged flat viewer; the two switch at any time (the flat library's VR button, F9, the menu's Exit VR).
    ///
    /// It starts itself in desktop mode and enters VR when an OpenXR runtime is registered (a Quest through Link, Air Link or Virtual
    /// Desktop on a PC; always on Android) unless told otherwise: command line -hmvr off | on | sim | auto, or the environment variable
    /// HM_VR. XR is started here, on demand (XR Plug-in Management is set to not initialise at start), so a PC without a headset never
    /// logs an XR error. With "sim" (or hm_vr in the editor) the whole mode runs on simulated hands with no XR.
    /// </summary>
    [DefaultExecutionOrder(-400)]
    [AddComponentMenu("")]
    public sealed class VrMode : MonoBehaviour
    {
        public enum Phase
        {
            Desktop,
            Starting,
            Vr,
            Simulated
        }

        public static VrMode Instance { get; private set; }
        public static bool Active => Instance != null && (Instance.phase == Phase.Vr || Instance.phase == Phase.Simulated);

        /// <summary>auto | on | off | sim</summary>
        public static string Preference = "auto";

        public Phase Current => phase;
        public string LastError { get; private set; }
        public string XrStatus { get; private set; } = "not started";
        public RuntimeProbe Runtime { get; private set; }
        public VrSession Session { get; private set; }
        public readonly List<string> Log = new();

        Phase phase = Phase.Desktop;
        bool startedXr;
        GameObject simulatorGo;
        int autoTries;
        bool autoDone;
        float startTime;

        // ------------------------------------------------------------------ start

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Instance = null;
            Preference = "auto";
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Application.isBatchMode || Instance != null) return;
            if (UnityEngine.Object.FindAnyObjectByType<HeadMovement>() == null) return; // not the viewer scene
            GameObject go = new("VR Mode");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<VrMode>();
        }

        void Awake()
        {
            Instance = this;
            Runtime = Probe();
            Preference = ReadPreference();
            startTime = Time.unscaledTime;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        static string ReadPreference()
        {
            string pref = "auto";
            string env = Environment.GetEnvironmentVariable("HM_VR");
            if (!string.IsNullOrEmpty(env)) pref = env.Trim().ToLowerInvariant();
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-hmvr", StringComparison.OrdinalIgnoreCase)) pref = args[i + 1].Trim().ToLowerInvariant();
            }

            return pref is "on" or "off" or "sim" or "auto" ? pref : "auto";
        }

        /// <summary>the OpenXR runtime this machine has registered (Windows: the registry's ActiveRuntime or XR_RUNTIME_JSON), or Android: always</summary>
        public static RuntimeProbe Probe()
        {
            RuntimeProbe p = new();
#if UNITY_ANDROID && !UNITY_EDITOR
            p.Present = true;
            p.Name = "Android OpenXR runtime (the headset)";
            return p;
#else
            string path = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
#if UNITY_STANDALONE_WIN || (UNITY_EDITOR_WIN && !UNITY_ANDROID) // the registry type is not in the Android API profile (editor on the Android target)
            if (string.IsNullOrEmpty(path))
            {
                try
                {
                    using Microsoft.Win32.RegistryKey key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                        .OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1");
                    path = key?.GetValue("ActiveRuntime") as string;
                }
                catch (Exception e)
                {
                    p.Note = "registry unreadable: " + e.Message;
                }
            }
#endif
            if (string.IsNullOrEmpty(path))
            {
                p.Note ??= "no active OpenXR runtime registered";
                return p;
            }

            p.Path = path;
            if (!File.Exists(path))
            {
                p.Note = "the registered runtime file is missing";
                return p;
            }

            try
            {
                Newtonsoft.Json.Linq.JObject j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                p.Name = j.Value<string>("runtime") != null ? (j["runtime"] as Newtonsoft.Json.Linq.JObject)?.Value<string>("name") : j.Value<string>("name");
            }
            catch (Exception)
            {
                p.Name = Path.GetFileName(path);
            }

            p.Present = true;
            return p;
#endif
        }

        // ------------------------------------------------------------------ per frame: auto-enter, recording guard, F9

        void Update()
        {
            if (phase == Phase.Desktop && !autoDone && Time.unscaledTime - startTime > 0.3f)
            {
                autoDone = true;
                if (!FilmLibrary.RecordingActive)
                {
                    if (Preference == "sim") Enter(true);
                    else if (Preference == "on" || (Preference == "auto" && Runtime.Present)) Enter(false);
                }
            }

            if (Active && FilmLibrary.RecordingActive) Exit("a recording started");
            if (phase == Phase.Vr && Time.unscaledTime < sweepUntil) SweepCameras(false);
            Keyboard kb = Keyboard.current;
            if (kb != null && kb.f9Key.wasPressedThisFrame && !FilmLibrary.RecordingActive) Toggle();
        }

        /// <summary>flat UI: a small button to enter or leave VR mode</summary>
        void OnGUI()
        {
            if (Application.isBatchMode || FilmLibrary.RecordingActive) return;
            bool offer = Runtime.Present || Application.isEditor || Preference != "off";
            if (!offer || phase == Phase.Starting) return;
            if (!Active && FilmLibrary.Instance == null) return;
            string label = Active ? "Desktop mode" : Runtime.Present ? "Enter VR" : "VR (simulated hands)";
            float w = Mathf.Clamp(Screen.width * 0.11f, 120f, 190f), h = Mathf.Clamp(Screen.height * 0.04f, 26f, 40f);
            GUI.color = new Color(1f, 1f, 1f, 0.78f);
            if (GUI.Button(new Rect(Screen.width - w - 14f, 12f, w, h), label)) Toggle();
            GUI.color = Color.white;
        }

        // ------------------------------------------------------------------ enter / exit

        public void Toggle()
        {
            if (Active) Exit("toggled");
            else Enter(!Runtime.Present);
        }

        public void Enter(bool simulated)
        {
            if (phase == Phase.Starting || Active) return;
            if (FilmLibrary.RecordingActive)
            {
                Note("not entering VR during a recording");
                return;
            }

            LastError = null;
            if (simulated) Build(false);
            else StartCoroutine(StartXr());
        }

        IEnumerator StartXr()
        {
            phase = Phase.Starting;
            XrStatus = "starting";
            Note("starting XR");
            XRGeneralSettings general = XRGeneralSettings.Instance;
            XRManagerSettings manager = general != null ? general.Manager : null;
            if (manager == null)
            {
                Fail("XR Plug-in Management has no settings for this platform (enable OpenXR in Project Settings > XR Plug-in Management)");
                yield break;
            }

            if (manager.activeLoader == null)
            {
                yield return manager.InitializeLoader();
                startedXr = manager.activeLoader != null;
            }

            if (manager.activeLoader == null)
            {
                Fail("XR could not start: " + (Runtime.Present ? "the OpenXR runtime refused (is the headset connected and Quest Link / Air Link running?)" : Runtime.Note));
                yield break;
            }

            manager.StartSubsystems();
            yield return null;
            List<XRDisplaySubsystem> displays = new();
            SubsystemManager.GetSubsystems(displays);
            bool running = false;
            foreach (XRDisplaySubsystem d in displays) running |= d.running;
            if (!running)
            {
                // some runtimes report running a frame or two late
                for (int i = 0; i < 20 && !running; i++)
                {
                    yield return null;
                    displays.Clear();
                    SubsystemManager.GetSubsystems(displays);
                    foreach (XRDisplaySubsystem d in displays) running |= d.running;
                }
            }

            if (!running)
            {
                StopXr();
                Fail("XR started but no display is running");
                yield break;
            }

            XrStatus = "running (" + manager.activeLoader.name + ")";
            Note("XR display running");
            Build(true);
        }

        void Fail(string message)
        {
            LastError = message;
            XrStatus = "failed";
            phase = Phase.Desktop;
            Note("VR not started: " + message);

        }

        void StopXr()
        {
            XRManagerSettings m = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
            if (m == null || !startedXr) return;
            try
            {
                m.StopSubsystems();
                m.DeinitializeLoader();
            }
            catch (Exception e)
            {
                Debug.LogWarning("VrMode: stopping XR: " + e.Message);
            }

            startedXr = false;
            XrStatus = "stopped";
        }

        void Build(bool xr)
        {
            FilmLibrary lib = FilmLibrary.Instance;
            if (lib != null) lib.Show(); // stops a flat lesson; the VR menu takes over from the library state
            Camera template = DanceText.ViewCamera;
            VRTKLite.Controllers.CameraControl orbit = HeadMovement.Instance != null ? HeadMovement.Instance.OrbitCamera : null;
            simulatorGo = orbit != null ? orbit.gameObject : null;
            if (simulatorGo != null) simulatorGo.SetActive(false);
            VrRig rig = VrRig.Create(xr, template);
            if (template != null)
            {
                rig.SetImmersiveBackground(template.backgroundColor);
                rig.Cam.farClipPlane = Mathf.Max(rig.Cam.farClipPlane, template.farClipPlane);
            }

            Session = new GameObject("VR Session").AddComponent<VrSession>();
            Session.Init(this, rig, xr);
            FilmDirector.HeadIsCamera = true;
            phase = xr ? Phase.Vr : Phase.Simulated;
            Note(xr ? "VR mode on (headset)" : "VR mode on (simulated hands)");
            if (xr)
            {
                // the headset only: the simulated mode keeps the desktop cameras the editor scenarios rely on
                HookSdkManager();
                SweepCameras(true);
            }
        }

        // ------------------------------------------------------------------ one camera in VR

        /// <summary>cameras this mode switched off (their Camera component only), switched back on at Exit</summary>
        readonly List<Camera> disabledCameras = new();
        float sweepUntil = -1f;
        bool sweepLogged;
        VRTKLite.SDK.SDKManager sdkManager;

        /// <summary>
        /// In VR the head camera (VrRig's "VR Head") must be the only camera that renders to the screen / the eye buffers. VRTKLite's
        /// SDKManager.Start loads the "OpenXR Display" setup and activates its GenericXR "head" camera (tracked, enabled): a second
        /// base camera that rendered both eyes every frame, splat passes included (2026-11 GPU audit, 0.5-5 ms). Every other enabled
        /// camera without a target texture is switched off here (the Camera component, never the GameObject: its tracking and children
        /// keep working) - right after the rig is built, every frame for 2 s after it and after each SDKManager setup change (its Start
        /// may run later). Cameras rendering into a texture, cameras under the rig and the head camera's overlay stack are left alone.
        /// </summary>
        void SweepCameras(bool restartWindow)
        {
            if (restartWindow) sweepUntil = Time.unscaledTime + 2f;
            VrRig rig = VrRig.Instance;
            Camera head = rig != null ? rig.Cam : null;
            if (head == null) return;
            List<Camera> stack = head.GetUniversalAdditionalCameraData()?.cameraStack;
            List<string> names = null;
            foreach (Camera c in Camera.allCameras) // enabled, active cameras only
            {
                if (c == head || c.targetTexture != null || c.transform.IsChildOf(rig.transform)) continue;
                if (stack != null && stack.Contains(c)) continue;
                c.enabled = false;
                if (!disabledCameras.Contains(c)) disabledCameras.Add(c);
                (names ??= new List<string>()).Add(PathOf(c.transform));
            }

            if (names != null)
            {
                string line = $"HM-PERF cameras: disabled {string.Join(", ", names)} (only VR Head renders)";
                if (!sweepLogged) Debug.Log(line);
                else Note(line);
                sweepLogged = true;
            }
        }

        static string PathOf(Transform t) => t.parent != null ? t.parent.name + "/" + t.name : t.name;

        void HookSdkManager()
        {
            if (sdkManager != null) return;
            sdkManager = FindAnyObjectByType<VRTKLite.SDK.SDKManager>();
            if (sdkManager != null) sdkManager.LoadedVRSetupChanged += OnSdkSetupChanged; // catches up with a loaded setup at once
        }

        void UnhookSdkManager()
        {
            if (sdkManager != null) sdkManager.LoadedVRSetupChanged -= OnSdkSetupChanged;
            sdkManager = null;
        }

        void OnSdkSetupChanged(GameObject setup)
        {
            if (phase == Phase.Vr) SweepCameras(true);
        }

        void RestoreCameras()
        {
            foreach (Camera c in disabledCameras)
            {
                if (c != null) c.enabled = true;
            }

            disabledCameras.Clear();
            sweepUntil = -1f;
            sweepLogged = false;
        }

        /// <summary>enabled cameras rendering to the screen / XR now (the HM-PERF line: 1 in VR)</summary>
        public static int ScreenCameraCount()
        {
            int n = 0;
            foreach (Camera c in Camera.allCameras)
            {
                if (c.targetTexture == null) n++;
            }

            return n;
        }

        public void Exit(string why = null)
        {
            if (!Active && phase != Phase.Starting) return;
            Note("leaving VR: " + (why ?? "requested"));
            FilmDirector.HeadIsCamera = false;
            FilmLibrary lib = FilmLibrary.Instance;
            if (lib != null) lib.Show(); // a lesson stops; the flat library comes back
            VrQuality.Restore();
            UnhookSdkManager();
            RestoreCameras();
            if (Session != null)
            {
                Session.Teardown();
                Session = null;
            }

            if (simulatorGo != null) simulatorGo.SetActive(true);
            simulatorGo = null;
            StopXr();
            phase = Phase.Desktop;
        }

        void Note(string s)
        {
            Log.Add($"{Time.unscaledTime:0.0} {s}");
            Debug.Log("VrMode: " + s); // the player log / adb logcat -s Unity show the mode changes
            if (Log.Count > 40) Log.RemoveAt(0);
        }

        public Dictionary<string, object> State()
        {
            Dictionary<string, object> s = new()
            {
                ["phase"] = phase.ToString().ToLowerInvariant(), ["active"] = Active, ["preference"] = Preference, ["xr"] = XrStatus, ["error"] = LastError,
                ["runtime"] = new Dictionary<string, object> { ["present"] = Runtime.Present, ["name"] = Runtime.Name, ["path"] = Runtime.Path, ["note"] = Runtime.Note },
                ["headIsCamera"] = FilmDirector.HeadIsCamera, ["log"] = Log
            };
            if (Session != null) s["session"] = Session.State();
            return s;
        }
    }
}
