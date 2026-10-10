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
        public bool Xr { get; private set; }
        public int LessonStarts, LessonEnds;
        public string LastLesson;

        bool wasLesson;
        float handsMissing;
        readonly HashSet<Canvas> suppressed = new();
        float nextCanvasScan;
        GameObject root;
        bool tornDown;

        public void Init(VrMode mode, VrRig rig, bool xr)
        {
            Mode = mode;
            Rig = rig;
            Xr = xr;
            root = gameObject;
            DontDestroyOnLoad(root);
            Input = root.AddComponent<VrInput>();
            Input.Source = xr ? new XrInputSource(rig.transform, rig.Head) : rig.Sim;
            Pointers = root.AddComponent<VrPointers>();
            Locomotion = root.AddComponent<VrLocomotion>();
            Hands = root.AddComponent<VrHandVisuals>();
            Menu = root.AddComponent<VrMenu>();
            HandPanel = root.AddComponent<VrHandPanel>();
            Captions = root.AddComponent<VrCaptions>();
            Wrist = root.AddComponent<VrWristButton>();

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
                if (Menu.IsOpen) Menu.Close();
                else Menu.Open(true);
            };

            VrQuality.Apply(xr);
            ApplyCullMask(false);
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
                Menu.Notice = handsMissing > 4f ? "No hand tracking from the headset: turn on Hand tracking (Settings > Movement tracking) and put the controllers down" : "";
            }

            bool lesson = VrFilm.InLesson;
            if (lesson && !wasLesson) OnLessonStart();
            else if (!lesson && wasLesson) OnLessonEnd();
            wasLesson = lesson;
            ApplyCullMask(lesson);
            Locomotion.Enabled = lesson && !Menu.IsOpen;
            Pointers.SuppressRays = false;
        }

        void OnLessonStart()
        {
            LessonStarts++;
            LastLesson = VrFilm.Entry?.Id;
            FilmDirector d = VrFilm.Director;
            float t = d != null && !float.IsNaN(d.DanceTime) ? d.DanceTime : 0f;
            List<Viewpoint> list = VrViewpoints.Build(HeadMovement.Instance, VrFilm.Entry, t);
            Locomotion.SetViewpoints(list);
            Locomotion.GoTo("start", true);
            HandPanel.ShowChapters = HandPanel.ShowViews = false;
            Menu.Close();
        }

        void OnLessonEnd()
        {
            LessonEnds++;
            Locomotion.SetViewpoints(null);
            Locomotion.Enabled = false;
            // back at the library: the user is put back at the lobby origin facing forward
            Rig.PlaceHead(new Vector3(0f, 0f, 0f), 0f, 0f, false);
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
                ["wristButtonAlpha"] = Wrist?.Alpha ?? 0f, ["suppressedCanvases"] = suppressed.Count, ["quality"] = VrQuality.State(), ["hands"] = (Input?.Source as XrInputSource)?.HandsStatus
            };
            if (VrFilm.InLesson)
            {
                s["film"] = new Dictionary<string, object>
                {
                    ["time"] = Round(VrFilm.Time), ["duration"] = Round(VrFilm.Duration), ["rate"] = VrFilm.Rate, ["paused"] = VrFilm.Paused,
                    ["chapter"] = VrFilm.ChapterIndex, ["chapterTitle"] = VrFilm.ChapterTitle, ["scrubbing"] = VrFilm.Director.Scrubbing
                };
            }

            return s;
        }

        static float Round(float v) => Mathf.Round(v * 1000f) / 1000f;
    }
}
