using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The film's screen layer (one Screen Space Overlay canvas at scale 1: positions are screen pixels, laid out from
/// Screen and the aspect; the Recorder's Game view input captures it):
/// - the title card (VIEWER_SPEC 3.13): "Lead + Follow × Song →" (the text comes from the direction's title) with the
///   conditions text above the arrow and an empty product slot; at the end card the slot holds the dance's path through
///   the state machine. Names in the skeleton colours (lead red, follow white), "×" and the song in the accent yellow.
///   9:16: two rows under the top 14 % band; 16:9: one row at the top.
/// - narration captions (CaptionsOverlay: the current word on a yellow box);
/// - the zouk beat counter (8 eighths of the measure, accents by beat type, the current eighth pulsing);
/// - the move caption and graph inset (DanceHud, VIEWER_SPEC 3.12) re-placed for the film: 9:16 at the top under the
///   safe band (scaled to fit 1080 px), hidden under the title; 16:9 lower-left / lower-right raised above the
///   narration line. Restored on stop.
/// - a white flash on instant replays, a fade from black at the start and to black at the end.
/// Pure function of film time (+ dance time for the beat).
/// </summary>
[AddComponentMenu("")]
public class FilmOverlay : MonoBehaviour
{
    FilmDirector director;
    Canvas canvas;
    RectTransform root, captionRoot, titleRoot, beatRoot, cardRoot;
    CaptionsOverlay captions;
    TitleCard title;
    ChapterCards cards;
    BeatCounter beat;
    Image flash, fade;
    readonly HudPlacement hud = new();
    float unit = 1f;
    int laidW, laidH;
    string laidAspect;

    public RectTransform LabelRoot { get; private set; }

    /// <summary>labels and readouts stay above this screen y (px from the bottom): the caption block + a margin</summary>
    public float LabelFloor { get; private set; }

    /// <summary>labels stay below this screen y (px from the bottom): the top safe band, the title, the 9:16 HUD row</summary>
    public float LabelCeiling { get; private set; } = float.MaxValue;

    public CaptionsOverlay Captions => captions;
    public float TitleAlpha { get; private set; }

    /// <summary>the viewer's playback bar (FilmPlaybackBar): its height in px from the bottom and how much of it is shown (0..1). The
    /// captions, the move caption and the call-out labels make room above it while it is up. Both stay 0 in a recording (no bar there).</summary>
    public float BarHeight, BarShown;

    public void Init(FilmDirector d)
    {
        director = d;
        GameObject go = new("Film overlay", typeof(RectTransform));
        go.transform.SetParent(transform, false);
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 70; // over the dance HUD (50)
        canvas.pixelPerfect = false;
        root = (RectTransform)go.transform;

        LabelRoot = Stretch("Call-out labels");
        captionRoot = Stretch("Captions");
        titleRoot = Stretch("Title card");
        beatRoot = Stretch("Beat counter");
        cardRoot = Stretch("Chapter cards");
        captions = captionRoot.gameObject.AddComponent<CaptionsOverlay>();
        captions.Init(captionRoot, d.Direction.Narration?.ChunksFor(d.Aspect), d.Direction.CaptionStyle, d.AspectInfo, d.Direction.Dir);
        title = new TitleCard(titleRoot, d.Direction, Accent(d.Direction));
        cards = new ChapterCards(cardRoot, d.Direction, Accent(d.Direction));
        beat = new BeatCounter(beatRoot);
        flash = FilmUi.Box(root, "Replay flash", null, new Color(1f, 1f, 1f, 0f));
        fade = FilmUi.Box(root, "Fade", null, new Color(0f, 0f, 0f, 0f));
        foreach (Image i in new[] { flash, fade })
        {
            RectTransform rt = i.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            i.enabled = false;
        }
    }

    RectTransform Stretch(string name)
    {
        RectTransform rt = FilmUi.Rect(root, name);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return rt;
    }

    static Color Accent(FilmDirection d)
    {
        string hex = d.Root["captions"]?["current_word"]?.Value<string>("highlight");
        return FilmDirection.Hex(hex, new Color(1f, 0.839f, 0.039f));
    }

    public void OnAspectChanged()
    {
        captions.Init(captionRoot, director.Direction.Narration?.ChunksFor(director.Aspect), director.Direction.CaptionStyle, director.AspectInfo, director.Direction.Dir);
        laidAspect = null;
    }

    public void Restore()
    {
        hud.Restore();
        SkeletonLegend.TopOverride = -1f;
        SkeletonLegend.Scale = 1f;
    }

