using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// &lt;capture&gt;/floorcraft.json (dancecap.floorcraft; contract: dancecap CONTRACTS.md "counterbalance/ and floorcraft/") as
/// data in viewer coordinates. Version 3 adds the leader's STABLE T axis: t_axes (one averaged position + heading per
/// episode, held while he dances on it), transitions (T origin to T origin), axis_pivots (T reorientations over 30 deg),
/// dials (repeated turning about one point: the counterbalance pivot, a spin or a couple turn) with a balance
/// evaluation, and a per-frame balance track (chest -> stance foot, COM over the foot). The v2 keys (plants, moves,
/// rotations, pivots, track) are still read. Conventions as counterbalance.json: time_base "capture" (= the viewer's
/// audio clock) or "reference" (+ time_to_audio), frame "unity" or "world" (z mirrored); the viewer adds the origin
/// offset. Yaw / signed angles are counter-clockwise seen from above, 0 = +x, 90 = Unity +z (the same numbers in both
/// frames), so Dir(yaw) is the Unity floor direction.
/// </summary>
public class FloorCraftData
{
    public class Plant
    {
        public int Id;
        public float T0, T1, YawDeg;
        public Vector3 Origin;
    }

    public class Move
    {
        public int Id;
        public float T0, T1;
    }

    public class Rotation
    {
        public int Id;
        public float T0, T1, AngleDeg;
    }

    /// <summary>balance verdict of a turn (dancecap.floorcraft "evaluation"): an ESTIMATE from the fitted bodies. Only a
    /// pivot on ONE foot (and a dial) is graded; a turn on both feet or a stepped turn says "not graded" (TurnKind)</summary>
    public class Evaluation
    {
        public string Subject, Verdict = "unknown", Text, Measure, Worse, TurnKind, TurnKindLabel, BalanceVerdict;
        public bool Graded;
        public float AlignedShare = float.NaN, MeanOffsetM = float.NaN, MeanTiltDeg = float.NaN, MeanScore = float.NaN;

        /// <summary>0 good, 1 ok, 2 needs work, 3 not graded / unknown (no verdict icon)</summary>
        public int Index => !Graded ? 3 : Verdict switch { "good" => 0, "ok" => 1, "needs work" => 2, _ => 3 };
    }

    /// <summary>one stable T: averaged origin / heading over its forming window, then held until he leaves or turns</summary>
    public class TAxis
    {
        public int Id, SameAs = -1, Prev = -1, Next = -1, PivotIn = -1, PivotOut = -1, Dial = -1;
        public float T0, T1, TFormed, YawDeg;
        public Vector3 Origin;
        public string StartReason, EndReason;
    }

    public class Transition
    {
        public int Id, FromT = -1, ToT = -1, AxisPivot = -1, Dial = -1;
        public float T0, T1, DistanceM;
        public Vector3 From, To;
        public bool SamePlace;
        public string Reason;
    }

    public class AxisPivot
    {
        public int Id, FromT = -1, ToT = -1, Dial = -1, Stack;
        public float T0, T1, FromYawDeg, ToYawDeg, SignedDeg;
        public Vector3 At;
        public bool Stayed, ViaBrief;
        /// <summary>between_ts | lead_in | into_counterbalance | out_of_counterbalance | lead_out</summary>
        public string Context = "between_ts";
        public Evaluation Evaluation;
    }

    /// <summary>repeated turning about one point; RunT / RunDeg (the running signed degrees) are filled by the overlay</summary>
    public class Dial
    {
        public int Id, CounterbalancePivot = -1, TAxis = -1;
        public string Kind, Who, Subject;
        public float T0, T1, Degrees, RadiusM, ArcStartDeg, HeadingDeg = float.NaN, OrbitDeg = float.NaN;
        public Vector3 Centre;
        public int[] AxisPivots = Array.Empty<int>();
        public Evaluation Evaluation;
        public float[] TickT = Array.Empty<float>(), TickDeg = Array.Empty<float>();
        public float[] RunT = Array.Empty<float>(), RunDeg = Array.Empty<float>();
        public string RunSource = "time";
        public float RunRawEndDeg = float.NaN;
        public float AbsDeg => Mathf.Abs(Degrees);

        /// <summary>the needle follows the leader about the centre (else his own heading)</summary>
        public bool Orbit => Kind is "counterbalance" or "leader_around_follower";
    }

    public class BalanceTrack
    {
        public byte[] Stance;                     // 0 none, 1 left, 2 right (couple: 1 inside a pivot)
        public Vector3[] Support, Chest, Com;     // NaN when absent
        public float[] OffsetM, TiltDeg, Score;   // NaN when absent
    }

    /// <summary>per-frame balance axis (VIEWER_SPEC 3.8): chest -> stance foot per dancer, couple COM -> anchored foot</summary>
    public class BalanceData
    {
        public float[] T;
        public BalanceTrack Lead, Follow, Couple;
        public int[] CouplePivot;                 // counterbalance pivot id per frame, -1 outside
        public float GoodOffsetM = 0.04f, BadOffsetM = 0.12f, GoodTiltDeg = 3f, BadTiltDeg = 9f, AlignedScore = 0.5f;

        public int Nearest(float time)
        {
            if (T == null || T.Length == 0) return -1;
            int i = CounterbalanceData.IndexAtOrBefore(T, time);
            if (i < 0) return 0;
            if (i >= T.Length - 1) return T.Length - 1;
            return time - T[i] <= T[i + 1] - time ? i : i + 1;
        }
    }

    public readonly List<Plant> Plants = new();
    public readonly List<Move> Moves = new();
    public readonly List<Rotation> Rotations = new();
    public readonly List<CounterbalanceData.Pivot> Pivots = new();
    public readonly List<Evaluation> PivotEvaluations = new();
    public readonly List<TAxis> TAxes = new();
    public readonly List<Transition> Transitions = new();
    public readonly List<AxisPivot> AxisPivots = new();
    public readonly List<Dial> Dials = new();
    public BalanceData Balance;
    public float[] AxisT;
    public Vector3[] AxisOrigin;
    public float[] AxisYawDeg;
    public string Take, TimeBase, Frame;
    public int Version;

    // params.t_axis (the allowed region of a T and its drawing size)
    public float HalfWidthM = 0.30f, CorridorForwardM = 1.2f, CorridorSideM = 1.2f, PivotDeg = 30f;
    public float DrawForwardM = 0.8f, DrawSideM = 1.0f;

    public bool HasTAxes => TAxes.Count > 0;

