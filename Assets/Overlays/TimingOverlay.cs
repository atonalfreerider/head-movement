using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// timing.json (dancecap timing stage, exported by export_unity) as data: refined zouk beat grid, footstep
/// touchdowns with their asynchrony to the beat, per-role asynchrony summaries and the spline |jerk| of the SMPL-24
/// joints. All times are reference seconds; audio time = t + timeline.TimeToAudio.
/// </summary>
public class TimingData
{
    public struct Beat
    {
        public double T;
        public int Type, Measure, Count;
    }

    public struct Touchdown
    {
        public double T;
        public bool Left;
        public float AsyncMs;
        public double BeatT;
        public float SigmaMs;
    }

    public struct Summary
    {
        public int N;
        public float MeanMs, Ci0, Ci1, HitRate;
    }

    public readonly List<Beat> Beats = new();
    public readonly Dictionary<Role, List<Touchdown>> Touchdowns = new() { [Role.Lead] = new(), [Role.Follow] = new() };
    public readonly Dictionary<Role, Summary> Summaries = new();
    public readonly Dictionary<Role, float[][]> Jerk = new();
    public float FollowMinusLeadMs = float.NaN;

    /// <summary>timing.json absolute_timing.valid: false = no audio-video sync event pins the camera clocks to the
    /// soundtrack, so a step's offset from the beat includes an unknown A/V offset and absolute verdicts (on beat / late /
    /// early, hit rate) are not shown - only relative timing (follow - lead). A file without the field keeps the old
    /// verdicts (AbsoluteKnown false).</summary>
    public bool AbsoluteValid = true;
    public bool AbsoluteKnown;

    public static TimingData Load(string path)
    {
        JObject root = JObject.Parse(File.ReadAllText(path));
        TimingData d = new();
        if (root["absolute_timing"] is JObject abs && abs["valid"]?.Type == JTokenType.Boolean)
        {
            d.AbsoluteKnown = true;
            d.AbsoluteValid = abs.Value<bool>("valid");
        }

        if (root["beats"]?["grid"] is JArray grid)
        {
            foreach (JToken row in grid)
            {
                d.Beats.Add(new Beat
                {
                    T = row[0].Value<double>(), Type = row[1].Value<int>(),
                    Measure = row.Count() > 2 ? row[2].Value<int>() : 0, Count = row.Count() > 3 ? row[3].Value<int>() : 0
                });
            }
        }

        foreach (Role role in new[] { Role.Lead, Role.Follow })
        {
            string key = role.ToString().ToLowerInvariant();
            if (root["footsteps"]?[key] is JArray steps)
            {
                foreach (JToken s in steps)
                {
                    d.Touchdowns[role].Add(new Touchdown
                    {
                        T = s.Value<double>("t"), Left = s.Value<string>("foot") == "left",
                        AsyncMs = Num(s["async_ms"]), BeatT = Num(s["beat_t"]),
                        SigmaMs = Num(s["sigma"]) * 1000f
                    });
                }

                d.Touchdowns[role].Sort((a, b) => a.T.CompareTo(b.T));
            }

            if (root["asynchrony"]?[key] is JObject a)
            {
                JArray ci = a["ci95_ms"] as JArray;
                d.Summaries[role] = new Summary
                {
                    N = a["n"]?.Type == JTokenType.Integer ? a.Value<int>("n") : 0, MeanMs = Num(a["mean_ms"]),
                    Ci0 = ci != null && ci.Count == 2 ? Num(ci[0]) : float.NaN,
                    Ci1 = ci != null && ci.Count == 2 ? Num(ci[1]) : float.NaN, HitRate = Num(a["hit_rate"])
                };
            }

            if (root["jerk"]?[key] is JArray jerk)
            {
                d.Jerk[role] = jerk.Select(f => f.Select(x => x.Value<float>()).ToArray()).ToArray();
            }
        }

        d.FollowMinusLeadMs = Num(root["asynchrony"]?["follow_minus_lead_ms"]);
        return d;
    }