    public void Tick(float film, float dance)
    {
        bool vertical = director.Vertical;
        float W = Screen.width, H = Screen.height;
        unit = vertical ? W / 1080f : H / 1080f;
        FilmAspect aspect = director.AspectInfo;
        if (laidW != Screen.width || laidH != Screen.height || laidAspect != director.Aspect)
        {
            laidW = Screen.width;
            laidH = Screen.height;
            laidAspect = director.Aspect;
            title.Layout(vertical, W, H, unit, aspect);
            beat.Layout(vertical, W, H, unit, aspect);
        }

        // captions (word karaoke on the film clock)
        captions.SetTime(film);
        float fontPx = aspect.CaptionFontPx * unit;
        float lineH = fontPx * 1.24f, padY = 14f * unit;
        float captionBottom = vertical ? (1f - aspect.CaptionBottomY) * H : (1f - aspect.CaptionBaselineY) * H - 0.32f * fontPx - padY;
        float barLift = Mathf.Max(0f, BarHeight - captionBottom) * BarShown;
        if (Mathf.Abs(captionRoot.anchoredPosition.y - barLift) > 0.01f) captionRoot.anchoredPosition = new Vector2(0f, barLift);
        float captionTop = vertical
            ? (1f - aspect.CaptionBottomY) * H + 2f * lineH + 2f * padY
            : (1f - aspect.CaptionBaselineY) * H - 0.32f * fontPx - padY + lineH + 2f * padY;
        captionTop += barLift;
        if (captions.CurrentChunk >= 0) captionTop = Mathf.Max(captionTop, captions.BlockRect.yMax + barLift);
        LabelFloor = captionTop + 14f * unit;

        // title card
        float titleAlpha = title.Alpha(film);
        TitleAlpha = titleAlpha;
        title.Show(film, titleAlpha, director);
        // chapter cards (a segment's chapter_card): a pill under the top safe band; the HUD and the call-out labels make room like for the title
        cards.Tick(film, vertical, W, H, unit, aspect);
        float cardAlpha = cards.Alpha;

        // the dance HUD (move caption, graph inset): hidden under the 9:16 title
        // (gone by the time the title is a third visible: the fading title never overlaps the HUD boxes)
        float hudAlpha = vertical ? 1f - Mathf.Clamp01(Mathf.Max(titleAlpha, cardAlpha) * 3.4f) : 1f;
        float lift = captionTop + 18f * unit;
        hud.Apply(vertical, W, H, unit, aspect.SafeTop, lift, hudAlpha);
        // the physics legend ("Skeleton load - ESTIMATED") sits under the HUD row in 9:16, below the top 14 % platform band
        SkeletonLegend.TopOverride = vertical ? Mathf.Max(hud.BottomFromTop + 10f * unit, aspect.SafeTop * H + 8f * unit) : -1f;
        // a chapter / title card sits in the same band. The legend used to step below the card while it showed and jump back up when it faded (the audit:
        // it "jumped over the dancers' heads" at 292-297 s): it now always sits under the card's slot (a constant: the top band, the card's height and a gap),
        // so it never moves, and the physics shot frames the couple below it (camera `fit_band`)
        if (vertical)
        {
            float cardSlotBottom = aspect.SafeTop * H + 14f * unit + 96f * unit + 10f * unit;
            SkeletonLegend.TopOverride = Mathf.Max(SkeletonLegend.TopOverride, cardSlotBottom);
            if (Mathf.Max(cardAlpha, titleAlpha) > 0.05f) SkeletonLegend.TopOverride = Mathf.Max(SkeletonLegend.TopOverride, Mathf.Max(cards.BottomFromTop, title.BottomFromTop) + 10f * unit);
        }

        // 9:16 enlarges the legend: its 11 px text on a 1080-wide frame cannot be read on a phone (1.5x was still about 16 px)
        SkeletonLegend.Scale = vertical ? Mathf.Max(1f, 1.85f * unit) : 1f;
        // 16:9: the move caption (lower left) and the graph inset (lower right) are boxes the call-out labels keep out of
        // (9:16: they sit under the top band, which LabelCeiling already excludes)
        List<Rect> obstacles = director.Annotations.Obstacles;
        obstacles.Clear();
        if (!vertical && hudAlpha > 0.05f)
        {
            if (hud.MoveRect.width > 1f) obstacles.Add(hud.MoveRect);
            if (hud.InsetRect.width > 1f) obstacles.Add(hud.InsetRect);
        }
        float ceiling = (1f - (vertical ? aspect.SafeTop : Mathf.Max(0.04f, aspect.SafeTop))) * H;
        if (vertical && hud.Visible && hudAlpha > 0.05f) ceiling = Mathf.Min(ceiling, H - hud.BottomFromTop - 10f * unit);
        if (titleAlpha > 0.05f) ceiling = Mathf.Min(ceiling, H - title.BottomFromTop - 10f * unit);
        if (cardAlpha > 0.05f) ceiling = Mathf.Min(ceiling, H - cards.BottomFromTop - 10f * unit);
        LabelCeiling = ceiling;

        // beat counter
        int si = director.SegmentIndex;
        FilmDirector.SegLayers l = director.LayersOf(si);
        float beatAlpha = 0f;
        if (l != null && l.BeatCounter)
        {
            FilmSegment s = director.Segment;
            FilmDirector.SegLayers prev = director.LayersOf(si - 1);
            beatAlpha = prev != null && prev.BeatCounter ? 1f : Mathf.SmoothStep(0f, 1f, (film - s.F0) / 0.5f);
        }

        HeadMovement hm = HeadMovement.Instance;
        beat.Show(beatAlpha, hm != null ? hm.Beats : null, hm?.Timeline != null ? dance + hm.Timeline.TimeToAudio : float.NaN);

        // speed + POV pills (into the call-out readout stack)
        SpeedPill(film);
        FilmCamera fc = director.FilmCam;
        if (fc?.PovCamera != null && fc.PovVideoOpacity > 0.05f)
        {
            string text = director.Sources.VideoShowing ? $"phone {fc.PovCamera} · the original video" : $"phone {fc.PovCamera} · its point of view";
            director.Annotations.Extra.Add(("pov", text, FilmCameraSource.ColourOf(fc.PovCamera), Mathf.Clamp01(fc.PovVideoOpacity * 1.5f)));
        }

        // replay flash, fades
        float flashA = 0f;
        FilmSegment seg = director.Segment;
        if (seg != null && seg.Replay && film - seg.F0 < 0.3f && seg.Index > 0) flashA = 0.55f * (1f - (film - seg.F0) / 0.3f);
        SetImage(flash, flashA);
        float fadeA = Mathf.Max(1f - Mathf.Clamp01(film / 0.5f), Mathf.Clamp01((film - (director.FilmDuration - 0.9f)) / 0.9f));
        SetImage(fade, fadeA);
    }

