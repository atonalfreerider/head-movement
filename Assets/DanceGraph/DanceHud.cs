using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen captions for the dance layers (desktop / renders; a world-space panel is the VR follow-up):
/// - the move caption (VIEWER_SPEC 3.12): a provenance tag ("NARRATED - not yet reviewed"), the Brazilian Portuguese
///   name, the English alias smaller beneath, the confidence as a percentage with a small bar ("labelled" for human
///   labels), the next move and a path line in the Dance graph state. Lower-left (16:9); while HeadMovement's debug HUD
///   is up it sits above the HUD's beat ticker.
/// - the graph inset (3.12, GraphInset): the state machine as a live minimap, lower-right, during directed playback.
/// - a provenance badge (top centre: the fingerprint / static graph), the fingerprint side panel (time per energy band,
///   distinct moves, transitions, counterbalance share, longest phrase) and the tour state (top right).
/// uGUI with the built-in font; every text is only written when it changes (no per-frame allocations).
/// </summary>
public class DanceHud : MonoBehaviour
{
    Canvas canvas;
    Text badge, tourLabel, panelTitle, panelBody;
    Image badgeBack, panelBack;
    RectTransform panel;
    readonly Image[] bars = new Image[8];
    readonly Text[] barLabels = new Text[8];
    string badgeText, tourText, panelTitleText, panelBodyText;

    // move caption
    RectTransform moveBack;
    CanvasGroup moveGroup;
    Text moveTag, moveName, moveAlias, moveConf, moveNext, moveInfo;
    Image confTrack, confFill;
    string tagText, nameText, aliasText, confText, nextText, infoText;
    float? confValue;
    float captionLift = -1f;
    const float CaptionWidth = 800f, ConfTrackWidth = 220f;

    // graph inset
    RectTransform insetBack;
    GraphInset inset;
    Text insetTitle;

    /// <summary>set by the film director while a film plays: the move caption shows only the move name (and alias)</summary>
    public bool FilmMode
    {
        get => filmMode;
        set
        {
            if (filmMode == value) return;
            filmMode = value;
            HideMoveCaption(); // the graph layer re-sends the caption on its next frame, in the new mode
        }
    }

    bool filmMode;

    public string Caption => moveBack != null && moveBack.gameObject.activeSelf ? nameText : null;
    public string CaptionSub => moveBack != null && moveBack.gameObject.activeSelf ? aliasText : null;
    public string CaptionTag => MoveCaptionVisible ? tagText : null;
    public string CaptionConfidence => MoveCaptionVisible ? confText : null;
    public float? CaptionConfidenceValue => MoveCaptionVisible ? confValue : null;
    public string CaptionNext => MoveCaptionVisible ? nextText : null;
    public string CaptionInfo => MoveCaptionVisible ? infoText : null;
    public float CaptionAlpha => moveGroup != null ? moveGroup.alpha : 0f;
    public float CaptionLift => captionLift;
    public bool MoveCaptionVisible => moveBack != null && moveBack.gameObject.activeSelf;
    public string BadgeText => badgeText;
    public bool PanelVisible => panel != null && panel.gameObject.activeSelf;
    public string PanelText => panelBodyText;
    public bool InsetVisible => insetBack != null && insetBack.gameObject.activeSelf;
    public GraphInset Inset => inset;

    public static DanceHud Create(Transform parent)
    {
        GameObject go = new("Dance HUD");
        go.transform.SetParent(parent, false);
        DanceHud hud = go.AddComponent<DanceHud>();
        hud.Build();
        return hud;
    }

