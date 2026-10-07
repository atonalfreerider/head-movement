using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Forward kinematics of a dancer's SMPL-X motion in avatar-local space: exactly what SmplxAvatar.PoseBones and the bone
/// hierarchy produce (root at rest root + transl, child = parent position + parent rotation * rest offset), as pure
/// math for the sneakers' load-time fit (no Transforms touched; also runs offline).
/// </summary>
public class SneakerMotion
{
    public readonly int Frames;
    public readonly Vector3[] P; // [frame * Joints + joint]: avatar-local joint position
    public readonly Quaternion[] R; // avatar-local joint rotation
    public readonly Quaternion[] Local; // the motion's local rotations
    public readonly Vector3[] RestJoints;
    public readonly float[] Time; // seconds of each frame

    public SneakerMotion(SmplxData.Motion mo, SmplxData.Skin skin, Func<int, float> frameTime)
    {
        const int J = SmplxData.Joints;
        Frames = mo.FrameCount;
        P = new Vector3[Frames * J];
        R = new Quaternion[Frames * J];
        Local = mo.Rotations;
        RestJoints = skin.RestJoints;
        int[] par = skin.Parents;
        for (int j = 0; j < J; j++)
        {
            if (par[j] >= j) throw new InvalidOperationException($"SMPL-X parents out of order at joint {j}");
        }

        for (int k = 0; k < Frames; k++)
        {
            int o = k * J;
            for (int j = 0; j < J; j++)
            {
                int p = par[j];
                Quaternion q = mo.Rotations[o + j];
                if (p < 0)
                {
                    R[o + j] = q;
                    P[o + j] = skin.RestJoints[j] + mo.Transl[k];
                }
                else
                {
                    R[o + j] = R[o + p] * q;
                    P[o + j] = P[o + p] + R[o + p] * (skin.RestJoints[j] - skin.RestJoints[p]);
                }
            }
        }

        Time = new float[Frames];
        for (int k = 0; k < Frames; k++)
        {
            float t = frameTime != null ? frameTime(k) : k / 30f;
            Time[k] = float.IsFinite(t) ? t : k / 30f;
        }
    }

    /// <summary>seconds between frames a and b (30 fps when the clock is missing or not increasing)</summary>
    public float Dt(int a, int b)
    {
        float dt = Time[b] - Time[a];
        return dt > 1e-4f ? dt : Mathf.Max(1, b - a) / 30f;
    }
}

/// <summary>a point of a shoe's outsole edge (rest, avatar-local) and its toe-bone weight - the bevel rings of the
/// sole mesh, so the lowest of them is the lowest point of the shoe mesh</summary>
public struct SolePoint
{
    public Vector3 Rest;
    public float ToeWeight;
    public bool Fore; // forefoot group (x >= 0.55 L) for the pitch
}

public class SneakerFitSettings
{
    public float ToeBendScale = 0.8f, ToeMinDeg = -5f, ToeMaxDeg = 45f;
    public bool Contact = true, Plant = true;
    /// <summary>lift cap (the fit sinks a heel up to ~2.5 cm), pitch cap, plant cap</summary>
    public float MaxLift = 0.03f, MaxPitchDeg = 4f, PlantMax = 0.02f;
    /// <summary>contact (stance) from the motion: the outsole's lowest point at most ContactH over the floor and its lower
    /// end at most ContactV fast (m/s), for at least MinContact frames (gaps of 2 frames closed); the plant ramps in /
    /// out at most RampRate per frame (m). Same thresholds as the stance of the hover statistic (SneakerChecks)</summary>
    public float ContactH = 0.03f, ContactV = 0.10f, RampRate = 0.0025f;
    public int MinContact = 4;
    /// <summary>the most the floor bound of an end may change per frame (m): its slope-limited upper envelope; both
    /// limits grow by the ankle's own vertical motion beyond StillAnkle per frame</summary>
    public float SlopeMax = 0.003f, StillAnkle = 0.004f;

    public SneakerFitSettings Clone() => (SneakerFitSettings)MemberwiseClone();
}

/// <summary>one foot's per-frame shoe pose for the whole capture, relative to the dancer's ankle bone (so it is
/// applied as ankle * (DPos, DRot) whatever transform the avatar has)</summary>
public class SneakerFootFit
{
    public readonly int N;
    public readonly Vector3[] DPos;
    public readonly Quaternion[] DRot;
    public readonly float[] Toe, ToeRaw; // deg
    public readonly float[] RawMin, RawHeel, RawFore; // lowest outsole point over the floor before the correction (m)
    public readonly float[] Min; // after
    public readonly float[] Shift; // vertical correction at the shoe's centre (+ lifted, - planted)
    public readonly float[] Drop; // how far the collar (rim dip) moved down the leg (m, + = down): the body cut margin
    public readonly float[] Pitch; // deg, + = toe up
    public readonly float[] Score; // plant weight: 1 in a contact, the ramp round it
    public readonly float[] Speed; // vertical speed of the lower end of the sole (m/s)
    public readonly float[] AnkleY; // ankle joint height (avatar-local)
    public float MaxDrop;

    public SneakerFootFit(int n)
    {
        N = n;
        DPos = new Vector3[n];
        DRot = new Quaternion[n];
        Toe = new float[n];
        ToeRaw = new float[n];
        RawMin = new float[n];
        RawHeel = new float[n];
        RawFore = new float[n];
        Min = new float[n];
        Shift = new float[n];
        Drop = new float[n];
        Pitch = new float[n];
        Score = new float[n];
        Speed = new float[n];
        AnkleY = new float[n];
    }

    /// <summary>corrected ankle proxy (avatar-local) of frame k from the ankle joint's pose</summary>
    public void Proxy(int k, Vector3 ankleP, Quaternion ankleR, out Vector3 p, out Quaternion r)
    {
        p = ankleP + ankleR * DPos[k];
        r = ankleR * DRot[k];
    }
}

/// <summary>
/// The sneakers' load-time fit to the capture (SneakerBuilder.Build): floor contact baked per frame from the motion,
/// the body cut under the shoe's top edge with a margin for the largest plant, and the envelope of the visible lower
/// leg in each shoe's frame over the whole capture (the rear upper and the collar are sized to it).
///
/// Contact, per foot and frame, from the lowest point of the whole outsole edge (SolePoint rings):
///   - never below the floor: each end of the sole (heel, forefoot) is held at least at the floor - the bound smoothed
///     as an upper envelope (dilate + a blur no wider: no 1-frame steps, never under the bound); an end that is lifted
///     pitches the shoe about the other end so that one stays where it was (capped at MaxPitchDeg; any remaining
///     penetration is lifted out), all capped at MaxLift
///   - in contact (from the motion: low and slow for MinContact+ frames): one plant offset per contact, the median
///     hover over it (capped at PlantMax), ramped in / out over neighbouring frames at most RampRate per frame - the
///     shoe does not slide on the leg during a stance and nothing snaps
/// Stateless at runtime: scrubbing shows the same shoe pose for a frame every time.
/// </summary>
public static class SneakerFit
{
    /// <summary>signed rotation (deg) of q about axis (swing-twist), in Quaternion.AngleAxis's convention</summary>
    public static float TwistDeg(Quaternion q, Vector3 axis)
    {
        if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        float proj = q.x * axis.x + q.y * axis.y + q.z * axis.z;
        return 2f * Mathf.Atan2(proj, q.w) * Mathf.Rad2Deg;
    }