    void SpeedPill(float film)
    {
        FilmSegment s = director.Segment;
        if (s == null) return;
        string text = PillText(s);
        if (text == null) return;
        FilmSegment prev = s.Index > 0 ? director.Direction.Segments[s.Index - 1] : null;
        bool continues = prev != null && PillText(prev) != null && Mathf.Abs(prev.D1 - s.D0) <= FilmClock.CutJump;
        float a = continues ? 1f : Mathf.SmoothStep(0f, 1f, (film - s.F0) / 0.35f);
        FilmSegment next = s.Index + 1 < director.Direction.Segments.Count ? director.Direction.Segments[s.Index + 1] : null;
        if (next == null || PillText(next) == null) a = Mathf.Min(a, Mathf.SmoothStep(0f, 1f, (s.F1 - film) / 0.35f));
        director.Annotations.Extra.Add(("speed", text, new Color(1f, 0.84f, 0.04f), a));
    }

    static string PillText(FilmSegment s)
    {
        if (s.Mode == "end_card") return null;
        bool hold = s.Hold || (s.Duration > 0 && Mathf.Abs(s.D1 - s.D0) < 1e-4f);
        string replay = s.Replay ? "replay · " : "";
        if (hold) return replay + "freeze frame";
        if (s.Ramp) return null; // ramps back to real time: no pill
        if (s.S0 < 0.95f) return $"{replay}slow motion {s.S0.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}×";
        return s.Replay ? "replay" : null;
    }

    static void SetImage(Image i, float a)
    {
        bool on = a > 0.002f;
        if (i.enabled != on) i.enabled = on;
        if (on) FilmUi.SetAlpha(i, a);
    }

    public Dictionary<string, object> State() => new()
    {
        ["caption"] = captions.CurrentText,
        ["word"] = captions.CurrentWord,
        ["captionLines"] = captions.Lines,
        ["captionBlock"] = R(captions.BlockRect),
        ["highlight"] = R(captions.HighlightRect),
        ["captionFontPx"] = Math.Round(captions.FontPx, 1),
        ["title"] = Math.Round(TitleAlpha, 3),
        ["titleText"] = title.Text,
        ["chapterCard"] = cards.CurrentTitle,
        ["chapterCardAlpha"] = Math.Round(cards.Alpha, 3),
        ["emojiWords"] = captions.IconWords,
        ["captionSpeaker"] = captions.CurrentSpeaker,
        ["beatCounter"] = beat.Visible,
        ["labelFloor"] = Mathf.Round(LabelFloor),
        ["labelCeiling"] = Mathf.Round(LabelCeiling),
        ["hud"] = hud.State()
    };

    static float[] R(Rect r) => new[] { Mathf.Round(r.x), Mathf.Round(r.y), Mathf.Round(r.width), Mathf.Round(r.height) };

    void OnDestroy() => Restore();

    // ================================================================== title card

