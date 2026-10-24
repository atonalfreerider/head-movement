using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using VRTKLite.Controllers;

/// <summary>a camera pose: eye, look point, up, vertical FOV and where the look point sits on screen (0 = bottom, 1 =
/// top); Pov poses carry an exact rotation instead (a phone's view: the video must map 1:1)</summary>
public struct FilmPose
{
    public Vector3 Eye, Look, Up;
    public float Fov, ScreenY;
    public bool Pov;
    public Quaternion Rot;
    public Vector2 LensShift;

    public Quaternion Rotation()
    {
        if (Pov) return Rot;
        Vector3 f = Look - Eye;
        if (f.sqrMagnitude < 1e-8f) f = Vector3.forward;
        Vector3 up = Up.sqrMagnitude > 1e-6f ? Up : Vector3.up;
        if (Mathf.Abs(Vector3.Dot(f.normalized, up.normalized)) > 0.9995f) up = Vector3.forward;
        Quaternion q = Quaternion.LookRotation(f, up);
        // composition: put the look point at ScreenY by pitching about the camera's right axis
        float d = Mathf.Atan((2f * ScreenY - 1f) * Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad)) * Mathf.Rad2Deg;
        return q * Quaternion.Euler(d, 0f, 0f);
    }

    public static FilmPose FromRotation(Vector3 eye, Quaternion rot, float fov, float lookDistance = 3f) => new()
    {
        Eye = eye, Rot = rot, Pov = true, Fov = fov, ScreenY = 0.5f, Look = eye + rot * Vector3.forward * lookDistance,
        Up = rot * Vector3.up
    };
}

/// <summary>a run of consecutive segments with the same camera block</summary>
public class FilmShot
{
    public int Index;
    public string Mode;
    public float F0, F1;
    public JObject Camera;
    public readonly List<FilmSegment> Segments = new();
    public FilmShot Prev, Next;
    public bool CutIn;      // the dance time jumps at its start (replay): no blend from the previous shot
    public float BlendIn;   // seconds of blend from the previous shot (0 = cut)
    public int RunStart;    // index of the first shot of this mode run (same mode, no cut)

    /// <summary>true while the 16:9 output renders. Opt-in per-aspect overrides: a camera block may carry `horizontal` = {key: value}; while Horizontal
    /// those keys replace the block's own (P / S / B read them). The 9:16 render never looks at the block, so a direction without it, or any 9:16
    /// render of one with it, is exactly what it always was.</summary>
    public bool Horizontal;

    /// <summary>v3 flow (direction top-level "flow"): this shot in FilmDirection.Flow, with its camera.transition_in; null for older films</summary>
    public HmFilm.FlowShot Flow;

    JToken Tok(string key) => Horizontal && Camera["horizontal"] is JObject h && h[key] != null ? h[key] : Camera[key];
    public float P(string key, float fallback) => FilmDirection.F(Tok(key), fallback);
    public string S(string key, string fallback) => Tok(key)?.Type == JTokenType.String ? Tok(key).Value<string>() : fallback;
    public bool B(string key) => Tok(key)?.Type == JTokenType.Boolean && Tok(key).Value<bool>();
}

/// <summary>
/// The film's camera director (VIEWER_SPEC 5, 6, 8.3): every shot of the direction is a pure function of film time
/// (orbits, damped follows and blends are evaluated from the film clock, not integrated), so a seek or a frame-locked
/// recording shows exactly the same pictures. Shots blend from the previous shot's LIVE pose (cylindrically, the turn
/// direction fixed at the blend start) unless the dance time jumps (a replay: a cut). Framing per aspect: 9:16 keeps
/// the key action between the top 14 % and the caption block (the look point sits at ~59 % of the height), 16:9 just
/// above centre. The desktop rig (CameraControl) is parked while the film runs; this writes its transform and FOV.
/// </summary>
public class FilmCamera
{
    readonly FilmDirection direction;
    readonly FilmClock clock;
    readonly FilmTargets targets;
    readonly FilmCameraSource sources;
    public readonly List<FilmShot> Shots = new();
    bool vertical;
    public bool Vertical
    {
        get => vertical;
        set
        {
            if (vertical == value) return;
            vertical = value;
            foreach (FilmShot sh in Shots) sh.Horizontal = !value;
            entryAzimuth.Clear();
            planOf.Clear();
            if (flow != null) foreach (HmFilm.FlowFrustum fr in flow.Frustums) fr.DepthUsed = float.NaN;
        }
    }

    /// <summary>16:9 only, opt-in (FilmAspect.PortraitZoom, set by the director each frame): a crop's zoom is multiplied by this for a portrait phone clip
    /// (its panel is narrower than the frame, so the 9:16 plan's zoom leaves the teachers small); 1 = the plan's zoom</summary>
    public float PortraitZoom = 1f;
    public const float PortraitZoomMax = 2.1f;

    public float BaseFov = 40f;

    // fly_to_camera outputs for the director (video layer)
    public string PovCamera { get; private set; }
    public float PovVideoOpacity { get; private set; }
    public string PovComposite { get; private set; } = "3d_over_video";
    public FilmPose Last { get; private set; }
    public FilmShot Current { get; private set; }

    public FilmCamera(FilmDirection d, FilmClock c, FilmTargets t, FilmCameraSource s)
    {
        direction = d;
        clock = c;
        targets = t;
        sources = s;
        BuildShots();
        AttachFlow();
    }

    /// <summary>the v3 flow of the direction (null = an older film: blends and cuts as before)</summary>
    public HmFilm.FlowTimeline Flow => flow;

    HmFilm.FlowTimeline flow;
    public string FlowError { get; private set; }

    void AttachFlow()
    {
        HmFilm.FlowTimeline tl = direction.Flow;
        if (tl == null) return;
        if (tl.Shots.Count != Shots.Count)
        {
            FlowError = $"flow: {tl.Shots.Count} flow shots != {Shots.Count} camera shots; the transitions are ignored";
            return;
        }

        for (int i = 0; i < Shots.Count; i++)
        {
            if (Mathf.Abs(tl.Shots[i].F0 - Shots[i].F0) > 1e-3f)
            {
                FlowError = $"flow: shot {i} starts at {tl.Shots[i].F0} in the flow and {Shots[i].F0} on the camera; the transitions are ignored";
                return;
            }
        }

        for (int i = 0; i < Shots.Count; i++) Shots[i].Flow = tl.Shots[i];
        flow = tl;
    }

    void BuildShots()
    {
        FilmShot cur = null;
        foreach (FilmSegment seg in direction.Segments)
        {
            bool cut = seg.Index > 0 && Mathf.Abs(direction.Segments[seg.Index - 1].D1 - seg.D0) > FilmClock.CutJump;
            if (cur != null && !cut && JToken.DeepEquals(cur.Camera, seg.Camera))
            {
                cur.Segments.Add(seg);
                cur.F1 = seg.F1;
                continue;
            }

            FilmShot s = new()
            {
                Index = Shots.Count, Mode = seg.Mode, F0 = seg.F0, F1 = seg.F1, Camera = seg.Camera, Prev = cur, CutIn = cut || cur == null, Horizontal = !vertical
            };
            s.Segments.Add(seg);
            float defBlend = s.Mode == "fly_to_camera" ? 0f : s.Mode == "end_card" ? 0f : 1.2f;
            s.BlendIn = s.CutIn ? 0f : s.P("blend_s", defBlend);
            if (cur != null) cur.Next = s;
            s.RunStart = cur != null && !s.CutIn && cur.Mode == s.Mode ? cur.RunStart : s.Index;
            Shots.Add(s);
            cur = s;
        }
    }

    public FilmShot ShotAt(float film)
    {
        for (int i = Shots.Count - 1; i >= 0; i--)
        {
            if (Shots[i].F0 <= film + 1e-5f) return Shots[i];
        }

        return Shots.Count > 0 ? Shots[0] : null;
    }

    /// <summary>the pose on screen at film time f (shot + blend from the previous shot)</summary>
    public FilmPose Evaluate(float film)
    {
        FilmShot s = ShotAt(film);
        Current = s;
        PovCamera = null;
        PovVideoOpacity = 0f;
        TransitionShot = CameraShot = null;
        TransitionU = 0f;
        if (s == null) return Default(film);
        if (flow != null)
        {
            Last = EvaluateFlow(film);
            return Last;
        }

        FilmPose p = Pose(s, film, 0);
        if (s.Prev != null && s.BlendIn > 0f && film - s.F0 < s.BlendIn && s.Mode != "fly_to_camera")
        {
            string keepCam = PovCamera;
            float keepOpacity = PovVideoOpacity;
            FilmPose a = Pose(s.Prev, film, 1);
            PovCamera = keepCam;
            PovVideoOpacity = keepOpacity;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((film - s.F0) / s.BlendIn));
            FilmPose a0 = Pose(s.Prev, s.F0, 1), b0 = Pose(s, s.F0, 1);
            p = Blend(a, p, k, TurnAtStart(a0, b0));
        }

        if (Vertical && !p.Pov)
        {
            // the framing leads the title's fade in and lags its fade out by 0.4 s: the heads are never under the text
            float room = Mathf.Max(direction.TitleAlphaAt(film + 0.4f), direction.TitleAlphaAt(film - 0.4f));
            if (room > 0.001f) p = TitleRoom(p, room);
        }

