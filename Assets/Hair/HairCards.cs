using System;
using System.Collections.Generic;
using HeadMovementHair;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Layout of the guide-driven hair cards (the way shipped console / VR games do hair): layered, textured, slightly
/// curved cards that lie on the hair surface and follow the simulated guides. Each card's spine is a weighted blend of
/// 3 neighbouring guides on the same side of the part, so neighbouring cards move as one coherent sheet (what reads as
/// sleek). Layers, inside to outside: inner (wide, dense tiles, slightly under), mid, outer (pointed, piecey ends that
/// carry the tip glow), part (short cards combed away from the side part), flyaways (a few loose strands). The scalp
/// cap is built by HairStrands from the skin. Table: hair_groom.json "cards" (version 2) or the defaults below.
/// </summary>
public sealed class HairCardLayout
{
    public const int LayerCap = 0, LayerInner = 1, LayerMid = 2, LayerOuter = 3, LayerPart = 4, LayerFlyaway = 5;
    public static readonly string[] LayerNames = { "cap", "inner", "mid", "outer", "part", "flyaway" };

    public struct Region
    {
        public string Name;
        public int Count, Side;
        public float AbsAzLo, AbsAzHi, AzLo, AzHi, ElAbove;
    }

    public sealed class Layer
    {
        public string Name;
        public int Id, Count, TileLo, TileHi, Short;
        public float WidthRoot, WidthTip, Offset, LfLo, LfHi, LenLo, LenHi, Bulge, Ao, ShortLo, ShortHi, WobbleLo, WobbleHi;
        public bool EdgeFade;
        public Region[] Regions;
    }

    public struct Card
    {
        public CardDesc Desc;
        public int Layer, Tile;
        public float Random, Ao, Length;
        public bool EdgeFade, MidOdd;
    }

    public Card[] Cards;
    public GlowDesc[] Glows;
    /// <summary>glows drawn at LOD 1 / 2 (one per outer card)</summary>
    public int GlowsLowLod;

    static Region R(string name, int count, float absLo = 0, float absHi = 180, float azLo = -180, float azHi = 180, float elAbove = 999, int side = 0) =>
        new() { Name = name, Count = count, AbsAzLo = absLo, AbsAzHi = absHi, AzLo = azLo, AzHi = azHi, ElAbove = elAbove, Side = side };

    /// <summary>the research design's layer table (metres of the rest model)</summary>
    public static Layer[] DefaultLayers() => new[]
    {
        new Layer { Name = "inner", Id = LayerInner, Count = 24, WidthRoot = 0.040f, WidthTip = 0.030f, Offset = -0.003f, LfLo = 0.92f, LfHi = 0.92f, TileLo = 0, TileHi = 1, Bulge = 0.08f, Ao = 0.55f },
        new Layer { Name = "mid", Id = LayerMid, Count = 40, WidthRoot = 0.030f, WidthTip = 0.018f, Offset = 0f, LfLo = 0.97f, LfHi = 1f, TileLo = 2, TileHi = 4, Bulge = 0.15f, Ao = 0.8f, EdgeFade = true },
        new Layer
        {
            Name = "outer", Id = LayerOuter, Count = 48, WidthRoot = 0.022f, WidthTip = 0.006f, Offset = 0.004f, LfLo = 0.95f, LfHi = 1.03f, TileLo = 5, TileHi = 6, Bulge = 0.15f, Ao = 1f, EdgeFade = true,
            Regions = new[]
            {
                R("crown_back", 24, 110, 180, elAbove: 62), R("right_side", 9, azLo: -110, azHi: -50), R("left_side", 5, azLo: 50, azHi: 110),
                R("front_right", 7, azLo: -50, azHi: 50, side: -1), R("behind_left_ear", 3, azLo: -50, azHi: 50, side: 1)
            }
        },
        new Layer { Name = "part", Id = LayerPart, Count = 8, WidthRoot = 0.020f, WidthTip = 0.014f, Offset = 0.002f, LenLo = 0.10f, LenHi = 0.16f, TileLo = 2, TileHi = 4, Bulge = 0.10f, Ao = 0.9f, EdgeFade = true },
        new Layer { Name = "flyaway", Id = LayerFlyaway, Count = 16, WidthRoot = 0.006f, WidthTip = 0.003f, Offset = 0.008f, LfLo = 0.90f, LfHi = 1.02f, TileLo = 7, TileHi = 7, Ao = 1f, EdgeFade = true, Short = 6, ShortLo = 0.10f, ShortHi = 0.16f, WobbleLo = 0.006f, WobbleHi = 0.012f },
    };

