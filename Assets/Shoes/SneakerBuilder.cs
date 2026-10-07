using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// A dancer's rest foot measured in avatar-local REST coordinates (the space of SmplxData.Skin.RestVertices), in a
/// per-foot frame: x along the foot from the heel back (0) to the toe tip (L), s across it (medial = the big-toe side
/// positive), h above the sole plane (the lowest foot vertex, which is the sneaker's outsole bottom: dancecap's world
/// scale treats the fitted mesh sole as the shoe sole). Every axis comes from the SMPL-X landmark vertices; nothing is
/// hard-coded per capture.
/// </summary>
public class FootMeasure
{
    public const int Stations = 24;

    public bool Left;
    public int AnkleJoint, BallJoint, KneeJoint;
    public Vector3 Origin; // heel back on the sole plane
    public Vector3 Fwd, Med; // horizontal unit axes
    public Vector3 FlexAxis; // rest-space axis about which a positive angle lifts the toes
    public float Y0, L, BallX, BallH, AnkleX, AnkleH;
    public float XEye; // front of the leg at ankle-joint height: where the top eyelets sit
    public Vector3 AnkleRest, BallRest;
    public readonly float[] HalfMed = new float[Stations], HalfLat = new float[Stations], Dorsum = new float[Stations];
    public int VertexCount;
    public int[] FootVertices; // split vertices with w(ankle) + w(foot) >= 0.5
    public Vector3[] FootLocal; // their (x, s, h)
    public Vector3[] LegLocal; // this leg's vertices (w(ankle) + w(foot) + w(knee) >= 0.5) below 25 cm, (x, s, h)
    public float LegCs; // s of the leg's section centre at the ankle joint's height (the joint itself sits medially)

    // set by SneakerShape.SetCut (diagnostics): the leg's section at the body cut
    public float SliceXMin, SliceXMax, SliceHalfMed, SliceHalfLat;

    public Vector3 Rest(float x, float s, float h) => Origin + Fwd * x + Med * s + Vector3.up * h;

    public Vector3 Local(Vector3 p)
    {
        Vector3 d = p - Origin;
        return new Vector3(Vector3.Dot(d, Fwd), Vector3.Dot(d, Med), d.y);
    }

    public float StationX(int i) => (i + 0.5f) / Stations * L;

    public float Sample(float[] a, float x)
    {
        float f = Mathf.Clamp(x / L * Stations - 0.5f, 0f, Stations - 1.001f);
        int i = (int)f;
        return Mathf.Lerp(a[i], a[i + 1], f - i);
    }

    /// <summary>top of the leg / instep near x (|s| &lt; sHalf) below hMax; NaN when nothing is there</summary>
    public float TopH(float x, float band, float sHalf, float hMax)
    {
        float h = float.NaN;
        foreach (Vector3 p in LegLocal)
        {
            if (Mathf.Abs(p.x - x) > band || Mathf.Abs(p.y) > sHalf || p.z > hMax) continue;
            h = float.IsNaN(h) ? p.z : Mathf.Max(h, p.z);
        }

        return h;
    }

    /// <summary>centre (s) of the leg's cross-section at height h</summary>
    public float CentreS(float h, float band)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (Vector3 p in LegLocal)
        {
            if (Mathf.Abs(p.z - h) > band) continue;
            lo = Mathf.Min(lo, p.y);
            hi = Mathf.Max(hi, p.y);
        }

        return hi >= lo ? 0.5f * (lo + hi) : 0f;
    }

    /// <summary>front-most x of the leg at height h (+- band) within |s| &lt; sHalf; NaN when nothing is there</summary>
    public float FrontX(float h, float band, float sHalf)
    {
        float x = float.NaN;
        foreach (Vector3 p in LegLocal)
        {
            if (Mathf.Abs(p.z - h) > band || Mathf.Abs(p.y) > sHalf) continue;
            x = float.IsNaN(x) ? p.x : Mathf.Max(x, p.x);
        }

        return x;
    }
}

/// <summary>
/// The sneaker's shape functions for one foot (shoe-CAD style): a plan outline from the measured foot widths plus
/// margins, a sole stack, a vamp ridge over the forefoot, and - once the body cut is known (SetCut) - a collar opening
/// around the leg's cross-section at the cut, eyestays that run down the instep to the throat, and a tongue lying on the
/// leg's front silhouette. Used by the mesh builder and the playtest's hull / collar checks.
/// </summary>
public class SneakerShape
{
    public const int K = 20, Kh = 4, Kt = 14; // perimeter columns per side: heel cap 0..Kh, side Kh..Kt (dense: the
                                              // eyestays run along it), toe cap Kt..K
    public const float SuperN = 2.6f;
    public const float MarginSide = 0.004f, MarginToe = 0.011f, MarginHeel = 0.006f, OpenMargin = 0.008f;
    const int SecN = 20, SpineN = 9;

    public readonly FootMeasure M;
    public readonly SneakerStyle S;
    public float X0, X1, Xh, Rh, Xt, Rt, Sc, Ha, XRidgeEnd;

    /// <summary>where the visible leg goes in this shoe over the whole capture (SneakerFit); null = rest pose only.
    /// The mesh builder pushes the rear upper and the collar out to it</summary>
    public LegEnvelope Env;

    /// <summary>shin share of the tongue's skinning at its top (it rides on the shin like a real tongue; the leg's
    /// front there is ~0.75-0.85 knee-weighted), ramping up from the top eyelets</summary>
    public const float TongueShin = 0.8f;

    /// <summary>shin weight of a tongue point at rest height h: 0 on the laced instep, ramping to TongueShin over the top
    /// eyelets (+-6 mm)</summary>
    public float TongueShinWeight(float h)
    {
        float hEye = spine.Count > LacePoints ? spine[LacePoints].z : Ha;
        return TongueShin * Smooth(hEye - 0.006f, hEye + 0.006f, h);
    }

    /// <summary>the body's cut height at a foot point (x, s) (the per-vertex foot mask; SneakerBuilder.FootHeights): round
    /// the collar opening, where the leg leaves the shoe, the shoe's top edge minus the margin (the shoe may be planted
    /// down the leg); in front of the top eyelets nothing leaves the shoe - the laced tongue and the closed vamp cover
    /// the instep - so everything under them is cut</summary>
    public float CutAt(float x, float s, float margin) => CoverTop(x, s) - margin * MarginShare(x);

    /// <summary>share of the cut margin at x: 1 round the collar, 0 under the laced tongue and the vamp</summary>
    public float MarginShare(float x) => 1f - Smooth(XEye + 0.010f, XEye + 0.025f, x);

    // set by SetCut
    public float CutH, XOpen, XEye, CxMin, CxMax, XBack, OpenXc;
    public int KOpen; // first front (closed vamp) column
    readonly float[] ridge = new float[33];
    readonly float[] secMed = new float[SecN], secLat = new float[SecN];
    readonly List<Vector3> spine = new(); // tongue centreline (x, s, h): throat -> top in front of the shin
    readonly List<Vector3> spineNormal = new(); // its forward / up normal in the x-h plane

    public IReadOnlyList<Vector3> Spine => spine;
    public IReadOnlyList<Vector3> SpineNormal => spineNormal;

    public SneakerShape(FootMeasure m, SneakerStyle s)
    {
        M = m;
        S = s;
        float L = m.L;
        X0 = -MarginHeel;
        X1 = L + MarginToe + (s.Platform ? 0.003f : 0f);
        Rh = 0.5f * (m.Sample(m.HalfMed, 0.12f * L) + m.Sample(m.HalfLat, 0.12f * L)) + MarginSide;
        Xh = X0 + Rh;
        Rt = 0.22f * L;
        Xt = X1 - Rt;
        Sc = 0.25f * (W(Xt, 1) - W(Xt, -1));
        Ha = m.AnkleH + s.CollarRaise;
        XRidgeEnd = X1 - 0.45f * Rt;
        SetCut(RimDip - SneakerBuilder.CutMargin); // provisional: Build() sets the avatar-wide cut
    }

    /// <summary>the leg's axis where it enters the shoe (x of the ankle joint, s of the leg's section centre)</summary>
    public Vector2 LegAxis
    {
        get
        {
            return new Vector2(M.AnkleX, M.LegCs);
        }
    }

    /// <summary>the lowest collar rim around the leg (below the ankle bones at the sides)</summary>
    public float RimDip => 0.88f * Ha;