    /// <summary>a rest point carried by the shoe's two proxies (ankle pa / ra; toe pivot at toeRest - ankleRest in the
    /// ankle frame, local rotation toe), linear blend by the toe weight like the GPU skinning</summary>
    public static Vector3 Carry(Vector3 rest, float toeW, Vector3 pa, Quaternion ra, Quaternion toe, Vector3 ankleRest, Vector3 toeRest)
    {
        Vector3 a = pa + ra * (rest - ankleRest);
        if (toeW <= 0f) return a;
        Vector3 t = pa + ra * (toeRest - ankleRest + toe * (rest - toeRest));
        return a + (t - a) * toeW;
    }

    static void RingMin(SolePoint[] ring, Vector3 pa, Quaternion ra, Quaternion toe, Vector3 ankleRest, Vector3 toeRest,
        out float min, out float heel, out float fore)
    {
        min = heel = fore = float.MaxValue;
        foreach (SolePoint sp in ring)
        {
            float y = Carry(sp.Rest, sp.ToeWeight, pa, ra, toe, ankleRest, toeRest).y;
            if (y < min) min = y;
            if (sp.Fore) fore = Mathf.Min(fore, y);
            else heel = Mathf.Min(heel, y);
        }
    }

    /// <summary>bake one foot's contact over the capture (avatar-local, floor y = 0)</summary>
    public static SneakerFootFit Bake(SneakerMotion mo, FootMeasure m, SneakerShape sh, SolePoint[] ring, Vector3 ankleRest,
        Vector3 toeRest, SneakerFitSettings st)
    {
        const int J = SmplxData.Joints;
        int n = mo.Frames;
        SneakerFootFit f = new(n);
        Vector3 refH = m.Rest(0.12f * m.L, 0f, sh.Bottom(0.12f * m.L)), refF = m.Rest(m.BallX, 0f, sh.Bottom(m.BallX));
        const float bF = 0.5f; // SneakerBuilder.Weight at the ball line
        Vector3 centre = m.Rest(0.5f * (sh.X0 + sh.X1), 0f, 0f);
        Vector3[] dips = { m.Rest(m.AnkleX, sh.W(m.AnkleX, 1), sh.RimDip), m.Rest(m.AnkleX, -sh.W(m.AnkleX, -1), sh.RimDip) };
        Vector3[] pa = new Vector3[n];
        Quaternion[] ra = new Quaternion[n], toeQ = new Quaternion[n];
        float[] yRefH = new float[n], yRefF = new float[n];
        for (int k = 0; k < n; k++)
        {
            int o = k * J;
            pa[k] = mo.P[o + m.AnkleJoint];
            ra[k] = mo.R[o + m.AnkleJoint];
            float raw = TwistDeg(mo.Local[o + m.BallJoint], m.FlexAxis);
            float th = Mathf.Clamp(raw * st.ToeBendScale, st.ToeMinDeg, st.ToeMaxDeg);
            f.ToeRaw[k] = raw;
            f.Toe[k] = th;
            toeQ[k] = Quaternion.AngleAxis(th, m.FlexAxis);
            RingMin(ring, pa[k], ra[k], toeQ[k], ankleRest, toeRest, out f.RawMin[k], out f.RawHeel[k], out f.RawFore[k]);
            yRefH[k] = Carry(refH, 0f, pa[k], ra[k], toeQ[k], ankleRest, toeRest).y;
            yRefF[k] = Carry(refF, bF, pa[k], ra[k], toeQ[k], ankleRest, toeRest).y;
            f.AnkleY[k] = pa[k].y;
        }

        // contact from the motion: the sole's lower end low and slow (gaps of up to 2 frames closed, runs shorter than
        // MinContact frames dropped). Each contact holds one plant offset - the median hover over it, capped - so the
        // shoe does not slide on the leg during a stance; it ramps in before and out after at most RampRate per frame
        bool[] low = new bool[n];
        for (int k = 0; k < n; k++)
        {
            int a = Mathf.Max(0, k - 1), b = Mathf.Min(n - 1, k + 1);
            float dt = mo.Dt(a, b);
            float vH = b > a ? Mathf.Abs(yRefH[b] - yRefH[a]) / dt : 0f, vF = b > a ? Mathf.Abs(yRefF[b] - yRefF[a]) / dt : 0f;
            float heelLow = SneakerShape.Smooth(-0.005f, 0.005f, f.RawFore[k] - f.RawHeel[k]);
            f.Speed[k] = heelLow * vH + (1f - heelLow) * vF;
            low[k] = f.RawMin[k] <= st.ContactH && f.Speed[k] <= st.ContactV;
        }

        for (int k = 1; k < n - 1; k++)
        {
            if (low[k]) continue;
            int a = k - 1, b = k;
            while (b < n && !low[b] && b - a <= 2) b++;
            if (a >= 0 && low[a] && b < n && low[b] && b - a <= 3)
            {
                for (int q = a + 1; q < b; q++) low[q] = true;
            }
        }

        float[] plant = new float[n];
        for (int k = 0; k < n;)
        {
            if (!low[k])
            {
                k++;
                continue;
            }

            int a = k;
            while (k < n && low[k]) k++;
            int b = k - 1;
            if (b - a + 1 < st.MinContact || !st.Plant) continue;
            // in the contact the plant follows the hover (capped); before / after it ramps from the edge's value
            float offA = -Mathf.Clamp(f.RawMin[a], 0f, st.PlantMax), offB = -Mathf.Clamp(f.RawMin[b], 0f, st.PlantMax);
            int rampA = Mathf.Max(1, Mathf.CeilToInt(-offA / st.RampRate)), rampB = Mathf.Max(1, Mathf.CeilToInt(-offB / st.RampRate));
            for (int q = Mathf.Max(0, a - rampA); q <= Mathf.Min(n - 1, b + rampB); q++)
            {
                float w = q < a ? 1f - (a - q) / (rampA + 1f) : q > b ? 1f - (q - b) / (rampB + 1f) : 1f;
                w = w * w * (3f - 2f * w);
                float off = q < a ? offA : q > b ? offB : -Mathf.Clamp(f.RawMin[q], 0f, st.PlantMax);
                plant[q] = Mathf.Min(plant[q], off * w);
                f.Score[q] = Mathf.Max(f.Score[q], w);
            }
        }

        // the shoe slides on the leg at most RampRate per frame (more while the ankle itself moves faster than a still
        // foot - StillAnkle per frame - the slide then hides in the motion): the plant's slope-limited upper envelope
        float[] moving = new float[n];
        for (int k = 1; k < n; k++) moving[k] = Mathf.Max(0f, Mathf.Abs(f.AnkleY[k] - f.AnkleY[k - 1]) - st.StillAnkle);
        plant = SlopeLimit(plant, st.RampRate, moving);

        // per end: the no-penetration bound (-height) as its tightest upper envelope that changes at most SlopeMax per
        // frame (never below the bound, so never under the floor; no steps), or the plant where the bound allows it
        float[] endH = new float[n], endF = new float[n];
        for (int k = 0; k < n; k++)
        {
            endH[k] = -f.RawHeel[k];
            endF[k] = -f.RawFore[k];
        }

        endH = SlopeLimit(endH, st.SlopeMax, moving);
        endF = SlopeLimit(endF, st.SlopeMax, moving);
        for (int k = 0; k < n; k++)
        {
            endH[k] = Mathf.Min(Mathf.Max(endH[k], plant[k]), st.MaxLift);
            endF[k] = Mathf.Min(Mathf.Max(endF[k], plant[k]), st.MaxLift);
        }
        float sinMax = Mathf.Sin(st.MaxPitchDeg * Mathf.Deg2Rad);
        for (int k = 0; k < n; k++)
        {
            Vector3 pc = pa[k];
            Quaternion rc = ra[k];
            float theta = 0f;
            if (st.Contact)
            {
                float cH = endH[k], cF = endF[k];
                Vector3 pH = Carry(refH, 0f, pa[k], ra[k], toeQ[k], ankleRest, toeRest);
                Vector3 pF = Carry(refF, bF, pa[k], ra[k], toeQ[k], ankleRest, toeRest);
                Vector3 d = pF - pH;
                d.y = 0f;
                float dist = d.magnitude;
                Quaternion rot = Quaternion.identity;
                if (dist > 0.03f)
                {
                    float s = Mathf.Clamp((cF - cH) / dist, -sinMax, sinMax);
                    theta = Mathf.Asin(s) * Mathf.Rad2Deg;
                    Vector3 dir = d / dist;
                    Vector3 axis = Vector3.Cross(Vector3.up, dir);
                    rot = Quaternion.AngleAxis(theta, axis);
                    if ((rot * dir).y * theta < 0f) rot = Quaternion.AngleAxis(-theta, axis);
                }

                Vector3 mid = 0.5f * (pH + pF);
                float dy = 0.5f * (cH + cF);
                rc = rot * ra[k];
                pc = mid + rot * (pa[k] - mid) + Vector3.up * dy;
                // hard floor: no outsole point below it (the pitch can tip a corner under)
                RingMin(ring, pc, rc, toeQ[k], ankleRest, toeRest, out float y2, out _, out _);
                if (y2 < 0f)
                {
                    float extra = Mathf.Min(-y2, Mathf.Max(0f, st.MaxLift - Mathf.Max(0f, dy)));
                    pc += Vector3.up * extra;
                }
            }

            RingMin(ring, pc, rc, toeQ[k], ankleRest, toeRest, out f.Min[k], out _, out _);
            Quaternion inv = Quaternion.Inverse(ra[k]);
            f.DPos[k] = inv * (pc - pa[k]);
            f.DRot[k] = inv * rc;
            f.Pitch[k] = theta;
            f.Shift[k] = (pc + rc * (centre - ankleRest)).y - (pa[k] + ra[k] * (centre - ankleRest)).y;
            float drop = 0f;
            foreach (Vector3 dp in dips) drop = Mathf.Max(drop, (pa[k] + ra[k] * (dp - ankleRest)).y - (pc + rc * (dp - ankleRest)).y);
            f.Drop[k] = drop;
            f.MaxDrop = Mathf.Max(f.MaxDrop, drop);
        }

        return f;
    }

