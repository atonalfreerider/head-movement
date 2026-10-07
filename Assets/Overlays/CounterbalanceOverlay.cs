using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// &lt;capture&gt;/counterbalance.json (dancecap.counterbalance, Unity copy: time_base "capture" = audio clock,
/// frame "unity" = capture coordinates before the VIEWER_SPEC 3.0 origin offset) as data in world coordinates.
/// A work file (time_base "reference", frame "world") is converted too.
/// </summary>
public class CounterbalanceData
{
    public class Pivot
    {
        public float T0, T1, Confidence, LeaderRadius, SweptDeg;
        public Vector3 Point;
        public string Foot, Direction;
        public float[] PathT;
        public Vector3[] PathPos;
        public float[] PathCumDeg; // cumulative |angle| swept about the pivot along the path
        public float[] TickT;
        public Vector3[] TickPos;
    }

    public class Interval
    {
        public string Kind;
        public float T0, T1, Confidence;
        public string Pair; // e.g. lead_r_hand-follow_l_hand
        public string PivotFoot;
        public float[] ComT;
        public Vector3[] Com;
        public readonly List<Pivot> Pivots = new();
    }

    public readonly List<Interval> Intervals = new();
    public readonly List<Vector2> Coverage = new();
    public string Take, TimeBase, Frame;

    public static CounterbalanceData Load(string path, CaptureTimeline timeline, Vector3 offset)
    {
        JObject root = JObject.Parse(File.ReadAllText(path));
        CounterbalanceData d = new()
        {
            Take = root.Value<string>("take"), TimeBase = root.Value<string>("time_base") ?? "capture",
            Frame = root.Value<string>("frame") ?? "unity"
        };
        // capture time is the viewer's audio clock; a work file's reference time needs time_to_audio
        float dt = string.Equals(d.TimeBase, "reference", StringComparison.OrdinalIgnoreCase) && timeline != null
            ? timeline.TimeToAudio
            : 0f;
        bool world = string.Equals(d.Frame, "world", StringComparison.OrdinalIgnoreCase);

        Vector3 P(JToken t)
        {
            if (t is not JArray a || a.Count < 3) return new Vector3(float.NaN, float.NaN, float.NaN);
            Vector3 v = new(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>());
            if (world) v.z = -v.z;
            return v + offset;
        }

        float T(JToken t) => t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? t.Value<float>() + dt : float.NaN;

        if (root["coverage"] is JArray coverage)
        {
            foreach (JToken c in coverage)
            {
                if (c is JArray r && r.Count == 2) d.Coverage.Add(new Vector2(T(r[0]), T(r[1])));
            }
        }

        if (root["intervals"] is not JArray intervals) return d;
        foreach (JToken it in intervals)
        {
            Interval iv = new()
            {
                Kind = it.Value<string>("kind") ?? (it["pivot"] is JArray ? "pivot" : "counterbalance"),
                T0 = T(it["t0"]), T1 = T(it["t1"]), Confidence = Num(it["confidence"], 1f),
                Pair = it["connection"]?.Type == JTokenType.Object ? it["connection"].Value<string>("pair") : null,
                PivotFoot = it.Value<string>("pivot_foot")
            };
            if (it["com_track"] is JObject track && track["t"] is JArray tt && track["com"] is JArray cc)
            {
                iv.ComT = tt.Select(x => T(x)).ToArray();
                iv.Com = cc.Select(P).ToArray();
            }

            if (it["pivots"] is JArray pivots && pivots.Count > 0)
            {
                foreach (JToken p in pivots) iv.Pivots.Add(ReadPivot(p, P, T));
            }
            else if (it["pivot"] is JArray)
            {
                Pivot p = ReadPivot(it, P, T);
                p.T0 = it["pivot_t0"] != null ? T(it["pivot_t0"]) : iv.T0;
                p.T1 = it["pivot_t1"] != null ? T(it["pivot_t1"]) : iv.T1;
                iv.Pivots.Add(p);
            }

            d.Intervals.Add(iv);
        }

        return d;
    }

