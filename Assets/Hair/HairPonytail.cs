using System;
using System.Collections.Generic;
using System.Linq;
using HeadMovementHair;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Ponytail style of the guide-strand hair (hair_groom.json style.type "ponytail"; every other style is untouched).
/// The hair is gathered at a tie on the back / crown of the head: each guide's rest shape runs from its root over the skull
/// (a great circle toward the tie, hugging the head capsule), through the tie, and on as the TAIL: a bundle that leaves the
/// tie along the tie normal drooping toward gravity, fanned out into a flat sheet toward the ends, each strand with a lateral
/// offset in the bundle that follows the side it entered from. The scalp part is pinned to the head (stiff shape constraint;
/// the scalp cap draws it, combed toward the tie), the tail is soft: it swings with the head, the neck, the back. Cards
/// start a little above the tie (the cap covers the scalp), normals turn around the tail axis (tube) so the tail reads from
/// every side. A small hair tie (scrunchie ring) and a barrette are drawn at the tie as vertex-coloured meshes on the head
/// bone (the same translucent attachment materials as the props). Parameters: groom style.ponytail (docs/HAIR_REFERENCE.md,
/// "Ponytail style"). Head-bone axes as everywhere in the hair: +x her left, +y up, +z BACKWARD (she faces -z).
/// </summary>
public partial class HairStrands
{
    sealed class PonytailCfg
    {
        public float TieAz = 180f, TieEl = 52f, TieLift = 0.010f;           // direction from the head centre (deg), tie centre above the hair surface (m)
        public float TailLen = 0.34f, TailJitter = 0.06f;                    // m of the rest model; +- fraction per guide (layered cut)
        public float Droop0 = 0.35f, DownSlerp = 0.28f;                      // tail leaves along slerp(tie normal, down, Droop0); then DownSlerp per 3 cm toward down
        public Vector2 Bundle = new(0.030f, 0.020f);                         // bundle radii (lateral, depth) at full offset (m)
        public float Flare = 2.4f;                                           // bundle width factor at the tail's end
        public float RampM = 0.05f;                                          // the offsets grow from 0 at the tie over this length
        public float SkullHold = 0.9f;                                       // per-iteration shape stiffness of the scalp part (pinned)
        public float TailRoot = 0.22f, TailFalloff = 0.18f;                  // per-frame shape stiffness at the tie, e-fold in tail fractions
        public float SettleRoot = 0.10f, SettleFalloff = 0.30f;
        public float Deviation = 0.8f;
        public float CardStartBack = 0.02f;                                  // cards start this far (m) above the tie, over the cap
        public float NeighbourBlend = 0.5f;
        public float SkullLayer = 0.004f;
        public bool Tie = true;
        public float TieBandM = 0.009f;                                     // scrunchie tube radius
        public Color TieColour = new(0.91f, 0.84f, 0.84f);
        public bool Clip = true;
        public float ClipAz = -158f, ClipEl = 30f, ClipAngle = -35f, ClipLen = 0.030f, ClipWidth = 0.013f;
        public Color ClipColour = new(0.36f, 0.72f, 0.90f);
    }

    PonytailCfg pt;
    bool ponytail;
    float[] ptTieS, ptTubePhi, ptTailLen, ptS0;
    Vector3[] ptRest;
    Vector3 ptTie, ptTieDir, ptAxis0, ptA, ptB;
    float ptHug;
    GameObject ptGo;
    MeshRenderer ptRenderer;
    Mesh ptMesh;

    public bool IsPonytail => ponytail;

    static Color Col(JToken t, Color fallback) => t is JArray a && a.Count >= 3 ? new Color(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>()) : fallback;

