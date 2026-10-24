using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HmFilm;
using Newtonsoft.Json;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The v3 flow checks on the LIVE director (Play mode, a v3 direction started with hm_film_show): the camera is a pure function of film time, so the
/// whole film (or a window) is swept frame by frame at the film's fps without rendering:
///
///   unity command hm_film_flow --action check [--from 0 --to 367.7] [--out <json>]
///
/// - jumps: a frame whose eye step or turn is far above its neighbours' (a per-frame camera jump) - none may exist outside the replay cuts;
/// - ground: eye height outside [0.5, 2.4] m except the crane ways and the overheads;
/// - surfaces: each frustum surface reads as a picture (alpha &gt;= 0.5 while it covers 1-90 % of the frame) for at least 0.6 s;
/// - hand-offs: the frame before each arrival and the frame of each departure: the phone's surface at alpha 1 covers the whole frame with its
///   cover at 1 (the picture is the phone's video alone on both sides of the switch);
/// - holds: each phone's full-frame window and the 3D shots' own poses between transitions; the fastest azimuth turn of every transition;
/// - phone poses: a phone shot whose own pose is not the phone's (CameraTour fell back to an orbit).
/// </summary>
public static class FilmFlowCheck
{
    [CliCommand("hm_film_flow", "v3 film flow checks on the running director (Play mode): check | series [--from s --to s] [--out json]")]
    public static string Run(
        [CliArg("action", "check | series (per-frame eye, rotation, fov, surfaces of a window)")] string action = "check",
        [CliArg("from", "film seconds (default 0)")] float from = 0f,
        [CliArg("to", "film seconds (default: the film's end)")] float to = -1f,
        [CliArg("out", "write the full result to this json file")] string @out = null)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("enter Play mode and start the film first (hm_film_show --action start)");
        FilmDirector fd = FilmDirector.Instance;
        if (fd == null || !fd.IsReady) throw new InvalidOperationException("no film is ready (hm_film_show --action start, then poll state until ready)");
        FilmCamera fc = fd.FilmCam;
        FlowTimeline flow = fc.Flow;
        if (flow == null) throw new InvalidOperationException($"the direction has no flow (v3) or it was rejected: {fc.FlowError}");
        if (action == "plan")
        {
            List<string> lines = new();
            foreach (FilmShot s in fc.Shots)
            {
                if (s.Flow?.T == null || s.Flow.TransEnd < from || (to > 0f && s.Flow.TransStart > to)) continue;
                lines.Add($"{s.Flow.TransStart:0.000} {s.Flow.T.Type}: {fc.PlanReport(s)}");
            }

            foreach (FlowFrustum fr in flow.Frustums)
            {
                if (fr.D < from || (to > 0f && fr.A > to)) continue;
                lines.Add($"frustum {fr.Camera} {fr.Role} {fr.A:0.000}: depth {fc.SurfaceDepth(fr):0.00} m");
            }

            return JsonConvert.SerializeObject(lines, Formatting.Indented);
        }

        bool wasPaused = fd.Paused;
        fd.SetPaused(true);
        float fps = fd.AspectInfo.Fps > 1f ? fd.AspectInfo.Fps : 30f;
        float t0 = Mathf.Max(0f, from), t1 = to > 0f ? Mathf.Min(to, fd.FilmDuration) : fd.FilmDuration;
        int n = Mathf.FloorToInt((t1 - t0) * fps) + 1;
        float aspect = fd.Vertical ? 9f / 16f : 16f / 9f;