    static Pivot ReadPivot(JToken p, Func<JToken, Vector3> P, Func<JToken, float> T)
    {
        Pivot pv = new()
        {
            T0 = T(p["t0"]), T1 = T(p["t1"]), Confidence = Num(p["confidence"], 1f),
            Point = P(p["pivot"]), Foot = p.Value<string>("pivot_foot"), Direction = p.Value<string>("direction"),
            LeaderRadius = Num(p["leader_radius_m"], float.NaN), SweptDeg = Num(p["swept_deg"], float.NaN)
        };
        if (p["leader_path"] is JObject path && path["t"] is JArray pt && path["pos"] is JArray pp)
        {
            pv.PathT = pt.Select(x => T(x)).ToArray();
            pv.PathPos = pp.Select(P).ToArray();
        }
        else
        {
            pv.PathT = Array.Empty<float>();
            pv.PathPos = Array.Empty<Vector3>();
        }

        // cumulative swept angle about the pivot (for the running "360°" label)
        pv.PathCumDeg = new float[pv.PathPos.Length];
        for (int i = 1; i < pv.PathPos.Length; i++)
        {
            Vector3 a = pv.PathPos[i - 1] - pv.Point, b = pv.PathPos[i] - pv.Point;
            pv.PathCumDeg[i] = pv.PathCumDeg[i - 1] + Mathf.Abs(Vector2.SignedAngle(new Vector2(a.x, a.z), new Vector2(b.x, b.z)));
        }

        if (p["beat_ticks"] is JArray ticks)
        {
            pv.TickT = ticks.Select(x => T(x["t"])).ToArray();
            pv.TickPos = ticks.Select(x => P(x["pos"])).ToArray();
        }
        else
        {
            pv.TickT = Array.Empty<float>();
            pv.TickPos = Array.Empty<Vector3>();
        }

        if (float.IsNaN(pv.SweptDeg) && pv.PathCumDeg.Length > 0) pv.SweptDeg = pv.PathCumDeg[^1];
        return pv;
    }

    static float Num(JToken t, float fallback) =>
        t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? t.Value<float>() : fallback;

    /// <summary>linear interpolation of a time series (clamped)</summary>
    public static Vector3 Sample(float[] t, Vector3[] v, float time)
    {
        if (t == null || t.Length == 0) return new Vector3(float.NaN, float.NaN, float.NaN);
        if (time <= t[0]) return v[0];
        if (time >= t[^1]) return v[^1];
        int lo = 0, hi = t.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (t[mid] <= time) lo = mid;
            else hi = mid;
        }

        float k = (time - t[lo]) / Mathf.Max(1e-6f, t[hi] - t[lo]);
        return Vector3.Lerp(v[lo], v[hi], k);
    }

    /// <summary>index of the last sample at or before time (-1 before the first)</summary>
    public static int IndexAtOrBefore(float[] t, float time)
    {
        if (t == null || t.Length == 0 || time < t[0]) return -1;
        int lo = 0, hi = t.Length - 1;
        if (time >= t[hi]) return hi;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (t[mid] <= time) lo = mid;
            else hi = mid;
        }

        return lo;
    }
}

/// <summary>
/// Layer "counterbalance" (VIEWER_SPEC 3.9, 3.5), visible in every view state:
/// - while the couple is in a counterbalance: a yellow dot on the floor under the combined centre of mass, a
///   yellow vertical axis up to the COM (small sphere), 0.2 s fade inside the interval edges, dashed when the
///   detection confidence is low; the connection drawn as a taut line between the connected hands;
/// - for every counterbalance pivot: a yellow ring at the follower's anchored foot, the leader's circling path as
///   a yellow floor arc that grows with time, a tick per beat and the swept angle ("360°"); after the pivot the
///   marker stays in the floor craft and fades over the footprint history (4 measures); ShowAllPivots (the
///   overhead coverage view) keeps every pivot of the dance on the floor.
/// Rebuilt only when the frame, the camera or a setting changes; no per-frame allocations.
/// </summary>
public class CounterbalanceOverlay : MonoBehaviour
{
    public float FadeSeconds = 0.2f;
    public float EdgeAlpha = 0.25f;
    public float LowConfidence = 0.6f;
    public float HistorySeconds = 12.6f; // 4 measures at 76 BPM; set from the beat grid
    public bool ShowAllPivots;
    public Color Yellow = new(1f, 0.85f, 0.1f);
    public Color Tension = new(1f, 0.6f, 0.15f);

    CounterbalanceData data;
    Dancer lead, follow;
    GlowMesh glow;
    Material material;
    readonly List<TextMesh> labels = new();
    readonly List<CounterbalanceData.Pivot> allPivots = new();
    readonly List<CounterbalanceData.Interval> pivotInterval = new();
    readonly List<string> labelText = new();
    bool visible = true;
    float lastTime = float.NaN;
    int lastFrame = -1;
    Vector3 lastViewer;
    bool dirty = true;

