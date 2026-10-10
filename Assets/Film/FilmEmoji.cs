using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Emoji caption words (a caption word with kind "emoji" and `emoji` = the code point in hex, e.g. "1f602"): they are drawn from an
/// IMAGE, never from the text font (the built-in dynamic font has no colour glyphs, so the recorder and the viewer would show an empty box).
/// Lookup order for a code point:
///   1. &lt;film folder&gt;/emoji/&lt;hex&gt;.png - written at setup time by the film tooling from the machine's own colour-emoji font; it stays in the
///      git-ignored film folder and is never part of this repository;
///   2. &lt;StreamingAssets&gt;/emoji/&lt;hex&gt;.png - the same, shared by every film of the project (git-ignored data folder);
///   3. a built-in drawing (this file: own artwork, MIT like the rest of the project) for the emoji the films use - "1f602" (face with tears of
///      joy). Without any of the three the word is shown as text.
/// The same sprite is used in the desktop viewer and in the frame-locked recorder (a Screen Space Overlay Image like every other film overlay).
/// </summary>
public static class FilmEmoji
{
    static readonly Dictionary<string, Sprite> Cache = new();

    /// <summary>where the last sprite came from: "file", "built-in" or null (the text fallback); for the state report and the checks</summary>
    public static string LastSource { get; private set; }

    public static Sprite Get(string hex, string filmDir)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        string key = hex.Trim().ToLowerInvariant();
        string cacheKey = (filmDir ?? "") + "|" + key;
        if (Cache.TryGetValue(cacheKey, out Sprite cached) && cached != null) return cached;
        Sprite s = FromFile(key, filmDir);
        if (s != null)
        {
            LastSource = "file";
        }
        else
        {
            s = key == "1f602" ? TearsOfJoy() : null;
            LastSource = s != null ? "built-in" : null;
        }