        Vector3[] eye = new Vector3[n], look = new Vector3[n];
        Quaternion[] rot = new Quaternion[n];
        float[] fov = new float[n], time = new float[n];
        string[] state = new string[n];
        bool[] crane = new bool[n], phoneOwn = new bool[n], pov = new bool[n];
        List<FlowSurface> surfs = new();
        Dictionary<FlowFrustum, float> readable = new();
        foreach (FlowFrustum f in flow.Frustums) readable[f] = 0f;
        List<string> phoneFallback = new(), handoffs = new(), offscreen = new();
        int offscreenFrames = 0;
        List<object> series = action == "series" ? new List<object>() : null;
        for (int i = 0; i < n; i++)
        {
            float t = t0 + i / fps;
            time[i] = t;
            FilmPose p = fc.Evaluate(t);
            eye[i] = p.Eye;
            look[i] = p.Pov ? new Vector3(float.NaN, 0f, 0f) : p.Look;
            rot[i] = p.Rotation();
            fov[i] = p.Fov;
            pov[i] = p.Pov;
            FilmShot tr = fc.TransitionShot, own = fc.CameraShot;
            crane[i] = tr != null && (tr.Flow.T.CranePath || tr.Flow.T.Crane) || own != null && own.Mode == "overhead_floorcraft";
            phoneOwn[i] = own != null && own.Mode == "camera_tour";
            state[i] = tr != null ? $"{tr.Flow.T.Type}>{tr.Segments[0].Id} u={fc.TransitionU:0.00}" : own != null ? $"{own.Mode} {own.Segments[0].Id}" : "?";
            if (phoneOwn[i] && (!p.Pov || fc.PovCamera == null) && phoneFallback.Count < 40) phoneFallback.Add($"{F(t)} {state[i]}: not the phone's pose");
            // the couple in view during a transition (its centre 1 m up inside the frame)
            if (tr != null)
            {
                Quaternion inv = Quaternion.Inverse(rot[i]);
                Vector3 cv = inv * (fd.Targets.CoupleCentre(fd.Clock.Dance(t)) + Vector3.up - p.Eye);
                float ty = Mathf.Tan(p.Fov * 0.5f * Mathf.Deg2Rad);
                bool inView = cv.z > 0.1f && Mathf.Abs(cv.x / (cv.z * ty * aspect)) <= 1f && Mathf.Abs(cv.y / (cv.z * ty)) <= 1f;
                if (!inView)
                {
                    offscreenFrames++;
                    if (offscreen.Count < 60) offscreen.Add($"{F(t)} {state[i]}");
                }
            }

            // surfaces readable as pictures
            flow.Surfaces(t, surfs);
            float dance = fd.Clock.Dance(t);
            foreach (FlowSurface s in surfs)
            {
                if (s.Alpha < 0.5f) continue;
                float cov = Coverage(fd, s, dance, p, aspect, out bool behind);
                if (!behind && cov > 0.01f && cov < 0.97f) readable[s.Frustum] += 1f / fps;
            }

            if (series != null)
            {
                List<string> sv = new();
                foreach (FlowSurface s in surfs) sv.Add($"{s.Camera} {s.Role} a={s.Alpha:0.000} top={s.Top:0.000} cov={Coverage(fd, s, dance, p, aspect, out bool bh):0.000}{(bh ? " behind" : "")}");
                Vector3 e = rot[i].eulerAngles;
                series.Add(new { t = Math.Round(t, 4), state = state[i], eye = new[] { Math.Round(p.Eye.x, 4), Math.Round(p.Eye.y, 4), Math.Round(p.Eye.z, 4) },
                    rot = new[] { Math.Round(rot[i].x, 5), Math.Round(rot[i].y, 5), Math.Round(rot[i].z, 5), Math.Round(rot[i].w, 5) }, fov = Math.Round(p.Fov, 3),
                    pov = fc.PovCamera, video = Math.Round(fc.PovVideoOpacity, 3), cover = Math.Round(flow.VideoCover(t), 3), splats = Math.Round(flow.SplatVisibility(t), 3), surfaces = sv,
                    couple = CoupleOnScreen(fd, p, dance, aspect) });
            }
        }

        // the hand-offs: the frame before an arrival (surface alone, full) and the frame of a departure (surface alone, full)
        foreach (FlowPhoneWindow w in flow.Windows)
        {
            // the last frame before the arrival, the first frame at or after the departure
            foreach ((float ft, string what) in new[] { ((Mathf.Ceil(w.Arrival * fps - 1e-4f) - 1f) / fps, "before arrival"), (Mathf.Ceil(w.Departure * fps - 1e-4f) / fps, "departure") })
            {
                if (ft < t0 || ft > t1) continue;
                FilmPose p = fc.Evaluate(ft);
                flow.Surfaces(ft, surfs);
                float dance = fd.Clock.Dance(ft);
                FlowSurface? mine = null;
                foreach (FlowSurface s in surfs)
                {
                    if (s.Camera == w.Phone) mine = s;
                }

                string res;
                if (mine == null) res = "NO SURFACE";
                else
                {
                    float cov = Coverage(fd, mine.Value, dance, p, aspect, out bool behind);
                    bool ok = mine.Value.Alpha > 0.999f && mine.Value.Top > 0.999f && cov > 0.999f;
                    res = $"{(ok ? "ok" : "CHECK")} alpha {mine.Value.Alpha:0.000} cover {mine.Value.Top:0.000} fills {cov:0.000}{(behind ? " (corner behind the eye)" : "")}";
                }

                handoffs.Add($"phone {w.Phone} {what} {F(ft)}: {res}");
            }
        }

        // jumps: an eye step or turn far above its neighbours' (median of +-6 frames), outside the replay cuts
        float[] step = new float[n], turn = new float[n], dfov = new float[n];
        for (int i = 1; i < n; i++)
        {
            step[i] = Vector3.Distance(eye[i], eye[i - 1]);
            turn[i] = RotAngle(rot[i], rot[i - 1]);
            dfov[i] = Mathf.Abs(fov[i] - fov[i - 1]);
        }