    public static float Smooth(float a, float b, float x)
    {
        float t = Mathf.Clamp01((x - a) / (b - a));
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// The body is clipped below cutH (above this foot's sole plane). The collar opening then has to enclose the
    /// leg's cross-section just above the cut, and the tongue has to cover the instep that remains above it.
    /// </summary>
    public void SetCut(float cutH)
    {
        CutH = cutH;
        float L = M.L;
        // the collar opening follows the leg's section just above the cut, but never lower than 2.5 cm under the rim
        // dip (a lower cut - for a large plant - would slice the instep into the opening)
        float sliceH = Mathf.Max(cutH, RimDip - 0.025f);

        // the leg section just above the cut: x range and half-widths per station
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (Vector3 p in M.LegLocal)
        {
            if (p.z < sliceH - 0.008f || p.z > sliceH + 0.012f) continue;
            lo = Mathf.Min(lo, p.x);
            hi = Mathf.Max(hi, p.x);
        }

        if (hi <= lo)
        {
            lo = M.AnkleX - 0.05f;
            hi = M.AnkleX + 0.05f;
        }

        CxMin = lo;
        CxMax = hi;
        for (int i = 0; i < SecN; i++)
        {
            secMed[i] = float.NaN;
            secLat[i] = float.NaN;
        }

        float win = 0.7f * (CxMax - CxMin) / SecN;
        foreach (Vector3 p in M.LegLocal)
        {
            if (p.z < sliceH - 0.008f || p.z > sliceH + 0.012f) continue;
            for (int i = 0; i < SecN; i++)
            {
                if (Mathf.Abs(p.x - SecX(i)) > win) continue;
                secMed[i] = float.IsNaN(secMed[i]) ? p.y : Mathf.Max(secMed[i], p.y);
                secLat[i] = float.IsNaN(secLat[i]) ? -p.y : Mathf.Max(secLat[i], -p.y);
            }
        }

        SneakerBuilder.FillGaps(secMed, 0.02f);
        SneakerBuilder.FillGaps(secLat, 0.02f);
        SneakerBuilder.Smooth3(secMed);
        SneakerBuilder.Smooth3(secLat);

        // the throat (front end of the lacing) sits well in front of the leg, like a sneaker's vamp (~0.6 L); the top
        // eyelets just in front of the leg at ankle height
        XOpen = Mathf.Clamp(Mathf.Max(CxMax + 0.02f, 0.60f * L), 0.50f * L, 0.68f * L);
        XBack = CxMin - OpenMargin;
        OpenXc = Mathf.Clamp(M.AnkleX, XBack + 0.02f, XOpen - 0.05f);
        float eye = M.FrontX(1.02f * M.AnkleH, 0.006f, 0.03f);
        XEye = Mathf.Clamp((float.IsNaN(eye) ? M.AnkleX + 0.035f : eye) + 0.004f, OpenXc + 0.01f, XOpen - 0.035f);
        M.SliceXMin = CxMin;
        M.SliceXMax = CxMax;
        M.SliceHalfMed = SecMed(M.AnkleX);
        M.SliceHalfLat = SecLat(M.AnkleX);
        KOpen = K;
        for (int k = Kh + 1; k <= Kt; k++)
        {
            if (Outline(k, 1, 0f).x >= XOpen - 0.004f)
            {
                KOpen = k;
                break;
            }
        }

        XOpen = Mathf.Min(Outline(KOpen, 1, 0f).x, Outline(KOpen, -1, 0f).x);
        XEye = Mathf.Min(XEye, XOpen - 0.03f);

        // vamp ridge height: over the dorsum + 6 mm and at least the toe-box minimum above the sole top; never rising
        // towards the toe, smoothed
        for (int i = 0; i < ridge.Length; i++)
        {
            float x = Mathf.Lerp(XOpen, X1, i / (ridge.Length - 1f));
            float boxMin = Mathf.Lerp(0.030f, 0.018f, Smooth(M.BallX, X1, x)) + (S.Platform ? 0.004f : 0f);
            // padding over the instep where the leg still shows above the body cut (the shoe may be planted up to
            // SneakerPair.PlantMax lower than the leg), the usual 6 mm towards the toes
            float pad = 0.006f + 0.014f * (1f - Smooth(CxMax, CxMax + 0.03f, x));
            ridge[i] = Mathf.Max(M.Sample(M.Dorsum, Mathf.Min(x, M.L)) + pad, SoleTopFlat(x) + boxMin);
        }

        for (int i = 1; i < ridge.Length; i++) ridge[i] = Mathf.Min(ridge[i], ridge[i - 1]);
        for (int pass = 0; pass < 3; pass++) SneakerBuilder.Smooth3(ridge);

        BuildSpine();
    }

    float SecX(int i) => Mathf.Lerp(CxMin, CxMax, (i + 0.5f) / SecN);

    float SecSample(float[] a, float x)
    {
        float f = Mathf.Clamp((x - CxMin) / Mathf.Max(1e-4f, CxMax - CxMin) * SecN - 0.5f, 0f, SecN - 1.001f);
        int i = (int)f;
        return Mathf.Lerp(a[i], a[i + 1], f - i);
    }

    public float SecMed(float x) => SecSample(secMed, x);
    public float SecLat(float x) => SecSample(secLat, x);

    /// <summary>the tongue's centre line: from under the vamp at the throat straight up the eyestay line to the top
    /// eyelets (lifted to clear the instep by 7 mm), then up the leg's front silhouette (7 mm in front of it) to 1.32 x
    /// the ankle height</summary>
    void BuildSpine()
    {
        spine.Clear();
        spineNormal.Clear();
        float top = 1.32f * M.AnkleH + S.CollarRaise;
        float instEye = M.TopH(XEye, 0.005f, 0.03f, Ha + 0.01f);
        float hEye = float.IsNaN(instEye) ? Ha : Mathf.Max(Ha, instEye + 0.007f);
        float h0 = Mathf.Max(Ridge(XOpen) - 0.005f, CutH - 0.004f);
        List<Vector2> pts = new() { new Vector2(XOpen - 0.004f, h0) };
        const int lace = 5;
        for (int j = 1; j <= lace; j++)
        {
            float x = Mathf.Lerp(XOpen - 0.004f, XEye, j / (float)lace);
            float h = Mathf.Lerp(h0, hEye, j / (float)lace);
            float inst = M.TopH(x, 0.005f, 0.03f, hEye);
            if (!float.IsNaN(inst)) h = Mathf.Max(h, inst + 0.012f); // padded tongue over the instep
            pts.Add(new Vector2(x, h));
        }

        if (Env != null) ClearLacedTongue(pts);

        // up the shin: front silhouette + 7 mm, leaning back (x never increases going up)
        float lastX = XEye;
        for (int j = 1; j <= 3; j++)
        {
            float h = Mathf.Lerp(hEye, top, j / 3f);
            float fx = M.FrontX(h, 0.006f, 0.03f);
            float x = float.IsNaN(fx) ? lastX - 0.01f : Mathf.Min(lastX, fx + 0.007f);
            pts.Add(new Vector2(x, Mathf.Max(h, pts[pts.Count - 1].y + 0.003f)));
            lastX = x;
        }

        for (int j = 0; j < pts.Count; j++)
        {
            Vector2 a = pts[Mathf.Max(0, j - 1)], b = pts[Mathf.Min(pts.Count - 1, j + 1)];
            Vector2 t = (b - a).normalized; // throat -> top: x decreases, h increases
            Vector2 n = new(t.y, -t.x); // forward / up
            if (n.x + n.y < 0f) n = -n;
            float s = pts[j].y > CutH + 0.01f ? M.CentreS(pts[j].y, 0.005f) * Smooth(XOpen, XEye, pts[j].x) : 0f;
            spine.Add(new Vector3(pts[j].x, s, pts[j].y));
            spineNormal.Add(new Vector3(n.x, 0f, n.y));
        }
    }

    /// <summary>clearance of the laced tongue over the leg's envelope (its back face at the edges sits 5.5 mm under the
    /// centre line), and the most it may be raised</summary>
    public const float TongueClear = 0.0085f, TongueRaiseMax = 0.02f;

    /// <summary>
    /// Raise the laced part of the tongue (spine points 1..LacePoints, throat -> top eyelets) along its normal where the
    /// leg's envelope over the capture (central band: the skin in front of the ankle rises when the foot points) comes
    /// within TongueClear, capped and spread to the neighbouring points; the throat end stays tucked under the vamp.
    /// </summary>
    void ClearLacedTongue(List<Vector2> pts)
    {
        int last = Mathf.Min(LacePoints, pts.Count - 1);
        float[] need = new float[last + 1];
        Vector2[] nrm = new Vector2[last + 1];
        for (int j = 1; j <= last; j++)
        {
            Vector2 a = pts[j - 1], b = pts[Mathf.Min(last, j + 1)];
            Vector2 t = (b - a).normalized;
            Vector2 n = new(t.y, -t.x);
            if (n.x + n.y < 0f) n = -n;
            nrm[j] = n;
            Vector2 p = pts[j];
            for (int xb = LegEnvelope.XB(p.x - 0.02f); xb <= LegEnvelope.XB(p.x + 0.02f); xb++)
            {
                for (int hb = LegEnvelope.HB(p.y - 0.02f); hb <= LegEnvelope.HB(p.y + 0.03f); hb++)
                {
                    if (!Env.Central[xb * LegEnvelope.NH + hb]) continue;
                    Vector2 c = new(LegEnvelope.XMin + (xb + 0.5f) * LegEnvelope.Cell, (hb + 0.5f) * LegEnvelope.Cell);
                    Vector2 d = c - p;
                    if (Mathf.Abs(Vector2.Dot(d, t)) > 0.006f) continue;
                    need[j] = Mathf.Max(need[j], Vector2.Dot(d, n) + 0.5f * LegEnvelope.Cell + TongueClear);
                }
            }
        }

        // spread (half to the neighbours), cap, apply
        float[] spread = (float[])need.Clone();
        for (int j = 1; j <= last; j++)
        {
            if (j > 1) spread[j] = Mathf.Max(spread[j], 0.5f * need[j - 1]);
            if (j < last) spread[j] = Mathf.Max(spread[j], 0.5f * need[j + 1]);
        }

        for (int j = 1; j <= last; j++)
        {
            float d = Mathf.Min(spread[j], TongueRaiseMax);
            if (d > 0f) pts[j] += nrm[j] * d;
        }

        for (int j = 1; j <= last; j++)
        {
            if (pts[j].y < pts[j - 1].y + 0.003f) pts[j] = new Vector2(pts[j].x, pts[j - 1].y + 0.003f);
            if (pts[j].x > pts[j - 1].x - 0.002f) pts[j] = new Vector2(pts[j - 1].x - 0.002f, pts[j].y);
        }
    }

    /// <summary>height of the tongue's centre above x (the instep part of the spine, throat -> eyelets)</summary>
    public float TongueH(float x)
    {
        int last = Mathf.Min(spine.Count - 1, LacePoints); // the instep part only (the shin part may lean forward)
        for (int j = 1; j <= last; j++)
        {
            Vector3 a = spine[j - 1], b = spine[j];
            if (x <= a.x + 1e-5f && x >= b.x - 1e-5f)
            {
                float t = Mathf.Abs(a.x - b.x) < 1e-6f ? 1f : (a.x - x) / (a.x - b.x);
                return Mathf.Lerp(a.z, b.z, t);
            }
        }

        return x >= spine[0].x ? spine[0].z : spine[last].z;
    }

    /// <summary>spine points 0..LacePoints run over the instep (throat -> top eyelets), the rest up the shin</summary>
    public const int LacePoints = 5;

    /// <summary>upper half-width (foot + margin) on side sigma (+1 medial, -1 lateral)</summary>
    public float W(float x, int sigma) =>
        Mathf.Max(0.016f, sigma > 0 ? M.Sample(M.HalfMed, Mathf.Clamp(x, 0, M.L)) : M.Sample(M.HalfLat, Mathf.Clamp(x, 0, M.L))) + MarginSide;

    static float SafePow(float v, float e) => Mathf.Pow(Mathf.Max(0f, v), e); // cos(pi / 2) is slightly negative in float

    /// <summary>plan outline point (x, s) of column k on side sigma, inflated by e (the sole flare)</summary>
    public Vector2 Outline(int k, int sigma, float e)
    {
        if (k <= Kh)
        {
            float phi = k / (float)Kh * Mathf.PI * 0.5f;
            return new Vector2(Xh - (Rh + e) * Mathf.Cos(phi), sigma * (W(Xh, sigma) + e) * Mathf.Sin(phi));
        }

        if (k <= Kt)
        {
            float x = Mathf.Lerp(Xh, Xt, (k - Kh) / (float)(Kt - Kh));
            return new Vector2(x, sigma * (W(x, sigma) + e));
        }

        float p = (k - Kt) / (float)(K - Kt) * Mathf.PI * 0.5f;
        float c = SafePow(Mathf.Cos(p), 2f / SuperN), sn = SafePow(Mathf.Sin(p), 2f / SuperN);
        return new Vector2(Xt + (Rt + e) * sn, Sc + (sigma * (W(Xt, sigma) + e) - Sc) * c);
    }

    /// <summary>sole flare beyond the upper at x (+ the runner's heel unit bulge)</summary>
    public float Flare(float x) => S.Flare + S.HeelBump * Smooth(M.L * 0.02f, M.L * 0.08f, x) * (1f - Smooth(M.L * 0.22f, M.L * 0.34f, x));

    public float Spring(float x) => S.ToeSpringM * Sq(Smooth(M.BallX, X1, x));
    public float Bevel(float x) => S.HeelBevelM * Sq(1f - Smooth(X0, X0 + 0.07f * M.L, x));
    public float Thick(float x) => Mathf.Lerp(S.SoleHeelM, S.SoleForeM, Smooth(0.2f * M.L, 0.75f * M.L, x));
    public float Bottom(float x) => Spring(x) + Bevel(x);
    float SoleTopFlat(float x) => Spring(x) + Thick(x);

    /// <summary>top of the sole wall (wraps up over the toe bumper and the heel cup)</summary>
    public float SoleTop(float x) => SoleTopFlat(x) + 0.005f * Smooth(X1 - 0.16f * M.L, X1, x) +
                                    0.003f * (1f - Smooth(X0, X0 + 0.10f * M.L, x));

    public float Ridge(float x)
    {
        float f = Mathf.Clamp01((x - XOpen) / (X1 - XOpen)) * (ridge.Length - 1);
        int i = Mathf.Min((int)f, ridge.Length - 2);
        return Mathf.Lerp(ridge[i], ridge[i + 1], f - i);
    }

    /// <summary>collar rim height above the sole plane at rim x (k = 0: the heel back, a little higher for the pull
    /// tab): heel counter, a dip below the ankle bones, rising to the top eyelets, then the eyestays follow the tongue
    /// down the instep to the throat</summary>
    public float Collar(float x, int k)
    {
        float back = (k == 0 ? 1.10f : 1.00f) * Ha;
        float dip = RimDip;
        float eye = Mathf.Max(TongueH(XEye) + 0.002f, dip);
        float h;
        if (x >= XEye) h = Mathf.Max(TongueH(x) + 0.002f, Ridge(XOpen) - 0.003f);
        else if (x <= XBack + 0.02f) h = back;
        else if (x <= M.AnkleX) h = Mathf.Lerp(back, dip, Smooth(XBack + 0.02f, M.AnkleX, x));
        else h = Mathf.Lerp(dip, eye, Smooth(M.AnkleX, XEye, x));
        if (k > 0 && k <= 2) h = Mathf.Max(h, Mathf.Lerp(1.10f * Ha, h, k / 3f)); // the heel tab's shoulders
        return h;
    }

    /// <summary>half-width of the collar opening on side sigma at x: the leg section + margin, an ellipse round the
    /// back, narrowing along the eyestays to the throat</summary>
    public float Opening(float x, int sigma)
    {
        float sec(float xx) => (sigma > 0 ? SecMed(xx) : SecLat(xx)) + OpenMargin;
        if (x <= XBack) return 0f;
        if (x <= OpenXc)
        {
            float u = (OpenXc - x) / (OpenXc - XBack);
            return Mathf.Max(sec(OpenXc) * Mathf.Sqrt(Mathf.Max(0f, 1f - u * u)), x >= CxMin ? sec(x) : 0f);
        }

        if (x <= XEye) return x <= CxMax ? sec(x) : sec(CxMax);
        // along the eyestays: a smooth taper from the top eyelets' gap to the throat (the tongue covers the instep)
        float eye = XEye <= CxMax ? sec(XEye) : sec(CxMax);
        return Mathf.Lerp(eye, 0.008f, Smooth(XEye, XOpen, x));
    }

    /// <summary>rim / ridge point (x, s, h) of column k on side sigma: the collar rim behind the throat, the vamp ridge
    /// in front of it</summary>
    public Vector3 Top(int k, int sigma)
    {
        Vector2 b = Outline(k, sigma, 0f);
        if (k >= KOpen)
        {
            float xr;
            if (k <= Kt) xr = Mathf.Min(b.x, XRidgeEnd);
            else
            {
                float p = (k - Kt) / (float)(K - Kt) * Mathf.PI * 0.5f;
                xr = Mathf.Lerp(Mathf.Min(Xt, XRidgeEnd), XRidgeEnd, Mathf.Sin(p));
            }

            return new Vector3(xr, Sc * Smooth(XOpen, X1, xr), Ridge(xr));
        }

        float rx;
        if (k <= Kh)
        {
            float phi = k / (float)Kh * Mathf.PI * 0.5f;
            rx = OpenXc - (OpenXc - XBack) * Mathf.Cos(phi);
        }
        else
        {
            rx = Mathf.Lerp(OpenXc, XOpen, Mathf.Clamp01((b.x - Xh) / Mathf.Max(1e-4f, XOpen - Xh)));
        }

        float rs = sigma * Opening(rx, sigma);
        // never outside the upper's own base outline (the collar hugs the ankle, it does not flare out)
        float lim = Mathf.Abs(b.y) + 0.003f;
        if (Mathf.Abs(rs) > lim) rs = Mathf.Sign(rs) * lim;
        return new Vector3(rx, rs, Collar(rx, k));
    }

    /// <summary>height of the shoe's top edge above a foot point x: the rim behind the throat, the vamp in front</summary>
    public float CoverTop(float x, float s) => x >= XOpen ? Ridge(Mathf.Min(x, XRidgeEnd)) : Collar(Mathf.Max(x, XBack), 3);

    /// <summary>is a foot point (x, s) inside the upper's plan outline?</summary>
    public bool InsidePlan(float x, float s, float tol = 0.002f)
    {
        if (x < X0 - tol || x > X1 + tol) return false;
        int sigma = s >= 0 ? 1 : -1;
        float prevX = Outline(0, sigma, 0f).x, prevS = 0f;
        for (int k = 1; k <= K; k++)
        {
            Vector2 o = Outline(k, sigma, 0f);
            if (x >= prevX && x <= o.x)
            {
                float t = (x - prevX) / Mathf.Max(1e-6f, o.x - prevX);
                float half = Mathf.Abs(Mathf.Lerp(prevS, o.y, t));
                return Mathf.Abs(s) <= half + tol;
            }

            prevX = o.x;
            prevS = o.y;
        }

        return false;
    }

    static float Sq(float x) => x * x;
}

/// <summary>atlas layout facts the texture painter needs (normalised along the shoe, averaged over both feet)</summary>
public class SneakerAtlasInfo
{
    public float[] ColumnX = new float[SneakerShape.K + 1]; // x / shoe length of every perimeter column (base outline)
    public float KOpen, XnOpen, XnEye, XnBall, XnAnkle;
    public float XnKOpen; // x / shoe length of the first vamp column (where the upper's u switches to x)
}

public class SneakerBuildResult
{
    public Mesh Mesh;
    /// <summary>SmplxAvatar.FootCut with FootHeights: 0 (the heights are relative to the per-vertex cut); without a fit, the
    /// level cut (m relative to the ankle joint, rest pose) CutMargin below the lowest collar rim of both shoes</summary>
    public float FootCut;
    /// <summary>per skin vertex: rest height over its cut (SmplxAvatar.SetFootHeights; null = the level cut)</summary>
    public float[] FootHeights;
    /// <summary>how far the cut runs under the shoe's top edge (m): the largest drop of the collar + CutClear, more if a
    /// cut-edge point came within 4 mm of the top edge on some frame</summary>
    public float CutMarginM;
    public SneakerShape[] Shapes = new SneakerShape[2]; // [0] left, [1] right
    public SolePoint[][] Sole = new SolePoint[2][]; // outsole edge rings (floor contact)
    public Vector3[] BoneRest = new Vector3[SneakerBuilder.Bones]; // per foot: ankle, toe pivot, shin (= the ankle joint) (avatar-local rest)
    public SneakerAtlasInfo Atlas = new();
    public int TrianglesPerShoeMax, VerticesPerShoeMax;
    public int HiddenVertices; // foot vertices (w(ankle) + w(foot) >= 0.5) below the cut
    public int HiddenOutside; // ... of those above the sole top: outside the upper's plan outline or above its top edge
    public int[] FootVertexStart = new int[3]; // mesh vertex ranges: foot f = [start[f], start[f + 1])
    public Vector2Int[] TongueTris = new Vector2Int[2]; // per foot: the tongue's triangle index range [x, y)
    public int[] TongueFront; // the tongues' outer-face triangles of the central strip (both feet)
    public Vector3[] RestVertices; // the mesh's rest vertices / bone weights / triangles / uv / normals (no Mesh API)
    public BoneWeight[] Weights;
    public int[] Triangles;
    public Vector2[] Uv;
    public Vector3[] Normals;