    public static FloorCraftData Load(string path, CaptureTimeline timeline, Vector3 offset)
    {
        JObject root = JObject.Parse(File.ReadAllText(path));
        FloorCraftData d = new()
        {
            Take = root.Value<string>("take"), TimeBase = root.Value<string>("time_base") ?? "capture",
            Frame = root.Value<string>("frame") ?? "unity", Version = root["version"]?.Type == JTokenType.Integer ? root.Value<int>("version") : 0
        };
        float dt = string.Equals(d.TimeBase, "reference", StringComparison.OrdinalIgnoreCase) && timeline != null ? timeline.TimeToAudio : 0f;
        bool world = string.Equals(d.Frame, "world", StringComparison.OrdinalIgnoreCase);
        Vector3 nan = new(float.NaN, float.NaN, float.NaN);

        // a point with its height (body points: chest, COM)
        Vector3 P3(JToken t)
        {
            if (t is not JArray a || a.Count < 2) return nan;
            Vector3 v = a.Count >= 3 ? new Vector3(Num(a[0]), Num(a[1]), Num(a[2])) : new Vector3(Num(a[0]), 0f, Num(a[1]));
            if (world) v.z = -v.z;
            return v + new Vector3(offset.x, 0f, offset.z);
        }

        // a floor point
        Vector3 P(JToken t)
        {
            Vector3 v = P3(t);
            v.y = 0f;
            return v;
        }

        float T(JToken t) => t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? t.Value<float>() + dt : float.NaN;

        if (root["params"]?["t_axis"] is JObject tp)
        {
            d.HalfWidthM = Num(tp["half_width_m"], d.HalfWidthM);
            d.CorridorForwardM = Num(tp["forward_m"], d.CorridorForwardM);
            d.CorridorSideM = Num(tp["side_m"], d.CorridorSideM);
            d.PivotDeg = Num(tp["pivot_deg"], d.PivotDeg);
            d.DrawForwardM = Num(tp["draw_forward_m"], d.DrawForwardM);
            d.DrawSideM = Num(tp["draw_side_m"], d.DrawSideM);
        }

        // the smoothed chest axis (15 Hz): the live-axis fallback and the heading of spin dials
        JObject at = root["track"] as JObject ?? root["axis_track"] as JObject;
        if (at != null && at["t"] is JArray tt && at["origin"] is JArray oo && at["yaw_deg"] is JArray yy &&
            tt.Count == oo.Count && tt.Count == yy.Count && tt.Count > 1)
        {
            d.AxisT = tt.Select(T).ToArray();
            d.AxisOrigin = oo.Select(P).ToArray();
            d.AxisYawDeg = yy.Select(x => Num(x)).ToArray();
        }

        // v2 records: read for the counts (hm_state); the v3 T axis replaces them on the floor
        if (root["plants"] is JArray plants)
        {
            foreach (JToken p in plants)
            {
                d.Plants.Add(new Plant { Id = Int(p["id"], d.Plants.Count), T0 = T(p["t0"]), T1 = T(p["t1"]), Origin = P(p["origin"]), YawDeg = Num(p["yaw_deg"]) });
            }
        }

        if (root["moves"] is JArray moves)
        {
            foreach (JToken m in moves) d.Moves.Add(new Move { Id = Int(m["id"], d.Moves.Count), T0 = T(m["t0"]), T1 = T(m["t1"]) });
        }

        if (root["rotations"] is JArray rotations)
        {
            foreach (JToken r in rotations)
            {
                d.Rotations.Add(new Rotation { Id = Int(r["id"], d.Rotations.Count), T0 = T(r["t0"]), T1 = T(r["t1"]), AngleDeg = Num(r["signed_deg"] ?? r["angle_deg"]) });
            }
        }

        if (root["pivots"] is JArray pivots)
        {
            foreach (JToken p in pivots)
            {
                d.Pivots.Add(ReadPivot(p, P, T));
                d.PivotEvaluations.Add(ReadEvaluation(p["evaluation"]));
            }
        }

        // ---- version 3: the stable T axis
        if (root["t_axes"] is JArray tAxes)
        {
            foreach (JToken a in tAxes)
            {
                TAxis x = new()
                {
                    Id = Int(a["id"], d.TAxes.Count), T0 = T(a["t0"]), T1 = T(a["t1"]), TFormed = T(a["t_formed"]),
                    Origin = P(a["origin"]), YawDeg = Num(a["yaw_deg"]), StartReason = a.Value<string>("start_reason"),
                    EndReason = a.Value<string>("end_reason"), SameAs = Int(a["same_place_as"], -1), Prev = Int(a["prev"], -1),
                    Next = Int(a["next"], -1), PivotIn = Int(a["pivot_in"], -1), PivotOut = Int(a["pivot_out"], -1), Dial = Int(a["dial"], -1)
                };
                if (float.IsNaN(x.TFormed)) x.TFormed = x.T0;
                if (float.IsNaN(x.Origin.x) || float.IsNaN(x.YawDeg) || float.IsNaN(x.T0) || float.IsNaN(x.T1)) continue;
                d.TAxes.Add(x);
            }

            d.TAxes.Sort((a, b) => a.T0.CompareTo(b.T0));
        }

        if (root["transitions"] is JArray transitions)
        {
            foreach (JToken a in transitions)
            {
                d.Transitions.Add(new Transition
                {
                    Id = Int(a["id"], d.Transitions.Count), FromT = Int(a["from_t"], -1), ToT = Int(a["to_t"], -1),
                    T0 = T(a["t0"]), T1 = T(a["t1"]), From = P(a["from"]), To = P(a["to"]), DistanceM = Num(a["distance_m"], float.NaN),
                    SamePlace = a.Value<bool?>("same_place") ?? false, Reason = a.Value<string>("reason"),
                    AxisPivot = Int(a["axis_pivot"], -1), Dial = Int(a["dial"], -1)
                });
            }
        }

        if (root["axis_pivots"] is JArray axisPivots)
        {
            foreach (JToken a in axisPivots)
            {
                d.AxisPivots.Add(new AxisPivot
                {
                    Id = Int(a["id"], d.AxisPivots.Count), T0 = T(a["t0"]), T1 = T(a["t1"]), At = P(a["at"]),
                    FromT = Int(a["from_t"], -1), ToT = Int(a["to_t"], -1), FromYawDeg = Num(a["from_yaw_deg"]),
                    ToYawDeg = Num(a["to_yaw_deg"]), SignedDeg = Num(a["signed_deg"]), Stayed = a.Value<bool?>("stayed") ?? false,
                    Dial = Int(a["dial"], -1), Evaluation = ReadEvaluation(a["evaluation"]),
                    Context = a.Value<string>("context") ?? "between_ts", ViaBrief = a.Value<bool?>("via_brief") ?? false
                });
            }

            // pivots at the same spot step outward so their arcs do not overlap
            for (int i = 0; i < d.AxisPivots.Count; i++)
            {
                int stack = 0;
                for (int j = 0; j < i; j++)
                {
                    Vector3 a = d.AxisPivots[j].At, b = d.AxisPivots[i].At;
                    if (new Vector2(a.x - b.x, a.z - b.z).magnitude < 0.25f) stack++;
                }

                d.AxisPivots[i].Stack = stack;
            }
        }

        if (root["dials"] is JArray dials)
        {
            foreach (JToken a in dials)
            {
                Dial dl = new()
                {
                    Id = Int(a["id"], d.Dials.Count), Kind = a.Value<string>("kind") ?? "turn", Who = a.Value<string>("who"),
                    Subject = a.Value<string>("subject"), T0 = T(a["t0"]), T1 = T(a["t1"]), Centre = P(a["centre"] ?? a["center"]),
                    Degrees = Num(a["degrees"], 0f), RadiusM = Num(a["radius_m"], 0.3f), ArcStartDeg = Num(a["arc_start_deg"], 0f),
                    HeadingDeg = Num(a["heading_deg"]), OrbitDeg = Num(a["orbit_deg"]), CounterbalancePivot = Int(a["counterbalance_pivot"], -1),
                    TAxis = Int(a["t_axis"], -1), Evaluation = ReadEvaluation(a["evaluation"]),
                    AxisPivots = a["axis_pivots"] is JArray ap ? ap.Select(x => Int(x, -1)).Where(x => x >= 0).ToArray() : Array.Empty<int>()
                };
                if (float.IsNaN(dl.Centre.x) || float.IsNaN(dl.T0) || float.IsNaN(dl.T1)) continue;
                d.Dials.Add(dl);
            }
        }

        if (root["balance"] is JObject bal && bal["t"] is JArray bt && bt.Count > 0)
        {
            BalanceData b = new() { T = bt.Select(T).ToArray() };
            if (bal["params"] is JObject bp)
            {
                b.GoodOffsetM = Num(bp["good_offset_m"], b.GoodOffsetM);
                b.BadOffsetM = Num(bp["bad_offset_m"], b.BadOffsetM);
                b.GoodTiltDeg = Num(bp["good_tilt_deg"], b.GoodTiltDeg);
                b.BadTiltDeg = Num(bp["bad_tilt_deg"], b.BadTiltDeg);
                b.AlignedScore = Num(bp["aligned_score"], b.AlignedScore);
            }
            else if (root["params"]?["balance"] is JObject rp)
            {
                b.GoodOffsetM = Num(rp["good_offset_m"], b.GoodOffsetM);
                b.BadOffsetM = Num(rp["bad_offset_m"], b.BadOffsetM);
                b.GoodTiltDeg = Num(rp["good_tilt_deg"], b.GoodTiltDeg);
                b.BadTiltDeg = Num(rp["bad_tilt_deg"], b.BadTiltDeg);
                b.AlignedScore = Num(rp["aligned_score"], b.AlignedScore);
            }

            int n = b.T.Length;
            b.Lead = ReadTrack(bal["lead"] as JObject, n, P, P3, false);
            b.Follow = ReadTrack(bal["follow"] as JObject, n, P, P3, false);
            b.Couple = ReadTrack(bal["couple"] as JObject, n, P, P3, true);
            b.CouplePivot = new int[n];
            JArray cp = bal["couple"]?["pivot"] as JArray;
            for (int i = 0; i < n; i++) b.CouplePivot[i] = cp != null && i < cp.Count ? Int(cp[i], -1) : -1;
            if (b.Couple != null)
            {
                for (int i = 0; i < n; i++) b.Couple.Stance[i] = (byte)(b.CouplePivot[i] >= 0 && !float.IsNaN(b.Couple.Com[i].x) && !float.IsNaN(b.Couple.Support[i].x) ? 1 : 0);
            }

            d.Balance = b;
        }

        return d;
    }

    static BalanceTrack ReadTrack(JObject j, int n, Func<JToken, Vector3> floor, Func<JToken, Vector3> body, bool couple)
    {
        if (j == null) return null;
        BalanceTrack t = new()
        {
            Stance = new byte[n], Support = Points(j["support"], n, floor), Chest = Points(j["chest"], n, body), Com = Points(j["com"], n, body),
            OffsetM = Floats(j["com_offset_m"], n), TiltDeg = Floats(j["tilt_deg"], n), Score = Floats(j["score"], n)
        };
        if (!couple && j["stance"] is JArray st)
        {
            for (int i = 0; i < n && i < st.Count; i++) t.Stance[i] = (byte)Mathf.Clamp(Int(st[i], 0), 0, 2);
        }

        return t;
    }