    /// <summary>the tightest upper envelope of a with a slope of at most m per frame: max_j (a[j] - m |i - j|)
    /// (a forward and a backward pass)</summary>
    public static float[] SlopeLimit(float[] a, float m) => SlopeLimit(a, m, null);

    /// <param name="extra">per step i-1 -> i: more slope allowed there (null = none)</param>
    public static float[] SlopeLimit(float[] a, float m, float[] extra)
    {
        float[] o = (float[])a.Clone();
        for (int i = 1; i < o.Length; i++) o[i] = Mathf.Max(o[i], o[i - 1] - m - (extra != null ? extra[i] : 0f));
        for (int i = o.Length - 2; i >= 0; i--) o[i] = Mathf.Max(o[i], o[i + 1] - m - (extra != null ? extra[i + 1] : 0f));
        return o;
    }
}

/// <summary>
/// The lower leg of one side as the viewer draws it with the feet hidden (HM_AvatarLit clips fragments whose foot mask -
/// SmplxAvatar.SetFootHeights: rest height over the shoe's top edge minus the cut margin - is below 0; the cut edge
/// runs along the triangle edges): the visible vertices near the shoe, the cut-edge points and the visible triangles'
/// edge midpoints / centroids, each with its skinning weights - posed per frame into the shoe's frame (no Transforms,
/// also offline).
/// </summary>
public class LegSamples
{
    public int Count; // vertices
    public Vector3[] Rest;
    public int[] SkinIndex; // the skin vertex of each sample vertex
    public float[] Base, Share; // mask parts: H = Base + margin * Share (SneakerBuilder.MaskParts)
    public float[] H; // the foot mask at the current margin (visible where >= 0)
    public float Margin;
    public int[] Joint; // 4 per vertex
    public float[] Weight;
    public int[] JointsUsed;
    int[] pairA, pairB; // unique edges among the sample vertices
    int[] triA0, triB0, triC0; // triangles among them
    // at the current margin
    public int[] EdgeA, EdgeB;
    public float[] EdgeT;
    public bool[] EdgeCut; // the cut edge (t from the mask) vs a midpoint (t 0.5) of a visible edge
    public int[] TriA, TriB, TriC; // visible triangles (centroids)
    public int[] CutVertices; // the vertices of the cut edges