    // the load-time fit to the capture (null without a motion)
    public SneakerFootFit[] Fits;
    public LegSamples[] Legs;
    public float CutExcessMm = float.NaN; // highest cut-edge point relative to (shoe top edge - 4 mm) over the capture
    public float MaxDrop; // largest plant of the collar down the leg (m)
    public int CutPasses;
    public double FitMs, EnvelopeMs, MeshMs;
    public float[] WallPushMaxMm = new float[2]; // largest outward push of the rear upper / eyestays per foot
    public List<string> Notes = new(); // fit diagnostics (the worst cut-edge point of each pass)
}

/// <summary>
/// Procedural skinned sneakers built at load from a dancer's fitted rest feet (Assets/Shoes, VIEWER_SPEC 3.2 shoes):
/// a layered-outline loft (shoe-CAD style) per foot - outsole with a bevelled bottom edge, sole wall with a hard lip,
/// a rounded upper closing over a vamp ridge in front of the throat and rising to a padded collar behind it, an inner
/// lining with a liner below the body cut, a tongue on the leg's front silhouette, lace bars across the eyestays and a
/// heel pull tab - about 1.1-1.3k triangles per shoe, one UV atlas (SneakerTexture), two proxy bones per foot (ankle +
/// toe, the toe pivoting at the outsole under the ball line).
/// </summary>
public static class SneakerBuilder
{
    public const int Ankle = 0, Toe = 1, Shin = 2, PerFoot = 3, Bones = 6; // bone slot per foot: foot f uses PerFoot * f + slot

    public static int Bone(int foot, int slot) => PerFoot * foot + slot;
    const int UpperRings = 6;

    /// <summary>the body's cut edge sits at least this far below the lowest collar rim (SmplxAvatar.FootCut), and
    /// CutClear below the largest drop of the collar down the leg over the capture (the baked plant + pitch; lifting the
    /// shoe only moves the rim away from the cut). Build() lowers it further if a cut-edge point would reach within 4 mm
    /// of the shoe's top edge on any frame</summary>
    public const float CutMargin = 0.020f, CutClear = 0.006f;

    // smoothing groups (normals are averaged across vertices at the same position within a group only)
    const int GSole = 1, GLedge = 2, GUpper = 3, GLining = 4, GTongueF = 5, GTongueB = 6, GLace = 7, GTabF = 8, GTabB = 9, GBottom = 1;

    static readonly int[] LeftIds = { 5770, 5780, 8846 }, RightIds = { 8463, 8474, 8635 }; // big toe, small toe, heel

    // ------------------------------------------------------------------------------------------------ measure