    static Vector3[] Points(JToken arr, int n, Func<JToken, Vector3> f)
    {
        Vector3[] o = new Vector3[n];
        JArray a = arr as JArray;
        for (int i = 0; i < n; i++) o[i] = a != null && i < a.Count ? f(a[i]) : new Vector3(float.NaN, float.NaN, float.NaN);
        return o;
    }

    static float[] Floats(JToken arr, int n)
    {
        float[] o = new float[n];
        JArray a = arr as JArray;
        for (int i = 0; i < n; i++) o[i] = a != null && i < a.Count ? Num(a[i]) : float.NaN;
        return o;
    }

    static Evaluation ReadEvaluation(JToken e)
    {
        if (e is not JObject j) return null;
        string verdict = j.Value<string>("verdict") ?? "unknown";
        return new Evaluation
        {
            Subject = j.Value<string>("subject"), Verdict = verdict, Text = j.Value<string>("text"),
            Measure = j.Value<string>("measure"), Worse = j.Value<string>("worse"), AlignedShare = Num(j["aligned_share"]),
            MeanOffsetM = Num(j["mean_offset_m"]), MeanTiltDeg = Num(j["mean_tilt_deg"]), MeanScore = Num(j["mean_score"]),
            TurnKind = j.Value<string>("turn_kind"), TurnKindLabel = j.Value<string>("turn_kind_label"), BalanceVerdict = j.Value<string>("balance_verdict"),
            // revision 3.0 files have no "graded": every good / ok / needs-work verdict counted
            Graded = j["graded"]?.Type == JTokenType.Boolean ? j.Value<bool>("graded") : verdict is "good" or "ok" or "needs work"
        };
    }

    static CounterbalanceData.Pivot ReadPivot(JToken p, Func<JToken, Vector3> P, Func<JToken, float> T)
    {
        CounterbalanceData.Pivot pv = new()
        {
            T0 = T(p["t0"] ?? p["pivot_t0"]), T1 = T(p["t1"] ?? p["pivot_t1"]), Confidence = Num(p["confidence"], 1f),
            Point = P(p["pivot"]), Foot = p.Value<string>("pivot_foot"), Direction = p.Value<string>("direction"),
            LeaderRadius = Num(p["leader_radius_m"], float.NaN), SweptDeg = Num(p["swept_deg"], float.NaN)
        };
        if (p["leader_path"] is JObject path && path["t"] is JArray pt && path["pos"] is JArray pp)
        {
            pv.PathT = pt.Select(T).ToArray();
            pv.PathPos = pp.Select(P).ToArray();
        }
        else
        {
            pv.PathT = Array.Empty<float>();
            pv.PathPos = Array.Empty<Vector3>();
        }

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

    static float Num(JToken t) => Num(t, float.NaN);

    static float Num(JToken t, float fallback) =>
        t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? t.Value<float>() : fallback;

    static int Int(JToken t, int fallback) => t != null && t.Type == JTokenType.Integer ? t.Value<int>() : fallback;
}

/// <summary>
/// Layers "axis" and "floorcraft" (VIEWER_SPEC 3.5 / 3.6, user 2026-10-07 "the arrow T should find a stable position"):
///  - AXIS: the leader's CURRENT T - a bright T in the lead's red-orange at the T's averaged position and heading
///    (crossbar along his chest line = floorcraft draw_side_m, arrow stem along the normal to his chest =
///    draw_forward_m). It does NOT follow his chest: it grows in over the T's forming window (~1 beat) and then stays
///    put while he walks forward / back along the stem or side to side along the crossbar; when he leaves the T's
///    corridors or turns it by more than 30 deg the old T hands over (it fades to the record) and the next T grows in.
///    Captures without a v3 floorcraft.json fall back to the live chest axis (2 m chest line + 0.8 m forward arrow).
///  - FLOORCRAFT: the record, accumulating to the end of the take - every OLD T faded to a very faint teal-blue
///    (#19C3D6 at 5 % brightness, OldTLevel), a DOTTED line from each T origin to the next in time order, every AXIS
///    PIVOT (a reorientation of his axis > 30 deg: between Ts, before the first T, into / out of the counterbalance)
///    as a small arc at its place with its angle while recent, and a DIAL for every repeated turn about one point (the
///    counterbalance pivot - with the yellow pivot ring at her anchored foot at its centre - a spin or a couple turn):
///    a gauge whose arc fills with the cumulative degrees turned ("701°"). After ~2 measures (RecentSeconds) arcs and
///    dials fade to the same faint teal record (RecordLevel) without words; only the counterbalance dial keeps its
///    degree label.
///    In PHYSICS mode each GRADED turn - a dial (from its end: the verdict judges the whole turn) or a pivot on one
///    foot - shows its balance verdict, good / ok / needs work, as text while recent plus a shape that stays (dial rim
///    solid / dashed / dotted, pivot icon dot / ring / x), colour-coded subtly (never colour alone). A turn on both
///    feet or a stepped turn is not graded: no icon, its kind in words while fresh ("stepped", "both feet").
/// The v2 plant / move / rotation visuals are retired (their counts stay in hm_state). Rebuilt only when the frame,
/// the camera, physics mode or a setting changes (one additive glow mesh + pooled labels).
/// </summary>
public class FloorCraftOverlay : MonoBehaviour
{
    [Header("current T (layer axis)")]
    [Tooltip("the current T: the lead's red-orange (a deep base: the glow material and bloom lift it)")]
    public Color LeadColour = new(1f, 0.24f, 0.07f);
    public float TBrightness = 0.85f;
    public float TWidth = 0.02f;
    /// <summary>film director: only the dial that is running keeps its degree read-out; the turn arcs and old dials of the
    /// floor record show no numbers (a call-out shot is not cluttered by the record's labels)</summary>
    public bool LabelsOnlyCurrent;
    public float THeadLength = 0.15f, THeadWidth = 0.13f;

    [Header("record (layer floorcraft)")]
    [Tooltip("teal-blue of the old Ts and the dotted path (#19C3D6)")]
    public Color Teal = new(0.10f, 0.76f, 0.84f);
    [Tooltip("brightness of an old T once faded (very faint)")]
    public float OldTLevel = 0.05f;
    public float OldTWidth = 0.006f;
    [Tooltip("an old T shrinks to this scale as it fades into the record (13 Ts in a 2 m area overlap less)")]
    public float OldTScale = 0.75f;
    [Tooltip("s: an ended T fades from the lead colour to the faint teal record")]
    public float OldFadeSeconds = 1.0f;
    public float PathRecentLevel = 0.55f, PathLevel = 0.2f;
    public float DotSpacing = 0.06f, DotRadius = 0.011f;
    public float PivotRadiusM = 0.15f, PivotLevel = 0.25f;
    [Tooltip("brightness of a settled pivot arc / dial once it is part of the faint teal record (cf. OldTLevel)")]
    public float RecordLevel = 0.06f;
    [Tooltip("s: marks stay 'recent' this long after their end (labels, verdict words), then fade into the record")]
    public float RecentSeconds = 6.3f; // ~2 measures at 76 BPM; set from the beat grid
    public Color DialColour = new(1f, 0.85f, 0.1f);       // counterbalance yellow (VIEWER_SPEC 3.9)
    public Color TurnDialColour = new(1f, 0.6f, 0.16f);   // other repeated turns: amber
    public float DialLevel = 0.32f;
    public float SpiralPerTurnM = 0.035f;

    [Header("live axis fallback (no v3 floorcraft.json)")]
    public float LongAxisM = 2.0f;
    public float ForwardAxisM = 0.8f;
    public float AxisWidth = 0.016f;
    public float SmoothSeconds = 0.08f;

    // verdicts: subtle colours, always with text + a rim pattern (never colour alone)
    public static readonly Color VerdictGood = new(0.35f, 0.95f, 0.5f), VerdictOk = new(1f, 0.75f, 0.2f), VerdictBad = new(1f, 0.38f, 0.48f);
    static readonly string[] VerdictNames = { "good", "ok", "needs work", "" };

    FloorCraftData data;
    readonly List<FloorCraftData.Dial> dials = new();
    readonly Dictionary<int, CounterbalanceData.Pivot> dialPivots = new();
    Dancer lead;
    GlowMesh glow;
    Material material;
    Vector3[] axisOrigin, axisForward;
    double[] frameTime;
    readonly List<TextMesh> labels = new();
    readonly List<string> labelText = new();
    int labelsUsed;
    bool showAxis = true, showRecord = true, physicsMode;
    int lastFrame = -1;
    float lastTime = float.NaN;
    Vector3 lastEye;
    Vector3 screenDown = Vector3.back; // floor direction that points down the screen (dial labels hang below their gauge)
    bool dirty = true;
    static string[] degreeStrings;
    readonly Dictionary<int, string> verdictStrings = new();
    readonly Dictionary<string, string> kindStrings = new();