    /// <summary>
    /// The lower leg of one side near its shoe: every vertex whose mask can come within reach for margins 0..marginMax
    /// (H = Base + margin * Share from -5 cm to hTop), their edges and triangles; SetMargin picks the visible ones.
    /// </summary>
    /// <param name="baseH">per skin vertex: mask height at margin 0 (+1 off the lower legs)</param>
    /// <param name="share">per skin vertex: how much of the margin applies there</param>
    public static LegSamples Build(SmplxData.Skin skin, bool left, float[] baseH, float[] share, float marginMax, float hTop, float margin)
    {
        const int J = SmplxData.Joints;
        int n = skin.RestVertices.Length;
        int[] map = new int[n];
        List<int> verts = new();
        for (int v = 0; v < n; v++)
        {
            map[v] = -1;
            int o = v * J;
            float wl = skin.Weights[o + 4] + skin.Weights[o + 7] + skin.Weights[o + 10];
            float wr = skin.Weights[o + 5] + skin.Weights[o + 8] + skin.Weights[o + 11];
            bool mine = wl + wr >= 0.25f && (left ? wl >= wr : wr > wl);
            if (!mine || baseH[v] + marginMax * share[v] < -0.05f || baseH[v] > hTop) continue;
            map[v] = verts.Count;
            verts.Add(v);
        }

        int c = verts.Count;
        LegSamples s = new()
        {
            Count = c, Rest = new Vector3[c], SkinIndex = verts.ToArray(), Base = new float[c], Share = new float[c], H = new float[c],
            Joint = new int[c * 4], Weight = new float[c * 4]
        };
        HashSet<int> used = new();
        List<(int j, float w)> top = new();
        for (int i = 0; i < c; i++)
        {
            int v = verts[i], o = v * J;
            s.Rest[i] = skin.RestVertices[v];
            s.Base[i] = baseH[v];
            s.Share[i] = share[v];
            top.Clear();
            for (int j = 0; j < J; j++)
            {
                if (skin.Weights[o + j] > 1e-4f) top.Add((j, skin.Weights[o + j]));
            }

            top.Sort((p, q) => q.w.CompareTo(p.w));
            while (top.Count < 4) top.Add((0, 0f));
            float sum = top[0].w + top[1].w + top[2].w + top[3].w;
            for (int k = 0; k < 4; k++)
            {
                s.Joint[4 * i + k] = top[k].j;
                s.Weight[4 * i + k] = sum > 0f ? top[k].w / sum : 0f;
                if (top[k].w > 0f) used.Add(top[k].j);
            }
        }

        s.JointsUsed = new int[used.Count];
        used.CopyTo(s.JointsUsed);
        List<int> pa = new(), pb = new(), ta = new(), tb = new(), tc = new();
        HashSet<long> seen = new();
        int[] T = skin.Triangles;
        for (int t = 0; t + 2 < T.Length; t += 3)
        {
            int a = map[T[t]], b = map[T[t + 1]], d = map[T[t + 2]];
            if (a < 0 || b < 0 || d < 0) continue;
            ta.Add(a);
            tb.Add(b);
            tc.Add(d);
            int[] tri = { a, b, d };
            for (int e = 0; e < 3; e++)
            {
                int p = tri[e], q = tri[(e + 1) % 3];
                long key = (long)Mathf.Min(p, q) * 1000003L + Mathf.Max(p, q);
                if (!seen.Add(key)) continue;
                pa.Add(p);
                pb.Add(q);
            }
        }

        s.pairA = pa.ToArray();
        s.pairB = pb.ToArray();
        s.triA0 = ta.ToArray();
        s.triB0 = tb.ToArray();
        s.triC0 = tc.ToArray();
        s.SetMargin(margin);
        return s;
    }

    /// <summary>the visible samples for a cut margin: H, the cut edges (t from the mask), the visible edges' midpoints,
    /// the visible triangles</summary>
    public void SetMargin(float margin)
    {
        Margin = margin;
        for (int i = 0; i < Count; i++) H[i] = Base[i] + margin * Share[i];
        List<int> ea = new(), eb = new(), ta = new(), tb = new(), tc = new();
        List<float> et = new();
        List<bool> ec = new();
        HashSet<int> cutV = new();
        for (int e = 0; e < pairA.Length; e++)
        {
            int p = pairA[e], q = pairB[e];
            float hp = H[p], hq = H[q];
            if ((hp < 0f) != (hq < 0f))
            {
                ea.Add(p);
                eb.Add(q);
                et.Add(-hp / (hq - hp));
                ec.Add(true);
                cutV.Add(p);
                cutV.Add(q);
            }
            else if (hp >= 0f)
            {
                ea.Add(p);
                eb.Add(q);
                et.Add(0.5f);
                ec.Add(false);
            }
        }

        for (int t = 0; t < triA0.Length; t++)
        {
            if (H[triA0[t]] < 0f || H[triB0[t]] < 0f || H[triC0[t]] < 0f) continue;
            ta.Add(triA0[t]);
            tb.Add(triB0[t]);
            tc.Add(triC0[t]);
        }

        EdgeA = ea.ToArray();
        EdgeB = eb.ToArray();
        EdgeT = et.ToArray();
        EdgeCut = ec.ToArray();
        TriA = ta.ToArray();
        TriB = tb.ToArray();
        TriC = tc.ToArray();
        CutVertices = new int[cutV.Count];
        cutV.CopyTo(CutVertices);
    }

    readonly float[] mats = new float[SmplxData.Joints * 12];

    /// <summary>skin the sample vertices (all, or just `only`) at frame k into the frame of a shoe whose ankle proxy is
    /// (pc, rc), as rest-space (avatar-local rest) points: out[i] = the vertex where the shoe's rest mesh is</summary>
    public void PoseInto(SneakerMotion mo, int k, Vector3 pc, Quaternion rc, Vector3 ankleRest, Vector3[] outRest, int[] only = null)
    {
        const int J = SmplxData.Joints;
        int o = k * J;
        Quaternion inv = Quaternion.Inverse(rc);
        // per joint: q = inv * R_j as a 3x3 matrix, t = inv * (P_j - pc) - q * rest_j
        float[] m = mats;
        foreach (int j in JointsUsed)
        {
            Quaternion q = inv * mo.R[o + j];
            Vector3 ex = q * new Vector3(1, 0, 0), ey = q * new Vector3(0, 1, 0), ez = q * new Vector3(0, 0, 1);
            Vector3 t = inv * (mo.P[o + j] - pc) - q * mo.RestJoints[j];
            int b = j * 12;
            m[b] = ex.x; m[b + 1] = ey.x; m[b + 2] = ez.x; m[b + 3] = t.x;
            m[b + 4] = ex.y; m[b + 5] = ey.y; m[b + 6] = ez.y; m[b + 7] = t.y;
            m[b + 8] = ex.z; m[b + 9] = ey.z; m[b + 10] = ez.z; m[b + 11] = t.z;
        }

        int count = only != null ? only.Length : Count;
        for (int ii = 0; ii < count; ii++)
        {
            int i = only != null ? only[ii] : ii;
            Vector3 r = Rest[i];
            float x = 0f, y = 0f, z = 0f;
            for (int c = 0; c < 4; c++)
            {
                float w = Weight[4 * i + c];
                if (w <= 0f) continue;
                int b = Joint[4 * i + c] * 12;
                x += w * (m[b] * r.x + m[b + 1] * r.y + m[b + 2] * r.z + m[b + 3]);
                y += w * (m[b + 4] * r.x + m[b + 5] * r.y + m[b + 6] * r.z + m[b + 7]);
                z += w * (m[b + 8] * r.x + m[b + 9] * r.y + m[b + 10] * r.z + m[b + 11]);
            }

            outRest[i] = new Vector3(x + ankleRest.x, y + ankleRest.y, z + ankleRest.z);
        }
    }