    public static FootMeasure Measure(SmplxData.Skin skin, bool left)
    {
        const int J = SmplxData.Joints;
        FootMeasure m = new() { Left = left, AnkleJoint = left ? 7 : 8, BallJoint = left ? 10 : 11, KneeJoint = left ? 4 : 5 };
        int a = m.AnkleJoint, f = m.BallJoint, kn = m.KneeJoint;
        Vector3[] V = skin.RestVertices;
        float[] w = skin.Weights;
        m.VertexCount = V.Length;
        List<int> foot = new();
        for (int v = 0; v < V.Length; v++)
        {
            if (w[v * J + a] + w[v * J + f] >= 0.5f) foot.Add(v);
        }

        if (foot.Count < 50) throw new InvalidOperationException($"{(left ? "left" : "right")} foot: only {foot.Count} foot vertices");
        m.FootVertices = foot.ToArray();
        m.AnkleRest = skin.RestJoints[a];
        m.BallRest = skin.RestJoints[f];

        // landmarks (SMPL-X vertex ids -> first split vertex); fall back to the foot set's extremes on other topologies
        int[] ids = left ? LeftIds : RightIds;
        Vector3? big = Landmark(skin, ids[0]), small = Landmark(skin, ids[1]), heel = Landmark(skin, ids[2]);
        Vector3 fwd;
        if (big.HasValue && small.HasValue && heel.HasValue)
        {
            fwd = 0.5f * (big.Value + small.Value) - heel.Value;
        }
        else
        {
            fwd = m.BallRest - m.AnkleRest;
            fwd.y = 0;
            fwd.Normalize();
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (int v in foot)
            {
                float x = Vector3.Dot(V[v], fwd);
                if (x < lo) { lo = x; heel = V[v]; }
                if (x > hi) { hi = x; big = V[v]; }
            }

            small = big.Value - Vector3.Cross(Vector3.up, fwd) * 0.01f;
        }

        fwd.y = 0;
        m.Fwd = fwd.normalized;
        Vector3 side = Vector3.Cross(Vector3.up, m.Fwd).normalized;
        m.Med = Vector3.Dot(big.Value - small.Value, side) >= 0 ? side : -side;
        m.Y0 = float.MaxValue;
        foreach (int v in foot) m.Y0 = Mathf.Min(m.Y0, V[v].y);
        float xmin = float.MaxValue, xmax = float.MinValue;
        foreach (int v in foot)
        {
            float x = Vector3.Dot(V[v] - heel.Value, m.Fwd);
            xmin = Mathf.Min(xmin, x);
            xmax = Mathf.Max(xmax, x);
        }

        Vector3 h0 = heel.Value;
        h0.y = m.Y0;
        m.Origin = h0 + m.Fwd * xmin;
        m.L = xmax - xmin;
        Vector3 ball = m.Local(m.BallRest), ankle = m.Local(m.AnkleRest);
        m.BallX = ball.x;
        m.BallH = ball.z;
        m.AnkleX = ankle.x;
        m.AnkleH = ankle.z;

        m.FootLocal = new Vector3[foot.Count];
        for (int i = 0; i < foot.Count; i++) m.FootLocal[i] = m.Local(V[foot[i]]);

        // station half-widths (everything below 7 cm, so the malleoli are enclosed) and dorsum heights
        float win = 0.75f * m.L / FootMeasure.Stations;
        for (int i = 0; i < FootMeasure.Stations; i++)
        {
            float xs = m.StationX(i), med = float.NaN, lat = float.NaN, top = float.NaN;
            foreach (Vector3 p in m.FootLocal)
            {
                if (Mathf.Abs(p.x - xs) > win) continue;
                if (p.z < 0.07f)
                {
                    med = float.IsNaN(med) ? p.y : Mathf.Max(med, p.y);
                    lat = float.IsNaN(lat) ? -p.y : Mathf.Max(lat, -p.y);
                }

                top = float.IsNaN(top) ? p.z : Mathf.Max(top, p.z);
            }

            m.HalfMed[i] = med;
            m.HalfLat[i] = lat;
            m.Dorsum[i] = top;
        }

        FillGaps(m.HalfMed, 0.03f);
        FillGaps(m.HalfLat, 0.03f);
        FillGaps(m.Dorsum, 0.03f);
        for (int pass = 0; pass < 2; pass++)
        {
            Smooth3(m.HalfMed);
            Smooth3(m.HalfLat);
            Smooth3(m.Dorsum);
        }

        // this leg's lower surface (ankle / foot / knee): the cut section, the front silhouette for the tongue. SMPL-X is
        // coarse round the ankle (1-2 cm between vertices), so the triangles' edge midpoints and centres join the vertices
        List<Vector3> leg = new();
        bool[] isLeg = new bool[V.Length];
        for (int v = 0; v < V.Length; v++)
        {
            if (w[v * J + a] + w[v * J + f] + w[v * J + kn] < 0.5f) continue;
            Vector3 p = m.Local(V[v]);
            if (p.z >= 0.25f) continue;
            isLeg[v] = true;
            leg.Add(p);
        }

        int[] T = skin.Triangles;
        for (int t = 0; t + 2 < T.Length; t += 3)
        {
            int i0 = T[t], i1 = T[t + 1], i2 = T[t + 2];
            if (!isLeg[i0] || !isLeg[i1] || !isLeg[i2]) continue;
            Vector3 p0 = m.Local(V[i0]), p1 = m.Local(V[i1]), p2 = m.Local(V[i2]);
            leg.Add(0.5f * (p0 + p1));
            leg.Add(0.5f * (p1 + p2));
            leg.Add(0.5f * (p2 + p0));
            leg.Add((p0 + p1 + p2) / 3f);
        }

        m.LegLocal = leg.ToArray();
        m.XEye = m.FrontX(m.AnkleH, 0.006f, 0.03f);
        float sLo = float.MaxValue, sHi = float.MinValue;
        foreach (Vector3 p in m.LegLocal)
        {
            if (Mathf.Abs(p.z - m.AnkleH) > 0.012f || Mathf.Abs(p.x - m.AnkleX) > 0.035f) continue;
            sLo = Mathf.Min(sLo, p.y);
            sHi = Mathf.Max(sHi, p.y);
        }

        m.LegCs = sHi >= sLo ? 0.5f * (sLo + sHi) : ankle.y;

        // behind the ball the outline also holds the leg's own section up to the ankle joint (the ankle bones): a shoe
        // lifted out of a sunken heel carries the leg down into the sole's height
        for (int i = 0; i < FootMeasure.Stations; i++)
        {
            float xs = m.StationX(i);
            if (xs > m.BallX - 0.03f) continue;
            foreach (Vector3 p in m.LegLocal)
            {
                if (Mathf.Abs(p.x - xs) > win || p.z > m.AnkleH || p.z < 0.01f) continue;
                m.HalfMed[i] = Mathf.Max(m.HalfMed[i], p.y);
                m.HalfLat[i] = Mathf.Max(m.HalfLat[i], -p.y);
            }
        }

        for (int pass = 0; pass < 2; pass++)
        {
            Smooth3(m.HalfMed);
            Smooth3(m.HalfLat);
        }

        // toe flexion axis: horizontal, across the foot, signed so a positive angle lifts the toes
        Vector3 axis = Vector3.Cross(Vector3.up, m.Fwd).normalized;
        if ((Quaternion.AngleAxis(10f, axis) * m.Fwd).y < 0f) axis = -axis;
        m.FlexAxis = axis;
        return m;
    }

    static Vector3? Landmark(SmplxData.Skin skin, int smplxId)
    {
        if (skin.VertexIds == null) return smplxId < skin.RestVertices.Length && skin.RestVertices.Length == 10475 ? skin.RestVertices[smplxId] : null;
        for (int i = 0; i < skin.VertexIds.Length; i++)
        {
            if (skin.VertexIds[i] == smplxId) return skin.RestVertices[i];
        }

        return null;
    }

    public static void FillGaps(float[] a, float fallback)
    {
        int n = a.Length;
        for (int i = 0; i < n; i++)
        {
            if (!float.IsNaN(a[i])) continue;
            int lo = i - 1, hi = i + 1;
            while (lo >= 0 && float.IsNaN(a[lo])) lo--;
            while (hi < n && float.IsNaN(a[hi])) hi++;
            a[i] = lo >= 0 && hi < n ? Mathf.Lerp(a[lo], a[hi], (i - lo) / (float)(hi - lo)) : lo >= 0 ? a[lo] : hi < n ? a[hi] : fallback;
        }
    }

    public static void Smooth3(float[] a)
    {
        float prev = a[0];
        for (int i = 1; i < a.Length - 1; i++)
        {
            float cur = a[i];
            a[i] = 0.25f * prev + 0.5f * cur + 0.25f * a[i + 1];
            prev = cur;
        }
    }

    // ------------------------------------------------------------------------------------------------ build

    class Acc
    {
        public readonly List<Vector3> V = new();
        public readonly List<Vector2> UV = new();
        public readonly List<BoneWeight> W = new();
        public readonly List<int> G = new();
        public readonly List<int> T = new();
        public int TongueT0, TongueT1; // triangle range of the last shoe's tongue
        public readonly List<int> TongueFront = new(); // the tongue's outer-face triangles of its central strip (all shoes)

        public int Add(Vector3 p, Vector2 uv, BoneWeight w, int group)
        {
            V.Add(p);
            UV.Add(uv);
            W.Add(w);
            G.Add(group);
            return V.Count - 1;
        }

        /// <summary>triangle oriented so its (Unity front-face) normal points along hint</summary>
        public void Tri(int a, int b, int c, Vector3 hint)
        {
            if (a == b || b == c || a == c) return;
            Vector3 n = Vector3.Cross(V[b] - V[a], V[c] - V[a]);
            if (n.sqrMagnitude < 1e-16f) return;
            if (Vector3.Dot(n, hint) >= 0f)
            {
                T.Add(a); T.Add(b); T.Add(c);
            }
            else
            {
                T.Add(a); T.Add(c); T.Add(b);
            }
        }

        public void Quad(int a, int b, int c, int d, Vector3 hint)
        {
            // split along the shorter diagonal
            if ((V[a] - V[c]).sqrMagnitude <= (V[b] - V[d]).sqrMagnitude)
            {
                Tri(a, b, c, hint);
                Tri(a, c, d, hint);
            }
            else
            {
                Tri(a, b, d, hint);
                Tri(b, c, d, hint);
            }
        }
    }

    public static SneakerBuildResult Build(FootMeasure left, FootMeasure right, SneakerStyle style) => Build(left, right, style, null, null, null);

    /// <summary>
    /// Both shoes as one skinned mesh (avatar-local rest coordinates; bones: 0 L ankle, 1 L toe, 2 R ankle, 3 R toe;
    /// bindposes = translation to the bone's rest position) + the avatar foot cut + the outsole contact rings. With the
    /// dancer's skin and motion the shoes are fitted to the capture (SneakerFit): floor contact baked per frame, the foot
    /// cut below the largest plant, the vamp / tongue / rear upper / collar sized to the visible leg over every frame.
    /// </summary>
    public static SneakerBuildResult Build(FootMeasure left, FootMeasure right, SneakerStyle style, SmplxData.Skin skin,
        SneakerMotion motion, SneakerFitSettings fit) => Build(left, right, style, skin, motion, fit, true);

