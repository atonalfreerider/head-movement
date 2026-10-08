using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Colours of the glowing skeletons (Dancer.cs) per frame and joint (VIEWER_SPEC 3.3 / 3.8). Two modes:
///
/// RHYTHM (the default view): on each beat a pulse of light travels through each skeleton, starting where the body
/// meets the floor (the stance foot / feet of that frame), up the legs, through the pelvis and spine and out through the
/// arms and head over TravelSeconds; the swing leg lights last (the pulse runs down it from the hip). Strength by beat
/// type (measure downbeat 1, the other zouk "1" 0.85, the accent eighths 0.6; the remaining eighths WeakStrength) and,
/// per dancer and body part (legs / trunk / arms / head), by how well that dancer's own kinematic accent lands on the
/// beat: the nearest |jerk| peak (timing.json spline jerk, reliable joints only) within AccentWindow of the beat ->
/// gain lerp(MissGain, HitGain, exp(-(dt / AccentSigma)^2)) (on the beat it flares, without an accent it is dim) and the
/// part's pulse arrives dt later (a late part lags visibly, an early one leads). Without timing.json the beats come
/// from the capture's beat grid and the accents from the skeleton's own jerk (Dancer.JerkSeries: central differences of
/// the exported joints, zero-phase Gaussian JerkSmoothFrames before peak picking; AccentSource says which).
///
/// PHYSICS (the physics view state / layer): every limb is coloured by an ESTIMATED axial load on one shared scale for
/// both dancers - tension orange, neutral grey-white, compression blue, brighter with magnitude. The skeleton material
/// multiplies the (LDR, Color32) line colour by ~6.4 in HDR and bloom adds to it, so a colour's minor channels must stay
/// low or the screen shifts its hue (review 2026-10-07: tension (1, 0.42, 0.08) x 1.5 showed yellow-gold, hue ~49 deg):
/// LoadColour scales brightness with the magnitude only down from the base (never above 1: no clipped red channel) and
/// the bases are deep - tension (1, 0.22, 0.02), compression (0.08, 0.28, 1). DisplayColour (legend) approximates the
/// screen: linear colour x DisplayGain, clamped.
///   legs  = -(vertical support factor) x (that leg's share): the stance leg(s) carry the body weight in compression;
///           support factor = F_net,y / (m g) from physics.json (L0 centroidal dynamics) when present, else 1 + a_y / g
///           of the skeleton's torso COM; the share between two stance feet by the lever rule (the foot nearer the
///           COM's floor projection carries more); a swing leg is neutral
///   arms  = the partner-connection estimate (PartnerConnection spring-damper-inertia signal, + = tension) on the arm
///           whose hand holds the partner (lead hands; the follower's hand when hand-in-hand), neutral otherwise
///   trunk = -0.55 x support factor (the upper body's weight on the spine), head / neck = 0.4 of it
/// These are model estimates from motion, never measurements; the legend says so (SkeletonLegend).
/// All per-frame values are precomputed at load; Colours() fills a cached array (no allocation per frame).
/// </summary>
public class SkeletonStyle
{
    public enum Mode
    {
        Rhythm,
        Physics
    }

    public const int Joints = 24;

    // rhythm
    public float TravelSeconds = 0.18f;
    public float RiseSeconds = 0.025f;
    public float DecaySeconds = 0.14f;
    public float WeakStrength = 0f;
    public float AccentWindow = 0.2f;
    public float AccentSigma = 0.07f;
    public float HitGain = 1.6f;
    public float MissGain = 0.45f;
    public float IdleLevel = 0.22f;
    public float PulseLevel = 1.9f;

    // physics
    public static readonly Color TensionColour = new(1f, 0.22f, 0.02f);
    public static readonly Color CompressionColour = new(0.08f, 0.28f, 1f);
    public static readonly Color NeutralColour = new(0.78f, 0.78f, 0.82f);

    /// <summary>legend: linear-light gain from the line colour to the screen (skeleton material ~6.4 x the translucent
    /// body over it ~0.45)</summary>
    public const float DisplayGain = 2.9f;