    public Vector3 Edge(Vector3[] posed, int e) => posed[EdgeA[e]] + (posed[EdgeB[e]] - posed[EdgeA[e]]) * EdgeT[e];

    /// <summary>rest x (foot frame) of an edge sample</summary>
    public float RestX(int e, FootMeasure m) => m.Local(Rest[EdgeA[e]] + (Rest[EdgeB[e]] - Rest[EdgeA[e]]) * EdgeT[e]).x;
}

/// <summary>
/// Where the visible lower leg goes in one shoe's frame over the whole capture (foot frame x along, s medial, h up):
/// a 3D occupancy of (x, s, h) cells for the rear upper and the collar, and its central band (|s - Sc| &lt;= Band) per
/// (x, h) for the tongue.
/// </summary>
public class LegEnvelope
{
    public const float Cell = 0.0025f, XMin = -0.06f, SMin = -0.10f;
    public const int NX = 160, NS = 80, NH = 100; // x -0.06..0.34, s -0.1..0.1, h 0..0.25
    public float Cx, Sc, Band = 0.03f;
    readonly ulong[] occ = new ulong[(NX * NS * NH + 63) / 64]; // any leg point in the (x, s, h) cell over the capture
    public readonly bool[] Central = new bool[NX * NH]; // a leg point with |s - Sc| <= Band at (x, h)
    public int Samples;

    public LegEnvelope(float cx, float sc)
    {
        Cx = cx;
        Sc = sc;
    }

    public static int HB(float h) => Mathf.Clamp(Mathf.FloorToInt(h / Cell), 0, NH - 1);
    public static int XB(float x) => Mathf.Clamp(Mathf.FloorToInt((x - XMin) / Cell), 0, NX - 1);
    public static int SB(float s) => Mathf.Clamp(Mathf.FloorToInt((s - SMin) / Cell), 0, NS - 1);

    static int Index(int xb, int sb, int hb) => (xb * NS + sb) * NH + hb;

    /// <summary>add a leg point (x, s, h)</summary>
    public void Add(Vector3 l)
    {
        if (l.z < 0f || l.z >= NH * Cell || l.x < XMin || l.x >= XMin + NX * Cell || l.y < SMin || l.y >= SMin + NS * Cell) return;
        Samples++;
        int xb = XB(l.x), hb = HB(l.z);
        int i = Index(xb, SB(l.y), hb);
        occ[i >> 6] |= 1UL << (i & 63);
        if (Mathf.Abs(l.y - Sc) <= Band) Central[xb * NH + hb] = true;
    }

    /// <summary>
    /// How far the leg reaches past a wall point p (x, s) along its outward plan normal n, within +-halfWidth along the
    /// wall and heights h0..h1 (m; negative = it stays that far inside; float.NegativeInfinity when no leg is near)
    /// </summary>
    public float Reach(Vector2 p, Vector2 n, float halfWidth, float h0, float h1, float inside = 0.03f, float outside = 0.05f)
    {
        Vector2 t = new(-n.y, n.x);
        const float step = 0.5f * Cell;
        int hb0 = HB(h0), hb1 = HB(h1);
        for (float d = outside; d >= -inside; d -= step)
        {
            for (float u = -halfWidth; u <= halfWidth + 1e-6f; u += step)
            {
                Vector2 q = p + n * d + t * u;
                if (q.x < XMin || q.x >= XMin + NX * Cell || q.y < SMin || q.y >= SMin + NS * Cell) continue;
                int xb = XB(q.x), sb = SB(q.y);
                for (int hb = hb0; hb <= hb1; hb++)
                {
                    int i = Index(xb, sb, hb);
                    if ((occ[i >> 6] & (1UL << (i & 63))) != 0) return d + 0.5f * Cell;
                }
            }
        }

        return float.NegativeInfinity;
    }
}

/// <summary>
/// The leg-in-shoe check against the actual shoe mesh (rest, one foot; the rear of the shoe is rigid on the ankle
/// proxy): every shoe triangle rasterised into a cylinder map round the leg's axis (outermost surface radius per angle
/// and height, i.e. what a horizontal ray from the leg first leaves through) and a top-down height map. A leg point
/// below the shoe's top edge that lies outside the outermost surface pokes through the shoe; a cut-edge point must
/// always be inside it (otherwise the cut body shows).
/// </summary>
public class ShoeVolume
{
    public const int NPhi = 180, NH = 160; // 2 deg, 1 mm (0..16 cm)
    public const float HCell = 0.001f, XYCell = 0.002f, XMin = -0.04f, SMin = -0.10f;
    public const int NX = 200, NS = 100;
    public readonly float Cx, Sc;
    public readonly float[] Rmax = new float[NPhi * NH]; // -1 empty: the rigid parts (on the ankle / toe proxy)
    public readonly float[] Rdyn = new float[NPhi * NH]; // the shin-skinned parts as posed this frame
    public readonly float[] Top = new float[NX * NS]; // max h, -1 empty
    readonly FootMeasure m;
    readonly SneakerShape sh;

    /// <param name="verts">this foot's rest mesh vertices (avatar-local rest)</param>
    /// <param name="mine">triangles of the rigid layer (the per-frame layer gets the others via BeginFrame / AddTriangle)</param>
    public ShoeVolume(FootMeasure m, SneakerShape sh, IList<Vector3> verts, IList<int> tris, Func<int, bool> mine, float cx, float sc)
    {
        this.m = m;
        this.sh = sh;
        Cx = cx;
        Sc = sc;
        for (int i = 0; i < Rmax.Length; i++)
        {
            Rmax[i] = -1f;
            Rdyn[i] = -1f;
        }

        for (int i = 0; i < Top.Length; i++) Top[i] = -1f;
        for (int t = 0; t + 2 < tris.Count; t += 3)
        {
            if (!mine(t / 3)) continue;
            Raster(m.Local(verts[tris[t]]), m.Local(verts[tris[t + 1]]), m.Local(verts[tris[t + 2]]), Rmax, true);
        }
    }

    public void BeginFrame()
    {
        for (int i = 0; i < Rdyn.Length; i++) Rdyn[i] = -1f;
    }

    /// <summary>a triangle of the per-frame layer, corners in the shoe's rest space as posed this frame</summary>
    public void AddTriangle(Vector3 a, Vector3 b, Vector3 c) => Raster(m.Local(a), m.Local(b), m.Local(c), Rdyn, false);