    public FloorCraftData Data => data;
    public bool HasRecord => data != null;
    public bool HasTAxes => data != null && data.HasTAxes;
    public string AxisSource { get; private set; } = "skeleton";
    public string DialSource { get; private set; } = "none";
    public bool AxisVisible => showAxis;
    public bool RecordVisible => showRecord;
    public bool PhysicsMode => physicsMode;

    // shown now (hm_state)
    public int CurrentT { get; private set; } = -1;
    public float CurrentTAlpha { get; private set; }
    public float CurrentTYawShown { get; private set; } = float.NaN;
    public int OldTShown { get; private set; }
    public int TransitionsShown { get; private set; }
    public int AxisPivotsShown { get; private set; }
    public int DialsShown { get; private set; }
    public int VerdictsShown { get; private set; }
    public int PivotLabelsShown { get; private set; }
    public int DialLabelsShown { get; private set; }
    public int DialVerdictsShown { get; private set; }
    public int DialVerdictsDue { get; private set; }
    public int CurrentDial { get; private set; } = -1;
    public float CurrentDialDeg { get; private set; }
    public string CurrentDialLabel { get; private set; }
    public int LastDial { get; private set; } = -1;
    public string LastDialLabel { get; private set; }
    public float OldTBrightnessMax { get; private set; }
    public Vector3 AxisOrigin { get; private set; }
    public Vector3 AxisForward { get; private set; }

    public IReadOnlyList<FloorCraftData.Dial> Dials => dials;

    public void Init(FloorCraftData floorcraft, CounterbalanceData counterbalance, Dancer leadDancer, CaptureTimeline timeline, float measureSeconds)
    {
        data = floorcraft;
        lead = leadDancer;
        if (measureSeconds > 0.5f) RecentSeconds = 2f * measureSeconds;
        material = GlowMesh.NewMaterial("Floor craft glow", 2.4f);
        glow = GlowMesh.Create("Floor craft", transform, material);
        degreeStrings ??= Enumerable.Range(0, 2161).Select(i => $"{i}°").ToArray();

        int n = lead.FrameCount;
        frameTime = new double[n];
        for (int f = 0; f < n; f++) frameTime[f] = timeline.AudioTimeOf(Mathf.Min(f, timeline.Count - 1));
        if (!HasTAxes) BuildLiveAxis();
        else AxisSource = "floorcraft.json t_axes (stable T)";

        BuildDials(counterbalance);
        dirty = true;
    }

    // ------------------------------------------------------------------ dials

    void BuildDials(CounterbalanceData counterbalance)
    {
        dials.Clear();
        dialPivots.Clear();
        List<CounterbalanceData.Pivot> pivots = data != null && data.Pivots.Count > 0 ? data.Pivots
            : counterbalance != null ? counterbalance.Intervals.SelectMany(iv => iv.Pivots).ToList()
            : new List<CounterbalanceData.Pivot>();
        if (data != null && data.Dials.Count > 0)
        {
            dials.AddRange(data.Dials);
            DialSource = "floorcraft.json dials";
        }
        else
        {
            // no v3 dials: every counterbalance pivot becomes a dial (its leader circling about her anchored foot)
            for (int i = 0; i < pivots.Count; i++)
            {
                CounterbalanceData.Pivot p = pivots[i];
                if (float.IsNaN(p.Point.x) || float.IsNaN(p.T0) || float.IsNaN(p.T1)) continue;
                float swept = float.IsNaN(p.SweptDeg) ? 0f : Mathf.Abs(p.SweptDeg);
                float sign = string.Equals(p.Direction, "cw", StringComparison.OrdinalIgnoreCase) ? -1f
                    : string.Equals(p.Direction, "ccw", StringComparison.OrdinalIgnoreCase) ? 1f : SignedPathSign(p);
                float start = 0f;
                if (p.PathPos.Length > 0)
                {
                    Vector3 v = p.PathPos[0] - p.Point;
                    start = Mathf.Atan2(v.z, v.x) * Mathf.Rad2Deg;
                }

                dials.Add(new FloorCraftData.Dial
                {
                    Id = dials.Count, Kind = "counterbalance", T0 = p.T0, T1 = p.T1, Centre = p.Point, Degrees = sign * swept,
                    RadiusM = float.IsNaN(p.LeaderRadius) ? 0.35f : p.LeaderRadius, ArcStartDeg = start, CounterbalancePivot = i,
                    Who = "lead circles the follow's anchored foot", Subject = "couple"
                });
            }

            DialSource = dials.Count > 0 ? (data != null && data.Pivots.Count > 0 ? "floorcraft.json pivots" : "counterbalance.json pivots") : "none";
        }

        foreach (FloorCraftData.Dial d in dials)
        {
            if (d.CounterbalancePivot >= 0 && d.CounterbalancePivot < pivots.Count)
            {
                CounterbalanceData.Pivot p = pivots[d.CounterbalancePivot];
                dialPivots[d.Id] = p;
                d.TickT = p.TickT;
                d.TickDeg = p.TickPos.Select(tp => Mathf.Atan2(tp.z - d.Centre.z, tp.x - d.Centre.x) * Mathf.Rad2Deg).ToArray();
            }

            BuildRun(d);
        }
    }

