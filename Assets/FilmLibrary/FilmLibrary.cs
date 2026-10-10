using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>
/// The library screen (docs/FILM_LIBRARY.md, VIEWER_SPEC 2.3 / 6): what the viewer shows when it starts. One card per film of
/// StreamingAssets/library/library.json - a big poster, the title, the subtitle, the length and a Play button - and nothing else:
/// no other capture, take or test data is listed anywhere in the viewer (the HUD list and the digit keys of HeadMovement only
/// offer the library's captures; the developer commands hm_load / hm_film_show are untouched).
///
/// A film whose capture, direction or soundtrack does not exist yet is shown as "Processing" with a disabled Play button; the
/// screen re-reads the data and re-checks the files once a second, so the card turns playable by itself when they appear.
/// Play loads the film's capture, waits until the director is ready, then plays the directed film with the playback bar
/// (FilmPlaybackBar); Back (the bar's button, Esc) stops it and returns here.
///
/// Input: mouse / touch (UiPointer) and the keyboard (arrows choose, Enter or Space plays). The UI is a screen-space overlay
/// canvas drawn with the film's own procedural sprites (FilmUi); it is never created in a recording (Unity Recorder runs
/// frame-locked: Time.captureDeltaTime > 0) and a developer command that loads a capture or starts a film puts it away.
/// </summary>
[DefaultExecutionOrder(-80)]
[AddComponentMenu("")]
public class FilmLibrary : MonoBehaviour
{
    public enum Mode { Hidden, Library, Loading, Playing }

    public static FilmLibrary Instance { get; private set; }

    /// <summary>true while the library or the loading screen covers the viewer: the IMGUI HUD of HeadMovement and its keys stay quiet</summary>
    public static bool OwnsScreen => Instance != null && (Instance.mode == Mode.Library || Instance.mode == Mode.Loading);

    const int SortingOrder = 100;
    const float ReloadSeconds = 1f;
    const float LoadTimeoutSeconds = 90f;
    static readonly Color Accent = new(1f, 0.839f, 0.039f);
    static readonly Color Ink = new(0.039f, 0.047f, 0.067f);
    static readonly Color PanelColour = new(0.082f, 0.094f, 0.129f);
    static readonly Color Muted = new(0.63f, 0.68f, 0.77f);
    static readonly Color Amber = new(1f, 0.72f, 0.25f);

    public Mode State => mode;
    public LibraryData Data => data;
    public LibraryEntry Current => current;
    public string Error => error;
    public int Selected => selected;
    public IReadOnlyList<LibraryEntry> Films => data?.Films ?? new List<LibraryEntry>();

    Mode mode = Mode.Hidden;
    LibraryData data;
    LibraryEntry current;
    string error, signature;
    int selected, pressedCard = -1;
    float nextReload, loadStart;
    bool layoutDirty = true;
    int laidW, laidH;
    float u = 1f;
    string aspectUsed = "horizontal";

    Canvas canvas;
    RectTransform root, libRoot, loadRoot;
    Image background;
    Text header, kicker, footer, errorText, loadTitle, loadStatus, loadHint;
    Image loadBarBack, loadBarFill;
    readonly List<Card> cards = new();
    readonly Dictionary<string, (DateTime stamp, Texture2D tex)> thumbs = new();

    class Card
    {
        public LibraryEntry Entry;
        public RectTransform Root;
        public Image Outline, Panel, PosterBack, PlayBack, PlayIcon, Pill;
        public RawImage Poster;
        public Text Title, Subtitle, Meta, Status, PlayLabel, PosterLabel, PillLabel;
        public Rect CardRect, PosterRect, PlayRect;
        public float Hover;
    }