    // rhythm accents without timing.json: Gaussian sigma (frames) on the skeleton's |jerk| before peak picking
    public float JerkSmoothFrames = 1.5f;

    static readonly int[] Parent = { -1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19, 20, 21 };

    // body part of each joint: 0 legs, 1 trunk, 2 arms, 3 head
    static readonly int[] Group = { 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 3, 2, 2, 3, 2, 2, 2, 2, 2, 2, 2, 2 };
    public static readonly string[] GroupNames = { "legs", "trunk", "arms", "head" };

    class Track
    {
        public Role Role;
        public Color Base;
        public float[] Dist;        // [frame * 24 + j] normalised skeleton distance from the stance foot (head = 1)
        public bool[] StanceL, StanceR;
        public float[] Gain, Lag;   // [beat * 4 + group]
        public bool AccentsKnown;
        public string AccentSource = "none";
        public float[] Load;        // [frame * 24 + j] signed load (+ tension, - compression)
        public float[] Support;     // per frame vertical support factor
        public readonly Color[] Out = new Color[Joints];
        public float[] GroupHit = new float[4], GroupLagMs = new float[4];
        public int[] GroupBeats = new int[4];
    }

    readonly Dictionary<Role, Track> tracks = new();
    double[] frameAudio;
    float[] beatT, beatS;
    int[] beatKind; // 0 downbeat, 1 other type 1, 2 accent, 3 weak
    int frameCount;

    public Mode Current = Mode.Rhythm;
    public string BeatSource { get; private set; } = "none";
    public string AccentSource { get; private set; } = "none";
    public string AccentSourceOf(Role role) => tracks.TryGetValue(role, out Track t) ? t.AccentSource : "none";
    public string LoadSource { get; private set; } = "none";
    public int BeatCount => beatT?.Length ?? 0;

    /// <summary>build everything for a capture (beats: timing.json grid when present, else the capture's beat grid)</summary>
    public void Init(Dancer lead, Dancer follow, CaptureTimeline timeline, TimingData timing, BeatGrid grid,
        PhysicsData physics, PartnerConnection connection)
    {
        tracks.Clear();
        AccentSource = "none";
        frameCount = Mathf.Min(lead.FrameCount, follow.FrameCount, timeline.Count);
        frameAudio = new double[frameCount];
        for (int f = 0; f < frameCount; f++) frameAudio[f] = timeline.AudioTimeOf(f);
        BuildBeats(timeline, timing, grid);

        float dt = frameCount > 1 ? (float)((frameAudio[^1] - frameAudio[0]) / (frameCount - 1)) : 1f / 30f;
        foreach (Dancer d in new[] { lead, follow })
        {
            Track t = new()
            {
                Role = d.DancerRole,
                Base = d.DancerRole == Role.Lead ? new Color(1f, 0.05f, 0f) : Color.white
            };
            Stance(d, dt, t);
            Distances(d, t);
            Accents(d, timing, timeline, t);
            tracks[t.Role] = t;
        }

        Loads(lead, follow, physics, connection, timeline, dt);
    }

    void BuildBeats(CaptureTimeline timeline, TimingData timing, BeatGrid grid)
    {
        List<(float t, int kind)> beats = new();
        if (timing != null && timing.Beats.Count > 0)
        {
            foreach (TimingData.Beat b in timing.Beats)
            {
                int kind = b.Type == 1 ? (b.Count == 0 ? 0 : 1) : b.Type == 2 ? 2 : 3;
                beats.Add(((float)timeline.ToAudio(b.T), kind));
            }

            BeatSource = "timing.json beats.grid";
        }
        else if (grid != null && grid.Count > 0)
        {
            for (int i = 0; i < grid.Count; i++)
            {
                int kind = grid.Types[i] == 1 ? (i % BeatGrid.BeatsPerMeasure == 0 ? 0 : 1) : grid.Types[i] == 2 ? 2 : 3;
                beats.Add((grid.Times[i], kind));
            }

            BeatSource = "capture beat grid (zouk-time-analysis.json)";
        }

        // keep beats around the capture only
        double a = frameAudio.Length > 0 ? frameAudio[0] - 1.0 : 0, b2 = frameAudio.Length > 0 ? frameAudio[^1] + 1.0 : 0;
        beats = beats.Where(x => x.t >= a && x.t <= b2).OrderBy(x => x.t).ToList();
        beatT = beats.Select(x => x.t).ToArray();
        beatKind = beats.Select(x => x.kind).ToArray();
        beatS = beatKind.Select(k => k switch { 0 => 1f, 1 => 0.85f, 2 => 0.6f, _ => WeakStrength }).ToArray();
    }