    class TitleCard
    {
        readonly RectTransform root;
        readonly CanvasGroup group;
        readonly Text lead, plus, follow, times, song, conditions, slotMark;
        readonly Image arrowBar, arrowHead, slotOuter, slotInner;
        readonly RectTransform productRoot;
        readonly List<(float f0, float f1, float fadeIn, float fadeOut, bool product)> windows = new();
        GraphInset product;
        readonly Color accent, leadColour;
        public string Text { get; }
        public float BottomFromTop { get; private set; }
        bool vertical;
        float unit;
        Vector2 slotSize, slotCentre;

        public TitleCard(RectTransform parent, FilmDirection d, Color accentColour)
        {
            accent = accentColour;
            leadColour = FilmDirection.Hex(d.Root["style"]?["annotation_colours"]?.Value<string>("lead"), new Color(1f, 0.23f, 0.19f));
            root = FilmUi.Rect(parent, "Reaction title");
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = root.offsetMax = Vector2.zero;
            group = root.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = group.blocksRaycasts = false;

            string text = null, cond = null;
            foreach (FilmSegment s in d.Segments)
            {
                if (s.TitleJson == null) continue;
                text ??= s.TitleJson.Value<string>("text");
                cond ??= s.TitleJson.Value<string>("conditions");
                float f0 = s.F0 + FilmDirection.F(s.TitleJson["film_in"], 0f);
                float f1 = s.TitleJson["film_out"] != null ? s.F0 + FilmDirection.F(s.TitleJson["film_out"], s.Duration) : s.F1;
                bool prod = s.TitleJson["product"] != null;
                windows.Add((f0, Mathf.Min(f1, s.F1), prod ? 0.8f : 0.6f, s.Index == d.Segments.Count - 1 ? 0f : 0.6f, prod));
            }

            text = Demojibake(text ?? d.Title ?? "");
            cond = Demojibake(cond ?? "");
            Text = text;
            // "Lead + Follow × Song"
            string names = text, songName = "";
            int x = text.IndexOf('×');
            if (x < 0) x = text.IndexOf(" x ", StringComparison.OrdinalIgnoreCase);
            if (x >= 0)
            {
                names = text.Substring(0, x).Trim();
                songName = text.Substring(x + 1).Trim().TrimStart('x', ' ').Trim();
            }

            string[] pair = names.Split('+');
            string leadName = pair[0].Trim(), followName = pair.Length > 1 ? pair[1].Trim() : "";
            lead = Lbl("Lead", leadName, leadColour);
            plus = Lbl("Plus", "+", Color.white);
            follow = Lbl("Follow", followName, Color.white);
            times = Lbl("Times", "×", accent);
            song = Lbl("Song", songName, accent);
            conditions = Lbl("Conditions", cond, new Color(0.85f, 0.88f, 0.92f));
            conditions.fontStyle = FontStyle.Normal;
            arrowBar = FilmUi.Box(root, "Arrow", null, Color.white);
            arrowHead = FilmUi.Box(root, "Arrowhead", FilmUi.Triangle, Color.white);
            slotOuter = FilmUi.Box(root, "Product slot", FilmUi.Rounded, new Color(1f, 1f, 1f, 0.85f));
            slotInner = FilmUi.Box(root, "Product slot inner", FilmUi.Rounded, new Color(0.03f, 0.03f, 0.05f, 0.75f));
            productRoot = FilmUi.Rect(root, "Product");
            // the empty product slot reads as a deliberate blank (a hollow frame with a question mark) until the dance's
            // fingerprint lands in it at the end card
            slotMark = Lbl("Slot mark", "?", accent);
        }

        /// <summary>a UTF-8 string that was decoded as cp1252 once ("Ã—" for "×", "Ãª" for "ê"): decode it back</summary>
        static string Demojibake(string s)
        {
            if (string.IsNullOrEmpty(s) || (s.IndexOf('Ã') < 0 && s.IndexOf('Â') < 0)) return s;
            try
            {
                System.Text.Encoding cp = System.Text.Encoding.GetEncoding(1252);
                string r = System.Text.Encoding.UTF8.GetString(cp.GetBytes(s));
                return r.Contains('�') ? s : r;
            }
            catch
            {
                return s;
            }
        }

        Text Lbl(string name, string text, Color c)
        {
            Text t = FilmUi.Label(root, name, 60, c, TextAnchor.MiddleLeft, true);
            t.text = text;
            FilmUi.Shadow(t, new Color(0f, 0f, 0f, 0.65f), 3f);
            return t;
        }

        public float Alpha(float film)
        {
            float a = 0f;
            foreach ((float f0, float f1, float fi, float fo, bool _) in windows)
            {
                if (film < f0 || film > f1) continue;
                float k = fi > 0 ? Mathf.SmoothStep(0f, 1f, (film - f0) / fi) : 1f;
                if (fo > 0) k = Mathf.Min(k, Mathf.SmoothStep(0f, 1f, (f1 - film) / fo));
                a = Mathf.Max(a, k);
            }

            return a;
        }

