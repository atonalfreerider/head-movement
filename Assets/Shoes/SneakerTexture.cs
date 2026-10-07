using UnityEngine;

/// <summary>
/// The sneaker's 512x256 UV atlas, painted procedurally at load (a few ms, never written to disk), mipmapped and
/// block-compressed like the avatar albedo. Layout (u horizontal, v up):
///   [0, .62] x [0, 1]      upper: u = perimeter column heel back (0) -> toe tip (K); v = medial base (0) -> top /
///                          ridge (.5) -> lateral base (1)
///   [.62, .86] x [0, 1]    sole wall: u = perimeter column; v = medial bottom -> top (0..0.5), lateral (0.5..1)
///   [.86, 1] x [0, .5]     outsole bottom (planar: x along, s across) - tread
///   [.86, .93] x [.5, .75] lining (collar inside, tongue back)
///   [.93, 1] x [.5, .75]   tongue front
///   [.86, .93] x [.75, 1]  laces;  [.93, 1] x [.75, 1] heel pull tab
/// Details: stitched panels (toe cap, heel counter, eyestays with eyelets), painted AO at the sole join / collar /
/// under the laces, flex creases, leather grain (court) or knit (runner), a generic swept side stripe (runner - never a
/// brand mark), sole groove + stitching (court) or outsole band + heel unit (runner), herringbone / waffle tread.
/// </summary>
public static class SneakerTexture
{
    public const int Width = 512, Height = 256;

    /// <summary>paint + upload (main thread)</summary>
    public static Texture2D Paint(SneakerStyle s, SneakerAtlasInfo info, int w = Width, int h = Height) =>
        ToTexture(PaintPixels(s, info, w, h), s, w, h);

    /// <summary>mipmapped, block-compressed (DXT1 / ETC) texture from painted pixels (main thread)</summary>
    public static Texture2D ToTexture(Color32[] px, SneakerStyle s, int w = Width, int h = Height)
    {
        Texture2D tex = new(w, h, TextureFormat.RGB24, true, false) { name = $"Sneaker atlas ({s.Style})" };
        tex.SetPixels32(px);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 2;
        tex.Apply(true, false);
        tex.Compress(false);
        tex.Apply(false, true);
        return tex;
    }

    /// <summary>the atlas pixels (row-major, v up). Pure managed code: safe on a worker thread</summary>
    public static Color32[] PaintPixels(SneakerStyle s, SneakerAtlasInfo info, int w = Width, int h = Height)
    {
        Color32[] px = new Color32[w * h];
        for (int y = 0; y < h; y++)
        {
            float v = (y + 0.5f) / h;
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w;
                Color c;
                if (u < 0.62f) c = Upper(s, info, u / 0.62f, v, x, y);
                else if (u < 0.86f) c = SoleWall(s, info, (u - 0.62f) / 0.24f, v, x, y);
                else if (v < 0.5f) c = Tread(s, (u - 0.86f) / 0.14f, v / 0.5f, x, y);
                else if (v < 0.75f) c = u < 0.93f ? Lining(s, (v - 0.5f) / 0.25f, x, y) : Tongue(s, (u - 0.93f) / 0.07f, (v - 0.5f) / 0.25f, x, y);
                else c = u < 0.93f ? Lace(s, (u - 0.86f) / 0.07f, (v - 0.75f) / 0.25f, x, y) : Tab(s, (u - 0.93f) / 0.07f, (v - 0.75f) / 0.25f);
                c.a = 1f;
                px[y * w + x] = c;
            }
        }

