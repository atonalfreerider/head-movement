using System;
using UnityEngine;

/// <summary>
/// VIEWER_SPEC 3.2 (user 2026-10-07: "avatars should be 65 % transparent"): the avatar opacity is what the SCREEN
/// shows, so each body gets its own blend alpha. The body blends in linear light and the display is sRGB, so the same
/// alpha looks far more opaque on a bright body than on a dark one: measured (hm_opacity --probe, Game-view pipeline)
/// alpha 0.35 left 48 % of a background change visible through a bright body and 74 % through a dark one.
///
/// Model (fitted to those measurements: rms 0.002 per dancer with its own shading factor; with the shared factor 0.95
/// the 65 % target lands within about +-0.015 for both): for a
/// body colour Y (linear luminance) and blend alpha a, the displayed difference matte over a black and a reference grey
/// background G is
///     t(a, Y) = (sRGB(a Y + (1 - a) G) - sRGB(a Y)) / sRGB(G),   G = 0.5225 (sRGB 0.75)
/// and a body's transparency T(a) is the area-weighted median of t over its surface (Y = albedo luminance at every
/// triangle centroid x Shading, tint included). BlendAlpha(opacity) inverts T: the alpha at which the body shows
/// T = 1 - opacity on screen (opacity 0.35 -> 65 % transparent: a bright body needs a much lower alpha than a dark one). Built once per body.
///
/// Per pixel (the default, SmplxAvatar.AlphaMode Pixel): one uniform alpha per body still leaves its bright parts
/// (bright skin or clothes at one body-wide alpha: ~37 % transparent) far more solid than its dark ones, so the colour pass
/// looks its alpha up per fragment from the shaded colour's luminance Y and the displayed opacity: AlphaLut(), a
/// LutY x LutO table of AlphaFor(Y, opacity) = the a with t(a, Y) = 1 - opacity, Y log-spaced 1e-4..2, opacity 0..1.
/// Every part of a body then shows the same displayed transparency.
/// </summary>
public sealed class DisplayTransparency
{
    public const float ReferenceGrey = 0.5225f; // linear of sRGB 0.75 (the probe's grey background)
    public const float Shading = 0.95f; // rendered / albedo luminance (fitted per body; 0.95 is the shared value)
    const int GridSteps = 256;
    const int Bins = 160; // log-spaced luminance bins (every triangle counts; the grid runs over the bins)
    const float BinMin = 1e-4f, BinMax = 1.5f;

    readonly float[] alphaGrid = new float[GridSteps + 1];
    readonly float[] transparency = new float[GridSteps + 1]; // T(alphaGrid[j]), decreasing from 1 to 0

    public int Samples { get; }
    public float MedianLuminance { get; }

    DisplayTransparency(float[] y, float[] w)
    {
        Samples = y.Length;
        MedianLuminance = WeightedMedian((float[])y.Clone(), (float[])w.Clone());
        float sg = Srgb(ReferenceGrey);
        float[] t = new float[y.Length];
        float[] wt = new float[y.Length];
        for (int j = 0; j <= GridSteps; j++)
        {
            float a = (float)j / GridSteps;
            for (int i = 0; i < y.Length; i++)
            {
                float ay = a * y[i];
                t[i] = (Srgb(ay + (1f - a) * ReferenceGrey) - Srgb(ay)) / sg;
                wt[i] = w[i];
            }

            alphaGrid[j] = a;
            transparency[j] = j == 0 ? 1f : j == GridSteps ? 0f : Mathf.Clamp01(WeightedMedian(t, wt));
        }

        for (int j = 1; j <= GridSteps; j++) transparency[j] = Mathf.Min(transparency[j], transparency[j - 1]); // monotone
    }

    /// <summary>a body of one colour (untextured tint)</summary>
    public static DisplayTransparency FromColour(Color tint) => new(new[] { Luminance(tint.linear) * Shading }, new[] { 1f });

    /// <summary>a textured body: the readable (uncompressed) albedo sampled at EVERY triangle centroid's UV,
    /// area-weighted, binned by luminance (log-spaced; a subsample biased the median by up to 15 %)</summary>
    public static DisplayTransparency FromTexture(Texture2D readable, Vector3[] vertices, int[] triangles, Vector2[] uv, Color tint)
    {
        int tris = triangles.Length / 3;
        double[] sw = new double[Bins], swy = new double[Bins];
        Color tl = tint.linear;
        float logMin = Mathf.Log(BinMin), logSpan = Mathf.Log(BinMax) - logMin;
        for (int f = 0; f < tris; f++)
        {
            int a = triangles[3 * f], b = triangles[3 * f + 1], c = triangles[3 * f + 2];
            Vector2 u = (uv[a] + uv[b] + uv[c]) / 3f;
            Color col = readable.GetPixelBilinear(u.x, u.y).linear; // stored sRGB bytes -> linear
            float y = Luminance(new Color(col.r * tl.r, col.g * tl.g, col.b * tl.b)) * Shading;
            float area = 0.5f * Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).magnitude;
            int bin = Mathf.Clamp((int)((Mathf.Log(Mathf.Max(y, BinMin)) - logMin) / logSpan * Bins), 0, Bins - 1);
            sw[bin] += area;
            swy[bin] += area * y;
        }

