using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using VRTKLite.Controllers;

/// <summary>
/// View states (VIEWER_SPEC 4) and the prototype directed tour (6, "hm_tour"; Timeline + Cinemachine later):
/// Orbit -> [Camera tour: slot, skipped until source videos are exported] -> Overhead floor craft -> Geometry ->
/// Physics -> Dance graph -> Fingerprint. Each state lasts a whole number of measures (switches on measure starts)
/// and blends avatar opacity, skeleton fade, graph fade and the camera over BlendSeconds.
/// Camera (VIEWER_SPEC 5.2): the tour is the desktop rig's ICameraDirector. The rig (CameraControl, order -20) asks
/// TryGetCameraPose each LateUpdate for the CURRENT state's pose only; each state's eye height depends on the state
/// only, never on the dancers, and follows the rig's damped XZ anchors. Every state switch is a new Shot: the rig
/// blends cylindrically from the pose on screen (so a switch, or a switch during a blend, never pops). Each state's
/// motion runs on its own phase from its entry. Explicit commands (start, goto, next, hm_graph, the O key) hand the
/// camera to the director; the automatic measure-boundary advance does not take it back from a user who flew away
/// (W A S D Q E Z X). In VR there is no rig: the states only change layers.
/// Stop() restores every layer it touched and hands the camera to free-fly where it is.
/// </summary>
[DefaultExecutionOrder(-10)]
public class DanceTour : MonoBehaviour, ICameraDirector
{
    public enum View
    {
        None,
        Orbit,
        CameraTour,
        Overhead,
        Geometry,
        Physics,
        DanceGraph,
        Fingerprint
    }

    public float BlendSeconds = 1.0f;
    public int MeasuresPerState = 2;
    public int GraphMeasures = 3;

    static readonly View[] Sequence =
        { View.Orbit, View.CameraTour, View.Overhead, View.Geometry, View.Physics, View.DanceGraph, View.Fingerprint };

    static readonly string[] HeadLayers = { "floor", "timing", "physics", "tension", "avatars", "hud" };

    // avatar opacity per state as a factor of the user default (HeadMovement.AvatarOpacity: the DISPLAYED opacity, 0.35 =
    // 65 % transparent on screen): Orbit 0.35, Overhead 0.23, Geometry 0.175, Physics 0.12 (VIEWER_SPEC 4)
    static float OpacityFactor(View v) => v switch
    {
        View.Orbit or View.CameraTour => 1f,
        View.Overhead => 2f / 3f,
        View.Geometry => 0.5f,
        View.Physics => 1f / 3f,
        _ => 0f
    };

    View current = View.None, previous = View.None;
    bool running; // auto-advance
    int sequenceIndex = -1;
    int measuresInState;
    int lastMeasure = -1;
    float stateStart, blendStart = -10f;
    float phase; // Time.time at the current state's entry (its orbit / drift clock)
    int shot;
    float shotBlend;
    readonly Dictionary<string, bool> savedHeadLayers = new();
    readonly Dictionary<LineRenderer, float> savedWidth = new();
    readonly List<LineRenderer> lineBuffer = new();
    List<LineRenderer> contactLines;
    float avatarAlphaFrom = -1, avatarAlphaTo = -1, skeletonFrom = 1, skeletonTo = 1, graphFrom, graphTo;
    float avatarAlphaNow = -1, skeletonNow = 1, graphNow, opacityBase = -1;
    // dance-graph chase smoothing (XZ 0.5 s, Y 1.0 s); exact when paused or on a cut
    Vector3 chaseEye, chaseLook, chaseEyeVel, chaseLookVel;
    bool chaseValid;
    string skippedNote;
    readonly List<Dictionary<string, object>> history = new(); // running tour: state switches (dance time, measure)
    public float OverheadFov = 24f;

    public View Current => current;
    public bool Running => running;
    public bool Active => current != View.None;
    public string DirectorState => Name(current);
    public int Shot => shot;
    public float ShotBlendSeconds => shotBlend;

