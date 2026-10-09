using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Resolves the shot list's annotation / camera targets (direction "targets") to Unity world positions at a DANCE time
/// (reference seconds), interpolated between the two neighbouring frames like the film's sub-frame poses, so arrows stay
/// on a moving foot in slow motion: joint, foot (ankle-ball midpoint at the floor), com (physics.json), couple_com
/// (mass-weighted, on the floor = the counterbalance dot), pivot_foot, counterbalance_axis, leader_t, dial, floor_point,
/// stance_foot, miniature_couple, graph_node. Unknown / unresolvable targets return false (the annotation hides).
/// </summary>
public class FilmTargets
{
    readonly HeadMovement hm;

    public FilmTargets(HeadMovement head)
    {
        hm = head;
    }

    float TimeToAudio => hm.Timeline != null ? hm.Timeline.TimeToAudio : 0f;

    /// <summary>neighbouring frames of a dance time and the blend between them</summary>
    public bool Frames(float dance, out int f0, out int f1, out float k)
    {
        f0 = f1 = 0;
        k = 0f;
        CaptureTimeline tl = hm.Timeline;
        if (tl == null || tl.Count == 0) return false;
        double t = dance + tl.TimeToAudio;
        double[] ts = tl.AudioTimes;
        int n = ts.Length;
        if (t <= ts[0] || n == 1) return true;
        if (t >= ts[n - 1])
        {
            f0 = f1 = n - 1;
            return true;
        }

        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (ts[mid] <= t) lo = mid;
            else hi = mid;
        }