        return px;
    }

    // ------------------------------------------------------------------------------------------------ helpers

    static float Hash(int x, int y)
    {
        unchecked
        {
            uint n = (uint)(x * 374761393 + y * 668265263);
            n = (n ^ (n >> 13)) * 1274126177u;
            return ((n ^ (n >> 16)) & 0xffff) / 65535f;
        }
    }

    static float Noise(int x, int y, float amp) => (Hash(x, y) - 0.5f) * 2f * amp;

    static Color Mul(Color c, float f) => new(c.r * f, c.g * f, c.b * f, 1f);

    static float Band(float d, float halfWidth) => Mathf.Clamp01(1f - Mathf.Abs(d) / halfWidth);

    /// <summary>stitches: dashes along a line at signed distance d (in v-ish units), parameter a along the line</summary>
    static float Stitch(float d, float along, float halfWidth = 0.006f, float period = 0.022f)
    {
        if (Mathf.Abs(d) > halfWidth) return 0f;
        return Mathf.Repeat(along, period) < period * 0.6f ? 1f : 0f;
    }

    static float ColumnToX(SneakerAtlasInfo info, float col)
    {
        float f = Mathf.Clamp(col, 0f, SneakerShape.K - 0.0001f);
        int i = (int)f;
        return Mathf.Lerp(info.ColumnX[i], info.ColumnX[i + 1], f - i);
    }

    /// <summary>x / shoe length at an upper-atlas u (0..1): perimeter columns behind the throat, x over the vamp
    /// (SneakerBuilder.UpperU)</summary>
    static float UpperX(SneakerAtlasInfo info, float un)
    {
        float uo = info.KOpen / SneakerShape.K;
        if (un < uo) return ColumnToX(info, un * SneakerShape.K);
        return Mathf.Lerp(info.XnKOpen, 1f, (un - uo) / Mathf.Max(1e-4f, 1f - uo));
    }

    // ------------------------------------------------------------------------------------------------ regions

    /// <param name="un">0..1 across the upper region (perimeter column / K)</param>
    static Color Upper(SneakerStyle s, SneakerAtlasInfo info, float un, float v, int px, int py)
    {
        float col = un * SneakerShape.K;
        float xn = UpperX(info, un);
        bool lateral = v > 0.5f;
        float t = lateral ? (1f - v) / 0.5f : v / 0.5f; // 0 base -> 1 top
        bool front = col >= info.KOpen - 0.5f;
        Color c = s.Upper;

        if (s.Runner)
        {
            // engineered knit: fine rows + speckle
            float knit = 1f + 0.05f * Mathf.Sin(py * 1.9f + Mathf.Sin(px * 0.35f) * 1.2f) + Noise(px, py, 0.035f);
            c = Mul(c, knit);
            // heel counter and toe cap overlays (a touch darker / glossier)
            if (xn < 0.17f - 0.05f * t) c = Mul(s.Upper, 0.82f);
            if (xn > 0.86f + 0.04f * t) c = Mul(s.Upper, 0.86f);
            // generic swept side stripe on both side walls (a tapering sweep rising to the front; never a brand mark)
            float xEnd = info.XnKOpen - 0.01f;
            if (!front && xn > 0.14f && xn < xEnd && t < 0.8f)
            {
                float a = Mathf.InverseLerp(0.14f, xEnd, xn);
                float centre = 0.24f + 0.34f * a * a;
                float halfW = 0.10f * Mathf.Sin(Mathf.Min(1f, a * 1.15f) * Mathf.PI * 0.5f) * Mathf.Sqrt(Mathf.Clamp01((xEnd - xn) / 0.04f));
                float m = Band(t - centre, halfW + 0.005f);
                if (m > 0.3f) c = Color.Lerp(c, s.Accent, Mathf.Clamp01((m - 0.3f) * 6f));
            }
        }
        else
        {
            // leather grain
            c = Mul(c, 1f + Noise(px / 2, py / 2, 0.018f) + Noise(px, py, 0.01f));
            // toe cap panel with a stitched seam
            float seamToe = 0.80f - 0.05f * t * t;
            if (xn > seamToe) c = Mul(c, 0.985f);
            if (Stitch((xn - seamToe + 0.012f) * 6f, t * 0.6f) > 0f) c = Mul(c, 0.82f);
            // heel counter panel with a stitched edge
            float seamHeel = 0.20f + 0.07f * (1f - t);
            if (xn < seamHeel) c = Mul(c, 0.975f);
            if (Stitch((xn - seamHeel - 0.01f) * 6f, t * 0.6f) > 0f) c = Mul(c, 0.82f);
            // quarter seam
            float seamQ = 0.52f + 0.04f * t;
            if (!front && Stitch((xn - seamQ) * 6f, t * 0.6f) > 0f) c = Mul(c, 0.86f);
        }

        // heel tab: the back seam's top, ~3.5 cm wide (a dark pull area on runners); court shoes carry a larger
        // contrasting heel-counter panel over the upper half of the heel (a soft rounded lower edge)
        if (s.Platform)
        {
            float edge = 0.42f + 0.10f * Mathf.Clamp01(col / 2.4f) * Mathf.Clamp01(col / 2.4f);
            if (col < 2.4f && t > edge) c = Mul(s.HeelTab, 1f + Noise(px / 2, py / 2, 0.02f));
        }
        else if (col < 1.5f && t > 0.7f) c = s.HeelTab;
        // eyestays + eyelets along the opening from the top eyelets down to the throat
        if (!front && xn > info.XnEye - 0.02f && t > 0.84f)
        {
            c = Mul(c, s.Runner ? 0.8f : 0.94f);
            float along = Mathf.InverseLerp(info.XnEye, info.XnOpen - 0.03f, xn) * 4f;
            float d = Mathf.Abs(along - Mathf.Round(along));
            if (Mathf.Round(along) >= 0f && Mathf.Round(along) <= 4f && d < 0.14f && Mathf.Abs(t - 0.92f) < 0.045f)
                c = Mul(s.Lace, 0.55f);
        }

        // two faint flex creases across the vamp at the ball
        if (front && t > 0.5f)
        {
            float dx = (xn - info.XnBall) * 70f + 0.5f * Mathf.Sin(t * 7f);
            if (Mathf.Abs(dx) < 1.6f && Mathf.Abs(dx - Mathf.Round(dx)) < 0.1f) c = Mul(c, 0.96f);
        }

        // painted AO: sole join, collar rim
        c = Mul(c, Mathf.Lerp(0.74f, 1f, Mathf.Clamp01(t / 0.12f)));
        if (!front && t > 0.93f) c = Mul(c, 0.9f);
        return c;
    }

    static Color SoleWall(SneakerStyle s, SneakerAtlasInfo info, float un, float v, int px, int py)
    {
        float xn = ColumnToX(info, un * SneakerShape.K);
        float hf = v < 0.5f ? Mathf.InverseLerp(0.01f, 0.49f, v) : Mathf.InverseLerp(0.51f, 0.99f, v); // 0 bottom -> 1 top
        float rear = 1f - SneakerShape.Smooth(0.27f, 0.33f, xn - 0.07f * hf); // a slanted heel-unit edge
        Color c = Color.Lerp(s.Sole, s.SoleHeel, rear);
        c = Mul(c, 1f + Noise(px, py, 0.012f));
        if (s.Runner)
        {
            // heel unit: a window with a soft highlight, framed by the foam
            if (rear > 0.5f && hf > 0.28f && hf < 0.86f && xn > 0.03f)
            {
                float hl = Band(hf - 0.68f, 0.06f);
                c = Color.Lerp(Mul(s.SoleHeel, 0.9f), Color.white, 0.25f * hl);
            }

            // sculpted foam line
            if (Mathf.Abs(hf - (0.55f + 0.12f * Mathf.Sin(xn * 9f))) < 0.025f) c = Mul(c, 0.9f);
        }
        else
        {
            // cupsole: a smooth rounded wall - a faint groove and a tone-on-tone stitch line (court platforms: barely)
            if (Mathf.Abs(hf - 0.45f) < 0.03f) c = Mul(c, s.Platform ? 0.96f : 0.72f);
            if (Stitch((hf - 0.78f) * 0.8f, xn * 3.2f, 0.012f, 0.02f) > 0f) c = Mul(c, s.Platform ? 0.93f : 0.8f);
        }

        // outsole band at the bottom, top lip highlight, bottom-edge AO
        float band = s.OutsoleBandM / Mathf.Max(0.012f, 0.5f * (s.SoleHeelM + s.SoleForeM));
        if (hf < band) c = s.Outsole;
        if (hf > 0.94f) c = Mul(c, 1.04f);
        c = Mul(c, Mathf.Lerp(0.8f, 1f, Mathf.Clamp01(hf / 0.06f)));
        return c;
    }

    static Color Tread(SneakerStyle s, float a, float b, int px, int py)
    {
        Color c = Mul(s.Outsole, 1f + Noise(px, py, 0.03f));
        if (s.Runner)
        {
            // waffle pods
            float gx = Mathf.Repeat(a * 26f, 1f), gy = Mathf.Repeat(b * 9f + (Mathf.Floor(a * 26f) % 2) * 0.5f, 1f);
            if (gx < 0.18f || gy < 0.18f) c = Mul(c, 0.7f);
        }
        else
        {
            // herringbone
            float k = Mathf.Floor(b * 4f) % 2 == 0 ? 1f : -1f;
            float d = Mathf.Repeat(a * 40f + k * b * 12f, 1f);
            if (d < 0.22f) c = Mul(c, 0.72f);
        }

        // edge band
        if (b < 0.06f || b > 0.94f) c = Mul(c, 0.9f);
        return c;
    }

    static Color Lining(SneakerStyle s, float b, int px, int py)
    {
        Color c = Mul(s.Lining, 1f + Noise(px, py, 0.04f));
        return Mul(c, Mathf.Lerp(0.55f, 1f, b)); // darker deeper inside
    }

    static Color Tongue(SneakerStyle s, float a, float b, int px, int py)
    {
        Color c = s.Runner ? Mul(s.Upper, 1f + 0.05f * Mathf.Sin(py * 1.9f) + Noise(px, py, 0.03f)) : Mul(s.Upper, 1f + Noise(px, py, 0.015f));
        // padded edges (piping) and a plain generic label near the top
        if (a < 0.08f || a > 0.92f) c = Mul(c, 0.85f);
        if (b > 0.82f && b < 0.9f && a > 0.38f && a < 0.62f) c = s.Runner ? Color.Lerp(s.Upper, s.Accent, 0.6f) : Mul(s.Upper, 0.9f);
        if (b > 0.96f) c = Mul(c, 0.85f);
        return c;
    }

    static Color Lace(SneakerStyle s, float a, float b, int px, int py)
    {
        float weave = Mathf.Repeat(a * 30f + b * 6f, 1f) < 0.5f ? 1f : 0.9f;
        return Mul(s.Lace, weave * (b < 0.12f || b > 0.88f ? 0.85f : 1f) * (1f + Noise(px, py, 0.02f)));
    }

    static Color Tab(SneakerStyle s, float a, float b)
    {
        Color c = s.HeelTab;
        if (a < 0.1f || a > 0.9f || b > 0.92f) c = Mul(c, 0.8f);
        return c;
    }
}