    // ------------------------------------------------------------------ start

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Application.isBatchMode || Instance != null) return;
        if (Object.FindAnyObjectByType<HeadMovement>() == null) return; // not the viewer scene
        if (!LibraryData.Exists) return;                                 // no library data: the plain developer viewer, as before
        new GameObject("Film Library").AddComponent<FilmLibrary>();
    }

    /// <summary>a recording is running (the Unity Recorder fixes the frame rate) or the recorder state machine is armed:
    /// no library UI may be drawn into the picture</summary>
    public static bool RecordingActive =>
        Time.captureDeltaTime > 0f || (FilmRecordingDriver.Instance != null && FilmRecordingDriver.Instance.OnFrame != null);

    void Awake()
    {
        Instance = this;
        data = LibraryData.Load();
        HeadMovement.CaptureLoading += OnCaptureLoading;
    }

    void Start()
    {
        if (RecordingActive || UnityEngine.XR.XRSettings.isDeviceActive) return; // desktop screen only; never inside a render
        Show();
    }

    void OnDestroy()
    {
        HeadMovement.CaptureLoading -= OnCaptureLoading;
        if (Instance == this) Instance = null;
        HeadMovement.LibraryOwnsScreen = false;
        foreach (var kv in thumbs)
        {
            if (kv.Value.tex != null) Destroy(kv.Value.tex);
        }
    }

    // ------------------------------------------------------------------ public actions (the UI, the CLI, a later VR panel)

    /// <summary>show the library screen (stops a running film)</summary>
    public void Show()
    {
        if (mode == Mode.Loading || mode == Mode.Playing || FilmDirector.Instance != null) StopFilm();
        EnsureCanvas();
        error = null;
        current = null;
        Reload(true);
        mode = Mode.Library;
        HeadMovement.LibraryOwnsScreen = true;
        canvas.enabled = true;
        libRoot.gameObject.SetActive(true);
        loadRoot.gameObject.SetActive(false);
        layoutDirty = true;
    }

    /// <summary>put the screen away without starting anything (developer takeover)</summary>
    public void Hide()
    {
        if (mode == Mode.Loading || mode == Mode.Playing) StopFilm();
        mode = Mode.Hidden;
        HeadMovement.LibraryOwnsScreen = false;
        if (canvas != null) canvas.enabled = false;
    }

    /// <summary>back to the library from a film (the bar's Back button, Esc)</summary>
    public void Back()
    {
        if (mode == Mode.Library) return;
        Show();
    }

    public LibraryEntry Find(string idOrCapture) =>
        Films.FirstOrDefault(f => string.Equals(f.Id, idOrCapture, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(f.Capture, idOrCapture, StringComparison.OrdinalIgnoreCase));

    /// <summary>play a film of the library by id (or capture folder); false (and a message on the screen) when it is not ready</summary>
    public bool Play(string idOrCapture)
    {
        LibraryEntry e = Find(idOrCapture);
        if (e == null)
        {
            error = $"no film '{idOrCapture}' in the library";
            return false;
        }

        return Play(e);
    }

    public bool Play(LibraryEntry e)
    {
        if (e == null) return false;
        e.Probe();
        if (!e.Ready)
        {
            error = $"{e.Title}: {e.WhyNot}";
            return false;
        }

        if (mode == Mode.Hidden) Show();
        if (mode == Mode.Loading || mode == Mode.Playing) StopFilm();
        error = null;
        current = e;
        mode = Mode.Loading;
        loadStart = Time.realtimeSinceStartup;
        aspectUsed = Screen.height > Screen.width ? "vertical" : "horizontal";
        HeadMovement.LibraryOwnsScreen = true;
        FilmDirector d = FilmDirector.Prepare(e.DirectionPath, aspectUsed);
        if (d == null)
        {
            Fail($"{e.Title}: {FilmDirector.LastStaticError ?? "the film could not be prepared"}");
            return false;
        }

        string mix = d.MixPath;
        if (mix != null && File.Exists(mix)) FilmSoundtrack.Preload(mix);
        ShowLoadingScreen();
        return true;
    }

    void StopFilm()
    {
        if (FilmDirector.Instance != null) FilmDirector.Instance.Stop();
        FilmPlaybackBar.Remove();
    }

    void Fail(string message)
    {
        Debug.LogWarning($"FilmLibrary: {message}");
        StopFilm();
        current = null;
        Show();
        error = message;
    }

    /// <summary>chapters of the library film whose direction file this is (null when no entry matches)</summary>
    public static LibraryEntry EntryForDirection(string directionPath)
    {
        if (string.IsNullOrEmpty(directionPath)) return null;
        LibraryData d = Instance != null ? Instance.data : LibraryData.Exists ? LibraryData.Load() : null;
        if (d == null) return null;
        string full = Path.GetFullPath(directionPath);
        return d.Films.FirstOrDefault(f => f.DirectionPath != null && string.Equals(Path.GetFullPath(f.DirectionPath), full, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ events from the rest of the viewer

    void OnCaptureLoading(string folder)
    {
        // our own film loads its capture: not a takeover
        if ((mode == Mode.Loading || mode == Mode.Playing) && current != null && string.Equals(folder, current.Capture, StringComparison.OrdinalIgnoreCase)) return;
        if (mode == Mode.Library || mode == Mode.Loading) Hide(); // a developer command (hm_load, hm_film_show, the recorder) wants the viewer
    }

    // ------------------------------------------------------------------ per frame

    void Update()
    {
        if (mode == Mode.Hidden) return;
        if (RecordingActive)
        {
            if (mode != Mode.Playing) Hide();
            return;
        }

        UiPointer.Poll();
        switch (mode)
        {
            case Mode.Library:
                // a film started by a command (hm_film_show) while the library is up: the library gives way
                if (FilmDirector.Instance != null && !FilmDirector.Instance.IsFinished)
                {
                    Hide();
                    return;
                }

                UpdateLibrary();
                break;
            case Mode.Loading:
                UpdateLoading();
                break;
            case Mode.Playing:
                if (FilmDirector.Instance == null)
                {
                    FilmPlaybackBar.Remove();
                    Show(); // the film was stopped from outside: back to the library
                    break;
                }

                FollowWindowAspect(FilmDirector.Instance);
                break;
        }
    }

    float aspectSince = -1f;

    /// <summary>the film is laid out for the window's aspect: a window turned from landscape to portrait (or back) for more than half a
    /// second switches the playing film to the other layout (the director re-lays out its captions and call-outs; nothing reloads)</summary>
    void FollowWindowAspect(FilmDirector d)
    {
        string want = Screen.height > Screen.width ? "vertical" : "horizontal";
        if (d.Aspect == want)
        {
            aspectSince = -1f;
            return;
        }

        if (aspectSince < 0f) aspectSince = Time.unscaledTime;
        if (Time.unscaledTime - aspectSince < 0.5f) return;
        aspectSince = -1f;
        aspectUsed = want;
        FilmDirector.Prepare(d.DirectionPath, want);
    }

    void UpdateLibrary()
    {
        if (Time.unscaledTime >= nextReload) Reload(false);
        if (layoutDirty || laidW != Screen.width || laidH != Screen.height) Layout();

        int hover = -1;
        if (UiPointer.Present)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i].CardRect.Contains(UiPointer.Pos)) hover = i;
            }
        }

        if (UiPointer.Moved && hover >= 0) selected = hover;
        if (UiPointer.Down) pressedCard = hover;
        if (UiPointer.Up)
        {
            if (pressedCard >= 0 && pressedCard == hover) Activate(pressedCard);
            pressedCard = -1;
        }

        if (cards.Count > 0)
        {
            if (UiPointer.Pressed(Key.LeftArrow) || UiPointer.Pressed(Key.UpArrow)) selected = (selected + cards.Count - 1) % cards.Count;
            if (UiPointer.Pressed(Key.RightArrow) || UiPointer.Pressed(Key.DownArrow) || UiPointer.Pressed(Key.Tab)) selected = (selected + 1) % cards.Count;
            if (UiPointer.Pressed(Key.Enter) || UiPointer.Pressed(Key.NumpadEnter) || UiPointer.Pressed(Key.Space)) Activate(selected);
        }

        selected = cards.Count == 0 ? 0 : Mathf.Clamp(selected, 0, cards.Count - 1);
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 2.4f);
        for (int i = 0; i < cards.Count; i++)
        {
            Card c = cards[i];
            c.Hover = Mathf.MoveTowards(c.Hover, i == selected ? 1f : 0f, Time.unscaledDeltaTime * 8f);
            FilmUi.SetAlpha(c.Outline, c.Hover * 0.95f);
            c.Panel.color = Color.Lerp(PanelColour, new Color(0.115f, 0.13f, 0.18f), c.Hover);
            if (!c.Entry.Ready)
            {
                FilmUi.SetAlpha(c.Pill, 0.78f + 0.2f * pulse);
                if (c.PosterLabel != null && c.PosterLabel.gameObject.activeSelf) FilmUi.SetAlpha(c.PosterLabel, 0.55f + 0.4f * pulse);
            }
            else
            {
                c.PlayBack.color = Color.Lerp(new Color(Accent.r * 0.82f, Accent.g * 0.82f, Accent.b * 0.82f), Accent, c.Hover);
            }
        }

        if (errorText != null)
        {
            bool on = !string.IsNullOrEmpty(error) || !string.IsNullOrEmpty(data?.Error);
            if (errorText.gameObject.activeSelf != on) errorText.gameObject.SetActive(on);
            if (on) errorText.text = error ?? data.Error;
        }
    }

    void Activate(int index)
    {
        if (index < 0 || index >= cards.Count) return;
        Card c = cards[index];
        selected = index;
        if (c.Entry.Ready) Play(c.Entry);
        else error = $"{c.Entry.Title}: {c.Entry.WhyNot}";
    }

    void UpdateLoading()
    {
        FilmDirector d = FilmDirector.Instance;
        float waited = Time.realtimeSinceStartup - loadStart;
        if (UiPointer.Pressed(Key.Escape))
        {
            Show();
            return;
        }

        if (d == null)
        {
            Fail($"{current?.Title}: the film director stopped while loading");
            return;
        }

        if (d.LastError != null)
        {
            Fail($"{current?.Title}: {d.LastError}");
            return;
        }

        FilmSoundtrack st = FilmSoundtrack.Current;
        bool audioReady = st == null || st.IsLoaded || st.Error != null;
        string phones = d.Sources != null ? d.Sources.Status : null;
        string status = d.IsReady
            ? (audioReady ? "starting" : "loading the soundtrack")
            : "loading the capture and its phone videos" + (string.IsNullOrEmpty(phones) || phones == "none" ? "" : $" ({phones})");
        if (loadStatus != null) loadStatus.text = status;
        AnimateLoading();
        if (layoutDirty || laidW != Screen.width || laidH != Screen.height) Layout();

        bool ready = d.IsReady && audioReady;
        if (!ready && waited < LoadTimeoutSeconds) return;
        if (!ready) Debug.LogWarning($"FilmLibrary: {current.Title} was not fully ready after {LoadTimeoutSeconds:0} s ({status}); starting anyway");
        BeginFilm(d);
    }

    void BeginFilm(FilmDirector prepared)
    {
        // FilmDirector.Show finds the prepared director (same direction), starts its clock at 0 and plays the soundtrack
        FilmDirector d = FilmDirector.Show(current.DirectionPath, aspectUsed, 0f);
        if (d == null)
        {
            Fail($"{current.Title}: {FilmDirector.LastStaticError ?? "the film could not start"}");
            return;
        }

        FilmPlaybackBar.Attach(d, current);
        mode = Mode.Playing;
        HeadMovement.LibraryOwnsScreen = false;
        libRoot.gameObject.SetActive(false);
        loadRoot.gameObject.SetActive(false);
        if (canvas != null) canvas.enabled = false;
    }

    // ------------------------------------------------------------------ data

    void Reload(bool force)
    {
        nextReload = Time.unscaledTime + ReloadSeconds;
        if (force || LibraryData.StampOf() != data.Stamp || data.Error != null && LibraryData.Exists) data = LibraryData.Load();
        else foreach (LibraryEntry f in data.Films) f.Probe();
        string sig = string.Join("|", data.Films.Select(f =>
            $"{f.Id}/{f.Ready}/{f.Title}/{f.Subtitle}/{f.ThumbnailPath}/{f.DurationS}/{f.Chapters.Count}/{(f.ThumbnailPath != null && File.Exists(f.ThumbnailPath) ? File.GetLastWriteTimeUtc(f.ThumbnailPath).Ticks : 0)}"));
        if (sig == signature && cards.Count == data.Films.Count) return;
        signature = sig;
        string keep = selected >= 0 && selected < cards.Count ? cards[selected].Entry.Id : null;
        BuildCards();
        int at = keep == null ? -1 : cards.FindIndex(c => c.Entry.Id == keep);
        selected = at >= 0 ? at : Mathf.Max(0, cards.FindIndex(c => c.Entry.Ready));
        layoutDirty = true;
    }

    // ------------------------------------------------------------------ UI building

    void EnsureCanvas()
    {
        if (canvas != null) return;
        GameObject go = new("Film library canvas", typeof(RectTransform));
        go.transform.SetParent(transform, false);
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        canvas.pixelPerfect = false;
        root = (RectTransform)go.transform;

        background = FilmUi.Box(root, "Background", null, Ink);
        RectTransform bg = background.rectTransform;
        bg.anchorMin = Vector2.zero;
        bg.anchorMax = Vector2.one;
        bg.offsetMin = bg.offsetMax = Vector2.zero;

        libRoot = Container(root, "Library");
        loadRoot = Container(root, "Loading");
        header = Label(libRoot, "Header", Color.white, TextAnchor.MiddleCenter, true, false);
        header.text = "Head Movement";
        kicker = Label(libRoot, "Kicker", Muted, TextAnchor.MiddleCenter, false, false);
        kicker.text = "Choose a film";
        footer = Label(libRoot, "Footer", new Color(Muted.r, Muted.g, Muted.b, 0.75f), TextAnchor.MiddleCenter, false, false);
        footer.text = "Click a film or press Enter to play      Arrow keys choose";
        errorText = Label(libRoot, "Message", Amber, TextAnchor.MiddleCenter, false, true);
        errorText.gameObject.SetActive(false);

        loadTitle = Label(loadRoot, "Loading title", Color.white, TextAnchor.MiddleCenter, true, true);
        loadStatus = Label(loadRoot, "Loading status", Muted, TextAnchor.MiddleCenter, false, true);
        loadHint = Label(loadRoot, "Loading hint", new Color(Muted.r, Muted.g, Muted.b, 0.7f), TextAnchor.MiddleCenter, false, false);
        loadHint.text = "Esc cancels";
        loadBarBack = FilmUi.Box(loadRoot, "Bar", FilmUi.Rounded, new Color(1f, 1f, 1f, 0.12f));
        loadBarFill = FilmUi.Box(loadRoot, "Bar fill", FilmUi.Rounded, Accent);
    }

    /// <summary>a group node at the canvas' lower-left corner with no size of its own (a new RectTransform is 100 x 100: its children, anchored at
    /// its lower-left corner, would sit 50 px off per level), so children are placed in screen pixels</summary>
    static RectTransform Container(Transform parent, string name)
    {
        RectTransform rt = FilmUi.Rect(parent, name);
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = Vector2.zero;
        rt.sizeDelta = Vector2.zero;
        rt.anchoredPosition = Vector2.zero;
        return rt;
    }

    static Text Label(Transform parent, string name, Color colour, TextAnchor anchor, bool bold, bool wrap)
    {
        Text t = FilmUi.Label(parent, name, 20, colour, anchor, bold);
        if (wrap)
        {
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
        }

        return t;
    }

    void BuildCards()
    {
        foreach (Card c in cards)
        {
            if (c.Root != null) Destroy(c.Root.gameObject);
        }

        cards.Clear();
        foreach (LibraryEntry e in data.Films)
        {
            Card c = new() { Entry = e };
            c.Root = Container(libRoot, $"Card {e.Id}");
            c.Outline = FilmUi.Box(c.Root, "Outline", FilmUi.Rounded, new Color(Accent.r, Accent.g, Accent.b, 0f));
            c.Panel = FilmUi.Box(c.Root, "Panel", FilmUi.Rounded, PanelColour);
            c.PosterBack = FilmUi.Box(c.Root, "Poster back", FilmUi.Rounded, new Color(0.02f, 0.025f, 0.04f));
            Texture2D tex = ThumbFor(e);
            GameObject pg = new("Poster", typeof(RectTransform));
            pg.transform.SetParent(c.Root, false);
            c.Poster = pg.AddComponent<RawImage>();
            c.Poster.raycastTarget = false;
            c.Poster.texture = tex;
            c.Poster.enabled = tex != null;
            c.Poster.color = e.Ready ? Color.white : new Color(1f, 1f, 1f, 0.38f);
            RectTransform prt = c.Poster.rectTransform;
            prt.anchorMin = prt.anchorMax = Vector2.zero;
            prt.pivot = new Vector2(0.5f, 0.5f);
            c.PosterLabel = Label(c.Root, "Poster label", new Color(1f, 1f, 1f, 0.85f), TextAnchor.MiddleCenter, true, true);
            c.PosterLabel.text = tex == null ? (e.Ready ? e.Title : "Processing") : "";
            c.PosterLabel.gameObject.SetActive(tex == null);
            c.Pill = FilmUi.Box(c.Root, "Processing pill", FilmUi.Rounded, new Color(0f, 0f, 0f, 0.8f));
            c.PillLabel = Label(c.Root, "Processing label", Amber, TextAnchor.MiddleCenter, true, false);
            c.PillLabel.text = "Processing";
            c.Pill.gameObject.SetActive(!e.Ready);
            c.PillLabel.gameObject.SetActive(!e.Ready);
            c.Title = Label(c.Root, "Title", Color.white, TextAnchor.UpperLeft, true, true);
            c.Title.text = e.Title ?? "";
            c.Subtitle = Label(c.Root, "Subtitle", Muted, TextAnchor.UpperLeft, false, true);
            c.Subtitle.text = e.Subtitle ?? "";
            c.Meta = Label(c.Root, "Meta", new Color(0.82f, 0.86f, 0.93f), TextAnchor.UpperLeft, false, true);
            c.Meta.text = MetaText(e);
            c.Status = Label(c.Root, "Status", Amber, TextAnchor.UpperLeft, false, true);
            c.Status.text = e.Ready ? "" : "This film is still being prepared. It will open here as soon as it is ready.";
            c.Status.gameObject.SetActive(!e.Ready);
            c.PlayBack = FilmUi.Box(c.Root, "Play button", FilmUi.Rounded, e.Ready ? Accent : new Color(1f, 1f, 1f, 0.09f));
            c.PlayIcon = FilmUi.Box(c.Root, "Play icon", FilmUi.Triangle, Ink);
            c.PlayIcon.gameObject.SetActive(e.Ready);
            c.PlayLabel = Label(c.Root, "Play label", e.Ready ? Ink : new Color(1f, 1f, 1f, 0.45f), TextAnchor.MiddleCenter, true, false);
            c.PlayLabel.text = e.Ready ? "Play" : "Processing";
            cards.Add(c);
        }
    }

    static string MetaText(LibraryEntry e)
    {
        List<string> parts = new();
        if (!string.IsNullOrEmpty(e.DurationText)) parts.Add(e.DurationText);
        if (e.Chapters.Count > 0) parts.Add(e.Chapters.Count == 1 ? "1 chapter" : $"{e.Chapters.Count} chapters");
        return string.Join("   ·   ", parts);
    }

    Texture2D ThumbFor(LibraryEntry e)
    {
        string p = e.ThumbnailPath;
        if (p == null || !File.Exists(p)) return null;
        DateTime stamp = File.GetLastWriteTimeUtc(p);
        if (thumbs.TryGetValue(p, out var hit) && hit.tex != null && hit.stamp == stamp) return hit.tex;
        if (hit.tex != null) Destroy(hit.tex);
        try
        {
            Texture2D t = new(2, 2, TextureFormat.RGBA32, true) { name = "library thumbnail", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            if (!t.LoadImage(File.ReadAllBytes(p), true))
            {
                Destroy(t);
                return null;
            }

            thumbs[p] = (stamp, t);
            return t;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"FilmLibrary: thumbnail {Path.GetFileName(p)} unreadable ({ex.Message})");
            return null;
        }
    }

    // ------------------------------------------------------------------ layout (screen pixels from the bottom-left; unit u = the 1920x1080 / 1080x1920 design)

    void Layout()
    {
        layoutDirty = false;
        float W = laidW = Screen.width, H = laidH = Screen.height;
        bool landscape = W >= H;
        u = landscape ? Mathf.Min(W / 1920f, H / 1080f) : Mathf.Min(W / 1080f, H / 1920f);
        FilmUi.Place(background.rectTransform, new Vector2(W * 0.5f, H * 0.5f), new Vector2(W, H));

        float margin = 48f * u, gap = 36f * u, headerH = (landscape ? 140f : 160f) * u, footerH = 56f * u;
        SetText(header, new Rect(0, H - 86f * u, W, 60f * u), 46f);
        SetText(kicker, new Rect(0, H - 128f * u, W, 34f * u), 24f);
        SetText(footer, new Rect(0, 8f * u, W, footerH * 0.6f), 20f);
        SetText(errorText, new Rect(margin, footerH + 4f * u, W - 2f * margin, 56f * u), 24f);

        int n = Mathf.Max(1, cards.Count);
        int cols = landscape ? n : 1, rows = landscape ? 1 : n;
        float areaW = W - 2f * margin, areaH = H - headerH - footerH - 12f * u - (string.IsNullOrEmpty(error) ? 0f : 40f * u);
        float cw = Mathf.Min((areaW - gap * (cols - 1)) / cols, 980f * u), ch = Mathf.Min((areaH - gap * (rows - 1)) / rows, 900f * u);
        float blockW = cw * cols + gap * (cols - 1), blockH = ch * rows + gap * (rows - 1);
        float x0 = (W - blockW) * 0.5f, yTop = H - headerH - (areaH - blockH) * 0.5f;
        for (int i = 0; i < cards.Count; i++)
        {
            int col = landscape ? i : 0, row = landscape ? 0 : i;
            LayoutCard(cards[i], new Rect(x0 + col * (cw + gap), yTop - (row + 1) * ch - row * gap, cw, ch));
        }

        // the loading screen
        SetText(loadTitle, new Rect(W * 0.1f, H * 0.5f + 24f * u, W * 0.8f, 110f * u), 40f);
        SetText(loadStatus, new Rect(W * 0.1f, H * 0.5f - 66f * u, W * 0.8f, 60f * u), 22f);
        SetText(loadHint, new Rect(0, 24f * u, W, 30f * u), 20f);
        float bw = Mathf.Min(W * 0.6f, 560f * u);
        FilmUi.Place(loadBarBack.rectTransform, new Vector2(W * 0.5f, H * 0.5f - 6f * u), new Vector2(bw, 8f * u));
        loadBarBack.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 4f * u);
        loadBarFill.pixelsPerUnitMultiplier = loadBarBack.pixelsPerUnitMultiplier;
        AnimateLoading();
    }

    void ShowLoadingScreen()
    {
        EnsureCanvas();
        canvas.enabled = true;
        libRoot.gameObject.SetActive(false);
        loadRoot.gameObject.SetActive(true);
        loadTitle.text = current?.Title ?? "";
        loadStatus.text = "loading";
        layoutDirty = true;
        Layout();
    }

    void AnimateLoading()
    {
        if (loadBarFill == null || loadBarBack == null) return;
        Vector2 c = loadBarBack.rectTransform.anchoredPosition, s = loadBarBack.rectTransform.sizeDelta;
        float fw = s.x * 0.28f;
        float k = Mathf.SmoothStep(0f, 1f, Mathf.PingPong(Time.unscaledTime * 0.7f, 1f));
        FilmUi.Place(loadBarFill.rectTransform, new Vector2(c.x - s.x * 0.5f + fw * 0.5f + k * (s.x - fw), c.y), new Vector2(fw, s.y));
    }

    void SetText(Text t, Rect r, float sizeU)
    {
        int fs = Mathf.Max(8, Mathf.RoundToInt(sizeU * u));
        if (t.fontSize != fs) t.fontSize = fs;
        FilmUi.Place(t.rectTransform, r.center, r.size);
    }

    void LayoutCard(Card c, Rect card)
    {
        c.CardRect = card;
        float pad = 22f * u;
        FilmUi.Place(c.Outline.rectTransform, card.center, card.size + Vector2.one * 8f * u);
        FilmUi.Place(c.Panel.rectTransform, card.center, card.size);
        c.Outline.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 18f * u);
        c.Panel.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 14f * u);
        c.PosterBack.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 10f * u);

        float ph = card.height - 2f * pad, pw = ph * 9f / 16f;
        if (pw > card.width * 0.56f)
        {
            pw = card.width * 0.56f;
            ph = pw * 16f / 9f;
        }

        Rect poster = new(card.x + pad, card.y + (card.height - ph) * 0.5f, pw, ph);
        c.PosterRect = poster;
        FilmUi.Place(c.PosterBack.rectTransform, poster.center, poster.size);
        Texture tex = c.Poster.texture;
        Vector2 fit = poster.size;
        if (tex != null && tex.height > 0)
        {
            float ta = tex.width / (float)tex.height, pa = poster.width / poster.height;
            fit = ta > pa ? new Vector2(poster.width, poster.width / ta) : new Vector2(poster.height * ta, poster.height);
        }

        FilmUi.Place(c.Poster.rectTransform, poster.center, fit);
        SetText(c.PosterLabel, new Rect(poster.x + 12f * u, poster.y + poster.height * 0.35f, poster.width - 24f * u, poster.height * 0.3f), 30f);
        Vector2 pill = new(150f * u, 40f * u);
        FilmUi.Place(c.Pill.rectTransform, new Vector2(poster.x + 14f * u + pill.x * 0.5f, poster.yMax - 14f * u - pill.y * 0.5f), pill);
        c.Pill.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 18f * u);
        SetText(c.PillLabel, new Rect(c.Pill.rectTransform.anchoredPosition.x - pill.x * 0.5f, c.Pill.rectTransform.anchoredPosition.y - pill.y * 0.5f, pill.x, pill.y), 22f);

        float ix = poster.xMax + pad * 1.3f, iw = card.xMax - pad - ix;
        float top = card.yMax - pad, bottom = card.y + pad;
        float titleH = 3f * 36f * 1.2f * u, subH = 5f * 24f * 1.25f * u, metaH = 34f * u, statusH = 3f * 22f * 1.25f * u;
        SetText(c.Title, new Rect(ix, top - titleH, iw, titleH), 36f);
        SetText(c.Subtitle, new Rect(ix, top - titleH - 12f * u - subH, iw, subH), 24f);
        SetText(c.Meta, new Rect(ix, bottom + 96f * u, iw, metaH), 24f);
        SetText(c.Status, new Rect(ix, bottom + 96f * u + metaH + 6f * u, iw, statusH), 22f);
        float bh = 72f * u, bwid = Mathf.Min(iw, 380f * u);
        Rect play = new(ix, bottom, bwid, bh);
        c.PlayRect = play;
        FilmUi.Place(c.PlayBack.rectTransform, play.center, play.size);
        c.PlayBack.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 22f * u);
        float isz = 26f * u;
        FilmUi.Place(c.PlayIcon.rectTransform, new Vector2(play.x + play.width * 0.5f - 52f * u + isz * 0.17f, play.center.y), new Vector2(isz, isz));
        SetText(c.PlayLabel, c.Entry.Ready ? new Rect(play.x + play.width * 0.5f - 20f * u, play.y, 120f * u, play.height) : play, c.Entry.Ready ? 30f : 26f);
        if (c.Entry.Ready) c.PlayLabel.alignment = TextAnchor.MiddleLeft;
    }

    // ------------------------------------------------------------------ state (hm_library)

    public Dictionary<string, object> StateDict()
    {
        Dictionary<string, object> s = new()
        {
            ["mode"] = mode.ToString().ToLowerInvariant(), ["ownsScreen"] = OwnsScreen, ["error"] = error ?? data?.Error,
            ["libraryFile"] = LibraryData.Exists, ["selected"] = selected, ["current"] = current?.Id,
            ["films"] = Films.Select(f => new Dictionary<string, object>
            {
                ["id"] = f.Id, ["title"] = f.Title, ["capture"] = f.Capture, ["ready"] = f.Ready, ["why"] = f.WhyNot,
                ["durationS"] = float.IsFinite(f.DurationS) ? f.DurationS : null, ["chapters"] = f.Chapters.Count,
                ["thumbnail"] = f.ThumbnailPath != null && File.Exists(f.ThumbnailPath), ["direction"] = f.DirectionPath != null && File.Exists(f.DirectionPath)
            }).ToList(),
            ["pickerCaptures"] = LibraryData.PickerCaptureNames(),
            ["screen"] = $"{Screen.width}x{Screen.height}", ["canvas"] = canvas != null && canvas.enabled
        };
        s["cards"] = cards.Select(c => new Dictionary<string, object>
        {
            ["id"] = c.Entry.Id, ["card"] = R(c.CardRect), ["poster"] = R(c.PosterRect), ["play"] = R(c.PlayRect), ["ready"] = c.Entry.Ready,
            ["posterTexture"] = c.Poster.texture != null
        }).ToList();
        return s;
    }

    static float[] R(Rect r) => new[] { Mathf.Round(r.x), Mathf.Round(r.y), Mathf.Round(r.width), Mathf.Round(r.height) };
}
