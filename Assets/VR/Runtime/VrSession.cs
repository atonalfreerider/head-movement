using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace HmVr
{
    /// <summary>
    /// One VR mode session: builds the input, pointers, locomotion, hand visuals, menu, hand panel, captions and wrist button, wires them
    /// to the library and the lesson, and each frame follows the library's state: in the lobby (library / loading) the headset sees only
    /// the menu in an empty dark space; when a lesson starts the user is placed at the lesson's comfort vantage and the world appears;
    /// back to the library takes the lesson down cleanly. The flat screen overlays are kept off the picture.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class VrSession : MonoBehaviour
    {
        public VrMode Mode { get; private set; }
        public VrRig Rig { get; private set; }
        public VrInput Input { get; private set; }
        public VrPointers Pointers { get; private set; }
        public VrLocomotion Locomotion { get; private set; }
        public VrMenu Menu { get; private set; }
        public VrHandPanel HandPanel { get; private set; }
        public VrCaptions Captions { get; private set; }
        public VrWristButton Wrist { get; private set; }
        public VrHandVisuals Hands { get; private set; }
        public VrDisplay Display { get; private set; }
        public VrPlacement Placement { get; private set; }
        public VrPerf Perf { get; private set; }
        public VrHud Hud { get; private set; }
        public VrSplash Splash { get; private set; }
        public bool Xr { get; private set; }
        public int LessonStarts, LessonEnds;
        public string LastLesson;

        bool wasLesson;
        float handsMissing;
        bool handsWereRunning;
        readonly HashSet<Canvas> suppressed = new();
        float nextCanvasScan;
        GameObject root;
        bool tornDown, skeletonsWere;

        public void Init(VrMode mode, VrRig rig, bool xr)
        {
            Mode = mode;
            Rig = rig;
            Xr = xr;
            root = gameObject;
            DontDestroyOnLoad(root);
            Input = root.AddComponent<VrInput>();
            Input.Source = xr ? new XrInputSource(rig.TrackingSpace, rig.Head) : rig.Sim;
            Pointers = root.AddComponent<VrPointers>();
            Locomotion = root.AddComponent<VrLocomotion>();
            Hands = root.AddComponent<VrHandVisuals>();
            Menu = root.AddComponent<VrMenu>();
            HandPanel = root.AddComponent<VrHandPanel>();
            Captions = root.AddComponent<VrCaptions>();
            Wrist = root.AddComponent<VrWristButton>();
            Display = root.AddComponent<VrDisplay>();
            Placement = root.AddComponent<VrPlacement>();
            Perf = root.AddComponent<VrPerf>();
            Hud = root.AddComponent<VrHud>();
            Splash = root.AddComponent<VrSplash>();
            HeadMovement.AsyncLoad = true; // a lesson loads in slices behind the splash (the main thread keeps rendering)

            Menu.OnExitVr = () =>
            {
                if (Application.platform == RuntimePlatform.Android) Application.Quit(); // on the headset there is no desktop to go back to
                else Mode.Exit("Exit VR button");
            };
            Menu.LeftHanded = () => Input.Dominant == Hand.Left;
            Menu.ToggleHanded = () => Input.Dominant = Input.Dominant.Other();
            HandPanel.MenuOpen = () => Menu.IsOpen;
            HandPanel.OnLibrary = () => VrFilm.BackToLibrary();
            Wrist.Available = () => VrFilm.InLesson;
            Wrist.OnPressed = () =>
            {
                if (!Menu.IsOpen)
                {
                    Menu.Open(true);
                    return;
                }

                // an open menu that the user is looking at is closed by the button; one they turned away from is recalled in front of them
                Vector3 to = Menu.AnchorPosition - Rig.Head.position;
                if (Vector3.Angle(Rig.Head.forward, to) > 50f) Menu.Recall();
                else Menu.Close();
            };

            VrQuality.Apply(xr);
            // skeleton lines and joints at FULL opacity whatever the avatar opacity is (a lesson can set 0.25 for the bodies): the translucent
            // body skips the pixels of its own skeleton (stencil), so the skeleton shows in its own colour, untinted by the body
            skeletonsWere = SmplxAvatar.SkeletonsOverBodies;
            SmplxAvatar.SkeletonsOverBodies = true;
            ApplyCullMask(false);
            Placement.Init(rig, Display);
            rig.Moved += RecallPanels;
            Display.Changed += OnDisplayChanged;
            Display.Init(rig, xr); // passthrough or immersive: the remembered choice, else passthrough on a headset
        }

        /// <summary>the user was put somewhere else: every open anchored panel is placed once more in front of them (no drift otherwise)</summary>
        void RecallPanels()
        {
            if (tornDown) return;
            if (Menu != null && Menu.IsOpen) Menu.Recall();
            if (HandPanel != null && HandPanel.Anchored) HandPanel.Recall();
            Captions?.Recall();
            Hud?.Recall();
            Splash?.Recall();
        }

        bool Passthrough => Display != null && Display.PassthroughActive;

        /// <summary>the view was switched (menu button) or passthrough failed to start: the lesson's vantage follows the mode</summary>
        void OnDisplayChanged()
        {
            if (Hands != null) Hands.Minimal = Passthrough;
            if (!VrFilm.InLesson || tornDown) return;
            if (Passthrough)
            {
                // the rig keeps its immersive pose until the placement takes over (next frame, in front of the user)
                Locomotion.Enabled = false;
            }
            else
            {
                Placement.Release();
                Locomotion.GoTo("start", true);
            }
        }

        void ApplyCullMask(bool lesson)
        {
            if (Rig == null || Rig.Cam == null) return;
            Rig.Cam.cullingMask = lesson ? ~0 : 1 << VrStyle.Layer;
        }

        void Update()
        {
            if (tornDown) return;
            FilmLibrary lib = FilmLibrary.Instance;
            if (lib != null && lib.State == FilmLibrary.Mode.Hidden && !FilmLibrary.RecordingActive) lib.Show();
            if (Input.Source is XrInputSource xs)
            {
                handsMissing = xs.HandsRunning ? 0f : handsMissing + Time.unscaledDeltaTime;
                if (xs.HandsRunning != handsWereRunning)
                {
                    handsWereRunning = xs.HandsRunning;
                    Debug.Log("VrSession: hand tracking " + (handsWereRunning ? "running" : "stopped") + " (" + xs.HandsStatus + ")");
                }
                Menu.Notice = handsMissing > 4f ? "No hand tracking from the headset: turn on Hand tracking (Settings > Movement tracking) and put the controllers down" : "";
            }

            bool lesson = VrFilm.InLesson;
            if (lesson && !wasLesson) OnLessonStart();
            else if (!lesson && wasLesson) OnLessonEnd();
            wasLesson = lesson;
            ApplyCullMask(lesson);
            // passthrough: the user walks around the dancers themselves (no teleport, no snap turn, no directed vantage points)
            Locomotion.Enabled = lesson && !Menu.IsOpen && !Passthrough;
            Pointers.SuppressRays = false;
            if (Hands != null && Hands.Minimal != Passthrough) Hands.Minimal = Passthrough;
        }

        void OnLessonStart()
        {
            LessonStarts++;
            LastLesson = VrFilm.Entry?.Id;
            FilmDirector d = VrFilm.Director;
            float t = d != null && !float.IsNaN(d.DanceTime) ? d.DanceTime : 0f;
            List<Viewpoint> list = VrViewpoints.Build(HeadMovement.Instance, VrFilm.Entry, t);
            Locomotion.SetViewpoints(list);
            if (!Passthrough) Locomotion.GoTo("start", true); // in passthrough VrPlacement stands the dance in front of the user instead
            else Rig.ClearPlacement();
            HandPanel.ShowChapters = HandPanel.ShowViews = false;
            Menu.Close();
        }

        void OnLessonEnd()
        {
            LessonEnds++;
            Locomotion.SetViewpoints(null);
            Locomotion.Enabled = false;
            // back at the library: the user is put back at the lobby origin facing forward (passthrough: the rig simply on the real floor)
            Placement.Release();
            if (!Passthrough) Rig.PlaceHead(new Vector3(0f, 0f, 0f), 0f, 0f, false);
        }

        void LateUpdate()
        {
            if (tornDown) return;
            // the flat screen overlays (library, bar, captions, titles) never go into the headset's view or its mirror
            if (Time.unscaledTime >= nextCanvasScan)
            {
                nextCanvasScan = Time.unscaledTime + 0.4f;
                foreach (Canvas c in FindObjectsByType<Canvas>(FindObjectsInactive.Exclude))
                {
                    if (c != null && c.renderMode == RenderMode.ScreenSpaceOverlay && c.enabled) suppressed.Add(c);
                }
            }

            foreach (Canvas c in suppressed)
            {
                if (c != null && c.enabled) c.enabled = false;
            }
        }

        /// <summary>called by VrMode.Exit: the VR objects go, the flat screens come back by themselves (the library is shown again)</summary>
        public void Teardown()
        {
            tornDown = true;
            SmplxAvatar.SkeletonsOverBodies = skeletonsWere;
            HeadMovement.AsyncLoad = false;
            suppressed.Clear();
            if (Rig != null) Destroy(Rig.gameObject);
            Destroy(gameObject);
        }

        public System.Collections.Generic.Dictionary<string, object> State()
        {
            FilmLibrary lib = FilmLibrary.Instance;
            VrInput input = Input;
            Vector3 head = Rig != null && Rig.Cam != null ? Rig.Head.position : Vector3.zero;
            Dictionary<string, object> s = new()
            {
                ["xr"] = Xr, ["source"] = input?.Source?.Name, ["simulated"] = input?.Source?.IsSimulated ?? false, ["library"] = lib?.State.ToString().ToLowerInvariant(),
                ["lesson"] = VrFilm.InLesson, ["lessonId"] = VrFilm.Entry?.Id, ["lessonStarts"] = LessonStarts, ["lessonEnds"] = LessonEnds,
                ["dominant"] = input?.Dominant.ToString().ToLowerInvariant(),
                ["head"] = new[] { Round(head.x), Round(head.y), Round(head.z) }, ["headYawDeg"] = Rig != null ? Mathf.Round(Rig.HeadWorldYawDeg * 10f) / 10f : 0f,
                ["rigFloorY"] = Rig != null ? Rig.FloorY : 0f, ["teleports"] = Locomotion?.Teleports ?? 0, ["turns"] = Locomotion?.Turns ?? 0,
                ["rigTurnsDeg"] = Rig != null ? Rig.TotalTurnDeg : 0f, ["viewpoint"] = Locomotion?.CurrentViewpoint,
                ["viewpoints"] = Locomotion != null ? Locomotion.Viewpoints.ConvertAll(v => (object)new Dictionary<string, object>
                    { ["id"] = v.Id, ["label"] = v.Label, ["head"] = new[] { Round(v.Head.x), Round(v.Head.y), Round(v.Head.z) }, ["yawDeg"] = Round(v.YawDeg), ["floorY"] = v.FloorY }) : null,
                ["cullMask"] = Rig != null && Rig.Cam != null ? Rig.Cam.cullingMask : 0, ["cameraTag"] = Rig != null ? Rig.Cam.tag : null,
                ["pointers"] = Pointers?.State(), ["menu"] = Menu?.State(), ["handPanel"] = HandPanel?.State(),
                ["captions"] = new Dictionary<string, object>
                    { ["enabled"] = Captions != null && Captions.Enabled, ["hasTranscript"] = Captions != null && Captions.HasTranscript, ["alpha"] = Captions?.Alpha ?? 0f, ["text"] = Captions?.CurrentText, ["word"] = Captions?.CurrentWord },
                ["display"] = Display?.State_(), ["placement"] = Placement?.State(), ["perf"] = Perf?.Summary(), ["hud"] = Hud?.State(), ["splash"] = Splash?.State(), ["skeletonsOverBodies"] = SmplxAvatar.SkeletonsOverBodies,
                ["wristButtonAlpha"] = Wrist?.Alpha ?? 0f, ["suppressedCanvases"] = suppressed.Count, ["quality"] = VrQuality.State(), ["hands"] = (Input?.Source as XrInputSource)?.HandsStatus
            };
            if (VrFilm.InLesson)
            {
                s["film"] = new Dictionary<string, object>
                {
                    ["time"] = Round(VrFilm.Time), ["duration"] = Round(VrFilm.Duration), ["rate"] = VrFilm.Rate, ["slowNote"] = VrFilm.SlowAudioHint, ["paused"] = VrFilm.Paused,
                    ["chapter"] = VrFilm.ChapterIndex, ["chapterTitle"] = VrFilm.ChapterTitle, ["scrubbing"] = VrFilm.Director.Scrubbing
                };
            }

            return s;
        }

        static float Round(float v) => Mathf.Round(v * 1000f) / 1000f;
    }
}