        Cache[cacheKey] = s;
        return s;
    }

    /// <summary>forget the cached sprites (a changed file on disk is read again)</summary>
    public static void Clear() => Cache.Clear();

    static Sprite FromFile(string key, string filmDir)
    {
        foreach (string dir in new[] { string.IsNullOrEmpty(filmDir) ? null : Path.Combine(filmDir, "emoji"), Path.Combine(Application.streamingAssetsPath, "emoji") })
        {
            if (dir == null) continue;
            string path = Path.Combine(dir, key + ".png");
            if (!File.Exists(path)) continue;
            try
            {
                Texture2D t = new(2, 2, TextureFormat.RGBA32, false) { name = "emoji " + key, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                if (!ImageConversion.LoadImage(t, File.ReadAllBytes(path), false)) continue;
                Sprite s = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f);
                s.name = "emoji " + key;
                return s;
            }
            catch (System.Exception)
            {
                // unreadable file: try the next source
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ built-in drawing: face with tears of joy (U+1F602)

    static Sprite TearsOfJoy()
    {
        const int n = 192;
        Texture2D t = new(n, n, TextureFormat.RGBA32, false) { name = "emoji 1f602 (built-in)", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        Color32[] px = new Color32[n * n];
        float aa = 1.5f / n;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n, v = (y + 0.5f) / n;           // v up
                Color c = new(0f, 0f, 0f, 0f);
                // face: a disc with a soft top-light gradient and a darker rim
                float dFace = Dist(u, v, 0.5f, 0.5f) - 0.47f;
                if (dFace < aa)
                {
                    float k = Mathf.Clamp01((v - 0.05f) / 0.9f);
                    Color body = Color.Lerp(new Color(0.99f, 0.74f, 0.10f), new Color(1f, 0.89f, 0.30f), k);
                    c = Over(c, body, Cover(dFace, aa));
                    c = Over(c, new Color(0.80f, 0.50f, 0.05f), Cover(Mathf.Abs(dFace + 0.012f) - 0.014f, aa) * 0.9f);
                }

                // tears: a blue drop at each side of the eyes, running down the cheeks
                foreach (float side in new[] { -1f, 1f })
                {
                    float cx = 0.5f + side * 0.335f, cy = 0.45f;
                    float dTear = Ellipse(u, v, cx, cy, 0.105f, 0.15f, side * 0.25f);
                    if (dTear < aa)
                    {
                        c = Over(c, new Color(0.14f, 0.50f, 0.88f), Cover(dTear, aa));
                        float dIn = Ellipse(u, v, cx - side * 0.004f, cy - 0.006f, 0.085f, 0.128f, side * 0.25f);
                        c = Over(c, new Color(0.34f, 0.76f, 0.99f), Cover(dIn, aa));
                        float dHi = Ellipse(u, v, cx + side * 0.03f, cy + 0.06f, 0.018f, 0.04f, side * 0.25f);
                        c = Over(c, new Color(1f, 1f, 1f), Cover(dHi, aa) * 0.85f);
                    }
                }

                // closed, squeezed eyes (arches) and raised brows
                foreach (float side in new[] { -1f, 1f })
                {
                    float ex = 0.5f + side * 0.185f;
                    c = Over(c, new Color(0.40f, 0.20f, 0.04f), Cover(Arc(u, v, ex, 0.585f, 0.078f, 0.026f, 18f, 162f), aa));
                    c = Over(c, new Color(0.50f, 0.28f, 0.05f), Cover(Arc(u, v, ex + side * 0.01f, 0.735f, 0.095f, 0.02f, 38f, 142f), aa) * 0.9f);
                }

                // mouth: a wide open D with teeth along the top and a tongue
                float dMouth = Mathf.Max(Ellipse(u, v, 0.5f, 0.455f, 0.275f, 0.265f, 0f), v - 0.455f);
                if (dMouth < aa)
                {
                    c = Over(c, new Color(0.42f, 0.14f, 0.06f), Cover(dMouth, aa));
                    float dIn = Mathf.Max(Ellipse(u, v, 0.5f, 0.455f, 0.255f, 0.245f, 0f), v - 0.455f);
                    c = Over(c, new Color(0.30f, 0.07f, 0.04f), Cover(dIn, aa));
                    float dTeeth = Mathf.Max(dIn, 0.385f - v);
                    c = Over(c, new Color(1f, 1f, 1f), Cover(dTeeth, aa));
                    float dTongue = Ellipse(u, v, 0.5f, 0.27f, 0.125f, 0.075f, 0f);
                    c = Over(c, new Color(0.93f, 0.33f, 0.36f), Cover(Mathf.Max(dTongue, dIn), aa));
                }

                px[y * n + x] = c;
            }
        }

        t.SetPixels32(px);
        t.Apply(false, true);
        Sprite s = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        s.name = "emoji 1f602 (built-in)";
        return s;
    }

    static float Dist(float u, float v, float cx, float cy) => Mathf.Sqrt((u - cx) * (u - cx) + (v - cy) * (v - cy));

    /// <summary>approximate signed distance to a (rotated) ellipse in unit-square coordinates: negative inside</summary>
    static float Ellipse(float u, float v, float cx, float cy, float rx, float ry, float rot)
    {
        float dx = u - cx, dy = v - cy;
        float cs = Mathf.Cos(rot), sn = Mathf.Sin(rot);
        float x = dx * cs + dy * sn, y = -dx * sn + dy * cs;
        float k = Mathf.Sqrt((x / rx) * (x / rx) + (y / ry) * (y / ry));
        return (k - 1f) * Mathf.Min(rx, ry);
    }

    /// <summary>signed distance to a thick arc (centre, radius, thickness) between two angles in degrees (counter-clockwise from +x), a round-capped ring segment</summary>
    static float Arc(float u, float v, float cx, float cy, float r, float thick, float a0, float a1)
    {
        float dx = u - cx, dy = v - cy;
        float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
        if (ang < 0f) ang += 360f;
        float ring = Mathf.Abs(Mathf.Sqrt(dx * dx + dy * dy) - r) - thick * 0.5f;
        if (ang >= a0 && ang <= a1) return ring;
        // round caps at the two ends
        Vector2 p = new(u, v);
        Vector2 e0 = new(cx + r * Mathf.Cos(a0 * Mathf.Deg2Rad), cy + r * Mathf.Sin(a0 * Mathf.Deg2Rad));
        Vector2 e1 = new(cx + r * Mathf.Cos(a1 * Mathf.Deg2Rad), cy + r * Mathf.Sin(a1 * Mathf.Deg2Rad));
        return Mathf.Min(Vector2.Distance(p, e0), Vector2.Distance(p, e1)) - thick * 0.5f;
    }

    static float Cover(float signedDistance, float aa) => Mathf.Clamp01(0.5f - signedDistance / (2f * aa));

    static Color Over(Color dst, Color src, float a)
    {
        a = Mathf.Clamp01(a) * src.a;
        if (a <= 0f) return dst;
        float outA = a + dst.a * (1f - a);
        if (outA <= 0f) return dst;
        Color o = (src * a + dst * dst.a * (1f - a)) / outA;
        o.a = outA;
        return o;
    }
}
