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

        public VrListPanel(string name, string title, Transform parent)
        {
            Title = title;
            Panel = VrPanel.Create(name, new Vector2(W, 200f), parent, true, 0.78f);
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
    /// The in-lesson controls, all hand input. A panel floats above the palm of the non-dominant hand while it is raised palm-up in view
    /// (PalmDetector), and fades away when it is not; the other hand pokes it or points at it and pinches. It carries: play / pause,
    /// slow motion (1x, 0.5x, 0.25x, 0.1x), 5 s back / forward, previous / next chapter, a pinch-drag scrubber with the chapter ticks, the
    /// chapter's name and the time, and tabs for the chapter list, the viewpoint presets (with 45 degree turns), captions on / off and
    /// Back to library. "Pin" leaves the panel hanging in the air where it is (steadier than a hand) until unpinned.
    /// </summary>
    [DefaultExecutionOrder(150)]
    [AddComponentMenu("")]
    public sealed class VrHandPanel : MonoBehaviour
    {
        public static VrHandPanel Instance { get; private set; }

        public bool Pinned { get; private set; }
        public bool Shown => alpha > 0.5f;
        public float Alpha => alpha;
        public VrPanel Main => main;
        [System.NonSerialized] public VrListPanel ChaptersList, ViewsList;
        public bool ShowChapters, ShowViews;
        public Func<bool> MenuOpen = () => false;
        public Action OnLibrary = () => { };

        VrPanel main;
        Transform anchor;
        VrWidget playBtn, pin, scrub, chapterLabel, timeLabel, cc, tabChapters, tabViews, library;
        VrWidget[] rateBtns = new VrWidget[4];
        Image pauseA, pauseB, playTri;
        Vector3 vel;
        float alpha, graceUntil;
        bool wasShown;
        int ticksFor = -1;
        const float PanelW = 480f, PanelH = 350f, Scale = 0.78f, MmToM = 0.001f * Scale;

        void Awake()
        {
            Instance = this;
            anchor = new GameObject("Hand panel anchor").transform;
            anchor.SetParent(transform, false);
            main = VrPanel.Create("Hand panel", new Vector2(PanelW, PanelH), anchor, true, Scale);
            Build();
            ChaptersList = new VrListPanel("Chapters panel", "Chapters", anchor);
            ChaptersList.IsSelected = id => int.TryParse(id, out int i) && i == VrFilm.ChapterIndex;
            ChaptersList.OnPick = id =>
            {
                if (int.TryParse(id, out int i)) VrFilm.GoToChapter(i);
            };
            ViewsList = new VrListPanel("Viewpoints panel", "Viewpoints", anchor);
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
            cc = main.AddButton("captions", "CC", VrPanel.R(tx + tw * 2.5f + 2f * tg, y, tw, 58f), CycleCaptions, 24f);
            pin = main.AddButton("pin", "Pin", VrPanel.R(tx + tw * 3.5f + 3f * tg, y, tw, 58f), () => SetPinned(!Pinned), 21f);
            library = main.AddButton("library", "Library", VrPanel.R(tx + tw * 4.5f + 4f * tg, y, tw, 58f), () => OnLibrary(), 18f);
        }

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
        }

        void SetPinned(bool on)
        {
            Pinned = on;
            pin.Toggled = on;
            pin.Label.text = on ? "Pinned" : "Pin";
            if (!on) main.AttachedTo = null;
        }

        public void SetPinnedForTest(bool on) => SetPinned(on);

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
            bool want = false;
            if (lesson && input != null && input.Source != null && !MenuOpen())
            {
                VrHand off = input.OffHand;
                if (Pinned) want = true;
                else if (off.Tracked && off.Palm.Facing) want = true;
                // keep it while a hand is working on it
                bool busy = false;
                foreach (VrWidget w in main.Widgets) busy |= w.Pushed || w.Hovered;
                if (busy) graceUntil = Time.unscaledTime + 0.6f;
                if (Time.unscaledTime < graceUntil && !Pinned && alpha > 0.5f) want = true;
            }

            float dt = Time.unscaledDeltaTime;
            alpha = Mathf.MoveTowards(alpha, want ? 1f : 0f, dt * (want ? 7f : 4f));
            main.Alpha = alpha;
            bool visible = alpha > 0.002f;
            if (!visible)
            {
                wasShown = false;
                SetListsVisible(false, false);
                return;
            }

            if (!lesson) return;
            Place(input, !wasShown, dt);
            wasShown = true;
            UpdateContents();
            bool vc = ShowChapters && alpha > 0.5f, vv = ShowViews && alpha > 0.5f;
            SetListsVisible(vc, vv);
        }

        void SetListsVisible(bool chapters, bool views)
        {
            if (ChaptersList.Panel.gameObject.activeSelf != chapters) ChaptersList.Panel.gameObject.SetActive(chapters);
            if (ViewsList.Panel.gameObject.activeSelf != views) ViewsList.Panel.gameObject.SetActive(views);
            ChaptersList.Panel.Alpha = chapters ? alpha : 0f;
            ViewsList.Panel.Alpha = views ? alpha : 0f;
        }

        void Place(VrInput input, bool snap, float dt)
        {
            if (!Pinned)
            {
                VrHand off = input.OffHand;
                main.AttachedTo = off.Side;
                if (off.Tracked)
                {
                    Vector3 head = input.Frame.HeadPosition;
                    Vector3 palm = off.State.Palm;
                    Vector3 toHeadFlat = head - palm;
                    toHeadFlat.y = 0f;
                    toHeadFlat = toHeadFlat.sqrMagnitude > 1e-6f ? toHeadFlat.normalized : Vector3.back;
                    // above the palm, a little toward the user, tilted to face them; the lower edge about 6 cm over the hand
                    Vector3 target = palm + Vector3.up * (0.045f + PanelH * 0.5f * MmToM) + toHeadFlat * 0.04f;
                    Vector3 pos = snap ? target : Vector3.SmoothDamp(anchor.position, target, ref vel, 0.07f, 3f, dt);
                    MovePanels(pos, head);
                }
            }
            else
            {
                main.AttachedTo = null;
            }

            // lists to the side of the main panel (in its own frame)
            float side = input.Dominant == Hand.Right ? -1f : 1f; // away from the off hand: toward the pointing hand's arm is crowded
            side = -side;
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

        void MovePanels(Vector3 pos, Vector3 head)
        {
            main.FaceFrom(pos, head, 0.9f);
            anchor.SetPositionAndRotation(main.transform.position, main.transform.rotation);
            main.transform.localPosition = Vector3.zero;
            main.transform.localRotation = Quaternion.identity;
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
            ["shown"] = Shown, ["alpha"] = Math.Round(alpha, 3), ["pinned"] = Pinned, ["chapters"] = ShowChapters, ["views"] = ShowViews,
            ["position"] = main != null ? new[] { main.transform.position.x, main.transform.position.y, main.transform.position.z } : null
        };
    }
}