    /// <summary>stance per foot per frame: foot joint near this dancer's floor level and slow (hysteresis-free, median
    /// of 3 frames)</summary>
    static void Stance(Dancer d, float dt, Track t)
    {
        int n = d.FrameCount;
        t.StanceL = FootStance(d, n, dt, SmplJoint.L_Foot, SmplJoint.L_Ankle);
        t.StanceR = FootStance(d, n, dt, SmplJoint.R_Foot, SmplJoint.R_Ankle);
    }

    /// <summary>foot height per frame above this dancer's floor level: the lower of the foot joint and the ankle - 6 cm,
    /// minus its 3rd percentile over the take (NaN where the joints are missing)</summary>
    public static float[] FootHeight(Dancer d, int n, SmplJoint foot, SmplJoint ankle)
    {
        float[] h = new float[n];
        for (int f = 0; f < n; f++) h[f] = Mathf.Min(d.Joint(f, foot).y, d.Joint(f, ankle).y - 0.06f);
        float[] sorted = h.Where(x => !float.IsNaN(x)).OrderBy(x => x).ToArray();
        float floor = sorted.Length > 0 ? sorted[Mathf.FloorToInt((sorted.Length - 1) * 0.03f)] : 0f;
        for (int f = 0; f < n; f++) h[f] -= floor;
        return h;
    }

    public static bool[] FootStance(Dancer d, int n, float dt, SmplJoint foot, SmplJoint ankle, float heightM = 0.05f, float speedMps = 0.6f)
    {
        float[] h = FootHeight(d, n, foot, ankle);
        bool[] raw = new bool[n];
        for (int f = 0; f < n; f++)
        {
            Vector3 a = d.Joint(Mathf.Max(0, f - 1), foot), b = d.Joint(Mathf.Min(n - 1, f + 1), foot);
            float span = (Mathf.Min(n - 1, f + 1) - Mathf.Max(0, f - 1)) * dt;
            float speed = span > 0 ? new Vector2(b.x - a.x, b.z - a.z).magnitude / span : 0f;
            raw[f] = !float.IsNaN(h[f]) && h[f] < heightM && speed < speedMps;
        }

        bool[] o = new bool[n];
        for (int f = 0; f < n; f++)
        {
            int c = 0;
            for (int k = -1; k <= 1; k++) c += raw[Mathf.Clamp(f + k, 0, n - 1)] ? 1 : 0;
            o[f] = c >= 2;
        }

        return o;
    }

    /// <summary>per frame, the distance of every joint from the stance foot along the skeleton tree, divided by the
    /// stance foot -> head distance (multi-source relaxation over the 23 bones)</summary>
    void Distances(Dancer d, Track t)
    {
        t.Dist = new float[frameCount * Joints];
        float[] dist = new float[Joints];
        float[] len = new float[Joints];
        for (int f = 0; f < frameCount; f++)
        {
            for (int j = 1; j < Joints; j++)
            {
                float l = Vector3.Distance(d.Joint(f, (SmplJoint)j), d.Joint(f, (SmplJoint)Parent[j]));
                len[j] = float.IsNaN(l) ? 0.1f : l;
            }

            for (int j = 0; j < Joints; j++) dist[j] = float.MaxValue;
            bool l0 = t.StanceL[f], r0 = t.StanceR[f];
            if (!l0 && !r0)
            {
                // flight / both feet moving: the lower foot is where the pulse starts
                l0 = d.Joint(f, SmplJoint.L_Foot).y <= d.Joint(f, SmplJoint.R_Foot).y;
                r0 = !l0;
            }

            if (l0) dist[(int)SmplJoint.L_Foot] = 0f;
            if (r0) dist[(int)SmplJoint.R_Foot] = 0f;
            for (int it = 0; it < 12; it++)
            {
                bool changed = false;
                for (int j = 1; j < Joints; j++)
                {
                    int p = Parent[j];
                    if (dist[p] < float.MaxValue && dist[p] + len[j] < dist[j] - 1e-6f)
                    {
                        dist[j] = dist[p] + len[j];
                        changed = true;
                    }

                    if (dist[j] < float.MaxValue && dist[j] + len[j] < dist[p] - 1e-6f)
                    {
                        dist[p] = dist[j] + len[j];
                        changed = true;
                    }
                }

                if (!changed) break;
            }

            float head = Mathf.Max(0.3f, dist[(int)SmplJoint.Head]);
            for (int j = 0; j < Joints; j++) t.Dist[f * Joints + j] = dist[j] / head;
        }
    }