        int n = 0;
        for (int i = 0; i < Bins; i++)
            if (sw[i] > 0) n++;
        if (n == 0) return FromColour(tint);
        float[] ys = new float[n], ws = new float[n];
        for (int i = 0, k = 0; i < Bins; i++)
        {
            if (sw[i] <= 0) continue;
            ys[k] = (float)(swy[i] / sw[i]);
            ws[k++] = (float)sw[i];
        }

        return new DisplayTransparency(ys, ws) { Triangles = tris };
    }

    /// <summary>triangles sampled (0 for a one-colour body)</summary>
    public int Triangles { get; private set; }

    // ---------------------------------------------------------------- per pixel

    public const int LutY = 64, LutO = 33;
    public const float LutLogMin = -9.2103404f, LutLogMax = 0.6931472f; // ln 1e-4 .. ln 2
    static Texture2D lut;

    /// <summary>displayed difference-matte transparency of one colour (linear luminance y) at blend alpha a</summary>
    public static float PixelTransparency(float a, float y) =>
        (Srgb(a * y + (1f - a) * ReferenceGrey) - Srgb(a * y)) / Srgb(ReferenceGrey);

    /// <summary>the blend alpha at which a colour of linear luminance y shows the displayed opacity (bisection)</summary>
    public static float AlphaFor(float y, float opacity)
    {
        if (!(opacity > 0f)) return 0f;
        if (opacity >= 1f) return 1f;
        float target = 1f - opacity, lo = 0f, hi = 1f;
        for (int i = 0; i < 30; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (PixelTransparency(mid, y) > target) lo = mid;
            else hi = mid;
        }

        return 0.5f * (lo + hi);
    }

    /// <summary>the shader's table (R = alpha): x = (ln Y - LutLogMin) / (LutLogMax - LutLogMin) over LutY texels,
    /// y = displayed opacity over LutO texels (texel centres at 0 and 1); built once, bilinear, clamped</summary>
    public static Texture2D AlphaLut()
    {
        if (lut != null) return lut;
        Color[] px = new Color[LutY * LutO];
        for (int j = 0; j < LutO; j++)
        {
            float o = (float)j / (LutO - 1);
            for (int i = 0; i < LutY; i++)
            {
                float y = Mathf.Exp(Mathf.Lerp(LutLogMin, LutLogMax, (float)i / (LutY - 1)));
                px[j * LutY + i] = new Color(AlphaFor(y, o), 0f, 0f, 1f);
            }
        }

        lut = new Texture2D(LutY, LutO, TextureFormat.RHalf, false, true)
        {
            name = "HM avatar alpha LUT", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.DontSave
        };
        lut.SetPixels(px);
        lut.Apply(false, true);
        return lut;
    }

    /// <summary>displayed transparency (0..1) of the body at blend alpha a</summary>
    public float TransparencyAt(float alpha)
    {
        float x = Mathf.Clamp01(alpha) * GridSteps;
        int j = Mathf.Min(GridSteps - 1, (int)x);
        return Mathf.Lerp(transparency[j], transparency[j + 1], x - j);
    }

    /// <summary>the blend alpha at which the body SHOWS this opacity (1 - displayed transparency)</summary>
    public float BlendAlpha(float displayOpacity)
    {
        if (!(displayOpacity > 0f)) return 0f;
        if (displayOpacity >= 1f) return 1f;
        float target = 1f - displayOpacity;
        int lo = 0, hi = GridSteps; // transparency[lo] >= target >= transparency[hi]
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (transparency[mid] >= target) lo = mid;
            else hi = mid;
        }

        float t0 = transparency[lo], t1 = transparency[hi];
        float f = t0 - t1 > 1e-6f ? (t0 - target) / (t0 - t1) : 0f;
        return Mathf.Lerp(alphaGrid[lo], alphaGrid[hi], f);
    }

    static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

    static float Srgb(float x)
    {
        if (x <= 0f) return 0f;
        return x <= 0.0031308f ? 12.92f * x : 1.055f * Mathf.Pow(x, 1f / 2.4f) - 0.055f;
    }

    /// <summary>weighted median (sorts both arrays in place)</summary>
    static float WeightedMedian(float[] v, float[] w)
    {
        Array.Sort(v, w);
        float total = 0f;
        foreach (float x in w) total += x;
        float half = 0.5f * total, acc = 0f;
        for (int i = 0; i < v.Length; i++)
        {
            acc += w[i];
            if (acc >= half) return v[i];
        }

        return v.Length > 0 ? v[^1] : 0f;
    }
}