    /// <summary>style.type == "ponytail": read style.ponytail (missing values keep the defaults above)</summary>
    void ReadPonytail(JObject style)
    {
        ponytail = false;
        if (style == null || style.Value<string>("type") != "ponytail") return;
        JObject p = style["ponytail"] as JObject ?? new JObject();
        pt = new PonytailCfg();
        pt.TieAz = F(p, "tie_az_deg", pt.TieAz);
        pt.TieEl = F(p, "tie_el_deg", pt.TieEl);
        pt.TieLift = F(p, "tie_lift_m", pt.TieLift);
        pt.TailLen = F(p, "tail_length_m", pt.TailLen);
        pt.TailJitter = F(p, "tail_length_jitter", pt.TailJitter);
        pt.Droop0 = F(p, "tail_droop0", pt.Droop0);
        pt.DownSlerp = F(p, "tail_down_slerp", pt.DownSlerp);
        if (p["bundle_radius_m"] is JArray b && b.Count == 2) pt.Bundle = new Vector2(b[0].Value<float>(), b[1].Value<float>());
        pt.Flare = F(p, "flare", pt.Flare);
        pt.RampM = F(p, "offset_ramp_m", pt.RampM);
        pt.SkullHold = F(p, "skull_hold", pt.SkullHold);
        pt.Deviation = F(p, "deviation", pt.Deviation);
        pt.CardStartBack = F(p, "card_start_back_m", pt.CardStartBack);
        pt.NeighbourBlend = F(p, "neighbour_blend", pt.NeighbourBlend);
        pt.SkullLayer = F(p, "skull_layer_m", pt.SkullLayer);
        if (p["tail_hold"] is JObject th)
        {
            pt.TailRoot = F(th, "root", pt.TailRoot);
            pt.TailFalloff = F(th, "falloff", pt.TailFalloff);
            pt.SettleRoot = F(th, "settle_root", pt.SettleRoot);
            pt.SettleFalloff = F(th, "settle_falloff", pt.SettleFalloff);
        }

        if (p["tie"] is JObject t)
        {
            pt.Tie = t["enabled"] == null || t.Value<bool>("enabled");
            pt.TieBandM = F(t, "band_radius_m", pt.TieBandM);
            pt.TieColour = Col(t["colour"], pt.TieColour);
            if (t["clip"] is JObject c)
            {
                pt.Clip = c["enabled"] == null || c.Value<bool>("enabled");
                pt.ClipAz = F(c, "az_deg", pt.ClipAz);
                pt.ClipEl = F(c, "el_deg", pt.ClipEl);
                pt.ClipAngle = F(c, "angle_deg", pt.ClipAngle);
                pt.ClipLen = F(c, "length_m", pt.ClipLen);
                pt.ClipWidth = F(c, "width_m", pt.ClipWidth);
                pt.ClipColour = Col(c["colour"], pt.ClipColour);
            }
        }

        ponytail = true;
    }

    Vector3 PtClosest(Vector3 p)
    {
        Vector3 ab = ptB - ptA;
        float t = Mathf.Clamp01(Vector3.Dot(p - ptA, ab) / Mathf.Max(ab.sqrMagnitude, 1e-12f));
        return ptA + ab * t;
    }

    /// <summary>the point along the ray from the head centre in direction u at distance rad from the head capsule</summary>
    Vector3 PtSurface(Vector3 u, float rad)
    {
        float lo = 0f, hi = 0.5f;
        for (int i = 0; i < 32; i++)
        {
            float mid = 0.5f * (lo + hi);
            Vector3 p = headCentreLocal + u * mid;
            if ((p - PtClosest(p)).magnitude < rad) lo = mid;
            else hi = mid;
        }

        return headCentreLocal + u * lo;
    }

