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

    public float P(string key, float fallback) => FilmDirection.F(Camera[key], fallback);
    public string S(string key, string fallback) => Camera[key]?.Type == JTokenType.String ? Camera.Value<string>(key) : fallback;
    public bool B(string key) => Camera[key]?.Type == JTokenType.Boolean && Camera.Value<bool>(key);
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
    public bool Vertical;
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
                Index = Shots.Count, Mode = seg.Mode, F0 = seg.F0, F1 = seg.F1, Camera = seg.Camera, Prev = cur, CutIn = cut || cur == null
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
        if (s == null) return Default(film);
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
        float runStart = Shots[s.RunStart].F0;
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

    FilmPose Orbit(FilmShot s, float film, float dance, float radius, float height, float lookH, float period)
    {
        Vector3 c = CoupleFollow(s, film);
        if (Vertical) radius *= 1.25f;
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

        float phi = SideAzimuth(s, 1) + RunAngle(s, film, r => OrbitRate(r));
        if (s.B("avoid_inline")) height += InlineLift(s, film, phi);
        Vector3 eye = c + new Vector3(Mathf.Cos(phi) * radius, height, Mathf.Sin(phi) * radius);
        Vector3 look = c + Vector3.up * lookH;
        FilmPose p = new() { Eye = eye, Look = look, Up = Vector3.up, Fov = CoupleFov(Vector3.Distance(eye, look)), ScreenY = ScreenYFor() };
        if (s.B("fit_span")) FitSpan(s, film, ref p);
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
        Vector3 c = CoupleFollow(s, film);
        float phi = SideAzimuth(s, 1) + RunAngle(s, film, r => OrbitRate(r));
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
        float phi = line + Mathf.PI * 0.5f + azimuth + RunAngle(s, film, r => r.P("drift_deg_s", 4f) * Mathf.Deg2Rad);
        float dist = s.P("distance_m", Vertical ? 4.3f : 3.3f), el = 18f * Mathf.Deg2Rad;
        Vector3 look = c + Vector3.up * 0.9f;
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
        if (z < 1.001f && Mathf.Abs(cx) < 1e-3f && Mathf.Abs(cy) < 1e-3f) return;
        sources.TryGetAspect(cam, out float aspect);
        float tanV = Mathf.Tan(pov.Fov * 0.5f * Mathf.Deg2Rad), tanH = tanV * aspect;
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
        float phi = SideAzimuth(s, 1) + s.P("azimuth_offset_deg", 25f) * Mathf.Deg2Rad;
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
        if (framing == "floor")
        {
            Rect area = DanceLayers.Instance != null ? DanceLayers.Instance.DanceArea : new Rect(-1.5f, -1.5f, 3f, 3f);
            look = new Vector3(area.center.x, 0f, area.center.y);
            float halfZ = area.height * 0.5f + 0.5f, halfX = area.width * 0.5f + 0.5f;
            dist = Mathf.Max(halfZ / (ty * bandY), halfX / (tx * 0.92f));
        }
        else
        {
            look = Follow(s, film, 0.8f, targets.CoupleCentre);
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
            if (s.P("fill", 0f) > 0.01f) dist = OverheadFillDistance(s, film, ref look, tx, ty, bandY);
        }

        dist = Mathf.Clamp(dist, 3f, 30f);
        Vector3 eye = look + new Vector3(0f, dist, -0.001f);
        FilmPose p = new() { Eye = eye, Look = look, Up = Vector3.forward, Fov = fov, ScreenY = Vertical ? 0.6f : 0.53f };
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
    float OverheadFillDistance(FilmShot s, float film, ref Vector3 look, float tx, float ty, float bandY)
    {
        float fill = Mathf.Clamp(s.P("fill", 0.62f), 0.3f, 0.9f);
        float minFill = Mathf.Clamp(s.P("min_fill", 0.46f), 0.2f, fill);
        const float never = 0.9f, margin = 0.22f; // margin: a body beyond its joints (hands, hair, a thick arm)
        float NeedAt(float f, float frac, float hfrac)
        {
            float dn = clock.Dance(Mathf.Clamp(f, 0f, direction.FilmDuration));
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
        Vector3 c = Follow(s, film, 0.8f, d =>
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