    /// <summary>|jerk| per frame per SMPL-24 joint from the dancer's skeleton (Dancer.JerkSeries), smoothed by a zero-phase
    /// Gaussian of JerkSmoothFrames so single-frame fit noise does not make peaks; null when unavailable</summary>
    float[][] SkeletonJerk(Dancer d)
    {
        float[][] per = new float[Joints][];
        for (int x = 0; x < Joints; x++)
        {
            float[] series = d.JerkSeries((SmplJoint)x);
            if (series == null || series.Length < frameCount) return null;
            per[x] = Gauss(series.Take(frameCount).ToArray(), JerkSmoothFrames);
        }

        float[][] o = new float[frameCount][];
        for (int f = 0; f < frameCount; f++)
        {
            o[f] = new float[Joints];
            for (int x = 0; x < Joints; x++) o[f][x] = float.IsNaN(per[x][f]) ? 0f : per[x][f];
        }

        return o;
    }

    /// <summary>per beat and body part: accent gain and lag from the nearest |jerk| peak of that dancer's part</summary>
    void Accents(Dancer d, TimingData timing, CaptureTimeline timeline, Track t)
    {
        int nb = beatT.Length;
        t.Gain = new float[nb * 4];
        t.Lag = new float[nb * 4];
        for (int i = 0; i < t.Gain.Length; i++) t.Gain[i] = 1f;
        float[][] jerk = null;
        string source;
        if (timing != null && timing.Jerk.TryGetValue(d.DancerRole, out float[][] j) && j.Length >= frameCount)
        {
            jerk = j;
            source = "timing.json spline jerk (reliable joints)";
        }
        else
        {
            jerk = SkeletonJerk(d);
            source = jerk != null
                ? $"skeleton jerk fallback ({d.JerkSource}, Gaussian {JerkSmoothFrames:0.#} frames; no timing.json jerk)"
                : "none (neutral: no jerk)";
        }

        t.AccentsKnown = jerk != null;
        t.AccentSource = source;
        AccentSource = tracks.Count == 0 || AccentSource == source || AccentSource == "none" ? source : $"{AccentSource} | {d.DancerRole}: {source}";
        if (jerk == null) return;

        for (int g = 0; g < 4; g++)
        {
            int[] members = Enumerable.Range(0, Joints).Where(x => Group[x] == g).ToArray();
            // reliable joints of this part: timing.py writes unreliable ones as 0
            members = members.Where(x => Enumerable.Range(0, frameCount).Any(f => jerk[f][x] > 0)).ToArray();
            if (members.Length == 0)
            {
                for (int b = 0; b < nb; b++) t.Gain[b * 4 + g] = 1f; // unknown part: neutral
                continue;
            }

            float[] e = new float[frameCount];
            for (int f = 0; f < frameCount; f++)
            {
                float m = 0;
                foreach (int x in members) m = Mathf.Max(m, jerk[f][x]);
                e[f] = m;
            }

            float[] sorted = e.OrderBy(x => x).ToArray();
            float p50 = sorted[sorted.Length / 2], p90 = sorted[Mathf.Min(sorted.Length - 1, (int)(sorted.Length * 0.9f))];
            float scale = Mathf.Max(1e-3f, p90);
            // peaks: local maxima above the median
            List<(float t, float s)> peaks = new();
            for (int f = 1; f < frameCount - 1; f++)
            {
                if (e[f] >= e[f - 1] && e[f] > e[f + 1] && e[f] > p50)
                {
                    // parabolic sub-frame refinement
                    float den = e[f - 1] - 2 * e[f] + e[f + 1];
                    float off = Mathf.Abs(den) > 1e-6f ? Mathf.Clamp(0.5f * (e[f - 1] - e[f + 1]) / den, -0.5f, 0.5f) : 0f;
                    double tf = frameAudio[f] + off * (frameAudio[Mathf.Min(frameCount - 1, f + 1)] - frameAudio[Mathf.Max(0, f - 1)]) * 0.5;
                    peaks.Add(((float)tf, Mathf.Clamp01(e[f] / scale)));
                }
            }

            int hits = 0, counted = 0;
            float lagSum = 0;
            for (int b = 0; b < nb; b++)
            {
                if (beatT[b] < frameAudio[0] || beatT[b] > frameAudio[^1]) continue;
                float best = float.MaxValue, bestS = 0;
                foreach ((float pt, float ps) in peaks)
                {
                    float dtp = pt - beatT[b];
                    if (Mathf.Abs(dtp) <= AccentWindow && Mathf.Abs(dtp) < Mathf.Abs(best))
                    {
                        best = dtp;
                        bestS = ps;
                    }
                }

                if (best == float.MaxValue)
                {
                    t.Gain[b * 4 + g] = MissGain;
                    t.Lag[b * 4 + g] = 0f;
                }
                else
                {
                    float q = Mathf.Exp(-(best / AccentSigma) * (best / AccentSigma)) * Mathf.Lerp(0.6f, 1f, bestS);
                    t.Gain[b * 4 + g] = Mathf.Lerp(MissGain, HitGain, q);
                    t.Lag[b * 4 + g] = Mathf.Clamp(best, -0.12f, AccentWindow);
                }

                if (beatKind[b] <= 2)
                {
                    counted++;
                    if (best != float.MaxValue && Mathf.Abs(best) <= 0.06f) hits++;
                    if (best != float.MaxValue) lagSum += best;
                }
            }

            t.GroupBeats[g] = counted;
            t.GroupHit[g] = counted > 0 ? hits / (float)counted : 0f;
            t.GroupLagMs[g] = counted > 0 ? 1000f * lagSum / counted : 0f;
        }
    }