    static float SignedPathSign(CounterbalanceData.Pivot p)
    {
        float s = 0;
        for (int i = 1; i < p.PathPos.Length; i++)
        {
            Vector3 a = p.PathPos[i - 1] - p.Point, b = p.PathPos[i] - p.Point;
            s += Vector2.SignedAngle(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
        }

        return s < 0 ? -1f : 1f;
    }

    /// <summary>the dial's running signed degrees on the frame grid: the leader's angle about the centre (orbit dials)
    /// or his heading (spins / couple turns), scaled so the end equals the analysed total; linear in time when the
    /// motion disagrees with the analysis</summary>
    void BuildRun(FloorCraftData.Dial d)
    {
        List<float> ts = new(), raw = new();
        float prev = float.NaN, cum = 0f;
        for (int f = 0; f < frameTime.Length; f++)
        {
            float t = (float)frameTime[f];
            if (t < d.T0 - 1e-4f) continue;
            if (t > d.T1 + 1e-4f) break;
            float ang;
            if (d.Orbit)
            {
                Vector3 c = lead.Joint(f, SmplJoint.Spine3) - d.Centre;
                ang = Mathf.Atan2(c.z, c.x) * Mathf.Rad2Deg;
            }
            else
            {
                ang = HeadingAt(f, t);
            }

            if (float.IsNaN(ang)) continue;
            if (!float.IsNaN(prev)) cum += Mathf.DeltaAngle(prev, ang);
            prev = ang;
            ts.Add(t);
            raw.Add(cum);
        }

        d.RunRawEndDeg = raw.Count > 0 ? raw[^1] : float.NaN;
        float end = d.RunRawEndDeg;
        float ratio = Mathf.Abs(end) > 1f ? d.Degrees / end : float.NaN;
        if (raw.Count >= 3 && !float.IsNaN(ratio) && ratio > 0.6f && ratio < 1.6f)
        {
            d.RunT = ts.ToArray();
            d.RunDeg = raw.Select(x => x * ratio).ToArray();
            d.RunDeg[^1] = d.Degrees;
            d.RunSource = d.Orbit ? "leader about the centre" : "leader heading";
        }
        else
        {
            d.RunT = new[] { d.T0, Mathf.Max(d.T1, d.T0 + 1e-3f) };
            d.RunDeg = new[] { 0f, d.Degrees };
            d.RunSource = "time";
        }
    }

    float HeadingAt(int f, float t)
    {
        if (data?.AxisT != null && data.AxisT.Length > 1)
        {
            int i = Mathf.Clamp(CounterbalanceData.IndexAtOrBefore(data.AxisT, t), 0, data.AxisT.Length - 1);
            int j = Mathf.Min(i + 1, data.AxisT.Length - 1);
            float k = j > i ? Mathf.Clamp01((t - data.AxisT[i]) / Mathf.Max(1e-6f, data.AxisT[j] - data.AxisT[i])) : 0f;
            return data.AxisYawDeg[i] + Mathf.DeltaAngle(data.AxisYawDeg[i], data.AxisYawDeg[j]) * k;
        }

        return YawOf(ForwardOf(lead, f));
    }

    public static float RunAt(FloorCraftData.Dial d, float time)
    {
        if (d.RunT.Length == 0) return 0f;
        if (time <= d.RunT[0]) return d.RunDeg[0];
        if (time >= d.RunT[^1]) return d.RunDeg[^1];
        int i = CounterbalanceData.IndexAtOrBefore(d.RunT, time);
        float k = (time - d.RunT[i]) / Mathf.Max(1e-6f, d.RunT[i + 1] - d.RunT[i]);
        return Mathf.Lerp(d.RunDeg[i], d.RunDeg[i + 1], k);
    }

    // ------------------------------------------------------------------ live axis fallback

    void BuildLiveAxis()
    {
        int n = frameTime.Length;
        axisOrigin = new Vector3[n];
        axisForward = new Vector3[n];
        if (data?.AxisT != null && data.AxisT.Length > 1)
        {
            AxisSource = "floorcraft.json track (live axis)";
            for (int f = 0; f < n; f++)
            {
                float t = (float)frameTime[f];
                Vector3 o = CounterbalanceData.Sample(data.AxisT, data.AxisOrigin, t);
                axisOrigin[f] = new Vector3(o.x, 0f, o.z);
                axisForward[f] = Dir(HeadingAt(f, t));
            }

            return;
        }

        AxisSource = "skeleton (spine3 + shoulder line, smoothed; live axis)";
        Vector3[] o0 = new Vector3[n], f0 = new Vector3[n];
        for (int f = 0; f < n; f++)
        {
            Vector3 c = lead.Joint(f, SmplJoint.Spine3);
            o0[f] = new Vector3(c.x, 0f, c.z);
            f0[f] = ForwardOf(lead, f);
        }

        float interval = n > 1 ? (float)((frameTime[^1] - frameTime[0]) / (n - 1)) : 1f / 30f;
        float sigma = SmoothSeconds / Mathf.Max(1e-3f, interval);
        axisOrigin = Smooth(o0, sigma, false);
        axisForward = Smooth(f0, sigma, true);
    }

    /// <summary>horizontal forward of a dancer's chest: perpendicular to the shoulder line (right x up)</summary>
    public static Vector3 ForwardOf(Dancer d, int f)
    {
        Vector3 right = d.Joint(f, SmplJoint.R_Shoulder) - d.Joint(f, SmplJoint.L_Shoulder);
        right.y = 0;
        if (right.sqrMagnitude < 1e-8f || float.IsNaN(right.x)) return Vector3.forward;
        Vector3 fwd = Vector3.Cross(right.normalized, Vector3.up);
        return fwd.normalized;
    }

    public static Vector3 Dir(float yawDeg) => new(Mathf.Cos(yawDeg * Mathf.Deg2Rad), 0f, Mathf.Sin(yawDeg * Mathf.Deg2Rad));

    static float YawOf(Vector3 v) => Mathf.Atan2(v.z, v.x) * Mathf.Rad2Deg;

    static Vector3[] Smooth(Vector3[] v, float sigma, bool normalise)
    {
        int n = v.Length;
        if (n == 0 || sigma < 0.3f) return v;
        int r = Mathf.CeilToInt(3f * sigma);
        Vector3[] o = new Vector3[n];
        for (int f = 0; f < n; f++)
        {
            Vector3 s = Vector3.zero;
            float w = 0;
            for (int i = -r; i <= r; i++)
            {
                Vector3 x = v[Mathf.Clamp(f + i, 0, n - 1)];
                if (float.IsNaN(x.x)) continue;
                float g = Mathf.Exp(-0.5f * i * i / (sigma * sigma));
                s += x * g;
                w += g;
            }

            o[f] = w > 0 ? s / w : v[f];
            if (normalise) o[f] = o[f].sqrMagnitude > 1e-10f ? o[f].normalized : v[f];
        }

        return o;
    }

    // ------------------------------------------------------------------ switches

    public void SetVisible(bool axis, bool record)
    {
        if (showAxis == axis && showRecord == record) return;
        showAxis = axis;
        showRecord = record;
        glow.SetVisible(axis || record);
        dirty = true;
    }

    bool roleHidden;
    float roleAlpha = 1f;

    /// <summary>roleHidden (RoleHiddenSpans): the leader's alpha 0..1 - his current T, the record being laid down (old Ts, the T-to-T path,
    /// axis pivots, dials: all derived from his pose) are scaled by it (the glow mesh and the labels fade together) and not drawn at ~0.
    /// Afterwards a record entry that lies wholly inside the fully hidden part of the span stays out.</summary>
    public void SetRoleAlpha(float alpha)
    {
        alpha = Mathf.Clamp01(float.IsFinite(alpha) ? alpha : 1f);
        bool hidden = alpha <= RoleHiddenSpans.HiddenBelow;
        if (Mathf.Abs(alpha - roleAlpha) < 1e-4f && hidden == roleHidden) return;
        roleAlpha = alpha;
        roleHidden = hidden;
        if (glow != null) glow.SetOpacity(alpha);
        dirty = true;
    }

    public bool RoleHidden => roleHidden;
    public float RoleAlpha => roleAlpha;

    /// <summary>the entry [t0, t1] (audio seconds) was derived from the leader's pose inside a hidden span: it is never shown</summary>
    bool Muted(float t0, float t1)
    {
        RoleHiddenSpans spans = HeadMovement.Instance != null ? HeadMovement.Instance.RoleHidden : null;
        return spans != null && spans.Any && spans.WhollyInsideAudio(Role.Lead, t0, t1);
    }

    /// <summary>physics mode adds the balance verdicts to the dials and axis pivots</summary>
    public void SetPhysicsMode(bool on)
    {
        if (physicsMode == on) return;
        physicsMode = on;
        dirty = true;
    }

    public void MarkDirty() => dirty = true;

    // ------------------------------------------------------------------ per frame

    static float Smooth01(float x) => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(x));

    /// <summary>scales rgb only (the glow shader multiplies rgb by alpha: scaling both would fade quadratically)</summary>
    static Color Scale(Color c, float k) => new(c.r * k, c.g * k, c.b * k, c.a);

    public void SetTime(float time, int frame, Camera viewer)
    {
        if (glow == null || frame < 0) return;
        frame = Mathf.Min(frame, frameTime.Length - 1);
        Vector3 eye = viewer != null ? viewer.transform.position : Vector3.zero;
        bool cameraMoved = (eye - lastEye).sqrMagnitude > 1e-6f;
        if (!dirty && frame == lastFrame && Mathf.Approximately(time, lastTime) && !cameraMoved) return;
        dirty = false;
        lastFrame = frame;
        lastTime = time;
        lastEye = eye;
        labelsUsed = 0;
        OldTShown = TransitionsShown = AxisPivotsShown = DialsShown = VerdictsShown = 0;
        PivotLabelsShown = DialLabelsShown = DialVerdictsShown = DialVerdictsDue = 0;
        CurrentT = -1;
        CurrentTAlpha = 0f;
        CurrentTYawShown = float.NaN;
        CurrentDial = -1;
        CurrentDialDeg = 0f;
        CurrentDialLabel = null;
        LastDial = -1;
        LastDialLabel = null;
        OldTBrightnessMax = 0f;
        if (viewer != null)
        {
            Vector3 up = viewer.transform.up, fw = viewer.transform.forward;
            Vector3 down = new(-up.x, 0f, -up.z);
            if (down.sqrMagnitude < 1e-4f) down = new Vector3(-fw.x, 0f, -fw.z);
            screenDown = down.sqrMagnitude > 1e-8f ? down.normalized : Vector3.back;
        }

        glow.Begin();
        glow.Viewer = eye;
        if (roleHidden)
        {
            // the leader is hidden: nothing derived from his pose is drawn (the live axis keeps tracking for hm_state)
            if (!HasTAxes)
            {
                AxisOrigin = axisOrigin[frame];
                AxisForward = axisForward[frame];
            }
        }
        else if (HasTAxes)
        {
            int current = TIndexAt(time);
            if (showRecord)
            {
                Transitions(time);
                OldTs(time, current);
                AxisPivots(time);
                DialRecord(time);
            }

            if (showAxis) CurrentTAxis(time, frame);
        }
        else
        {
            if (showRecord) DialRecord(time);
            AxisOrigin = axisOrigin[frame];
            AxisForward = axisForward[frame];
            if (showAxis) LiveAxis(AxisOrigin, AxisForward);
        }

        glow.End();
        for (int i = labelsUsed; i < labels.Count; i++)
        {
            if (labels[i].gameObject.activeSelf) labels[i].gameObject.SetActive(false);
        }
    }

    /// <summary>the T active now (t0 &lt;= time &lt;= t1; a T starting where the previous ends wins), or -1</summary>
    public int TIndexAt(float time)
    {
        if (!HasTAxes) return -1;
        for (int i = data.TAxes.Count - 1; i >= 0; i--)
        {
            FloorCraftData.TAxis a = data.TAxes[i];
            if (time >= a.T0 && time <= a.T1) return i;
        }

        return -1;
    }