    // state for hm_state / playtests
    public bool Loaded => data != null;
    public int IntervalCount => data?.Intervals.Count ?? 0;
    public int ActiveInterval { get; private set; } = -1;
    public float AxisAlpha { get; private set; }
    public bool AxisVisible => visible && ActiveInterval >= 0 && AxisAlpha > 0;
    public bool Dashed { get; private set; }
    public Vector3 Com { get; private set; }
    public Vector3 Dot { get; private set; }
    public int PivotsShown { get; private set; }
    public int CurrentPivot { get; private set; } = -1;
    public float CurrentSweptDeg { get; private set; }
    public bool ConnectionShown { get; private set; }
    public CounterbalanceData Data => data;

    /// <summary>active interval (or null) - the follower traces emphasise her free foot/hand during it</summary>
    public CounterbalanceData.Interval Active => data != null && ActiveInterval >= 0 ? data.Intervals[ActiveInterval] : null;

    static string[] degreeStrings;

    public void Init(CounterbalanceData counterbalance, Dancer leadDancer, Dancer followDancer, float measureSeconds)
    {
        data = counterbalance;
        lead = leadDancer;
        follow = followDancer;
        if (measureSeconds > 0.5f) HistorySeconds = 4f * measureSeconds;
        material = GlowMesh.NewMaterial("Counterbalance glow", 3f);
        glow = GlowMesh.Create("Counterbalance", transform, material);
        degreeStrings ??= Enumerable.Range(0, 289).Select(i => $"{i * 5}°").ToArray();
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        foreach (CounterbalanceData.Interval iv in data.Intervals)
        {
            foreach (CounterbalanceData.Pivot p in iv.Pivots)
            {
                allPivots.Add(p);
                pivotInterval.Add(iv);
                labels.Add(DanceText.WorldLabel(transform, "Swept angle", font, 0.11f, Yellow));
                labelText.Add(null);
            }
        }

        foreach (TextMesh l in labels) l.gameObject.SetActive(false);
        dirty = true;
    }

    public void SetVisible(bool on)
    {
        visible = on;
        if (glow != null) glow.SetVisible(on);
        if (!on)
        {
            foreach (TextMesh l in labels) l.gameObject.SetActive(false);
        }

        dirty = true;
    }

    public void SetShowAllPivots(bool on)
    {
        if (ShowAllPivots == on) return;
        ShowAllPivots = on;
        dirty = true;
    }

    /// <summary>update for the shown frame (audio/capture time of that frame)</summary>
    public void SetTime(float time, int frame, Camera viewer)
    {
        if (data == null) return;
        Vector3 eye = viewer != null ? viewer.transform.position : Vector3.zero;
        bool cameraMoved = (eye - lastViewer).sqrMagnitude > 1e-6f;
        if (!dirty && frame == lastFrame && Mathf.Approximately(time, lastTime) && !cameraMoved) return;
        dirty = false;
        lastFrame = frame;
        lastTime = time;
        lastViewer = eye;

        ActiveInterval = -1;
        AxisAlpha = 0;
        for (int i = 0; i < data.Intervals.Count; i++)
        {
            CounterbalanceData.Interval iv = data.Intervals[i];
            if (time < iv.T0 || time > iv.T1) continue;
            ActiveInterval = i;
            float edge = Mathf.Min(time - iv.T0, iv.T1 - time);
            AxisAlpha = Mathf.Max(EdgeAlpha, Mathf.Clamp01(edge / Mathf.Max(1e-3f, FadeSeconds)));
            break;
        }

        if (!visible) return;
        glow.Begin();
        glow.Viewer = eye;
        CounterbalanceData.Interval active = Active;
        Dashed = false;
        ConnectionShown = false;
        if (active != null)
        {
            Vector3 com = CounterbalanceData.Sample(active.ComT, active.Com, time);
            if (float.IsNaN(com.x) && lead != null && follow != null)
            {
                com = (lead.CenterOfMass(frame) + follow.CenterOfMass(frame)) * 0.5f; // no track: torso estimate
            }

            Com = com;
            Dot = new Vector3(com.x, 0.004f, com.z);
            Dashed = active.Confidence < LowConfidence;
            Color c = Yellow * AxisAlpha;
            glow.Disc(Dot, 0.06f, c, c * 0.5f);
            glow.Ring(Dot, 0.09f, 0.01f, c);
            if (Dashed) glow.DashedLine(Dot, com, 0.018f, c, 0.05f, 0.035f);
            else glow.Line(Dot, com, 0.018f, c);
            glow.Sphere(com, 0.035f, c);

            if (lead != null && follow != null && TryConnection(active.Pair, frame, out Vector3 a, out Vector3 b))
            {
                glow.Line(a, b, 0.01f, Tension * AxisAlpha);
                ConnectionShown = true;
            }
        }

        PivotMarkers(time);
        glow.End();
    }