        bool ProductAt(float film) => windows.Any(w => w.product && film >= w.f0 && film <= w.f1);

        public void Layout(bool v, float W, float H, float u, FilmAspect aspect)
        {
            vertical = v;
            unit = u;
            int big = Mathf.RoundToInt((v ? 76f : 62f) * u), mid = Mathf.RoundToInt((v ? 60f : 62f) * u), small = Mathf.RoundToInt((v ? 30f : 26f) * u);
            foreach (Text t in new[] { lead, plus, follow }) t.fontSize = big;
            foreach (Text t in new[] { times, song }) t.fontSize = v ? mid : big;
            conditions.fontSize = small;
            float gap = 0.32f * big;
            float wLead = FilmUi.Width(lead.text, big, FontStyle.Bold), wPlus = FilmUi.Width("+", big, FontStyle.Bold);
            float wFollow = FilmUi.Width(follow.text, big, FontStyle.Bold);
            int sz = times.fontSize;
            float wTimes = FilmUi.Width("×", sz, FontStyle.Bold), wSong = FilmUi.Width(song.text, sz, FontStyle.Bold);
            float arrowLen = (v ? 120f : 130f) * u;
            // 16:9: the conditions text sits above the arrow, so the arrow is as long as the text (+ a little): the text
            // never reaches the song or the product slot. 9:16 puts it on its own line under the second row instead.
            if (!v) arrowLen = Mathf.Max(arrowLen, FilmUi.Width(conditions.text, small, FontStyle.Normal) + 0.9f * gap);
            slotSize = new Vector2(v ? 96f : 92f, v ? 96f : 92f) * u;
            float topY = H * (1f - Mathf.Max(aspect.SafeTop, v ? 0.14f : 0.05f)) - 10f * u; // top edge of the block (px from the bottom)
            float row1, row2;
            if (v)
            {
                // row 1: Lead + Follow; row 2: × Song -> [ ]
                row1 = topY - big * 0.62f;
                float x = W * 0.5f - (wLead + gap + wPlus + gap + wFollow) * 0.5f;
                Place(lead, x, row1, wLead, big);
                x += wLead + gap;
                Place(plus, x, row1, wPlus, big);
                x += wPlus + gap;
                Place(follow, x, row1, wFollow, big);
                row2 = row1 - big * 0.62f - mid * 0.95f - small * 0.6f;
                float total = wTimes + gap + wSong + gap + arrowLen + gap + slotSize.x;
                x = W * 0.5f - total * 0.5f;
                Place(times, x, row2, wTimes, sz);
                x += wTimes + gap;
                Place(song, x, row2, wSong, sz);
                x += wSong + gap;
                ArrowAt(x, row2, arrowLen, sz, small);
                x += arrowLen + gap;
                slotCentre = new Vector2(x + slotSize.x * 0.5f, row2);
                // row 3: the conditions, centred under the song / arrow / slot row
                float row3 = row2 - Mathf.Max(slotSize.y * 0.5f, sz * 0.6f) - small * 0.9f;
                float wCond = FilmUi.Width(conditions.text, small, FontStyle.Normal);
                Place(conditions, W * 0.5f - wCond * 0.5f, row3, wCond, small);
                BottomFromTop = H - (row3 - small * 0.8f);
            }
            else
            {
                row1 = topY - big * 0.62f - small * 1.1f;
                row2 = row1;
                float total = wLead + gap + wPlus + gap + wFollow + gap + wTimes + gap + wSong + gap + arrowLen + gap + slotSize.x;
                float x = W * 0.5f - total * 0.5f;
                Place(lead, x, row1, wLead, big);
                x += wLead + gap;
                Place(plus, x, row1, wPlus, big);
                x += wPlus + gap;
                Place(follow, x, row1, wFollow, big);
                x += wFollow + gap;
                Place(times, x, row1, wTimes, sz);
                x += wTimes + gap;
                Place(song, x, row1, wSong, sz);
                x += wSong + gap;
                ArrowAt(x, row1, arrowLen, sz, small);
                x += arrowLen + gap;
                slotCentre = new Vector2(x + slotSize.x * 0.5f, row1);
                BottomFromTop = H - (row1 - Mathf.Max(slotSize.y * 0.5f, big * 0.6f));
            }

            FilmUi.Place(slotOuter.rectTransform, slotCentre, slotSize);
            FilmUi.Place(slotInner.rectTransform, slotCentre, slotSize - Vector2.one * 6f * u);
            slotOuter.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 14f * u);
            slotInner.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 11f * u);
            FilmUi.Place(productRoot, slotCentre, slotSize - Vector2.one * 14f * u);
            slotMark.fontSize = Mathf.RoundToInt(slotSize.y * 0.62f);
            FilmUi.Place(slotMark.rectTransform, slotCentre, slotSize);
        }

        void ArrowAt(float x, float y, float len, int size, int small)
        {
            float th = Mathf.Max(3f, 0.07f * size);
            float head = 0.42f * size;
            FilmUi.Place(arrowBar.rectTransform, new Vector2(x + (len - head * 0.6f) * 0.5f, y), new Vector2(len - head * 0.6f, th));
            FilmUi.Place(arrowHead.rectTransform, new Vector2(x + len - head * 0.5f, y), new Vector2(head, head));
            if (vertical) return; // 9:16: placed under the row by Layout
            float wc = FilmUi.Width(conditions.text, small, FontStyle.Normal);
            Place(conditions, x + len * 0.5f - wc * 0.5f, y + small * 1.05f, wc, small);
        }

        static void Place(Text t, float x, float centreY, float w, int size)
        {
            FilmUi.Place(t.rectTransform, new Vector2(x + w * 0.5f, centreY), new Vector2(w + 8f, size * 1.3f));
            t.alignment = TextAnchor.MiddleCenter;
        }

        public void Show(float film, float alpha, FilmDirector d)
        {
            if (Mathf.Abs(group.alpha - alpha) > 0.003f) group.alpha = alpha;
            bool on = alpha > 0.002f;
            if (root.gameObject.activeSelf != on) root.gameObject.SetActive(on);
            if (!on) return;
            bool prod = ProductAt(film);
            if (prod && product == null)
            {
                DanceGraphLayer g = DanceLayers.Instance != null ? DanceLayers.Instance.Graph : null;
                if (g != null && g.Data != null && g.Data.Nodes.Count > 0)
                {
                    GameObject go = new("Product graph", typeof(RectTransform));
                    go.transform.SetParent(productRoot, false);
                    go.AddComponent<CanvasRenderer>();
                    product = go.AddComponent<GraphInset>();
                    product.raycastTarget = false;
                    RectTransform rt = product.rectTransform;
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = rt.offsetMax = Vector2.zero;
                    product.Init(g.Data);
                    List<(int, int, bool)> path = new();
                    List<int> visited = new();
                    List<DanceGraphData.Step> steps = g.Data.Steps;
                    for (int k = 0; k < steps.Count; k++)
                    {
                        if (steps[k].NodeIndex >= 0) visited.Add(steps[k].NodeIndex);
                        if (k > 0 && steps[k].NodeIndex >= 0 && steps[k - 1].NodeIndex >= 0 && steps[k].NodeIndex != steps[k - 1].NodeIndex)
                            path.Add((steps[k - 1].NodeIndex, steps[k].NodeIndex, steps[k].BreakBefore));
                    }

                    product.SetState(-1, path, visited);
                }
            }

            if (product != null) product.gameObject.SetActive(prod);
            if (slotMark.gameObject.activeSelf == prod) slotMark.gameObject.SetActive(!prod);
            FilmUi.SetAlpha(slotOuter, prod ? 0.85f : 0.5f);
            FilmUi.SetAlpha(slotInner, prod ? 0.75f : 0.94f); // empty: a white ring round a dark hollow
            // the product lands in the slot: the slot grows a little when filled
            float grow = prod ? 1.6f : 1f;
            Vector2 size = slotSize * grow;
            FilmUi.Place(slotOuter.rectTransform, slotCentre + new Vector2((size.x - slotSize.x) * 0.5f, 0f), size);
            FilmUi.Place(slotInner.rectTransform, slotCentre + new Vector2((size.x - slotSize.x) * 0.5f, 0f), size - Vector2.one * 6f * unit);
            FilmUi.Place(productRoot, slotCentre + new Vector2((size.x - slotSize.x) * 0.5f, 0f), size - Vector2.one * 14f * unit);
        }
    }

    // ================================================================== beat counter

    class BeatCounter
    {
        readonly RectTransform root;
        readonly Image back;
        readonly Image[] dots = new Image[8];
        readonly Text label, measure;
        float unit;
        bool vertical;
        public bool Visible { get; private set; }
        int shownMeasure = -1;

        public BeatCounter(RectTransform parent)
        {
            root = FilmUi.Rect(parent, "Zouk count");
            back = FilmUi.Box(root, "Back", FilmUi.Rounded, new Color(0f, 0f, 0f, 0.55f));
            for (int i = 0; i < 8; i++) dots[i] = FilmUi.Box(root, $"Eighth {i + 1}", FilmUi.Disc, Color.white);
            label = FilmUi.Label(root, "Label", 22, new Color(0.85f, 0.88f, 0.92f), TextAnchor.MiddleLeft);
            label.text = "zouk beat";
            measure = FilmUi.Label(root, "Measure", 22, new Color(0.85f, 0.88f, 0.92f), TextAnchor.MiddleRight);
            root.gameObject.SetActive(false);
        }

        public void Layout(bool v, float W, float H, float u, FilmAspect aspect)
        {
            vertical = v;
            unit = u;
            float spacing = 36f * u, w = spacing * 8f + 28f * u, h = 88f * u;
            float right = W - (v ? 26f : 40f) * u;
            float topY = H * (1f - Mathf.Max(aspect.SafeTop, v ? 0.14f : 0.05f)) - 8f * u;
            Vector2 centre = new(right - w * 0.5f, topY - h * 0.5f);
            root.anchorMin = root.anchorMax = Vector2.zero;
            root.pivot = new Vector2(0.5f, 0.5f);
            root.anchoredPosition = centre;
            root.sizeDelta = new Vector2(w, h);
            FilmUi.Place(back.rectTransform, new Vector2(w * 0.5f, h * 0.5f), new Vector2(w, h));
            back.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 14f * u);
            int fs = Mathf.RoundToInt(22f * u);
            label.fontSize = measure.fontSize = fs;
            FilmUi.Place(label.rectTransform, new Vector2(w * 0.5f, h - fs * 0.9f), new Vector2(w - 28f * u, fs * 1.3f));
            FilmUi.Place(measure.rectTransform, new Vector2(w * 0.5f, h - fs * 0.9f), new Vector2(w - 28f * u, fs * 1.3f));
        }

        public void Show(float alpha, BeatGrid beats, float audioTime)
        {
            bool on = alpha > 0.002f && beats != null && beats.Count > 0 && float.IsFinite(audioTime);
            Visible = on;
            if (root.gameObject.activeSelf != on) root.gameObject.SetActive(on);
            if (!on) return;
            float spacing = 36f * unit;
            int bi = beats.IndexAtOrBefore(audioTime + BeatGrid.SnapTolerance);
            int m = bi >= 0 ? bi / BeatGrid.BeatsPerMeasure : -1;
            int eighth = bi >= 0 ? bi % BeatGrid.BeatsPerMeasure : -1;
            float since = bi >= 0 ? audioTime - beats.Times[bi] : 99f;
            float pulse = Mathf.Exp(-Mathf.Max(0f, since) / 0.16f);
            int first = m * BeatGrid.BeatsPerMeasure;
            for (int i = 0; i < 8; i++)
            {
                int idx = first + i;
                int type = idx >= 0 && idx < beats.Count ? beats.Types[idx] : 3;
                float size = (type == 1 ? 26f : type == 2 ? 19f : 12f) * unit;
                bool cur = i == eighth;
                Color c = cur ? new Color(1f, 0.84f, 0.04f, alpha) : new Color(1f, 1f, 1f, (type == 1 ? 0.7f : type == 2 ? 0.55f : 0.35f) * alpha);
                if (cur) size *= 1f + 0.55f * pulse;
                dots[i].color = c;
                FilmUi.Place(dots[i].rectTransform, new Vector2(14f * unit + spacing * (i + 0.5f), 30f * unit), new Vector2(size, size));
            }

            if (m != shownMeasure)
            {
                shownMeasure = m;
                measure.text = m >= 0 ? $"measure {m + 1}" : "";
            }

            FilmUi.SetAlpha(back, 0.55f * alpha);
            FilmUi.SetAlpha(label, alpha);
            FilmUi.SetAlpha(measure, alpha);
        }
    }

    // ================================================================== the dance HUD in the film

    class HudPlacement
    {
        DanceHud hud;
        CanvasScaler scaler;
        CanvasGroup group;
        RectTransform move, inset;
        bool saved;
        CanvasScaler.ScaleMode mode;
        Vector2 refRes;
        float match, scaleFactor;
        (Vector2 min, Vector2 max, Vector2 pivot, Vector2 pos) moveSaved, insetSaved;
        public bool Visible { get; private set; }
        public float BottomFromTop { get; private set; }
        /// <summary>screen px (bottom-left origin) of the visible move caption / graph inset in 16:9; empty otherwise</summary>
        public Rect MoveRect { get; private set; }
        public Rect InsetRect { get; private set; }
        string placed;

        public void Apply(bool vertical, float W, float H, float u, float safeTop, float lift, float alpha)
        {
            DanceHud h = DanceLayers.Instance != null ? DanceLayers.Instance.Hud : null;
            if (h == null) return;
            if (!ReferenceEquals(h, hud)) Bind(h);
            if (move == null || inset == null || scaler == null) return;
            Visible = (move.gameObject.activeInHierarchy && h.MoveCaptionVisible) || h.InsetVisible;
            if (group != null && Mathf.Abs(group.alpha - alpha) > 0.003f) group.alpha = alpha;
            if (vertical)
            {
                float s = 0.68f * W / 1080f;
                if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ConstantPixelSize) scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                if (Mathf.Abs(scaler.scaleFactor - s) > 1e-4f) scaler.scaleFactor = s;
                float top = safeTop * H + 8f * u;
                Set(move, new Vector2(0f, 1f), new Vector2(22f * u / s, -top / s));
                Set(inset, new Vector2(1f, 1f), new Vector2(-22f * u / s, -top / s));
                float bottom = top;
                if (h.MoveCaptionVisible) bottom = Mathf.Max(bottom, top + move.sizeDelta.y * s);
                if (h.InsetVisible) bottom = Mathf.Max(bottom, top + inset.sizeDelta.y * s);
                BottomFromTop = bottom;
                placed = "vertical";
                MoveRect = InsetRect = default;
            }
            else
            {
                RestoreScaler();
                float s = H / 1080f;
                Set(move, new Vector2(0f, 0f), new Vector2(36f, lift / s));
                Set(inset, new Vector2(1f, 0f), new Vector2(-36f, lift / s));
                BottomFromTop = 0f;
                placed = "horizontal";
                MoveRect = h.MoveCaptionVisible ? new Rect(36f * s, lift, move.sizeDelta.x * s, move.sizeDelta.y * s) : default;
                InsetRect = h.InsetVisible ? new Rect(W - (36f + inset.sizeDelta.x) * s, lift, inset.sizeDelta.x * s, inset.sizeDelta.y * s) : default;
            }
        }

        static void Set(RectTransform rt, Vector2 anchor, Vector2 pos)
        {
            if (rt.anchorMin != anchor) rt.anchorMin = anchor;
            if (rt.anchorMax != anchor) rt.anchorMax = anchor;
            if (rt.pivot != anchor) rt.pivot = anchor;
            if ((rt.anchoredPosition - pos).sqrMagnitude > 0.01f) rt.anchoredPosition = pos;
        }

        void Bind(DanceHud h)
        {
            Restore();
            hud = h;
            scaler = h.GetComponent<CanvasScaler>();
            foreach (Transform c in h.transform)
            {
                if (c.name == "Move caption") move = (RectTransform)c;
                else if (c.name == "Graph inset") inset = (RectTransform)c;
            }

            if (scaler == null || move == null || inset == null) return;
            mode = scaler.uiScaleMode;
            refRes = scaler.referenceResolution;
            match = scaler.matchWidthOrHeight;
            scaleFactor = scaler.scaleFactor;
            moveSaved = (move.anchorMin, move.anchorMax, move.pivot, move.anchoredPosition);
            insetSaved = (inset.anchorMin, inset.anchorMax, inset.pivot, inset.anchoredPosition);
            group = h.GetComponent<CanvasGroup>();
            if (group == null) group = h.gameObject.AddComponent<CanvasGroup>();
            group.interactable = group.blocksRaycasts = false;
            saved = true;
        }

        void RestoreScaler()
        {
            if (!saved || scaler == null) return;
            if (scaler.uiScaleMode != mode) scaler.uiScaleMode = mode;
            if (scaler.referenceResolution != refRes) scaler.referenceResolution = refRes;
            if (!Mathf.Approximately(scaler.matchWidthOrHeight, match)) scaler.matchWidthOrHeight = match;
            if (!Mathf.Approximately(scaler.scaleFactor, scaleFactor)) scaler.scaleFactor = scaleFactor;
        }

        public void Restore()
        {
            if (!saved) return;
            RestoreScaler();
            if (move != null)
            {
                move.anchorMin = moveSaved.min;
                move.anchorMax = moveSaved.max;
                move.pivot = moveSaved.pivot;
                move.anchoredPosition = moveSaved.pos;
            }

            if (inset != null)
            {
                inset.anchorMin = insetSaved.min;
                inset.anchorMax = insetSaved.max;
                inset.pivot = insetSaved.pivot;
                inset.anchoredPosition = insetSaved.pos;
            }

            if (group != null) UnityEngine.Object.Destroy(group);
            saved = false;
            hud = null;
            move = inset = null;
            scaler = null;
            group = null;
            placed = null;
        }

        public Dictionary<string, object> State() => new()
        {
            ["placed"] = placed, ["visible"] = Visible, ["bottomFromTop"] = Mathf.Round(BottomFromTop),
            ["move"] = move != null ? new[] { move.anchoredPosition.x, move.anchoredPosition.y } : null,
            ["inset"] = inset != null ? new[] { inset.anchoredPosition.x, inset.anchoredPosition.y } : null,
            ["alpha"] = group != null ? group.alpha : 1f
        };
    }
}