    void Raster(Vector3 a, Vector3 b, Vector3 c, float[] map, bool top)
    {
        float e = Mathf.Max((b - a).magnitude, Mathf.Max((c - b).magnitude, (a - c).magnitude));
        int steps = Mathf.Clamp(Mathf.CeilToInt(e / 0.0006f), 1, 120);
        for (int i = 0; i <= steps; i++)
        {
            for (int j = 0; j <= steps - i; j++)
            {
                float u = i / (float)steps, v = j / (float)steps;
                Splat(a + (b - a) * u + (c - a) * v, map, top);
            }
        }
    }

    void Splat(Vector3 l, float[] map, bool top)
    {
        float dx = l.x - Cx, ds = l.y - Sc;
        int hb = Mathf.FloorToInt(l.z / HCell);
        if (hb >= 0 && hb < NH)
        {
            int p = LegEnvelopeBin(Mathf.Atan2(ds, dx)) * NH + hb;
            float rho = Mathf.Sqrt(dx * dx + ds * ds);
            if (rho > map[p]) map[p] = rho;
        }

        if (!top) return;
        int xb = Mathf.FloorToInt((l.x - XMin) / XYCell), sb = Mathf.FloorToInt((l.y - SMin) / XYCell);
        if (xb >= 0 && xb < NX && sb >= 0 && sb < NS && l.z > Top[xb * NS + sb]) Top[xb * NS + sb] = l.z;
    }

    static int LegEnvelopeBin(float phi)
    {
        float u = phi / (2f * Mathf.PI);
        u -= Mathf.Floor(u);
        return Mathf.Clamp((int)(u * NPhi), 0, NPhi - 1);
    }

    /// <summary>
    /// how far a leg point (x, s, h in the shoe's foot frame) is outside the shoe where the shoe covers it (m, &lt;= 0
    /// inside or not covered); covered = false when the point is above the shoe's top edge (visible leg, fine)
    /// </summary>
    public float Outside(Vector3 l, out bool covered)
    {
        covered = false;
        int hb = Mathf.FloorToInt(l.z / HCell);
        if (hb < 0) return 0f; // below the outsole: the floor check's business
        if (hb >= NH) return 0f;
        float dx = l.x - Cx, ds = l.y - Sc;
        float rho = Mathf.Sqrt(dx * dx + ds * ds);
        int p = LegEnvelopeBin(Mathf.Atan2(ds, dx));
        // covered when the shoe has a surface at this angle within +-1 mm of this height (its top edge is above)
        float r = -1f;
        for (int d = -1; d <= 1; d++)
        {
            int h = hb + d;
            if (h < 0 || h >= NH) continue;
            r = Mathf.Max(r, Rmax[p * NH + h]);
        }

        float outRadial = 0f;
        if (r > 0f)
        {
            covered = true;
            outRadial = rho - r;
        }

        // in front of the throat the instep sits under the vamp: above the top-down surface = through it
        float outTop = 0f;
        if (l.x >= sh.XOpen && sh.InsidePlan(l.x, l.y, 0f))
        {
            int xb = Mathf.FloorToInt((l.x - XMin) / XYCell), sb = Mathf.FloorToInt((l.y - SMin) / XYCell);
            if (xb >= 0 && xb < NX && sb >= 0 && sb < NS && Top[xb * NS + sb] > 0f)
            {
                covered = true;
                outTop = l.z - Top[xb * NS + sb];
            }
        }

        return Mathf.Max(outRadial, outTop);
    }
}

/// <summary>
/// The whole-capture checks of a pair (hm_shoes sweep; also offline): per foot and frame the skinned shoe mesh against
/// the floor, every visible leg point against the actual shoe surface (ShoeVolume, 1 mm tolerance), the cut edge
/// inside the shoe, frame-to-frame jumps of the contact correction on a still ankle, and the hover in stance (stance from
/// the motion: low and slow before any correction). Poses come from callbacks, so the viewer checks its real bones and
/// proxies and the offline harness its own kinematics.
/// </summary>
public static class SneakerChecks
{
    /// <summary>the shoe proxies of a foot at the frame last posed: ankle position / rotation in a space whose floor is
    /// at floorY, toe and shin local rotations</summary>
    public delegate void ProxyPose(int foot, out Vector3 pa, out Quaternion ra, out Quaternion toe, out Quaternion shin);

    /// <summary>a shoe mesh vertex in the shoe's rest space (the ankle proxy's frame) for the toe and shin rotations</summary>
    public static Vector3 InShoe(SneakerBuildResult b, int foot, int v, Quaternion toe, Quaternion shin)
    {
        Vector3 rest = b.RestVertices[v];
        BoneWeight w = b.Weights[v];
        Vector3 aRest = b.BoneRest[SneakerBuilder.Bone(foot, SneakerBuilder.Ankle)];
        Vector3 Part(int bone)
        {
            int slot = bone - SneakerBuilder.PerFoot * foot;
            if (slot == SneakerBuilder.Toe)
            {
                Vector3 tRest = b.BoneRest[bone];
                return tRest + toe * (rest - tRest);
            }

            return slot == SneakerBuilder.Shin ? aRest + shin * (rest - aRest) : rest;
        }

        Vector3 p = w.weight0 * Part(w.boneIndex0);
        if (w.weight1 > 0f) p += w.weight1 * Part(w.boneIndex1);
        return p;
    }

    /// <summary>the foot's LegSamples vertices as posed now, in its shoe's rest space</summary>
    public delegate void LegPose(int foot, Vector3[] outRest);

    public const float Tolerance = 0.001f;

    /// <summary>stance (for the hover statistic): the outsole's lowest point at most this high and its lower end this slow
    /// before any correction, on 3 consecutive frames (SneakerFitSettings.ContactH / ContactV)</summary>
    public const float StanceH = 0.03f, StanceV = 0.10f;

    static float Q(List<float> v, float q)
    {
        if (v.Count == 0) return float.NaN;
        List<float> s = new(v);
        s.Sort();
        return s[Mathf.Clamp((int)(q * (s.Count - 1)), 0, s.Count - 1)];
    }

    static Dictionary<string, object> Stats(List<float> v) => new()
    {
        ["min"] = Q(v, 0f), ["p10"] = Q(v, 0.1f), ["median"] = Q(v, 0.5f), ["p90"] = Q(v, 0.9f), ["max"] = Q(v, 1f)
    };