        List<string> jumps = new(), stutters = new();
        float maxStep = 0f, maxTurn = 0f;
        string maxStepAt = null, maxTurnAt = null;
        for (int i = 1; i < n; i++)
        {
            if (fd.Clock.CutBetween(time[i - 1], time[i])) continue;
            List<float> ns = new(), nt = new();
            for (int k = Math.Max(1, i - 6); k <= Math.Min(n - 1, i + 6); k++)
            {
                if (k == i) continue;
                ns.Add(step[k]);
                nt.Add(turn[k]);
            }

            ns.Sort();
            nt.Sort();
            float ms = ns.Count > 0 ? ns[ns.Count / 2] : 0f, mt = nt.Count > 0 ? nt[nt.Count / 2] : 0f;
            bool js = step[i] > 0.03f && step[i] > 4f * Mathf.Max(ms, 0.004f);
            bool jt = turn[i] > 1.0f && turn[i] > 4f * Mathf.Max(mt, 0.15f);
            float mf = 0f;
            {
                List<float> nf = new();
                for (int k = Math.Max(1, i - 6); k <= Math.Min(n - 1, i + 6); k++)
                    if (k != i) nf.Add(dfov[k]);
                nf.Sort();
                if (nf.Count > 0) mf = nf[nf.Count / 2];
            }

            bool jf = dfov[i] > 0.5f && dfov[i] > 4f * Mathf.Max(mf, 0.1f);
            // a stutter: this frame's step or turn departs from the mean of its two neighbours by more than a third (a kink in the speed profile:
            // a smootherstep's ease in and out stays far below that)
            if (i >= 2 && i + 1 < n && !fd.Clock.CutBetween(time[i - 2], time[i + 1]) && stutters.Count < 60)
            {
                float sm = 0.5f * (step[i - 1] + step[i + 1]), tm = 0.5f * (turn[i - 1] + turn[i + 1]);
                bool sKink = Mathf.Abs(step[i] - sm) > 0.015f && Mathf.Abs(step[i] - sm) > 0.33f * Mathf.Max(sm, step[i]);
                bool tKink = Mathf.Abs(turn[i] - tm) > 1.0f && Mathf.Abs(turn[i] - tm) > 0.4f * Mathf.Max(tm, turn[i]);
                if (sKink || tKink)
                    stutters.Add($"{F(time[i])} {state[i]}: step {step[i - 1] * 100f:0.0} / {step[i] * 100f:0.0} / {step[i + 1] * 100f:0.0} cm, turn {turn[i - 1]:0.00} / {turn[i]:0.00} / {turn[i + 1]:0.00} deg");
            }

            if ((js || jt || jf) && jumps.Count < 60)
                jumps.Add($"{F(time[i])} {state[i]}: step {step[i] * 100f:0.0} cm (median {ms * 100f:0.0}), turn {turn[i]:0.00} deg (median {mt:0.00}), fov {dfov[i]:0.00}");
            if (step[i] > maxStep)
            {
                maxStep = step[i];
                maxStepAt = $"{F(time[i])} {state[i]}";
            }

            if (turn[i] > maxTurn)
            {
                maxTurn = turn[i];
                maxTurnAt = $"{F(time[i])} {state[i]}";
            }
        }

        // ground band
        List<string> ground = new();
        int groundOut = 0;
        for (int i = 0; i < n; i++)
        {
            if (crane[i]) continue;
            if (eye[i].y < flow.GroundLo - 0.01f || eye[i].y > flow.GroundHi + 0.01f)
            {
                groundOut++;
                if (ground.Count < 40) ground.Add($"{F(time[i])} {state[i]}: eye {eye[i].y:0.00} m");
            }
        }

        // transitions: duration, fastest azimuth turn about the couple
        List<string> ways = new();
        foreach (FlowShot s in flow.Shots)
        {
            if (s.T == null || s.TransEnd < t0 || s.TransStart > t1) continue;
            float best = 0f;
            for (int i = 1; i < n; i++)
            {
                if (time[i] < s.TransStart || time[i] > s.TransEnd) continue;
                Vector3 c = fd.Targets.CoupleCentre(fd.Clock.Dance(time[i]));
                c.y = 1f;
                // the eye's angular speed about the couple (3D: over the top of a crane the azimuth alone is meaningless)
                best = Mathf.Max(best, Vector3.Angle(eye[i - 1] - c, eye[i] - c) * fps);
            }

            ways.Add($"{F(s.TransStart)}-{F(s.TransEnd)} {s.T.Type} -> {s.FirstSegmentId} ({s.T.DurationS:0.00} s, {s.T.Path}{(s.T.Crane ? ", crane" : "")}): fastest turn about the couple {best:0} deg/s");
        }

        // the geometry of every phone way: where its 3D end stands relative to the phone (forward along the full optical axis, sideways, up)
        List<string> geometry = new();
        foreach (FlowShot s in flow.Shots)
        {
            if (s.T == null || s.TransEnd < t0 || s.TransStart > t1 || !(s.T.PhoneIn || s.T.PhoneOut)) continue;
            bool into = s.T.PhoneIn;
            float tq = into ? s.TransStart : s.TransEnd;          // the 3D end's own time
            float tp = into ? s.TransEnd : s.TransStart;          // the phone end's time
            FilmPose p3d = fc.Evaluate(tq), pph = fc.Evaluate(tp);
            string phone = fc.PovCamera;
            float dance = fd.Clock.Dance(tp);
            if (!fd.Sources.TryGetPose(phone ?? (into ? s.Phone : flow.Shots[s.Index - 1].Phone), dance, out Vector3 pos, out Quaternion r, out _, out _)) continue;
            Vector3 d = p3d.Eye - pos;
            geometry.Add($"{F(s.TransStart)} {s.T.Type} {(into ? "from" : "to")} {s.FirstSegmentId}: 3D end {Vector3.Dot(d, r * Vector3.forward):0.00} m forward, " +
                         $"{Vector3.Dot(d, r * Vector3.right):0.00} m right, {Vector3.Dot(d, r * Vector3.up):0.00} m up of the phone ({s.T.DurationS:0.00} s)");
        }

