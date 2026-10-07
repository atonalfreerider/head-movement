using System;
using HeadMovementHair;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Procedural strand atlas of the hair cards, generated at load with a fixed seed (Burst; nothing sampled from a
/// person, so it is safe for the public repo). 1024 x 1024 RGBA32, 8 tiles of 128 x 1024 px (u across a card, v from
/// root to tip), 4 px empty margins, 6 mips with coverage-preserving alpha (Castano). Channels: R = coverage
/// (Gaussian strand profiles, thinner and fainter toward their ends), G = id of the top strand (colour variation,
/// specular jitter, sparkle), B = depth (0 back .. 1 front: in-tile occlusion), A = the top strand's end v.
/// Tiles: T0-T1 inner (dense, near-solid core), T2-T4 mid (clumped), T5-T6 outer (tight clumps: pointed, piecey
/// ends), T7 flyaways (a few separate strands). Straight strands: the look is sleek (no waviness except flyaways).
/// Glow anchors (GlowAnchor): per tile, the strand bundle whose end is the card's visible tip - u of its clump centre
/// (flyaways: the strand) and the v where its coverage last reaches 0.5 (what the alpha-to-coverage depth pass keeps),
/// so the tip glow sits on a real strand and ends where it does.
/// </summary>
public static class HairAtlas
{
    public const int Width = 1024, Height = 1024, Tiles = 8, TileWidth = Width / Tiles, Margin = 4, Levels = 6;

    struct TileSpec
    {
        public int Strands, Clumps;
        public float Sigma, Clump, VEndLo, VEndHi, WLo, WHi, Fill, Wave;
    }

    static readonly TileSpec Inner = new() { Strands = 90, Clumps = 6, Sigma = 18, Clump = 0.10f, VEndLo = 0.92f, VEndHi = 1f, WLo = 1.2f, WHi = 2.2f, Fill = 0.9f };
    static readonly TileSpec Mid = new() { Strands = 70, Clumps = 4, Sigma = 16, Clump = 0.35f, VEndLo = 0.85f, VEndHi = 1f, WLo = 1.4f, WHi = 2.4f, Fill = 0.55f };
    static readonly TileSpec Outer = new() { Strands = 55, Clumps = 3, Sigma = 12, Clump = 0.60f, VEndLo = 0.78f, VEndHi = 1f, WLo = 1.4f, WHi = 2.4f, Fill = 0.3f };
    static readonly TileSpec Fly = new() { Strands = 6, Clumps = 0, Sigma = 0, Clump = 0f, VEndLo = 0.70f, VEndHi = 1f, WLo = 1.0f, WHi = 1.6f, Wave = 0.3f };

    static Texture2D cached;
    static int cachedSeed = int.MinValue;
    static Vector2[] anchors = new Vector2[Tiles]; // per tile: (u across the card, visible end v)

    public static double LastGenerateMs { get; private set; }
    public static long Bytes => (long)Width * Height * 4 * 4 / 3;

    /// <summary>(u across the card 0..1, v of its visible end 0..1) of the tile's glow strand; (0.5, 0) before Get</summary>
    public static Vector2 GlowAnchor(int tile) => tile >= 0 && tile < Tiles && anchors[tile].y > 0f ? anchors[tile] : new Vector2(0.5f, 0f);

    /// <summary>atlas u of position u (0..1 across the card) in a tile</summary>
    public static float AtlasU(int tile, float u) => (tile * TileWidth + Margin + Mathf.Clamp01(u) * (TileWidth - 2 * Margin)) / Width;

    /// <summary>the atlas (generated once per seed and domain; shared by every hair)</summary>
    public static Texture2D Get(int seed = 1)
    {
        if (cached != null && cachedSeed == seed) return cached;
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        TileSpec[] specs = { Inner, Inner, Mid, Mid, Mid, Outer, Outer, Fly };
        System.Random rng = new(seed);
        int total = 0;
        foreach (TileSpec s in specs) total += s.Strands;
        using NativeArray<AtlasStrand> strands = new(total, Allocator.TempJob);
        using NativeArray<int2> range = new(Tiles, Allocator.TempJob);
        using NativeArray<float> fill = new(Tiles, Allocator.TempJob);
        NativeArray<AtlasStrand> st = strands;
        int[] clumpOf = new int[total];
        int at = 0;
        float usable = TileWidth - 2 * Margin;
        for (int t = 0; t < Tiles; t++)
        {
            TileSpec s = specs[t];
            NativeArray<int2> rr = range;
            rr[t] = new int2(at, s.Strands);
            NativeArray<float> ff = fill;
            ff[t] = s.Fill;
            float[] centres = new float[Math.Max(1, s.Clumps)];
            for (int j = 0; j < centres.Length; j++)
            {
                float spacing = usable / centres.Length;
                centres[j] = Margin + (j + 0.5f) * spacing + (float)(rng.NextDouble() - 0.5) * 0.5f * spacing;
            }

            for (int k = 0; k < s.Strands; k++)
            {
                float x0, xc;
                if (s.Clumps > 0)
                {
                    int j = rng.Next(centres.Length);
                    clumpOf[at] = j;
                    float c = centres[j];
                    x0 = Mathf.Clamp(c + Gauss(rng) * s.Sigma, Margin + 1.5f, TileWidth - Margin - 1.5f);
                    xc = Mathf.Clamp(c + Gauss(rng) * s.Sigma * 0.15f, Margin + 2f, TileWidth - Margin - 2f);
                }
                else
                {
                    clumpOf[at] = k; // a separate strand
                    x0 = Margin + 8f + (float)rng.NextDouble() * (usable - 16f);
                    xc = x0;
                }

                st[at++] = new AtlasStrand
                {
                    x0 = x0, xc = xc, clump = s.Clump, w0 = Mathf.Lerp(s.WLo, s.WHi, (float)rng.NextDouble()),
                    vEnd = Mathf.Lerp(s.VEndLo, s.VEndHi, (float)rng.NextDouble()), depth = (float)rng.NextDouble(),
                    id = (float)rng.NextDouble(), wave = s.Wave * (float)rng.NextDouble()
                };
            }
        }

        int texels = 0;
        int[] offsets = new int[Levels];
        for (int l = 0, w = Width, h = Height; l < Levels; l++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
        {
            offsets[l] = texels;
            texels += w * h;
        }

        using NativeArray<float4> pyramid = new(texels, Allocator.TempJob);
        using NativeArray<byte> bytes = new(texels * 4, Allocator.TempJob);
        using NativeArray<int> levelOffset = new(offsets, Allocator.TempJob);
        using NativeArray<float> target = new(Tiles, Allocator.TempJob);
        JobHandle rows = new AtlasRowJob
        {
            strands = strands, tileRange = range, tileFill = fill, texels = pyramid, width = Width, height = Height,
            tileWidth = TileWidth, margin = Margin, tiles = Tiles
        }.Schedule(Height * Tiles, 16);
        new AtlasMipJob
        {
            pyramid = pyramid, bytes = bytes, levelOffset = levelOffset, target = target, width = Width, height = Height,
            levels = Levels, tiles = Tiles
        }.Schedule(rows).Complete();

        FindGlowAnchors(st, range, pyramid, clumpOf, specs);
        Texture2D tex = new(Width, Height, TextureFormat.RGBA32, Levels, true)
        {
            name = "Hair strand atlas (procedural)", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear,
            anisoLevel = 4, mipMapBias = -0.5f, hideFlags = HideFlags.DontSave
        };
        for (int l = 0; l < Levels; l++) tex.SetPixelData(bytes, l, offsets[l] * 4);
        tex.Apply(false, true);
        if (cached != null) UnityEngine.Object.Destroy(cached);
        cached = tex;
        cachedSeed = seed;
        LastGenerateMs = watch.Elapsed.TotalMilliseconds;
        return tex;
    }

    /// <summary>
    /// Per tile: the bundle (clump of at least 4 strands, or a flyaway strand) whose coverage reaches furthest toward
    /// the tip. Its path is the mean strand position (the clumping of AtlasRowJob); its visible end is the last row where
    /// the level-0 coverage at the path (+-1 px) is still at least 0.5.
    /// </summary>
    static void FindGlowAnchors(NativeArray<AtlasStrand> st, NativeArray<int2> range, NativeArray<float4> level0, int[] clumpOf, TileSpec[] specs)
    {
        for (int t = 0; t < Tiles; t++)
        {
            int2 r = range[t];
            int groups = specs[t].Clumps > 0 ? specs[t].Clumps : specs[t].Strands;
            Vector2 best = new(0.5f, 0f);
            for (int j = 0; j < groups; j++)
            {
                int n = 0;
                for (int k = r.x; k < r.x + r.y; k++)
                    if (clumpOf[k] == j) n++;
                if (n == 0 || (specs[t].Clumps > 0 && n < 4)) continue;
                float PathX(float v)
                {
                    float sum = 0f;
                    for (int k = r.x; k < r.x + r.y; k++)
                    {
                        if (clumpOf[k] != j) continue;
                        AtlasStrand a = st[k];
                        float conv = a.clump * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 1f, v));
                        sum += Mathf.Lerp(a.x0, a.xc, conv) + a.wave * Mathf.Sin(v * 37f + a.id * 40f);
                    }

                    return sum / n;
                }

                for (int y = Height - 1; y > Height / 2; y--)
                {
                    float v = (y + 0.5f) / Height;
                    int x = Mathf.Clamp(Mathf.RoundToInt(PathX(v) - 0.5f), Margin, TileWidth - Margin - 1);
                    float cov = 0f;
                    for (int dx = -1; dx <= 1; dx++)
                        cov = Mathf.Max(cov, level0[y * Width + t * TileWidth + Mathf.Clamp(x + dx, 0, TileWidth - 1)].x);
                    if (cov < 0.5f) continue;
                    if (v > best.y) best = new Vector2(Mathf.Clamp01((PathX(v) - Margin) / (TileWidth - 2 * Margin)), v);
                    break;
                }
            }

            anchors[t] = best;
        }
    }

    static float Gauss(System.Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }
}