    /// <param name="poseFrame">pose everything at frame k (before the foot callbacks)</param>
    public static Dictionary<string, object> Run(SneakerBuildResult b, int frames, Action<int> poseFrame, ProxyPose proxy,
        LegPose leg, float floorY, Func<int, float> time)
    {
        Dictionary<string, object> report = new() { ["frames"] = frames };
        float[][] footY = { new float[frames], new float[frames] }, heelY = { new float[frames], new float[frames] }, foreY = { new float[frames], new float[frames] };
        ShoeVolume[] vol = new ShoeVolume[2];
        int[][] dynTris = new int[2][];
        List<(Vector3 a, Vector3 b, Vector3 c, Vector3 n)> tongue = new();
        Vector3[][] posed = new Vector3[2][];
        for (int f = 0; f < 2; f++)
        {
            SneakerShape sh = b.Shapes[f];
            int lo = b.FootVertexStart[f], hi = b.FootVertexStart[f + 1];
            int shinBone = SneakerBuilder.Bone(f, SneakerBuilder.Shin);
            bool Rigid(int v) => b.Weights[v].boneIndex0 != shinBone && !(b.Weights[v].boneIndex1 == shinBone && b.Weights[v].weight1 > 0f);
            List<int> dyn = new();
            vol[f] = new ShoeVolume(sh.M, sh, b.RestVertices, b.Triangles, t =>
            {
                int a = b.Triangles[3 * t], c = b.Triangles[3 * t + 1], d = b.Triangles[3 * t + 2];
                if (a < lo || a >= hi) return false;
                if (Rigid(a) && Rigid(c) && Rigid(d)) return true;
                dyn.Add(t);
                return false;
            }, sh.LegAxis.x, sh.LegAxis.y);
            dynTris[f] = dyn.ToArray();
            if (b.Legs != null) posed[f] = new Vector3[b.Legs[f].Count];
        }

        List<float>[] meshMin = { new(), new() }, pokeMm = { new(), new() };
        int[] pokeFrames = new int[2], exposedFrames = new int[2], meshPen1 = new int[2], meshPen3 = new int[2];
        float[] pokeWorst = new float[2], exposedWorst = new float[2];
        string[] pokeWhere = new string[2], exposedWhere = new string[2];
        int[] pokeWorstK = { -1, -1 };
        List<string>[] pokeList = { new(), new() };
        for (int k = 0; k < frames; k++)
        {
            poseFrame(k);
            for (int f = 0; f < 2; f++)
            {
                SneakerShape sh = b.Shapes[f];
                FootMeasure m = sh.M;
                proxy(f, out Vector3 pa, out Quaternion ra, out Quaternion toe, out Quaternion shin);
                Vector3 aRest = b.BoneRest[SneakerBuilder.Bone(f, SneakerBuilder.Ankle)];
                float min = float.MaxValue, minH = float.MaxValue, minF = float.MaxValue;
                for (int v = b.FootVertexStart[f]; v < b.FootVertexStart[f + 1]; v++)
                {
                    float y = (pa + ra * (InShoe(b, f, v, toe, shin) - aRest)).y - floorY;
                    min = Mathf.Min(min, y);
                    if (m.Local(b.RestVertices[v]).x >= 0.55f * m.L) minF = Mathf.Min(minF, y);
                    else minH = Mathf.Min(minH, y);
                }

                meshMin[f].Add(min);
                footY[f][k] = min;
                heelY[f][k] = minH;
                foreY[f][k] = minF;
                if (min < -0.001f) meshPen1[f]++;
                if (min < -0.003f) meshPen3[f]++;
                if (b.Legs == null) continue;

                LegSamples legs = b.Legs[f];
                leg(f, posed[f]);
                // the tongue as posed (it rides on the shin): its outward-facing triangles
                tongue.Clear();
                foreach (int t in b.TongueFront)
                {
                    if (t < b.TongueTris[f].x || t >= b.TongueTris[f].y) continue;
                    Vector3 a = InShoe(b, f, b.Triangles[3 * t], toe, shin), c = InShoe(b, f, b.Triangles[3 * t + 1], toe, shin), d = InShoe(b, f, b.Triangles[3 * t + 2], toe, shin);
                    Vector3 nrm = Vector3.Cross(c - a, d - a); // the outer face is wound outward (Quad hint)
                    if (nrm.sqrMagnitude < 1e-14f) continue;
                    tongue.Add((a, c, d, nrm.normalized));
                }

                float worst = 0f, worstCut = 0f;
                string where = null, whereCut = null;

                void Test(Vector3 rest, bool cutEdge, string kind)
                {
                    Vector3 l = m.Local(rest);
                    float o = vol[f].Outside(l, out bool covered);
                    float ot = ThroughTongue(tongue, rest, out bool behind);
                    if (ot > o)
                    {
                        o = ot;
                        covered = true;
                        kind += " through the tongue";
                    }
                    else if (!covered && behind) covered = true; // under the tongue (it rides on the shin)

                    if (cutEdge)
                    {
                        float e = covered ? o : Mathf.Max(o, 0.002f); // not covered = above the top edge: the cut shows
                        if (e > worstCut)
                        {
                            worstCut = e;
                            whereCut = $"{kind} x {l.x:0.000} s {l.y:0.000} h {l.z:0.000}{(covered ? "" : " above the top edge")}";
                        }
                    }
                    else if (covered && o > worst)
                    {
                        worst = o;
                        where = $"{kind} x {l.x:0.000} s {l.y:0.000} h {l.z:0.000}";
                    }
                }

                for (int i = 0; i < legs.Count; i++)
                {
                    if (legs.H[i] >= b.FootCut) Test(posed[f][i], false, "vertex");
                }

                for (int e = 0; e < legs.EdgeA.Length; e++) Test(legs.Edge(posed[f], e), legs.EdgeCut[e], legs.EdgeCut[e] ? "cut edge" : "edge");
                for (int t = 0; t < legs.TriA.Length; t++) Test((posed[f][legs.TriA[t]] + posed[f][legs.TriB[t]] + posed[f][legs.TriC[t]]) / 3f, false, "face");
                pokeMm[f].Add(worst * 1000f);
                if (worst > Tolerance)
                {
                    pokeFrames[f]++;
                    if (pokeList[f].Count < 12) pokeList[f].Add($"f{k}:{worst * 1000f:0.0}");
                }

                if (worst > pokeWorst[f])
                {
                    pokeWorst[f] = worst;
                    pokeWorstK[f] = k;
                    pokeWhere[f] = where;
                }

                if (worstCut > Tolerance) exposedFrames[f]++;
                if (worstCut > exposedWorst[f])
                {
                    exposedWorst[f] = worstCut;
                    exposedWhere[f] = $"f{k} {whereCut}";
                }
            }
        }

        for (int f = 0; f < 2; f++)
        {
            SneakerFootFit fit = b.Fits?[f];
            Dictionary<string, object> d = new()
            {
                ["meshMin"] = Stats(meshMin[f]), ["meshBelow1mmFrames"] = meshPen1[f], ["meshBelow3mmFrames"] = meshPen3[f],
                ["legPokeFrames"] = pokeFrames[f], ["legPokeWorstMm"] = pokeWorst[f] * 1000f, ["legPokeWorstFrame"] = pokeWorstK[f],
                ["legPokeWhere"] = pokeWhere[f], ["legPokeList"] = pokeList[f], ["legPokeMm"] = Stats(pokeMm[f]),
                ["cutEdgeExposedFrames"] = exposedFrames[f], ["cutEdgeWorstMm"] = exposedWorst[f] * 1000f, ["cutEdgeWhere"] = exposedWhere[f]
            };
            if (fit != null)
            {
                // stance from the motion (before any correction): the sole's lower end low and slow for 3 frames
                List<float> hover = new(), lifts = new(), plants = new(), toes = new(), raw = new();
                List<string> hoverList = new();
                float jumpStill = 0f, jumpAny = 0f, pitchJump = 0f;
                int jumpK = -1;
                for (int k = 0; k < fit.N; k++)
                {
                    raw.Add(fit.RawMin[k]);
                    toes.Add(fit.Toe[k]);
                    lifts.Add(Mathf.Max(0f, fit.Shift[k]));
                    plants.Add(Mathf.Max(0f, -fit.Shift[k]));
                    bool stance = true;
                    for (int j = Mathf.Max(0, k - 1); j <= Mathf.Min(fit.N - 1, k + 1); j++)
                    {
                        if (fit.RawMin[j] > StanceH || fit.Speed[j] > StanceV) stance = false;
                    }

                    if (stance && k < frames)
                    {
                        hover.Add(Mathf.Max(0f, footY[f][k]) * 1000f);
                        if (footY[f][k] > 0.005f && hoverList.Count < 24) hoverList.Add($"f{k}:{footY[f][k] * 1000f:0}");
                    }

                    if (k == 0) continue;
                    float dj = Mathf.Abs(fit.Shift[k] - fit.Shift[k - 1]);
                    jumpAny = Mathf.Max(jumpAny, dj);
                    pitchJump = Mathf.Max(pitchJump, Mathf.Abs(fit.Pitch[k] - fit.Pitch[k - 1]));
                    if (Mathf.Abs(fit.AnkleY[k] - fit.AnkleY[k - 1]) < 0.004f && dj > jumpStill)
                    {
                        jumpStill = dj;
                        jumpK = k;
                    }
                }

                d["rawLowest"] = Stats(raw);
                d["lift"] = Stats(lifts);
                d["plant"] = Stats(plants);
                d["toeDeg"] = Stats(toes);
                d["maxPitchDeg"] = MaxAbs(fit.Pitch);
                d["stanceFrames"] = hover.Count;
                d["stanceHoverMm"] = Stats(hover);
                d["stanceHoverOver5mm"] = hoverList;
                d["jumpStillAnkleMm"] = jumpStill * 1000f;
                d["jumpStillAnkleFrame"] = jumpK;
                d["jumpAnyMm"] = jumpAny * 1000f;
                d["pitchJumpDeg"] = pitchJump;
                d["penetratingBefore"] = CountBelow(fit.RawMin, -0.001f);
            }

            report[f == 0 ? "left" : "right"] = d;
        }

        // review moments: standing (both soles down, slowest), stepping (one foot high, the other down), on the toes
        // (forefoot down, heel up, most toe bend)
        int standing = -1, stepping = -1, toesK = -1;
        float bestStand = float.MaxValue, bestStep = 0f, bestToe = float.MinValue;
        for (int k = 1; k < frames - 1; k++)
        {
            float speed = b.Fits != null ? Mathf.Max(b.Fits[0].Speed[k], b.Fits[1].Speed[k]) : 0f;
            float sumLow = Mathf.Abs(footY[0][k]) + Mathf.Abs(footY[1][k]);
            if (footY[0][k] < 0.01f && footY[1][k] < 0.01f && speed + 5f * sumLow < bestStand)
            {
                bestStand = speed + 5f * sumLow;
                standing = k;
            }

            for (int i = 0; i < 2; i++)
            {
                float up = footY[i][k], other = footY[1 - i][k];
                if (other < 0.01f && up > bestStep)
                {
                    bestStep = up;
                    stepping = k;
                }

                float toeDeg = b.Fits != null ? b.Fits[i].Toe[k] : 0f;
                if (foreY[i][k] < 0.012f && heelY[i][k] > 0.03f && toeDeg >= 0f)
                {
                    float score = heelY[i][k] + 0.002f * toeDeg;
                    if (score > bestToe)
                    {
                        bestToe = score;
                        toesK = k;
                    }
                }
            }
        }

        Dictionary<string, object> Moment(int k) => k < 0 ? null : new Dictionary<string, object>
        {
            ["frame"] = k, ["audioTime"] = time != null ? time(k) : k / 30f, ["leftLowest"] = footY[0][k], ["rightLowest"] = footY[1][k],
            ["leftHeel"] = heelY[0][k], ["rightHeel"] = heelY[1][k], ["leftFore"] = foreY[0][k], ["rightFore"] = foreY[1][k]
        };
        report["moments"] = new Dictionary<string, object> { ["standing"] = Moment(standing), ["stepping"] = Moment(stepping), ["toes"] = Moment(toesK) };
        return report;
    }