    void PivotMarkers(float time)
    {
        PivotsShown = 0;
        CurrentPivot = -1;
        CurrentSweptDeg = 0;
        for (int i = 0; i < allPivots.Count; i++)
        {
            CounterbalanceData.Pivot p = allPivots[i];
            TextMesh label = labels[i];
            float a;
            if (time < p.T0) a = ShowAllPivots ? 0.3f : 0f;
            else if (time <= p.T1) a = 1f;
            else a = Mathf.Max(ShowAllPivots ? 0.45f : 0f, 1f - (time - p.T1) / Mathf.Max(0.1f, HistorySeconds));
            if (a <= 0 || float.IsNaN(p.Point.x))
            {
                label.gameObject.SetActive(false);
                continue;
            }

            PivotsShown++;
            bool running = time >= p.T0 && time <= p.T1;
            if (running) CurrentPivot = i;
            Color c = Yellow * a;
            Vector3 centre = new(p.Point.x, 0.006f, p.Point.z);
            glow.Ring(centre, 0.09f, 0.012f, c);
            glow.Disc(centre, 0.025f, c, c);

            // the leader's circling arc up to now (the whole arc in the coverage view / after the pivot)
            float until = ShowAllPivots && !running ? float.MaxValue : time;
            int last = -1;
            for (int k = 1; k < p.PathPos.Length; k++)
            {
                if (p.PathT[k] > until) break;
                Vector3 a0 = p.PathPos[k - 1], a1 = p.PathPos[k];
                glow.FloorStrip(new Vector3(a0.x, 0.007f, a0.z), new Vector3(a1.x, 0.007f, a1.z), 0.014f, c);
                last = k;
            }

            for (int k = 0; k < p.TickPos.Length; k++)
            {
                if (p.TickT[k] > until) break;
                Vector3 tp = p.TickPos[k];
                Vector3 radial = new Vector3(tp.x - p.Point.x, 0, tp.z - p.Point.z);
                if (radial.sqrMagnitude < 1e-6f) continue;
                radial.Normalize();
                Vector3 m = new(tp.x, 0.008f, tp.z);
                glow.FloorStrip(m - radial * 0.06f, m + radial * 0.06f, 0.01f, c);
            }

            // running: the angle swept so far; afterwards (and in the coverage view): the whole pivot
            float swept = running ? (last >= 0 ? p.PathCumDeg[last] : 0f) : p.SweptDeg;
            if (float.IsNaN(swept)) swept = 0;
            if (running) CurrentSweptDeg = swept;
            int idx = Mathf.Clamp(Mathf.RoundToInt(swept / 5f), 0, degreeStrings.Length - 1);
            string text = degreeStrings[idx];
            if (!ReferenceEquals(labelText[i], text))
            {
                labelText[i] = text;
                label.text = text;
            }

            Vector3 anchor = last >= 0 ? p.PathPos[last] : p.Point;
            Vector3 outward = new Vector3(anchor.x - p.Point.x, 0, anchor.z - p.Point.z);
            if (outward.sqrMagnitude > 1e-6f) anchor += outward.normalized * 0.18f;
            label.gameObject.SetActive(true);
            label.transform.position = new Vector3(anchor.x, 0.05f, anchor.z);
            DanceText.Billboard(label.transform);
            DanceText.SetAlpha(label, a);
        }
    }