    void Loads(Dancer lead, Dancer follow, PhysicsData physics, PartnerConnection connection, CaptureTimeline timeline, float dt)
    {
        LoadSource = physics != null ? "physics.json F_net + contact; partner connection estimate" : "skeleton COM acceleration + foot stance; partner connection estimate";
        int[] physFrame = null;
        if (physics != null)
        {
            double[] pa = physics.T.Select(x => timeline.ToAudio(x)).ToArray();
            physFrame = new int[frameCount];
            for (int f = 0; f < frameCount; f++) physFrame[f] = CaptureTimeline.Nearest(pa, frameAudio[f]);
        }

        foreach (Dancer d in new[] { lead, follow })
        {
            Track t = tracks[d.DancerRole];
            t.Load = new float[frameCount * Joints];
            t.Support = new float[frameCount];
            // vertical support factor from the skeleton: 1 + a_y / g of the torso COM (smoothed)
            float[] y = new float[frameCount];
            for (int f = 0; f < frameCount; f++) y[f] = d.CenterOfMass(f).y;
            y = Gauss(y, 2.5f);
            PhysicsData.DancerTrack pt = null;
            physics?.Dancers.TryGetValue(d.DancerRole, out pt);
            for (int f = 0; f < frameCount; f++)
            {
                float ay = (y[Mathf.Min(frameCount - 1, f + 1)] - 2 * y[f] + y[Mathf.Max(0, f - 1)]) / (dt * dt);
                float support = Mathf.Clamp(1f + ay / 9.81f, 0f, 2.5f);
                bool sl = t.StanceL[f], sr = t.StanceR[f];
                if (pt != null && physFrame != null)
                {
                    int k = physFrame[f];
                    if (k < pt.FNet.Length && pt.MassKg > 1 && !float.IsNaN(pt.FNet[k].y)) support = Mathf.Clamp(pt.FNet[k].y / (pt.MassKg * 9.81f), 0f, 2.5f);
                    if (pt.Contact != null && k < pt.Contact.Length && pt.Contact[k] != null)
                    {
                        string c = pt.Contact[k];
                        sl = c.Contains("L");
                        sr = c.Contains("R");
                    }
                }

                t.Support[f] = support;
                Vector3 com = d.CenterOfMass(f);
                Vector3 fl = (d.Joint(f, SmplJoint.L_Foot) + d.Joint(f, SmplJoint.L_Ankle)) * 0.5f;
                Vector3 fr = (d.Joint(f, SmplJoint.R_Foot) + d.Joint(f, SmplJoint.R_Ankle)) * 0.5f;
                float dl = new Vector2(fl.x - com.x, fl.z - com.z).magnitude, dr = new Vector2(fr.x - com.x, fr.z - com.z).magnitude;
                float shareL = sl && sr ? dr / Mathf.Max(1e-4f, dl + dr) : sl ? 1f : 0f;
                float shareR = sl && sr ? 1f - shareL : sr ? 1f : 0f;
                if (!sl && !sr) shareL = shareR = 0f; // flight: no leg load
                float legL = -support * shareL, legR = -support * shareR, trunk = -0.55f * support;
                int o = f * Joints;
                foreach (int j in new[] { 1, 4, 7, 10 }) t.Load[o + j] = legL;
                foreach (int j in new[] { 2, 5, 8, 11 }) t.Load[o + j] = legR;
                foreach (int j in new[] { 0, 3, 6, 9 }) t.Load[o + j] = trunk;
                t.Load[o + 12] = t.Load[o + 15] = 0.4f * trunk;
                t.Load[o + 13] = t.Load[o + 14] = 0.5f * trunk;
            }
        }

        if (connection == null) return;
        // arms: the partner connection estimate on the arm(s) that hold the partner
        foreach (PartnerConnection.Connection c in connection.Connections)
        {
            if (c.Active == null || c.Signal == null) continue;
            bool leadLeft = c.Name.Contains("L hand"), leadRight = c.Name.Contains("R hand");
            if (!leadLeft && !leadRight) continue; // the chest frame is not an arm
            int[] leadArm = leadLeft ? new[] { 16, 18, 20, 22 } : new[] { 17, 19, 21, 23 };
            for (int f = 0; f < Mathf.Min(frameCount, c.Active.Length); f++)
            {
                if (!c.Active[f]) continue;
                float s = Mathf.Clamp(c.Signal[f], -1.5f, 1.5f);
                int o = f * Joints;
                foreach (int j in leadArm) tracks[Role.Lead].Load[o + j] = s;
                // the follower's arm when her hand is the contact point
                Vector3 p = c.FollowContact[f];
                int[] followArm = null;
                if ((p - follow.GetLeftHandContact(f)).sqrMagnitude < 1e-8f) followArm = new[] { 16, 18, 20, 22 };
                else if ((p - follow.GetRightHandContact(f)).sqrMagnitude < 1e-8f) followArm = new[] { 17, 19, 21, 23 };
                if (followArm != null)
                {
                    foreach (int j in followArm) tracks[Role.Follow].Load[o + j] = s;
                }
            }
        }
    }