        f0 = lo;
        f1 = hi;
        k = (float)((t - ts[lo]) / Math.Max(1e-9, ts[hi] - ts[lo]));
        return true;
    }

    public Dancer DancerOf(string role) =>
        string.Equals(role, "follow", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "follower", StringComparison.OrdinalIgnoreCase)
            ? hm.FollowDancer
            : hm.LeadDancer;

    public static Role RoleOf(string role) =>
        string.Equals(role, "follow", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "follower", StringComparison.OrdinalIgnoreCase)
            ? Role.Follow
            : Role.Lead;

    public Vector3 Joint(Dancer d, SmplJoint j, float dance)
    {
        if (d == null || !Frames(dance, out int f0, out int f1, out float k)) return Nan;
        f0 = Mathf.Min(f0, d.FrameCount - 1);
        f1 = Mathf.Min(f1, d.FrameCount - 1);
        Vector3 a = d.Joint(f0, j), b = d.Joint(f1, j);
        if (float.IsNaN(a.x)) return b;
        if (float.IsNaN(b.x)) return a;
        return Vector3.Lerp(a, b, k);
    }

    public Vector3 Foot(Dancer d, bool left, float dance, float y = 0.04f)
    {
        Vector3 a = Joint(d, left ? SmplJoint.L_Ankle : SmplJoint.R_Ankle, dance);
        Vector3 b = Joint(d, left ? SmplJoint.L_Foot : SmplJoint.R_Foot, dance);
        Vector3 m = (a + b) * 0.5f;
        if (float.IsNaN(m.x)) return Nan;
        m.y = y;
        return m;
    }

    /// <summary>pelvis midpoint of both dancers on the floor</summary>
    public Vector3 CoupleCentre(float dance)
    {
        Vector3 l = Joint(hm.LeadDancer, SmplJoint.Pelvis, dance), f = Joint(hm.FollowDancer, SmplJoint.Pelvis, dance);
        // roleHidden (RoleHiddenSpans): a hidden dancer does not pull the camera - the centre is weighted by each dancer's visibility at
        // this dance time (a pure function of it), so it follows the one who is seen and glides to the midpoint as the other fades in
        float wl = 1f, wf = 1f;
        RoleHiddenSpans spans = hm.RoleHidden;
        if (spans != null && spans.Any)
        {
            double audio = dance + TimeToAudio;
            wl = spans.AlphaAtAudio(Role.Lead, audio);
            wf = spans.AlphaAtAudio(Role.Follow, audio);
            if (wl + wf < 1e-4f) wl = wf = 1f;
        }

        Vector3 p = (l * wl + f * wf) / (wl + wf);
        p.y = 0f;
        return p;
    }

    /// <summary>midpoint of both dancers' four feet (XZ), on the floor</summary>
    public Vector3 FeetCentre(float dance)
    {
        Vector3 s = Vector3.zero;
        int n = 0;
        foreach (Dancer d in new[] { hm.LeadDancer, hm.FollowDancer })
        {
            foreach (bool left in new[] { true, false })
            {
                Vector3 f = Foot(d, left, dance, 0f);
                if (float.IsNaN(f.x)) continue;
                s += f;
                n++;
            }
        }

        return n > 0 ? s / n : CoupleCentre(dance);
    }

    /// <summary>physics.json centre of mass (Unity, origin-shifted); falls back to the torso estimate</summary>
    public Vector3 Com(Role role, float dance)
    {
        PhysicsData p = hm.Physics;
        if (p != null && p.Dancers.TryGetValue(role, out PhysicsData.DancerTrack tr) && tr.Com != null && p.T != null && p.T.Length > 0)
        {
            Vector3 v = SampleSeries(p.T, tr.Com, dance);
            if (!float.IsNaN(v.x)) return v;
        }

        Dancer d = role == Role.Follow ? hm.FollowDancer : hm.LeadDancer;
        if (d == null || !Frames(dance, out int f0, out int f1, out float k)) return Nan;
        return Vector3.Lerp(d.CenterOfMass(Mathf.Min(f0, d.FrameCount - 1)), d.CenterOfMass(Mathf.Min(f1, d.FrameCount - 1)), k);
    }

    public float Mass(Role role)
    {
        PhysicsData p = hm.Physics;
        return p != null && p.Dancers.TryGetValue(role, out PhysicsData.DancerTrack tr) && tr.MassKg > 0 ? tr.MassKg : role == Role.Lead ? 75f : 55f;
    }

    /// <summary>mass-weighted COM of the couple (3D)</summary>
    public Vector3 CoupleCom3(float dance)
    {
        Vector3 a = Com(Role.Lead, dance), b = Com(Role.Follow, dance);
        float ma = Mass(Role.Lead), mb = Mass(Role.Follow);
        if (float.IsNaN(a.x)) return b;
        if (float.IsNaN(b.x)) return a;
        return (a * ma + b * mb) / (ma + mb);
    }

    public Vector3 CoupleComFloor(float dance)
    {
        Vector3 c = CoupleCom3(dance);
        c.y = 0.004f;
        return c;
    }

    /// <summary>horizontal unit facing of a dancer (shoulders + hips, as film/analysis/exit_analysis.py), Unity frame</summary>
    public Vector3 Facing(Dancer d, float dance)
    {
        Vector3 right = Joint(d, SmplJoint.R_Shoulder, dance) - Joint(d, SmplJoint.L_Shoulder, dance) +
                        Joint(d, SmplJoint.R_Hip, dance) - Joint(d, SmplJoint.L_Hip, dance);
        right.y = 0f;
        if (float.IsNaN(right.x) || right.sqrMagnitude < 1e-8f) return Vector3.forward;
        return Vector3.Cross(right.normalized, Vector3.up).normalized;
    }

    public static Vector3 FloorPoint(JToken xz)
    {
        if (xz is not JArray a || a.Count < 2) return Nan;
        return new Vector3(a[0].Value<float>(), 0.003f, -a[a.Count - 1].Value<float>()) + DanceOrigin.Offset;
    }

    /// <summary>resolve a target object of the shot list at a dance time</summary>
    public bool Resolve(JObject target, float dance, out Vector3 p)
    {
        p = Nan;
        if (target == null) return false;
        string type = target.Value<string>("type") ?? "";
        DanceLayers layers = DanceLayers.Instance;
        try
        {
            switch (type)
            {
                case "joint":
                    if (!TryJoint(target.Value<string>("joint"), out SmplJoint j) || !KnownDancer(target.Value<string>("dancer"))) return false;
                    p = Joint(DancerOf(target.Value<string>("dancer")), j, dance);
                    break;
                case "foot":
                {
                    // never a default: an unknown side / dancer (a TBD that slipped through) draws nothing
                    if (!TrySide(target.Value<string>("side"), out bool leftFoot) || !KnownDancer(target.Value<string>("dancer"))) return false;
                    p = Foot(DancerOf(target.Value<string>("dancer")), leftFoot, dance);
                    break;
                }
                case "com":
                    if (!KnownDancer(target.Value<string>("dancer"))) return false;
                    p = Com(RoleOf(target.Value<string>("dancer")), dance);
                    break;
                case "couple_com":
                    p = CoupleComFloor(dance);
                    break;
                case "pivot_foot":
                {
                    CounterbalanceData cb = layers != null && layers.Counterbalance != null ? layers.Counterbalance.Data : null;
                    int i = IntervalIndex(target, cb);
                    if (cb == null || i < 0 || i >= cb.Intervals.Count || cb.Intervals[i].Pivots.Count == 0) return false;
                    p = cb.Intervals[i].Pivots[0].Point;
                    p.y = 0.03f;
                    break;
                }
                case "counterbalance_axis":
                {
                    CounterbalanceData cb = layers != null && layers.Counterbalance != null ? layers.Counterbalance.Data : null;
                    int i = IntervalIndex(target, cb);
                    if (cb == null || i < 0 || i >= cb.Intervals.Count || cb.Intervals[i].Pivots.Count == 0) return false;
                    Vector3 foot = cb.Intervals[i].Pivots[0].Point;
                    foot.y = 0f;
                    p = (foot + CoupleCom3(dance)) * 0.5f;
                    break;
                }
                case "leader_t":
                {
                    FloorCraftData fc = layers != null && layers.FloorCraft != null ? layers.FloorCraft.Data : null;
                    int id = target.Value<int?>("t_axis") ?? -1;
                    FloorCraftData.TAxis a = fc?.TAxes.Find(x => x.Id == id);
                    if (a == null) return false;
                    p = a.Origin;
                    p.y = 0.01f;
                    break;
                }
                case "dial":
                {
                    FloorCraftData fc = layers != null && layers.FloorCraft != null ? layers.FloorCraft.Data : null;
                    int id = target.Value<int?>("dial") ?? -1;
                    FloorCraftData.Dial dl = fc?.Dials.Find(x => x.Id == id);
                    if (dl == null) return false;
                    p = dl.Centre;
                    p.y = 0.01f;
                    break;
                }
                case "floor_point":
                    p = FloorPoint(target["xz"]);
                    break;
                case "stance_foot":
                    p = StanceFoot(target, dance);
                    break;
                case "miniature_couple":
                    if (layers?.Graph == null || !layers.Graph.HasPath) return false;
                    p = layers.Graph.MiniPosition + Vector3.up * 0.12f;
                    break;
                case "graph_node":
                {
                    if (layers?.Graph == null) return false;
                    string move = target.Value<string>("move");
                    DanceGraphData.Node node = layers.Graph.Data.Nodes.Find(n => n.Move == move || n.Slug == move || n.Id == move);
                    if (node == null) return false;
                    p = node.World;
                    break;
                }
                default:
                    return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return !float.IsNaN(p.x) && !float.IsInfinity(p.x);
    }

    /// <summary>"left" | "right" only (case-insensitive); anything else (TBD, null, both) is not a foot</summary>
    public static bool TrySide(string side, out bool left)
    {
        left = true;
        if (string.Equals(side, "left", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(side, "right", StringComparison.OrdinalIgnoreCase))
        {
            left = false;
            return true;
        }

        return false;
    }

    /// <summary>lead | leader | follow | follower; an absent dancer means the leader (kept for older directions)</summary>
    public static bool KnownDancer(string who) =>
        who == null || who.Equals("lead", StringComparison.OrdinalIgnoreCase) || who.Equals("leader", StringComparison.OrdinalIgnoreCase) ||
        who.Equals("follow", StringComparison.OrdinalIgnoreCase) || who.Equals("follower", StringComparison.OrdinalIgnoreCase);

    /// <summary>the counterbalance interval a target names: "interval_t" (a dance time inside it: the interval that
    /// contains it, else the nearest within 3 s; robust to a different interval count), else "interval" as an index or
    /// "cbN" (the N-th); -1 when it names none. Interval times are on the audio clock.</summary>
    public int IntervalIndex(JObject target, CounterbalanceData cb)
    {
        if (cb == null) return -1;
        JToken at = target["interval_t"];
        if (at != null && (at.Type == JTokenType.Float || at.Type == JTokenType.Integer))
        {
            float audio = at.Value<float>() + TimeToAudio;
            int best = -1;
            float bestD = float.MaxValue;
            for (int k = 0; k < cb.Intervals.Count; k++)
            {
                CounterbalanceData.Interval iv = cb.Intervals[k];
                if (audio >= iv.T0 && audio <= iv.T1) return k;
                float d = audio < iv.T0 ? iv.T0 - audio : audio - iv.T1;
                if (d < bestD)
                {
                    bestD = d;
                    best = k;
                }
            }

            return bestD <= 3f ? best : -1;
        }

        JToken it = target["interval"];
        if (it == null) return 0;
        if (it.Type == JTokenType.Integer) return it.Value<int>();
        string s = it.Type == JTokenType.String ? it.Value<string>() : null;
        if (s == null) return -1;
        if (s.StartsWith("cb", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        else return int.TryParse(s, out int idx) ? idx : -1;
        return int.TryParse(s, out int n) ? n - 1 : -1;
    }

    /// <summary>the dancer's foot in contact at a beat ("first type-1 beat after 44.0 s"): physics contact L/R, else the
    /// lower ankle; the target then follows that foot</summary>
    Vector3 StanceFoot(JObject target, float dance)
    {
        Dancer d = DancerOf(target.Value<string>("dancer"));
        Role role = RoleOf(target.Value<string>("dancer"));
        float at = dance;
        string spec = target.Value<string>("at_beat") ?? "";
        Match m = Regex.Match(spec, @"([0-9]+(\.[0-9]+)?)");
        BeatGrid beats = hm.Beats;
        if (m.Success && beats != null)
        {
            float after = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + TimeToAudio;
            int want = spec.Contains("type-1") || spec.Contains("type 1") ? 1 : 0;
            for (int i = 0; i < beats.Count; i++)
            {
                if (beats.Times[i] < after || (want != 0 && beats.Types[i] != want)) continue;
                at = beats.Times[i] - TimeToAudio;
                break;
            }
        }

        bool left = true;
        PhysicsData p = hm.Physics;
        string contact = null;
        if (p != null && p.Dancers.TryGetValue(role, out PhysicsData.DancerTrack tr) && tr.Contact != null && p.T != null)
        {
            int i = Nearest(p.T, at);
            if (i >= 0 && i < tr.Contact.Length) contact = tr.Contact[i];
        }

        if (contact == "L") left = true;
        else if (contact == "R") left = false;
        else left = Joint(d, SmplJoint.L_Ankle, at).y <= Joint(d, SmplJoint.R_Ankle, at).y;
        return Foot(d, left, dance);
    }

    public static bool TryJoint(string name, out SmplJoint j)
    {
        string n = (name ?? "").Trim().ToLowerInvariant().Replace(" ", "_");
        j = n switch
        {
            "pelvis" => SmplJoint.Pelvis,
            "left_hip" or "l_hip" => SmplJoint.L_Hip,
            "right_hip" or "r_hip" => SmplJoint.R_Hip,
            "spine1" => SmplJoint.Spine1,
            "left_knee" or "l_knee" => SmplJoint.L_Knee,
            "right_knee" or "r_knee" => SmplJoint.R_Knee,
            "spine2" => SmplJoint.Spine2,
            "left_ankle" or "l_ankle" => SmplJoint.L_Ankle,
            "right_ankle" or "r_ankle" => SmplJoint.R_Ankle,
            "spine3" => SmplJoint.Spine3,
            "left_foot" or "l_foot" => SmplJoint.L_Foot,
            "right_foot" or "r_foot" => SmplJoint.R_Foot,
            "neck" => SmplJoint.Neck,
            "left_collar" or "l_collar" => SmplJoint.L_Collar,
            "right_collar" or "r_collar" => SmplJoint.R_Collar,
            "head" => SmplJoint.Head,
            "left_shoulder" or "l_shoulder" => SmplJoint.L_Shoulder,
            "right_shoulder" or "r_shoulder" => SmplJoint.R_Shoulder,
            "left_elbow" or "l_elbow" => SmplJoint.L_Elbow,
            "right_elbow" or "r_elbow" => SmplJoint.R_Elbow,
            "left_wrist" or "l_wrist" => SmplJoint.L_Wrist,
            "right_wrist" or "r_wrist" => SmplJoint.R_Wrist,
            "left_hand" or "l_hand" => SmplJoint.L_Hand,
            "right_hand" or "r_hand" => SmplJoint.R_Hand,
            _ => (SmplJoint)(-1)
        };
        return (int)j >= 0;
    }

    public static Vector3 SampleSeries(double[] t, Vector3[] v, float time)
    {
        if (t == null || v == null || t.Length == 0) return Nan;
        int n = Mathf.Min(t.Length, v.Length);
        if (time <= t[0]) return v[0];
        if (time >= t[n - 1]) return v[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (t[mid] <= time) lo = mid;
            else hi = mid;
        }

        float k = (float)((time - t[lo]) / Math.Max(1e-9, t[hi] - t[lo]));
        Vector3 a = v[lo], b = v[hi];
        if (float.IsNaN(a.x)) return b;
        if (float.IsNaN(b.x)) return a;
        return Vector3.Lerp(a, b, k);
    }

    static int Nearest(double[] t, float time)
    {
        if (t == null || t.Length == 0) return -1;
        return CaptureTimeline.Nearest(t, time);
    }

    public static readonly Vector3 Nan = new(float.NaN, float.NaN, float.NaN);
}