    DanceLayers Layers => DanceLayers.Instance;
    HeadMovement Hm => Layers != null ? Layers.Head : HeadMovement.Instance;

    public static string Name(View v) => v switch
    {
        View.Orbit => "orbit",
        View.CameraTour => "camera_tour",
        View.Overhead => "overhead",
        View.Geometry => "geometry",
        View.Physics => "physics",
        View.DanceGraph => "dance_graph",
        View.Fingerprint => "fingerprint",
        _ => "none"
    };

    public static View Parse(string name)
    {
        string n = (name ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return n switch
        {
            "orbit" => View.Orbit,
            "camera_tour" or "cameras" => View.CameraTour,
            "overhead" or "floor_craft" or "overhead_floor_craft" => View.Overhead,
            "geometry" => View.Geometry,
            "physics" => View.Physics,
            "dance_graph" or "graph" or "path" => View.DanceGraph,
            "fingerprint" => View.Fingerprint,
            "none" or "off" => View.None,
            _ => throw new ArgumentException($"unknown view state '{name}' (orbit|camera_tour|overhead|geometry|physics|dance_graph|fingerprint)")
        };
    }

    // ---------------------------------------------------------------- commands

    /// <summary>hm_tour start: play the dance from measure 1 and walk the state list</summary>
    public string StartTour(int measures = 0, string from = null, bool restart = true)
    {
        HeadMovement hm = Hm;
        if (hm == null || !hm.AudioLoaded) throw new InvalidOperationException("load a capture first (hm_load) and wait for audioLoaded");
        if (measures > 0) MeasuresPerState = measures;
        running = true;
        int start = 0;
        if (!string.IsNullOrEmpty(from)) start = Math.Max(0, Array.IndexOf(Sequence, Parse(from)));
        if (restart) hm.Restart();
        hm.Play();
        history.Clear();
        sequenceIndex = start - 1;
        Advance(true);
        return Status();
    }

    /// <summary>hm_tour goto / hm_graph: show one state and hold it (no auto-advance)</summary>
    public string Hold(View view)
    {
        running = false;
        if (view == View.None)
        {
            Stop();
            return Status();
        }

        if (view == View.CameraTour && !HasCameraVideos())
        {
            throw new ArgumentException("camera tour: no source videos exported for this capture (slot kept for later)");
        }

        sequenceIndex = Array.IndexOf(Sequence, view);
        Enter(view);
        return Status();
    }

    public string Next()
    {
        if (current == View.None) return StartTour(restart: false);
        Advance(true);
        return Status();
    }

    public void Stop()
    {
        bool wasActive = current != View.None;
        running = false;
        current = previous = View.None;
        sequenceIndex = -1;
        if (!wasActive) return;
        HeadMovement hm = Hm;
        foreach (KeyValuePair<string, bool> kv in savedHeadLayers)
        {
            try
            {
                hm?.SetLayerVisible(kv.Key, kv.Value);
            }
            catch (ArgumentException)
            {
            }
        }

        savedHeadLayers.Clear();
        hm?.SetDirectorAvatarOpacity(null); // back to the user default
        foreach (KeyValuePair<LineRenderer, float> kv in savedWidth)
        {
            if (kv.Key == null) continue;
            kv.Key.widthMultiplier = kv.Value;
            kv.Key.enabled = true;
        }

        savedWidth.Clear();
        avatarAlphaNow = -1;
        opacityBase = -1;
        skeletonNow = 1;
        if (Layers != null)
        {
            Layers.CounterbalanceOverride = null;
            Layers.TracesOverride = null;
            Layers.NeckOverride = null;
            Layers.GraphOverride = null;
            Layers.MovesOverride = null;
            Layers.InsetOverride = null;
            Layers.ShowAllPivots = false;
            Layers.ApplyVisibility();
            if (Layers.Graph != null) Layers.Graph.SetFade(1f);
            Layers.Hud.SetTour(null);
        }

        chaseValid = false;
        CameraControl rig = Rig();
        if (rig != null && rig.Mode == CameraControl.Owner.Director) rig.TakeControl(); // free-fly from where it is
    }

    /// <summary>a new capture was loaded: the old avatars/skeletons are gone - forget their saved values and re-enter</summary>
    public void OnCaptureLoaded()
    {
        savedWidth.Clear();
        contactLines = null;
        avatarAlphaNow = -1;
        skeletonNow = 1;
        chaseValid = false;
        if (current != View.None)
        {
            View v = current;
            current = View.None;
            Enter(v, false, false); // the camera keeps its owner (the rig snaps on the new capture)
        }
    }

    /// <param name="grabCamera">explicit commands take the camera; the running tour's measure-boundary advance does not</param>
    void Advance(bool grabCamera)
    {
        for (int guard = 0; guard < Sequence.Length + 1; guard++)
        {
            sequenceIndex = (sequenceIndex + 1) % Sequence.Length;
            View v = Sequence[sequenceIndex];
            if (v == View.CameraTour && !HasCameraVideos())
            {
                skippedNote = "camera tour skipped (no source videos exported for this capture)";
                continue;
            }

            if ((v is View.DanceGraph or View.Fingerprint) && Layers?.Graph == null) continue; // no moves/ in this capture

            Enter(v, true, grabCamera);
            if (running) Record(v);
            return;
        }
    }

    void Record(View v)
    {
        HeadMovement hm = Hm;
        if (hm?.Timeline == null || hm.CurrentFrame < 0) return;
        float t = hm.Timeline.AudioTimeOf(hm.CurrentFrame);
        Dictionary<string, object> e = new() { ["state"] = Name(v), ["time"] = t };
        if (hm.Beats != null)
        {
            int m = hm.Beats.MeasureIndex(t);
            e["measure"] = m + 1;
            e["measureStart"] = hm.Beats.MeasureStart(m);
        }

        if (history.Count >= 64) history.RemoveAt(0);
        history.Add(e);
    }

    bool HasCameraVideos()
    {
        CaptureManifest m = Hm?.Manifest;
        if (m == null) return false;
        string dir = Path.Combine(m.Folder, "cameras");
        return Directory.Exists(dir) && Directory.GetFiles(dir, "*.mp4", SearchOption.AllDirectories).Length > 0;
    }

    // ---------------------------------------------------------------- state parameters

    void Enter(View view, bool blend = true, bool grabCamera = true)
    {
        HeadMovement hm = Hm;
        DanceLayers layers = Layers;
        if (hm == null || layers == null) return;
        if (savedHeadLayers.Count == 0) SaveHeadLayers(hm);
        bool newShot = view != current || !blend; // re-entering the shown state keeps its camera (no restart, no pop)
        previous = blend ? current : view;
        current = view;
        stateStart = Time.time;
        blendStart = blend ? Time.time : -10f;
        measuresInState = 0;
        lastMeasure = -1;
        if (view == View.DanceGraph && previous != View.DanceGraph) chaseValid = false;
        if (newShot)
        {
            // the camera: a new shot that the rig blends into from whatever is on screen (or cuts to: capture load)
            phase = Time.time;
            shot++;
            shotBlend = blend ? BlendSeconds : 0f;
        }

        bool graphView = view == View.DanceGraph || view == View.Fingerprint;
        // layers of the full-size dance (§4 table); the graph states fade the dance out
        SetHead(hm, "floor", view is View.Orbit or View.Overhead);
        SetHead(hm, "timing", view is View.Orbit or View.Overhead);
        SetHead(hm, "physics", view == View.Physics);
        SetHead(hm, "tension", view is View.Orbit or View.Physics);
        SetHead(hm, "avatars", true);
        SetHead(hm, "hud", false);
        layers.CounterbalanceOverride = !graphView;
        layers.TracesOverride = view == View.Geometry ? true : graphView ? false : (bool?)null; // on by default (free extremities only)
        layers.NeckOverride = graphView ? false : null; // the neck axis follows its layer flag; never over the graph
        layers.ShowAllPivots = view == View.Overhead;
        layers.GraphOverride = view == View.DanceGraph ? DanceGraphLayer.Mode.Path
            : view == View.Fingerprint ? DanceGraphLayer.Mode.Fingerprint
            : DanceGraphLayer.Mode.Off;
        layers.MovesOverride = true; // the move caption throughout directed playback (VIEWER_SPEC 3.12)
        layers.InsetOverride = !graphView; // the graph inset; hidden where the full graph is on screen
        layers.ApplyVisibility();

        avatarAlphaFrom = avatarAlphaNow < 0 ? hm.EffectiveAvatarOpacity : avatarAlphaNow;
        opacityBase = hm.AvatarOpacity;
        avatarAlphaTo = opacityBase * OpacityFactor(view);
        skeletonFrom = skeletonNow;
        skeletonTo = graphView ? 0f : 1f;
        graphFrom = previous is View.DanceGraph or View.Fingerprint ? 1f : 0f;
        graphTo = graphView ? 1f : 0f;
        if (!blend)
        {
            avatarAlphaFrom = avatarAlphaTo;
            skeletonFrom = skeletonTo;
            graphFrom = graphTo;
        }

        ApplyBlend(blend ? 0f : 1f);
        if (grabCamera) Rig()?.UseDirector(blend);
        UpdateTourLabel();
    }

    /// <summary>the desktop camera rig (null in VR, where the director never moves the user); registers this tour as
    /// its director</summary>
    CameraControl Rig()
    {
        CameraControl rig = Hm != null ? Hm.OrbitCamera : null;
        if (rig == null) return null;
        if (!ReferenceEquals(rig.Director, this)) rig.Director = this;
        return rig;
    }

    void OnDisable()
    {
        CameraControl rig = HeadMovement.Instance != null ? HeadMovement.Instance.OrbitCamera : null;
        if (rig != null && ReferenceEquals(rig.Director, this)) rig.Director = null;
    }

    void SaveHeadLayers(HeadMovement hm)
    {
        savedHeadLayers.Clear();
        foreach (string layer in HeadLayers) savedHeadLayers[layer] = hm.LayerVisible(layer);
    }

    static void SetHead(HeadMovement hm, string layer, bool on)
    {
        if (hm.LayerVisible(layer) != on) hm.SetLayerVisible(layer, on);
    }

    void ApplyBlend(float k)
    {
        HeadMovement hm = Hm;
        if (hm == null) return;
        float s = Mathf.SmoothStep(0f, 1f, k);
        float alpha = Mathf.Lerp(avatarAlphaFrom, avatarAlphaTo, s);
        float skel = Mathf.Lerp(skeletonFrom, skeletonTo, s);
        float g = Mathf.Lerp(graphFrom, graphTo, s);
        if (Mathf.Abs(alpha - avatarAlphaNow) > 1e-3f) SetAvatarAlpha(hm, alpha);
        if (Mathf.Abs(skel - skeletonNow) > 1e-3f) SetSkeletonWidth(hm, skel);
        graphNow = g;
        DanceLayers layers = Layers;
        if (layers?.Graph != null) layers.Graph.SetFade(Mathf.Max(0.02f, g));
    }

    void SetAvatarAlpha(HeadMovement hm, float alpha)
    {
        avatarAlphaNow = alpha;
        hm.SetDirectorAvatarOpacity(alpha); // SmplxAvatar.Opacity; the body hides itself at 0 (hair and shoes follow)
    }

    void SetSkeletonWidth(HeadMovement hm, float k)
    {
        skeletonNow = k;
        WidthOf(hm.LeadDancer, k);
        WidthOf(hm.FollowDancer, k);
        // ContactDetection's contact lines belong to the full-size dance too (root objects, so not found above)
        if (contactLines == null)
        {
            contactLines = new List<LineRenderer>();
            ContactDetection contacts = hm.GetComponent<ContactDetection>();
            if (contacts != null)
            {
                foreach (System.Reflection.FieldInfo field in typeof(ContactDetection).GetFields(
                             System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
                {
                    if (field.FieldType == typeof(LineRenderer) && field.GetValue(contacts) is LineRenderer l && l != null) contactLines.Add(l);
                }
            }
        }

        foreach (LineRenderer line in contactLines)
        {
            if (line == null) continue;
            if (!savedWidth.TryGetValue(line, out float w))
            {
                w = line.widthMultiplier;
                savedWidth[line] = w;
            }

            line.widthMultiplier = w * k;
            line.enabled = k > 0.01f;
        }
    }

    void WidthOf(Dancer d, float k)
    {
        if (d == null) return;
        d.GetComponentsInChildren(true, lineBuffer);
        foreach (LineRenderer line in lineBuffer)
        {
            if (!savedWidth.TryGetValue(line, out float w))
            {
                w = line.widthMultiplier;
                savedWidth[line] = w;
            }

            line.widthMultiplier = w * k;
            line.enabled = k > 0.01f;
        }
    }

    void UpdateTourLabel()
    {
        DanceLayers layers = Layers;
        if (layers == null) return;
        if (current == View.None)
        {
            layers.Hud.SetTour(null);
            return;
        }

        string label = current switch
        {
            View.Orbit => "Orbit",
            View.CameraTour => "Camera tour",
            View.Overhead => "Overhead floor craft",
            View.Geometry => "Geometry",
            View.Physics => "Physics",
            View.DanceGraph => "Dance graph",
            View.Fingerprint => "Fingerprint",
            _ => ""
        };
        layers.Hud.SetTour(running ? $"Tour  {Array.IndexOf(Sequence, current) + 1}/{Sequence.Length}  {label}" : label);
    }

    // ---------------------------------------------------------------- per frame

    void LateUpdate()
    {
        if (current == View.None) return;
        HeadMovement hm = Hm;
        DanceLayers layers = Layers;
        // (a script reload in Play mode leaves HeadMovement without its non-serialised state: do nothing)
        if (hm == null || layers == null || !hm.AudioLoaded || hm.Timeline == null || hm.LeadDancer == null || hm.FollowDancer == null) return;

        if (opacityBase >= 0 && Mathf.Abs(opacityBase - hm.AvatarOpacity) > 1e-4f)
        {
            // hm_opacity changed the user default while a state is shown: re-target from where the blend is
            opacityBase = hm.AvatarOpacity;
            avatarAlphaFrom = avatarAlphaNow < 0 ? hm.EffectiveAvatarOpacity : avatarAlphaNow;
            avatarAlphaTo = opacityBase * OpacityFactor(current);
            if (Time.time - blendStart >= BlendSeconds) avatarAlphaFrom = avatarAlphaTo;
        }

        float k = BlendSeconds > 0 ? Mathf.Clamp01((Time.time - blendStart) / BlendSeconds) : 1f;
        if (k < 1f || Mathf.Abs(graphNow - graphTo) > 1e-3f || Mathf.Abs(skeletonNow - skeletonTo) > 1e-3f ||
            Mathf.Abs(avatarAlphaNow - avatarAlphaTo) > 1e-3f) ApplyBlend(k);
        if (running) TickTour(hm);
        Rig(); // keep the registration (the rig asks TryGetCameraPose in its own LateUpdate)
    }

    void TickTour(HeadMovement hm)
    {
        CaptureTimeline timeline = hm.Timeline;
        if (timeline == null) return;
        int frame = hm.CurrentFrame;
        if (!hm.IsPlaying)
        {
            // the take ended: loop it (a user pause in the middle is respected)
            if (frame >= timeline.Count - 2)
            {
                hm.Restart();
                hm.Play();
            }

            return;
        }

        BeatGrid beats = hm.Beats;
        if (beats == null)
        {
            if (Time.time - stateStart > 6f) Advance(false);
            return;
        }

        int m = beats.MeasureIndex(timeline.AudioTimeOf(frame));
        if (lastMeasure < 0)
        {
            lastMeasure = m;
            return;
        }

        if (m != lastMeasure)
        {
            measuresInState++;
            lastMeasure = m;
        }

        int length = current == View.DanceGraph ? GraphMeasures : MeasuresPerState;
        if (measuresInState >= length) Advance(false);
    }

    /// <summary>ICameraDirector: the camera pose of the current state only (the rig blends shot changes from the pose on
    /// screen). Eye heights depend on the state only; the follow states orbit the rig's damped XZ anchors.</summary>
    public bool TryGetCameraPose(in CameraFollow follow, float dt, out Vector3 eye, out Vector3 look, out float fov)
    {
        eye = look = Vector3.zero;
        fov = float.NaN;
        if (current == View.None) return false;
        HeadMovement hm = Hm;
        DanceLayers layers = Layers;
        if (hm == null || layers == null || !hm.AudioLoaded || hm.Timeline == null || hm.LeadDancer == null || hm.FollowDancer == null) return false;
        int frame = hm.CurrentFrame;
        if (frame < 0) return false;
        float t = hm.Timeline.AudioTimeOf(frame);
        CameraFollow f = follow;
        if (!f.Valid)
        {
            Vector3 c = (hm.LeadDancer.Joint(frame, SmplJoint.Pelvis) + hm.FollowDancer.Joint(frame, SmplJoint.Pelvis)) * 0.5f;
            f.Raw = f.LookAnchor = f.EyeAnchor = new Vector2(c.x, c.z);
        }

        Pose(current, layers, t, f, dt, out eye, out look, out fov);
        return true;
    }

    static Vector3 At(Vector2 xz, float y) => new(xz.x, y, xz.y);

    static Vector3 Spherical(Vector3 target, float azimuthDeg, float elevationDeg, float radius)
    {
        float az = azimuthDeg * Mathf.Deg2Rad, el = elevationDeg * Mathf.Deg2Rad;
        return target + new Vector3(Mathf.Cos(el) * Mathf.Cos(az), Mathf.Sin(el), Mathf.Cos(el) * Mathf.Sin(az)) * radius;
    }

    /// <summary>one state's camera: look point on the look anchor at the state's height, eye on a sphere around the eye
    /// anchor at the same height (so the eye height is the state's, whatever the dancers do)</summary>
    void Pose(View view, DanceLayers layers, float t, in CameraFollow f, float dt, out Vector3 eye, out Vector3 look, out float fov)
    {
        float since = Time.time - phase;
        fov = float.NaN;
        switch (view)
        {
            case View.Overhead:
            {
                Rect area = layers.DanceArea;
                look = new Vector3(area.center.x, 0f, area.center.y);
                Camera cam = DanceText.ViewCamera;
                float vfov = OverheadFov * Mathf.Deg2Rad;
                float aspect = cam != null ? cam.aspect : 16f / 9f;
                float halfZ = area.height * 0.5f + 0.6f, halfX = area.width * 0.5f + 0.6f;
                float dist = Mathf.Max(halfZ / Mathf.Tan(vfov * 0.5f), halfX / (Mathf.Tan(vfov * 0.5f) * aspect)) + 0.4f;
                eye = Spherical(look, -90f, 88.5f, Mathf.Clamp(dist, 2f, 9.5f)); // north (+Z) up
                fov = OverheadFov;
                return;
            }
            case View.Geometry:
                look = At(f.LookAnchor, 0.85f);
                eye = Spherical(At(f.EyeAnchor, 0.85f), 35f + since * 9f, 12f, 2.9f);
                return;
            case View.Physics:
                look = At(f.LookAnchor, 0.90f);
                eye = Spherical(At(f.EyeAnchor, 0.90f), 40f, 15f, 3.3f);
                return;
            case View.DanceGraph when layers.Graph != null && layers.Graph.HasPath:
            {
                layers.Graph.Chase(t, out Vector3 e, out Vector3 l);
                HeadMovement hm = Hm;
                bool exact = !chaseValid || f.Cut || hm == null || !hm.IsPlaying || dt <= 0f;
                if (exact)
                {
                    chaseEye = e;
                    chaseLook = l;
                    chaseEyeVel = chaseLookVel = Vector3.zero;
                }
                else
                {
                    chaseEye = SmoothXZY(chaseEye, e, ref chaseEyeVel, dt);
                    chaseLook = SmoothXZY(chaseLook, l, ref chaseLookVel, dt);
                }

                chaseValid = true;
                eye = chaseEye;
                look = chaseLook;
                return;
            }
            case View.Fingerprint when layers.Graph != null:
            {
                layers.Graph.Bounds(out Vector3 centre, out float radius);
                eye = Spherical(centre, -60f + since * 6f, 22f, Mathf.Clamp(radius * 1.3f, 3f, 9.5f));
                // pan so the graph sits left of the side panel
                Vector3 right = Vector3.Cross(Vector3.up, centre - eye).normalized;
                Vector3 pan = right * (radius * 0.28f);
                look = centre + pan;
                eye += pan;
                return;
            }
            default: // Orbit (and the camera-tour slot until it exists): ~1 rev / 20 s, gentle height drift
                look = At(f.LookAnchor, 0.95f);
                eye = Spherical(At(f.EyeAnchor, 0.95f), -70f + since * 18f, 16f + 5f * Mathf.Sin(since * 2f * Mathf.PI / 30f), 3.1f);
                return;
        }
    }

    /// <summary>graph chase smoothing: XZ with a 0.5 s critically damped spring, Y with 1.0 s</summary>
    static Vector3 SmoothXZY(Vector3 from, Vector3 to, ref Vector3 vel, float dt)
    {
        Vector2 vxz = new(vel.x, vel.z);
        Vector2 xz = Vector2.SmoothDamp(new Vector2(from.x, from.z), new Vector2(to.x, to.z), ref vxz, 0.5f, 20f, dt);
        float vy = vel.y;
        float y = Mathf.SmoothDamp(from.y, to.y, ref vy, 1.0f, 20f, dt);
        vel = new Vector3(vxz.x, vy, vxz.y);
        return new Vector3(xz.x, y, xz.y);
    }

    // ---------------------------------------------------------------- hm_state / hm_tour status

    public Dictionary<string, object> State()
    {
        return new Dictionary<string, object>
        {
            ["state"] = Name(current), ["running"] = running, ["holding"] = current != View.None && !running,
            ["index"] = sequenceIndex, ["sequence"] = Sequence.Select(Name).ToList(),
            ["measuresInState"] = measuresInState,
            ["measuresPerState"] = current == View.DanceGraph ? GraphMeasures : MeasuresPerState,
            ["blend"] = BlendSeconds > 0 ? Mathf.Clamp01((Time.time - blendStart) / BlendSeconds) : 1f, ["shot"] = shot,
            ["avatarAlpha"] = avatarAlphaNow, ["skeleton"] = skeletonNow, ["graphFade"] = graphNow,
            ["cameraDriven"] = current != View.None && Hm != null && Hm.OrbitCamera != null &&
                                Hm.OrbitCamera.enabled && Hm.OrbitCamera.Mode == CameraControl.Owner.Director,
            ["avatarOpacityBase"] = opacityBase, ["note"] = skippedNote,
            ["cameraTourAvailable"] = HasCameraVideos(), ["history"] = history.ToList()
        };
    }

    public string Status() => Newtonsoft.Json.JsonConvert.SerializeObject(State());
}