    static float[] Gauss(float[] x, float sigma)
    {
        int n = x.Length, r = Mathf.CeilToInt(3 * sigma);
        float[] o = new float[n];
        for (int i = 0; i < n; i++)
        {
            float s = 0, w = 0;
            for (int k = -r; k <= r; k++)
            {
                int j = Mathf.Clamp(i + k, 0, n - 1);
                if (float.IsNaN(x[j])) continue;
                float g = Mathf.Exp(-0.5f * k * k / (sigma * sigma));
                s += x[j] * g;
                w += g;
            }

            o[i] = w > 0 ? s / w : x[i];
        }

        return o;
    }

    // ------------------------------------------------------------------ per frame

    /// <summary>pulse intensity of a joint at an audio time (sum over the nearby beats)</summary>
    public float Pulse(Role role, int frame, int joint, double time)
    {
        if (!tracks.TryGetValue(role, out Track t) || beatT == null || beatT.Length == 0) return 0f;
        frame = Mathf.Clamp(frame, 0, frameCount - 1);
        float dist = t.Dist[frame * Joints + joint];
        int g = Group[joint];
        float sum = 0;
        int i = UpperBound(beatT, (float)time + 0.15f);
        for (int b = i - 1; b >= 0; b--)
        {
            float age = (float)time - beatT[b];
            if (age > TravelSeconds * 2.2f + DecaySeconds * 5f + AccentWindow) break;
            if (beatS[b] <= 0) continue;
            float arrive = TravelSeconds * dist + t.Lag[b * 4 + g];
            float x = age - arrive;
            float env = x < 0 ? Mathf.Exp(-(x / RiseSeconds) * (x / RiseSeconds)) : Mathf.Exp(-x / DecaySeconds);
            sum += beatS[b] * t.Gain[b * 4 + g] * env;
        }

        return sum;
    }

