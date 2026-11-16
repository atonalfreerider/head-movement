using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HmVr
{
    /// <summary>A titled, paged list of big buttons (chapters, viewpoints).</summary>
    public sealed class VrListPanel
    {
        public readonly VrPanel Panel;
        public readonly string Title;
        public int PageSize = 5;
        public int Page;
        public Func<string, bool> IsSelected = _ => false;
        public Action<string> OnPick = _ => { };
        readonly List<(string id, string label)> items = new();
        readonly List<(VrWidget w, string id)> rows = new();
        readonly List<(string id, string label)> extra = new();
        readonly List<(string id, string label, Action act)> footer = new();
        VrWidget up, down, pageLabel;
        const float W = 420f, Row = 54f, Gap = 8f, Margin = 18f;

        public VrListPanel(string name, string title, Transform parent, float scale = 0.78f)
        {
            Title = title;
            Panel = VrPanel.Create(name, new Vector2(W, 200f), parent, true, scale);
            Panel.gameObject.SetActive(false);
        }

        public int Count => items.Count;
        public int Pages => Mathf.Max(1, Mathf.CeilToInt(items.Count / (float)PageSize));

        public void SetItems(IEnumerable<(string id, string label)> list, IEnumerable<(string id, string label, Action act)> footerButtons = null)
        {
            items.Clear();
            items.AddRange(list);
            footer.Clear();
            if (footerButtons != null) footer.AddRange(footerButtons);
            Page = Mathf.Clamp(Page, 0, Pages - 1);
            Rebuild();
        }

        public void ShowPageOf(string id)
        {
            int i = items.FindIndex(x => x.id == id);
            if (i < 0) return;
            int p = i / PageSize;
            if (p != Page)
            {
                Page = p;
                Rebuild();
            }
        }

        void Rebuild()
        {
            VrPanel.ClearWidgets(Panel);
            rows.Clear();
            int shown = Mathf.Min(PageSize, items.Count - Page * PageSize);
            shown = Mathf.Max(shown, 0);
            bool paged = Pages > 1;
            float h = Margin + 46f + Gap + shown * (Row + Gap) + (paged ? Row + Gap : 0f) + (footer.Count > 0 ? Row + Gap : 0f) + Margin - Gap * 0.5f;
            Panel.SizeMm = new Vector2(W, h);
            Panel.Content.sizeDelta = Panel.SizeMm;
            float y = h * 0.5f - Margin;
            VrWidget t = Panel.AddLabel("title", Title, new Rect(-W * 0.5f + Margin + 6f, y - 46f, W - 2f * Margin, 46f), 30f, VrStyle.Muted, TextAnchor.MiddleLeft, true);
            y -= 46f + Gap;
            for (int i = 0; i < shown; i++)
            {
                (string id, string label) it = items[Page * PageSize + i];
                string id = it.id;
                VrWidget w = Panel.AddButton("row:" + id, it.label, new Rect(-W * 0.5f + Margin, y - Row, W - 2f * Margin, Row), () => OnPick(id), 25f, false);
                w.Label.alignment = TextAnchor.MiddleLeft;
                RectTransform lr = w.Label.rectTransform;
                lr.offsetMin = new Vector2(18f, 0f);
                lr.offsetMax = new Vector2(-10f, 0f);
                w.Label.horizontalOverflow = HorizontalWrapMode.Wrap;
                w.Label.resizeTextForBestFit = true;
                w.Label.resizeTextMinSize = 14;
                w.Label.resizeTextMaxSize = 25;
                rows.Add((w, id));
                y -= Row + Gap;
            }

            if (paged)
            {
                float bw = (W - 2f * Margin - 2f * Gap) / 3f;
                up = Panel.AddButton("page up", "Up", new Rect(-W * 0.5f + Margin, y - Row, bw, Row), () => SetPage(Page - 1), 25f);
                pageLabel = Panel.AddLabel("page", $"{Page + 1}/{Pages}", new Rect(-W * 0.5f + Margin + bw + Gap, y - Row, bw, Row), 25f, VrStyle.Muted, TextAnchor.MiddleCenter);
                down = Panel.AddButton("page down", "Down", new Rect(-W * 0.5f + Margin + 2f * (bw + Gap), y - Row, bw, Row), () => SetPage(Page + 1), 25f);
                up.Enabled = () => Page > 0;
                down.Enabled = () => Page < Pages - 1;
                y -= Row + Gap;
            }

            if (footer.Count > 0)
            {
                float bw = (W - 2f * Margin - (footer.Count - 1) * Gap) / footer.Count;
                for (int i = 0; i < footer.Count; i++)
                {
                    Action act = footer[i].act;
                    Panel.AddButton("footer:" + footer[i].id, footer[i].label, new Rect(-W * 0.5f + Margin + i * (bw + Gap), y - Row, bw, Row), () => act(), 25f);
                }
            }

            _ = t;
        }

        void SetPage(int p)
        {
            Page = Mathf.Clamp(p, 0, Pages - 1);
            Rebuild();
        }

        public void RefreshSelection()
        {
            foreach ((VrWidget w, string id) r in rows) r.w.Toggled = IsSelected(r.id);
        }

        public float Height => Panel.SizeMm.y;
    }

    /// <summary>
    /// The in-lesson controls on the LEFT WRIST (owner 2026-11-15: the menu tracks the left wrist and appears nowhere else). While the left
    /// hand is tracked and turned palm-up toward the face (PalmDetector) the panel rides on the wrist - posed from the wrist joints every frame
    /// (WristPose, lightly smoothed against tracking jitter), facing the eyes - and it fades away as soon as the hand is lowered or lost; the
    /// right hand pokes it or points at it and pinches (the left hand cannot press its own panel). It carries: play / pause, slow motion (1x,
    /// 0.5x, 0.25x, 0.1x), 5 s back / forward, previous / next chapter, a pinch-drag scrubber with the chapter ticks, the chapter's name and
    /// the time, and tabs for the chapter list, the viewpoint presets (with 45 degree turns), captions, the PHYSICS / RHYTHM mode (a permanent
    /// setting, VrSkeletonPref) and Back to library.
    /// </summary>
    [DefaultExecutionOrder(150)]
    [AddComponentMenu("")]
    public sealed class VrHandPanel : MonoBehaviour
    {
        public static VrHandPanel Instance { get; private set; }

        /// <summary>never anchored in the room any more: the panel lives on the left wrist</summary>
        public bool Anchored => false;
        public int Recalls { get; private set; }
        public Vector3 AnchorPosition => anchor != null ? anchor.position : Vector3.zero;
        public bool Shown => alpha > 0.5f;
        public float Alpha => alpha;
        public VrPanel Main => main;

        /// <summary>the small note beside SLOW MOTION ("dialogue paused" while a speech lesson is slowed), empty otherwise</summary>
        public string SlowNoteText => slowNote != null && slowNote.Label != null ? slowNote.Label.text : "";
        [System.NonSerialized] public VrListPanel ChaptersList, ViewsList;
        public bool ShowChapters, ShowViews;
        public Func<bool> MenuOpen = () => false;
        public Action OnLibrary = () => { };

        VrPanel main;
        Transform anchor;
        VrWidget playBtn, skeletonBtn, scrub, chapterLabel, timeLabel, cc, tabChapters, tabViews, library, slowNote;
        VrWidget[] rateBtns = new VrWidget[4];
        Image pauseA, pauseB, playTri;
        float alpha;
        bool open, posed;
        float lastFacing = -10f;
        Vector3 poseVel;
        int ticksFor = -1;
        const float PanelW = 480f, PanelH = 350f, Scale = 0.5f, MmToM = 0.001f * Scale;
        /// <summary>the panel's centre: this far out of the palm (toward the eyes) and this far from the wrist toward the elbow</summary>
        public const float WristUpM = 0.11f, WristAlongM = 0.04f;
        /// <summary>it stays this long after the palm stops facing (a glance away while poking does not drop it)</summary>
        const float FacingGraceS = 0.6f;

        void Awake()
        {
            Instance = this;
            anchor = new GameObject("Hand panel anchor").transform;
            anchor.SetParent(transform, false);
            main = VrPanel.Create("Hand panel", new Vector2(PanelW, PanelH), anchor, true, Scale);
            Build();
            ChaptersList = new VrListPanel("Chapters panel", "Chapters", anchor, Scale);
            ChaptersList.IsSelected = id => int.TryParse(id, out int i) && i == VrFilm.ChapterIndex;
            ChaptersList.OnPick = id =>
            {
                if (int.TryParse(id, out int i)) VrFilm.GoToChapter(i);
            };
            ViewsList = new VrListPanel("Viewpoints panel", "Viewpoints", anchor, Scale);
            ViewsList.IsSelected = id => VrLocomotion.Instance != null && VrLocomotion.Instance.CurrentViewpoint == id;
            ViewsList.OnPick = id => VrLocomotion.Instance?.GoTo(id);
            main.Alpha = 0f;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Build()
        {
            float W = PanelW, y;
            // row A: the chapter and the time
            y = 142f;
            chapterLabel = main.AddLabel("chapter", "", VrPanel.R(-W * 0.5f + 150f, y, 290f, 46f), 26f, VrStyle.Text, TextAnchor.MiddleLeft, true);
            chapterLabel.Label.horizontalOverflow = HorizontalWrapMode.Wrap; // best fit needs the box to wrap: the name shrinks to fit beside the time
            chapterLabel.Label.verticalOverflow = VerticalWrapMode.Truncate;
            chapterLabel.Label.resizeTextForBestFit = true;
            chapterLabel.Label.resizeTextMinSize = 13;
            chapterLabel.Label.resizeTextMaxSize = 26;
            timeLabel = main.AddLabel("time", "", VrPanel.R(W * 0.5f - 95f, y, 170f, 46f), 24f, VrStyle.Muted, TextAnchor.MiddleRight);
            // row B: scrubber
            y = 86f;
            scrub = main.AddSlider("scrubber", VrPanel.R(0f, y, W - 70f, 56f), OnScrub, OnScrubBegin, OnScrubEnd);
            // row C: transport
            y = 22f;
            float[] wd = { 56f, 74f, 88f, 74f, 56f };
            float gap = 26f, total = 0f;
            foreach (float v in wd) total += v;
            total += gap * (wd.Length - 1);
            float x = -total * 0.5f;
            VrWidget prev = main.AddButton("prev chapter", "", VrPanel.R(x + wd[0] * 0.5f, y, wd[0], 60f), VrFilm.PreviousChapter);
            VrPanel.GlyphSkip(prev, true, 28f);
            x += wd[0] + gap;
            main.AddButton("back 5", "-5 s", VrPanel.R(x + wd[1] * 0.5f, y, wd[1], 60f), () => VrFilm.SeekBy(-5f), 25f);
            x += wd[1] + gap;
            playBtn = main.AddButton("play", "", VrPanel.R(x + wd[2] * 0.5f, y, wd[2], 70f), VrFilm.TogglePlay);
            playBtn.Normal = VrStyle.Accent * 0.9f;
            playBtn.Hot = VrStyle.Accent;
            playBtn.Normal.a = 1f;
            playBtn.Hot.a = 1f;
            playBtn.Pressed = Color.white;
            VrPanel.GlyphPlay(playBtn, 32f);
            playTri = (Image)playBtn.Glyphs[0];
            (pauseA, pauseB) = VrPanel.GlyphPause(playBtn, 32f);
            x += wd[2] + gap;
            main.AddButton("forward 5", "+5 s", VrPanel.R(x + wd[3] * 0.5f, y, wd[3], 60f), () => VrFilm.SeekBy(5f), 25f);
            x += wd[3] + gap;
            VrWidget next = main.AddButton("next chapter", "", VrPanel.R(x + wd[4] * 0.5f, y, wd[4], 60f), VrFilm.NextChapter);
            VrPanel.GlyphSkip(next, false, 28f);
            // row D: slow motion
            main.AddLabel("speed", "SLOW MOTION", VrPanel.R(-W * 0.5f + 110f, -28f, 200f, 24f), 18f, VrStyle.Muted, TextAnchor.MiddleLeft, true);
            // "dialogue paused" while a speech lesson is slowed: its sound is faded out (the captions stay), the note says why it is quiet
            slowNote = main.AddLabel("slow note", "", VrPanel.R(W * 0.5f - 145f, -28f, 250f, 24f), 18f, VrStyle.Accent, TextAnchor.MiddleRight, true);
            y = -80f;
            string[] names = { "1x", "0.5x", "0.25x", "0.1x" };
            float bw = 94f, bg = 24f, bx = -(bw * 4 + bg * 3) * 0.5f;
            for (int i = 0; i < 4; i++)
            {
                float rate = VrFilm.SlowRates[i];
                rateBtns[i] = main.AddButton("rate " + names[i], names[i], VrPanel.R(bx + bw * 0.5f + i * (bw + bg), y, bw, 58f), () => VrFilm.SetRate(rate), 26f);
            }

            // row E: tabs and pin
            y = -146f;
            float tw = 82f, tg = 16f, tx = -(tw * 5 + tg * 4) * 0.5f;
            tabChapters = main.AddButton("tab chapters", "Chapters", VrPanel.R(tx + tw * 0.5f, y, tw, 58f), () => ToggleList(true), 17f);
            tabViews = main.AddButton("tab views", "Views", VrPanel.R(tx + tw * 1.5f + tg, y, tw, 58f), () => ToggleList(false), 20f);
            tabViews.Enabled = () => !PassthroughNow; // in passthrough the user walks around the dancers: no directed viewpoints
            cc = main.AddButton("captions", "CC", VrPanel.R(tx + tw * 2.5f + 2f * tg, y, tw, 58f), CycleCaptions, 24f);
            skeletonBtn = main.AddButton("skeleton mode", "Rhythm", VrPanel.R(tx + tw * 3.5f + 3f * tg, y, tw, 58f), VrSkeletonPref.Toggle, 20f);
            library = main.AddButton("library", "Library", VrPanel.R(tx + tw * 4.5f + 4f * tg, y, tw, 58f), () => OnLibrary(), 18f);
        }

        static bool PassthroughNow => VrDisplay.Instance != null && VrDisplay.Instance.PassthroughActive;

        /// <summary>CC: captions below the dancers' view line, then above it, then off</summary>
        void CycleCaptions()
        {
            VrCaptions c = VrCaptions.Instance;
            if (c == null) return;
            if (!c.Enabled)
            {
                c.Enabled = true;
                c.High = false;
            }
            else if (!c.High)
            {
                c.High = true;
            }
            else
            {
                c.Enabled = false;
                c.High = false;
            }

            c.Recall(); // the captions are anchored: the new height is applied by placing them again
        }

        void ToggleList(bool chapters)
        {
            if (chapters)
            {
                ShowChapters = !ShowChapters;
                if (ShowChapters) ShowViews = false;
            }
            else
            {
                ShowViews = !ShowViews;
                if (ShowViews) ShowChapters = false;
            }
        }

        // ------------------------------------------------------------------ scrubber

        void OnScrubBegin() => VrFilm.BeginScrub();

        void OnScrub(float u)
        {
            float dur = VrFilm.Duration;
            if (dur <= 0f) return;
            IReadOnlyList<LibraryChapter> chs = VrFilm.Chapters;
            List<float> ticks = new();
            foreach (LibraryChapter c in chs)
            {
                if (c.StartS > 0.05f) ticks.Add(c.StartS / dur);
            }

            float snap = SliderMath.Snap(u, ticks, 0.012f);
            if (snap >= 0f) u = snap;
            VrPanel.SetSlider(scrub, u);
            VrFilm.ScrubTo(u);
        }

        void OnScrubEnd() => VrFilm.EndScrub();

        // ------------------------------------------------------------------ per frame

        void Update()
        {
            VrInput input = VrInput.Instance;
            bool lesson = VrFilm.InLesson;
            bool canShow = lesson && input != null && input.Source != null && !MenuOpen();
            VrHand left = canShow ? input.Left : null;
            bool tracked = left != null && left.Tracked;
            if (tracked && left.Palm.Facing) lastFacing = Time.unscaledTime;
            bool wasOpen = open;
            open = tracked && Time.unscaledTime - lastFacing <= FacingGraceS;
            if (open && !wasOpen)
            {
                Recalls++;
                posed = false;
            }

            float dt = Time.unscaledDeltaTime;
            alpha = Mathf.MoveTowards(alpha, open ? 1f : 0f, dt * (open ? 7f : 6f));
            if (!tracked) alpha = 0f; // never left hanging where the hand was
            main.Alpha = alpha;
            main.AttachedTo = Hand.Left; // the right hand presses it; the hand that wears it cannot
            ChaptersList.Panel.AttachedTo = ViewsList.Panel.AttachedTo = Hand.Left;
            bool visible = alpha > 0.002f;
            if (!visible)
            {
                SetListsVisible(false, false);
                return;
            }

            if (!lesson) return;
            FollowWrist(input, dt);
            PlaceListsAndSelection(input);
            UpdateContents();
            bool vc = ShowChapters && alpha > 0.5f, vv = ShowViews && !PassthroughNow && alpha > 0.5f;
            SetListsVisible(vc, vv);
        }

        /// <summary>the panel rides on the left wrist: posed from the wrist joints every frame, facing the eyes, lightly smoothed</summary>
        void FollowWrist(VrInput input, float dt)
        {
            HandState h = input.Left.State;
            if (h == null) return;
            // out of the PALM side (the panel shows while the palm faces the eyes), a little toward the elbow
            WristPose.WristPanelPose(h.Wrist, h.Forward, -h.PalmNormal, input.Frame.HeadPosition, WristUpM * HandState.WorldScale,
                WristAlongM * HandState.WorldScale, out Vector3 pos, out Quaternion rot);
            if (!posed)
            {
                posed = true;
                poseVel = Vector3.zero;
                anchor.SetPositionAndRotation(pos, rot);
            }
            else
            {
                anchor.SetPositionAndRotation(Vector3.SmoothDamp(anchor.position, pos, ref poseVel, 0.045f, Mathf.Infinity, dt),
                    Quaternion.Slerp(anchor.rotation, rot, 1f - Mathf.Exp(-dt / 0.06f)));
            }

            main.transform.localPosition = Vector3.zero;
            main.transform.localRotation = Quaternion.identity;
        }

        /// <summary>kept for callers that used to re-place the panel: it lives on the wrist now, so only the captions and the HUD are recalled</summary>
        public void Recall()
        {
            VrCaptions.Instance?.Recall();
            VrHud.Instance?.Recall();
        }

        public void Close() => lastFacing = -10f;

        void SetListsVisible(bool chapters, bool views)
        {
            if (ChaptersList.Panel.gameObject.activeSelf != chapters) ChaptersList.Panel.gameObject.SetActive(chapters);
            if (ViewsList.Panel.gameObject.activeSelf != views) ViewsList.Panel.gameObject.SetActive(views);
            ChaptersList.Panel.Alpha = chapters ? alpha : 0f;
            ViewsList.Panel.Alpha = views ? alpha : 0f;
        }

        void PlaceListsAndSelection(VrInput input)
        {
            // lists to the side of the main panel (in its own frame), fixed with it
            float side = input.Dominant == Hand.Right ? 1f : -1f;
            float mainHalf = PanelW * 0.5f * MmToM;
            PlaceList(ChaptersList, mainHalf, side);
            PlaceList(ViewsList, mainHalf, side);
            if (ShowChapters)
            {
                ChaptersList.RefreshSelection();
                if (lastChapterPaged != VrFilm.ChapterIndex)
                {
                    lastChapterPaged = VrFilm.ChapterIndex;
                    ChaptersList.ShowPageOf(lastChapterPaged.ToString());
                }
            }

            if (ShowViews) ViewsList.RefreshSelection();
        }

        void PlaceList(VrListPanel list, float mainHalfM, float side)
        {
            float half = list.Panel.SizeMm.x * 0.5f * MmToM;
            float top = (PanelH - list.Panel.SizeMm.y) * 0.5f * MmToM;
            list.Panel.transform.localPosition = new Vector3(side * (mainHalfM + half + 0.025f), top, 0f);
            list.Panel.transform.localRotation = Quaternion.identity;
        }

        void UpdateContents()
        {
            float dur = Mathf.Max(0.01f, VrFilm.Duration);
            float t = VrFilm.Time;
            bool dragging = VrPointers.Instance != null && (VrPointers.Instance.Pointers[0].Drag == scrub || VrPointers.Instance.Pointers[1].Drag == scrub);
            if (!dragging) VrPanel.SetSlider(scrub, t / dur);
            IReadOnlyList<LibraryChapter> chs = VrFilm.Chapters;
            if (ticksFor != chs.Count)
            {
                ticksFor = chs.Count;
                List<float> u = new();
                foreach (LibraryChapter c in chs)
                {
                    if (c.StartS > 0.05f) u.Add(c.StartS / dur);
                }

                main.SetTicks(scrub, u);
                ChaptersList.SetItems(BuildChapterItems());
                ViewsList.SetItems(BuildViewItems(), ViewFooter());
            }

            int ci = VrFilm.ChapterIndex;
            string title = ci >= 0 ? $"{ci + 1}/{chs.Count}  {VrFilm.ChapterTitle}" : (VrFilm.Entry?.Title ?? "");
            if (chapterLabel.Label.text != title) chapterLabel.Label.text = title;
            string time = $"{LibraryData.Clock(t)} / {LibraryData.Clock(dur)}";
            if (timeLabel.Label.text != time) timeLabel.Label.text = time;
            bool playing = VrFilm.Playing;
            playTri.gameObject.SetActive(!playing);
            pauseA.gameObject.SetActive(playing);
            pauseB.gameObject.SetActive(playing);
            for (int i = 0; i < rateBtns.Length; i++) rateBtns[i].Toggled = Mathf.Abs(VrFilm.Rate - VrFilm.SlowRates[i]) < 0.001f;
            string slowHint = VrFilm.SlowAudioHint;
            if (slowNote.Label.text != slowHint) slowNote.Label.text = slowHint;
            bool physics = VrSkeletonPref.Physics;
            string sk = physics ? "Physics" : "Rhythm";
            if (skeletonBtn.Label.text != sk) skeletonBtn.Label.text = sk;
            skeletonBtn.Toggled = physics;
            tabChapters.Toggled = ShowChapters;
            tabViews.Toggled = ShowViews;
            VrCaptions caps = VrCaptions.Instance;
            cc.Toggled = caps != null && caps.Enabled && caps.HasTranscript;
            string ccLabel = caps == null || !caps.Enabled ? "CC off" : caps.High ? "CC up" : "CC";
            if (cc.Label.text != ccLabel) cc.Label.text = ccLabel;
            cc.Enabled = () => VrCaptions.Instance != null && VrCaptions.Instance.HasTranscript;
            tabChapters.Enabled = () => VrFilm.Chapters.Count > 0;
            // the viewpoint list follows the locomotion's list (it is rebuilt when the lesson starts)
            if (VrLocomotion.Instance != null && lastViewCount != VrLocomotion.Instance.Viewpoints.Count)
            {
                lastViewCount = VrLocomotion.Instance.Viewpoints.Count;
                ViewsList.SetItems(BuildViewItems(), ViewFooter());
            }
        }

        int lastViewCount = -1, lastChapterPaged = -2;

        IEnumerable<(string id, string label)> BuildChapterItems()
        {
            IReadOnlyList<LibraryChapter> chs = VrFilm.Chapters;
            for (int i = 0; i < chs.Count; i++) yield return (i.ToString(), $"{i + 1}   {chs[i].Title}");
        }

        IEnumerable<(string id, string label)> BuildViewItems()
        {
            if (VrLocomotion.Instance == null) yield break;
            foreach (Viewpoint vp in VrLocomotion.Instance.Viewpoints) yield return (vp.Id, vp.Label);
        }

        IEnumerable<(string id, string label, Action act)> ViewFooter()
        {
            yield return ("turn left", "Turn 45 left", () => VrLocomotion.Instance?.TurnBy(-VrLocomotion.SnapDegrees));
            yield return ("turn right", "Turn 45 right", () => VrLocomotion.Instance?.TurnBy(VrLocomotion.SnapDegrees));
        }

        public Dictionary<string, object> State() => new()
        {
            ["shown"] = Shown, ["alpha"] = Math.Round(alpha, 3), ["open"] = open, ["onWrist"] = "left", ["recalls"] = Recalls, ["chapters"] = ShowChapters,
            ["views"] = ShowViews, ["skeleton"] = VrSkeletonPref.Physics ? "physics" : "rhythm",
            ["position"] = main != null ? new[] { main.transform.position.x, main.transform.position.y, main.transform.position.z } : null
        };
    }
}