    static float Num(JToken t) =>
        t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? t.Value<float>() : float.NaN;
}

/// <summary>
/// Layer "timing": a glowing ring on the floor at every touchdown (coloured by its asynchrony to the beat: green on
/// the beat, amber/red late, blue early) that flares at the touchdown and fades over RecentSeconds, plus a beat
/// ticker HUD strip: the beat grid around the playhead with both dancers' touchdowns.
/// </summary>
public class TimingOverlay : MonoBehaviour
{
    public float RecentSeconds = 1.6f;
    public float TickerSeconds = 2.0f;

    TimingData data;
    CaptureTimeline timeline;
    readonly List<(Role role, TimingData.Touchdown td, Vector3 pos, LineRenderer ring)> rings = new();
    bool visible = true;
    float lastAudioTime = float.NaN;

    public int TouchdownCount => rings.Count;
    public int VisibleRings { get; private set; }

    public void Init(TimingData timing, CaptureTimeline frames, Dancer lead, Dancer follow, Material glow)
    {
        data = timing;
        timeline = frames;
        foreach ((Role role, Dancer dancer) in new[] { (Role.Lead, lead), (Role.Follow, follow) })
        {
            foreach (TimingData.Touchdown td in data.Touchdowns[role])
            {
                double audio = timeline.ToAudio(td.T);
                if (!timeline.Covers(audio)) continue;
                int frame = Mathf.Min(timeline.FrameAt(audio), dancer.FrameCount - 1);
                Vector3 foot = dancer.Joint(frame, td.Left ? SmplJoint.L_Foot : SmplJoint.R_Foot);
                Vector3 pos = new(foot.x, 0.006f, foot.z);
                LineRenderer ring = OverlayDraw.Line(transform, $"{role} touchdown {td.T:0.000}", glow,
                    role == Role.Lead ? 0.012f : 0.009f, 32, true);
                ring.gameObject.SetActive(false);
                rings.Add((role, td, pos, ring));
            }
        }
    }

    public void SetVisible(bool on)
    {
        visible = on;
        if (!on)
        {
            foreach (var r in rings) r.ring.gameObject.SetActive(false);
            VisibleRings = 0;
        }
        else
        {
            float t = lastAudioTime;
            lastAudioTime = float.NaN;
            if (!float.IsNaN(t)) SetTime(t);
        }
    }

    /// <summary>update ring visibility/brightness for an audio time</summary>
    public void SetTime(float audioTime)
    {
        if (Mathf.Approximately(audioTime, lastAudioTime)) return;
        lastAudioTime = audioTime;
        if (!visible) return;

        int shown = 0;
        foreach ((Role role, TimingData.Touchdown td, Vector3 pos, LineRenderer ring) in rings)
        {
            float age = audioTime - (float)timeline.ToAudio(td.T);
            bool on = age >= -0.02f && age <= RecentSeconds;
            ring.gameObject.SetActive(on);
            if (!on) continue;
            shown++;
            float k = Mathf.Clamp01(age / RecentSeconds);
            float baseR = role == Role.Lead ? 0.11f : 0.085f;
            float r = baseR * (age < 0.15f ? Mathf.Lerp(0.5f, 1.15f, Mathf.Clamp01(age / 0.15f)) : Mathf.Lerp(1.15f, 1f, k));
            OverlayDraw.Ring(ring, pos, r);
            Color c = OverlayDraw.AsyncColor(td.AsyncMs) * Mathf.Lerp(2.2f, 0.25f, k);
            c.a = 1;
            OverlayDraw.SetColor(ring, c);
        }

        VisibleRings = shown;
    }

    /// <summary>latest touchdown at or before an audio time</summary>
    public bool Latest(Role role, float audioTime, out TimingData.Touchdown td)
    {
        td = default;
        List<TimingData.Touchdown> list = data.Touchdowns[role];
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (timeline.ToAudio(list[i].T) <= audioTime + 1e-4)
            {
                td = list[i];
                return true;
            }
        }