    void Build()
    {
        canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50;
        CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;

        BuildMoveCaption();
        BuildInset();

        badgeBack = Box("Badge back", new Vector2(0.5f, 1f), new Vector2(0, -26), new Vector2(620, 52), new Color(0.85f, 0.35f, 0.05f, 0.9f));
        badge = Label(badgeBack.rectTransform, "Badge", 28, TextAnchor.MiddleCenter, Vector2.zero, new Vector2(600, 48), Color.white);
        badge.fontStyle = FontStyle.Bold;
        badge.rectTransform.anchorMin = badge.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        badge.rectTransform.pivot = new Vector2(0.5f, 0.5f);

        tourLabel = Label((RectTransform)transform, "Tour", 26, TextAnchor.UpperRight, new Vector2(-24, -20), new Vector2(700, 40),
            new Color(0.75f, 0.95f, 1f));
        tourLabel.rectTransform.anchorMin = tourLabel.rectTransform.anchorMax = new Vector2(1f, 1f);
        tourLabel.rectTransform.pivot = new Vector2(1f, 1f);

        panelBack = Box("Fingerprint panel", new Vector2(1f, 0.5f), new Vector2(-24, 0), new Vector2(560, 640), new Color(0, 0, 0, 0.6f));
        panelBack.rectTransform.pivot = new Vector2(1f, 0.5f);
        panel = panelBack.rectTransform;
        panelTitle = Label(panel, "Panel title", 32, TextAnchor.UpperLeft, new Vector2(22, -16), new Vector2(520, 44), Color.white);
        panelTitle.fontStyle = FontStyle.Bold;
        TopLeft(panelTitle.rectTransform);
        for (int i = 0; i < bars.Length; i++)
        {
            barLabels[i] = Label(panel, $"Band label {i}", 22, TextAnchor.MiddleLeft, new Vector2(22, -78 - i * 40), new Vector2(520, 32),
                new Color(0.85f, 0.9f, 0.95f));
            TopLeft(barLabels[i].rectTransform);
            GameObject b = new($"Band bar {i}");
            b.transform.SetParent(panel, false);
            bars[i] = b.AddComponent<Image>();
            RectTransform rt = bars[i].rectTransform;
            TopLeft(rt);
            rt.anchoredPosition = new Vector2(250, -82 - i * 40);
            rt.sizeDelta = new Vector2(10, 24);
            b.SetActive(false);
            barLabels[i].gameObject.SetActive(false);
        }

        panelBody = Label(panel, "Panel body", 23, TextAnchor.UpperLeft, new Vector2(22, -300), new Vector2(520, 330),
            new Color(0.9f, 0.92f, 0.95f));
        TopLeft(panelBody.rectTransform);
        panelBody.horizontalOverflow = HorizontalWrapMode.Wrap;
        panelBody.verticalOverflow = VerticalWrapMode.Overflow;

        HideMoveCaption();
        HideInset();
        SetBadge(null);
        SetTour(null);
        HidePanel();
    }

