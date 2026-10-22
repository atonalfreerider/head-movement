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

        Vector3[] eye = new Vector3[n];
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
            turn[i] = Quaternion.Angle(rot[i], rot[i - 1]);
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
            ways, geometry, offscreenFrames, offscreen, warnings = flow.Warnings
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
