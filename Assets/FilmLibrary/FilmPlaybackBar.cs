using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// The playback bar of the directed film in the viewer (docs/FILM_LIBRARY.md, VIEWER_SPEC 7.1): play / pause, a scrubber across
/// the whole film (click or drag to seek) with chapter marks (a tick per chapter; hovering one names it, clicking it jumps to
/// its start), previous / next chapter, the current chapter's name, the time, a speed button and Back to the library.
/// It hides itself while the film plays and the pointer rests; it comes back on any pointer or key activity, while paused and at
/// the end.
///
/// Keys: Space play / pause, Left / Right 5 s (they repeat), [ and ] previous / next chapter, Home / End, Esc back to the library.
///
/// It exists only in a viewer session (FilmDirector.Show, i.e. the library's Play and hm_film_show --audio true): the film
/// RECORDER starts the director through Prepare / Begin and never gets a bar, and the bar also takes itself out of the picture
/// whenever the Unity Recorder is frame-locking the game (FilmLibrary.RecordingActive), so a render can never contain it.
/// All of its actions are public methods (SeekTo, TogglePlay, NextChapter, ...) so that a later VR panel can drive the same bar.
/// </summary>
[DefaultExecutionOrder(-60)]
[AddComponentMenu("")]
public class FilmPlaybackBar : MonoBehaviour
{
    public static FilmPlaybackBar Instance { get; private set; }

    /// <summary>tests and screenshots: keep the bar on screen</summary>
    public static bool ForceVisible;

    public const float SeekStepSeconds = 5f;
    public const float IdleHideSeconds = 3f;
    static readonly float[] Rates = { 0.5f, 1f, 1.5f, 2f };
    static readonly Color Accent = new(1f, 0.839f, 0.039f);

    public static FilmPlaybackBar Attach(FilmDirector director, LibraryEntry entry = null)
    {
        if (director == null) return null;
        if (Instance != null && Instance.director == director && (entry == null || Instance.entry == entry)) return Instance;
        Remove();
        GameObject go = new("Film Playback Bar");
        FilmPlaybackBar bar = go.AddComponent<FilmPlaybackBar>();
        bar.Init(director, entry ?? FilmLibrary.EntryForDirection(director.DirectionPath));
        return bar;
    }

    public static void Remove()
    {
        if (Instance != null) Destroy(Instance.gameObject);
        Instance = null;
    }

    FilmDirector director;
    LibraryEntry entry;
    readonly List<LibraryChapter> chapters = new();
    Canvas canvas;
    CanvasGroup group;
    RectTransform root;
    Image backdrop, trackBack, trackFill, hoverLine, head, headRing, tipBox;
    Text chapterLabel, timeLabel, tipText;
    readonly List<(Image shadow, Image tick)> ticks = new();
    readonly List<Btn> buttons = new();
    Btn backBtn, rewBtn, prevBtn, playBtn, nextBtn, fwdBtn, speedBtn;
    Image playTri, pauseA, pauseB;
    Sprite gradient;

    int laidW, laidH, rateIndex = 1;
    float u = 1f, lastActive, alpha = 1f, leftHeld = -1f, rightHeld = -1f, downX;
    Rect trackRect, trackHit, barZone;
    bool dragging, scrubbedSincePress, pointerSeen, closing;
    int snapTick = -1, hoverTick = -1;
    Btn hoverBtn, pressedBtn;
    float hoverTime = float.NaN;
    string tip = "";

    class Btn
    {
        public string Name;
        public Rect Rect;
        public Image Disc;
        public Text Label;
        public Action Click;
        public Func<bool> Enabled = () => true;
        public readonly List<Image> Glyphs = new();
        public float Hover;
        public bool Shown = true;
    }

    // ------------------------------------------------------------------ state

    public bool IsVisible => alpha > 0.02f && canvas != null && canvas.enabled;
    public float Alpha => alpha;
    public FilmDirector Director => director;
    public IReadOnlyList<LibraryChapter> Chapters => chapters;
    public bool Dragging => dragging;
    public Rect TrackRect => trackRect;

    public float Duration => director != null ? director.FilmDuration : 0f;
    public float FilmTime => director != null ? director.FilmTime : 0f;

    public int ChapterIndex => ChapterIndexAt(FilmTime);

