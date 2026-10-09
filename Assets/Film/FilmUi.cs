using UnityEngine;
using UnityEngine.UI;

/// <summary>uGUI helpers for the film overlays: procedural sprites (rounded box, disc, triangle), texts with the built-in
/// font and pixel-exact text widths (dynamic font metrics), so the captions can lay words out one by one.</summary>
public static class FilmUi
{
    static Sprite rounded, disc, triangle;

    /// <summary>a white rounded box (corner radius 24 px of a 64 px texture), 9-sliced: scale the corner with
    /// Image.pixelsPerUnitMultiplier</summary>
    public static Sprite Rounded
    {
        get
        {
            if (rounded != null) return rounded;
            const int n = 64, r = 24;
            Texture2D t = new(n, n, TextureFormat.RGBA32, false) { name = "film rounded", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            Color32[] px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, r, n - r), cy = Mathf.Clamp(y + 0.5f, r, n - r);
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    byte a = (byte)(Mathf.Clamp01(r - d + 0.5f) * 255);
                    px[y * n + x] = new Color32(255, 255, 255, a);
                }
            }

            t.SetPixels32(px);
            t.Apply(false, true);
            rounded = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
            rounded.name = "film rounded";
            return rounded;
        }
    }

    public static Sprite Disc
    {
        get
        {
            if (disc != null) return disc;
            const int n = 64;
            Texture2D t = new(n, n, TextureFormat.RGBA32, false) { name = "film disc", wrapMode = TextureWrapMode.Clamp };
            Color32[] px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - n / 2f) * (x + 0.5f - n / 2f) + (y + 0.5f - n / 2f) * (y + 0.5f - n / 2f));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(n / 2f - 1f - d + 0.5f) * 255));
                }
            }

            t.SetPixels32(px);
            t.Apply(false, true);
            disc = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            disc.name = "film disc";
            return disc;
        }
    }

    /// <summary>a right-pointing triangle (arrowheads in the title)</summary>
    public static Sprite Triangle
    {
        get
        {
            if (triangle != null) return triangle;
            const int n = 64;
            Texture2D t = new(n, n, TextureFormat.RGBA32, false) { name = "film triangle", wrapMode = TextureWrapMode.Clamp };
            Color32[] px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n, v = (y + 0.5f) / n;
                    float half = 0.5f * (1f - u); // width at u
                    float d = Mathf.Min(half - Mathf.Abs(v - 0.5f), u) * n;
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(d + 0.5f) * 255));
                }
            }

            t.SetPixels32(px);
            t.Apply(false, true);
            triangle = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            triangle.name = "film triangle";
            return triangle;
        }
    }

    public static RectTransform Rect(Transform parent, string name)
    {
        GameObject go = new(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        return rt;
    }

    /// <summary>an Image; anchored at the canvas' bottom-left (positions are screen pixels)</summary>
    public static Image Box(Transform parent, string name, Sprite sprite, Color color)
    {
        RectTransform rt = Rect(parent, name);
        Image img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.type = sprite != null && sprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    public static Text Label(Transform parent, string name, int size, Color color, TextAnchor align = TextAnchor.MiddleCenter, bool bold = false)
    {
        RectTransform rt = Rect(parent, name);
        Text t = rt.gameObject.AddComponent<Text>();
        t.font = DanceText.Font;
        t.fontSize = size;
        t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        t.alignment = align;
        t.color = color;
        t.raycastTarget = false;
        t.supportRichText = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    public static void Outline(Text t, Color colour, float px)
    {
        Outline o = t.gameObject.GetComponent<Outline>();
        if (o == null) o = t.gameObject.AddComponent<Outline>();
        o.effectColor = colour;
        o.effectDistance = new Vector2(px, -px);
        o.useGraphicAlpha = true;
    }

    public static void Shadow(Text t, Color colour, float px)
    {
        Shadow[] all = t.gameObject.GetComponents<Shadow>();
        Shadow s = null;
        foreach (Shadow x in all)
        {
            if (x.GetType() == typeof(Shadow)) s = x;
        }

        if (s == null) s = t.gameObject.AddComponent<Shadow>();
        s.effectColor = colour;
        s.effectDistance = new Vector2(px * 0.6f, -px);
        s.useGraphicAlpha = true;
    }

    /// <summary>advance width of a string in pixels at a font size / style (dynamic font metrics)</summary>
    public static float Width(string text, int size, FontStyle style = FontStyle.Normal)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        Font f = DanceText.Font;
        f.RequestCharactersInTexture(text, size, style);
        float w = 0f;
        foreach (char c in text)
        {
            if (f.GetCharacterInfo(c, out CharacterInfo ci, size, style)) w += ci.advance;
            else w += size * 0.5f;
        }

        return w;
    }

    public static void Place(RectTransform rt, Vector2 centre, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = centre;
        rt.sizeDelta = size;
    }

    public static void SetAlpha(Graphic g, float a)
    {
        Color c = g.color;
        if (Mathf.Abs(c.a - a) < 0.004f) return;
        c.a = a;
        g.color = c;
    }

    public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);
}