    void CurrentTAxis(float time, int frame)
    {
        int i = TIndexAt(time);
        if (i < 0)
        {
            AxisOrigin = new Vector3(float.NaN, 0f, float.NaN);
            AxisForward = Vector3.zero;
            return;
        }

        FloorCraftData.TAxis a = data.TAxes[i];
        float g = a.TFormed > a.T0 + 1e-3f ? Smooth01((time - a.T0) / (a.TFormed - a.T0)) : 1f;
        float yaw = a.YawDeg, scale, bright;
        FloorCraftData.TAxis prev = a.SameAs >= 0 ? data.TAxes.Find(x => x.Id == a.SameAs) : null;
        if (prev != null)
        {
            // a rotated T at the same place: the T turns in place from the old heading to the new one
            yaw = prev.YawDeg + Mathf.DeltaAngle(prev.YawDeg, a.YawDeg) * g;
            scale = 1f;
            bright = 1f;
        }
        else
        {
            scale = Mathf.Lerp(0.5f, 1f, g);
            bright = g;
        }

        CurrentT = a.Id;
        CurrentTAlpha = bright;
        CurrentTYawShown = yaw;
        AxisOrigin = a.Origin;
        AxisForward = Dir(yaw);
        DrawT(a.Origin, yaw, scale, TWidth, Scale(LeadColour, bright * TBrightness), 0.016f, true);
    }

    /// <summary>a T on the floor: crossbar along the chest line (his left-right), arrow stem along his forward</summary>
    void DrawT(Vector3 origin, float yawDeg, float scale, float width, Color c, float y, bool bright)
    {
        if (c.r + c.g + c.b < 1e-4f) return;
        Vector3 fwd = Dir(yawDeg), left = new(-fwd.z, 0f, fwd.x);
        Vector3 p = Floor(origin, y);
        float half = data.DrawSideM * 0.5f * scale, len = data.DrawForwardM * scale;
        Color tip = Scale(c, 0.55f);
        glow.Strip(p, p + left * half, width, c, tip, Vector3.up);
        glow.Strip(p, p - left * half, width, c, tip, Vector3.up);
        float head = (bright ? THeadLength : THeadLength * 0.5f) * scale;
        glow.FloorStrip(p, p + fwd * (len - head * 0.8f), width * (bright ? 1.1f : 1f), c);
        Arrowhead(p + fwd * len, fwd, head, (bright ? THeadWidth : THeadWidth * 0.5f) * scale, c);
        glow.Disc(p, (bright ? 0.032f : 0.018f) * scale, Scale(c, 1.15f), Scale(c, 0.6f), 16);
    }

    void OldTs(float time, int current)
    {
        for (int i = 0; i < data.TAxes.Count; i++)
        {
            FloorCraftData.TAxis a = data.TAxes[i];
            if (i == current || time < a.T0 || Muted(a.T0, a.T1)) continue; // the current T (also while forming) is drawn by CurrentTAxis
            float k = Smooth01((time - a.T1) / Mathf.Max(0.05f, OldFadeSeconds));
            float level = Mathf.Lerp(TBrightness, OldTLevel, k);
            Color c = Scale(Color.Lerp(LeadColour, Teal, k), level);
            DrawT(a.Origin, a.YawDeg, Mathf.Lerp(1f, OldTScale, k), Mathf.Lerp(TWidth, OldTWidth, k), c, 0.006f, false);
            if (k >= 1f) OldTBrightnessMax = Mathf.Max(OldTBrightnessMax, level);
            OldTShown++;
        }
    }

    void Transitions(float time)
    {
        Color baseColour = Color.Lerp(Teal, Color.white, 0.3f);
        foreach (FloorCraftData.Transition tr in data.Transitions)
        {
            if (time < tr.T0 || tr.SamePlace || float.IsNaN(tr.From.x) || float.IsNaN(tr.To.x) || Muted(tr.T0, tr.T1)) continue;
            float p = tr.T1 > tr.T0 + 0.02f ? Mathf.Clamp01((time - tr.T0) / (tr.T1 - tr.T0)) : 1f;
            Vector3 end = Vector3.Lerp(tr.From, tr.To, p);
            float age = time - tr.T1;
            float level = age <= 0 ? PathRecentLevel : Mathf.Lerp(PathRecentLevel, PathLevel, Smooth01(age / Mathf.Max(0.1f, RecentSeconds)));
            Dots(tr.From, end, Scale(baseColour, level));
            TransitionsShown++;
        }
    }

    void Dots(Vector3 a, Vector3 b, Color c)
    {
        Vector3 d = new(b.x - a.x, 0f, b.z - a.z);
        float len = d.magnitude;
        if (len < 1e-4f) return;
        Vector3 dir = d / len;
        for (float s = DotSpacing * 0.5f; s <= len; s += DotSpacing) glow.Disc(Floor(a + dir * s, 0.007f), DotRadius, c, Scale(c, 0.6f), 8);
    }

    /// <summary>record level of a settled mark: fresh (1) -> recent (settledLevel, over half the recent window) -> the
    /// faint teal record (RecordLevel, over the next recent window, starting RecentSeconds after its end)</summary>
    float SettleLevel(float age, float settledLevel, out float settle, out float record)
    {
        settle = age <= 0 ? 0f : Smooth01(age / Mathf.Max(0.1f, 0.5f * RecentSeconds));
        record = age <= RecentSeconds ? 0f : Smooth01((age - RecentSeconds) / Mathf.Max(0.1f, RecentSeconds));
        return Mathf.Lerp(Mathf.Lerp(1f, settledLevel, settle), RecordLevel, record);
    }

    void AxisPivots(float time)
    {
        foreach (FloorCraftData.AxisPivot pv in data.AxisPivots)
        {
            if (time < pv.T0 || pv.Dial >= 0 || float.IsNaN(pv.At.x) || float.IsNaN(pv.SignedDeg) || float.IsNaN(pv.FromYawDeg) || Muted(pv.T0, pv.T1)) continue;
            float k = pv.T1 > pv.T0 + 1e-3f ? Mathf.Clamp01((time - pv.T0) / (pv.T1 - pv.T0)) : 1f;
            float age = time - pv.T1;
            float level = SettleLevel(age, PivotLevel, out float settle, out float record);
            Color c = Scale(Color.Lerp(LeadColour, Teal, settle), level);
            float radius = PivotRadiusM + 0.03f * (pv.Stack % 3);
            Vector3 o = Floor(pv.At, 0.009f);
            float swept = pv.SignedDeg * k;
            Vector3 end = Arc(o, radius, pv.FromYawDeg, swept, 0.008f, c, 0.025f);
            glow.FloorStrip(o + Dir(pv.FromYawDeg) * (radius - 0.025f), o + Dir(pv.FromYawDeg) * (radius + 0.025f), 0.006f, c);
            bool recent = age < RecentSeconds;
            int v = pv.Evaluation?.Index ?? 3;
            // a verdict icon only on a GRADED turn (a pivot on one foot); turns on both feet / stepped turns get none
            bool verdict = physicsMode && v < 3;
            if (verdict)
            {
                VerdictIcon(end + Dir(pv.FromYawDeg + swept) * 0.05f, v, recent ? Mathf.Max(0.45f, level) : Mathf.Max(0.12f, level));
                VerdictsShown++;
            }

            if (recent && !LabelsOnlyCurrent)
            {
                // the words only while the turn is fresh (~1 measure): the verdict, or how it was turned when it is not
                // graded (physics mode); afterwards the angle until the record fades, then nothing (the arc stays)
                bool fresh = physicsMode && age < 0.5f * RecentSeconds;
                string kind = pv.Evaluation?.TurnKindLabel;
                string text = fresh && verdict ? VerdictLabel(Mathf.Abs(swept), v)
                    : fresh && !string.IsNullOrEmpty(kind) ? KindLabel(Mathf.Abs(swept), kind)
                    : Degrees(Mathf.Abs(swept));
                Color lc = fresh && verdict ? VerdictColour(v) : Color.Lerp(LeadColour, Teal, settle);
                Label(o + Dir(pv.FromYawDeg + swept * 0.5f) * (radius + 0.1f), text, lc, Mathf.Max(0.5f, level), 0.06f);
                PivotLabelsShown++;
            }

            AxisPivotsShown++;
        }
    }

    string KindLabel(float deg, string kind)
    {
        int d = Mathf.Clamp(Mathf.RoundToInt(deg), 0, degreeStrings.Length - 1);
        string key = kind + "|" + d;
        if (!kindStrings.TryGetValue(key, out string s))
        {
            s = $"{degreeStrings[d]}\n{kind}";
            kindStrings[key] = s;
        }

        return s;
    }