    /// <summary>how far a point is in front of the tongue's outer face (m, 0 when it is behind it or off the tongue): the
    /// nearest outer-face triangle of its central strip (within 15 mm) whose prism holds the point decides</summary>
    static float ThroughTongue(List<(Vector3 a, Vector3 b, Vector3 c, Vector3 n)> tris, Vector3 p, out bool behind)
    {
        float best = 0f, nearest = 0.015f, nearC = 0.02f;
        behind = false;
        bool inPrism = false, behindC = false;
        foreach ((Vector3 a, Vector3 b, Vector3 c, Vector3 n) in tris)
        {
            // fallback where the prisms of a bent strip leave a gap: the nearest triangle centre within 2 cm
            Vector3 cen = (a + b + c) / 3f;
            float dc = (p - cen).magnitude;
            if (dc < nearC)
            {
                nearC = dc;
                behindC = Vector3.Dot(p - cen, n) <= Tolerance;
            }

            float d = Vector3.Dot(p - a, n);
            if (Mathf.Abs(d) >= nearest) continue;
            Vector3 q = p - n * d, e0 = b - a, e1 = c - a, w = q - a;
            float d00 = Vector3.Dot(e0, e0), d01 = Vector3.Dot(e0, e1), d11 = Vector3.Dot(e1, e1), d20 = Vector3.Dot(w, e0), d21 = Vector3.Dot(w, e1);
            float den = d00 * d11 - d01 * d01;
            if (Mathf.Abs(den) < 1e-14f) continue;
            float v = (d11 * d20 - d01 * d21) / den, u = (d00 * d21 - d01 * d20) / den;
            if (v < 0f || u < 0f || u + v > 1f) continue;
            nearest = Mathf.Abs(d);
            best = Mathf.Max(0f, d);
            behind = d <= Tolerance;
            inPrism = true;
        }

        if (!inPrism) behind = behindC;
        return best;
    }

    static float MaxAbs(float[] a)
    {
        float m = 0f;
        foreach (float v in a) m = Mathf.Max(m, Mathf.Abs(v));
        return m;
    }

    static int CountBelow(float[] a, float t)
    {
        int n = 0;
        foreach (float v in a)
        {
            if (v < t) n++;
        }

        return n;
    }
}