    bool TryConnection(string pair, int frame, out Vector3 a, out Vector3 b)
    {
        a = b = default;
        if (string.IsNullOrEmpty(pair)) return false;
        // "lead_r_hand-follow_l_hand"
        int dash = pair.IndexOf('-');
        if (dash < 0) return false;
        bool leadRight = pair.IndexOf("lead_r", StringComparison.Ordinal) >= 0;
        bool followRight = pair.IndexOf("follow_r", StringComparison.Ordinal) >= 0;
        if (frame < 0 || frame >= lead.FrameCount || frame >= follow.FrameCount) return false;
        a = leadRight ? lead.GetRightHandContact(frame) : lead.GetLeftHandContact(frame);
        b = followRight ? follow.GetRightHandContact(frame) : follow.GetLeftHandContact(frame);
        return true;
    }

    /// <summary>first pivot interval (for the free foot / free hand of the follower traces)</summary>
    public static bool FollowerFreeLimbs(CounterbalanceData.Interval iv, out bool freeFootRight, out bool freeHandRight,
        out bool footKnown)
    {
        freeFootRight = freeHandRight = false;
        footKnown = false;
        if (iv == null) return false;
        string foot = iv.PivotFoot ?? (iv.Pivots.Count > 0 ? iv.Pivots[0].Foot : null);
        if (!string.IsNullOrEmpty(foot))
        {
            footKnown = true;
            freeFootRight = string.Equals(foot, "left", StringComparison.OrdinalIgnoreCase);
        }

        // the follower's connected hand holds the leader; the other one is free
        bool followConnectedRight = iv.Pair != null && iv.Pair.IndexOf("follow_r", StringComparison.Ordinal) >= 0;
        freeHandRight = !followConnectedRight;
        return true;
    }

    public Dictionary<string, object> State()
    {
        Dictionary<string, object> s = new()
        {
            ["loaded"] = Loaded, ["visible"] = visible, ["intervals"] = IntervalCount,
            ["pivots"] = allPivots.Count, ["active"] = ActiveInterval >= 0, ["axisVisible"] = AxisVisible,
            ["axisRendered"] = glow != null && glow.Visible && glow.VertexCount > 0 && ActiveInterval >= 0,
            ["alpha"] = AxisAlpha, ["dashed"] = Dashed, ["pivotsShown"] = PivotsShown,
            ["showAllPivots"] = ShowAllPivots, ["connection"] = ConnectionShown, ["time"] = lastTime,
            ["pivotRunning"] = CurrentPivot >= 0,
            ["intervalTimes"] = data?.Intervals.Select(iv => new object[]
            {
                iv.T0, iv.T1, iv.Pivots.Count > 0 ? iv.Pivots[0].T0 : (float?)null, iv.Pivots.Count > 0 ? iv.Pivots[0].T1 : (float?)null
            }).ToList()
        };
        if (ActiveInterval >= 0)
        {
            CounterbalanceData.Interval iv = data.Intervals[ActiveInterval];
            s["interval"] = ActiveInterval;
            s["kind"] = iv.Kind;
            s["t0"] = iv.T0;
            s["t1"] = iv.T1;
            s["confidence"] = iv.Confidence;
            s["com"] = new[] { Com.x, Com.y, Com.z };
            s["dot"] = new[] { Dot.x, Dot.z };
        }

        if (CurrentPivot >= 0)
        {
            CounterbalanceData.Pivot p = allPivots[CurrentPivot];
            s["pivot"] = new[] { p.Point.x, p.Point.y, p.Point.z };
            s["pivotT0"] = p.T0;
            s["pivotT1"] = p.T1;
            s["sweptDeg"] = CurrentSweptDeg;
            s["leaderRadiusM"] = p.LeaderRadius;
            if (follow != null && lastFrame >= 0 && lastFrame < follow.FrameCount)
            {
                // the ring must sit under the follower's anchored foot as the (origin-shifted) skeleton shows it
                bool right = string.Equals(p.Foot, "right", StringComparison.OrdinalIgnoreCase);
                Vector3 foot = follow.Joint(lastFrame, right ? SmplJoint.R_Foot : SmplJoint.L_Foot);
                s["pivotFoot"] = new[] { foot.x, foot.y, foot.z };
                s["pivotToFootM"] = new Vector2(foot.x - p.Point.x, foot.z - p.Point.z).magnitude;
            }
        }
        else if (allPivots.Count > 0)
        {
            CounterbalanceData.Pivot p = allPivots[0];
            s["firstPivot"] = new[] { p.Point.x, p.Point.y, p.Point.z };
        }

        return s;
    }

    void OnDestroy()
    {
        if (material != null) Destroy(material);
    }
}