    public int ChapterIndexAt(float t)
    {
        int idx = -1;
        for (int i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].StartS <= t + 1e-3f) idx = i;
        }

        return idx;
    }

    /// <summary>screen x of a film time on the scrubber</summary>
    public float XOf(float t) => trackRect.x + Mathf.Clamp01(Duration > 0f ? t / Duration : 0f) * trackRect.width;

    public float TimeAtX(float x) => Duration > 0f ? Mathf.Clamp01((x - trackRect.x) / Mathf.Max(1f, trackRect.width)) * Duration : 0f;

    // ------------------------------------------------------------------ actions (the UI, the keys, the CLI, a later VR panel)

    public void SeekTo(float t)
    {
        if (director == null) return;
        director.Seek(Mathf.Clamp(t, 0f, director.FilmDuration));
        Touch();
    }

    public void SeekBy(float seconds) => SeekTo(director.FilmTime + seconds);

    public void TogglePlay()
    {
        if (director == null) return;
        if (director.FilmTime >= director.FilmDuration - 0.05f)
        {
            director.Seek(0f); // play at the end starts over
            director.SetPaused(false);
        }
        else
        {
            director.SetPaused(!director.Paused);
        }

        Touch();
    }

    public void NextChapter()
    {
        if (director == null) return;
        float t = director.FilmTime;
        foreach (LibraryChapter c in chapters)
        {
            if (c.StartS > t + 0.25f)
            {
                SeekTo(c.StartS);
                return;
            }
        }

        Touch();
    }

    /// <summary>like a media player: a little way into a chapter it restarts it, near its start it goes to the one before</summary>
    public void PreviousChapter()
    {
        if (director == null) return;
        float t = director.FilmTime;
        int i = ChapterIndexAt(t);
        if (i < 0) SeekTo(0f);
        else if (t - chapters[i].StartS > 2f) SeekTo(chapters[i].StartS);
        else SeekTo(i > 0 ? chapters[i - 1].StartS : 0f);
    }

    public void CycleRate()
    {
        rateIndex = (rateIndex + 1) % Rates.Length;
        director.Rate = Rates[rateIndex];
        Touch();
    }

    public void Back()
    {
        closing = true;
        if (FilmLibrary.Instance != null) FilmLibrary.Instance.Back();
        else
        {
            if (director != null) director.Stop();
            Remove();
        }
    }

    /// <summary>pointer or key activity: show the bar (and keep it up for IdleHideSeconds)</summary>
    public void Touch() => lastActive = Time.unscaledTime;

    // ------------------------------------------------------------------ building

    void Init(FilmDirector d, LibraryEntry e)
    {
        Instance = this;
        director = d;
        entry = e;
        if (e != null)
        {
            e.NormaliseChapters(d.FilmDuration);
            chapters.AddRange(e.Chapters.Where(c => c.StartS < d.FilmDuration - 0.5f));
        }

        lastActive = Time.unscaledTime;
        GameObject cg = new("Film playback bar canvas", typeof(RectTransform));
        cg.transform.SetParent(transform, false);
        canvas = cg.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 90; // over the film's overlay (70), under the library (100)
        canvas.pixelPerfect = false;
        group = cg.AddComponent<CanvasGroup>();
        group.interactable = group.blocksRaycasts = false;
        root = (RectTransform)cg.transform;

        backdrop = FilmUi.Box(root, "Backdrop", GradientSprite(), Color.white);
        backdrop.type = Image.Type.Simple;
        trackBack = FilmUi.Box(root, "Track", FilmUi.Rounded, new Color(1f, 1f, 1f, 0.28f));
        trackFill = FilmUi.Box(root, "Played", FilmUi.Rounded, Accent);
        for (int i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].StartS <= 0.05f) continue; // no tick at the very start
            Image shadow = FilmUi.Box(root, $"Tick shadow {i}", null, new Color(0f, 0f, 0f, 0.55f));
            Image tick = FilmUi.Box(root, $"Chapter tick {i}", null, new Color(1f, 1f, 1f, 0.95f));
            ticks.Add((shadow, tick));
        }

        hoverLine = FilmUi.Box(root, "Hover", null, new Color(1f, 1f, 1f, 0.5f));
        headRing = FilmUi.Box(root, "Playhead ring", FilmUi.Disc, new Color(0f, 0f, 0f, 0.45f));
        head = FilmUi.Box(root, "Playhead", FilmUi.Disc, Color.white);
        chapterLabel = Txt("Chapter", Color.white, TextAnchor.MiddleLeft, true);
        timeLabel = Txt("Time", new Color(1f, 1f, 1f, 0.9f), TextAnchor.MiddleRight, false);
        tipBox = FilmUi.Box(root, "Tooltip", FilmUi.Rounded, new Color(0.02f, 0.02f, 0.03f, 0.9f));
        tipText = Txt("Tooltip text", Color.white, TextAnchor.MiddleCenter, true);

        backBtn = Pill("Back", "Library", () => Back());
        backBtn.Glyphs.Add(FilmUi.Box(backBtn.Disc.transform, "Chevron", FilmUi.Triangle, Color.white));
        rewBtn = Pill("Back 5 s", "-5 s", () => SeekBy(-SeekStepSeconds));
        prevBtn = Round("Previous chapter", () => PreviousChapter());
        playBtn = Round("Play pause", () => TogglePlay());
        nextBtn = Round("Next chapter", () => NextChapter());
        fwdBtn = Pill("Forward 5 s", "+5 s", () => SeekBy(SeekStepSeconds));
        speedBtn = Pill("Speed", "1x", () => CycleRate());
        prevBtn.Enabled = nextBtn.Enabled = () => chapters.Count > 0;
        // glyphs
        prevBtn.Disc.name = "Previous chapter";
        playTri = FilmUi.Box(playBtn.Disc.transform, "Play glyph", FilmUi.Triangle, new Color(0.05f, 0.05f, 0.07f));
        pauseA = FilmUi.Box(playBtn.Disc.transform, "Pause glyph A", null, new Color(0.05f, 0.05f, 0.07f));
        pauseB = FilmUi.Box(playBtn.Disc.transform, "Pause glyph B", null, new Color(0.05f, 0.05f, 0.07f));
        for (int k = 0; k < 2; k++)
        {
            Btn b = k == 0 ? prevBtn : nextBtn;
            b.Glyphs.Add(FilmUi.Box(b.Disc.transform, "Bar", null, Color.white));
            b.Glyphs.Add(FilmUi.Box(b.Disc.transform, "Triangle", FilmUi.Triangle, Color.white));
        }

        laidW = laidH = -1;
    }

    Sprite GradientSprite()
    {
        if (gradient != null) return gradient;
        const int n = 64;
        Texture2D t = new(1, n, TextureFormat.RGBA32, false) { name = "bar gradient", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        for (int y = 0; y < n; y++)
        {
            float k = y / (n - 1f); // 0 at the bottom
            float a = 0.86f * Mathf.Pow(1f - k, 1.6f);
            t.SetPixel(0, y, new Color(0.02f, 0.02f, 0.03f, a));
        }

        t.Apply(false, true);
        gradient = Sprite.Create(t, new Rect(0, 0, 1, n), new Vector2(0.5f, 0.5f), 100f);
        return gradient;
    }

    Text Txt(string name, Color colour, TextAnchor anchor, bool bold)
    {
        Text t = FilmUi.Label(root, name, 24, colour, anchor, bold);
        FilmUi.Shadow(t, new Color(0f, 0f, 0f, 0.7f), 2f);
        return t;
    }

    Btn Pill(string name, string label, Action click)
    {
        Btn b = new() { Name = name, Click = click };
        b.Disc = FilmUi.Box(root, name, FilmUi.Rounded, new Color(1f, 1f, 1f, 0.12f));
        b.Label = FilmUi.Label(b.Disc.transform, "Label", 24, Color.white, TextAnchor.MiddleCenter, true);
        b.Label.text = label;
        buttons.Add(b);
        return b;
    }

    Btn Round(string name, Action click)
    {
        Btn b = new() { Name = name, Click = click };
        b.Disc = FilmUi.Box(root, name, FilmUi.Disc, new Color(1f, 1f, 1f, 0f));
        buttons.Add(b);
        return b;
    }

    // ------------------------------------------------------------------ layout

    void Layout()
    {
        float W = laidW = Screen.width, H = laidH = Screen.height;
        bool landscape = W >= H;
        u = landscape ? Mathf.Min(W / 1920f, H / 1080f) : Mathf.Min(W / 1080f, H / 1920f);
        float m = 52f * u, trackY = 92f * u, trackH = 8f * u, btnY = 42f * u;
        FilmUi.Place(backdrop.rectTransform, new Vector2(W * 0.5f, 140f * u), new Vector2(W, 280f * u));
        barZone = new Rect(0f, 0f, W, 250f * u);
        trackRect = new Rect(m, trackY - trackH * 0.5f, W - 2f * m, trackH);
        trackHit = new Rect(m - 18f * u, trackY - 32f * u, trackRect.width + 36f * u, 64f * u);
        FilmUi.Place(trackBack.rectTransform, trackRect.center, trackRect.size);
        trackBack.pixelsPerUnitMultiplier = trackFill.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 4f * u);
        int fs = Mathf.Max(8, Mathf.RoundToInt(28f * u));
        chapterLabel.fontSize = fs;
        timeLabel.fontSize = Mathf.Max(8, Mathf.RoundToInt(26f * u));
        FilmUi.Place(chapterLabel.rectTransform, new Vector2(m + W * 0.3f, 146f * u), new Vector2(W * 0.6f, 44f * u));
        FilmUi.Place(timeLabel.rectTransform, new Vector2(W - m - 180f * u, 146f * u), new Vector2(360f * u, 44f * u));
        tipText.fontSize = Mathf.Max(8, Mathf.RoundToInt(24f * u));

        // buttons: Back left, speed right, the transport in the middle
        float cx = W * 0.5f, rr = 24f * u, big = 36f * u;
        SetPill(backBtn, new Rect(m, btnY - 25f * u, 172f * u, 50f * u));
        SetPill(speedBtn, new Rect(W - m - 92f * u, btnY - 25f * u, 92f * u, 50f * u));
        SetPill(rewBtn, new Rect(cx - 240f * u - 40f * u, btnY - 25f * u, 80f * u, 50f * u));
        SetPill(fwdBtn, new Rect(cx + 240f * u - 40f * u, btnY - 25f * u, 80f * u, 50f * u));
        SetRound(prevBtn, new Vector2(cx - 112f * u, btnY), rr);
        SetRound(playBtn, new Vector2(cx, btnY), big);
        SetRound(nextBtn, new Vector2(cx + 112f * u, btnY), rr);
        playBtn.Disc.color = Accent;
        backBtn.Label.rectTransform.anchoredPosition = Vector2.zero;
        Transform chev = backBtn.Disc.transform.Find("Chevron");
        if (chev != null)
        {
            RectTransform cr = (RectTransform)chev;
            float cs = 20f * u;
            cr.anchorMin = cr.anchorMax = new Vector2(0f, 0.5f);
            cr.pivot = new Vector2(0.5f, 0.5f);
            cr.sizeDelta = new Vector2(cs, cs);
            cr.anchoredPosition = new Vector2(26f * u - cs / 6f, 0f);
            cr.localEulerAngles = new Vector3(0f, 0f, 180f);
            backBtn.Label.rectTransform.anchoredPosition = new Vector2(16f * u, 0f);
        }

        // transport glyphs (children of the discs: positions relative to the disc's centre)
        PlaceGlyphs(prevBtn, true, rr);
        PlaceGlyphs(nextBtn, false, rr);
        UpdatePlayGlyph();
        PlaceTicks();
    }

    void SetPill(Btn b, Rect r)
    {
        b.Rect = r;
        FilmUi.Place(b.Disc.rectTransform, r.center, r.size);
        b.Disc.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 22f * u);
        RectTransform lr = b.Label.rectTransform;
        lr.anchorMin = Vector2.zero;
        lr.anchorMax = Vector2.one;
        lr.offsetMin = lr.offsetMax = Vector2.zero;
        b.Label.fontSize = Mathf.Max(8, Mathf.RoundToInt(24f * u));
    }

    void SetRound(Btn b, Vector2 centre, float radius)
    {
        b.Rect = new Rect(centre.x - radius, centre.y - radius, radius * 2f, radius * 2f);
        FilmUi.Place(b.Disc.rectTransform, centre, new Vector2(radius * 2f, radius * 2f));
    }

    static void GlyphAt(Image img, Vector2 local, Vector2 size, float z = 0f)
    {
        RectTransform r = img.rectTransform;
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
        r.pivot = new Vector2(0.5f, 0.5f);
        r.anchoredPosition = local;
        r.sizeDelta = size;
        r.localEulerAngles = new Vector3(0f, 0f, z);
    }

    void PlaceGlyphs(Btn b, bool previous, float radius)
    {
        Transform bar = b.Disc.transform.Find("Bar"), tri = b.Disc.transform.Find("Triangle");
        float s = radius * 0.95f;
        if (bar != null) GlyphAt(bar.GetComponent<Image>(), new Vector2((previous ? -0.46f : 0.46f) * s, 0f), new Vector2(Mathf.Max(2f, 0.16f * s), s * 0.95f));
        if (tri != null) GlyphAt(tri.GetComponent<Image>(), new Vector2((previous ? 0.12f : -0.12f) * s + (previous ? -1f : 1f) * s / 6f, 0f), new Vector2(s, s), previous ? 180f : 0f);
    }

    void UpdatePlayGlyph()
    {
        bool playing = !director.Paused && director.FilmTime < director.FilmDuration - 0.05f;
        float s = 30f * u;
        playTri.gameObject.SetActive(!playing);
        pauseA.gameObject.SetActive(playing);
        pauseB.gameObject.SetActive(playing);
        GlyphAt(playTri, new Vector2(s / 6f, 0f), new Vector2(s, s));
        GlyphAt(pauseA, new Vector2(-0.2f * s, 0f), new Vector2(0.17f * s, 0.8f * s));
        GlyphAt(pauseB, new Vector2(0.2f * s, 0f), new Vector2(0.17f * s, 0.8f * s));
    }

    void PlaceTicks()
    {
        int k = 0;
        for (int i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].StartS <= 0.05f) continue;
            if (k >= ticks.Count) break;
            float x = XOf(chapters[i].StartS);
            FilmUi.Place(ticks[k].shadow.rectTransform, new Vector2(x, trackRect.center.y), new Vector2(5f * u, 26f * u));
            FilmUi.Place(ticks[k].tick.rectTransform, new Vector2(x, trackRect.center.y), new Vector2(3f * u, 22f * u));
            k++;
        }
    }

    int TickChapter(int tickIndex)
    {
        int k = 0;
        for (int i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].StartS <= 0.05f) continue;
            if (k == tickIndex) return i;
            k++;
        }

        return -1;
    }

    /// <summary>index (into ticks) of the chapter tick within snap distance of screen x, or -1</summary>
    int TickNear(float x, float reach)
    {
        int best = -1;
        float bestD = reach;
        for (int k = 0; k < ticks.Count; k++)
        {
            int ci = TickChapter(k);
            if (ci < 0) continue;
            float d = Mathf.Abs(XOf(chapters[ci].StartS) - x);
            if (d <= bestD)
            {
                bestD = d;
                best = k;
            }
        }

        return best;
    }

    // ------------------------------------------------------------------ per frame

    void Update()
    {
        if (closing) return;
        if (director == null || FilmDirector.Instance != director)
        {
            Destroy(gameObject);
            if (Instance == this) Instance = null;
            return;
        }

        bool rec = FilmLibrary.RecordingActive;
        if (canvas.enabled == rec) canvas.enabled = !rec; // never in a render
        if (rec) return;
        if (laidW != Screen.width || laidH != Screen.height) Layout();

        UiPointer.Poll();
        HandleKeys();
        if (!closing) HandlePointer();
    }

    void HandleKeys()
    {
        if (UiPointer.Pressed(Key.Escape))
        {
            Back();
            return;
        }

        if (UiPointer.Pressed(Key.Space)) TogglePlay();
        if (UiPointer.Repeat(Key.RightArrow, ref rightHeld)) SeekBy(SeekStepSeconds);
        if (UiPointer.Repeat(Key.LeftArrow, ref leftHeld)) SeekBy(-SeekStepSeconds);
        if (UiPointer.Pressed(Key.RightBracket)) NextChapter();
        if (UiPointer.Pressed(Key.LeftBracket)) PreviousChapter();
        if (UiPointer.Pressed(Key.Home)) SeekTo(0f);
        if (UiPointer.Pressed(Key.End)) SeekTo(director.FilmDuration);
    }

    void HandlePointer()
    {
        if (UiPointer.Moved || UiPointer.Down || UiPointer.Up) Touch();
        if (UiPointer.Moved) pointerSeen = true;
        Vector2 p = UiPointer.Pos;
        bool live = alpha > 0.4f && UiPointer.Present;
        hoverBtn = null;
        if (live && !dragging)
        {
            foreach (Btn b in buttons)
            {
                if (b.Rect.Contains(p) && b.Enabled()) hoverBtn = b;
            }
        }

        bool overTrack = live && trackHit.Contains(p) && Duration > 0f;
        if (UiPointer.Down && live)
        {
            if (overTrack) BeginDrag(p);
            else if (hoverBtn != null) pressedBtn = hoverBtn;
        }

        if (dragging)
        {
            if (UiPointer.Held || UiPointer.Down)
            {
                if (Mathf.Abs(p.x - downX) > 8f * u) scrubbedSincePress = true;
                ScrubTo(p.x);
            }

            if (UiPointer.Up || !UiPointer.Held) EndDrag();
        }

        if (UiPointer.Up)
        {
            if (pressedBtn != null && pressedBtn == hoverBtn) pressedBtn.Click();
            pressedBtn = null;
        }

        // the hover tooltip: the time under the pointer and its chapter; near a tick it snaps to that chapter's start
        hoverTick = -1;
        hoverTime = float.NaN;
        if (overTrack || dragging)
        {
            hoverTick = dragging ? (scrubbedSincePress ? -1 : snapTick) : TickNear(p.x, 12f * u);
            int ci = hoverTick >= 0 ? TickChapter(hoverTick) : -1;
            hoverTime = ci >= 0 ? chapters[ci].StartS : TimeAtX(p.x);
            if (dragging && scrubbedSincePress) hoverTime = director.FilmTime;
        }
    }

    void BeginDrag(Vector2 p)
    {
        dragging = true;
        scrubbedSincePress = false;
        downX = p.x;
        snapTick = TickNear(p.x, 12f * u);
        director.BeginScrub();
        ScrubTo(p.x);
    }

    void ScrubTo(float x)
    {
        int ci = snapTick >= 0 && !scrubbedSincePress ? TickChapter(snapTick) : -1;
        director.Seek(ci >= 0 ? chapters[ci].StartS : TimeAtX(x));
    }

    void EndDrag()
    {
        dragging = false;
        snapTick = -1;
        director.EndScrub();
    }

    void LateUpdate()
    {
        if (closing || director == null || canvas == null || !canvas.enabled) return;
        if (laidW != Screen.width || laidH != Screen.height) Layout();
        float t = director.FilmTime, dur = Mathf.Max(0.01f, director.FilmDuration);
        bool want = ForceVisible || dragging || director.Paused || director.IsFinished ||
                    Time.unscaledTime - lastActive < IdleHideSeconds || (pointerSeen && UiPointer.Present && barZone.Contains(UiPointer.Pos));
        alpha = Mathf.MoveTowards(alpha, want ? 1f : 0f, Time.unscaledDeltaTime * 4f);
        group.alpha = Mathf.SmoothStep(0f, 1f, alpha);
        // the film's captions, move caption and call-out labels make room above the bar while it is up
        FilmOverlay overlay = director.Overlay;
        if (overlay != null)
        {
            overlay.BarHeight = 184f * u;
            overlay.BarShown = group.alpha;
        }

        if (alpha <= 0.001f) return;

        float x = XOf(t);
        float th = trackRect.height * (dragging || !float.IsNaN(hoverTime) ? 1.7f : 1f);
        FilmUi.Place(trackBack.rectTransform, trackRect.center, new Vector2(trackRect.width, th));
        FilmUi.Place(trackFill.rectTransform, new Vector2((trackRect.x + x) * 0.5f, trackRect.center.y), new Vector2(Mathf.Max(0.5f, x - trackRect.x), th));
        float hs = (dragging ? 26f : 20f) * u;
        FilmUi.Place(head.rectTransform, new Vector2(x, trackRect.center.y), new Vector2(hs, hs));
        FilmUi.Place(headRing.rectTransform, new Vector2(x, trackRect.center.y), new Vector2(hs + 6f * u, hs + 6f * u));

        // chapter ticks: the hovered one lights up in the accent colour
        for (int k = 0; k < ticks.Count; k++)
        {
            bool hot = k == hoverTick;
            ticks[k].tick.color = hot ? Accent : new Color(1f, 1f, 1f, 0.95f);
            Vector2 sz = new((hot ? 5f : 3f) * u, (hot ? 30f : 22f) * u);
            RectTransform tr = ticks[k].tick.rectTransform;
            if (tr.sizeDelta != sz) tr.sizeDelta = sz;
        }

        // texts
        int ci = ChapterIndexAt(t);
        string chapter = ci >= 0 ? $"{ci + 1}/{chapters.Count}   {chapters[ci].Title}" : "";
        if (chapterLabel.text != chapter) chapterLabel.text = chapter;
        string time = $"{LibraryData.Clock(t)} / {LibraryData.Clock(dur)}";
        if (timeLabel.text != time) timeLabel.text = time;
        string rate = Rates[rateIndex] == 1f ? "1x" : $"{Rates[rateIndex]:0.#}x";
        if (speedBtn.Label.text != rate) speedBtn.Label.text = rate;
        UpdatePlayGlyph();

        // button hover
        foreach (Btn b in buttons)
        {
            b.Hover = Mathf.MoveTowards(b.Hover, b == hoverBtn ? 1f : 0f, Time.unscaledDeltaTime * 10f);
            if (b == playBtn) b.Disc.color = Color.Lerp(Accent * 0.9f, Accent, b.Hover);
            else if (b == prevBtn || b == nextBtn) b.Disc.color = new Color(1f, 1f, 1f, 0.2f * b.Hover);
            else b.Disc.color = new Color(1f, 1f, 1f, 0.12f + 0.2f * b.Hover);
            bool enabled = b.Enabled();
            if (b.Shown != enabled)
            {
                b.Shown = enabled;
                if (b.Label != null) b.Label.color = enabled ? Color.white : new Color(1f, 1f, 1f, 0.35f);
                foreach (Image g in b.Glyphs) g.color = enabled ? Color.white : new Color(1f, 1f, 1f, 0.3f);
            }
        }

        // tooltip
        bool tipOn = !float.IsNaN(hoverTime) && alpha > 0.5f;
        tipBox.gameObject.SetActive(tipOn);
        tipText.gameObject.SetActive(tipOn);
        hoverLine.gameObject.SetActive(tipOn && !dragging);
        if (tipOn)
        {
            int hc = ChapterIndexAt(hoverTime);
            tip = hc >= 0 ? $"{LibraryData.Clock(hoverTime)}   {chapters[hc].Title}" : LibraryData.Clock(hoverTime);
            tipText.text = tip;
            float w = FilmUi.Width(tip, tipText.fontSize, FontStyle.Bold) + 36f * u, h = 44f * u;
            float tx = Mathf.Clamp(XOf(hoverTime), w * 0.5f + 8f * u, Screen.width - w * 0.5f - 8f * u);
            FilmUi.Place(tipBox.rectTransform, new Vector2(tx, trackRect.center.y + 52f * u), new Vector2(w, h));
            tipBox.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 12f * u);
            FilmUi.Place(tipText.rectTransform, new Vector2(tx, trackRect.center.y + 52f * u), new Vector2(w, h));
            FilmUi.Place(hoverLine.rectTransform, new Vector2(XOf(hoverTime), trackRect.center.y), new Vector2(2f * u, 22f * u));
        }
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (director != null && director.Overlay != null) director.Overlay.BarShown = 0f;
        if (dragging && director != null) director.EndScrub();
        if (gradient != null)
        {
            Destroy(gradient.texture);
            Destroy(gradient);
        }
    }

    // ------------------------------------------------------------------ state (hm_library)

    public Dictionary<string, object> StateDict()
    {
        Dictionary<string, object> s = new()
        {
            ["visible"] = IsVisible, ["alpha"] = Math.Round(alpha, 3), ["dragging"] = dragging, ["rate"] = Rates[rateIndex],
            ["chapter"] = ChapterIndex, ["chapterTitle"] = ChapterIndex >= 0 ? chapters[ChapterIndex].Title : null, ["chapters"] = chapters.Count,
            ["track"] = R(trackRect), ["trackHit"] = R(trackHit), ["tooltip"] = float.IsNaN(hoverTime) ? null : tip, ["hoverTick"] = hoverTick,
            ["ticks"] = chapters.Where(c => c.StartS > 0.05f).Select(c => new[] { c.StartS, XOf(c.StartS) }).ToList(),
            ["buttons"] = buttons.ToDictionary(b => b.Name, b => (object)new Dictionary<string, object> { ["rect"] = R(b.Rect), ["enabled"] = b.Enabled() }),
            ["screen"] = $"{Screen.width}x{Screen.height}"
        };
        return s;
    }

    static float[] R(Rect r) => new[] { Mathf.Round(r.x * 10f) / 10f, Mathf.Round(r.y * 10f) / 10f, Mathf.Round(r.width * 10f) / 10f, Mathf.Round(r.height * 10f) / 10f };
}