    void BuildMoveCaption()
    {
        Image back = Box("Move caption", new Vector2(0f, 0f), new Vector2(36, 40), new Vector2(CaptionWidth, 200), new Color(0, 0, 0, 0.58f));
        moveBack = back.rectTransform;
        moveGroup = back.gameObject.AddComponent<CanvasGroup>();
        moveGroup.interactable = false;
        moveGroup.blocksRaycasts = false;
        float w = CaptionWidth - 44;
        moveTag = Label(moveBack, "Provenance tag", 19, TextAnchor.UpperLeft, new Vector2(22, -12), new Vector2(w, 26), new Color(1f, 0.78f, 0.35f));
        moveTag.fontStyle = FontStyle.Bold;
        moveName = Label(moveBack, "Move name", 46, TextAnchor.UpperLeft, new Vector2(22, -36), new Vector2(w, 56), Color.white);
        moveName.fontStyle = FontStyle.Bold;
        moveAlias = Label(moveBack, "Move alias", 27, TextAnchor.UpperLeft, new Vector2(22, -92), new Vector2(w, 34), new Color(0.8f, 0.85f, 0.9f));
        GameObject track = new("Confidence track");
        track.transform.SetParent(moveBack, false);
        confTrack = track.AddComponent<Image>();
        confTrack.color = new Color(1f, 1f, 1f, 0.16f);
        confTrack.raycastTarget = false;
        TopLeft(confTrack.rectTransform);
        confTrack.rectTransform.anchoredPosition = new Vector2(24, -140);
        confTrack.rectTransform.sizeDelta = new Vector2(ConfTrackWidth, 12);
        GameObject fill = new("Confidence bar");
        fill.transform.SetParent(track.transform, false);
        confFill = fill.AddComponent<Image>();
        confFill.raycastTarget = false;
        TopLeft(confFill.rectTransform);
        confFill.rectTransform.anchoredPosition = Vector2.zero;
        confFill.rectTransform.sizeDelta = new Vector2(0, 12);
        moveConf = Label(moveBack, "Confidence", 22, TextAnchor.UpperLeft, new Vector2(24, -132), new Vector2(w, 28), new Color(0.88f, 0.9f, 0.92f));
        moveNext = Label(moveBack, "Next move", 22, TextAnchor.UpperLeft, new Vector2(22, -166), new Vector2(w, 28), new Color(0.62f, 0.9f, 1f));
        moveInfo = Label(moveBack, "Path info", 19, TextAnchor.UpperLeft, new Vector2(22, -194), new Vector2(w, 26), new Color(0.7f, 0.72f, 0.76f));
        foreach (Text t in new[] { moveTag, moveName, moveAlias, moveConf, moveNext, moveInfo }) TopLeft(t.rectTransform);
        confTrack.gameObject.SetActive(false);
        LayoutCaption();
    }

    void BuildInset()
    {
        Image back = Box("Graph inset", new Vector2(1f, 0f), new Vector2(-36, 40), new Vector2(470, 310), new Color(0, 0, 0, 0.55f));
        insetBack = back.rectTransform;
        GameObject g = new("Graph inset map");
        g.transform.SetParent(insetBack, false);
        g.AddComponent<CanvasRenderer>();
        inset = g.AddComponent<GraphInset>();
        inset.raycastTarget = false;
        RectTransform rt = inset.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(10, 10);
        rt.offsetMax = new Vector2(-10, -34);
        insetTitle = Label(insetBack, "Graph inset title", 19, TextAnchor.UpperLeft, new Vector2(14, -8), new Vector2(440, 26), new Color(0.75f, 0.8f, 0.86f));
        TopLeft(insetTitle.rectTransform);
        insetTitle.text = "dance graph";
    }