    static int UpperBound(float[] a, float x)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int m = (lo + hi) >> 1;
            if (a[m] <= x) lo = m + 1;
            else hi = m;
        }

        return lo;
    }

    /// <summary>colour of every SMPL-24 joint of a dancer at a frame (cached array, valid until the next call)</summary>
    public Color[] Colours(Role role, int frame)
    {
        if (!tracks.TryGetValue(role, out Track t)) return null;
        frame = Mathf.Clamp(frame, 0, frameCount - 1);
        if (Current == Mode.Physics)
        {
            for (int j = 0; j < Joints; j++) t.Out[j] = LoadColour(t.Load[frame * Joints + j]);
            return t.Out;
        }

        double time = frameAudio[frame];
        for (int j = 0; j < Joints; j++)
        {
            float p = Pulse(role, frame, j, time);
            t.Out[j] = t.Base * (IdleLevel + PulseLevel * Mathf.Min(p, 2f));
            t.Out[j].a = 1f;
        }

        return t.Out;
    }

    /// <summary>tension orange (+) / neutral / compression blue (-) on one scale (1 = body weight / full connection),
    /// brighter with the magnitude. Never above 1 in any channel (the line colour is Color32: a clipped red would turn
    /// orange into yellow), so the hue stays the base's at every magnitude.</summary>
    public static Color LoadColour(float load)
    {
        float m = Mathf.Clamp01(Mathf.Abs(load));
        Color target = load >= 0 ? TensionColour : CompressionColour;
        Color c = Color.Lerp(NeutralColour, target, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(m * 1.4f)));
        c *= Mathf.Lerp(0.45f, 1f, m);
        float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        if (max > 1f) c /= max;
        c.a = 1f;
        return c;
    }

    /// <summary>approximately how a load colour shows on screen (for the legend): linear light x DisplayGain, clamped</summary>
    public static Color DisplayColour(float load)
    {
        Color lin = LoadColour(load).linear * DisplayGain;
        Color c = new Color(Mathf.Clamp01(lin.r), Mathf.Clamp01(lin.g), Mathf.Clamp01(lin.b)).gamma;
        c.a = 1f;
        return c;
    }

    public Dictionary<string, object> State(int frame)
    {
        Dictionary<string, object> s = new()
        {
            ["mode"] = Current.ToString().ToLowerInvariant(), ["beatSource"] = BeatSource, ["accentSource"] = AccentSource,
            ["loadSource"] = LoadSource, ["beats"] = BeatCount, ["travelSeconds"] = TravelSeconds, ["frame"] = frame
        };
        if (frame < 0 || frame >= frameCount) return s;
        double time = frameAudio[frame];
        int prev = beatT != null ? UpperBound(beatT, (float)time) - 1 : -1;
        s["time"] = time;
        s["lastBeat"] = prev >= 0 ? new Dictionary<string, object> { ["t"] = beatT[prev], ["kind"] = beatKind[prev], ["strength"] = beatS[prev], ["age"] = time - beatT[prev] } : null;
        foreach ((Role role, Track t) in tracks)
        {
            Color[] cols = Colours(role, frame);
            s[role.ToString().ToLowerInvariant()] = new Dictionary<string, object>
            {
                ["accentsKnown"] = t.AccentsKnown,
                ["accentSource"] = t.AccentSource,
                ["stance"] = (t.StanceL[frame] ? "L" : "") + (t.StanceR[frame] ? "R" : ""),
                ["support"] = t.Support[frame],
                ["pulse"] = Enumerable.Range(0, Joints).Select(j => Pulse(role, frame, j, time)).ToArray(),
                ["dist"] = Enumerable.Range(0, Joints).Select(j => t.Dist[frame * Joints + j]).ToArray(),
                ["load"] = Enumerable.Range(0, Joints).Select(j => t.Load[frame * Joints + j]).ToArray(),
                ["colour"] = cols.Select(c => new[] { c.r, c.g, c.b }).ToArray(),
                ["accentHitRate"] = Enumerable.Range(0, 4).ToDictionary(g => GroupNames[g], g => (object)t.GroupHit[g]),
                ["accentMeanLagMs"] = Enumerable.Range(0, 4).ToDictionary(g => GroupNames[g], g => (object)t.GroupLagMs[g]),
                ["accentBeats"] = Enumerable.Range(0, 4).ToDictionary(g => GroupNames[g], g => (object)t.GroupBeats[g])
            };
        }

        return s;
    }
}

