using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Short title cards at chapter starts (a segment's "chapter_card": {kind: "chapter" | "title", index, title, subtitle, film_in, film_out}, seconds
/// into the segment): a dark rounded pill under the top safe band with the chapter number on an accent disc and the title (9:16 centred, 16:9 at
/// the left), or, for kind "title", a two-line card (title, subtitle) with no number. It fades in over 0.35 s, slides up a little and fades out
/// over 0.45 s. A pure function of film time like every other film overlay (seeks and frame-locked recordings agree).
/// </summary>
public class ChapterCards
{
    class Card
    {
        public float F0, F1;
        public string Kind, Title, Subtitle;
        public int Index;
    }

    readonly List<Card> cards = new();
    readonly RectTransform root;
    readonly CanvasGroup group;
    readonly Image back, badge;
    readonly Text number, title, subtitle;
    readonly Color accent;
    Card laid;
    int laidW, laidH;
    bool laidVertical;
    float shownUnit = 1f;

    /// <summary>opacity of the card now (0..1)</summary>
    public float Alpha { get; private set; }

    /// <summary>the card's lower edge, px from the top of the screen (0 when none shows)</summary>
    public float BottomFromTop { get; private set; }

    public string CurrentTitle { get; private set; }
    public bool Any => cards.Count > 0;

    public ChapterCards(RectTransform parent, FilmDirection d, Color accentColour)
    {
        accent = accentColour;
        foreach (FilmSegment s in d.Segments)
        {
            if (s.Card == null) continue;
            float f0 = s.F0 + FilmDirection.F(s.Card["film_in"], 0.3f);
            float f1 = Mathf.Min(s.F1, s.F0 + FilmDirection.F(s.Card["film_out"], 2.9f));
            if (f1 - f0 < 0.3f) continue;
            cards.Add(new Card
            {
                F0 = f0, F1 = f1, Kind = s.Card.Value<string>("kind") ?? "chapter", Title = s.Card.Value<string>("title") ?? "", Subtitle = s.Card.Value<string>("subtitle"),
                Index = s.Card.Value<int?>("index") ?? 0
            });
        }

        root = FilmUi.Rect(parent, "Chapter card");
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.offsetMin = root.offsetMax = Vector2.zero;
        group = root.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = group.blocksRaycasts = false;
        back = FilmUi.Box(root, "Card backing", FilmUi.Rounded, new Color(0.03f, 0.03f, 0.05f, 0.62f));
        badge = FilmUi.Box(root, "Chapter number disc", FilmUi.Disc, accent);
        number = FilmUi.Label(root, "Chapter number", 40, new Color(0.067f, 0.067f, 0.067f), TextAnchor.MiddleCenter, true);
        title = FilmUi.Label(root, "Chapter title", 44, Color.white, TextAnchor.MiddleLeft, true);
        subtitle = FilmUi.Label(root, "Chapter subtitle", 26, new Color(0.85f, 0.88f, 0.92f), TextAnchor.MiddleLeft, false);
        FilmUi.Shadow(title, new Color(0f, 0f, 0f, 0.6f), 3f);
        FilmUi.Shadow(subtitle, new Color(0f, 0f, 0f, 0.6f), 2f);
    }

    Card ActiveAt(float film, out float a)
    {
        Card best = null;
        a = 0f;
        foreach (Card c in cards)
        {
            if (film < c.F0 || film > c.F1) continue;
            float fin = Mathf.SmoothStep(0f, 1f, (film - c.F0) / 0.35f), fout = Mathf.SmoothStep(0f, 1f, (c.F1 - film) / 0.45f);
            float k = Mathf.Min(fin, fout);
            if (k > a)
            {
                a = k;
                best = c;
            }
        }

        return best;
    }

    /// <summary>opacity of the card at film time f without laying anything out (the framing / HUD logic reads it)</summary>
    public float AlphaAt(float film)
    {
        ActiveAt(film, out float a);
        return a;
    }

    public void Tick(float film, bool vertical, float W, float H, float unit, FilmAspect aspect)
    {
        Card c = ActiveAt(film, out float a);
        Alpha = a;
        if (c == null)
        {
            if (group.alpha > 0f) group.alpha = 0f;
            BottomFromTop = 0f;
            CurrentTitle = null;
            return;
        }

        CurrentTitle = c.Title;
        if (!ReferenceEquals(c, laid) || laidW != Screen.width || laidH != Screen.height || laidVertical != vertical) Layout(c, vertical, W, H, unit, aspect);
        group.alpha = a;
        root.anchoredPosition = new Vector2(0f, -(1f - a) * 16f * shownUnit);
    }

    void Layout(Card c, bool vertical, float W, float H, float u, FilmAspect aspect)
    {
        laid = c;
        laidW = Screen.width;
        laidH = Screen.height;
        laidVertical = vertical;
        shownUnit = u;
        bool big = c.Kind == "title";
        float topSafe = Mathf.Max(aspect.SafeTop, vertical ? 0.14f : 0.05f);
        float topY = H * (1f - topSafe) - 14f * u;                       // the card's top edge, px from the bottom
        float titleSize = (big ? (vertical ? 66f : 54f) : (vertical ? 44f : 36f)) * u;
        float subSize = (vertical ? 30f : 26f) * u;
        float maxW = (vertical ? 0.92f : 0.55f) * W;
        string num = c.Index > 0 ? c.Index.ToString() : "";
        bool hasBadge = !big && num.Length > 0;
        float badgeD = titleSize * 1.15f, gap = titleSize * 0.4f, pad = titleSize * 0.45f;

        float scale = 1f;
        float tw = 0f, sw = 0f;
        for (int pass = 0; pass < 3; pass++)
        {
            int ts = Mathf.Max(10, Mathf.RoundToInt(titleSize * scale)), ss = Mathf.Max(10, Mathf.RoundToInt(subSize * scale));
            tw = FilmUi.Width(c.Title, ts, FontStyle.Bold);
            sw = big && !string.IsNullOrEmpty(c.Subtitle) ? FilmUi.Width(c.Subtitle, ss, FontStyle.Normal) : 0f;
            float content = Mathf.Max(tw, sw) + (hasBadge ? badgeD + gap : 0f) + 2f * pad;
            if (content <= maxW) break;
            scale *= Mathf.Max(0.6f, maxW / content);
        }

        int tSize = Mathf.Max(10, Mathf.RoundToInt(titleSize * scale)), sSize = Mathf.Max(10, Mathf.RoundToInt(subSize * scale));
        float textBlockH = tSize * 1.2f + (sw > 0f ? sSize * 1.35f : 0f);
        float h = Mathf.Max(hasBadge ? badgeD : 0f, textBlockH) + 2f * pad * 0.7f;
        float w = Mathf.Max(tw, sw) + (hasBadge ? badgeD + gap : 0f) + 2f * pad;
        float cx = vertical ? W * 0.5f : 0.04f * W + w * 0.5f;
        float cy = topY - h * 0.5f;
        BottomFromTop = H - (topY - h);

        FilmUi.Place(back.rectTransform, new Vector2(cx, cy), new Vector2(w, h));
        back.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 16f * u);
        float left = cx - w * 0.5f + pad;
        badge.enabled = number.enabled = hasBadge;
        if (hasBadge)
        {
            FilmUi.Place(badge.rectTransform, new Vector2(left + badgeD * 0.5f, cy), new Vector2(badgeD, badgeD));
            number.text = num;
            number.fontSize = Mathf.RoundToInt(badgeD * 0.55f);
            FilmUi.Place(number.rectTransform, new Vector2(left + badgeD * 0.5f, cy), new Vector2(badgeD, badgeD));
            left += badgeD + gap;
        }

        title.text = c.Title;
        title.fontSize = tSize;
        float ty = sw > 0f ? cy + sSize * 0.62f : cy;
        FilmUi.Place(title.rectTransform, new Vector2(left + tw * 0.5f, ty), new Vector2(tw + 8f, tSize * 1.3f));
        subtitle.enabled = sw > 0f;
        if (sw > 0f)
        {
            subtitle.text = c.Subtitle;
            subtitle.fontSize = sSize;
            FilmUi.Place(subtitle.rectTransform, new Vector2(left + sw * 0.5f, cy - tSize * 0.5f), new Vector2(sw + 8f, sSize * 1.4f));
        }
    }

    public Dictionary<string, object> State() => new()
    {
        ["chapterCard"] = CurrentTitle, ["chapterCardAlpha"] = Mathf.Round(Alpha * 1000f) / 1000f, ["chapterCards"] = cards.Count
    };
}