    static void TopLeft(RectTransform rt)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
    }

    Image Box(string name, Vector2 anchor, Vector2 position, Vector2 size, Color color)
    {
        GameObject go = new(name);
        go.transform.SetParent(transform, false);
        Image img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        RectTransform rt = img.rectTransform;
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.anchoredPosition = position;
        rt.sizeDelta = size;
        return img;
    }

    static Text Label(RectTransform parent, string name, int size, TextAnchor align, Vector2 position, Vector2 box, Color color)
    {
        GameObject go = new(name);
        go.transform.SetParent(parent, false);
        Text t = go.AddComponent<Text>();
        t.font = DanceText.Font;
        t.fontSize = size;
        t.alignment = align;
        t.color = color;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        RectTransform rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = position;
        rt.sizeDelta = box;
        return t;
    }

    static bool Changed(ref string field, string value)
    {
        if (ReferenceEquals(field, value) || field == value) return false;
        field = value;
        return true;
    }

    // ---------------------------------------------------------------- move caption

    /// <param name="tag">provenance tag (null: none, e.g. human labels)</param>
    /// <param name="confidence">0..1 for the bar, null = no bar (human: "labelled", unlabelled spans)</param>
    /// <param name="confText">"narration match 84 %", "labelled", ...</param>
    public void SetMoveCaption(string tag, string name, string alias, float? confidence, string confText, string next, string info,
        Color nameColor)
    {
        if (FilmMode)
        {
            // the directed film shows the move's name (and its English alias) only: no provenance tag, confidence bar,
            // next move or path line, and nothing at all while no move is labelled
            tag = confText = next = info = null;
            confidence = null;
            if (string.Equals(name, "unlabelled", System.StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(name))
            {
                HideMoveCaption();
                return;
            }
        }

        if (!moveBack.gameObject.activeSelf) moveBack.gameObject.SetActive(true);
        bool layout = false;
        if (Changed(ref tagText, tag))
        {
            moveTag.text = tag ?? "";
            layout = true;
        }

        if (Changed(ref nameText, name))
        {
            moveName.text = name ?? "";
            if (filmMode) layout = true; // the film's box hugs the name: re-measure
        }

        if (moveName.color != nameColor) moveName.color = nameColor;
        if (Changed(ref aliasText, alias))
        {
            moveAlias.text = alias ?? "";
            layout = true;
        }

        if (Changed(ref this.confText, confText))
        {
            moveConf.text = confText ?? "";
            layout = true;
        }

        if (confValue != confidence)
        {
            confValue = confidence;
            confTrack.gameObject.SetActive(confidence.HasValue);
            if (confidence.HasValue)
            {
                float v = Mathf.Clamp01(confidence.Value);
                confFill.rectTransform.sizeDelta = new Vector2(Mathf.Max(2f, ConfTrackWidth * v), 12);
                confFill.color = v >= 0.75f ? new Color(0.45f, 0.85f, 0.45f) : v >= 0.5f ? new Color(0.95f, 0.82f, 0.3f) : new Color(1f, 0.55f, 0.2f);
            }

            layout = true;
        }

        if (Changed(ref nextText, next))
        {
            moveNext.text = next ?? "";
            layout = true;
        }

        if (Changed(ref infoText, info))
        {
            moveInfo.text = info ?? "";
            layout = true;
        }

        if (layout) LayoutCaption();
    }

    /// <summary>stack the caption lines (empty lines collapse) and size the box</summary>
    void LayoutCaption()
    {
        float y = -12;
        bool hasTag = !string.IsNullOrEmpty(tagText);
        moveTag.gameObject.SetActive(hasTag);
        if (hasTag)
        {
            moveTag.rectTransform.anchoredPosition = new Vector2(22, y);
            y -= 24;
        }

        moveName.rectTransform.anchoredPosition = new Vector2(22, y);
        y -= 56;
        bool hasAlias = !string.IsNullOrEmpty(aliasText);
        moveAlias.gameObject.SetActive(hasAlias);
        if (hasAlias)
        {
            moveAlias.rectTransform.anchoredPosition = new Vector2(22, y);
            y -= 38;
        }

        bool hasConf = !string.IsNullOrEmpty(confText);
        moveConf.gameObject.SetActive(hasConf);
        if (hasConf || confValue.HasValue)
        {
            float x = confValue.HasValue ? 24 + ConfTrackWidth + 14 : 24;
            confTrack.rectTransform.anchoredPosition = new Vector2(24, y - 8);
            moveConf.rectTransform.anchoredPosition = new Vector2(x, y);
            y -= 32;
        }

        bool hasNext = !string.IsNullOrEmpty(nextText);
        moveNext.gameObject.SetActive(hasNext);
        if (hasNext)
        {
            moveNext.rectTransform.anchoredPosition = new Vector2(22, y);
            y -= 30;
        }

        bool hasInfo = !string.IsNullOrEmpty(infoText);
        moveInfo.gameObject.SetActive(hasInfo);
        if (hasInfo)
        {
            moveInfo.rectTransform.anchoredPosition = new Vector2(22, y);
            y -= 28;
        }

        float width = CaptionWidth;
        if (filmMode)
        {
            // in a film the box hugs its text: an 800 px plate would show as an orphan dark rectangle over the video at a
            // phone's point of view (16:9, the video is narrower than the box)
            float tw = moveName.preferredWidth;
            if (hasAlias) tw = Mathf.Max(tw, moveAlias.preferredWidth);
            width = Mathf.Clamp(tw + 44f, 140f, CaptionWidth);
        }

        moveBack.sizeDelta = new Vector2(width, -y + 8);
    }

    /// <summary>cross-fade at a segment boundary (alpha in dance time, so paused frames and renders are deterministic)</summary>
    public void SetMoveAlpha(float alpha)
    {
        if (moveGroup != null && Mathf.Abs(moveGroup.alpha - alpha) > 0.01f) moveGroup.alpha = alpha;
    }

    /// <summary>lower-left (spec 16:9 layout); while HeadMovement's debug HUD is up it sits above the HUD's beat ticker
    /// (the bottom 96 screen px)</summary>
    public void PlaceCaption(bool debugHud)
    {
        float lift = debugHud ? 112f * 1080f / Mathf.Max(1, Screen.height) : 40f;
        if (Mathf.Abs(captionLift - lift) < 0.5f) return;
        captionLift = lift;
        moveBack.anchoredPosition = new Vector2(36, lift);
    }

    public void HideMoveCaption()
    {
        if (moveBack != null && moveBack.gameObject.activeSelf) moveBack.gameObject.SetActive(false);
    }

    // ---------------------------------------------------------------- graph inset

    public void ShowInset(DanceGraphData data)
    {
        if (inset.Data != data) inset.Init(data);
        if (!insetBack.gameObject.activeSelf) insetBack.gameObject.SetActive(true);
    }

    public void HideInset()
    {
        if (insetBack != null && insetBack.gameObject.activeSelf) insetBack.gameObject.SetActive(false);
    }

    // ---------------------------------------------------------------- badge, tour label, fingerprint panel

    public void SetBadge(string text)
    {
        if (!Changed(ref badgeText, text) && badgeBack.gameObject.activeSelf == (text != null)) return;
        badgeBack.gameObject.SetActive(text != null);
        badge.text = text ?? "";
        if (text == null) return;
        bool placeholder = text.StartsWith("PLACEHOLDER");
        badgeBack.color = placeholder ? new Color(0.85f, 0.35f, 0.05f, 0.92f) : new Color(0.55f, 0.45f, 0.1f, 0.92f);
        badgeBack.rectTransform.sizeDelta = new Vector2(Mathf.Max(420, text.Length * 17 + 60), 52);
        badge.rectTransform.sizeDelta = new Vector2(Mathf.Max(400, text.Length * 17 + 40), 48);
    }

    public void SetTour(string text)
    {
        if (!Changed(ref tourText, text) && tourLabel.gameObject.activeSelf == (text != null)) return;
        tourLabel.gameObject.SetActive(text != null);
        tourLabel.text = text ?? "";
    }

    /// <summary>fingerprint side panel: bars = (label, seconds, share, colour)</summary>
    public void ShowPanel(string title, (string label, float seconds, float share, Color color)[] rows, string body)
    {
        panel.gameObject.SetActive(true);
        if (Changed(ref panelTitleText, title)) panelTitle.text = title;
        if (Changed(ref panelBodyText, body)) panelBody.text = body;
        float max = 0;
        foreach (var row in rows) max = Mathf.Max(max, row.share);
        for (int i = 0; i < bars.Length; i++)
        {
            bool on = i < rows.Length;
            bars[i].gameObject.SetActive(on);
            barLabels[i].gameObject.SetActive(on);
            if (!on) continue;
            (string label, float seconds, float share, Color color) = rows[i];
            barLabels[i].text = $"{label,-10} {seconds,5:0.0} s";
            bars[i].color = color;
            bars[i].rectTransform.sizeDelta = new Vector2(4 + 270 * (max > 0 ? share / max : 0), 24);
        }

        panelBody.rectTransform.anchoredPosition = new Vector2(22, -92 - rows.Length * 40);
    }

    public void HidePanel()
    {
        if (panel != null) panel.gameObject.SetActive(false);
    }

    public void Clear()
    {
        HideMoveCaption();
        HideInset();
        SetBadge(null);
        HidePanel();
    }
}