    /// <summary>arc (spiral beyond 360 deg) round o from yaw 'from' sweeping 'swept' degrees, arrowhead at the end;
    /// returns the end point</summary>
    Vector3 Arc(Vector3 o, float radius, float from, float swept, float width, Color c, float perTurn)
    {
        int segs = Mathf.Clamp(Mathf.CeilToInt(Mathf.Abs(swept) / 5f), 1, 400);
        float RadAt(float deg) => radius + perTurn * Mathf.Abs(deg) / 360f;
        Vector3 prev = o + Dir(from) * radius;
        for (int s = 1; s <= segs; s++)
        {
            float dg = swept * s / segs;
            Vector3 q = o + Dir(from + dg) * RadAt(dg);
            glow.FloorStrip(prev, q, width, c);
            prev = q;
        }

        if (Mathf.Abs(swept) > 4f)
        {
            float endYaw = from + swept;
            Vector3 tangent = Dir(endYaw + Mathf.Sign(swept) * 90f);
            Arrowhead(o + Dir(endYaw) * RadAt(swept) + tangent * (2.5f * width), tangent, 6f * width, 5f * width, c);
        }

        return prev;
    }

    void DialRecord(float time)
    {
        foreach (FloorCraftData.Dial d in dials)
        {
            if (time < d.T0 || Muted(d.T0, d.T1)) continue;
            bool running = time <= d.T1;
            float run = running ? RunAt(d, time) : d.Degrees;
            float age = time - d.T1;
            // fresh -> faint (DialLevel) -> the teal record level after ~2 measures, like the old Ts
            float record = 0f, level = 1f;
            if (!running) level = SettleLevel(age, DialLevel, out _, out record);
            bool counterbalance = d.Kind == "counterbalance";
            bool recent = running || age < RecentSeconds;
            Color baseColour = Color.Lerp(counterbalance ? DialColour : TurnDialColour, Teal, record);
            Color c = Scale(baseColour, level);
            Vector3 o = Floor(d.Centre, 0.011f);
            float r = Mathf.Clamp(float.IsNaN(d.RadiusM) ? 0.3f : d.RadiusM, 0.2f, 0.6f);
            float dir = d.Degrees < 0 ? -1f : 1f;

            // the gauge face: a faint ring with quarter-turn ticks from the start angle
            glow.Ring(o, r, 0.005f, Scale(c, 0.35f), 64);
            for (int q = 0; q < 4; q++)
            {
                Vector3 u = Dir(d.ArcStartDeg + dir * 90f * q);
                glow.FloorStrip(o + u * (r - 0.04f), o + u * (r - 0.006f), q == 0 ? 0.009f : 0.006f, Scale(c, q == 0 ? 0.8f : 0.45f));
            }

            // beats (counterbalance pivots): a short tick on the face where the leader was on each beat
            for (int k = 0; k < d.TickT.Length && k < d.TickDeg.Length; k++)
            {
                if (d.TickT[k] > time) break;
                Vector3 u = Dir(d.TickDeg[k]);
                glow.FloorStrip(o + u * (r - 0.02f), o + u * (r + 0.03f), 0.008f, Scale(c, 0.7f));
            }

            // the fill: the cumulative degrees turned, spiralling outward per full turn
            Vector3 end = Arc(o, r, d.ArcStartDeg, run, 0.02f, c, SpiralPerTurnM);
            if (running) glow.FloorStrip(o, end, 0.007f, Scale(c, 0.8f)); // the needle

            // the centre: the counterbalance pivot ring at her anchored foot, else a dot on the turning point
            if (counterbalance)
            {
                Color y = Scale(Color.Lerp(DialColour, Teal, record), level);
                glow.Ring(o, 0.07f, 0.012f, y, 32);
                glow.Disc(o, 0.022f, y, y, 12);
            }
            else
            {
                glow.Disc(o, 0.02f, c, c, 12);
            }

            // the verdict evaluates the WHOLE turn: shown from its end (t1), never while it runs
            int v = d.Evaluation?.Index ?? 3;
            bool verdict = physicsMode && v < 3 && !running;
            if (physicsMode && v < 3 && !running) DialVerdictsDue++;
            if (verdict)
            {
                // the verdict rim: solid = good, dashed = ok, dotted = needs work (subtle colour on top of the shape)
                float rim = r + SpiralPerTurnM * Mathf.Abs(d.Degrees) / 360f + 0.045f;
                glow.Ring(o, rim, 0.008f, Scale(Color.Lerp(VerdictColour(v), Teal, record), 0.6f * Mathf.Max(recent ? 0.5f : 0.15f, level)), 72,
                    v == 0 ? 1f : v == 1 ? 0.6f : 0.25f);
                VerdictsShown++;
                DialVerdictsShown++;
            }

            // the read-out sits just below the gauge on screen (her feet stand on the centre of a counterbalance dial):
            // the degrees (+ the verdict in words while recent); after ~2 measures only the counterbalance keeps its
            // degree label, the other dials fade into the record without words
            float abs = Mathf.Abs(run);
            string text = null;
            if (recent) text = verdict ? VerdictLabel(abs, v) : Degrees(abs);
            else if (counterbalance) text = Degrees(abs);
            if (text != null && (!LabelsOnlyCurrent || running))
            {
                float height = counterbalance ? 0.11f : 0.09f;
                float outer = r + SpiralPerTurnM * Mathf.Abs(d.Degrees) / 360f + (verdict ? 0.06f : 0.02f);
                Color lc = verdict && recent ? Color.Lerp(baseColour, VerdictColour(v), 0.6f) : baseColour;
                Label(o + screenDown * (outer + 0.03f), text, lc, running ? 1f : recent ? Mathf.Max(0.45f, level) : 0.4f, height, TextAnchor.UpperCenter);
                DialLabelsShown++;
            }

            DialsShown++;
            LastDial = d.Id;
            LastDialLabel = text;
            if (running)
            {
                CurrentDial = d.Id;
                CurrentDialDeg = run;
                CurrentDialLabel = text;
            }
        }
    }

    void VerdictIcon(Vector3 at, int v, float level)
    {
        Color c = Scale(VerdictColour(v), 0.8f * level);
        Vector3 p = Floor(at, 0.012f);
        switch (v)
        {
            case 0: glow.Disc(p, 0.022f, c, c, 12); break;                 // good: filled dot
            case 1: glow.Ring(p, 0.022f, 0.008f, c, 16); break;            // ok: ring
            default:                                                        // needs work: a small x
                glow.FloorStrip(p + new Vector3(-0.02f, 0, -0.02f), p + new Vector3(0.02f, 0, 0.02f), 0.008f, c);
                glow.FloorStrip(p + new Vector3(-0.02f, 0, 0.02f), p + new Vector3(0.02f, 0, -0.02f), 0.008f, c);
                break;
        }
    }

    public static Color VerdictColour(int v) => v switch { 0 => VerdictGood, 1 => VerdictOk, 2 => VerdictBad, _ => Color.grey };

    string VerdictLabel(float deg, int v)
    {
        int d = Mathf.Clamp(Mathf.RoundToInt(deg), 0, degreeStrings.Length - 1);
        int key = d * 4 + v;
        if (!verdictStrings.TryGetValue(key, out string s))
        {
            s = $"{degreeStrings[d]}\n{VerdictNames[Mathf.Clamp(v, 0, 3)]}";
            verdictStrings[key] = s;
        }

        return s;
    }

    void LiveAxis(Vector3 o, Vector3 fwd)
    {
        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
        Vector3 p = new(o.x, 0.012f, o.z);
        Color c = LeadColour;
        Color tip = c * 0.25f;
        float half = LongAxisM * 0.5f;
        glow.Strip(p, p + right * half, AxisWidth, c, tip, Vector3.up);
        glow.Strip(p, p - right * half, AxisWidth, c, tip, Vector3.up);
        Vector3 end = p + fwd * ForwardAxisM;
        glow.FloorStrip(p, end - fwd * 0.12f, AxisWidth * 1.2f, c);
        Arrowhead(end, fwd, 0.16f, 0.13f, c);
        glow.Disc(p, 0.035f, c * 1.2f, c * 0.6f, 16);
    }

    /// <summary>flat filled triangle on the floor, tip at 'tip' pointing along dir</summary>
    void Arrowhead(Vector3 tip, Vector3 dir, float length, float width, Color c)
    {
        dir.y = 0;
        if (dir.sqrMagnitude < 1e-10f) return;
        dir.Normalize();
        Vector3 side = Vector3.Cross(Vector3.up, dir) * (width * 0.5f);
        Vector3 b = tip - dir * length;
        glow.Triangle(tip, b + side, b - side, c);
    }

    static Vector3 Floor(Vector3 p, float y) => new(p.x, y, p.z);

    static string Degrees(float deg) => degreeStrings[Mathf.Clamp(Mathf.RoundToInt(deg), 0, degreeStrings.Length - 1)];