        Last = p;
        return p;
    }

    /// <summary>9:16 while the reaction title is up: the title block fills the frame from the 14 % safe line down to about
    /// 29 %, so the couple settles into the band below it (heads under the text, feet above the captions): the lens
    /// widens (or the eye backs off when the lens is at its limit) so the couple fits that band, and the look point drops.
    /// k = the title's opacity (the framing follows the fade in and out)</summary>
    static FilmPose TitleRoom(FilmPose p, float k)
    {
        // the couple (floor to ~1.95 m: his head, a raised hand) between the title block's bottom (about 29 % from the top,
        // plus a margin) and the caption block's top (about 31 % from the bottom): 0.37 .. 0.69 of the frame height
        // (tuned on the 04 joints by film/review/director/framing_sim.py: the heads stay 4.7 % of the height under the text)
        FilmPose fit = p;
        FitBand(ref fit, new Vector3(p.Look.x, 0f, p.Look.z), 1.95f, 0.37f, 0.69f, 24f, 66f);
        p.Eye = Vector3.Lerp(p.Eye, fit.Eye, k);
        p.Fov = Mathf.Lerp(p.Fov, fit.Fov, k);
        p.ScreenY = Mathf.Lerp(p.ScreenY, fit.ScreenY, k);
        return p;
    }

    /// <summary>fit the couple's vertical extent (the floor under the couple up to yHigh metres) into the band
    /// [bandLo, bandHi] of the frame height (0 = bottom) with the pose's own perspective projection: the lens widens to
    /// the band's height (or the eye backs off when the lens is at maxFov; the lens never narrows past minFov) and the
    /// look point's screen height is shifted so the span is centred in the band. A few fixed-point iterations; pure
    /// geometry, so it is a function of the pose only (seeks and recordings agree)</summary>
    static void FitBand(ref FilmPose p, Vector3 couplePos, float yHigh, float bandLo, float bandHi, float minFov, float maxFov)
    {
        float target = Mathf.Max(0.05f, bandHi - bandLo), mid = 0.5f * (bandLo + bandHi);
        Vector3 head = couplePos + Vector3.up * yHigh;
        for (int it = 0; it < 5; it++)
        {
            if (!ProjectSpan(p, couplePos, head, out float lo, out float hi)) return;
            float span = hi - lo;
            if (span < 1e-4f) return;
            float tanHalf = Mathf.Tan(p.Fov * 0.5f * Mathf.Deg2Rad);
            float tanNeed = tanHalf * span / target;
            float fov = 2f * Mathf.Atan(tanNeed) * Mathf.Rad2Deg;
            if (fov > maxFov)
            {
                // the lens is at its widest: the eye backs off along its line to the look point
                float ratio = tanNeed / Mathf.Tan(maxFov * 0.5f * Mathf.Deg2Rad);
                p.Eye = p.Look + (p.Eye - p.Look) * Mathf.Min(ratio, 3f);
                fov = maxFov;
            }

            p.Fov = Mathf.Max(minFov, fov);
            if (!ProjectSpan(p, couplePos, head, out lo, out hi)) return;
            p.ScreenY = Mathf.Clamp(p.ScreenY + (mid - 0.5f * (lo + hi)), 0.1f, 0.9f);
        }
    }

    /// <summary>screen heights (0 = bottom .. 1 = top) of two world points through the pose's camera, including its look-point
    /// composition (ScreenY pitches the camera about its right axis: FilmPose.Rotation); false when one is behind the lens</summary>
    static bool ProjectSpan(FilmPose p, Vector3 a, Vector3 b, out float sa, out float sb)
    {
        sa = sb = 0f;
        Vector3 f = (p.Look - p.Eye).normalized;
        if (f.sqrMagnitude < 0.5f) return false;
        Vector3 right = Vector3.Cross(Vector3.up, f);
        if (right.sqrMagnitude < 1e-6f) return false;
        right.Normalize();
        Vector3 u = Vector3.Cross(f, right);
        float tanHalf = Mathf.Tan(p.Fov * 0.5f * Mathf.Deg2Rad);
        float pitch = Mathf.Atan((2f * p.ScreenY - 1f) * tanHalf);
        Vector3 f2 = f * Mathf.Cos(pitch) - u * Mathf.Sin(pitch), u2 = u * Mathf.Cos(pitch) + f * Mathf.Sin(pitch);
        Vector3 va = a - p.Eye, vb = b - p.Eye;
        float za = Vector3.Dot(va, f2), zb = Vector3.Dot(vb, f2);
        if (za < 0.1f || zb < 0.1f) return false;
        sa = 0.5f * (Vector3.Dot(va, u2) / (za * tanHalf) + 1f);
        sb = 0.5f * (Vector3.Dot(vb, u2) / (zb * tanHalf) + 1f);
        return true;
    }

    // ------------------------------------------------------------------ shots

    /// <summary>the pose of a shot at a film time. A NESTED evaluation (the blend from the previous shot, SideAzimuth asking where the previous
    /// shot's eye was) must not leak the phone-POV outputs: before this wrapper a 3D shot that followed a phone shot inherited its PovCamera and
    /// video opacity (the "phone 05 - its point of view" pill over a 3D shot, and the phone's video behind it while the clip had frames)</summary>
    FilmPose Pose(FilmShot s, float film, int depth)
    {
        if (depth == 0) return PoseCore(s, film, depth);
        string cam = PovCamera, comp = PovComposite;
        float op = PovVideoOpacity;
        FilmPose p = PoseCore(s, film, depth);
        PovCamera = cam;
        PovVideoOpacity = op;
        PovComposite = comp;
        return p;
    }

    FilmPose PoseCore(FilmShot s, float film, int depth)
    {
        FilmPose p = PoseMode(s, film, depth);
        // v3 `ground_level`: an eye above the ground band is lowered to its top (the look point kept), never below its bottom
        if (flow != null && !p.Pov && s.B("ground_level"))
        {
            float y = Mathf.Clamp(p.Eye.y, flow.GroundLo, flow.GroundHi);
            if (!Mathf.Approximately(y, p.Eye.y)) p.Eye.y = y;
        }

        return p;
    }

    FilmPose PoseMode(FilmShot s, float film, int depth)
    {
        if (depth > 3) return Default(film);
        float dance = clock.Dance(Mathf.Clamp(film, 0f, direction.FilmDuration));
        switch (s.Mode)
        {
            case "orbit":
                return Orbit(s, film, dance, s.P("radius_m", 3.1f), s.P("height_m", 1.6f), s.P("look_height_m", 0.95f), s.P("period_s", 20f));
            case "geometry_orbit":
                return Orbit(s, film, dance, s.P("radius_m", 2.9f), s.P("height_m", 1.2f), 0.85f, s.P("period_s", 40f));
            case "rhythm":
                return Rhythm(s, film);
            case "physics_3q":
                return Physics3Q(s, film, dance);
            case "cameras_overview":
                return CamerasOverview(s, film, dance);
            case "fly_to_camera":
                return FlyToCamera(s, film, dance, depth);
            case "camera_tour":
                return CameraTour(s, film, dance, depth);
            case "feet_closeup":
                return FeetCloseup(s, film, dance);
            case "feature_zoom":
                return FeatureZoom(s, film, dance);
            case "overhead_floorcraft":
                return Overhead(s, film, dance);
            case "graph_wide":
                return GraphWide(s, film, depth);
            case "end_card":
            {
                FilmPose start = s.Prev != null ? Pose(s.Prev, s.F0, depth + 1) : Default(s.F0);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((film - s.F0) / Mathf.Max(0.1f, s.F1 - s.F0)));
                // 16:9: a slow push-in. 9:16 holds the framing: TitleRoom (Evaluate) settles the couple below the
                // three-row title card while it is up
                if (!Vertical) start.Eye = Vector3.LerpUnclamped(start.Eye, start.Look, 0.18f * k);
                return start;
            }
            default:
                return Orbit(s, film, dance, 3.1f, 1.6f, 0.95f, 20f);
        }
    }

    FilmPose Default(float film)
    {
        float dance = clock.Dance(film);
        Vector3 c = targets.CoupleCentre(dance);
        return new FilmPose { Eye = c + new Vector3(0, 1.6f, -3.2f), Look = c + Vector3.up * 0.95f, Up = Vector3.up, Fov = 45f, ScreenY = ScreenYFor() };
    }

    float ScreenYFor() => Vertical ? 0.6f : 0.52f;

    /// <summary>vertical FOV that keeps the couple (2.3 m with floor margin) inside the key-action band</summary>
    float CoupleFov(float eyeDistance)
    {
        float band = Vertical ? 0.48f : 0.72f;
        float ang = 2f * Mathf.Atan(1.15f / Mathf.Max(0.5f, eyeDistance)) * Mathf.Rad2Deg;
        return Mathf.Clamp(ang / band, 24f, Vertical ? 66f : 55f);
    }

    /// <summary>exponentially damped XZ follow of the couple centre over the film clock (deterministic: a weighted
    /// average of past samples back to the start of the shot's mode run, never across a cut)</summary>
    Vector3 Follow(FilmShot s, float film, float tau, Func<float, Vector3> target)
    {
        Vector3 r = Follow3(s, film, tau, target);
        r.y = 0f;
        return r;
    }

    /// <summary>Follow, keeping the height (a feature zoom frames a joint, not the floor under it)</summary>
    Vector3 Follow3(FilmShot s, float film, float tau, Func<float, Vector3> target)
    {
        // v3 flow: the average reaches back over shot boundaries (the camera flies between them) and only stops at a dance-time cut (a
        // replay), and a sample comes in with a weight ramp instead of a step - the follow is continuous in film time (a step of a few cm
        // at the run start was hidden by the old cuts and blends; a flight shows it)
        float runStart = flow != null ? LastCutAtOrBefore(film) : Shots[s.RunStart].F0;
        const int n = 10;
        float dt = tau / 3f;
        Vector3 sum = Vector3.zero;
        float wsum = 0f;
        for (int i = 0; i < n; i++)
        {
            float f = film - i * dt;
            if (i > 0 && f < runStart) break;
            Vector3 p = target(clock.Dance(Mathf.Clamp(f, 0f, direction.FilmDuration)));
            if (float.IsNaN(p.x)) continue;
            float w = Mathf.Exp(-i * dt / tau);
            if (flow != null && i > 0) w *= Mathf.Clamp01((f - runStart) / dt);
            sum += p * w;
            wsum += w;
        }

        if (wsum <= 0f) return targets.CoupleCentre(clock.Dance(film));
        return sum / wsum;
    }

    Vector3 CoupleFollow(FilmShot s, float film, float tau = 0.6f) => Follow(s, film, tau, targets.CoupleCentre);

    /// <summary>accumulated orbit angle (rad) of a mode run up to film time f: each shot of the run turns at its own rate</summary>
    float RunAngle(FilmShot s, float film, Func<FilmShot, float> rate)
    {
        float a = 0f;
        for (int i = s.RunStart; i <= s.Index; i++)
        {
            FilmShot r = Shots[i];
            float end = i == s.Index ? film : r.F1;
            a += rate(r) * Mathf.Max(0f, end - r.F0);
        }

        return a;
    }

    /// <summary>side-on azimuth (rad): perpendicular to the lead -> follow line at the run's start, on the side nearest
    /// the previous shot's eye (or +90 deg)</summary>
    float SideAzimuth(FilmShot s, int depth)
    {
        FilmShot first = Shots[s.RunStart];
        float d0 = clock.Dance(first.F0);
        Vector3 l = targets.Joint(targets.DancerOf("follow"), SmplJoint.Pelvis, d0) - targets.Joint(targets.DancerOf("lead"), SmplJoint.Pelvis, d0);
        float line = float.IsNaN(l.x) || new Vector2(l.x, l.z).sqrMagnitude < 1e-6f ? 0f : Mathf.Atan2(l.z, l.x);
        float a = line + Mathf.PI * 0.5f, b = line - Mathf.PI * 0.5f;
        if (first.Prev == null || depth > 1) return a;
        FilmPose prev = Pose(first.Prev, first.F0, depth + 1);
        Vector3 c = targets.CoupleCentre(d0);
        float pa = Mathf.Atan2(prev.Eye.z - c.z, prev.Eye.x - c.x);
        return Mathf.Abs(Mathf.DeltaAngle(a * Mathf.Rad2Deg, pa * Mathf.Rad2Deg)) <= Mathf.Abs(Mathf.DeltaAngle(b * Mathf.Rad2Deg, pa * Mathf.Rad2Deg)) ? a : b;
    }

    /// <summary>v4.2: the factor on a closer slow orbit's eye offset at film (1 = the block's, 0.8 x v4.1's; 1 / scale = v4.1's): camera.v4.closer.scale
    /// with the weight w = Ramp(closer_in_s) x (1 - Ramp(closer_out_s)) (smootherstep; a missing window counts as 1); 1 for any other block</summary>
    static float CloserFactor(FilmShot s, float film)
    {
        float k = FilmDirection.F(s.Camera["v4"]?["closer"]?["scale"], 1f);
        Vector2? win = s.Camera["closer_in_s"] is JArray a && a.Count == 2 ? new Vector2(a[0].Value<float>(), a[1].Value<float>()) : null;
        Vector2? wout = s.Camera["closer_out_s"] is JArray b && b.Count == 2 ? new Vector2(b[0].Value<float>(), b[1].Value<float>()) : null;
        return HmFilm.FlowMath.CloserFactor(k, film, win, wout);
    }

    FilmPose Orbit(FilmShot s, float film, float dance, float radius, float height, float lookH, float period)
    {
        Vector3 c = CoupleFollow(s, film);
        // 9:16 stands further back (the narrow frame) unless the shot names its own radius_vertical_m (the heat films' closer portrait framing)
        if (Vertical) radius = s.Camera["radius_vertical_m"] != null ? s.P("radius_vertical_m", radius) : radius * 1.25f;
        // v4.2 (dancecap UNITY_CONTRACT_v4 I): the closer slow orbit - the block's offset is 0.8 x v4.1's; next to a phone way it eases (closer_in_s /
        // closer_out_s, smootherstep) back to v4.1's distance at the way, so the way's live end state - and the way - are v4.1's. A pure function of film
        // time: the eye's offset about the look point scales (radius, the height above the look point, the inline lift's cap), never the look point
        float closer = CloserFactor(s, film);
        radius *= closer;
        height = lookH + (height - lookH) * closer;
        // opt-in camera keys of the class recap's talking shots (a direction without them gets exactly the orbit it always got):
        // focus = lead | follow with focus_weight 0..1 - the orbit centre leans toward that dancer (a talk shot follows the speaker);
        // fit_span = true - the lens widens (then the eye backs off) until both dancers stay inside the frame however far apart they stand
        string focus = s.S("focus", "couple");
        float fw = Mathf.Clamp01(s.P("focus_weight", 0f));
        if (fw > 0.001f && (focus == "lead" || focus == "follow"))
        {
            Vector3 dp = Follow(s, film, 0.6f, d => targets.Joint(targets.DancerOf(focus), SmplJoint.Pelvis, d));
            if (!float.IsNaN(dp.x))
            {
                Vector3 delta = dp - c;
                delta.y = 0f;
                c += delta * fw;
                radius *= Mathf.Lerp(1f, 0.88f, fw);
            }
        }

        // opt-in (the auto-directed heat films, FILM_LIBRARY.md): azimuth_deg = the eye's absolute azimuth (atan2(z, x) in world degrees) at the
        // shot's start, drift_deg_s = how fast it glides from there; a camera that stays on one side of the room instead of circling the couple
        float phi = WorldAzimuth(s, film, out float wphi) ? wphi
            : s.Camera["azimuth_deg"] != null
            ? (s.P("azimuth_deg", 0f) + s.P("drift_deg_s", 0f) * Mathf.Max(0f, film - s.F0)) * Mathf.Deg2Rad
            : SideAzimuth(s, 1) + RunAngle(s, film, r => OrbitRate(r));
        if (s.Flow?.Track != null && s.Flow.Track.TryLift(film, out float trackLift))
        {
            // v4.3 (UNITY_CONTRACT_v4 K): inside an azimuth track's window its planned lift REPLACES the live inline lift below (the planner's twin
            // of it: the damped pelvises, the +-36-frame Hann window, the cap and the closer factor already applied; the track's crossing.lift says
            // whether the planned line crossing raises the eye toward the ground band's top, so the dancer behind shows longer); outside the
            // window the live lift
            height += trackLift;
        }
        else if (s.B("avoid_inline"))
        {
            float cap = (s.Camera["inline_lift_max_m"] != null ? s.P("inline_lift_max_m", 0.4f) : float.PositiveInfinity) * closer;
            float lift;
            if (flow != null && flow.Version >= 2 && s.Camera["world_azimuth_deg"] != null)
            {
                // v4 slow orbit: the lift is a Hann-weighted mean of the capped lift over +-1.2 s of film time. The instant lift follows the
                // dancers' line, which a spin swings past the view in a few frames: measured on the first v4 sweep, the eye dropped 25 cm in
                // 0.2 s (1.6 m/s) every time the couple turned. A pure function of film time, so seeks and recordings still agree
                // every frame of the window (1/30 s; sparser samples alias the lift's frame-level twitches into a 6-frame wobble); a sample
                // beyond a dance-time cut (the replay) takes the value at the cut's own side: the window never reaches across it.
                // v4.3: this live lift only runs OUTSIDE an azimuth track's window (inside it the table's lift plays, above), so its samples read
                // the block's law, never the table: a frame next to the window (a phone way) is exactly the frame without the track
                float sum = 0f, ws = 0f;
                float lastCut = LastCutAtOrBefore(film), nextCut = NextCutAfter(film);
                for (int k = -36; k <= 36; k++)
                {
                    float w = Mathf.Cos(k / 37f * Mathf.PI * 0.5f);
                    w *= w;
                    float tk = Mathf.Clamp(film + k / 30f, Mathf.Max(lastCut, 0f), nextCut - 1e-3f);
                    WorldAzimuth(s, tk, out float pk, useTrack: false);
                    sum += w * Mathf.Min(InlineLift(s, tk, pk), cap);
                    ws += w;
                }

                lift = sum / ws;
            }
            else
            {
                lift = Mathf.Min(InlineLift(s, film, phi), cap);
            }

            // v3: the planned azimuth may run along the dancers' line; the lift is capped (ground level)
            height += lift;
        }
        Vector3 eye = c + new Vector3(Mathf.Cos(phi) * radius, height, Mathf.Sin(phi) * radius);
        Vector3 look = c + Vector3.up * lookH;
        // v4.2 opt-in `lens_distance_scale` (the class recap's closer slow orbits, dancecap UNITY_CONTRACT_v4 I): the lens is chosen for the eye distance x
        // this - the v4.1 distance of an orbit moved 0.8 x closer - so the dancers fill more of the frame instead of CoupleFov widening the closeness away;
        // divided by the closer factor, so the lens stays the v4.1 lens through the ease (a dolly in / out with a fixed lens)
        float lensScale = Mathf.Clamp(s.P("lens_distance_scale", 1f) / Mathf.Max(0.1f, closer), 0.5f, 2f);
        FilmPose p = new() { Eye = eye, Look = look, Up = Vector3.up, Fov = CoupleFov(Vector3.Distance(eye, look) * lensScale), ScreenY = ScreenYFor() };
        // opt-in `fov_scale` (the 16:9 recap's `horizontal` block): the lens narrowed by this factor, so the couple fills more of a wide frame (fit_span
        // below still widens it whenever the dancers stand apart)
        float fovScale = s.P("fov_scale", 1f);
        if (Mathf.Abs(fovScale - 1f) > 1e-4f) p.Fov = Mathf.Max(20f, p.Fov * Mathf.Clamp(fovScale, 0.4f, 1.5f));
        if (s.B("fit_span")) FitSpan(s, film, ref p);
        // v4 slow orbit in 9:16 (UNITY_CONTRACT_v4 C): the couple's 0..top_m between the caption block and the physics legend (band in frame
        // heights from the bottom). A lens / composition change of the pose (never narrower than FitSpan needed): a pure function of the
        // damped positions, so it never makes the eye jump
        if (Vertical && s.Camera["vertical_band"] is JObject vb && (vb.Value<bool?>("fit_band") ?? false))
        {
            FitBand(ref p, new Vector3(c.x, 0f, c.z), FilmDirection.F(vb["top_m"], 1.95f), FilmDirection.F(vb["band_lo"], 0.31f), FilmDirection.F(vb["band_hi"], 0.77f),
                Mathf.Max(24f, p.Fov), 66f);
        }

        // v4.2 (UNITY_CONTRACT_v4 I): the same in 16:9 - the closer orbit's couple between the caption block and the top of the frame (the lens only
        // widens when the couple would not fit; the composition moves up so the feet stay above the captions)
        if (!Vertical && s.Camera["horizontal_band"] is JObject hb && (hb.Value<bool?>("fit_band") ?? false))
        {
            FitBand(ref p, new Vector3(c.x, 0f, c.z), FilmDirection.F(hb["top_m"], 1.9f), FilmDirection.F(hb["band_lo"], 0.17f), FilmDirection.F(hb["band_hi"], 0.95f),
                Mathf.Max(24f, p.Fov), 55f);
        }

        return p;
    }

    /// <summary>extra eye height (m) while the view runs along the line from one dancer to the other (the first render's closing orbit hid the leader
    /// behind the follower for four seconds): up to 1.9 m within 45 deg of that line, so the dancer behind shows above the one in front.
    /// A smooth function of the azimuth and the dancers' damped positions (a clamp of the azimuth cannot be continuous for a full orbit)</summary>
    float InlineLift(FilmShot s, float film, float phi)
    {
        Vector3 pl = Follow(s, film, 0.6f, d => targets.Joint(targets.DancerOf("lead"), SmplJoint.Pelvis, d));
        Vector3 pf = Follow(s, film, 0.6f, d => targets.Joint(targets.DancerOf("follow"), SmplJoint.Pelvis, d));
        if (float.IsNaN(pl.x) || float.IsNaN(pf.x)) return 0f;
        Vector2 l = new(pf.x - pl.x, pf.z - pl.z);
        if (l.sqrMagnitude < 0.04f) return 0f; // standing together: no line to speak of
        float line = Mathf.Atan2(l.y, l.x);
        float r = Mathf.Repeat(CylindricalBlend.WrapPi(phi - line) + Mathf.PI * 0.5f, Mathf.PI) - Mathf.PI * 0.5f; // 0 = along the line (either side)
        float k = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Abs(r) / (Mathf.PI * 0.25f));
        float apart = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 0.7f, l.magnitude));
        return 1.9f * k * apart;
    }

    /// <summary>widen the lens (up to the shot's maximum), then back the eye off along its line to the look point, until both dancers' pelvises
    /// (damped like the orbit's own follow) with an arm's margin fit inside 90 % of the frame width. Never narrows: a couple that stands close
    /// keeps the orbit's own framing. Pure geometry of the pose and the dancers' damped positions.</summary>
    void FitSpan(FilmShot s, float film, ref FilmPose p)
    {
        Vector3 pl = Follow(s, film, 0.6f, d => targets.Joint(targets.DancerOf("lead"), SmplJoint.Pelvis, d));
        Vector3 pf = Follow(s, film, 0.6f, d => targets.Joint(targets.DancerOf("follow"), SmplJoint.Pelvis, d));
        if (float.IsNaN(pl.x) || float.IsNaN(pf.x)) return;
        float aspect = Vertical ? 9f / 16f : 16f / 9f;
        float maxFov = Vertical ? 66f : 55f;
        for (int it = 0; it < 3; it++)
        {
            Vector3 f = p.Look - p.Eye;
            if (f.sqrMagnitude < 1e-6f) return;
            Quaternion q = Quaternion.LookRotation(f, Vector3.up);
            Vector3 fwd = q * Vector3.forward, right = q * Vector3.right;
            float need = 0f;
            foreach (Vector3 pos in new[] { pl, pf })
            {
                Vector3 v = pos + Vector3.up - p.Eye;
                float z = Vector3.Dot(v, fwd);
                if (z < 0.5f) continue;
                need = Mathf.Max(need, (Mathf.Abs(Vector3.Dot(v, right)) + 0.45f) / (z * 0.9f));
            }

            float tx = Mathf.Tan(p.Fov * 0.5f * Mathf.Deg2Rad) * aspect;
            if (need <= tx + 1e-4f) return;
            float fovNeed = 2f * Mathf.Atan(need / aspect) * Mathf.Rad2Deg;
            if (fovNeed <= maxFov)
            {
                p.Fov = fovNeed;
                return;
            }

            p.Fov = maxFov;
            float ratio = need / (Mathf.Tan(maxFov * 0.5f * Mathf.Deg2Rad) * aspect);
            p.Eye = p.Look + (p.Eye - p.Look) * Mathf.Min(ratio, 2.5f);
        }
    }

    /// <summary>the rhythm finale's orbit (eye 1.1 m, period 30 s): far enough and with the look point low enough that the
    /// whole couple with raised arms (about 2.0 m) sits between the captions and the top safe band, leaving the sides
    /// and the headroom for the beat counter and the call-outs (9:16: 37 %..86 % of the height; 16:9: 30 %..94 %)</summary>
    FilmPose Rhythm(FilmShot s, float film)
    {
        const float eyeH = 1.1f, lookH = 0.9f;
        float top = 2.2f; // the capture's highest hand or head is 2.0 m: a margin under the 9:16 top band and above the 16:9 frame edge
        float bandLo = Vertical ? 0.37f : 0.30f, bandHi = Vertical ? 0.86f : 0.94f, maxFov = Vertical ? 66f : 55f;
        // opt-in `band_lo` / `band_hi` (the 16:9 recap's `horizontal` block); without them the bands above
        bandLo = s.P("band_lo", bandLo);
        bandHi = s.P("band_hi", bandHi);
        Vector3 c = CoupleFollow(s, film);
        float phi = WorldAzimuth(s, film, out float wphi) ? wphi : SideAzimuth(s, 1) + RunAngle(s, film, r => OrbitRate(r));
        float radius = Vertical ? 3.9f : 3.3f; // the start; FitBand sets the lens (and backs the eye off) to fit the couple
        Vector3 eye = c + new Vector3(Mathf.Cos(phi) * radius, eyeH, Mathf.Sin(phi) * radius);
        Vector3 look = c + Vector3.up * lookH;
        FilmPose p = new() { Eye = eye, Look = look, Up = Vector3.up, Fov = maxFov * 0.8f, ScreenY = 0.5f };
        FitBand(ref p, new Vector3(c.x, 0f, c.z), top, bandLo, bandHi, 24f, maxFov);
        return p;
    }

    static float OrbitRate(FilmShot r)
    {
        float period = r.Mode switch
        {
            "rhythm" => 30f,
            "geometry_orbit" => r.P("period_s", 40f),
            "graph_wide" => r.P("period_s", 90f),
            _ => r.P("period_s", 20f)
        };
        return Mathf.Abs(period) < 0.1f ? 0f : 2f * Mathf.PI / period;
    }

    FilmPose Physics3Q(FilmShot s, float film, float dance)
    {
        Vector3 c = CoupleFollow(s, film, 0.8f);
        float azimuth = s.P("azimuth_deg", 35f) * Mathf.Deg2Rad;
        FilmShot first = Shots[s.RunStart];
        float d0 = clock.Dance(first.F0);
        Vector3 l = targets.Joint(targets.DancerOf("follow"), SmplJoint.Pelvis, d0) - targets.Joint(targets.DancerOf("lead"), SmplJoint.Pelvis, d0);
        float line = float.IsNaN(l.x) ? 0f : Mathf.Atan2(l.z, l.x);
        float phi = WorldAzimuth(s, film, out float wphi) ? wphi : line + Mathf.PI * 0.5f + azimuth + RunAngle(s, film, r => r.P("drift_deg_s", 4f) * Mathf.Deg2Rad);
        float dist = s.P("distance_m", Vertical ? 4.3f : 3.3f), el = s.P("elevation_deg", 18f) * Mathf.Deg2Rad;
        Vector3 look = c + Vector3.up * 0.9f;
        // v3 `look_target` (a FilmTargets target, e.g. the counterbalance pivot foot) + `look_target_weight`: the look point moves that share
        // toward it, horizontally (damped like the couple follow)
        if (s.Camera["look_target"] is JObject lt)
        {
            float w = Mathf.Clamp01(s.P("look_target_weight", 0f));
            if (w > 1e-4f)
            {
                Vector3 tp = Follow(s, film, 0.6f, d => targets.Resolve(lt, d, out Vector3 q) ? q : FilmTargets.Nan);
                if (!float.IsNaN(tp.x))
                {
                    look.x += (tp.x - look.x) * w;
                    look.z += (tp.z - look.z) * w;
                }
            }
        }

        Vector3 eye = look + new Vector3(Mathf.Cos(el) * Mathf.Cos(phi), Mathf.Sin(el), Mathf.Cos(el) * Mathf.Sin(phi)) * dist;
        FilmPose p = new() { Eye = eye, Look = look, Up = Vector3.up, Fov = CoupleFov(dist), ScreenY = ScreenYFor() };
        // opt-in (the class recap): the whole couple inside the band between the captions and the physics legend, as close as that allows
        if (s.B("fit_band")) FitBand(ref p, new Vector3(c.x, 0f, c.z), s.P("top_m", 2.2f), s.P("band_lo", Vertical ? 0.36f : 0.2f), s.P("band_hi", Vertical ? 0.72f : 0.9f), 24f, Vertical ? 66f : 55f);
        return p;
    }

    FilmPose CamerasOverview(FilmShot s, float film, float dance)
    {
        List<Vector3> pts = new();
        Vector3 c = targets.CoupleCentre(dance);
        pts.Add(c);
        pts.Add(c + Vector3.up * 1.8f);
        foreach (string id in sources.Cameras)
        {
            if (sources.TryGetPose(id, dance, out Vector3 p, out _, out _, out _)) pts.Add(p);
        }

        Vector3 centre = Vector3.zero;
        Bounds b = new(pts[0], Vector3.zero);
        foreach (Vector3 p in pts) b.Encapsulate(p);
        centre = new Vector3(b.center.x, 0f, b.center.z);
        float u = Mathf.Clamp01((film - s.F0) / Mathf.Max(0.1f, s.F1 - s.F0));
        float drift = s.P("yaw_drift_deg", 15f);
        // end the drift on the side of the phone the next shot flies into (the flight then comes from behind it)
        float endAz = -90f;
        string nextCam = s.Next != null && s.Next.Mode == "fly_to_camera" ? s.Next.S("camera", null) : null;
        if (nextCam != null && sources.TryGetPose(nextCam, clock.Dance(s.F1), out Vector3 np, out _, out _, out _))
        {
            endAz = Mathf.Atan2(np.z - centre.z, np.x - centre.x) * Mathf.Rad2Deg;
        }

        float az = (endAz - drift * (1f - Mathf.SmoothStep(0f, 1f, u))) * Mathf.Deg2Rad;
        float pitch = s.P("pitch_deg", 72f) * Mathf.Deg2Rad;
        Vector3 toEye = new(Mathf.Cos(pitch) * Mathf.Cos(az), Mathf.Sin(pitch), Mathf.Cos(pitch) * Mathf.Sin(az));
        float fov = Vertical ? 62f : 48f;
        float dist = FitDistance(centre, -toEye, fov, pts, 0.08f);
        dist = Mathf.Max(dist, s.P("height_m", 11f) / Mathf.Sin(pitch) * 0.6f);
        return new FilmPose { Eye = centre + toEye * dist, Look = centre, Up = Vector3.up, Fov = fov, ScreenY = Vertical ? 0.58f : 0.5f };
    }

    /// <summary>distance along -viewDir from the look point at which every point projects inside the frame (margin as
    /// a fraction; in 9:16 the caption block and the top band are excluded)</summary>
    float FitDistance(Vector3 look, Vector3 viewDir, float fov, List<Vector3> pts, float margin)
    {
        viewDir.Normalize();
        Quaternion q = Quaternion.LookRotation(viewDir, Mathf.Abs(viewDir.y) > 0.999f ? Vector3.forward : Vector3.up);
        float aspect = Vertical ? 9f / 16f : 16f / 9f;
        float ty = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), tx = ty * aspect;
        float yHi = Vertical ? 1f - 2f * 0.16f : 1f - 2f * margin; // NDC limits (top band)
        float yLo = Vertical ? -1f + 2f * 0.36f : -1f + 2f * 0.12f; // above the captions
        float xLim = 1f - 2f * margin;
        float best = 1f;
        // solve per point for the distance that puts it inside the limits (camera at look - viewDir * d)
        foreach (Vector3 p in pts)
        {
            Vector3 local = Quaternion.Inverse(q) * (p - look); // camera space with the camera at the look point
            for (int it = 0; it < 2; it++)
            {
                // x / (z + d) <= tx * xLim  ->  d >= |x| / (tx * xLim) - z
                best = Mathf.Max(best, Mathf.Abs(local.x) / (tx * xLim) - local.z);
                float yl = local.y >= 0 ? yHi : -yLo;
                best = Mathf.Max(best, Mathf.Abs(local.y) / (ty * Mathf.Max(0.1f, Mathf.Abs(yl))) - local.z);
            }
        }

        // the look point itself is at the frame centre while the key band is not centred: recentre approximately
        return best;
    }

    /// <summary>a phone's pose for a POV shot: its own pose and zoom, or - with the shot's `crop` block (the class recap: the video framed on the two
    /// teachers) - the SAME position with the view turned and narrowed onto the part of the phone's frame the plan chose. The video quad lies on the
    /// phone's image plane in the world, so any rotation and any narrower lens at the phone's own position still maps the picture 1:1 and the 3D
    /// overlay stays registered on it. crop = {t0, dt, cx[], cy[], zoom[]} (centre in -1..1 of the frame, y up; the zoom >= 1) in dance seconds.</summary>
    FilmPose PovPose(FilmShot s, string cam, float dance, Vector3 pos, Quaternion rot, float vfov, Vector2 shift)
    {
        FilmPose pov = FilmPose.FromRotation(pos, rot, vfov, Vector3.Distance(pos, targets.CoupleCentre(dance) + Vector3.up));
        pov.LensShift = shift;
        if (s.Camera["crop"] is JObject crop) CropPov(ref pov, crop, cam, dance);
        return pov;
    }

    void CropPov(ref FilmPose pov, JObject crop, string cam, float dance)
    {
        if (crop["cx"] is not JArray ax || crop["cy"] is not JArray ay || crop["zoom"] is not JArray az || ax.Count < 1 || ay.Count != ax.Count || az.Count != ax.Count) return;
        float t0 = FilmDirection.F(crop["t0"], 0f), dt = Mathf.Max(1e-3f, FilmDirection.F(crop["dt"], 0.1f));
        float u = Mathf.Clamp((dance - t0) / dt, 0f, ax.Count - 1);
        int i0 = Mathf.FloorToInt(u), i1 = Mathf.Min(ax.Count - 1, i0 + 1);
        float k = u - i0;
        float cx = Mathf.Lerp(ax[i0].Value<float>(), ax[i1].Value<float>(), k), cy = Mathf.Lerp(ay[i0].Value<float>(), ay[i1].Value<float>(), k);
        float z = Mathf.Max(1f, Mathf.Lerp(az[i0].Value<float>(), az[i1].Value<float>(), k));
        sources.TryGetAspect(cam, out float aspect);
        if (!Vertical && PortraitZoom > 1.001f && aspect < 1f) z = Mathf.Max(z, Mathf.Min(z * PortraitZoom, PortraitZoomMax));
        if (z < 1.001f && Mathf.Abs(cx) < 1e-3f && Mathf.Abs(cy) < 1e-3f) return;
        float tanV = Mathf.Tan(pov.Fov * 0.5f * Mathf.Deg2Rad), tanH = tanV * aspect;
        if (!Vertical)
        {
            // the crop is planned for the 9:16 window. In the 16:9 output the same vertical window is 3.2x wider (relative to a portrait picture), so the
            // pan only matters where the window is narrower than the picture: a landscape clip is kept inside its own frame (no black strip at an
            // edge), a portrait clip (the whole width is visible) stays centred as a panel with the side fill around it. The aim is a rotation, so the
            // limit is in angles: the frustum's edge angle (aim + half the narrowed lens) must stay inside the picture's edge angle.
            float halfW = Mathf.Atan(tanV / z * (16f / 9f)), halfH = Mathf.Atan(tanV / z);
            float roomX = Mathf.Atan(tanH) - halfW, roomY = Mathf.Atan(tanV) - halfH;
            cx = roomX <= 0f ? 0f : Mathf.Clamp(cx, -Mathf.Tan(roomX) / tanH, Mathf.Tan(roomX) / tanH);
            cy = roomY <= 0f ? 0f : Mathf.Clamp(cy, -Mathf.Tan(roomY) / tanV, Mathf.Tan(roomY) / tanV);
        }

        Vector3 d = new Vector3(cx * tanH, cy * tanV, 1f).normalized;
        Vector3 fwd = pov.Rot * d, up = pov.Rot * Vector3.up;
        float dist = Vector3.Distance(pov.Eye, pov.Look);
        pov.Rot = Quaternion.LookRotation(fwd, up);
        pov.Fov = 2f * Mathf.Atan(tanV / z) * Mathf.Rad2Deg;
        pov.Look = pov.Eye + fwd * dist;
        pov.Up = pov.Rot * Vector3.up;
    }

    FilmPose FlyToCamera(FilmShot s, float film, float dance, int depth)
    {
        string cam = s.S("camera", "06");
        float fly = s.P("fly_s", 1.5f), fin = s.P("fade_in_s", 0.8f), hold = s.P("hold_s", 6.5f), fout = s.P("fade_out_s", 0.8f), pull = s.P("pull_back_s", 3.4f);
        float tau = film - s.F0;
        PovComposite = s.S("composite", "3d_over_video");
        if (!sources.TryGetPose(cam, dance, out Vector3 pos, out Quaternion rot, out float vfov, out Vector2 shift))
        {
            return Orbit(s, film, dance, 3.4f, 1.5f, 0.95f, 30f);
        }

        FilmPose pov = PovPose(s, cam, dance, pos, rot, vfov, shift);
        if (tau < fly)
        {
            FilmPose from = s.Prev != null ? Pose(s.Prev, film, depth + 1) : Default(film);
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(tau / fly));
            k = Mathf.SmoothStep(0f, 1f, k); // ease harder into the lens
            return Fly(from, pov, k);
        }

        float t2 = tau - fly;
        PovCamera = cam;
        if (t2 < fin + hold + fout)
        {
            PovVideoOpacity = t2 < fin ? Mathf.SmoothStep(0f, 1f, t2 / Mathf.Max(0.01f, fin))
                : t2 < fin + hold ? 1f
                : 1f - Mathf.SmoothStep(0f, 1f, (t2 - fin - hold) / Mathf.Max(0.01f, fout));
            return pov;
        }

        // pull back out of the lens: dolly back and up, turn toward the couple, widen
        float k2 = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t2 - fin - hold - fout) / Mathf.Max(0.1f, pull)));
        Vector3 couple = targets.CoupleCentre(dance) + Vector3.up * 0.95f;
        Vector3 back = pos - (rot * Vector3.forward) * 2.2f + Vector3.up * 0.8f;
        FilmPose end = new() { Eye = back, Look = couple, Up = Vector3.up, Fov = CoupleFov(Vector3.Distance(back, couple)), ScreenY = ScreenYFor() };
        return Fly(pov, end, k2);
    }

    /// <summary>straight flight between two poses: position lerp, rotation slerp (incl. roll), FOV lerp</summary>
    static FilmPose Fly(FilmPose a, FilmPose b, float k)
    {
        Quaternion ra = a.Rotation(), rb = b.Rotation();
        Quaternion r = Quaternion.Slerp(ra, rb, k);
        Vector3 eye = Vector3.Lerp(a.Eye, b.Eye, k);
        // mid-flight the lens keeps looking at the couple (both ends look at it): a straight slerp of the two orientations
        // swings the picture away (a black frame with the couple at the bottom, the review's weak second of the flight).
        // The aim is blended in as a bump: 0 at both ends (the poses are exact), strongest in the middle.
        Vector3 lookMix = Vector3.Lerp(a.Look, b.Look, k) - eye;
        Vector3 upMix = Vector3.Slerp(a.Up.sqrMagnitude > 1e-6f ? a.Up : Vector3.up, b.Up.sqrMagnitude > 1e-6f ? b.Up : Vector3.up, k);
        float bump = Mathf.Sin(Mathf.PI * Mathf.Clamp01(k));
        if (lookMix.sqrMagnitude > 0.04f && upMix.sqrMagnitude > 1e-6f && Mathf.Abs(Vector3.Dot(lookMix.normalized, upMix.normalized)) < 0.995f)
        {
            r = Quaternion.Slerp(r, Quaternion.LookRotation(lookMix, upMix), 0.8f * bump * bump);
        }

        float fov = Mathf.Lerp(a.Fov, b.Fov, k);
        FilmPose p = FilmPose.FromRotation(eye, r, fov);
        p.LensShift = Vector2.Lerp(a.LensShift, b.LensShift, k);
        return p;
    }

    FilmPose CameraTour(FilmShot s, float film, float dance, int depth)
    {
        if (s.Camera["cameras"] is JArray list)
        {
            foreach (JToken c in list)
            {
                if (c["dance_t"] is not JArray w || w.Count != 2) continue;
                if (dance < w[0].Value<float>() - 1e-3f || dance > w[1].Value<float>() + 1e-3f) continue;
                string cam = c.Value<string>("camera");
                if (!sources.TryGetPose(cam, dance, out Vector3 pos, out Quaternion rot, out float vfov, out Vector2 shift)) continue;
                PovCamera = cam;
                PovVideoOpacity = 1f;
                PovComposite = s.S("composite", "3d_over_video");
                return PovPose(s, cam, dance, pos, rot, vfov, shift);
            }
        }

        return Orbit(s, film, dance, 3.1f, 1.6f, 0.95f, 25f);
    }

    FilmPose FeetCloseup(FilmShot s, float film, float dance)
    {
        Dancer d = targets.DancerOf(s.S("dancer", "lead"));
        // "left" | "right" | anything else (both, an unknown TBD side): the middle of both of his feet - never a guessed foot
        bool both = !FilmTargets.TrySide(s.S("foot", "both"), out bool left);
        // the shot's reference instant: its hold (the step), else its middle
        float dRef = clock.Dance((s.F0 + s.F1) * 0.5f);
        foreach (FilmSegment seg in s.Segments)
        {
            if (seg.Hold)
            {
                dRef = seg.D0;
                break;
            }
        }

        Vector3 centre = targets.CoupleCentre(dRef);
        Vector3 foot = targets.Foot(d, left, dRef, 0f);
        if (both)
        {
            Vector3 fl = targets.Foot(d, true, dRef, 0f), fr = targets.Foot(d, false, dRef, 0f);
            foot = float.IsNaN(fl.x) ? fr : float.IsNaN(fr.x) ? fl : (fl + fr) * 0.5f;
        }

        Vector3 dir = foot - centre;
        dir.y = 0f;
        if (float.IsNaN(dir.x) || dir.sqrMagnitude < 1e-4f) dir = Vector3.back;
        dir.Normalize();
        // across the step a little: his foot nearest, her feet visible beside it
        dir = Quaternion.AngleAxis(s.P("azimuth_offset_deg", 25f), Vector3.up) * dir;
        if (WorldAzimuth(s, film, out float wphi)) dir = new Vector3(Mathf.Cos(wphi), 0f, Mathf.Sin(wphi)); // v3: the planned side
        float spread = 0f;
        Vector3[] feet =
        {
            targets.Foot(targets.DancerOf("lead"), true, dRef, 0f), targets.Foot(targets.DancerOf("lead"), false, dRef, 0f),
            targets.Foot(targets.DancerOf("follow"), true, dRef, 0f), targets.Foot(targets.DancerOf("follow"), false, dRef, 0f)
        };
        for (int i = 0; i < feet.Length; i++)
        {
            for (int j = i + 1; j < feet.Length; j++)
            {
                if (!float.IsNaN(feet[i].x) && !float.IsNaN(feet[j].x)) spread = Mathf.Max(spread, Vector3.Distance(feet[i], feet[j]));
            }
        }

        float fov = s.P("fov_deg", 35f);
        float height = s.P("height_m", 0.45f), lookH = s.P("look_height_m", 0.12f), dist = s.P("distance_m", 1.6f);
        float aspect = Vertical ? 9f / 16f : 16f / 9f;
        float tx = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * aspect;
        float need = (Mathf.Max(spread, 0.6f) * 0.5f + 0.3f) / tx;
        if (Vertical)
        {
            fov = Mathf.Max(fov, 44f);
            tx = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * aspect;
            need = (Mathf.Max(spread, 0.6f) * 0.5f + 0.25f) / tx;
            height += 0.15f;
        }

        dist = Mathf.Clamp(Mathf.Max(dist, need), 1.2f, 3.6f);
        Vector3 c = Follow(s, film, 0.5f, targets.FeetCentre);
        Vector3 eye = c + dir * dist + Vector3.up * height;
        Vector3 look = c + Vector3.up * lookH;
        return new FilmPose { Eye = eye, Look = look, Up = Vector3.up, Fov = fov, ScreenY = Vertical ? 0.55f : 0.45f };
    }

    // ------------------------------------------------------------------ feature zoom (the class recap: a tight 3D shot of the body part the teachers name)

    static List<SmplJoint> JointList(JToken arr)
    {
        List<SmplJoint> l = new();
        if (arr is JArray a)
        {
            foreach (JToken t in a)
            {
                if (FilmTargets.TryJoint(t.Value<string>(), out SmplJoint j)) l.Add(j);
            }
        }

        return l;
    }

    /// <summary>camera {feature, dancer lead|follow|both, joints[], partner_joints[], pad_m, min_distance_m, azimuth_offset_deg, elevation_deg, fov_deg}:
    /// the eye side-on to the couple's line at the feature's height, the look point on the named joints (damped), the distance the smallest that
    /// keeps those joints of the named dancer(s) - and the partner's matching joints, so both dancers stay readable - inside the key band of the
    /// frame (9:16: between the captions and the top band). No hand close-ups: the joint lists name shoulders, elbows, torso, hips, knees.</summary>
    FilmPose FeatureZoom(FilmShot s, float film, float dance)
    {
        string who = s.S("dancer", "both");
        List<SmplJoint> main = JointList(s.Camera["joints"]), partner = JointList(s.Camera["partner_joints"]);
        if (main.Count == 0) main.Add(SmplJoint.Spine3);
        Dancer lead = targets.DancerOf("lead"), follow = targets.DancerOf("follow");
        bool both = who != "lead" && who != "follow";
        Dancer primary = both ? null : targets.DancerOf(who), other = both ? null : (who == "lead" ? follow : lead);
        List<Vector3> Main(float d)
        {
            List<Vector3> pts = new();
            foreach (Dancer dn in both ? new[] { lead, follow } : new[] { primary })
            {
                foreach (SmplJoint j in main)
                {
                    Vector3 p = targets.Joint(dn, j, d);
                    if (!float.IsNaN(p.x)) pts.Add(p);
                }
            }

            return pts;
        }

        List<Vector3> Context(float d)
        {
            List<Vector3> pts = new();
            if (!both)
            {
                foreach (SmplJoint j in partner.Count > 0 ? partner : main)
                {
                    Vector3 p = targets.Joint(other, j, d);
                    if (!float.IsNaN(p.x)) pts.Add(p);
                }
            }

            return pts;
        }

        Vector3 Centroid(float d)
        {
            List<Vector3> m = Main(d);
            if (m.Count == 0) return new Vector3(float.NaN, float.NaN, float.NaN);
            Vector3 c = Vector3.zero;
            foreach (Vector3 p in m) c += p;
            return c / m.Count;
        }

        Vector3 centre = Follow3(s, film, 0.45f, Centroid);
        if (float.IsNaN(centre.x)) centre = targets.CoupleCentre(dance) + Vector3.up * 1.1f;
        float phi = WorldAzimuth(s, film, out float wphi) ? wphi : SideAzimuth(s, 1) + s.P("azimuth_offset_deg", 25f) * Mathf.Deg2Rad;
        float el = s.P("elevation_deg", 5f) * Mathf.Deg2Rad;
        Vector3 toEye = new(Mathf.Cos(el) * Mathf.Cos(phi), Mathf.Sin(el), Mathf.Cos(el) * Mathf.Sin(phi));
        Vector3 f = -toEye;
        Vector3 r = Vector3.Cross(Vector3.up, f).normalized, u = Vector3.Cross(f, r);
        float fov = s.P("fov_deg", 36f);
        float aspect = Vertical ? 9f / 16f : 16f / 9f;
        float ty = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), tx = ty * aspect;
        float screenY = Vertical ? 0.6f : 0.55f;
        float xLim = (Vertical ? 0.86f : 0.9f), yLim = (Vertical ? 0.46f : 0.6f);   // fractions of the half frame the points may reach
        float pad = s.P("pad_m", 0.4f);
        Vector3 cf = centre;
        // the distance the points of one instant ask for, smoothed over the past half second (a pure function of the film clock)
        float Need(float d)
        {
            float need = 0f;
            foreach (List<Vector3> pts in new[] { Main(d), Context(d) })
            {
                foreach (Vector3 p in pts)
                {
                    Vector3 v = p - cf;
                    float z0 = Vector3.Dot(v, f), x = Mathf.Abs(Vector3.Dot(v, r)) + pad, y = Mathf.Abs(Vector3.Dot(v, u)) + pad;
                    need = Mathf.Max(need, x / (tx * xLim) - z0, y / (ty * yLim) - z0);
                }
            }

            return need;
        }

        float dreq = Follow3(s, film, 0.5f, d => new Vector3(Need(d), 0f, 0f)).x;
        float now = Need(dance);
        float dist = Mathf.Clamp(Mathf.Max(dreq, now, s.P("min_distance_m", 1.6f)), 1.2f, 6f);
        Vector3 eye = centre + toEye * dist;
        return new FilmPose { Eye = eye, Look = centre, Up = Vector3.up, Fov = fov, ScreenY = screenY };
    }

    FilmPose Overhead(FilmShot s, float film, float dance)
    {
        string framing = s.S("framing", "couple");
        const float fov = 24f;
        float aspect = Vertical ? 9f / 16f : 16f / 9f;
        float ty = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), tx = ty * aspect;
        Vector3 look;
        float dist;
        float bandY = Vertical ? 0.5f : 0.84f; // share of the frame height for the content (vertical: 14 %..64 %)
        // v4 (owner 2026-10-22: the physics legend sits in the 9:16 top band, 14.4-21.6 % down): the couple between the legend and the
        // captions, 23 %..65 % of the height (the look point 44 % down)
        bool legendBand = Vertical && flow != null && flow.Version >= 2;
        if (legendBand) bandY = 0.42f;
        if (framing == "floor")
        {
            Rect area = DanceLayers.Instance != null ? DanceLayers.Instance.DanceArea : new Rect(-1.5f, -1.5f, 3f, 3f);
            look = new Vector3(area.center.x, 0f, area.center.y);
            float halfZ = area.height * 0.5f + 0.5f, halfX = area.width * 0.5f + 0.5f;
            dist = Mathf.Max(halfZ / (ty * bandY), halfX / (tx * 0.92f));
        }
        else
        {
            look = Follow(s, film, s.P("follow_s", 0.8f), targets.CoupleCentre);
            // the couple's footprint seen from above (his feet to her hand): tighter than the old 2.4 m. A shot may ask for more (`across_m`: the
            // recap film's wide pivots), and it is widened whenever the four feet are apart (a spin throws the arms and legs wide)
            float across = s.P("across_m", 1.9f);
            float feetSpread = 0f;
            Vector3[] fs =
            {
                targets.Foot(targets.DancerOf("lead"), true, clock.Dance(film), 0f), targets.Foot(targets.DancerOf("lead"), false, clock.Dance(film), 0f),
                targets.Foot(targets.DancerOf("follow"), true, clock.Dance(film), 0f), targets.Foot(targets.DancerOf("follow"), false, clock.Dance(film), 0f)
            };
            for (int i = 0; i < fs.Length; i++)
            {
                for (int j = i + 1; j < fs.Length; j++)
                {
                    if (!float.IsNaN(fs[i].x) && !float.IsNaN(fs[j].x)) feetSpread = Mathf.Max(feetSpread, Vector3.Distance(fs[i], fs[j]));
                }
            }

            if (s.P("across_m", 0f) > 0f) across = Mathf.Max(across, feetSpread + 1.3f);
            dist = Mathf.Max(across * 0.5f / (ty * bandY), across * 0.5f / (tx * 0.92f));
            if (s.P("fill", 0f) > 0.01f)
            {
                if (flow == null)
                {
                    dist = OverheadFillDistance(s, film, ref look, tx, ty, bandY, s.B("parallax"));
                }
                else if (flow.Version >= 2)
                {
                    // v4: the height a Hann-weighted power mean (p = 4: it leans toward the larger needs, so a spreading pivot still widens
                    // the view) over +-1 s, never across a dance-time cut. The v3 window (+-0.24 s) still moved the eye 20 cm in one frame
                    // (first v4 sweep, 240.0 s), and the cranes inherit the overhead's motion at their top
                    // the look point (the dancers' box centre) the same Hann mean: the box's corners are joints, its centre twitches
                    // with a hand frame by frame (the overhead's eye zig-zagged 1-4 cm per frame)
                    float sum = 0f, ws = 0f;
                    Vector3 lsum = Vector3.zero;
                    // every frame of the window (1/30 s): samples further apart alias the frame-level twitches into a 3-frame zig-zag
                    float lastCut = LastCutAtOrBefore(film), nextCut = NextCutAfter(film);
                    for (int i = -30; i <= 30; i++)
                    {
                        float ti = Mathf.Clamp(film + i / 30f, Mathf.Max(lastCut, 0f), nextCut - 1e-3f);
                        Vector3 l2 = look;
                        float w = Mathf.Cos(i / 31f * Mathf.PI * 0.5f);
                        w *= w;
                        float d = OverheadFillDistance(s, ti, ref l2, tx, ty, bandY, s.B("parallax"));
                        sum += w * d * d * d * d;
                        lsum += w * l2;
                        ws += w;
                    }

                    look = lsum / Mathf.Max(1e-6f, ws);
                    dist = Mathf.Pow(sum / Mathf.Max(1e-6f, ws), 0.25f);
                }
                else
                {
                    // v3: the height a weighted mean over +-0.24 s (the instant 'never crop' need follows swinging limbs frame by frame: the
                    // camera height jittered by up to 20 cm per frame in the overheads of the pivots)
                    float sum = 0f, ws = 0f;
                    Vector3 lk = look;
                    for (int i = -3; i <= 3; i++)
                    {
                        Vector3 l2 = look;
                        float w = 4f - Mathf.Abs(i);
                        sum += w * OverheadFillDistance(s, film + i * 0.08f, ref l2, tx, ty, bandY, s.B("parallax"));
                        ws += w;
                        if (i == 0) lk = l2;
                    }

                    dist = sum / ws;
                    look = lk;
                }
            }
        }

        dist = Mathf.Clamp(dist, 3f, 30f);
        Vector3 eye = look + new Vector3(0f, dist, -0.001f);
        FilmPose p = new() { Eye = eye, Look = look, Up = Vector3.forward, Fov = fov, ScreenY = legendBand ? 0.56f : Vertical ? 0.6f : 0.53f };
        // v3 `up_policy: from_entry`: the top of the picture is the view direction of the shot the crane rose from (Up = -(cos a, 0, sin a),
        // a = that eye's azimuth about its look point, LIVE at the transition's start; else `up_azimuth_deg`), and the eye stands at 89.9 deg on
        // that azimuth: the crane up / down arrives / leaves without a roll
        if (s.S("up_policy", "") == "from_entry")
        {
            float a = EntryAzimuth(s);
            if (!float.IsNaN(a))
            {
                p.Eye = look + HmFilm.FlowMath.Spherical(a, OverheadElevation) * dist;
                p.Up = -new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            }
        }
        else if (s.S("up_policy", "") == "orbit" && WorldAzimuth(s, film, out float oa))
        {
            // v4: the overhead turns with the orbit - Up = -(cos a(t), 0, sin a(t)), a(t) = world_azimuth_deg + drift_deg_s x (film - F0) (the
            // orbit's own function continued), the eye at 89.9 deg on that azimuth: the picture keeps turning at the orbit's rate (never static)
            p.Eye = look + HmFilm.FlowMath.Spherical(oa, OverheadElevation) * dist;
            p.Up = -new Vector3(Mathf.Cos(oa), 0f, Mathf.Sin(oa));
        }
        if (s.B("tilt_to_3q"))
        {
            // start overhead, tilt down to a high 3/4 (55 deg) from the south side so her lean reads
            float u = Mathf.Clamp01((film - s.F0 - 1.5f) / Mathf.Max(0.5f, (s.F1 - s.F0) * 0.55f));
            float k = Mathf.SmoothStep(0f, 1f, u);
            float el = Mathf.Lerp(89.9f, 55f, k) * Mathf.Deg2Rad;
            float d2 = Mathf.Lerp(dist, Vertical ? 4.6f : 3.8f, k);
            Vector3 lk = look + Vector3.up * Mathf.Lerp(0f, 0.7f, k);
            p.Eye = lk + new Vector3(0f, Mathf.Sin(el), -Mathf.Cos(el)) * d2;
            p.Look = lk;
            p.Up = Vector3.Slerp(Vector3.forward, Vector3.up, k);
            p.Fov = Mathf.Lerp(fov, Vertical ? 50f : 40f, k);
        }

        return p;
    }

    static readonly SmplJoint[] ExtentJoints =
    {
        SmplJoint.Head, SmplJoint.L_Shoulder, SmplJoint.R_Shoulder, SmplJoint.L_Elbow, SmplJoint.R_Elbow, SmplJoint.L_Wrist, SmplJoint.R_Wrist,
        SmplJoint.Pelvis, SmplJoint.L_Knee, SmplJoint.R_Knee, SmplJoint.L_Ankle, SmplJoint.R_Ankle, SmplJoint.L_Foot, SmplJoint.R_Foot
    };

    /// <summary>the floor-plan box of both dancers' joints at a dance time (false = none tracked)</summary>
    bool ExtentBox(float dance, out Vector2 lo, out Vector2 hi)
    {
        lo = new Vector2(float.MaxValue, float.MaxValue);
        hi = new Vector2(float.MinValue, float.MinValue);
        bool any = false;
        foreach (string role in new[] { "lead", "follow" })
        {
            Dancer dn = targets.DancerOf(role);
            foreach (SmplJoint j in ExtentJoints)
            {
                Vector3 p = targets.Joint(dn, j, dance);
                if (float.IsNaN(p.x)) continue;
                lo = Vector2.Min(lo, new Vector2(p.x, p.z));
                hi = Vector2.Max(hi, new Vector2(p.x, p.z));
                any = true;
            }
        }

        return any;
    }

    /// <summary>The class recap's overhead (owner 2026-10-10: "get closer with the camera"; the first render's couple filled a quarter of the frame):
    /// the camera height follows the dancers' own spread. `fill` = the share of the frame width the box of both dancers' joints (plus 0.22 m round
    /// it) fills at the tightest; the height is a weighted mean over -0.8 .. +0.8 s of what that asks (a smooth envelope that grows ahead of a pivot),
    /// and never less than what the instant (now, and 0.3 s ahead) needs to keep the box inside 90 % of the frame: a pivot or an exit is never cropped.
    /// The look point is the box centre (damped). Pure geometry of the film clock.</summary>
    float OverheadFillDistance(FilmShot s, float film, ref Vector3 look, float tx, float ty, float bandY, bool parallax = false)
    {
        float fill = Mathf.Clamp(s.P("fill", 0.62f), 0.3f, 0.9f);
        float minFill = Mathf.Clamp(s.P("min_fill", 0.46f), 0.2f, fill);
        // margin: a body beyond its joints (hands, hair, a thick arm); never: the share of the frame the instant's extent may reach. A shot may name
        // its own (`margin_m`, `never`: the 16:9 recap's `horizontal` block); the defaults are what every earlier direction got
        float never = Mathf.Clamp(s.P("never", 0.9f), 0.5f, 0.99f), margin = Mathf.Clamp(s.P("margin_m", 0.22f), 0f, 0.6f);
        // opt-in `parallax` (the 16:9 recap): the flat box above ignores that a head or a raised hand (up to 2 m) is much nearer the lens than the
        // floor under it, so it is drawn bigger and a wide frame (the height is what binds there) crops it. The distance is then the least at which
        // every joint (plus the margin) projects inside the fraction: |offset from the look point| / (tan * frac) + the joint's own height
        Vector3 centre = Vector3.zero;
        if (parallax)
        {
            centre = Follow(s, film, s.P("follow_s", 0.8f), d =>
            {
                if (!ExtentBox(d, out Vector2 l0, out Vector2 h0)) return targets.CoupleCentre(d);
                return new Vector3((l0.x + h0.x) * 0.5f, 0f, (l0.y + h0.y) * 0.5f);
            });
        }

        float NeedAt(float f, float frac, float hfrac)
        {
            float dn = clock.Dance(Mathf.Clamp(f, 0f, direction.FilmDuration));
            if (parallax && !float.IsNaN(centre.x))
            {
                float worst = float.NaN;
                foreach (string role in new[] { "lead", "follow" })
                {
                    Dancer dn2 = targets.DancerOf(role);
                    foreach (SmplJoint j in ExtentJoints)
                    {
                        Vector3 q = targets.Joint(dn2, j, dn);
                        if (float.IsNaN(q.x)) continue;
                        float h = Mathf.Max(0f, q.y) + 0.12f;
                        float need = Mathf.Max((Mathf.Abs(q.x - centre.x) + margin) / (tx * frac), (Mathf.Abs(q.z - centre.z) + margin) / (ty * bandY * hfrac)) + h;
                        worst = float.IsNaN(worst) ? need : Mathf.Max(worst, need);
                    }
                }

                return worst;
            }

            if (!ExtentBox(dn, out Vector2 lo, out Vector2 hi)) return float.NaN;
            return Mathf.Max((hi.x - lo.x + 2f * margin) / (2f * tx * frac), (hi.y - lo.y + 2f * margin) / (2f * ty * bandY * hfrac));
        }

        float sum = 0f, wsum = 0f;
        for (int i = -4; i <= 4; i++)
        {
            float w = 1f - Mathf.Abs(i) / 5f;
            float need = NeedAt(film + i * 0.2f, fill, never);
            if (float.IsNaN(need)) continue;
            sum += need * w;
            wsum += w;
        }

        float env = wsum > 0f ? sum / wsum : 8f;
        float inst = Mathf.Max(0f, NeedAt(film, never, never));
        float ahead = Mathf.Max(0f, NeedAt(film + 0.3f, never, never));
        float far = NeedAt(film, minFill, minFill); // never further than the least fill allows (a pivot's own need still wins below)
        if (!float.IsNaN(far)) env = Mathf.Min(env, far);
        Vector3 c = Follow(s, film, s.P("follow_s", 0.8f), d =>
        {
            if (!ExtentBox(d, out Vector2 lo, out Vector2 hi)) return targets.CoupleCentre(d);
            return new Vector3((lo.x + hi.x) * 0.5f, 0f, (lo.y + hi.y) * 0.5f);
        });
        if (!float.IsNaN(c.x)) look = c;
        return Mathf.Max(env, inst, ahead);
    }

    static float DefaultGraphFocus() =>
        DanceLayers.Instance != null && DanceLayers.Instance.Tour != null ? DanceLayers.Instance.Tour.GraphFocusDistanceScale : 0.5f;

    FilmPose GraphWide(FilmShot s, float film, int depth)
    {
        DanceGraphLayer g = DanceLayers.Instance != null ? DanceLayers.Instance.Graph : null;
        if (g == null) return Default(film);
        g.Bounds(out Vector3 centre, out float radius);
        float d = s.P("distance_m", 8f), h = s.P("height_m", 3.6f);
        if (s.B("zoom_out"))
        {
            float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((film - s.F0) / Mathf.Max(0.1f, s.F1 - s.F0)));
            FilmShot prev = s.Prev != null && s.Prev.Mode == "graph_wide" ? s.Prev : null;
            float d0 = prev != null ? prev.P("distance_m", 8f) : d, h0 = prev != null ? prev.P("height_m", 3.6f) : h;
            d = Mathf.Lerp(d0, d, u);
            h = Mathf.Lerp(h0, h, u);
        }

        float fov = Vertical ? 56f : 42f;
        float aspect = Vertical ? 9f / 16f : 16f / 9f;
        float tx = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * aspect;
        float fit = radius * 1.08f / tx;
        float scale = Mathf.Max(1f, fit / Mathf.Max(1f, d));
        d *= scale;
        h *= scale;
        // graph focus (user 2026-10-08: "50% closer to the state machine graph when it is the point of focus"): the eye sits
        // graph_focus_distance_scale x the whole-graph fit's distance along the same view direction (default: the viewer's
        // DanceTour.GraphFocusDistanceScale, 0.5). Closer, the whole graph no longer fits every frame, so the look point
        // moves to the centre of the nodes the PATH visits (graph_focus_path_weight 0..1, default 1): the path and the
        // miniature couple stay in frame, the orbit turns around them
        float focus = Mathf.Clamp(s.P("graph_focus_distance_scale", DefaultGraphFocus()), 0.2f, 2f);
        Vector3 look = centre;
        if (focus < 0.999f && g.PathBounds(out Vector3 pathCentre)) look = Vector3.Lerp(centre, pathCentre, Mathf.Clamp01(s.P("graph_focus_path_weight", 1f)));
        d *= focus;
        h *= focus;
        float phi = -Mathf.PI * 0.5f + RunAngle(s, film, OrbitRate);
        Vector3 eye = look + new Vector3(Mathf.Cos(phi) * d, h, Mathf.Sin(phi) * d);
        return new FilmPose { Eye = eye, Look = look, Up = Vector3.up, Fov = fov, ScreenY = Vertical ? 0.57f : 0.5f };
    }

    // ------------------------------------------------------------------ v3 flow: transitions instead of blends and cuts (RECAP_FILM.md "What the Unity director must implement" 1-2)

    /// <summary>the shot whose transition_in is running now (null = a shot's own pose is on screen)</summary>
    public FilmShot TransitionShot { get; private set; }

    /// <summary>the shot whose own pose is on screen (null during a transition)</summary>
    public FilmShot CameraShot { get; private set; }

    /// <summary>0..1 through the running transition (linear in film time; the path uses its smootherstep)</summary>
    public float TransitionU { get; private set; }

    /// <summary>the running transition has a phone at one of its ends (the director keeps the near plane small)</summary>
    public bool TransitionTouchesPhone => TransitionShot != null && (TransitionShot.Mode == "camera_tour" || TransitionShot.Prev?.Mode == "camera_tour");

    public const float OverheadElevation = 89.9f * Mathf.Deg2Rad;

    /// <summary>how far (0..1) the aim of a phone way is pulled from the couple toward the phone's picture mid-way (Composite)</summary>
    public static float AimToPicture = 0f; // tried 0.5: readable surfaces 26 -> 27 of 81, the couple out of frame 7 -> 11 frames - not worth it
    const float FrozenEps = 1e-3f;
    readonly Dictionary<int, float> entryAzimuth = new();
    readonly Dictionary<int, Vector2> planOf = new();
    readonly Dictionary<int, float> planScore = new();
    readonly HmFilm.FlowPath path = new();

    FilmPose EvaluateFlow(float film)
    {
        flow.CameraAt(film, out int own, out int trans, out float u);
        TransitionShot = trans >= 0 ? Shots[trans] : null;
        CameraShot = own >= 0 ? Shots[own] : null;
        TransitionU = u;
        Current = TransitionShot ?? CameraShot ?? ShotAt(film);
        if (TransitionShot != null) return TransitionPose(TransitionShot, film, u);
        // a phone shot's own pose sets PovCamera / PovVideoOpacity = 1: the full-frame video from the arrival to the next way out
        return CameraShot != null ? Pose(CameraShot, film, 0) : Default(film);
    }

    /// <summary>the eye's planned azimuth (rad) of a v3 3D shot: world_azimuth_deg + drift_deg_s x (film - F0) (not clamped before F0: a
    /// transition into the shot starts before it and sees the same smooth drift). v4.3 (dancecap UNITY_CONTRACT_v4 K): a block that names an
    /// azimuth track (camera.azimuth_track) plays the track's table strictly inside its film window instead - the ONE place every caller (the
    /// orbit, rhythm, physics_3q, the feature zoom, the feet close-up, the overhead's orbit Up) and every way (they blend these live poses)
    /// read the azimuth from; at and outside the window the law, so a way next to the window is exactly as without it. useTrack = false: the
    /// law always (the live inline lift's Hann samples of a frame outside the window, which may reach into it)</summary>
    bool WorldAzimuth(FilmShot s, float film, out float phi, bool useTrack = true)
    {
        phi = 0f;
        if (useTrack && s.Flow?.Track != null && s.Flow.Track.TryAzimuthDeg(film, out float deg))
        {
            phi = deg * Mathf.Deg2Rad;
            return true;
        }

        if (s.Camera["world_azimuth_deg"] == null) return false;
        phi = (s.P("world_azimuth_deg", 0f) + s.P("drift_deg_s", 0f) * (film - s.F0)) * Mathf.Deg2Rad;
        return true;
    }

    float NextCutAfter(float film)
    {
        float c = float.PositiveInfinity;
        foreach (float x in clock.Cuts)
        {
            if (x > film + 1e-5f && x < c) c = x;
        }

        return c;
    }

    float LastCutAtOrBefore(float film)
    {
        float c = float.NegativeInfinity;
        foreach (float x in clock.Cuts)
        {
            if (x <= film + 1e-5f && x > c) c = x;
        }

        return c;
    }

    /// <summary>the couple's centre on the floor at a film time (the axis of the cylindrical paths)</summary>
    Vector3 CentreAt(float film)
    {
        Vector3 c = targets.CoupleCentre(clock.Dance(Mathf.Clamp(film, 0f, direction.FilmDuration)));
        if (float.IsNaN(c.x)) c = Vector3.zero;
        c.y = 0f;
        return c;
    }

    /// <summary>the phone of a camera_tour shot at a dance time (its cameras[] window containing it)</summary>
    static string TourCamera(FilmShot s, float dance)
    {
        if (s.Camera["cameras"] is not JArray list) return null;
        foreach (JToken c in list)
        {
            if (c["dance_t"] is not JArray w || w.Count != 2) continue;
            if (dance < w[0].Value<float>() - 1e-3f || dance > w[1].Value<float>() + 1e-3f) continue;
            return c.Value<string>("camera");
        }

        return null;
    }

    /// <summary>a phone shot's phone position and its FULL (uncropped) optical axis at a film time</summary>
    bool PhoneAxis(FilmShot s, float film, out Vector3 pos, out Vector3 fwd)
    {
        pos = fwd = Vector3.zero;
        float dance = clock.Dance(Mathf.Clamp(film, 0f, direction.FilmDuration));
        string cam = TourCamera(s, dance);
        if (cam == null || !sources.TryGetPose(cam, dance, out pos, out Quaternion rot, out _, out _)) return false;
        fwd = rot * Vector3.forward;
        return true;
    }

    /// <summary>the azimuth (rad) of the view an overhead was entered from: the previous shot's eye about its look point at the start of the
    /// overhead's transition (a phone: opposite its view direction), cached per shot; else the plan's up_azimuth_deg; NaN without either</summary>
    public float EntryAzimuth(FilmShot over)
    {
        if (entryAzimuth.TryGetValue(over.Index, out float v)) return v;
        float az = over.P("up_azimuth_deg", float.NaN) * Mathf.Deg2Rad;
        if (over.Flow?.T != null && over.Prev != null)
        {
            entryAzimuth[over.Index] = az; // re-entrancy guard
            float t0 = over.Flow.T.Frozen ? over.F0 - FrozenEps : over.Flow.TransStart;
            FilmPose p = Pose(over.Prev, t0, 2);
            Vector3 d = p.Pov ? -(p.Rot * Vector3.forward) : p.Eye - p.Look;
            if (new Vector2(d.x, d.z).sqrMagnitude > 1e-6f) az = Mathf.Atan2(d.z, d.x);
        }

        entryAzimuth[over.Index] = az;
        return az;
    }

    FilmPose TransitionPose(FilmShot tr, float film, float u)
    {
        HmFilm.FlowTransition f = tr.Flow.T;
        if (f.IsV4 && flow.Version >= 2)
        {
            FilmPose v4 = TransitionPoseV4(tr, film, u, out bool handled);
            if (handled) return v4;
        }

        float k = HmFilm.FlowMath.Smootherstep(u);
        FilmShot prev = tr.Prev;
        float fromT = f.Frozen ? tr.F0 - FrozenEps : film;
        FilmPose a = prev != null ? Pose(prev, fromT, 1) : Default(film);
        FilmPose b = Pose(tr, film, 1);
        if (k <= 0f) return a;
        if (k >= 1f) return b;
        Vector3 c = CentreAt(film);
        bool aPhone = prev != null && prev.Mode == "camera_tour" && a.Pov;
        bool bPhone = tr.Mode == "camera_tour" && b.Pov;
        bool aOver = prev != null && prev.Mode == "overhead_floorcraft";
        bool bOver = tr.Mode == "overhead_floorcraft";
        if (!aPhone && !bPhone && !aOver && !bOver && !a.Pov && !b.Pov) return ArcLook(a, b, k, c, f);
        return Composite(tr, prev, a, b, film, fromT, k, c, f, aPhone, bPhone, aOver, bOver);
    }

    // ------------------------------------------------------------------ v4 ("hm film flow v2", dancecap UNITY_CONTRACT_v4 2): the ways are C2
    // blends of the two shots' LIVE poses by smootherstep ramps (the cruise law for the cranes) of their own windows of u. Nothing is frozen or
    // cached: at u = 0 the camera's position, velocity and acceleration are the departing view's, at u = 1 the arriving view's; between a way
    // and an orbit the camera never stops dead, out of a phone it leaves at the phone's own (handheld) motion.

    /// <summary>a v4 way's pose; handled = false for a combination v4 does not define (a crane with a phone at one end: the v3 path)</summary>
    FilmPose TransitionPoseV4(FilmShot tr, float film, float u, out bool handled)
    {
        handled = true;
        HmFilm.FlowTransition f = tr.Flow.T;
        FilmShot prev = tr.Prev;
        if (f.V4Path == "orbit_recentre") return RecentreV4(tr, film, u, f);
        FilmPose a = prev != null ? Pose(prev, film, 1) : Default(film);
        FilmPose b = Pose(tr, film, 1);
        if (u <= 0f) return a;
        if (u >= 1f) return b;
        if (f.V4Path == "crane")
        {
            if (a.Pov || b.Pov)
            {
                handled = false;
                return b;
            }

            return CraneV4(a, b, u, f, film);
        }

        return WayV4(tr, prev, a, b, film, u, f);
    }

    /// <summary>
    /// The phone ways and 3D flights (glide_axis = phone_in, axis_glide = phone_out, axis_glide_axis = phone to phone, glide): a phone end's
    /// anchor is P(t, d) = phone(t) - d x axis_run x fwd(t) on its FULL optical axis (d = ramp(dolly_out) for the phone left, 1 - ramp(dolly /
    /// dolly_in) for the phone entered), a 3D end's anchor is its eye; the eye = the cylindrical glide between the anchors about the couple's
    /// damped centre by ramp(glide); the rotation slerps the live rotations (a phone's is its CROPPED view) by ramp(rot); the FOV zooms by the
    /// log-tan law over ramp(zoom) (or the v4.fov window).
    /// </summary>
    FilmPose WayV4(FilmShot tr, FilmShot prev, FilmPose a, FilmPose b, float film, float u, HmFilm.FlowTransition f)
    {
        bool aPhone = prev != null && prev.Mode == "camera_tour" && a.Pov;
        bool bPhone = tr.Mode == "camera_tour" && b.Pov;
        float run = Mathf.Max(0f, f.AxisRunM);
        float s = HmFilm.FlowMath.Smootherstep(u);
        Vector3 eA = a.Eye, eB = b.Eye;
        if (aPhone && PhoneAxis(prev, film, out _, out Vector3 fwdA)) eA = a.Eye - fwdA * (run * f.W("dolly_out", u, s));
        if (bPhone && PhoneAxis(tr, film, out _, out Vector3 fwdB)) eB = b.Eye - fwdB * (run * (1f - f.W(f.Has("dolly_in") ? "dolly_in" : "dolly", u, s)));
        Vector3 c = CoupleFollow(tr, film);
        float g = f.W("glide", u, s);
        float yHi = float.IsInfinity(f.HeightHi) ? 2.4f : f.HeightHi;
        Vector3 eye = HmFilm.FlowMath.GlideEye(c, eA, eB, g, f.PlanDeltaRad, f.ClearRadiusM, f.HeightLo, yHi);
        Quaternion rot = Quaternion.Slerp(a.Rotation(), b.Rotation(), f.W("rot", u, s));
        float z = f.FovWin.Valid ? f.FovWin.At(u) : f.W("zoom", u, s);
        FilmPose p = FilmPose.FromRotation(eye, rot, HmFilm.FlowMath.LogTanFov(a.Fov, b.Fov, z), Mathf.Max(1f, Vector3.Distance(eye, c + Vector3.up)));
        p.LensShift = Vector2.Lerp(a.LensShift, b.LensShift, z);
        return p;
    }

    /// <summary>the azimuth (rad) of a pose's eye about its look point; straight above it (an overhead) the azimuth its Up points away from</summary>
    static float EyeAzimuth(FilmPose p)
    {
        Vector3 v = p.Eye - p.Look;
        if (new Vector2(v.x, v.z).sqrMagnitude > 1e-4f * v.sqrMagnitude) return Mathf.Atan2(v.z, v.x);
        Vector3 up = p.Pov ? p.Rot * Vector3.up : p.Up;
        return Mathf.Atan2(-up.z, -up.x);
    }

    /// <summary>
    /// A crane (crane_up / crane_down, UNITY_CONTRACT_v4 A): about the look points (lerped), the eye's elevation and azimuth between the two
    /// ends' live values by the cruise law (the azimuth is the orbit's a(t) at both ends: the overhead turns with the orbit), the distance by
    /// the profile's distance window, the aim locked on the look point with the in-plane up (no roll; at the top the overhead's orbit Up), the
    /// composition pitch lerped, the FOV by the log-tan law.
    /// </summary>
    FilmPose CraneV4(FilmPose a, FilmPose b, float u, HmFilm.FlowTransition f, float film)
    {
        float s = f.W("elevation", u, HmFilm.FlowMath.Cruise(u, 0.25f));
        float sd = f.W("distance", u, s);
        float sz = f.FovWin.Valid ? f.FovWin.At(u) : f.W("zoom", u, s);
        Vector3 va = a.Eye - a.Look, vb = b.Eye - b.Look;
        float da = Mathf.Max(1e-3f, va.magnitude), db = Mathf.Max(1e-3f, vb.magnitude);
        float ela = Mathf.Asin(Mathf.Clamp(va.y / da, -1f, 1f)), elb = Mathf.Asin(Mathf.Clamp(vb.y / db, -1f, 1f));
        float aza = EyeAzimuth(a), azb = EyeAzimuth(b);
        Vector3 look = Vector3.Lerp(a.Look, b.Look, s);
        float az = aza + HmFilm.FlowMath.WrapPi(azb - aza) * s, el = Mathf.Lerp(ela, elb, s);
        Vector3 eye = look + HmFilm.FlowMath.Spherical(az, el) * Mathf.Lerp(da, db, sd);
        float pitch = Mathf.Lerp(Pitch(a), Pitch(b), s);
        Quaternion fallback = Quaternion.Slerp(a.Rotation(), b.Rotation(), s);
        Quaternion rot = HmFilm.FlowMath.Aim(look - eye, HmFilm.FlowMath.CraneUp(az, el), fallback) * Quaternion.Euler(pitch, 0f, 0f);
        float fov = HmFilm.FlowMath.LogTanFov(a.Fov, b.Fov, sz);
        // mid-way neither end's framing holds (the heads are nearer the lens than the floor: the first 16:9 sweep lost the follower's head at
        // the top for 50 frames halfway up): the lens widens just enough to keep both dancers' heads and feet inside 92 % of the frame, by
        // a soft max weighted by a C2 plateau (smootherstep up over the first 15 % of the way, 1, down over the last 15 %: 0 with its
        // first two derivatives at both ends, so the ends stay exact and C2; a single 64 u^3 (1 - u)^3 bump gave only 42 % of the widening
        // at u = 0.75, where the 16:9 crane still touched the follower's hair to the top edge)
        float bump = HmFilm.FlowMath.Ramp(u, 0f, 0.15f) * (1f - HmFilm.FlowMath.Ramp(u, 0.85f, 1f));
        if (bump > 1e-4f)
        {
            float need = CraneFovNeed(eye, rot, film);
            if (!float.IsNaN(need))
            {
                const float k = 1.5f;
                float x = (need - fov) / k;
                float soft = k * (x > 20f ? x : Mathf.Log(1f + Mathf.Exp(x)));
                fov += bump * soft;
            }
        }

        FilmPose p = FilmPose.FromRotation(eye, rot, Mathf.Clamp(fov, 5f, 100f), Mathf.Max(0.5f, Vector3.Distance(eye, look)));
        p.LensShift = Vector2.Lerp(a.LensShift, b.LensShift, sz);
        return p;
    }

    /// <summary>the vertical FOV (deg) a camera at eye with rotation rot needs to keep both dancers' heads (+0.15 m) and feet inside 92 % of the
    /// frame (NaN without the joints)</summary>
    float CraneFovNeed(Vector3 eye, Quaternion rot, float film)
    {
        float dance = clock.Dance(Mathf.Clamp(film, 0f, direction.FilmDuration));
        float aspect = Vertical ? 9f / 16f : 16f / 9f;
        Quaternion inv = Quaternion.Inverse(rot);
        float t = 0f;
        bool any = false;
        foreach (string role in new[] { "lead", "follow" })
        {
            Dancer d = targets.DancerOf(role);
            Vector3 h = targets.Joint(d, SmplJoint.Head, dance);
            foreach (Vector3 q in new[] { h + Vector3.up * 0.15f, targets.Foot(d, true, dance, 0f), targets.Foot(d, false, dance, 0f) })
            {
                if (float.IsNaN(q.x)) continue;
                Vector3 v = inv * (q - eye);
                if (v.z < 0.2f) continue;
                t = Mathf.Max(t, Mathf.Abs(v.y) / (v.z * 0.92f), Mathf.Abs(v.x) / (v.z * 0.92f * aspect));
                any = true;
            }
        }

        return any ? 2f * Mathf.Atan(t) * Mathf.Rad2Deg : float.NaN;
    }

    /// <summary>
    /// The replay's ways (orbit_recentre): the camera keeps the orbit (this shot's own live pose: a(t), radius, height, lens); only its centre
    /// glides from the couple's damped centre 1 ms before the dance-time jump to the live one: eye and look + (1 - w) (C_frozen - C_live(t)),
    /// w = ramp(centre). At u = 0 that is the previous orbit's pose at the jump, so the camera does not move across it (the flash marks it).
    /// </summary>
    FilmPose RecentreV4(FilmShot tr, float film, float u, HmFilm.FlowTransition f)
    {
        FilmPose b = Pose(tr, film, 1);
        if (u >= 1f || b.Pov) return b;
        Vector3 cLive = CoupleFollow(tr, film);
        Vector3 cFrozen = tr.Prev != null ? CoupleFollow(tr.Prev, tr.F0 - FrozenEps) : cLive;
        float w = f.W("centre", u, HmFilm.FlowMath.Smootherstep(u));
        Vector3 off = (1f - w) * (cFrozen - cLive);
        off.y = 0f;
        b.Eye += off;
        b.Look += off;
        return b;
    }

    /// <summary>arc (3D to 3D, replays): cylindrical about the couple's centre - azimuth the plan's way round, horizontal radius and height lerped
    /// (the radius never below the clear radius, the height inside the ground band unless an end is outside it), look point / FOV / composition
    /// lerped, no roll; the aim stays on the couple because the look point does</summary>
    static FilmPose ArcLook(FilmPose a, FilmPose b, float k, Vector3 c, HmFilm.FlowTransition f)
    {
        Vector3 ca = HmFilm.FlowMath.ToCyl(a.Eye, c), cb = HmFilm.FlowMath.ToCyl(b.Eye, c);
        float delta = HmFilm.FlowMath.Branch(cb.x - ca.x, f.PlanDeltaRad);
        float r = Mathf.Max(Mathf.Lerp(ca.y, cb.y, k), Mathf.Min(f.ClearRadiusM, ca.y, cb.y));
        float y = Mathf.Clamp(Mathf.Lerp(ca.z, cb.z, k), Mathf.Min(f.HeightLo, ca.z, cb.z), Mathf.Max(f.HeightHi, ca.z, cb.z));
        Vector3 eye = HmFilm.FlowMath.FromCyl(new Vector3(ca.x + delta * k, r, y), c);
        return new FilmPose
        {
            Eye = eye, Look = Vector3.Lerp(a.Look, b.Look, k), Up = Vector3.Slerp(a.Up, b.Up, k).normalized, Fov = Mathf.Lerp(a.Fov, b.Fov, k),
            ScreenY = Mathf.Lerp(a.ScreenY, b.ScreenY, k), LensShift = Vector2.Lerp(a.LensShift, b.LensShift, k)
        };
    }

    /// <summary>
    /// The ways into, out of and between phones and the cranes (RECAP_FILM.md item 1): one path of keys sampled by cost length -
    ///   [out along phone A's optical axis to its portal] or [a crane down from the overhead to a ground pose at its entry azimuth]
    ///   + a cylindrical Bezier about the couple (G1 with the axes)
    ///   + [in along phone B's optical axis from its portal] or [a crane up to the overhead from a ground pose at its entry azimuth].
    /// The portal is axis_run_m behind the phone on its full optical axis, turned to the phone's CROPPED view; the rotation reaches the cropped
    /// view at the portal and keeps it on the dolly; the FOV lerps to the crop's. The live poses are the ends: exact at k = 0 and 1.
    /// </summary>
    /// <summary>the azimuth bulge of a transition's cylindrical part (FlowPath.AvoidBulge): fixed for the whole way - decided from the geometry at its
    /// start (a pure function of the shot, cached), so the path never switches shape mid-flight</summary>
    Vector2 PlanOf(FilmShot tr)
    {
        if (planOf.TryGetValue(tr.Index, out Vector2 v)) return v;
        planOf[tr.Index] = Vector2.zero; // re-entrancy guard
        float t = tr.Flow.TransStart + 1e-3f;
        HmFilm.FlowTransition f = tr.Flow.T;
        FilmShot prev = tr.Prev;
        float fromT = f.Frozen ? tr.F0 - FrozenEps : t;
        FilmPose a = prev != null ? Pose(prev, fromT, 1) : Default(t);
        FilmPose b = Pose(tr, t, 1);
        bool aPhone = prev != null && prev.Mode == "camera_tour" && a.Pov, bPhone = tr.Mode == "camera_tour" && b.Pov;
        bool aOver = prev != null && prev.Mode == "overhead_floorcraft", bOver = tr.Mode == "overhead_floorcraft";
        // the geometry at the way's start, middle and end (the live ends move during it: a 3D view that ends 0.5 m further in front of a phone
        // than it started made a plan from the start alone fly through the picture)
        List<HmFilm.FlowPath.CylGeo> outerGeos = planGeos, mine = new();
        planGeos = mine; // nested plans (SurfaceDepth) gather into their own lists
        foreach (float uu in new[] { 0f, 0.5f, 1f })
        {
            float tt = tr.Flow.TransStart + 1e-3f + uu * (f.DurationS - 2e-3f);
            float ft = f.Frozen ? tr.F0 - FrozenEps : tt;
            FilmPose a2 = prev != null ? Pose(prev, ft, 1) : Default(tt);
            FilmPose b2 = Pose(tr, tt, 1);
            Composite(tr, prev, a2, b2, tt, ft, 0.5f, CentreAt(tt), f, prev != null && prev.Mode == "camera_tour" && a2.Pov, tr.Mode == "camera_tour" && b2.Pov,
                aOver, bOver, true);
        }

        planGeos = outerGeos;
        Vector2 plan = HmFilm.FlowPath.AvoidPlan(mine, out float bad);
        if (PlanDebug.TryGetValue(tr.Index, out string db)) PlanDebug[tr.Index] = db + $" || over start / middle / end: bulge {plan.x * Mathf.Rad2Deg:0} deg lift {plan.y:0.00} m, summed badness + effort {bad:0.0}";
        planOf[tr.Index] = plan;
        planScore[tr.Index] = bad;
        return plan;
    }

    Vector2 lastPlan;
    List<HmFilm.FlowPath.CylGeo> planGeos = new();

    /// <summary>how each transition's plan was chosen (hm_film_flow --action plan)</summary>
    public readonly Dictionary<int, string> PlanDebug = new();

    /// <summary>the plan of the transition into this shot (computes it when needed)</summary>
    public string PlanReport(FilmShot tr)
    {
        if (tr?.Flow?.T == null) return "no transition";
        planOf.Remove(tr.Index);
        PlanOf(tr);
        return PlanDebug.TryGetValue(tr.Index, out string v) ? v : $"plan {planOf[tr.Index]} (no cylindrical part)";
    }

    /// <summary>the depth (m) of a frustum surface: the plan's (0.9), or closer to its phone when the 3D end of its way (the view the camera
    /// comes from or goes to) stands inside the frustum before the picture - from there the picture would cover the whole view; it then stands
    /// 0.35 m before that view (at least 0.3 m from the phone). Decided once from the poses at the way's start; the projection from the phone is
    /// the same at any depth, so the hand-off to the full-frame video does not change.</summary>
    public float SurfaceDepth(HmFilm.FlowFrustum fr)
    {
        if (!float.IsNaN(fr.DepthUsed)) return fr.DepthUsed;
        fr.DepthUsed = fr.DepthM;
        if (flow == null || fr.ShotIndex < 0 || fr.ShotIndex >= Shots.Count) return fr.DepthUsed;
        // the way: into the phone shot (enter) or out of it into the next shot (leave)
        FilmShot phoneShot = Shots[fr.ShotIndex];
        FilmShot way = fr.Enter ? phoneShot : phoneShot.Next;
        if (way?.Flow?.T == null) return fr.DepthUsed;
        float t = fr.Enter ? way.Flow.TransStart : way.Flow.TransEnd;
        FilmShot other = fr.Enter ? phoneShot.Prev : way;
        if (other == null) return fr.DepthUsed;
        FilmPose e = Pose(other, fr.Enter && way.Flow.T.Frozen ? way.F0 - FrozenEps : t, 2);
        float dance = clock.Dance(Mathf.Clamp(t, 0f, direction.FilmDuration));
        if (!sources.TryGetPose(fr.Camera, dance, out Vector3 pos, out Quaternion rot, out float vfov, out _)) return fr.DepthUsed;
        sources.TryGetAspect(fr.Camera, out float aspect);
        Vector3 fwd = rot * Vector3.forward, right = rot * Vector3.right, up = rot * Vector3.up;
        float tv = Mathf.Tan(vfov * 0.5f * Mathf.Deg2Rad), th = tv * aspect;
        Vector3 portal = pos - fwd * way.Flow.T.AxisRunM;
        float run = way.Flow.T.AxisRunM;
        // the 3D end inside the frustum before the picture: the picture would cover its whole view
        Vector3 d = e.Eye - pos;
        float fz = Vector3.Dot(d, fwd), x = Vector3.Dot(d, right), y = Vector3.Dot(d, up);
        bool inside = fz > 0.05f && fz < fr.DepthM + 0.15f && Mathf.Abs(x) < fz * th + 0.25f && Mathf.Abs(y) < fz * tv + 0.25f;
        float best = inside ? Mathf.Clamp(fz - 0.35f, 0.3f, fr.DepthM) : fr.DepthM;
        if (flow.Version >= 2)
        {
            // v4: the ways are live glides (no cached plan to score): only the 3D end standing inside the frustum before the picture moves it
            fr.DepthUsed = best;
            return best;
        }
        // the depth with the cleanest way: of best / 0.75 / 0.6 / 0.45 / 0.3 m the one whose plan (FlowPath.AvoidPlan over the way's start,
        // middle and end: crossings, hairpins, detour effort) scores lowest, a smaller picture costing 1.5 per metre. A straight-line test
        // shrank most phone-to-phone pictures to 0.3 m (too small to read); keeping 0.9 m whenever the default curve crossed made some ways
        // swing 100 deg round a picture the 3D view stands right behind
        float start = best, chosen = best, chosenScore = float.PositiveInfinity;
        foreach (float depth in new[] { start, 0.75f, 0.6f, 0.45f, 0.3f })
        {
            if (depth > start + 1e-4f) continue;
            fr.DepthUsed = depth;
            planOf.Remove(way.Index);
            PlanOf(way);
            // the plan's score (detour effort + badness) plus 1.5 per metre the picture comes closer: a smaller picture is worth a shorter
            // detour (a 45-deg swing round a 0.65-m picture in 0.85 s peaked at 22 m/s)
            float sc = (planScore.TryGetValue(way.Index, out float v) ? v : 0f) + 1.5f * (start - depth);
            if (sc < chosenScore - 1e-3f)
            {
                chosen = depth;
                chosenScore = sc;
            }
        }

        planOf.Remove(way.Index); // planned again with the depth chosen
        best = chosen;

        fr.DepthUsed = best;
        return fr.DepthUsed;
    }


    FilmPose Composite(FilmShot tr, FilmShot prev, FilmPose a, FilmPose b, float film, float fromT, float k, Vector3 c, HmFilm.FlowTransition f,
        bool aPhone, bool bPhone, bool aOver, bool bOver, bool planBulge = false)
    {
        Vector2 cachedPlan = planBulge ? Vector2.zero : PlanOf(tr); // before this path is built: PlanOf builds its own
        path.Clear();
        Quaternion ra = a.Rotation(), rb = b.Rotation();
        Vector3 upA = a.Pov ? ra * Vector3.up : a.Up, upB = b.Pov ? rb * Vector3.up : b.Up;
        Vector3 fwdA = Vector3.zero, fwdB = Vector3.zero;
        bool axA = aPhone && PhoneAxis(prev, fromT, out _, out fwdA);
        bool axB = bPhone && PhoneAxis(tr, film, out _, out fwdB);
        float run = Mathf.Max(0f, f.AxisRunM);
        Vector3 portalA = axA ? a.Eye - fwdA * run : a.Eye, portalB = axB ? b.Eye - fwdB * run : b.Eye;

        // the ground ends of the middle (cylindrical) part
        Vector3 g0 = portalA, g3 = portalB, l0 = a.Look, l3 = b.Look, u0 = upA, u3 = upB;
        Quaternion q0 = ra, q3 = rb;
        Vector3? t0 = axA ? -fwdA : null, t3 = axB ? fwdB : null;

        if (aOver || bOver)
        {
            // a crane (RECAP_FILM.md item 1): a vertical arc about the look point between the overhead and the ground end (the phone's portal, or
            // the 3D view itself), the elevation leaving / reaching the ground with zero slope; the azimuth turns near the ground (crane down:
            // after the descent; crane up: before the rise). A phone end adds its dolly along the optical axis.
            if (aOver)
            {
                Vector3 ge = axB ? portalB : b.Eye;
                path.CraneBetween(a.Eye, a.Look, ra, ge, b.Look, rb, f.PlanDeltaRad);
                if (axB) path.Line(portalB, b.Eye, rb);
            }
            else
            {
                if (axA) path.Line(a.Eye, portalA, ra);
                Vector3 ge = axA ? portalA : a.Eye;
                path.CraneBetween(ge, a.Look, ra, b.Eye, b.Look, rb, f.PlanDeltaRad);
            }

            if (planBulge) lastPlan = Vector2.zero;
        }
        else
        {
            if (axA) path.Line(a.Eye, portalA, ra);
            float r0 = HmFilm.FlowMath.ToCyl(g0, c).y, r3 = HmFilm.FlowMath.ToCyl(g3, c).y;
            float rMin = Mathf.Min(f.ClearRadiusM, r0, r3);
            float yLo = Mathf.Min(f.HeightLo, g0.y, g3.y), yHi = Mathf.Max(float.IsInfinity(f.HeightHi) ? 2.4f : f.HeightHi, Mathf.Max(g0.y, g3.y));
            Vector2 plan;
            if (planBulge)
            {
                // the phones' frustum surfaces at the ends (+ 0.2 m margin): the cylindrical part must neither pass through them nor double back
                // a list of its own: SurfaceDepth may plan other ways (and this one) while the rectangles are gathered
                List<(Vector3 centre, Vector3 right, Vector3 up, Vector3 normal, float hw, float hh)> rects = new();
                if (axA) AddSurfaceRect(rects, prev, fromT, f, false);
                if (axB) AddSurfaceRect(rects, tr, film, f, true);
                plan = HmFilm.FlowPath.AvoidPlan(c, g0, t0, g3, t3, f.PlanDeltaRad, rMin, yLo, yHi, rects);
                lastPlan = plan;
                HmFilm.FlowPath.CylGeo geo = new() { C = c, P0 = g0, P3 = g3, T0 = t0, T3 = t3, PlanDelta = f.PlanDeltaRad, RMin = rMin, YLo = yLo, YHi = yHi };
                geo.Rects.AddRange(rects);
                planGeos.Add(geo);
                System.Text.StringBuilder db = new();
                db.Append($"shot {tr.Segments[0].Id}: g0 {g0} t0 {t0} g3 {g3} t3 {t3} c {c} rMin {rMin:0.00} y [{yLo:0.00}, {yHi:0.00}] rects {rects.Count}");
                foreach ((Vector3 centre, Vector3 right, Vector3 up, Vector3 normal, float hw, float hh) r in rects) db.Append($" [centre {r.centre} hw {r.hw:0.00} hh {r.hh:0.00} n {r.normal}]");
                db.Append($" -> plan bulge {plan.x * Mathf.Rad2Deg:0} deg lift {plan.y:0.00} m badness {HmFilm.FlowPath.CylBadness(c, g0, t0, g3, t3, f.PlanDeltaRad, rMin, yLo, yHi, plan.x, plan.y, rects):0.0}");
                foreach ((float bd, float lf) in new[] { (0f, 0f), (0f, 0.5f), (45f, 0f), (-45f, 0f), (90f, 0f), (-90f, 0f) })
                    db.Append($"; ({bd},{lf}) {HmFilm.FlowPath.CylBadness(c, g0, t0, g3, t3, f.PlanDeltaRad, rMin, yLo, yHi, bd * Mathf.Deg2Rad, lf, rects):0.0}");
                PlanDebug[tr.Index] = db.ToString();
            }
            else
            {
                plan = cachedPlan;
            }

            // mid-way the aim is pulled halfway from the couple to a phone's picture (0.6 m along its axis): the picture comes into view earlier on
            // the way in and stays longer on the way out, the couple stays in the frame
            Vector3 l0c = axA ? Vector3.Lerp(l0, a.Eye + fwdA * 0.6f, AimToPicture) : l0, l3c = axB ? Vector3.Lerp(l3, b.Eye + fwdB * 0.6f, AimToPicture) : l3;
            path.Cyl(c, g0, t0, g3, t3, f.PlanDeltaRad, rMin, yLo, yHi, q0, q3, l0c, l3c, u0, u3, HmFilm.FlowPath.PartSamples, plan.x, plan.y);
            if (axB) path.Line(portalB, b.Eye, rb);
        }

        path.Sample(k, out Vector3 eye, out Quaternion rot);
        FilmPose p = FilmPose.FromRotation(eye, rot, Mathf.Lerp(a.Fov, b.Fov, k), Mathf.Max(1f, Vector3.Distance(eye, c + Vector3.up)));
        p.LensShift = Vector2.Lerp(a.LensShift, b.LensShift, k);
        return p;
    }

    /// <summary>a phone shot's frustum surface as a rectangle (centre, right, up, normal, half width / height + 0.2 m) for the bulge test</summary>
    void AddSurfaceRect(List<(Vector3 centre, Vector3 right, Vector3 up, Vector3 normal, float hw, float hh)> rects, FilmShot s, float film, HmFilm.FlowTransition f, bool enter)
    {
        float dance = clock.Dance(Mathf.Clamp(film, 0f, direction.FilmDuration));
        string cam = TourCamera(s, dance);
        float depth = flow != null ? flow.FrustumDepthM : 0.9f;
        if (flow != null)
        {
            foreach (HmFilm.FlowFrustum fr in flow.Frustums)
            {
                if (fr.ShotIndex == s.Index && fr.Enter == enter && fr.Camera == cam) depth = SurfaceDepth(fr);
            }
        }

        if (cam == null || !sources.TryGetSurfaceCorners(cam, dance, depth, out _, out Vector3 tl, out Vector3 tr, out Vector3 br, out Vector3 bl)) return;
        Vector3 centre = (tl + br) * 0.5f, right = tr - tl, up = tl - bl;
        float hw = right.magnitude * 0.5f + 0.2f, hh = up.magnitude * 0.5f + 0.2f;
        rects.Add((centre, right.normalized, up.normalized, Vector3.Cross(right, up).normalized, hw, hh));
    }

    /// <summary>a ground pose G for a crane at an overhead's look point lo: on the vertical plane at azimuth az, at the elevation and distance the far
    /// ground end (eye e, look le) has about its own look point, about lo raised to that look height; aimed at it with world up</summary>
    static void Ground(Vector3 lo, Vector3 e, Vector3 le, float az, float pitchDeg, out Vector3 eye, out Vector3 look, out float el, out float d, out Quaternion rot)
    {
        Vector3 v = e - le;
        d = Mathf.Max(0.5f, v.magnitude);
        el = Mathf.Asin(Mathf.Clamp(v.y / d, -1f, 1f));
        look = new Vector3(lo.x, le.y, lo.z);
        eye = look + HmFilm.FlowMath.Spherical(az, el) * d;
        rot = HmFilm.FlowMath.Aim(look - eye, Vector3.up, Quaternion.identity) * Quaternion.Euler(pitchDeg, 0f, 0f);
    }

    /// <summary>the composition pitch (deg) FilmPose.Rotation adds for a non-POV pose (its look point at ScreenY of the frame)</summary>
    static float Pitch(FilmPose p) => p.Pov ? 0f : Mathf.Atan((2f * p.ScreenY - 1f) * Mathf.Tan(p.Fov * 0.5f * Mathf.Deg2Rad)) * Mathf.Rad2Deg;

    // ------------------------------------------------------------------ blends

    float TurnAtStart(FilmPose a, FilmPose b)
    {
        float aa = Mathf.Atan2(a.Eye.z - a.Look.z, a.Eye.x - a.Look.x), ab = Mathf.Atan2(b.Eye.z - b.Look.z, b.Eye.x - b.Look.x);
        return CylindricalBlend.WrapPi(ab - aa);
    }

    /// <summary>cylindrical blend between two live poses: the look point lerps, the eye's height and horizontal distance
    /// lerp and its azimuth turns the way it was going to turn at the blend start (no flip mid-blend); near-vertical
    /// targets (overhead) crossfade to a straight lerp</summary>
    static FilmPose Blend(FilmPose a, FilmPose b, float s, float turn0)
    {
        if (a.Pov || b.Pov) return Fly(a, b, s);
        Vector3 look = Vector3.Lerp(a.Look, b.Look, s);
        Vector2 da = new(a.Eye.x - a.Look.x, a.Eye.z - a.Look.z), db = new(b.Eye.x - b.Look.x, b.Eye.z - b.Look.z);
        float ha = da.magnitude, hb = db.magnitude;
        float aa = Mathf.Atan2(da.y, da.x), ab = hb > 1e-4f ? Mathf.Atan2(db.y, db.x) : aa + turn0;
        float delta = turn0 + CylindricalBlend.WrapPi(CylindricalBlend.WrapPi(ab - aa) - turn0);
        float az = aa + delta * s;
        float h = Mathf.Lerp(ha, hb, s), y = Mathf.Lerp(a.Eye.y, b.Eye.y, s);
        Vector3 eye = new(look.x + h * Mathf.Cos(az), y, look.z + h * Mathf.Sin(az));
        float w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 1.5f, Mathf.Min(ha, hb)));
        if (w < 1f) eye = Vector3.Lerp(Vector3.Lerp(a.Eye, b.Eye, s), eye, w);
        return new FilmPose
        {
            Eye = eye, Look = look, Up = Vector3.Slerp(a.Up, b.Up, s).normalized, Fov = Mathf.Lerp(a.Fov, b.Fov, s),
            ScreenY = Mathf.Lerp(a.ScreenY, b.ScreenY, s)
        };
    }

    // ------------------------------------------------------------------ output

    /// <summary>write the pose to the render rig (parked CameraControl: nothing else moves it)</summary>
    public static void Apply(Transform rig, Camera cam, FilmPose p)
    {
        if (rig == null) return;
        Quaternion r = p.Rotation();
        if (!float.IsFinite(p.Eye.x) || !float.IsFinite(r.x)) return;
        rig.SetPositionAndRotation(p.Eye, r);
        if (cam == null) return;
        if (Mathf.Abs(cam.fieldOfView - p.Fov) > 1e-3f) cam.fieldOfView = Mathf.Clamp(p.Fov, 5f, 120f);
        bool shift = p.LensShift.sqrMagnitude > 1e-8f;
        if (shift || cam.usePhysicalProperties)
        {
            if (!shift && cam.usePhysicalProperties)
            {
                cam.lensShift = Vector2.zero;
            }
            else
            {
                cam.lensShift = p.LensShift;
            }
        }
    }
}