        return false;
    }

    /// <summary>false when timing.json says the absolute (on-the-beat) timing is not pinned by an A/V sync event</summary>
    public bool AbsoluteValid => data == null || data.AbsoluteValid;

    public IEnumerable<string> HudLines(float audioTime)
    {
        bool absolute = data.AbsoluteValid;
        foreach (Role role in new[] { Role.Lead, Role.Follow })
        {
            string last = Latest(role, audioTime, out TimingData.Touchdown td)
                ? $"last touchdown {td.AsyncMs:+0;-0} ms{(absolute ? "" : " vs beat")} ({(td.Left ? "L" : "R")})"
                : "no touchdown yet";
            // without an A/V sync event the hit rate is an absolute on-beat claim: not shown
            string summary = data.Summaries.TryGetValue(role, out TimingData.Summary s) && s.N > 0
                ? $"   mean {s.MeanMs:+0;-0} ms [{s.Ci0:+0;-0}, {s.Ci1:+0;-0}]{(absolute ? $"  hit {s.HitRate:P0}" : "")}"
                : "";
            yield return $"{role}: {last}{summary}";
        }

        if (!absolute)
        {
            yield return "timing vs beat UNSYNCED (no A/V sync event): relative only" +
                         (float.IsNaN(data.FollowMinusLeadMs) ? "" : $" - follow - lead {data.FollowMinusLeadMs:+0;-0} ms");
        }
    }

    /// <summary>beat ticker: grid ticks (taller = stronger beat type), playhead at the centre, lead touchdowns
    /// above and follow touchdowns below, coloured by asynchrony. Call from OnGUI.</summary>
    public void DrawTicker(Rect area, float audioTime)
    {
        OverlayDraw.Rect(area, new Color(0f, 0f, 0f, 0.55f));
        float mid = area.y + area.height * 0.5f;
        float PxOf(double audio) => area.x + area.width * (0.5f + (float)(audio - audioTime) / (2f * TickerSeconds));

        foreach (TimingData.Beat b in data.Beats)
        {
            double a = timeline.ToAudio(b.T);
            if (Math.Abs(a - audioTime) > TickerSeconds) continue;
            float h = b.Type == 1 ? 0.7f : b.Type == 2 ? 0.45f : 0.22f;
            float x = PxOf(a);
            Color c = b.Type == 1 ? new Color(1f, 1f, 1f, 0.95f) : b.Type == 2 ? new Color(0.85f, 0.85f, 0.85f, 0.8f)
                : new Color(0.6f, 0.6f, 0.6f, 0.6f);
            OverlayDraw.Rect(new Rect(x - 1, mid - area.height * h * 0.5f, b.Type == 1 ? 3 : 2, area.height * h), c);
            if (b.Type == 1 && b.Count == 0)
            {
                GUI.Label(new Rect(x + 3, area.y, 40, 18), $"m{b.Measure + 1}");
            }
        }

        foreach ((Role role, float y) in new[] { (Role.Lead, area.y + area.height * 0.18f), (Role.Follow, area.y + area.height * 0.82f) })
        {
            foreach (TimingData.Touchdown td in data.Touchdowns[role])
            {
                double a = timeline.ToAudio(td.T);
                if (Math.Abs(a - audioTime) > TickerSeconds) continue;
                float x = PxOf(a);
                OverlayDraw.Rect(new Rect(x - 4, y - 4, 8, 8), OverlayDraw.AsyncColor(td.AsyncMs));
            }

            GUI.Label(new Rect(area.x + 4, y - 9, 60, 18), role == Role.Lead ? "lead" : "follow");
        }

        OverlayDraw.Rect(new Rect(area.x + area.width * 0.5f - 1, area.y, 2, area.height), new Color(1f, 0.3f, 0.9f, 0.9f));
    }

    void OnDestroy()
    {
        foreach (var r in rings)
        {
            if (r.ring != null) Destroy(r.ring.gameObject);
        }
    }
}
