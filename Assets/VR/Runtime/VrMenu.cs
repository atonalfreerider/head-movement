using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace HmVr
{
    /// <summary>
    /// The lesson menu in VR: one big card per film of the library (the same library.json as the flat screen: poster, title, subtitle,
    /// length, chapter count, a Play button; a film whose files are missing says Processing and cannot be started), on an arc at arm's
    /// length in front of the user, a footer for the mode switches, and a loading panel while a lesson loads. Targets are 5-6 cm high with
    /// 3 cm between them; the cards are poked or pointed at and pinched. Seated or standing: the arc follows the user's gaze when they turn
    /// away from it. Opened over a lesson (the wrist button) it adds Resume and Back to library.
    /// </summary>
    [DefaultExecutionOrder(160)]
    [AddComponentMenu("")]
    public sealed class VrMenu : MonoBehaviour
    {
        public static VrMenu Instance { get; private set; }

        public const float Radius = 0.78f, CardW = 320f, CardH = 520f, CardStepMm = 355f, FooterW = 1100f, FooterH = 200f;

        public bool IsOpen { get; private set; }
        public bool OverLesson { get; private set; }
        public Action OnExitVr = () => { };
        public Func<bool> LeftHanded = () => false;
        public Action ToggleHanded = () => { };

        sealed class Card
        {
            public LibraryEntry Entry;
            public VrPanel Panel;
            public VrWidget Play;
        }

        readonly List<Card> cards = new();
        VrPanel header, footer, loading;
        VrWidget footerNote, loadTitle, loadStatus, loadBar, handedBtn, resumeBtn, libraryBtn, exitBtn, viewBtn, hairBtn;
        string signature;
        float menuYawDeg;
        bool placed, loadPlaced, wasFacing;
        float menuY;
        Vector3 menuHead, menuShift;
        public int Recalls { get; private set; }
        public Vector3 AnchorPosition => cards.Count > 0 ? cards[cards.Count / 2].Panel.transform.position : Vector3.zero;
        float nextRefresh;
        bool pausedByOverlay;
        readonly Dictionary<string, (DateTime stamp, Texture2D tex)> thumbs = new();
        public string Message = "";
        /// <summary>a standing remark under the buttons (the session sets it: hand tracking is not available)</summary>
        public string Notice = "";

        public IReadOnlyList<VrPanel> CardPanels => cards.Select(c => c.Panel).ToList();

        void Awake()
        {
            Instance = this;
            header = VrPanel.Create("VR menu header", new Vector2(760f, 120f), transform);
            header.Background.color = new Color(0.04f, 0.047f, 0.07f, 0.9f);
            header.AddLabel("title", "Head Movement", new Rect(-340f, 6f, 680f, 60f), 44f, VrStyle.Text, TextAnchor.MiddleCenter, true);
            header.AddLabel("kicker", "Choose a lesson", new Rect(-340f, -40f, 680f, 38f), 28f, VrStyle.Muted, TextAnchor.MiddleCenter);
            header.Interactive = false;

            footer = VrPanel.Create("VR menu footer", new Vector2(FooterW, FooterH), transform);
            footer.Background.color = new Color(0.04f, 0.047f, 0.07f, 0.9f);
            BuildFooter();

            loading = VrPanel.Create("VR loading", new Vector2(640f, 230f), transform);
            loading.Interactive = false;
            loadTitle = loading.AddLabel("title", "", new Rect(-290f, 50f, 580f, 90f), 36f, VrStyle.Text, TextAnchor.MiddleCenter, true, true);
            loadStatus = loading.AddLabel("status", "", new Rect(-290f, -30f, 580f, 50f), 24f, VrStyle.Muted, TextAnchor.MiddleCenter, false, true);
            loadBar = loading.AddBox("bar back", new Rect(-250f, -78f, 500f, 10f), new Color(1f, 1f, 1f, 0.15f));
            loading.AddBox("bar fill", new Rect(-250f, -78f, 140f, 10f), VrStyle.Accent);
            SetAll(false);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            foreach (var kv in thumbs)
            {
                if (kv.Value.tex != null) Destroy(kv.Value.tex);
            }
        }

        void BuildFooter()
        {
            VrPanel.ClearWidgets(footer);
            float w = FooterW;
            // two rows of three big buttons, kept narrow so the pointing ray to a corner button does not pass behind the outer cards
            const float bw = 330f, gap = 20f, rowA = 42f, rowB = -30f;
            float x0 = -(bw + gap);
            resumeBtn = footer.AddButton("resume", "Resume lesson", VrPanel.R(x0, rowA, bw, 58f), () => Close(), 25f);
            libraryBtn = footer.AddButton("back to library", "Back to library", VrPanel.R(x0 + bw + gap, rowA, bw, 58f), () => VrFilm.BackToLibrary(), 25f);
            viewBtn = footer.AddButton("view mode", "", VrPanel.R(x0 + 2f * (bw + gap), rowA, bw, 58f), () => VrDisplay.Instance?.Toggle(), 23f);
            viewBtn.LabelFunc = ViewLabel;
            hairBtn = footer.AddButton("hair level", "", VrPanel.R(x0, rowB, bw, 58f), () => VrQuality.SetHair(VrQuality.NextHairLevel(VrQuality.Hair)), 23f);
            hairBtn.LabelFunc = () => VrQuality.Current == VrQuality.Tier.Quest ? "Hair: " + VrQuality.Hair : "Hair: PC";
            hairBtn.Enabled = () => VrQuality.Current == VrQuality.Tier.Quest;
            handedBtn = footer.AddButton("handedness", "", VrPanel.R(x0 + bw + gap, rowB, bw, 58f), () => ToggleHanded(), 23f);
            handedBtn.LabelFunc = () => LeftHanded() ? "Pointing: left hand" : "Pointing: right hand";
            exitBtn = footer.AddButton("exit vr", Application.platform == RuntimePlatform.Android ? "Quit" : "Exit VR", VrPanel.R(x0 + 2f * (bw + gap), rowB, bw, 58f), () => OnExitVr(), 25f);
            footerNote = footer.AddLabel("note", "", VrPanel.R(0f, -80f, w - 40f, 30f), 20f, VrStyle.Amber, TextAnchor.MiddleCenter);
        }

        static string ViewLabel()
        {
            VrDisplay d = VrDisplay.Instance;
            if (d == null) return "View";
            if (d.Current == VrDisplay.State.Failed) return "View: Immersive";
            return d.Wanted == DisplayMode.Passthrough ? (d.Current == VrDisplay.State.Starting ? "View: Passthrough..." : "View: Passthrough") : "View: Immersive";
        }

        // ------------------------------------------------------------------ open / close

        public void Open(bool overLesson)
        {
            OverLesson = overLesson;
            IsOpen = true;
            placed = false;
            if (overLesson && VrFilm.InLesson && !VrFilm.Paused)
            {
                VrFilm.Pause();
                pausedByOverlay = true;
            }

            RebuildCards(true);
        }

        public void Close()
        {
            IsOpen = false;
            if (pausedByOverlay)
            {
                pausedByOverlay = false;
                if (VrFilm.InLesson) VrFilm.Resume();
            }

            OverLesson = false;
        }

        void SetAll(bool on)
        {
            header.Alpha = on ? 1f : 0f;
            footer.Alpha = on ? 1f : 0f;
            foreach (Card c in cards) c.Panel.Alpha = on ? 1f : 0f;
        }

        // ------------------------------------------------------------------ cards

        void RebuildCards(bool force)
        {
            FilmLibrary lib = FilmLibrary.Instance;
            if (lib == null) return;
            IReadOnlyList<LibraryEntry> films = lib.Films;
            string sig = string.Join("|", films.Select(f => $"{f.Id}/{f.Ready}/{f.Title}/{f.Subtitle}/{f.DurationS}/{f.Chapters.Count}/{f.ThumbnailPath}"));
            if (!force && sig == signature && cards.Count == films.Count) return;
            if (sig != signature) Debug.Log($"VrMenu: {films.Count} library cards ({films.Count(f => f.Ready)} ready) read from {LibraryData.FilePath}");
            signature = sig;
            foreach (Card c in cards)
            {
                if (c.Panel != null) Destroy(c.Panel.gameObject);
            }

            cards.Clear();
            foreach (LibraryEntry e in films)
            {
                Card c = new() { Entry = e };
                c.Panel = VrPanel.Create("VR card " + e.Id, new Vector2(CardW, CardH), transform);
                c.Panel.Background.color = new Color(0.082f, 0.094f, 0.129f, 0.97f);
                BuildCard(c);
                cards.Add(c);
            }
        }

        void BuildCard(Card c)
        {
            LibraryEntry e = c.Entry;
            VrPanel p = c.Panel;
            Rect pic = VrPanel.R(0f, 128f, 288f, 232f);
            VrWidget back = p.AddBox("poster back", pic, new Color(0.02f, 0.025f, 0.04f));
            Texture2D tex = ThumbFor(e);
            if (tex != null)
            {
                VrWidget w = p.AddPicture("poster", pic, tex);
                // cover-fit, biased to the upper-middle (where the dancers' faces are in a portrait poster)
                float ta = tex.width / (float)tex.height, ba = pic.width / pic.height;
                Rect uv = ta < ba ? new Rect(0f, Mathf.Clamp01(0.58f - ta / ba * 0.5f), 1f, ta / ba) : new Rect(0.5f - ba / ta * 0.5f, 0f, ba / ta, 1f);
                w.Picture.uvRect = uv;
                w.Picture.color = e.Ready ? Color.white : new Color(1f, 1f, 1f, 0.35f);
            }
            else
            {
                p.AddLabel("poster label", e.Ready ? e.Title : "Processing", new Rect(pic.x + 10f, pic.y + 60f, pic.width - 20f, 110f), 28f, new Color(1f, 1f, 1f, 0.8f), TextAnchor.MiddleCenter, true, true);
            }

            if (!e.Ready)
            {
                p.AddBox("processing pill", VrPanel.R(-86f, 218f, 100f, 34f), new Color(0f, 0f, 0f, 0.8f));
                p.AddLabel("processing pill label", "Processing", VrPanel.R(-86f, 218f, 100f, 34f), 18f, VrStyle.Amber, TextAnchor.MiddleCenter, true);
            }

            BestFit(p.AddLabel("title", e.Title ?? "", VrPanel.R(0f, -34f, 288f, 76f), 29f, VrStyle.Text, TextAnchor.UpperLeft, true, true), 20, 29);
            BestFit(p.AddLabel("subtitle", e.Subtitle ?? "", VrPanel.R(0f, -110f, 288f, 76f), 22f, VrStyle.Muted, TextAnchor.UpperLeft, false, true), 16, 22);
            List<string> meta = new();
            if (!string.IsNullOrEmpty(e.DurationText)) meta.Add(e.DurationText);
            if (e.Chapters.Count > 0) meta.Add(e.Chapters.Count == 1 ? "1 chapter" : $"{e.Chapters.Count} chapters");
            p.AddLabel("meta", string.Join("   ·   ", meta), VrPanel.R(0f, -168f, 288f, 30f), 22f, new Color(0.82f, 0.86f, 0.93f), TextAnchor.MiddleLeft);
            c.Play = p.AddButton("play:" + e.Id, e.Ready ? "Play" : "Processing", VrPanel.R(0f, -222f, 288f, 58f), () => PlayEntry(c.Entry), 29f);
            if (e.Ready)
            {
                c.Play.Normal = new Color(VrStyle.Accent.r * 0.85f, VrStyle.Accent.g * 0.85f, VrStyle.Accent.b * 0.85f, 1f);
                c.Play.Hot = VrStyle.Accent;
                c.Play.Pressed = Color.white;
                c.Play.LabelNormal = VrStyle.Ink;
            }

            c.Play.Enabled = () => c.Entry.Ready;
        }

        static void BestFit(VrWidget w, int min, int max)
        {
            w.Label.resizeTextForBestFit = true;
            w.Label.resizeTextMinSize = min;
            w.Label.resizeTextMaxSize = max;
        }

        void PlayEntry(LibraryEntry e)
        {
            FilmLibrary lib = FilmLibrary.Instance;
            if (lib == null || e == null) return;
            if (OverLesson)
            {
                pausedByOverlay = false; // the lesson is replaced; nothing to resume
                OverLesson = false;
            }

            if (!e.Ready)
            {
                Message = $"{e.Title}: {e.WhyNot}";
                return;
            }

            // loading a capture blocks the main thread for seconds on a headset: put the loading panel in front of the user first (a moment), then load
            Message = "";
            IsOpen = false;
            pendingPlay = e;
            pendingUntil = Time.unscaledTime + 0.4f;
            Debug.Log($"VrMenu: loading '{e.Id}'");
        }

        bool autoPlayDone;
        LibraryEntry pendingPlay;
        float pendingUntil;

        Texture2D ThumbFor(LibraryEntry e)
        {
            string p = e.ThumbnailPath;
            if (p == null || !File.Exists(p)) return null;
            DateTime stamp = File.GetLastWriteTimeUtc(p);
            if (thumbs.TryGetValue(p, out var hit) && hit.tex != null && hit.stamp == stamp) return hit.tex;
            if (hit.tex != null) Destroy(hit.tex);
            try
            {
                Texture2D t = new(2, 2, TextureFormat.RGBA32, true) { name = "vr library thumbnail", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
                if (!t.LoadImage(File.ReadAllBytes(p), true))
                {
                    Destroy(t);
                    return null;
                }

                thumbs[p] = (stamp, t);
                return t;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ per frame

        void Update()
        {
            FilmLibrary lib = FilmLibrary.Instance;
            VrInput input = VrInput.Instance;
            if (lib == null || input == null || input.Source == null) return;
            bool lessonPlaying = lib.State == FilmLibrary.Mode.Playing;
            bool lobby = lib.State == FilmLibrary.Mode.Library || lib.State == FilmLibrary.Mode.Hidden;
            bool loadingNow = lib.State == FilmLibrary.Mode.Loading || pendingPlay != null;
            if (pendingPlay != null && Time.unscaledTime >= pendingUntil)
            {
                LibraryEntry e = pendingPlay;
                pendingPlay = null;
                if (!lib.Play(e)) Message = lib.Error ?? "could not start";
                loadingNow = lib.State == FilmLibrary.Mode.Loading;
            }

            if (lobby && !IsOpen)
            {
                IsOpen = true;
                OverLesson = false;
                placed = false;
            }

            if (lessonPlaying && IsOpen && !OverLesson) IsOpen = false;
            if (!lessonPlaying && OverLesson) OverLesson = false;
            if (loadingNow) IsOpen = false;
            if (Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + 1f;
                if (IsOpen) RebuildCards(false);
            }

            if (!autoPlayDone && lib.State == FilmLibrary.Mode.Library && lib.Films.Count > 0 && Time.unscaledTime > 6f)
            {
                autoPlayDone = true;
                string want = VrArgs.Get("-hmplay");
                LibraryEntry auto = want == null ? null : lib.Films.FirstOrDefault(f => string.Equals(f.Id, want, StringComparison.OrdinalIgnoreCase));
                if (auto != null) PlayEntry(auto);
            }

            // palm raised (non-pointing hand) while the menu is open: recall it in front of the user
            bool facing = input.OffHand.Tracked && input.OffHand.Palm.Facing;
            if (facing && !wasFacing && IsOpen) Recall();
            wasFacing = facing;
            bool show = IsOpen && cards.Count >= 0 && !(VrSplash.Instance != null && VrSplash.Instance.Blocking);
            float dt = Time.unscaledDeltaTime;
            float target = show ? 1f : 0f;
            header.Alpha = Mathf.MoveTowards(header.Alpha, target, dt * 6f);
            footer.Alpha = header.Alpha;
            foreach (Card c in cards) c.Panel.Alpha = header.Alpha;
            header.Interactive = false;
            footer.Interactive = true;
            if (show) Layout(input);

            // footer buttons depend on the context
            resumeBtn.Enabled = () => OverLesson;
            libraryBtn.Enabled = () => OverLesson;
            exitBtn.Enabled = () => true;
            VrDisplay disp = VrDisplay.Instance;
            string displayNote = disp != null && disp.Current == VrDisplay.State.Failed ? VrDisplay.StatusLine : "";
            footerNote.Label.text = !string.IsNullOrEmpty(Message) ? Message : !string.IsNullOrEmpty(lib.Error) ? lib.Error : !string.IsNullOrEmpty(displayNote) ? displayNote : Notice ?? "";
            if (lib.State == FilmLibrary.Mode.Library && !string.IsNullOrEmpty(lib.Error)) Message = "";

            // loading
            loading.Alpha = 0f; // the loading screen is the splash (VrSplash) now
            UpdateSplash(lib, loadingNow);
            // an open menu over a finished lesson keeps the film paused; closing resumes
        }

        bool startSplashDone;

        void UpdateSplash(FilmLibrary lib, bool loadingNow)
        {
            VrSplash splash = VrSplash.Instance;
            if (splash == null) return;
            if (loadingNow)
            {
                LibraryEntry e = lib.Current ?? pendingPlay;
                splash.Show(e?.Title ?? "", e?.Subtitle ?? "");
                HeadMovement hm = HeadMovement.Instance;
                FilmDirector d = FilmDirector.Instance;
                float p = hm != null && hm.IsLoadingCapture ? hm.LoadProgress : d != null && d.IsReady ? 0.98f : hm != null ? Mathf.Max(0.02f, hm.LoadProgress) : 0.02f;
                string stage = pendingPlay != null ? "getting ready" : hm != null && hm.IsLoadingCapture ? hm.LoadStage : d != null && d.IsReady ? "starting" : "loading the soundtrack";
                splash.SetProgress(p, stage);
                startSplashDone = true;
                return;
            }

            if (!startSplashDone)
            {
                // the app's own splash: at least two seconds (the menu cards and their posters build behind it), then it gives way to the menu
                splash.Show("", "", 2.4f);
                splash.SetProgress(Mathf.Clamp01((Time.unscaledTime - 0.3f) / 2.4f), "starting");
                if (splash.MinimumElapsed && cards.Count > 0 && Time.unscaledTime > 2.6f)
                {
                    startSplashDone = true;
                    splash.Hide();
                }

                return;
            }

            splash.Hide();
        }

        /// <summary>the arc is placed ONCE when the menu opens (in front of the user along the gaze) and stays; Recall() places it again</summary>
        public void Recall()
        {
            placed = false;
        }

        void Layout(VrInput input)
        {
            InputFrame f = VrAnchor.Now(input.Frame);
            if (!placed)
            {
                placed = true;
                float headYaw = Mathf.Atan2(f.HeadForward.x, f.HeadForward.z) * Mathf.Rad2Deg;
                if (f.HeadForward.x * f.HeadForward.x + f.HeadForward.z * f.HeadForward.z < 0.04f) headYaw = menuYawDeg; // looking straight up or down
                menuYawDeg = headYaw;
                menuY = f.HeadPosition.y;
                menuHead = f.HeadPosition;
                Vector3 d0 = ViewpointMath.Dir(menuYawDeg);
                Vector3 front = new(menuHead.x + d0.x * Radius, menuY - 0.12f, menuHead.z + d0.z * Radius);
                menuShift = VrAnchor.KeepOutOfDancers(front, menuHead) - front; // never opens among the dancers
                Recalls++;
            }

            Vector3 head = menuHead;
            float centreY = menuY - 0.12f;
            int n = cards.Count;
            float stepDeg = CardStepMm * 0.001f / Radius * Mathf.Rad2Deg;
            for (int i = 0; i < n; i++)
            {
                float a = (i - (n - 1) * 0.5f) * stepDeg;
                Vector3 dir = ViewpointMath.Dir(menuYawDeg + a);
                Vector3 pos = new Vector3(head.x + dir.x * Radius, centreY, head.z + dir.z * Radius) + menuShift;
                cards[i].Panel.FaceFrom(pos, new Vector3(head.x, pos.y, head.z), 0f);
            }

            Vector3 fwd = ViewpointMath.Dir(menuYawDeg);
            Vector3 hp = new Vector3(head.x + fwd.x * Radius, centreY + CardH * 0.0005f + 0.09f, head.z + fwd.z * Radius) + menuShift;
            header.FaceFrom(hp, new Vector3(head.x, hp.y, head.z), 0f);
            Vector3 fp = new Vector3(head.x + fwd.x * (Radius - 0.03f), centreY - CardH * 0.0005f - 0.155f, head.z + fwd.z * (Radius - 0.03f)) + menuShift;
            footer.FaceFrom(fp, new Vector3(head.x, fp.y, head.z), 0f);
        }

        // ------------------------------------------------------------------ state

        public Dictionary<string, object> State() => new()
        {
            ["open"] = IsOpen, ["overLesson"] = OverLesson, ["cards"] = cards.Select(c => new Dictionary<string, object>
            {
                ["id"] = c.Entry.Id, ["title"] = c.Entry.Title, ["ready"] = c.Entry.Ready, ["playLabel"] = c.Play?.Label?.text,
                ["position"] = new[] { c.Panel.transform.position.x, c.Panel.transform.position.y, c.Panel.transform.position.z }
            }).ToList(),
            ["message"] = Message, ["loading"] = loading != null && loading.Alpha > 0.5f, ["menuYawDeg"] = Math.Round(menuYawDeg, 1)
        };
    }
}