    void Label(Vector3 at, string text, Color colour, float alpha, float height, TextAnchor anchor = TextAnchor.LowerCenter)
    {
        if (labelsUsed >= labels.Count)
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            labels.Add(DanceText.WorldLabel(transform, "Floor craft label", font, height, colour));
            labelText.Add(null);
        }

        int i = labelsUsed++;
        TextMesh l = labels[i];
        if (!ReferenceEquals(labelText[i], text))
        {
            labelText[i] = text;
            l.text = text;
        }

        DanceText.SetHeight(l, height);
        if (l.anchor != anchor) l.anchor = anchor;
        Color c = colour;
        float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        if (m > 1e-3f) c /= m;
        c.a = 1f;
        DanceText.SetColor(l, new Color(c.r, c.g, c.b, l.color.a));
        if (!l.gameObject.activeSelf) l.gameObject.SetActive(true);
        l.transform.position = new Vector3(at.x, 0.04f, at.z);
        DanceText.Billboard(l.transform);
        DanceText.SetAlpha(l, Mathf.Clamp01(alpha) * roleAlpha);
    }

    // ------------------------------------------------------------------ checks / hm_state

    /// <summary>live axis check (fallback only): angle between the long axis and the RAW shoulder line projection</summary>
    public float LongAxisErrorDeg(int frame)
    {
        if (axisForward == null || frame < 0 || frame >= axisForward.Length) return float.NaN;
        Vector3 right = lead.Joint(frame, SmplJoint.R_Shoulder) - lead.Joint(frame, SmplJoint.L_Shoulder);
        right.y = 0;
        Vector3 longAxis = Vector3.Cross(Vector3.up, axisForward[frame]);
        float a = Vector3.Angle(right, longAxis);
        return Mathf.Min(a, 180f - a);
    }

    /// <summary>the leader's chest relative to a T: (along its forward, along its crossbar) in metres, and whether that
    /// is inside the T's allowed (plus-shaped) region</summary>
    public Vector2 ChestInT(FloorCraftData.TAxis a, int frame, out bool inRegion)
    {
        Vector3 c = lead.Joint(Mathf.Clamp(frame, 0, lead.FrameCount - 1), SmplJoint.Spine3) - a.Origin;
        Vector3 fwd = Dir(a.YawDeg), left = new(-fwd.z, 0f, fwd.x);
        float along = c.x * fwd.x + c.z * fwd.z, side = c.x * left.x + c.z * left.z;
        float hw = data.HalfWidthM;
        inRegion = (Mathf.Abs(side) <= hw && Mathf.Abs(along) <= data.CorridorForwardM) || (Mathf.Abs(along) <= hw && Mathf.Abs(side) <= data.CorridorSideM);
        return new Vector2(along, side);
    }

    public Dictionary<string, object> State()
    {
        Dictionary<string, object> s = new()
        {
            ["axisVisible"] = showAxis, ["recordVisible"] = showRecord, ["axisSource"] = AxisSource, ["dialSource"] = DialSource,
            ["mode"] = HasTAxes ? "t_axes" : "live_axis", ["hasFloorcraftJson"] = data != null, ["version"] = data?.Version ?? 0,
            ["physicsMode"] = physicsMode, ["roleHidden"] = roleHidden,
            // v2 records: still in floorcraft.json, no longer drawn (the stable T replaced them)
            ["plants"] = data?.Plants.Count ?? 0, ["moves"] = data?.Moves.Count ?? 0, ["rotations"] = data?.Rotations.Count ?? 0,
            ["pivots"] = data?.Pivots.Count ?? 0,
            ["tAxes"] = data?.TAxes.Count ?? 0, ["transitions"] = data?.Transitions.Count ?? 0,
            ["transitionsDrawable"] = data?.Transitions.Count(t => !t.SamePlace) ?? 0, ["axisPivots"] = data?.AxisPivots.Count ?? 0,
            ["axisPivotsDrawable"] = data?.AxisPivots.Count(p => p.Dial < 0) ?? 0, ["dials"] = dials.Count,
            ["currentT"] = CurrentT, ["currentTAlpha"] = CurrentTAlpha, ["currentTYawShownDeg"] = CurrentTYawShown,
            ["oldTShown"] = OldTShown, ["oldTLevel"] = OldTLevel, ["oldTBrightnessMax"] = OldTBrightnessMax,
            ["oldTColour"] = new[] { Teal.r, Teal.g, Teal.b }, ["transitionsShown"] = TransitionsShown,
            ["axisPivotsShown"] = AxisPivotsShown, ["dialsShown"] = DialsShown, ["verdictsShown"] = VerdictsShown,
            ["pivotLabelsShown"] = PivotLabelsShown, ["dialLabelsShown"] = DialLabelsShown,
            ["dialVerdictsShown"] = DialVerdictsShown, ["dialVerdictsDue"] = DialVerdictsDue, ["recordLevel"] = RecordLevel,
            ["axisPivotsGraded"] = data?.AxisPivots.Count(p => p.Dial < 0 && (p.Evaluation?.Index ?? 3) < 3) ?? 0,
            ["labelsShown"] = labelsUsed, ["frame"] = lastFrame, ["time"] = lastTime, ["vertices"] = glow != null ? glow.VertexCount : 0,
            ["recentSeconds"] = RecentSeconds, ["axisOrigin"] = new[] { AxisOrigin.x, AxisOrigin.z },
            ["axisYawDeg"] = AxisForward.sqrMagnitude > 0 ? YawOf(AxisForward) : float.NaN,
            ["dialList"] = dials.Select(d => new object[]
            {
                d.Id, d.Kind, d.T0, d.T1, d.Degrees, d.Evaluation?.Verdict, d.RunSource, d.RunRawEndDeg, new[] { d.Centre.x, d.Centre.z }, d.RadiusM
            }).ToList()
        };
        if (HasTAxes)
        {
            s["tHalfWidthM"] = data.HalfWidthM;
            s["tCorridorM"] = new[] { data.CorridorForwardM, data.CorridorSideM };
            s["tDrawM"] = new[] { data.DrawForwardM, data.DrawSideM };
            s["pivotDeg"] = data.PivotDeg;
            s["tList"] = data.TAxes.Select(a => new object[]
            {
                a.Id, a.T0, a.TFormed, a.T1, a.Origin.x, a.Origin.z, a.YawDeg, a.StartReason, a.EndReason, a.SameAs
            }).ToList();
            s["pivotList"] = data.AxisPivots.Select(p => new object[]
            {
                p.Id, p.T0, p.T1, p.SignedDeg, p.Evaluation?.Verdict, p.Dial, p.FromT, p.ToT, new[] { p.At.x, p.At.z }, p.Context,
                p.Evaluation?.TurnKind, p.Evaluation?.Graded ?? false
            }).ToList();
            if (CurrentT >= 0)
            {
                FloorCraftData.TAxis a = data.TAxes.Find(x => x.Id == CurrentT);
                Vector2 rel = ChestInT(a, lastFrame, out bool inRegion);
                s["currentTOrigin"] = new[] { a.Origin.x, a.Origin.z };
                s["currentTYawDeg"] = a.YawDeg;
                s["currentTFormed"] = lastTime >= a.TFormed;
                s["chestInT"] = new[] { rel.x, rel.y };
                s["chestInRegion"] = inRegion;
            }
        }
        else if (lastFrame >= 0 && lead != null && lastFrame < lead.FrameCount)
        {
            Vector3 c = lead.Joint(lastFrame, SmplJoint.Spine3);
            s["axisToChestM"] = new Vector2(c.x - AxisOrigin.x, c.z - AxisOrigin.z).magnitude;
            s["longAxisErrorDeg"] = LongAxisErrorDeg(lastFrame);
        }

        int di = CurrentDial >= 0 ? CurrentDial : LastDial;
        FloorCraftData.Dial cd = di >= 0 ? dials.Find(x => x.Id == di) : null;
        if (cd != null)
        {
            s["dial"] = new Dictionary<string, object>
            {
                ["id"] = cd.Id, ["kind"] = cd.Kind, ["running"] = CurrentDial >= 0, ["runningDeg"] = CurrentDial >= 0 ? CurrentDialDeg : cd.Degrees,
                ["label"] = CurrentDial >= 0 ? CurrentDialLabel : LastDialLabel, ["degrees"] = cd.Degrees, ["absDeg"] = cd.AbsDeg,
                ["t0"] = cd.T0, ["t1"] = cd.T1, ["centre"] = new[] { cd.Centre.x, cd.Centre.z }, ["verdict"] = cd.Evaluation?.Verdict,
                ["evaluation"] = cd.Evaluation?.Text, ["runSource"] = cd.RunSource, ["who"] = cd.Who
            };
        }

        return s;
    }

    void OnDestroy()
    {
        if (material != null) Destroy(material);
    }
}