    /// <param name="createMesh">false: pure C# (a worker thread may run it); CreateMesh on the main thread later</param>
    public static SneakerBuildResult Build(FootMeasure left, FootMeasure right, SneakerStyle style, SmplxData.Skin skin,
        SneakerMotion motion, SneakerFitSettings fit, bool createMesh)
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        SneakerBuildResult res = new();
        FootMeasure[] feet = { left, right };
        for (int f = 0; f < 2; f++)
        {
            FootMeasure m = feet[f];
            res.Shapes[f] = new SneakerShape(m, style);
            res.BoneRest[Bone(f, Ankle)] = m.AnkleRest;
            res.BoneRest[Bone(f, Toe)] = m.Rest(m.BallX, 0f, 0f); // the toe pivots at the outsole under the ball line
            res.BoneRest[Bone(f, Shin)] = m.AnkleRest; // the shin turns about the ankle joint
            res.Sole[f] = SoleRing(res.Shapes[f]);
        }

        // floor contact over the capture (needs only the sole), then the cut below the largest plant of the collar
        float drop = 0f;
        if (motion != null)
        {
            fit ??= new SneakerFitSettings();
            res.Fits = new SneakerFootFit[2];
            for (int f = 0; f < 2; f++)
            {
                res.Fits[f] = SneakerFit.Bake(motion, feet[f], res.Shapes[f], res.Sole[f], res.BoneRest[Bone(f, Ankle)], res.BoneRest[Bone(f, Toe)], fit);
                drop = Mathf.Max(drop, res.Fits[f].MaxDrop);
            }
        }

        res.MaxDrop = drop;
        float margin = Mathf.Max(CutMargin, drop + CutClear);
        res.FitMs = sw.Elapsed.TotalMilliseconds;

        if (motion != null && skin != null)
        {
            // the body is cut along the shoe's top edge, margin under it round the collar (the instep under the tongue
            // and the vamp is hidden). The collar's cut edge must stay 4 mm under the top edge on every frame: widen the
            // margin by any excess (only the cut edge is posed for that); then the visible leg over the capture once
            for (int f = 0; f < 2; f++)
            {
                res.Shapes[f].Env = null;
                res.Shapes[f].SetCut(res.Shapes[f].RimDip - margin);
            }

            MaskParts(skin, feet, res.Shapes, out float[] baseH, out float[] share);
            float hTop = LegTop(res.Shapes);
            res.Legs = new LegSamples[2];
            for (int f = 0; f < 2; f++) res.Legs[f] = LegSamples.Build(skin, f == 0, baseH, share, MarginMax, hTop, margin);
            for (int pass = 0; pass < 3; pass++)
            {
                float excess = CutExcess(res, feet, motion, out string worst);
                res.CutPasses = pass + 1;
                res.CutExcessMm = excess * 1000f;
                res.Notes.Add($"margin {margin:0.0000}: {worst}");
                if (excess <= 0f || pass == 2 || margin >= MarginMax) break;
                margin = Mathf.Min(MarginMax, margin + excess + 0.001f);
                foreach (LegSamples l in res.Legs) l.SetMargin(margin);
            }

            Envelopes(res, feet, motion);

            // the final shape (the laced tongue cleared off the envelope); the mask and the leg samples follow it
            for (int f = 0; f < 2; f++) res.Shapes[f].SetCut(res.Shapes[f].RimDip - margin);
            MaskParts(skin, feet, res.Shapes, out baseH, out share);
            res.FootHeights = new float[baseH.Length];
            for (int v = 0; v < baseH.Length; v++) res.FootHeights[v] = baseH[v] + margin * share[v];
            for (int f = 0; f < 2; f++) res.Legs[f] = LegSamples.Build(skin, f == 0, baseH, share, MarginMax, hTop, margin);
            res.FootCut = 0f;
            res.CutMarginM = margin;
        }
        else
        {
            // no motion: the level cut CutMargin below the lowest collar rim of both shoes
            float cut = 0f;
            for (int f = 0; f < 2; f++) cut = Mathf.Min(cut, res.Shapes[f].RimDip - margin - feet[f].AnkleH);
            res.FootCut = Mathf.Clamp(cut, -0.07f, -0.005f);
            res.CutMarginM = margin;
            for (int f = 0; f < 2; f++) res.Shapes[f].SetCut(feet[f].AnkleH + res.FootCut);
        }

        res.EnvelopeMs = sw.Elapsed.TotalMilliseconds - res.FitMs;

        Acc acc = new();
        for (int f = 0; f < 2; f++)
        {
            int v0 = acc.V.Count, t0 = acc.T.Count;
            res.FootVertexStart[f] = v0;
            res.WallPushMaxMm[f] = BuildShoe(acc, res.Shapes[f], PerFoot * f) * 1000f;
            res.TongueTris[f] = new Vector2Int(acc.TongueT0, acc.TongueT1);
            res.TrianglesPerShoeMax = Mathf.Max(res.TrianglesPerShoeMax, (acc.T.Count - t0) / 3);
            res.VerticesPerShoeMax = Mathf.Max(res.VerticesPerShoeMax, acc.V.Count - v0);
        }

        res.FootVertexStart[2] = acc.V.Count;
        res.TongueFront = acc.TongueFront.ToArray();

        // atlas facts (both feet averaged)
        SneakerAtlasInfo info = res.Atlas;
        for (int k = 0; k <= SneakerShape.K; k++)
        {
            float x = 0f;
            foreach (SneakerShape sh in res.Shapes) x += (sh.Outline(k, -1, 0f).x - sh.X0) / (sh.X1 - sh.X0);
            info.ColumnX[k] = x / 2f;
        }

        foreach (SneakerShape sh in res.Shapes)
        {
            float len = sh.X1 - sh.X0;
            info.KOpen += 0.5f * sh.KOpen;
            info.XnKOpen += 0.5f * (sh.Outline(sh.KOpen, 1, 0f).x - sh.X0) / len;
            info.XnOpen += 0.5f * (sh.XOpen - sh.X0) / len;
            info.XnEye += 0.5f * (sh.XEye - sh.X0) / len;
            info.XnBall += 0.5f * (sh.M.BallX - sh.X0) / len;
            info.XnAnkle += 0.5f * (sh.M.AnkleX - sh.X0) / len;
        }

        res.RestVertices = acc.V.ToArray();
        res.Weights = acc.W.ToArray();
        res.Triangles = acc.T.ToArray();
        res.Uv = acc.UV.ToArray();
        res.Normals = SmoothNormals(acc).ToArray();
        if (createMesh) res.Mesh = CreateMesh(res);

        // rest check of the cut: every foot vertex below it that is above the sole top must lie inside the upper
        for (int f = 0; f < 2; f++)
        {
            FootMeasure m = feet[f];
            SneakerShape sh = res.Shapes[f];
            for (int i = 0; i < m.FootLocal.Length; i++)
            {
                Vector3 p = m.FootLocal[i];
                if (p.z >= (res.FootHeights != null ? sh.CutAt(p.x, p.y, res.CutMarginM) : sh.CutH)) continue;
                res.HiddenVertices++;
                if (p.z > sh.SoleTop(p.x) && (!sh.InsidePlan(p.x, p.y, 0.004f) || p.z > sh.CoverTop(p.x, p.y) + 0.002f)) res.HiddenOutside++;
            }
        }

        res.MeshMs = sw.Elapsed.TotalMilliseconds - res.FitMs - res.EnvelopeMs;
        return res;
    }