        object v4 = flow.Version >= 2 ? V4Measures(fd, fc, flow, eye, look, rot, fov, time, state, fps, t0, t1, aspect, phoneOwn, pov) : null;
        List<string> holds = flow.Windows.Where(w => w.Departure >= t0 && w.Arrival <= t1)
            .Select(w => $"phone {w.Phone} {F(w.Arrival)}-{F(w.Departure)}: {w.Departure - w.Arrival:0.00} s full frame").ToList();
        List<string> surfaces = readable.Where(kv => kv.Key.D >= t0 && kv.Key.A <= t1)
            .Select(kv => $"{kv.Key.Camera} {kv.Key.Role} {F(kv.Key.A)}-{F(kv.Key.D)}: readable {kv.Value:0.00} s{(kv.Value < 0.6f - 1e-3f ? "  SHORT" : "")}").ToList();

        fd.Seek(t0);
        fd.SetPaused(wasPaused);
        var result = new
        {
            film = new[] { t0, t1 }, frames = n, fps, jumps, jumpCount = jumps.Count, stutters, stutterCount = stutters.Count, maxStep = $"{maxStep * 100f:0.0} cm at {maxStepAt}",
            maxTurn = $"{maxTurn:0.00} deg at {maxTurnAt}", groundOut, ground, phoneFallback, handoffs, holds,
            shortestHold = flow.Windows.Where(w => w.Departure >= t0 && w.Arrival <= t1).Select(w => w.Departure - w.Arrival).DefaultIfEmpty(0f).Min(),
            surfaces, shortestReadable = readable.Where(kv => kv.Key.D >= t0 && kv.Key.A <= t1).Select(kv => kv.Value).DefaultIfEmpty(0f).Min(),
            ways, geometry, offscreenFrames, offscreen, warnings = flow.Warnings, v4
        };
        string json = series != null ? JsonConvert.SerializeObject(new { film = new[] { t0, t1 }, fps, series }, Formatting.Indented)
            : JsonConvert.SerializeObject(result, Formatting.Indented);
        if (!string.IsNullOrEmpty(@out))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(@out))!);
            File.WriteAllText(@out, json);
        }

        return json;
    }

    /// <summary>
    /// v4 (dancecap UNITY_CONTRACT_v4 5-6): the caps of the live camera per way (turn about the couple, view rotation, eye speed on the ground
    /// ways, the crane's pitch), the acceleration jump at both ends of every way (C2: the one-sided second differences over 3 frames), the
    /// orbits' settled rate about the couple (1-s central differences; 2.5-6 deg/s, never static, never turning back), the moves and holds, the
    /// couple's heads out of the frame in the 3D views and - 9:16 - under the physics legend's place (14.4-20.8 % down, 53-99 % across).
    /// v4.2: the heads under the meter only while it shows (flow.meter windows, both aspects), and per orbit the eye's distance to the couple, its
    /// radius, the nearest dancer, the lens and the higher head's screen height (the closer orbits).
    /// </summary>
    static object V4Measures(FilmDirector fd, FilmCamera fc, FlowTimeline flow, Vector3[] eye, Vector3[] look, Quaternion[] rot, float[] fov, float[] time, string[] state,
        float fps, float t0, float t1, float aspect, bool[] phoneOwn, bool[] pov)
    {
        int n = eye.Length;
        float[] az = new float[n], azLook = new float[n], pitch = new float[n];
        bool[] cut = new bool[n];
        bool[] povF = new bool[n];
        int headsOut3d = 0, legendHeads3d = 0, legendHeadsPhone = 0, frames3d = 0, meterFrames = 0;
        List<string> headsOutAt = new(), legendAt = new();
        // v4.2: the eye's distance to the couple (its smoothed centre 1 m up), its horizontal radius, the nearest dancer (pelvis, horizontal), the higher
        // head's screen height (-1 bottom .. 1 top) - per frame, for the orbits' numbers below
        float[] eyeDist = new float[n], eyeRad = new float[n], nearDancer = new float[n], headTop = new float[n];
        FilmTargets tg = fd.Targets;
        for (int i = 0; i < n; i++)
        {
            float dance = fd.Clock.Dance(time[i]);
            // the couple's centre smoothed over +-0.5 s (the live centre sways with every step: the turn about it is what a viewer reads)
            Vector3 c = Vector3.zero;
            int nc = 0;
            for (int k = -5; k <= 5; k++)
            {
                float tk = time[i] + 0.1f * k;
                if (k != 0 && fd.Clock.CutBetween(Mathf.Min(tk, time[i]), Mathf.Max(tk, time[i]))) continue;
                Vector3 q = tg.CoupleCentre(fd.Clock.Dance(Mathf.Clamp(tk, 0f, fd.FilmDuration)));
                if (float.IsNaN(q.x)) continue;
                c += q;
                nc++;
            }

            c = nc > 0 ? c / nc : tg.CoupleCentre(dance);
            az[i] = Mathf.Atan2(eye[i].z - c.z, eye[i].x - c.x);
            // the orbit's own turn: the eye about its look point (the damped couple centre the orbit is built on)
            azLook[i] = float.IsNaN(look[i].x) ? az[i] : Mathf.Atan2(eye[i].z - look[i].z, eye[i].x - look[i].x);
            Vector3 fwd = rot[i] * Vector3.forward;
            pitch[i] = Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
            cut[i] = i > 0 && fd.Clock.CutBetween(time[i - 1], time[i]);
            povF[i] = phoneOwn[i] && pov[i];
            Quaternion inv = Quaternion.Inverse(rot[i]);
            float ty = Mathf.Tan(fov[i] * 0.5f * Mathf.Deg2Rad), tx = ty * aspect;
            bool outF = false, underLegend = false;
            eyeDist[i] = Vector3.Distance(eye[i], c + Vector3.up);
            eyeRad[i] = new Vector2(eye[i].x - c.x, eye[i].z - c.z).magnitude;
            nearDancer[i] = float.PositiveInfinity;
            headTop[i] = float.NaN;
            // v4.2 (flow.meter): the meter shows only inside its windows, in both aspects (9:16 at 1.65x: 52.9-98.7 % across, 14.4-20.8 % down; 16:9 at
            // 1.5x: 71.6-95.0 % across, 5.7-16.0 % down); before v4.2 the 9:16 legend was there whenever the physics colours were
            bool meterOn = flow.HasMeter ? flow.MeterAlpha(time[i]) >= 0.05f : fd.Vertical;
            if (flow.HasMeter && meterOn) meterFrames++;
            foreach (string role in new[] { "lead", "follow" })
            {
                Vector3 pel = tg.Joint(tg.DancerOf(role), SmplJoint.Pelvis, dance);
                if (!float.IsNaN(pel.x)) nearDancer[i] = Mathf.Min(nearDancer[i], new Vector2(eye[i].x - pel.x, eye[i].z - pel.z).magnitude);
                Vector3 h = tg.Joint(tg.DancerOf(role), SmplJoint.Head, dance);
                if (float.IsNaN(h.x)) continue;
                Vector3 v = inv * (h + Vector3.up * 0.12f - eye[i]);
                if (v.z < 0.05f)
                {
                    outF = true;
                    continue;
                }

                float x = v.x / (v.z * tx), y = v.y / (v.z * ty);
                headTop[i] = float.IsNaN(headTop[i]) ? y : Mathf.Max(headTop[i], y);
                if (Mathf.Abs(x) > 1f || Mathf.Abs(y) > 1f) outF = true;
                if (!meterOn) continue;
                if (fd.Vertical && x > 0.058f && x < 0.974f && y > 0.584f && y < 0.712f) underLegend = true;
                if (!fd.Vertical && flow.HasMeter && x > 0.431f && x < 0.9f && y > 0.680f && y < 0.885f) underLegend = true;
            }

            if (!povF[i])
            {
                frames3d++;
                if (outF)
                {
                    headsOut3d++;
                    if (headsOutAt.Count < 40) headsOutAt.Add($"{F(time[i])} {state[i]}");
                }

                if (underLegend)
                {
                    legendHeads3d++;
                    if (legendAt.Count < 40) legendAt.Add($"{F(time[i])} {state[i]}");
                }
            }
            else if (underLegend)
            {
                legendHeadsPhone++;
            }
        }

        float Rate(float[] a, int i, int h)
        {
            int lo = Math.Max(0, i - h), hi = Math.Min(n - 1, i + h);
            for (int k = lo + 1; k <= hi; k++)
                if (cut[k]) return float.NaN;
            if (hi <= lo) return float.NaN;
            return FlowMath.WrapPi(a[hi] - a[lo]) * Mathf.Rad2Deg * fps / (hi - lo);
        }

        // ways
        List<object> ways = new();
        float capAz = 0f, capRot = 0f, capSpeed = 0f, capPitch = 0f;
        foreach (FlowShot s in flow.Shots)
        {
            if (s.T == null || s.TransEnd < t0 || s.TransStart > t1) continue;
            bool crane = s.T.CranePath || s.T.Crane;
            float pkAz = 0f, pkRot = 0f, pkSpeed = 0f, pkPitch = 0f;
            int i0 = -1, i1 = -1;
            for (int i = 1; i < n; i++)
            {
                if (time[i] < s.TransStart || time[i] > s.TransEnd || cut[i]) continue;
                if (i0 < 0) i0 = i;
                i1 = i;
                float a = Rate(az, i, 2);
                if (!float.IsNaN(a) && !crane) pkAz = Mathf.Max(pkAz, Mathf.Abs(a));
                pkRot = Mathf.Max(pkRot, RotAngle(rot[i - 1], rot[i]) * fps);
                if (!crane) pkSpeed = Mathf.Max(pkSpeed, Vector3.Distance(eye[i - 1], eye[i]) * fps);
                if (crane) pkPitch = Mathf.Max(pkPitch, Mathf.Abs(pitch[i] - pitch[i - 1]) * fps);
            }

            // the acceleration jump at the way's ends (one-sided second differences over 3 frames, m/s^2)
            float AccJump(int k)
            {
                if (k < 2 || k + 2 >= n) return float.NaN;
                for (int q = k - 2; q <= k + 2; q++)
                    if (cut[q]) return float.NaN;
                Vector3 before = (eye[k] - 2f * eye[k - 1] + eye[k - 2]) * fps * fps, after = (eye[k + 2] - 2f * eye[k + 1] + eye[k]) * fps * fps;
                return (after - before).magnitude;
            }

            if (!s.T.Type.StartsWith("replay")) capRot = Mathf.Max(capRot, pkRot);
            if (!crane) capAz = Mathf.Max(capAz, pkAz);
            if (!crane) capSpeed = Mathf.Max(capSpeed, pkSpeed);
            if (crane) capPitch = Mathf.Max(capPitch, pkPitch);
            ways.Add(new
            {
                film = $"{F(s.TransStart)}-{F(s.TransEnd)}", type = s.T.Type, into = s.FirstSegmentId, duration = Math.Round(s.T.DurationS, 2), path = s.T.V4Path ?? s.T.Path,
                peakTurnAboutCoupleDegS = Math.Round(pkAz, 2), peakRotationDegS = Math.Round(pkRot, 2), peakEyeSpeedMS = Math.Round(pkSpeed, 3),
                peakPitchDegS = Math.Round(pkPitch, 2),
                accJumpStartMS2 = i0 >= 0 ? Math.Round(AccJump(i0), 3) : double.NaN, accJumpEndMS2 = i1 >= 0 ? Math.Round(AccJump(i1), 3) : double.NaN
            });
        }

        // the orbits' own poses (3D shots between the ways): their settled rate about the couple. v4.3 (UNITY_CONTRACT_v4 L): a frame inside the
        // window of the shot's azimuth track is in band at the track's own rates (RateMin .. RateMax inside its crossing, RateMin .. RateMaxOutside
        // elsewhere, +-0.25: the live centre is damped), counted apart from the 2.5-6 band of the untracked orbits; turning back is the same rule
        List<object> orbits = new();
        int orbitOut = 0, orbitStatic = 0, orbitBack = 0, orbitFrames = 0;
        int trackedFrames = 0, trackedOut = 0, trackedCrossingFrames = 0;
        float trackedLo = float.PositiveInfinity, trackedHi = 0f, trackedHiOutsideCrossing = 0f, trackedEyeMax = float.NegativeInfinity;
        List<string> trackedOutAt = new();
        foreach (FlowShot s in flow.Shots)
        {
            if (s.Mode != "orbit" && s.Mode != "overhead_floorcraft") continue;
            FilmShot fs = fc.Shots[s.Index];
            float drift = fs.P("drift_deg_s", float.NaN);
            FlowTrack trk = s.Track;
            float lo = float.PositiveInfinity, hi = 0f, tLo = float.PositiveInfinity, tHi = 0f, tHiOut = 0f, tEye = float.NegativeInfinity;
            int frames = 0, tFrames = 0, tOut = 0;
            List<float> dists = new(), rads = new(), fovs = new(), tops = new();
            float nearMin = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                string own = $"{s.Mode} {s.FirstSegmentId}";
                if (s.Mode == "orbit" && state[i] == own)
                {
                    dists.Add(eyeDist[i]);
                    rads.Add(eyeRad[i]);
                    fovs.Add(fov[i]);
                    if (!float.IsNaN(headTop[i])) tops.Add(headTop[i]);
                    nearMin = Mathf.Min(nearMin, nearDancer[i]);
                }
                if (fc.Shots.Count <= s.Index || state[i] != own) continue;
                bool pure = true;
                for (int k = Math.Max(0, i - 15); k <= Math.Min(n - 1, i + 15) && pure; k++) pure = state[k] == own;
                if (!pure) continue;
                if (s.Mode == "overhead_floorcraft") continue; // the overhead turns its Up (eye straight above): not an azimuth about the couple
                float r = Rate(azLook, i, 15);
                if (float.IsNaN(r)) continue;
                frames++;
                orbitFrames++;
                float ar = Mathf.Abs(r);
                lo = Mathf.Min(lo, ar);
                hi = Mathf.Max(hi, ar);
                if (trk != null && trk.Inside(time[i]))
                {
                    // v4.3: the tracked frame's own band (the rate its way: a turning-back frame is out of band too)
                    bool cross = trk.InCrossing(time[i]);
                    float sr = !float.IsNaN(drift) && Mathf.Abs(drift) > 0.1f ? r * Mathf.Sign(drift) : ar;
                    tFrames++;
                    trackedFrames++;
                    if (cross) trackedCrossingFrames++;
                    tLo = Mathf.Min(tLo, sr);
                    tHi = Mathf.Max(tHi, sr);
                    if (!cross) tHiOut = Mathf.Max(tHiOut, sr);
                    tEye = Mathf.Max(tEye, eye[i].y);
                    if (sr < trk.RateMin - 0.25f || sr > trk.RateMaxAt(time[i]) + 0.25f)
                    {
                        tOut++;
                        trackedOut++;
                        if (trackedOutAt.Count < 40) trackedOutAt.Add($"{F(time[i])} {state[i]}: {sr:0.00} deg/s its way (band {trk.RateMin:0.##}-{trk.RateMaxAt(time[i]):0.##} +-0.25)");
                    }
                }
                else if (ar < 2.5f - 0.25f || ar > 6f + 0.25f) orbitOut++;
                if (ar < 0.2f) orbitStatic++;
                if (!float.IsNaN(drift) && Mathf.Abs(drift) > 0.1f && Mathf.Sign(r) != Mathf.Sign(drift) && ar > 1f) orbitBack++;
            }

            trackedLo = Mathf.Min(trackedLo, tLo);
            trackedHi = Mathf.Max(trackedHi, tHi);
            trackedHiOutsideCrossing = Mathf.Max(trackedHiOutsideCrossing, tHiOut);
            trackedEyeMax = Mathf.Max(trackedEyeMax, tEye);
            if (frames > 0)
                orbits.Add(new
                {
                    shot = s.FirstSegmentId, film = $"{F(s.F0)}-{F(s.F1)}", planDriftDegS = drift, frames, rateMin = Math.Round(lo, 2), rateMax = Math.Round(hi, 2),
                    planRadiusM = Math.Round(fs.P(fd.Vertical ? "radius_vertical_m" : "radius_m", float.NaN), 3), eyeDistanceMedianM = Math.Round(Median(dists), 3),
                    eyeDistanceMinM = dists.Count > 0 ? Math.Round(dists.Min(), 3) : double.NaN, radiusMedianM = Math.Round(Median(rads), 3),
                    nearestDancerMinM = Math.Round(nearMin, 3), fovMedianDeg = Math.Round(Median(fovs), 2), headTopMedian = Math.Round(Median(tops), 3),
                    track = trk == null ? null : new
                    {
                        id = trk.Id, window = $"{F(trk.A)}-{F(trk.B)}", crossing = float.IsNaN(trk.CrossA) ? null : $"{F(trk.CrossA)}-{F(trk.CrossB)}",
                        band = $"{trk.RateMin:0.##}-{trk.RateMax:0.##} deg/s in the crossing, {trk.RateMin:0.##}-{trk.RateMaxAt(float.NaN):0.##} elsewhere (+-0.25)",
                        frames = tFrames, outsideBand = tOut, rateMinItsWay = tFrames > 0 ? Math.Round(tLo, 2) : double.NaN,
                        rateMaxItsWay = tFrames > 0 ? Math.Round(tHi, 2) : double.NaN, rateMaxOutsideCrossing = tFrames > 0 ? Math.Round(tHiOut, 2) : double.NaN,
                        eyeMaxM = tFrames > 0 ? Math.Round(tEye, 3) : double.NaN
                    }
                });
        }

        // every orbit frame together (v4.2: 0.8 x v4.1's distance)
        List<float> allDist = new(), allRad = new(), allFov = new(), allTop = new();
        float allNear = float.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            if (!state[i].StartsWith("orbit ")) continue;
            allDist.Add(eyeDist[i]);
            allRad.Add(eyeRad[i]);
            allFov.Add(fov[i]);
            if (!float.IsNaN(headTop[i])) allTop.Add(headTop[i]);
            allNear = Mathf.Min(allNear, nearDancer[i]);
        }

        // the closest the eye comes to a dancer in any ground-level 3D frame (orbits and their ways, not the cranes / overheads / phone video)
        float groundNear = float.PositiveInfinity, groundRad = float.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            if (povF[i] || state[i].StartsWith("overhead_floorcraft") || state[i].StartsWith("crane")) continue;
            if (eye[i].y > 2.6f) continue;
            groundNear = Mathf.Min(groundNear, nearDancer[i]);
            groundRad = Mathf.Min(groundRad, eyeRad[i]);
        }

        List<float> holdLens = flow.Windows.Select(w => w.Departure - w.Arrival).ToList();
        return new
        {
            moves = flow.Shots.Count(s => s.T != null),
            moveTypes = flow.Shots.Where(s => s.T != null).GroupBy(s => s.T.Type).ToDictionary(g => g.Key, g => g.Count()),
            phoneHolds = holdLens.Select(x => Math.Round(x, 2)).ToList(),
            holdMean = holdLens.Count > 0 ? Math.Round(holdLens.Average(), 2) : 0, holdMin = holdLens.Count > 0 ? Math.Round(holdLens.Min(), 2) : 0,
            caps = new
            {
                turnAboutCoupleDegS = Math.Round(capAz, 2), rotationDegS = Math.Round(capRot, 2), eyeSpeedMS = Math.Round(capSpeed, 3), cranePitchDegS = Math.Round(capPitch, 2),
                limits = "turn 12, rotation 12, speed 1.2 (ground ways), crane pitch 20"
            },
            ways, orbits, orbitFrames, orbitFramesOutside2p5to6 = orbitOut, orbitStaticFrames = orbitStatic, orbitTurningBackFrames = orbitBack,
            orbitTrackedFrames = trackedFrames, orbitTrackedFramesOutsideBand = trackedOut, orbitTrackedCrossingFrames = trackedCrossingFrames,
            orbitTrackedRateMinItsWay = trackedFrames > 0 ? Math.Round(trackedLo, 2) : double.NaN, orbitTrackedRateMaxItsWay = trackedFrames > 0 ? Math.Round(trackedHi, 2) : double.NaN,
            orbitTrackedRateMaxOutsideCrossing = trackedFrames > 0 ? Math.Round(trackedHiOutsideCrossing, 2) : double.NaN,
            orbitTrackedEyeMaxM = trackedFrames > 0 ? Math.Round(trackedEyeMax, 3) : double.NaN, orbitTrackedOutAt = trackedOutAt,
            orbitAll = new
            {
                frames = allDist.Count, eyeDistanceMedianM = Math.Round(Median(allDist), 3), eyeDistanceMinM = allDist.Count > 0 ? Math.Round(allDist.Min(), 3) : double.NaN,
                radiusMedianM = Math.Round(Median(allRad), 3), radiusMinM = allRad.Count > 0 ? Math.Round(allRad.Min(), 3) : double.NaN, nearestDancerMinM = Math.Round(allNear, 3),
                fovMedianDeg = Math.Round(Median(allFov), 2), headTopMedian = Math.Round(Median(allTop), 3)
            },
            groundLevel3d = new { nearestDancerMinM = Math.Round(groundNear, 3), radiusMinM = Math.Round(groundRad, 3) },
            frames3d, headsOutOfFrame3d = headsOut3d, headsOutAt, legendOverHeads3d = legendHeads3d, legendOverHeadsPhone = legendHeadsPhone, legendAt,
            meter = new { windows = flow.MeterWindows.Count, framesShown = meterFrames, hasMeter = flow.HasMeter }
        };
    }

    static float Median(List<float> xs)
    {
        if (xs.Count == 0) return float.NaN;
        List<float> s = xs.Where(x => !float.IsNaN(x) && !float.IsInfinity(x)).OrderBy(x => x).ToList();
        return s.Count == 0 ? float.NaN : s[s.Count / 2];
    }

    /// <summary>the share of the frame a frustum surface covers seen from a pose (a 24 x 40 grid of the frame inside its projected quad);
    /// behind = a corner is behind the eye (the quad then fills the view: counted as 1)</summary>
    static float Coverage(FilmDirector fd, FlowSurface s, float dance, FilmPose p, float aspect, out bool behind)
    {
        behind = false;
        float depth = fd.FilmCam.SurfaceDepth(s.Frustum);
        if (!fd.Sources.TryGetSurfaceCorners(s.Camera, dance, depth, out _, out Vector3 tl, out Vector3 tr, out Vector3 br, out Vector3 bl)) return 0f;
        Quaternion inv = Quaternion.Inverse(p.Rotation());
        float ty = Mathf.Tan(p.Fov * 0.5f * Mathf.Deg2Rad), tx = ty * aspect;
        List<Vector2> poly = new();
        foreach (Vector3 c in new[] { tl, tr, br, bl })
        {
            Vector3 v = inv * (c - p.Eye);
            if (v.z < 0.01f)
            {
                behind = true;
                return 1f;
            }

            poly.Add(new Vector2(v.x / (v.z * tx), v.y / (v.z * ty)));
        }

        int inside = 0, total = 0;
        for (int iy = 0; iy < 40; iy++)
        {
            for (int ix = 0; ix < 24; ix++)
            {
                Vector2 q = new(-1f + (ix + 0.5f) * 2f / 24f, -1f + (iy + 0.5f) * 2f / 40f);
                total++;
                if (FaceSidecarMath.Inside(q, poly)) inside++;
            }
        }

        // the frame's own corners decide "fills it" exactly
        if (inside == total && new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) }.All(x => FaceSidecarMath.Inside(x, poly))) return 1f;
        return Mathf.Min(inside / (float)total, 0.9999f);
    }

    static string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>the angle (deg) between two rotations, exact for small angles (Quaternion.Angle returns 0 below about 0.16 deg: a slow orbit's
    /// 0.1 deg per frame read as no turn at all)</summary>
    static float RotAngle(Quaternion a, Quaternion b)
    {
        Quaternion r = Quaternion.Inverse(a) * b;
        float v = Mathf.Sqrt(r.x * r.x + r.y * r.y + r.z * r.z);
        return 2f * Mathf.Atan2(v, Mathf.Abs(r.w)) * Mathf.Rad2Deg;
    }

    /// <summary>the couple's centre (1 m up) and the two heads in normalised screen coordinates (-1..1, y up; null behind the eye)</summary>
    static object CoupleOnScreen(FilmDirector fd, FilmPose p, float dance, float aspect)
    {
        Quaternion inv = Quaternion.Inverse(p.Rotation());
        float ty = Mathf.Tan(p.Fov * 0.5f * Mathf.Deg2Rad), tx = ty * aspect;
        float[] S(Vector3 w)
        {
            Vector3 v = inv * (w - p.Eye);
            return v.z < 0.05f ? null : new[] { (float)Math.Round(v.x / (v.z * tx), 3), (float)Math.Round(v.y / (v.z * ty), 3) };
        }

        FilmTargets t = fd.Targets;
        return new
        {
            centre = S(t.CoupleCentre(dance) + Vector3.up), lead = S(t.Joint(t.DancerOf("lead"), SmplJoint.Head, dance)),
            follow = S(t.Joint(t.DancerOf("follow"), SmplJoint.Head, dance))
        };
    }
}