/// <summary>Physics view legend: the skeleton load colours, honestly labelled as estimates (OnGUI, independent of the
/// HUD flag: the tour hides the HUD but the legend belongs to the physics colouring)</summary>
public class SkeletonLegend : MonoBehaviour
{
    public bool Show;
    public string Source = "";
    static Texture2D bar;

    /// <summary>screen y of the legend's top: under the DanceHud tour label (reference 1080 p: 20 px + 40 px box)</summary>
    public static float TopOffset() => TopOverride >= 0f ? TopOverride : 14f + 64f * Screen.height / 1080f;

    /// <summary>screen y (px from the top) the film director gives the legend in 9:16 (below the platform's top band and the
    /// HUD row); negative = the viewer's own place</summary>
    public static float TopOverride = -1f;

    /// <summary>legend rectangle (playtests: no overlap with the tour label)</summary>
    public static Rect Area => new(Screen.width - 314, TopOffset(), 300, 74);

    void OnGUI()
    {
        if (!Show) return;
        if (bar == null)
        {
            bar = new Texture2D(64, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int i = 0; i < 64; i++)
            {
                float s = Mathf.Lerp(1f, -1f, i / 63f);
                bar.SetPixel(i, 0, SkeletonStyle.DisplayColour(s)); // as the skeleton shows it on screen
            }

            bar.Apply();
        }

        // below the tour title (DanceHud: top right, 26 px at 1080 p from 20 px down) so it never covers "from motion"
        float w = 300, h = 74;
        Rect r = new(Screen.width - w - 14, TopOffset(), w, h);
        GUI.Box(r, GUIContent.none);
        GUI.Label(new Rect(r.x + 8, r.y + 4, w - 16, 20), "Skeleton load - ESTIMATED from motion");
        GUI.DrawTexture(new Rect(r.x + 8, r.y + 26, w - 16, 12), bar);
        GUI.Label(new Rect(r.x + 8, r.y + 40, 90, 20), "tension");
        GUI.Label(new Rect(r.x + w * 0.5f - 24, r.y + 40, 70, 20), "neutral");
        GUI.Label(new Rect(r.x + w - 98, r.y + 40, 90, 20), "compression");
        GUI.Label(new Rect(r.x + 8, r.y + 55, w - 16, 20), "legs: stance weight share  arms: partner hold");
    }
}