    /// <summary>layer table from hair_groom.json "cards" (version 2); missing values keep the defaults</summary>
    public static Layer[] FromGroom(JObject cards)
    {
        Layer[] layers = DefaultLayers();
        if (cards?["layers"] is not JArray arr) return layers;
        foreach (JToken t in arr)
        {
            string name = t.Value<string>("name");
            Layer l = Array.Find(layers, x => x.Name == name);
            if (l == null) continue;
            l.Count = t["count"]?.Value<int>() ?? l.Count;
            if (t["width_m"] is JArray w && w.Count == 2) (l.WidthRoot, l.WidthTip) = (w[0].Value<float>(), w[1].Value<float>());
            l.Offset = t["offset_m"]?.Value<float>() ?? l.Offset;
            if (t["length_factor"] is JArray lf && lf.Count == 2) (l.LfLo, l.LfHi) = (lf[0].Value<float>(), lf[1].Value<float>());
            if (t["length_m"] is JArray lm && lm.Count == 2) (l.LenLo, l.LenHi) = (lm[0].Value<float>(), lm[1].Value<float>());
            if (t["tiles"] is JArray ti && ti.Count == 2) (l.TileLo, l.TileHi) = (ti[0].Value<int>(), ti[1].Value<int>());
            l.Bulge = t["bulge"]?.Value<float>() ?? l.Bulge;
            l.Ao = t["ao"]?.Value<float>() ?? l.Ao;
            l.EdgeFade = t["edge_fade"]?.Value<bool>() ?? l.EdgeFade;
            l.Short = t["short"]?.Value<int>() ?? l.Short;
            if (t["short_length_m"] is JArray sl && sl.Count == 2) (l.ShortLo, l.ShortHi) = (sl[0].Value<float>(), sl[1].Value<float>());
            if (t["wobble_m"] is JArray wb && wb.Count == 2) (l.WobbleLo, l.WobbleHi) = (wb[0].Value<float>(), wb[1].Value<float>());
            if (t["regions"] is JArray regs)
            {
                List<Region> rs = new();
                foreach (JToken r in regs)
                {
                    Region x = R(r.Value<string>("name"), r.Value<int>("count"));
                    if (r["abs_az"] is JArray aa) (x.AbsAzLo, x.AbsAzHi) = (aa[0].Value<float>(), aa[1].Value<float>());
                    if (r["az"] is JArray az) (x.AzLo, x.AzHi) = (az[0].Value<float>(), az[1].Value<float>());
                    x.ElAbove = r["el_above"]?.Value<float>() ?? x.ElAbove;
                    x.Side = r["side"]?.Value<int>() ?? 0;
                    rs.Add(x);
                }

                l.Regions = rs.ToArray();
            }
        }

        return layers;
    }

    static bool In(in Region r, float az, float el, int side) =>
        ((Mathf.Abs(az) >= r.AbsAzLo && Mathf.Abs(az) <= r.AbsAzHi && az >= r.AzLo && az <= r.AzHi) || el >= r.ElAbove) &&
        (r.Side == 0 || r.Side == side);