    static Vector3 Dir(float azDeg, float elDeg)
    {
        float az = azDeg * Mathf.Deg2Rad, el = elDeg * Mathf.Deg2Rad;
        return new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), -Mathf.Cos(az) * Mathf.Cos(el));
    }

    static float Smooth(float a, float b, float x) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, x));

    /// <summary>rest shapes (analytic, head-bone space), lengths, tie parameters and card tube angles of every guide; call
    /// after the colliders exist and before Allocate / Settle</summary>
    void BuildPonytailRest(Vector3[] roots, Vector3[] restJoints)
    {
        float ls = LengthScale;
        Vector3 headJ = restJoints[HeadJoint];
        ColliderDef hd = colliderDefs.FirstOrDefault(c => c.Head);
        ptA = restJoints[hd.A] - headJ + hd.Oa;
        ptB = restJoints[hd.B] - headJ + hd.Ob;
        float hr = hd.R > 0f ? hd.R + standoff : headRadius + standoff;
        if (hd.R <= 0f) ptA = ptB = headCentreLocal;
        ptHug = hr + ParticleRadius;
        ptTieDir = Dir(pt.TieAz, pt.TieEl);
        ptTie = PtSurface(ptTieDir, ptHug + pt.TieLift * ls);
        ptAxis0 = Vector3.Slerp(ptTieDir, Vector3.down, Mathf.Clamp01(pt.Droop0)).normalized;
        ptRest = new Vector3[guides * points];
        ptTieS = new float[guides];
        ptTubePhi = new float[guides];
        ptTailLen = new float[guides];
        ptS0 = new float[guides];

        const int M = 24;
        float step = 0.01f * ls;
        float f1 = 1f - Mathf.Pow(1f - Mathf.Clamp(pt.DownSlerp, 0.01f, 0.95f), step / (0.03f * ls));
        Vector3 c0 = headCentreLocal;
        List<Vector3> path = new(128);
        for (int k = 0; k < guides; k++)
        {
            System.Random rng = new(4711 + 31 * k);
            Vector3 root = roots[k];
            Vector3 u0 = (root - c0).normalized;
            float rootRad = (root - PtClosest(root)).magnitude;
            float vol = pt.SkullLayer * ls * Mathf.Clamp01((guideEl[k] + 30f) / 120f);
            path.Clear();
            path.Add(root);
            for (int m = 1; m <= M; m++)
            {
                float s = (float)m / M;
                Vector3 dir = Vector3.Slerp(u0, ptTieDir, s);
                float w1 = Smooth(0f, 0.12f, s);
                float rad = Mathf.Lerp(rootRad, ptHug, w1) + vol * Smooth(0f, 0.3f, s) * (1f - Smooth(0.6f, 1f, s)) + pt.TieLift * ls * Smooth(0.5f, 1f, s);
                path.Add(m == M ? ptTie : PtSurface(dir, rad));
            }

            int skullEnd = path.Count - 1;
            Vector3 vin = (ptTie - path[Mathf.Max(0, skullEnd - 3)]).normalized;
            Vector3 t0 = ptAxis0;
            Vector3 e1 = (Vector3.right - t0 * Vector3.Dot(Vector3.right, t0)).normalized;
            Vector3 e2 = Vector3.Cross(t0, e1);
            float c1 = Vector3.Dot(-vin, e1), c2 = Vector3.Dot(-vin, e2);
            float cn = Mathf.Sqrt(c1 * c1 + c2 * c2);
            if (cn < 0.3f)
            {
                float th = (float)(rng.NextDouble() * Math.PI * 2);
                c1 = Mathf.Cos(th);
                c2 = Mathf.Sin(th);
            }
            else
            {
                c1 /= cn;
                c2 /= cn;
            }

            float rk = 0.35f + 0.65f * Mathf.Sqrt((float)rng.NextDouble());
            float base1 = pt.Bundle.x * ls * rk * c1, base2 = pt.Bundle.y * ls * rk * c2;
            float tailLen = pt.TailLen * ls * (1f + pt.TailJitter * (2f * (float)rng.NextDouble() - 1f));
            ptTailLen[k] = tailLen;
            Vector3 centre = ptTie, dirT = ptAxis0;
            int nT = Mathf.CeilToInt(tailLen / step);
            for (int j = 1; j <= nT; j++)
            {
                dirT = Vector3.Slerp(dirT, Vector3.down, f1).normalized;
                centre += dirT * step;
                Vector3 a1 = (Vector3.right - dirT * Vector3.Dot(Vector3.right, dirT)).normalized;
                Vector3 a2 = Vector3.Cross(dirT, a1);
                float d = j * step;
                float ramp = Smooth(0f, pt.RampM * ls, d);
                float fl = 1f + (pt.Flare - 1f) * Smooth(0f, tailLen, d);
                path.Add(centre + a1 * (base1 * ramp * fl) + a2 * (base2 * ramp * fl));
            }

            // arc length, then resample at equal spacing
            float[] arc = new float[path.Count];
            for (int i = 1; i < path.Count; i++) arc[i] = arc[i - 1] + (path[i] - path[i - 1]).magnitude;
            float total = arc[^1];
            ptTieS[k] = arc[skullEnd] / total;
            guideLength[k] = total;
            int seg = 0;
            for (int i = 0; i < points; i++)
            {
                float a = total * i / (points - 1);
                while (seg < path.Count - 2 && arc[seg + 1] < a) seg++;
                float sl = Mathf.Max(arc[seg + 1] - arc[seg], 1e-7f);
                ptRest[k * points + i] = Vector3.Lerp(path[seg], path[seg + 1], Mathf.Clamp01((a - arc[seg]) / sl));
            }

            ptS0[k] = Mathf.Max(0f, ptTieS[k] - pt.CardStartBack * ls / total);
        }

        // tube angle of every guide's cards: the direction of its offset from the bundle axis, in the card frame (CardMeshJob)
        const float uRef = 0.45f;
        Vector3 AtU(int k, float u)
        {
            float s = Mathf.Clamp01(ptTieS[k] + Mathf.Clamp01(u) * (1f - ptTieS[k]));
            float x = s * (points - 1);
            int i = Mathf.Clamp((int)x, 0, points - 2);
            return Vector3.Lerp(ptRest[k * points + i], ptRest[k * points + i + 1], x - i);
        }

        Vector3 axis = Vector3.zero;
        for (int k = 0; k < guides; k++) axis += AtU(k, uRef);
        axis /= guides;
        float axisLen = 0.5f * ls;
        for (int k = 0; k < guides; k++)
        {
            Vector3 p = AtU(k, uRef), t = (AtU(k, uRef + 0.05f) - AtU(k, uRef - 0.05f)).normalized;
            Vector3 off = p - axis;
            Vector3 rel = p - headCentreLocal;
            float along = Mathf.Clamp(Vector3.Dot(rel, Vector3.down), 0f, axisLen);
            Vector3 n0 = p - (headCentreLocal + Vector3.down * along);
            n0 -= t * Vector3.Dot(n0, t);
            n0 = n0.sqrMagnitude > 1e-10f ? n0.normalized : Vector3.forward;
            Vector3 w0 = Vector3.Cross(t, n0);
            System.Random rng = new(99 + 17 * k);
            ptTubePhi[k] = off.magnitude < 0.004f * ls ? (float)(rng.NextDouble() * Math.PI * 2 - Math.PI) : Mathf.Atan2(Vector3.Dot(off, w0), Vector3.Dot(off, n0));
        }
    }

    void PonytailFillRest(int k)
    {
        int o = k * points;
        for (int i = 0; i < points; i++)
        {
            Vector3 p = ptRest[o + i];
            pos[o + i] = p;
            restLocal[o + i] = p;
            prev[o + i] = p;
        }

        guideInfo[k] = new float2(DeviationOf(k), guideSide[k]);
    }

    /// <summary>true while point i of guide k is on the scalp part (before the tie, plus half a segment)</summary>
    bool PtOnSkull(int k, int i) => (float)i / (points - 1) <= ptTieS[k] + 0.5f / (points - 1);

    float PonytailSettleK(int k, int i)
    {
        if (i < 2) return 0f;
        if (PtOnSkull(k, i)) return 0.9f;
        float s = (float)i / (points - 1);
        float u = Mathf.Clamp01((s - ptTieS[k]) / Mathf.Max(1e-3f, 1f - ptTieS[k]));
        return Mathf.Clamp01(pt.SettleRoot * Mathf.Exp(-u / Mathf.Max(pt.SettleFalloff, 1e-3f)));
    }

    /// <summary>dynamic shape stiffness: the scalp part pinned, the tail soft (a per-frame fraction toward the head-relative
    /// rest shape at the tie, decaying exponentially down the tail), converted to a per-iteration factor</summary>
    void PonytailShape()
    {
        int n = Mathf.Max(1, Substeps * Iterations);
        for (int g = 0; g < guides; g++)
        {
            for (int i = 0; i < points; i++)
            {
                if (i < 2)
                {
                    shapeK[g * points + i] = 0f;
                    continue;
                }

                if (PtOnSkull(g, i))
                {
                    shapeK[g * points + i] = Mathf.Clamp01(pt.SkullHold);
                    continue;
                }

                float s = (float)i / (points - 1);
                float u = Mathf.Clamp01((s - ptTieS[g]) / Mathf.Max(1e-3f, 1f - ptTieS[g]));
                float perFrame = Mathf.Clamp(pt.TailRoot * Mathf.Exp(-u / Mathf.Max(pt.TailFalloff, 1e-3f)), 0f, 0.99f);
                shapeK[g * points + i] = 1f - Mathf.Pow(1f - perFrame, 1f / n);
            }
        }
    }

    /// <summary>cap comb + the strand-across coordinate for the ponytail: strands run along the scalp toward the tie (a
    /// great circle through it); the coordinate across them is the angle around the tie axis x the head radius</summary>
    void PonytailCapComb(Vector3 d, Vector3 n, out Vector3 comb, out float cross)
    {
        Vector3 u = d.normalized;
        Vector3 toward = ptTieDir - u * Vector3.Dot(u, ptTieDir);
        toward -= n * Vector3.Dot(toward, n);
        comb = toward.sqrMagnitude > 1e-10f ? toward.normalized : Vector3.down;
        Vector3 a1 = Vector3.Cross(ptTieDir, Vector3.right).normalized;
        Vector3 a2 = Vector3.Cross(ptTieDir, a1);
        cross = Mathf.Atan2(Vector3.Dot(u, a2), Vector3.Dot(u, a1)) * headRadius;
    }

    HairCardLayout.PonytailCards PonytailCardData() => new()
    {
        S0 = ptS0, TieS = ptTieS, Phi = ptTubePhi, Blend = pt.NeighbourBlend
    };

    // ------------------------------------------------------------------------------------------------ the tie and the clip

    void BuildPonytailProps()
    {
        if (!pt.Tie && !pt.Clip) return;
        float ls = LengthScale;
        List<Vector3> V = new();
        List<Color32> C = new();
        List<int> I = new();
        void Quad(int a, int b, int c, int d)
        {
            I.Add(a); I.Add(b); I.Add(c);
            I.Add(a); I.Add(c); I.Add(d);
        }

        if (pt.Tie)
        {
            // scrunchie: a bumpy torus round the bundle at the tie, in the plane across the tail axis
            Vector3 axis = ptAxis0;
            Vector3 e1 = (Vector3.right - axis * Vector3.Dot(Vector3.right, axis)).normalized;
            Vector3 e2 = Vector3.Cross(axis, e1);
            float rm = pt.TieBandM * ls;
            float rx = pt.Bundle.x * ls * 0.75f + 0.2f * rm, rz = pt.Bundle.y * ls * 0.75f + 0.2f * rm;
            Vector3 centre = ptTie + axis * (0.45f * rm);
            const int NT = 28, NP = 8;
            int b0 = V.Count;
            Color32 col = pt.TieColour;
            for (int i = 0; i < NT; i++)
            {
                float th = 2f * Mathf.PI * i / NT;
                float bump = 1f + 0.16f * Mathf.Sin(9f * th);
                for (int j = 0; j < NP; j++)
                {
                    float ph = 2f * Mathf.PI * j / NP;
                    float r = rm * bump;
                    Vector3 radial = e1 * (Mathf.Cos(th) * (rx + r * Mathf.Cos(ph))) + e2 * (Mathf.Sin(th) * (rz + r * Mathf.Cos(ph)));
                    V.Add(centre + radial + axis * (r * Mathf.Sin(ph) * 0.9f));
                    float shade = 0.82f + 0.18f * Mathf.Cos(ph);
                    C.Add(new Color32((byte)(col.r * shade), (byte)(col.g * shade), (byte)(col.b * shade), 255));
                }
            }

            for (int i = 0; i < NT; i++)
            for (int j = 0; j < NP; j++)
            {
                int i1 = (i + 1) % NT, j1 = (j + 1) % NP;
                Quad(b0 + i * NP + j, b0 + i1 * NP + j, b0 + i1 * NP + j1, b0 + i * NP + j1);
            }
        }

        if (pt.Clip)
        {
            Vector3 u = Dir(pt.ClipAz, pt.ClipEl);
            Vector3 cpos = PtSurface(u, ptHug + 0.0045f * ls);
            Vector3 nrm = (cpos - PtClosest(cpos)).normalized;
            Vector3 up = Vector3.up - nrm * Vector3.Dot(Vector3.up, nrm);
            Vector3 tx = Vector3.Cross(up.normalized, nrm).normalized; // horizontal along the surface
            Vector3 long1 = Quaternion.AngleAxis(pt.ClipAngle, nrm) * tx;
            Vector3 wide = Vector3.Cross(nrm, long1).normalized;
            float hl = 0.5f * pt.ClipLen * ls, hw = 0.5f * pt.ClipWidth * ls, th2 = 0.0035f * ls;
            const int NO = 28;
            Color32 col = pt.ClipColour;
            // stadium outline (two half circles joined by straight sides)
            Vector3[] outline = new Vector3[NO];
            for (int i = 0; i < NO; i++)
            {
                float a = 2f * Mathf.PI * i / NO;
                float cx = Mathf.Cos(a), sy = Mathf.Sin(a);
                float x = Mathf.Sign(cx) * (Mathf.Max(0f, hl - hw) + Mathf.Abs(cx) * hw);
                outline[i] = long1 * x + wide * (sy * hw);
            }

            int bt = V.Count;
            V.Add(cpos + nrm * th2);
            C.Add(new Color32((byte)(col.r * 1.08f > 255 ? 255 : col.r * 1.08f), (byte)Mathf.Min(255, col.g * 1.08f), (byte)Mathf.Min(255, col.b * 1.05f), 255));
            for (int i = 0; i < NO; i++)
            {
                V.Add(cpos + outline[i] * 0.96f + nrm * th2 * 0.85f);
                C.Add(col);
            }

            for (int i = 0; i < NO; i++)
            {
                int a = bt + 1 + i, b = bt + 1 + (i + 1) % NO;
                I.Add(bt); I.Add(a); I.Add(b);
            }

            int sb = V.Count;
            for (int i = 0; i < NO; i++)
            {
                V.Add(cpos + outline[i] * 0.96f + nrm * th2 * 0.85f);
                C.Add(col);
                V.Add(cpos + outline[i] + nrm * 0.0005f * ls);
                C.Add(new Color32((byte)(col.r * 0.7f), (byte)(col.g * 0.7f), (byte)(col.b * 0.7f), 255));
            }

            for (int i = 0; i < NO; i++)
            {
                int i1 = (i + 1) % NO;
                Quad(sb + 2 * i, sb + 2 * i1, sb + 2 * i1 + 1, sb + 2 * i + 1);
            }
        }

        ptMesh = new Mesh { name = "Hair tie" };
        ptMesh.SetVertices(V);
        ptMesh.SetTriangles(I, 0);
        ptMesh.SetColors(C);
        ptMesh.RecalculateNormals();
        ptMesh.RecalculateBounds();
        ptGo = new GameObject("Hair tie") { layer = avatar.gameObject.layer };
        ptGo.transform.SetParent(avatar.Bone(HeadJoint), false);
        ptGo.AddComponent<MeshFilter>().sharedMesh = ptMesh;
        ptRenderer = ptGo.AddComponent<MeshRenderer>();
        ptRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        ptRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        Material colour = avatar.Attach(ptRenderer, Color.white, null, true, 0.85f);
        colour.SetFloat("_Specular", 0.2f);
        colour.SetFloat("_SpecPower", 30f);
        avatar.AppearanceChanged += PonytailAppearance;
        PonytailAppearance(avatar);
    }

    void PonytailAppearance(SmplxAvatar a)
    {
        if (ptRenderer == null || a == null) return;
        float o = HeadProps.OpacityFor(a.Opacity, 0.15f);
        float blend = SmplxAvatar.AlphaMode == SmplxAvatar.AlphaModes.Raw ? -1f : a.Display != null ? a.Display.BlendAlpha(o) : o;
        foreach (Material m in ptRenderer.sharedMaterials)
        {
            if (m == null) continue;
            m.SetFloat("_Opacity", o);
            m.SetFloat("_BlendAlpha", blend);
        }

        ptRenderer.enabled = Visible && a.Visible && o > 0.005f && a.CurrentFrame >= 0;
    }

    void DestroyPonytailProps()
    {
        if (avatar != null) avatar.AppearanceChanged -= PonytailAppearance;
        if (avatar != null && ptRenderer != null) avatar.Detach(ptRenderer);
        if (ptMesh != null) Destroy(ptMesh);
        if (ptGo != null) Destroy(ptGo);
    }

    /// <summary>numbers for hm_hair state / playtests</summary>
    Dictionary<string, object> PonytailState()
    {
        if (!ponytail) return null;
        float ls = LengthScale;
        return new Dictionary<string, object>
        {
            ["tieHeadLocal"] = new[] { ptTie.x, ptTie.y, ptTie.z }, ["tailAxis0"] = new[] { ptAxis0.x, ptAxis0.y, ptAxis0.z },
            ["tieS"] = ptTieS.Average(), ["tailLengthMeanM"] = ptTailLen.Average() / ls, ["guideLengthMeanM"] = guideLength.Average() / ls,
            ["tieElDeg"] = pt.TieEl, ["tieAzDeg"] = pt.TieAz, ["propDrawn"] = ptRenderer != null && ptRenderer.enabled
        };
    }
}
