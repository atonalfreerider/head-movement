using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen captions for the dance layers (desktop / renders; a world-space panel is the VR follow-up): the
/// current and next move with a provenance badge ("PLACEHOLDER - not an analysis"), the fingerprint side panel
/// (time per energy band, distinct moves, transitions, counterbalance share, longest phrase) and the tour state.
/// uGUI with the built-in font; every text is only written when it changes (no per-frame allocations).
/// </summary>
public class DanceHud : MonoBehaviour
{
    Canvas canvas;
    Text caption, captionSub, badge, tourLabel, panelTitle, panelBody;
    Image badgeBack, panelBack, captionBack;
    RectTransform panel;
    readonly Image[] bars = new Image[8];
    readonly Text[] barLabels = new Text[8];
    string captionText, captionSubText, badgeText, tourText, panelTitleText, panelBodyText;

    public string Caption => captionText;
    public string CaptionSub => captionSubText;
    public string BadgeText => badgeText;
    public bool PanelVisible => panel != null && panel.gameObject.activeSelf;
    public string PanelText => panelBodyText;

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

        captionBack = Box("Caption back", new Vector2(0.5f, 0f), new Vector2(0, 70), new Vector2(980, 110), new Color(0, 0, 0, 0.55f));
        caption = Label(captionBack.rectTransform, "Caption", 40, TextAnchor.UpperCenter, new Vector2(0, -8), new Vector2(960, 54), Color.white);
        caption.fontStyle = FontStyle.Bold;
        captionSub = Label(captionBack.rectTransform, "Caption sub", 24, TextAnchor.LowerCenter, new Vector2(0, 8), new Vector2(960, 36),
            new Color(0.8f, 0.85f, 0.9f));
        captionSub.rectTransform.anchorMin = captionSub.rectTransform.anchorMax = new Vector2(0.5f, 0f);
        captionSub.rectTransform.pivot = new Vector2(0.5f, 0f);

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
        panelTitle.rectTransform.anchorMin = panelTitle.rectTransform.anchorMax = new Vector2(0f, 1f);
        panelTitle.rectTransform.pivot = new Vector2(0f, 1f);
        for (int i = 0; i < bars.Length; i++)
        {
            barLabels[i] = Label(panel, $"Band label {i}", 22, TextAnchor.MiddleLeft, new Vector2(22, -78 - i * 40), new Vector2(520, 32),
                new Color(0.85f, 0.9f, 0.95f));
            barLabels[i].rectTransform.anchorMin = barLabels[i].rectTransform.anchorMax = new Vector2(0f, 1f);
            barLabels[i].rectTransform.pivot = new Vector2(0f, 1f);
            GameObject b = new($"Band bar {i}");
            b.transform.SetParent(panel, false);
            bars[i] = b.AddComponent<Image>();
            RectTransform rt = bars[i].rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(250, -82 - i * 40);
            rt.sizeDelta = new Vector2(10, 24);
            b.SetActive(false);
            barLabels[i].gameObject.SetActive(false);
        }

        panelBody = Label(panel, "Panel body", 23, TextAnchor.UpperLeft, new Vector2(22, -300), new Vector2(520, 330),
            new Color(0.9f, 0.92f, 0.95f));
        panelBody.rectTransform.anchorMin = panelBody.rectTransform.anchorMax = new Vector2(0f, 1f);
        panelBody.rectTransform.pivot = new Vector2(0f, 1f);
        panelBody.horizontalOverflow = HorizontalWrapMode.Wrap;
        panelBody.verticalOverflow = VerticalWrapMode.Overflow;

        SetCaption(null, null);
        SetBadge(null);
        SetTour(null);
        HidePanel();
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

    public void SetCaption(string main, string sub)
    {
        bool a = Changed(ref captionText, main), b = Changed(ref captionSubText, sub);
        if (!a && !b && captionBack.gameObject.activeSelf == (main != null)) return;
        captionBack.gameObject.SetActive(main != null);
        caption.text = main ?? "";
        captionSub.text = sub ?? "";
    }

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
        SetCaption(null, null);
        SetBadge(null);
        HidePanel();
    }
}