    /// <summary>the result's mesh (main thread): one submesh, 6 bones, bindposes = translation to each bone's rest</summary>
    public static Mesh CreateMesh(SneakerBuildResult res)
    {
        Mesh mesh = new() { name = "Sneakers", indexFormat = IndexFormat.UInt16 };
        mesh.SetVertices(new List<Vector3>(res.RestVertices));
        mesh.SetUVs(0, new List<Vector2>(res.Uv));
        // HM_AvatarLit's foot-mask channel: +1 = never clipped (the shoe is not a foot)
        List<Vector2> keep = new(res.RestVertices.Length);
        for (int i = 0; i < res.RestVertices.Length; i++) keep.Add(new Vector2(1f, 0f));
        mesh.SetUVs(1, keep);
        mesh.SetTriangles(new List<int>(res.Triangles), 0, false);
        mesh.SetNormals(new List<Vector3>(res.Normals));
        mesh.boneWeights = res.Weights;
        Matrix4x4[] bind = new Matrix4x4[Bones];
        for (int b = 0; b < Bones; b++) bind[b] = Matrix4x4.Translate(-res.BoneRest[b]);
        mesh.bindposes = bind;
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>widest the cut margin under the shoe's top edge may get (m)</summary>
    public const float MarginMax = 0.05f;

    /// <summary>
    /// The per-vertex foot mask (SmplxAvatar.SetFootHeights) in two parts: each lower-leg vertex's rest height over its
    /// side's shoe top edge at its own (x, s) (collar rim, tongue, vamp) and the share of the cut margin that applies
    /// there (SneakerShape.MarginShare); the mask is base + margin * share. Vertices off the lower legs get base +1 and
    /// share 0 (never cut), like the level mask.
    /// </summary>
    public static void MaskParts(SmplxData.Skin skin, FootMeasure[] feet, SneakerShape[] shapes, out float[] baseH, out float[] share)
    {
        const int J = SmplxData.Joints;
        int n = skin.RestVertices.Length;
        baseH = new float[n];
        share = new float[n];
        for (int v = 0; v < n; v++)
        {
            int o = v * J;
            float wl = skin.Weights[o + 4] + skin.Weights[o + 7] + skin.Weights[o + 10];
            float wr = skin.Weights[o + 5] + skin.Weights[o + 8] + skin.Weights[o + 11];
            if (wl + wr < 0.25f)
            {
                baseH[v] = 1f;
                continue;
            }

            int f = wl >= wr ? 0 : 1;
            Vector3 l = feet[f].Local(skin.RestVertices[v]);
            baseH[v] = l.z - shapes[f].CoverTop(l.x, l.y);
            share[v] = shapes[f].MarginShare(l.x);
        }
    }

    /// <summary>the highest mask height a leg sample needs (m over the cut): the collar / tongue top over the cut + the
    /// lift that brings higher leg down into the shoe</summary>
    static float LegTop(SneakerShape[] shapes)
    {
        float hTop = 0f;
        foreach (SneakerShape sh in shapes) hTop = Mathf.Max(hTop, Mathf.Max(1.12f * sh.Ha, 1.35f * sh.M.AnkleH + sh.S.CollarRaise) + 0.004f - sh.CutH);
        return hTop + 0.04f + 0.04f;
    }

    /// <summary>
    /// The worst height of a collar cut-edge point (where the full margin applies) over (the shoe's top edge - 4 mm)
    /// over every frame - positive means the cut body would show above the collar. Poses only the cut edges' vertices.
    /// </summary>
    static float CutExcess(SneakerBuildResult res, FootMeasure[] feet, SneakerMotion mo, out string worst)
    {
        const int J = SmplxData.Joints;
        float excess = float.NegativeInfinity;
        worst = null;
        for (int f = 0; f < 2; f++)
        {
            FootMeasure m = feet[f];
            SneakerShape sh = res.Shapes[f];
            LegSamples legs = res.Legs[f];
            List<int> edges = new();
            for (int e = 0; e < legs.EdgeA.Length; e++)
            {
                if (legs.EdgeCut[e] && sh.MarginShare(legs.RestX(e, m)) >= 0.999f) edges.Add(e);
            }

            Vector3[] posed = new Vector3[legs.Count];
            Vector3 aRest = res.BoneRest[Bone(f, Ankle)];
            for (int k = 0; k < mo.Frames; k++)
            {
                int o = k * J + m.AnkleJoint;
                res.Fits[f].Proxy(k, mo.P[o], mo.R[o], out Vector3 pc, out Quaternion rc);
                legs.PoseInto(mo, k, pc, rc, aRest, posed, legs.CutVertices);
                foreach (int e in edges)
                {
                    Vector3 l = m.Local(legs.Edge(posed, e));
                    float ex = l.z - (sh.CoverTop(l.x, l.y) - 0.004f);
                    if (ex <= excess) continue;
                    excess = ex;
                    worst = $"foot {f} f{k} x {l.x:0.000} s {l.y:0.000} h {l.z:0.000} top {sh.CoverTop(l.x, l.y):0.000} (shift {res.Fits[f].Shift[k]:0.000} pitch {res.Fits[f].Pitch[k]:0.0})";
                }
            }
        }

        return excess;
    }

    /// <summary>
    /// The visible lower leg of each side in its shoe's frame over every frame (each shape's Env): visible vertices, the
    /// cut edge, the visible edges' midpoints and the visible triangles' centres.
    /// </summary>
    static void Envelopes(SneakerBuildResult res, FootMeasure[] feet, SneakerMotion mo)
    {
        const int J = SmplxData.Joints;
        for (int f = 0; f < 2; f++)
        {
            FootMeasure m = feet[f];
            SneakerShape sh = res.Shapes[f];
            LegSamples legs = res.Legs[f];
            LegEnvelope env = new(sh.LegAxis.x, sh.LegAxis.y);
            Vector3[] posed = new Vector3[legs.Count];
            Vector3 aRest = res.BoneRest[Bone(f, Ankle)];
            for (int k = 0; k < mo.Frames; k++)
            {
                int o = k * J + m.AnkleJoint;
                res.Fits[f].Proxy(k, mo.P[o], mo.R[o], out Vector3 pc, out Quaternion rc);
                legs.PoseInto(mo, k, pc, rc, aRest, posed);
                for (int i = 0; i < legs.Count; i++)
                {
                    if (legs.H[i] >= 0f) env.Add(m.Local(posed[i]));
                }

                for (int e = 0; e < legs.EdgeA.Length; e++) env.Add(m.Local(legs.Edge(posed, e)));
                for (int t = 0; t < legs.TriA.Length; t++) env.Add(m.Local((posed[legs.TriA[t]] + posed[legs.TriB[t]] + posed[legs.TriC[t]]) / 3f));
            }

            sh.Env = env;
        }
    }

    /// <summary>the outsole edge as contact points: the bottom edge, the bevel and the wall foot of every perimeter
    /// column (the same points as the sole mesh's lowest rings)</summary>
    public static SolePoint[] SoleRing(SneakerShape sh)
    {
        FootMeasure m = sh.M;
        int[] sides = { 1, -1 };
        List<SolePoint> pts = new();
        foreach (int sg in sides)
        {
            for (int k = 0; k <= SneakerShape.K; k++)
            {
                SoleRingGeo(sh, k, sg, out Vector2 o, out Vector2 nIn, out float bot, out _);
                for (int r = 0; r < 3; r++)
                {
                    (Vector2 off, float h) = SoleRingOffset(r, nIn, bot, 0f, false, 0f);
                    Vector2 p = o + off;
                    pts.Add(new SolePoint
                    {
                        Rest = m.Rest(p.x, p.y, h), ToeWeight = SneakerShape.Smooth(m.BallX - 0.02f, m.BallX + 0.02f, p.x),
                        Fore = p.x >= 0.55f * m.L
                    });
                }
            }
        }

        return pts.ToArray();
    }

    const float SoleBevelR = 0.004f;

    static void SoleRingGeo(SneakerShape sh, int k, int sg, out Vector2 o, out Vector2 nIn, out float bot, out float top)
    {
        o = sh.Outline(k, sg, sh.Flare(sh.Outline(k, sg, 0f).x));
        nIn = PlanNormal(sh, k, sg) * -1f;
        bot = sh.Bottom(o.x);
        top = sh.SoleTop(o.x);
    }

    /// <summary>sole wall ring r (0 bottom edge inset by the bevel radius, 1 bevel, 2 wall foot, 3 band / groove, 4
    /// top): plan offset from the outline, height</summary>
    static (Vector2 off, float h) SoleRingOffset(int r, Vector2 nIn, float bot, float top, bool platform, float groove)
    {
        const float rb = SoleBevelR;
        return r switch
        {
            0 => (nIn * rb, bot),
            1 => (nIn * (rb * (1f - Mathf.Cos(Mathf.PI * 0.25f))), bot + rb * (1f - Mathf.Sin(Mathf.PI * 0.25f))),
            2 => (Vector2.zero, bot + rb),
            3 => (nIn * (platform ? 0.0012f : 0.0006f), Mathf.Lerp(bot, top, groove)),
            _ => (Vector2.zero, top)
        };
    }

    static BoneWeight Weight(SneakerShape sh, int bone0, float x)
    {
        float b = SneakerShape.Smooth(sh.M.BallX - 0.02f, sh.M.BallX + 0.02f, x);
        return new BoneWeight { boneIndex0 = bone0 + Ankle, weight0 = 1f - b, boneIndex1 = bone0 + Toe, weight1 = b };
    }

    static BoneWeight Rigid(int bone0) => new() { boneIndex0 = bone0 + Ankle, weight0 = 1f };

    static BoneWeight ShinWeight(int bone0, float w) => w <= 0f ? Rigid(bone0) :
        new BoneWeight { boneIndex0 = bone0 + Ankle, weight0 = 1f - w, boneIndex1 = bone0 + Shin, weight1 = w };

    /// <summary>upper atlas u (0..1): the perimeter column behind the throat (heel tab, counter and eyestays keep their
    /// resolution round the heel), the vertex's own x over the closed vamp (the column tops converge on the ridge)</summary>
    public static float UpperU(SneakerShape sh, int k, float x)
    {
        float uo = sh.KOpen / (float)SneakerShape.K;
        if (k < sh.KOpen) return k / (float)SneakerShape.K;
        float xo = sh.Outline(sh.KOpen, 1, 0f).x;
        return uo + (1f - uo) * Mathf.Clamp01((x - xo) / Mathf.Max(1e-4f, sh.X1 - xo));
    }

    static float Ease(float t) => Mathf.Sin(t * Mathf.PI * 0.5f);

    /// <summary>one shoe into the accumulator; bone0 = PerFoot * foot. Returns the largest outward push of the
    /// rear upper (m) that the leg's envelope asked for</summary>
    static float BuildShoe(Acc acc, SneakerShape sh, int bone0)
    {
        FootMeasure m = sh.M;
        SneakerStyle st = sh.S;
        const int K = SneakerShape.K;
        int[] sides = { 1, -1 }; // medial strip, lateral strip
        float len = sh.X1 - sh.X0;
        Vector3 centre = m.Rest(0.5f * (sh.X0 + sh.X1), 0f, 0.5f * sh.Ha);
        Vector3 up = Vector3.up;
        Vector3 ToRest(Vector3 d) => m.Fwd * d.x + m.Med * d.y + up * d.z; // foot-frame direction -> rest space

        // ---- sole wall: rings 0 bottom-edge (inset by the bevel radius), 1 bevel, 2 wall foot, 3 band / groove, 4 top
        const float rb = 0.004f;
        int[,,] sole = new int[2, K + 1, 5];
        for (int si = 0; si < 2; si++)
        {
            int sg = sides[si];
            for (int k = 0; k <= K; k++)
            {
                Vector2 o = sh.Outline(k, sg, sh.Flare(sh.Outline(k, sg, 0f).x));
                Vector2 nIn = PlanNormal(sh, k, sg) * -1f;
                float x = o.x, bot = sh.Bottom(x), top = sh.SoleTop(x);
                float groove = Mathf.Clamp(st.Platform ? 0.45f : st.OutsoleBandM / Mathf.Max(0.01f, top - bot), 0.15f, 0.6f);
                (Vector2 off, float h, float hf)[] ring =
                {
                    (nIn * rb, bot, 0f),
                    (nIn * (rb * (1f - Mathf.Cos(Mathf.PI * 0.25f))), bot + rb * (1f - Mathf.Sin(Mathf.PI * 0.25f)), 0.04f),
                    (Vector2.zero, bot + rb, 0.1f),
                    (nIn * (st.Platform ? 0.0012f : 0.0006f), Mathf.Lerp(bot, top, groove), groove),
                    (Vector2.zero, top, 1f)
                };
                for (int r = 0; r < 5; r++)
                {
                    Vector2 p = o + ring[r].off;
                    Vector2 uv = new(Mathf.Lerp(0.625f, 0.855f, k / (float)K), si == 0 ? Mathf.Lerp(0.01f, 0.49f, ring[r].hf) : Mathf.Lerp(0.51f, 0.99f, ring[r].hf));
                    sole[si, k, r] = acc.Add(m.Rest(p.x, p.y, ring[r].h), uv, Weight(sh, bone0, p.x), GSole);
                }
            }

            for (int k = 0; k < K; k++)
            {
                for (int r = 0; r < 4; r++)
                {
                    Vector3 hint = Outward(acc, sole[si, k, r], centre, 0.3f);
                    acc.Quad(sole[si, k, r], sole[si, k + 1, r], sole[si, k + 1, r + 1], sole[si, k, r + 1], hint);
                }
            }
        }

        // ---- outsole bottom: strips across between the medial and lateral bottom-edge rings; own vertices at the same
        // positions (planar UVs on the outsole swatch, normals smoothed with the bevel: same group)
        int[,] bottom = new int[2, K + 1];
        float smin = float.MaxValue, smax = float.MinValue;
        for (int si = 0; si < 2; si++)
        {
            for (int k = 0; k <= K; k++)
            {
                Vector3 l = m.Local(acc.V[sole[si, k, 0]]);
                smin = Mathf.Min(smin, l.y);
                smax = Mathf.Max(smax, l.y);
            }
        }

        for (int si = 0; si < 2; si++)
        {
            for (int k = 0; k <= K; k++)
            {
                Vector3 p = acc.V[sole[si, k, 0]];
                Vector3 l = m.Local(p);
                Vector2 uv = new(Mathf.Lerp(0.865f, 0.995f, (l.x - sh.X0) / len), Mathf.Lerp(0.01f, 0.48f, (l.y - smin) / Mathf.Max(1e-4f, smax - smin)));
                bottom[si, k] = acc.Add(p, uv, acc.W[sole[si, k, 0]], GBottom);
            }
        }

        for (int k = 0; k < K; k++) acc.Quad(bottom[0, k], bottom[0, k + 1], bottom[1, k + 1], bottom[1, k], -up);

        // ---- ledge: the sole's top lip from the sole outline in to the upper's base (hard edges both sides)
        int[,,] ledge = new int[2, K + 1, 2];
        for (int si = 0; si < 2; si++)
        {
            int sg = sides[si];
            for (int k = 0; k <= K; k++)
            {
                Vector2 b = sh.Outline(k, sg, 0f);
                Vector2 o = sh.Outline(k, sg, sh.Flare(b.x));
                Vector2 uvA = new(Mathf.Lerp(0.625f, 0.855f, k / (float)K), si == 0 ? 0.485f : 0.985f);
                ledge[si, k, 0] = acc.Add(m.Rest(o.x, o.y, sh.SoleTop(o.x)), uvA, Weight(sh, bone0, o.x), GLedge);
                ledge[si, k, 1] = acc.Add(m.Rest(b.x, b.y, sh.SoleTop(b.x) + 0.0008f), uvA, Weight(sh, bone0, b.x), GLedge);
            }

            for (int k = 0; k < K; k++) acc.Quad(ledge[si, k, 0], ledge[si, k + 1, 0], ledge[si, k + 1, 1], ledge[si, k, 1], up);
        }

        // ---- upper: per column from the base (tucked 2 mm into the sole) to the rim (rear) or the vamp ridge (front);
        // the ridge vertex is shared by the medial and lateral strips (a closed, smooth vamp). The rear columns are
        // pushed out where the leg reaches over the capture (a padded collar round the ankle bones)
        int[,,] upper = new int[2, K + 1, UpperRings];
        Vector3[,,] wall = new Vector3[2, K + 1, UpperRings]; // (x, s, h)
        for (int si = 0; si < 2; si++)
        {
            int sg = sides[si];
            for (int k = 0; k <= K; k++)
            {
                Vector2 b = sh.Outline(k, sg, 0f);
                Vector3 top = sh.Top(k, sg);
                bool front = k >= sh.KOpen;
                float hb = sh.SoleTop(b.x) - 0.002f;
                Vector2 nOut = PlanNormal(sh, k, sg);
                for (int r = 0; r < UpperRings; r++)
                {
                    float t = r / (UpperRings - 1f);
                    float a = front ? t * t : Mathf.Pow(t, 1.6f);
                    float hgt = front ? Ease(t) : t;
                    Vector2 plan = Vector2.Lerp(b, new Vector2(top.x, top.y), a) + nOut * (0.002f * Mathf.Sin(Mathf.PI * t) * (1f - t));
                    wall[si, k, r] = new Vector3(plan.x, plan.y, Mathf.Lerp(hb, top.z, hgt));
                }
            }
        }

        float[] eyePush = new float[2];
        float pushMax = PushUpper(sh, wall, eyePush);
        int[] ridgeV = new int[K + 1];
        for (int k = 0; k <= K; k++) ridgeV[k] = -1;
        for (int si = 0; si < 2; si++)
        {
            for (int k = 0; k <= K; k++)
            {
                bool front = k >= sh.KOpen;
                for (int r = 0; r < UpperRings; r++)
                {
                    float t = r / (UpperRings - 1f);
                    float v = si == 0 ? Mathf.Lerp(0.01f, 0.5f, t) : Mathf.Lerp(0.99f, 0.5f, t);
                    if (front && r == UpperRings - 1 && ridgeV[k] >= 0)
                    {
                        upper[si, k, r] = ridgeV[k];
                        continue;
                    }

                    Vector3 w = wall[si, k, r];
                    Vector2 uv = new(Mathf.Lerp(0.005f, 0.615f, UpperU(sh, k, w.x)), v);
                    int id = acc.Add(m.Rest(w.x, w.y, w.z), uv, Weight(sh, bone0, w.x), GUpper);
                    upper[si, k, r] = id;
                    if (front && r == UpperRings - 1) ridgeV[k] = id;
                }
            }

            for (int k = 0; k < K; k++)
            {
                for (int r = 0; r < UpperRings - 1; r++)
                {
                    Vector3 hint = Outward(acc, upper[si, k, r], centre, 0.6f);
                    acc.Quad(upper[si, k, r], upper[si, k + 1, r], upper[si, k + 1, r + 1], upper[si, k, r + 1], hint);
                }
            }
        }

        // ---- collar roll + lining: rear columns only, rolling over the rim towards the opening and down inside; around
        // the leg the lining reaches below the body cut, along the eyestays it is a short facing over the tongue
        int kOpen = sh.KOpen;
        int kmax = Mathf.Min(kOpen, K);
        int kEye = 0;
        for (int k = 0; k <= kmax; k++)
        {
            if (sh.Top(k, 1).x <= sh.XEye + 0.004f) kEye = k;
        }

        int[][,] rolls = new int[2][,];
        for (int si = 0; si < 2; si++)
        {
            int[,] roll = rolls[si] = new int[kOpen + 1, 4];
            for (int k = 0; k <= kmax; k++)
            {
                int rim = upper[si, k, UpperRings - 1];
                Vector3 p = acc.V[rim];
                Vector3 l = m.Local(p);
                // inward = towards the opening's centre line (across the foot along the eyestays)
                Vector3 dl = k <= sh.KOpen - 1 && l.x > sh.OpenXc ? new Vector3(0f, -Mathf.Sign(l.y), 0f) : new Vector3(sh.OpenXc - l.x, -l.y, 0f);
                Vector3 d = ToRest(dl.sqrMagnitude > 1e-10f ? dl.normalized : new Vector3(-1f, 0f, 0f));
                float depth = k <= kEye ? Mathf.Max(0.012f, l.z - (sh.CutH - 0.005f)) : 0.008f;
                Vector3[] pts =
                {
                    p + d * 0.0035f + up * 0.0025f,
                    p + d * 0.0075f + up * 0.0005f,
                    p + d * 0.0085f - up * 0.006f,
                    p + d * 0.0085f - up * depth
                };
                for (int r = 0; r < 4; r++)
                {
                    Vector2 uv = r < 2 || k > kEye
                        ? new Vector2(Mathf.Lerp(0.005f, 0.615f, UpperU(sh, k, m.Local(pts[r]).x)), si == 0 ? 0.5f - 0.004f * (r + 1) : 0.5f + 0.004f * (r + 1))
                        : new Vector2(Mathf.Lerp(0.865f, 0.925f, k / (float)K), r == 2 ? 0.745f : 0.505f);
                    roll[k, r] = acc.Add(pts[r], uv, Rigid(bone0), r < 2 || k > kEye ? GUpper : GLining);
                }
            }

            for (int k = 0; k < kmax; k++)
            {
                int a0 = upper[si, k, UpperRings - 1], a1 = upper[si, k + 1, UpperRings - 1];
                Vector3 inward = acc.V[roll[k, 1]] - acc.V[a0];
                inward.y = 0;
                inward = inward.sqrMagnitude > 1e-10f ? inward.normalized : -m.Fwd;
                acc.Quad(a0, a1, roll[k + 1, 0], roll[k, 0], up * 2f - inward);
                acc.Quad(roll[k, 0], roll[k + 1, 0], roll[k + 1, 1], roll[k, 1], up + inward);
                // the lining faces the opening (visible looking into the shoe)
                acc.Quad(roll[k, 1], roll[k + 1, 1], roll[k + 1, 2], roll[k, 2], inward + up * 0.5f);
                acc.Quad(roll[k, 2], roll[k + 1, 2], roll[k + 1, 3], roll[k, 3], inward);
            }
        }

        // liner: closes the shoe below the body cut around the leg, so looking down the opening never shows the cut leg
        // or the floor (the tongue covers the instep in front of the top eyelets)
        {
            float y = 0f;
            int n = 0;
            for (int si = 0; si < 2; si++)
            {
                for (int k = 0; k <= kEye; k++)
                {
                    y += acc.V[rolls[si][k, 3]].y;
                    n++;
                }
            }

            Vector3 c = m.Rest(sh.OpenXc, 0.5f * (sh.SecMed(sh.OpenXc) - sh.SecLat(sh.OpenXc)), 0f);
            c.y = y / Mathf.Max(1, n);
            int cv = acc.Add(c, new Vector2(0.895f, 0.505f), Rigid(bone0), GLining);
            for (int si = 0; si < 2; si++)
            {
                for (int k = 0; k < kEye; k++) acc.Tri(cv, rolls[si][k, 3], rolls[si][k + 1, 3], up);
            }

            acc.Tri(cv, rolls[0][kEye, 3], rolls[1][kEye, 3], up);
        }

        acc.TongueT0 = acc.T.Count / 3;
        // ---- tongue: along the spine (the leg's front silhouette, throat -> top); width = the eyestay gap + a tuck under
        // them, narrowing to a rounded 4.4 cm top; arched forward; front + back faces
        {
            IReadOnlyList<Vector3> sp = sh.Spine;
            IReadOnlyList<Vector3> sn = sh.SpineNormal;
            int nv = sp.Count;
            const int nu = 5;
            int[,] fr = new int[nu, nv], bk = new int[nu, nv];
            float cum = 0f, total = 0f;
            for (int j = 1; j < nv; j++) total += (sp[j] - sp[j - 1]).magnitude;
            for (int j = 0; j < nv; j++)
            {
                if (j > 0) cum += (sp[j] - sp[j - 1]).magnitude;
                float t = total > 0f ? cum / total : 0f;
                Vector3 c = sp[j], nf = sn[j];
                float slot = j <= SneakerShape.LacePoints ? 0.5f * (sh.Opening(c.x, 1) + sh.Opening(c.x, -1) + eyePush[0] + eyePush[1]) + 0.006f : 0.023f;
                float half = Mathf.Clamp(slot, 0.012f, 0.032f) * (j == nv - 1 ? 0.85f : 1f);
                for (int i = 0; i < nu; i++)
                {
                    float u = i / (nu - 1f) * 2f - 1f;
                    Vector3 local = c + new Vector3(0f, u * half, 0f) + nf * (0.006f * (1f - u * u)) - nf * (0.003f * u * u);
                    if (j == nv - 1) local.z -= 0.006f * u * u; // rounded top
                    Vector3 p = m.Rest(local.x, local.y, local.z);
                    Vector2 uvF = new(Mathf.Lerp(0.935f, 0.995f, (u + 1f) * 0.5f), Mathf.Lerp(0.505f, 0.745f, t));
                    Vector2 uvB = new(Mathf.Lerp(0.865f, 0.925f, (u + 1f) * 0.5f), Mathf.Lerp(0.505f, 0.745f, t));
                    BoneWeight tw = ShinWeight(bone0, sh.TongueShinWeight(local.z));
                    fr[i, j] = acc.Add(p, uvF, tw, GTongueF);
                    bk[i, j] = acc.Add(p - ToRest(nf) * 0.0025f, uvB, tw, GTongueB);
                }
            }

            for (int j = 0; j < nv - 1; j++)
            {
                Vector3 outN = ToRest((sn[j] + sn[j + 1]).normalized);
                for (int i = 0; i < nu - 1; i++)
                {
                    int tf = acc.T.Count / 3;
                    acc.Quad(fr[i, j], fr[i + 1, j], fr[i + 1, j + 1], fr[i, j + 1], outN);
                    if (i == 1 || i == nu - 3) for (int q = tf; q < acc.T.Count / 3; q++) acc.TongueFront.Add(q); // the central strip
                    acc.Quad(bk[i, j], bk[i + 1, j], bk[i + 1, j + 1], bk[i, j + 1], -outN);
                }
            }
        }

        acc.TongueT1 = acc.T.Count / 3;
        // ---- laces: bars across the eyestays from the top eyelets down to the throat (spaced ~13 mm along the line),
        // arched over the tongue, slightly criss-crossed
        {
            float x0 = sh.XEye + 0.004f, x1 = sh.XOpen - 0.012f;
            float rise = Mathf.Abs(sh.TongueH(x1) - sh.TongueH(x0));
            float run = Mathf.Sqrt((x1 - x0) * (x1 - x0) + rise * rise);
            int bars = Mathf.Clamp(Mathf.RoundToInt(run / 0.013f) + 1, 3, 7);
            for (int i = 0; i < bars; i++)
            {
                float x = bars > 1 ? Mathf.Lerp(x0, x1, i / (bars - 1f)) : 0.5f * (x0 + x1);
                float zc = sh.TongueH(x) + 0.006f + 0.003f; // over the tongue's arch
                float ze = sh.Collar(x, 3) + 0.0015f;
                float ho = sh.Opening(x, 1) + 0.002f + eyePush[0], hl = sh.Opening(x, -1) + 0.002f + eyePush[1];
                int[,] bar = new int[4, 2];
                for (int c = 0; c < 4; c++)
                {
                    float u = c / 3f;
                    float s = Mathf.Lerp(ho, -hl, u);
                    float z = Mathf.Lerp(ze, zc, Mathf.Sin(u * Mathf.PI));
                    for (int e = 0; e < 2; e++)
                    {
                        float xx = x + (e == 0 ? -0.003f : 0.003f) + (u - 0.5f) * 0.008f * (i % 2 == 0 ? 1f : -1f);
                        bar[c, e] = acc.Add(m.Rest(xx, s, z + (e == 0 ? 0.001f : 0f)),
                            new Vector2(Mathf.Lerp(0.865f, 0.925f, u), e == 0 ? 0.765f : 0.985f), Rigid(bone0), GLace);
                    }
                }

                for (int c = 0; c < 3; c++) acc.Quad(bar[c, 0], bar[c + 1, 0], bar[c + 1, 1], bar[c, 1], up + m.Fwd * 0.3f);
            }
        }

        // ---- heel pull tab: a small double-sided loop above the heel back
        {
            Vector3 rim = m.Local(acc.V[upper[0, 0, UpperRings - 1]]); // the (possibly pushed) heel back rim
            Vector3 back = new(rim.x - 0.002f, 0f, rim.z - 0.004f);
            float w = 0.011f, hgt = 0.014f;
            Vector3[] q =
            {
                new(back.x, -w, back.z), new(back.x, w, back.z), new(back.x - 0.004f, w * 0.85f, back.z + hgt), new(back.x - 0.004f, -w * 0.85f, back.z + hgt)
            };
            Vector2[] uvq = { new(0.935f, 0.755f), new(0.995f, 0.755f), new(0.995f, 0.995f), new(0.935f, 0.995f) };
            int[] fq = new int[4], bq = new int[4];
            for (int i = 0; i < 4; i++)
            {
                Vector3 p = m.Rest(q[i].x, q[i].y, q[i].z);
                fq[i] = acc.Add(p, uvq[i], Rigid(bone0), GTabF);
                bq[i] = acc.Add(p + m.Fwd * 0.0015f, uvq[i], Rigid(bone0), GTabB);
            }

            acc.Quad(fq[0], fq[1], fq[2], fq[3], -m.Fwd);
            acc.Quad(bq[0], bq[1], bq[2], bq[3], m.Fwd);
        }

        return pushMax;
    }

    /// <summary>clearance kept between the leg's envelope and the rear upper (the upper is one surface; its padding)</summary>
    const float PushMargin = 0.003f;

    static float AngleDiff(float a, float b)
    {
        float d = a - b;
        while (d > Mathf.PI) d -= 2f * Mathf.PI;
        while (d < -Mathf.PI) d += 2f * Mathf.PI;
        return d;
    }

    /// <summary>
    /// Push the rear upper's rings (columns 0 .. KOpen-1, both strips; wall = (x, s, h)) out wherever the leg's envelope
    /// over the capture + PushMargin would come through them, along each ring's own outward normal in plan. Each ring
    /// vertex covers the heights up to its neighbouring rings and half the way to its neighbouring columns, so the flat
    /// quads between them stay outside the leg too; the pushes are spread along the perimeter and up the column (no
    /// single-vertex bumps). eyePush gets the largest push of the eyestay columns' rims per strip (medial, lateral) for
    /// the tongue and laces. Returns the largest push (m).
    /// </summary>
    static float PushUpper(SneakerShape sh, Vector3[,,] wall, float[] eyePush)
    {
        LegEnvelope env = sh.Env;
        int kr = sh.KOpen, R = UpperRings;
        if (env == null || kr < 2) return 0f;
        int n = 2 * kr - 1; // perimeter order: lateral kr-1 .. 1, the heel back 0 (shared), medial 1 .. kr-1
        (int si, int k) At(int i) => i < kr - 1 ? (1, kr - 1 - i) : (0, i - (kr - 1));
        float[,] d = new float[n, R];
        Vector2[,] nrm = new Vector2[n, R];
        bool[] eyestay = new bool[n];
        for (int i = 0; i < n; i++)
        {
            (int si, int k) = At(i);
            int sg = si == 0 ? 1 : -1;
            eyestay[i] = sh.Top(k, sg).x > sh.XEye + 0.004f;
            (int sa, int ka) = At(Mathf.Max(0, i - 1));
            (int sb, int kb) = At(Mathf.Min(n - 1, i + 1));
            for (int r = 1; r < R; r++)
            {
                Vector3 p = wall[si, k, r];
                Vector2 p2 = new(p.x, p.y), pa = new(wall[sa, ka, r].x, wall[sa, ka, r].y), pb = new(wall[sb, kb, r].x, wall[sb, kb, r].y);
                Vector2 tan = pb - pa;
                Vector2 nn = tan.sqrMagnitude > 1e-10f ? new Vector2(tan.y, -tan.x).normalized : new Vector2(-1f, 0f);
                if (Vector2.Dot(nn, PlanNormal(sh, k, sg)) < 0f) nn = -nn; // outward like the outline's normal
                if (i == kr - 1) nn = new Vector2(-1f, 0f); // the heel back
                nrm[i, r] = nn;
                float half = Mathf.Clamp(0.5f * Mathf.Max((p2 - pa).magnitude, (p2 - pb).magnitude), 0.003f, 0.012f);
                float h0 = wall[si, k, r - 1].z - 0.001f, h1 = (r < R - 1 ? wall[si, k, r + 1].z : p.z) + 0.001f;
                float reach = env.Reach(p2, nn, half, h0, h1, PushMargin, 0.04f);
                if (reach > -PushMargin) d[i, r] = reach + PushMargin;
            }
        }

        // spread: half of a neighbour's push along the perimeter and up / down the column, never less than needed
        for (int pass = 0; pass < 2; pass++)
        {
            float[,] s = (float[,])d.Clone();
            for (int i = 0; i < n; i++)
            {
                for (int r = 1; r < R; r++)
                {
                    float v = d[i, r];
                    if (i > 0) v = Mathf.Max(v, 0.5f * d[i - 1, r]);
                    if (i < n - 1) v = Mathf.Max(v, 0.5f * d[i + 1, r]);
                    if (r > 1) v = Mathf.Max(v, 0.5f * d[i, r - 1]);
                    if (r < R - 1) v = Mathf.Max(v, 0.5f * d[i, r + 1]);
                    s[i, r] = v;
                }
            }

            d = s;
        }

        float max = 0f;
        for (int i = 0; i < n; i++)
        {
            (int si, int k) = At(i);
            int sg = si == 0 ? 1 : -1;
            for (int r = 1; r < R; r++)
            {
                float push = d[i, r];
                if (push <= 0f) continue;
                Vector3 p = wall[si, k, r];
                p.x += nrm[i, r].x * push;
                p.y += nrm[i, r].y * push;

                wall[si, k, r] = p;
                if (k == 0) wall[1 - si, 0, r] = p; // the heel back is shared by both strips
                max = Mathf.Max(max, push);
                if (eyestay[i] && r == R - 1) eyePush[si] = Mathf.Max(eyePush[si], push);
            }
        }

        return max;
    }

    /// <summary>outward plan normal (x, s) of the outline at column k on side sigma</summary>
    static Vector2 PlanNormal(SneakerShape sh, int k, int sigma)
    {
        Vector2 a = sh.Outline(Mathf.Max(0, k - 1), sigma, 0f), b = sh.Outline(Mathf.Min(SneakerShape.K, k + 1), sigma, 0f);
        Vector2 t = b - a;
        Vector2 n = new Vector2(-t.y, t.x).normalized * sigma; // medial side (s > 0): rotate the forward tangent left
        if (k == 0) n = new Vector2(-1f, 0f);
        if (k == SneakerShape.K) n = new Vector2(1f, 0f);
        return n;
    }

    static Vector3 Outward(Acc acc, int v, Vector3 centre, float upBias)
    {
        Vector3 d = acc.V[v] - centre;
        d.y = 0;
        return d.normalized + Vector3.up * upBias;
    }

    /// <summary>area-weighted vertex normals, averaged across vertices at the same position in the same smoothing
    /// group (UV seams stay smooth, group borders stay hard)</summary>
    static List<Vector3> SmoothNormals(Acc acc)
    {
        int n = acc.V.Count;
        Vector3[] nrm = new Vector3[n];
        for (int t = 0; t < acc.T.Count; t += 3)
        {
            int a = acc.T[t], b = acc.T[t + 1], c = acc.T[t + 2];
            Vector3 fn = Vector3.Cross(acc.V[b] - acc.V[a], acc.V[c] - acc.V[a]);
            nrm[a] += fn;
            nrm[b] += fn;
            nrm[c] += fn;
        }

        Dictionary<(int, int, int, int), Vector3> sum = new();
        (int, int, int, int) Key(int i) => (acc.G[i], Mathf.RoundToInt(acc.V[i].x * 10000f), Mathf.RoundToInt(acc.V[i].y * 10000f), Mathf.RoundToInt(acc.V[i].z * 10000f));
        for (int i = 0; i < n; i++)
        {
            var key = Key(i);
            sum[key] = sum.TryGetValue(key, out Vector3 s) ? s + nrm[i] : nrm[i];
        }

        List<Vector3> outN = new(n);
        for (int i = 0; i < n; i++)
        {
            Vector3 s = sum[Key(i)];
            outN.Add(s.sqrMagnitude > 1e-20f ? s.normalized : Vector3.up);
        }

        return outN;
    }

}