    /// <param name="roots">guide roots, head-bone space (m, already scaled)</param>
    /// <param name="az">guide root azimuth, deg (0 = facing, + = her left)</param>
    /// <param name="el">guide root elevation from the head-sphere centre, deg</param>
    /// <param name="side">side of the part per guide (-1 her right, +1 her left)</param>
    /// <param name="length">guide length (m, scaled)</param>
    /// <param name="partX">x of the side part (head-bone space, scaled)</param>
    /// <param name="scale">LengthScale (widths / offsets / fixed lengths are rest-model metres)</param>
    /// <param name="rest">rest polylines (guides x points, head-bone space): a card only blends guides whose rest shapes
    /// run together (a front guide swept over the temple never blends with one hanging down the back)</param>
    /// <param name="glowsPerOuterCard">tip glow ribbons per glowing outer card (1 or 2)</param>
    /// <param name="outerStride">every n-th outer card carries tip glow (sparser = subtler)</param>
    /// <param name="glowAnchor">per atlas tile: (u, visible end v) of the strand the glow follows (HairAtlas.GlowAnchor);
    /// null = the card's centre line, ending at the job's end fraction</param>
    /// <param name="glowSpacing">no two glow tips closer than this in the rest shape (m of the rest model; no crossing
    /// "X" marks)</param>
    /// <param name="glowMinAbsAz">no glow on cards whose guide root is nearer the face than this |azimuth| (the front
    /// sections hang over her chest, in the middle of every face-on view: the stylisation stays on the back and sides)</param>
    public static HairCardLayout Build(Layer[] layers, Vector3[] roots, Vector3[] rest, int points, float[] az, float[] el, int[] side,
        float[] length, float partX, float scale, int seed, int glowsPerOuterCard = 1, int outerStride = 1,
        Func<int, Vector2> glowAnchor = null, float glowSpacing = 0f, float glowMinAbsAz = 0f)
    {
        System.Random rng = new(seed);
        List<Card> cards = new();
        int n = roots.Length;
        foreach (Layer l in layers)
        {
            if (l.Count <= 0) continue;
            List<(Region region, List<int> cand)> groups = new();
            if (l.Id == LayerPart)
            {
                // both sides of the side part, near the top
                foreach (int s in new[] { -1, 1 })
                {
                    List<int> c = new();
                    for (int g = 0; g < n; g++)
                        if (side[g] == s && Mathf.Abs(roots[g].x - partX) < 0.035f * scale && el[g] > 30f) c.Add(g);
                    groups.Add((R($"part_{s}", l.Count / 2), c));
                }
            }
            else if (l.Regions != null && l.Regions.Length > 0)
            {
                foreach (Region r in l.Regions)
                {
                    List<int> c = new();
                    for (int g = 0; g < n; g++)
                        if (In(r, az[g], el[g], side[g])) c.Add(g);
                    groups.Add((r, c));
                }
            }
            else if (l.Id == LayerFlyaway && l.Short > 0)
            {
                List<int> crown = new(), ends = new();
                for (int g = 0; g < n; g++)
                {
                    if (el[g] > 45f) crown.Add(g);
                    if (Mathf.Abs(az[g]) > 70f) ends.Add(g);
                }

                groups.Add((R("short", Mathf.Min(l.Short, l.Count)), crown));
                groups.Add((R("ends", Mathf.Max(0, l.Count - l.Short)), ends));
            }
            else
            {
                List<int> all = new();
                for (int g = 0; g < n; g++) all.Add(g);
                groups.Add((R("all", l.Count), all));
            }

            int midIndex = 0;
            foreach ((Region region, List<int> cand0) in groups)
            {
                List<int> cand = cand0.Count > 0 ? cand0 : AllGuides(n);
                bool isShort = l.Id == LayerFlyaway && region.Name == "short";
                foreach (int g in Farthest(cand, roots, region.Count, rng))
                {
                    (int a, int b) = Neighbours(g, roots, rest, points, side, scale);
                    float wa = a >= 0 ? (float)rng.NextDouble() * 0.4f : 0f;
                    float wb = b >= 0 ? (float)rng.NextDouble() * (0.45f - wa * 0.5f) : 0f;
                    float w0 = 1f - wa - wb;
                    float len = w0 * length[g] + (a >= 0 ? wa * length[a] : 0f) + (b >= 0 ? wb * length[b] : 0f);
                    float sEnd;
                    if (l.Id == LayerPart) sEnd = Mathf.Min(1f, Mathf.Lerp(l.LenLo, l.LenHi, (float)rng.NextDouble()) * scale / Mathf.Max(len, 1e-3f));
                    else if (isShort) sEnd = Mathf.Min(1f, Mathf.Lerp(l.ShortLo, l.ShortHi, (float)rng.NextDouble()) * scale / Mathf.Max(len, 1e-3f));
                    else sEnd = Mathf.Lerp(l.LfLo, l.LfHi, (float)rng.NextDouble());
                    float wobble = l.Id == LayerFlyaway && !isShort ? Mathf.Lerp(l.WobbleLo, l.WobbleHi, (float)rng.NextDouble()) * scale * (rng.NextDouble() < 0.5 ? -1f : 1f) : 0f;
                    cards.Add(new Card
                    {
                        Desc = new CardDesc
                        {
                            guide = new int3(g, Mathf.Max(a, 0), Mathf.Max(b, 0)), weight = new float3(w0, wa, wb),
                            widthRoot = l.WidthRoot * scale, widthTip = l.WidthTip * scale, offset = l.Offset * scale,
                            bulge = l.Bulge, sStart = 0f, sEnd = sEnd, wobble = wobble, wobblePhase = (float)(rng.NextDouble() * Math.PI * 2)
                        },
                        Layer = l.Id, Tile = rng.Next(l.TileLo, l.TileHi + 1), Random = (float)rng.NextDouble(), Ao = l.Ao,
                        Length = len * sEnd, EdgeFade = l.EdgeFade, MidOdd = l.Id == LayerMid && (midIndex++ & 1) == 1
                    });
                }
            }
        }

        // tip glow: 1 or 2 per outer card (on the tile's glow strand, or u 0.3 / 0.7; the first set alone at LOD 1 / 2)
        // + 1 per end flyaway; a glow whose rest tip is within glowSpacing of an earlier one is skipped
        List<GlowDesc> first = new(), second = new(), fly = new();
        List<Vector3> tips = new();
        float spacing = glowSpacing * scale;
        Vector3 RestTip(in Card cd)
        {
            int last = points - 1;
            Vector3 t = rest[cd.Desc.guide.x * points + last] * cd.Desc.weight.x;
            if (cd.Desc.weight.y > 0f) t += rest[cd.Desc.guide.y * points + last] * cd.Desc.weight.y;
            if (cd.Desc.weight.z > 0f) t += rest[cd.Desc.guide.z * points + last] * cd.Desc.weight.z;
            return t;
        }

        bool Spaced(in Card cd)
        {
            if (spacing <= 0f) return true;
            Vector3 t = RestTip(cd);
            foreach (Vector3 q in tips)
                if ((q - t).sqrMagnitude < spacing * spacing) return false;
            tips.Add(t);
            return true;
        }

        GlowDesc Anchored(int c, float uFallback)
        {
            Vector2 a = glowAnchor?.Invoke(cards[c].Tile) ?? new Vector2(uFallback, 0f);
            return a.y > 0f ? new GlowDesc { card = c, u = a.x, vEnd = a.y } : new GlowDesc { card = c, u = uFallback };
        }

        int outerIndex = 0;
        for (int c = 0; c < cards.Count; c++)
        {
            if (Mathf.Abs(az[cards[c].Desc.guide.x]) < glowMinAbsAz)
            {
                if (cards[c].Layer == LayerOuter) outerIndex++;
                continue;
            }

            if (cards[c].Layer == LayerOuter && outerIndex++ % Math.Max(1, outerStride) == 0)
            {
                if (!Spaced(cards[c])) continue;
                if (glowsPerOuterCard >= 2)
                {
                    first.Add(new GlowDesc { card = c, u = 0.3f });
                    second.Add(new GlowDesc { card = c, u = 0.7f });
                }
                else if (glowsPerOuterCard == 1)
                {
                    first.Add(Anchored(c, 0.5f));
                }
            }
            else if (cards[c].Layer == LayerFlyaway && cards[c].Desc.sEnd > 0.5f && Spaced(cards[c]))
            {
                fly.Add(Anchored(c, 0.5f));
            }
        }

        List<GlowDesc> glows = new(first);
        glows.AddRange(second);
        glows.AddRange(fly);
        return new HairCardLayout { Cards = cards.ToArray(), Glows = glows.ToArray(), GlowsLowLod = first.Count };
    }

    static List<int> AllGuides(int n)
    {
        List<int> all = new();
        for (int g = 0; g < n; g++) all.Add(g);
        return all;
    }

    /// <summary>farthest-point sample of `count` guides from cand (wraps around when more cards than guides)</summary>
    static IEnumerable<int> Farthest(List<int> cand, Vector3[] roots, int count, System.Random rng)
    {
        if (cand.Count == 0 || count <= 0) yield break;
        float[] d = new float[cand.Count];
        for (int i = 0; i < d.Length; i++) d[i] = float.MaxValue;
        int pick = rng.Next(cand.Count);
        for (int k = 0; k < count; k++)
        {
            yield return cand[pick];
            float best = -1f;
            int next = 0;
            for (int i = 0; i < cand.Count; i++)
            {
                d[i] = Mathf.Min(d[i], (roots[cand[i]] - roots[cand[pick]]).sqrMagnitude);
                if (d[i] > best)
                {
                    best = d[i];
                    next = i;
                }
            }

            if (best <= 0f)
            {
                for (int i = 0; i < d.Length; i++) d[i] = float.MaxValue; // every guide used: start a new round
                next = rng.Next(cand.Count);
            }

            pick = next;
        }
    }

    /// <summary>the two nearest guides on the same side of the part whose roots are within 6 cm and whose rest shapes
    /// stay within 3.5 cm of this one's on average (-1 when none); nearest by rest-shape distance</summary>
    static (int, int) Neighbours(int g, Vector3[] roots, Vector3[] rest, int points, int[] side, float scale)
    {
        int a = -1, b = -1;
        float da = float.MaxValue, db = float.MaxValue, lim = 0.06f * scale, shapeLim = 0.035f * scale;
        for (int k = 0; k < roots.Length; k++)
        {
            if (k == g || side[k] != side[g]) continue;
            if ((roots[k] - roots[g]).magnitude > lim) continue;
            float d = 0f, dmax = 0f;
            for (int i = 1; i < points; i++)
            {
                float di = (rest[k * points + i] - rest[g * points + i]).magnitude;
                d += di;
                dmax = Mathf.Max(dmax, di);
            }

            d /= points - 1;
            if (d > shapeLim || dmax > 2.5f * shapeLim) continue;
            if (d < da)
            {
                (b, db) = (a, da);
                (a, da) = (k, d);
            }
            else if (d < db)
            {
                (b, db) = (k, d);
            }
        }

        return (a, b);
    }
}
