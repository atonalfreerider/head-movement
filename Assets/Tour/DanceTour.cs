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
/// and blends avatar opacity, skeleton fade, graph fade and the camera over BlendSeconds. The camera is driven
/// through the existing desktop CameraControl (Center + SetOrbit) in LateUpdate; in VR (no CameraControl) the
/// states only change layers - the director never moves the user. Stop() restores every layer it touched.
/// </summary>
[DefaultExecutionOrder(-10)] // camera first, so the view-facing overlay strips use this frame's camera
public class DanceTour : MonoBehaviour
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

    View current = View.None, previous = View.None;
    bool running; // auto-advance
    int sequenceIndex = -1;
    int measuresInState;
    int lastMeasure = -1;
    float stateStart, blendStart = -10f;
    float orbitPhase;
    readonly Dictionary<string, bool> savedHeadLayers = new();
    readonly Dictionary<Material, float> savedAlpha = new();
    readonly Dictionary<LineRenderer, float> savedWidth = new();
    readonly List<LineRenderer> lineBuffer = new();
    List<LineRenderer> contactLines;
    float avatarAlphaFrom = -1, avatarAlphaTo = -1, skeletonFrom = 1, skeletonTo = 1, graphFrom, graphTo;
    float avatarAlphaNow = -1, skeletonNow = 1, graphNow;
    bool cameraOwned;
    Vector3 eyeSmooth, targetSmooth;
    bool smoothValid;
    float lastFrameTime = float.NaN;
    string skippedNote;
    float baseFov = -1f;
    readonly List<Dictionary<string, object>> history = new(); // running tour: state switches (dance time, measure)
    public float OverheadFov = 24f;

    public View Current => current;
    public bool Running => running;
    public bool Active => current != View.None;

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
        Advance();
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
        Advance();
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
        foreach (KeyValuePair<Material, float> kv in savedAlpha)
        {
            if (kv.Key == null) continue;
            Color c = kv.Key.GetColor("_BaseColor");
            c.a = kv.Value;
            kv.Key.SetColor("_BaseColor", c);
        }

        savedAlpha.Clear();
        foreach (KeyValuePair<LineRenderer, float> kv in savedWidth)
        {
            if (kv.Key == null) continue;
            kv.Key.widthMultiplier = kv.Value;
            kv.Key.enabled = true;
        }

        savedWidth.Clear();
        avatarAlphaNow = -1;
        skeletonNow = 1;
        if (Layers != null)
        {
            Layers.CounterbalanceOverride = null;
            Layers.TracesOverride = null;
            Layers.GraphOverride = null;
            Layers.ShowAllPivots = false;
            Layers.ApplyVisibility();
            if (Layers.Graph != null) Layers.Graph.SetFade(1f);
            Layers.Hud.SetTour(null);
        }

        cameraOwned = false;
        smoothValid = false;
        Camera cam = DanceText.ViewCamera;
        if (cam != null && baseFov > 0) cam.fieldOfView = baseFov;
        baseFov = -1f;
    }

    /// <summary>a new capture was loaded: the old avatars/skeletons are gone - forget their saved values and re-enter</summary>
    public void OnCaptureLoaded()
    {
        savedAlpha.Clear();
        savedWidth.Clear();
        contactLines = null;
        avatarAlphaNow = -1;
        skeletonNow = 1;
        smoothValid = false;
        if (current != View.None)
        {
            View v = current;
            current = View.None;
            Enter(v, false);
        }
    }

    void Advance()
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

            Enter(v);
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

    void Enter(View view, bool blend = true)
    {
        HeadMovement hm = Hm;
        DanceLayers layers = Layers;
        if (hm == null || layers == null) return;
        if (savedHeadLayers.Count == 0) SaveHeadLayers(hm);
        previous = blend ? current : view;
        current = view;
        stateStart = Time.time;
        blendStart = blend ? Time.time : -10f;
        measuresInState = 0;
        lastMeasure = -1;
        smoothValid = smoothValid && blend;
        if (view == View.Orbit || view == View.Geometry) orbitPhase = Time.time;

        bool graphView = view == View.DanceGraph || view == View.Fingerprint;
        // layers of the full-size dance (§4 table); the graph states fade the dance out
        SetHead(hm, "floor", view is View.Orbit or View.Overhead);
        SetHead(hm, "timing", view is View.Orbit or View.Overhead);
        SetHead(hm, "physics", view == View.Physics);
        SetHead(hm, "tension", view is View.Orbit or View.Physics);
        SetHead(hm, "avatars", true);
        SetHead(hm, "hud", false);
        layers.CounterbalanceOverride = !graphView;
        layers.TracesOverride = view == View.Geometry;
        layers.ShowAllPivots = view == View.Overhead;
        layers.GraphOverride = view == View.DanceGraph ? DanceGraphLayer.Mode.Path
            : view == View.Fingerprint ? DanceGraphLayer.Mode.Fingerprint
            : DanceGraphLayer.Mode.Off;
        layers.ApplyVisibility();

        avatarAlphaFrom = avatarAlphaNow < 0 ? CurrentAvatarAlpha(hm) : avatarAlphaNow;
        avatarAlphaTo = view switch
        {
            View.Orbit or View.CameraTour => 0.3f,
            View.Overhead => 0.2f,
            View.Geometry => 0.15f,
            View.Physics => 0.1f,
            _ => 0f
        };
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
        cameraOwned = true;
        UpdateTourLabel();
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

    float CurrentAvatarAlpha(HeadMovement hm)
    {
        foreach (SmplxAvatar a in hm.Avatars.Values)
        {
            Renderer r = a != null ? a.GetComponent<Renderer>() : null;
            if (r != null && r.sharedMaterial != null && r.sharedMaterial.HasProperty("_BaseColor")) return r.sharedMaterial.GetColor("_BaseColor").a;
        }

        return 0.6f;
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
        foreach (SmplxAvatar a in hm.Avatars.Values)
        {
            if (a == null) continue;
            Renderer r = a.GetComponent<Renderer>();
            if (r == null) continue;
            Material m = r.sharedMaterial;
            if (m == null || !m.HasProperty("_BaseColor")) continue;
            Color c = m.GetColor("_BaseColor");
            if (!savedAlpha.ContainsKey(m)) savedAlpha[m] = c.a;
            c.a = alpha;
            m.SetColor("_BaseColor", c);
            if (r.enabled != alpha > 0.005f && hm.LayerVisible("avatars")) r.enabled = alpha > 0.005f;
        }
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

        float k = BlendSeconds > 0 ? Mathf.Clamp01((Time.time - blendStart) / BlendSeconds) : 1f;
        if (k < 1f || Mathf.Abs(graphNow - graphTo) > 1e-3f || Mathf.Abs(skeletonNow - skeletonTo) > 1e-3f) ApplyBlend(k);
        if (running) TickTour(hm);
        if (cameraOwned) DriveCamera(hm, layers, k);
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
            if (Time.time - stateStart > 6f) Advance();
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
        if (measuresInState >= length) Advance();
    }

    void DriveCamera(HeadMovement hm, DanceLayers layers, float blend)
    {
        CameraControl control = hm.OrbitCamera;
        if (control == null || !control.gameObject.activeInHierarchy) return; // VR: never move the user
        int frame = hm.CurrentFrame;
        if (frame < 0) return;
        float t = hm.Timeline.AudioTimeOf(frame);
        Pose(current, hm, layers, t, frame, out Vector3 eye, out Vector3 target);
        if (blend < 1f && previous != View.None && previous != current)
        {
            Pose(previous, hm, layers, t, frame, out Vector3 eye0, out Vector3 target0);
            float s = Mathf.SmoothStep(0f, 1f, blend);
            eye = Vector3.Lerp(eye0, eye, s);
            target = Vector3.Lerp(target0, target, s);
        }

        // smooth while playing (pose jitter, chase direction changes); exact when paused (screenshots, playtests)
        bool jumped = float.IsNaN(lastFrameTime) || Mathf.Abs(t - lastFrameTime) > 0.5f;
        lastFrameTime = t;
        if (hm.IsPlaying && smoothValid && !jumped)
        {
            float tau = current == View.DanceGraph ? 0.35f : 0.2f;
            float a = 1f - Mathf.Exp(-Time.deltaTime / tau);
            eyeSmooth = Vector3.Lerp(eyeSmooth, eye, a);
            targetSmooth = Vector3.Lerp(targetSmooth, target, a);
        }
        else
        {
            eyeSmooth = eye;
            targetSmooth = target;
        }

        smoothValid = true;
        Camera cam = DanceText.ViewCamera;
        if (cam != null)
        {
            if (baseFov < 0) baseFov = cam.fieldOfView;
            float fov = Fov(current);
            if (blend < 1f && previous != View.None) fov = Mathf.Lerp(Fov(previous), fov, Mathf.SmoothStep(0f, 1f, blend));
            if (Mathf.Abs(cam.fieldOfView - fov) > 0.01f) cam.fieldOfView = fov;
        }

        Vector3 d = eyeSmooth - targetSmooth;
        float r = Mathf.Max(0.3f, d.magnitude);
        float polar = Mathf.Acos(Mathf.Clamp(d.y / r, -1f, 1f));
        float azimuth = Mathf.Atan2(d.z, d.x);
        control.Center = targetSmooth;
        control.SetOrbit(azimuth, polar, r);
    }

    float Fov(View v) => v == View.Overhead ? OverheadFov : baseFov > 0 ? baseFov : 60f;

    static Vector3 CoupleCentre(HeadMovement hm, int frame)
    {
        Vector3 c = (hm.LeadDancer.Joint(frame, SmplJoint.Pelvis) + hm.FollowDancer.Joint(frame, SmplJoint.Pelvis)) * 0.5f;
        return new Vector3(c.x, 0.95f, c.z);
    }

    static Vector3 Spherical(Vector3 target, float azimuthDeg, float elevationDeg, float radius)
    {
        float az = azimuthDeg * Mathf.Deg2Rad, el = elevationDeg * Mathf.Deg2Rad;
        return target + new Vector3(Mathf.Cos(el) * Mathf.Cos(az), Mathf.Sin(el), Mathf.Cos(el) * Mathf.Sin(az)) * radius;
    }

    void Pose(View view, HeadMovement hm, DanceLayers layers, float t, int frame, out Vector3 eye, out Vector3 target)
    {
        float since = Time.time - orbitPhase;
        switch (view)
        {
            case View.Overhead:
            {
                Rect area = layers.DanceArea;
                target = new Vector3(area.center.x, 0f, area.center.y);
                Camera cam = DanceText.ViewCamera;
                float vfov = OverheadFov * Mathf.Deg2Rad;
                float aspect = cam != null ? cam.aspect : 16f / 9f;
                float halfZ = area.height * 0.5f + 0.6f, halfX = area.width * 0.5f + 0.6f;
                float dist = Mathf.Max(halfZ / Mathf.Tan(vfov * 0.5f), halfX / (Mathf.Tan(vfov * 0.5f) * aspect)) + 0.4f;
                eye = Spherical(target, -90f, 88.5f, Mathf.Clamp(dist, 2f, 9.5f)); // north (+Z) up
                return;
            }
            case View.Geometry:
                target = CoupleCentre(hm, frame) + Vector3.down * 0.1f;
                eye = Spherical(target, 35f + since * 360f / 40f, 12f, 2.9f);
                return;
            case View.Physics:
                target = CoupleCentre(hm, frame) + Vector3.down * 0.05f;
                eye = Spherical(target, 40f, 15f, 3.3f);
                return;
            case View.DanceGraph when layers.Graph != null && layers.Graph.HasPath:
                layers.Graph.Chase(t, out eye, out target);
                return;
            case View.Fingerprint when layers.Graph != null:
            {
                layers.Graph.Bounds(out Vector3 centre, out float radius);
                eye = Spherical(centre, -60f + since * 360f / 60f, 22f, Mathf.Clamp(radius * 1.3f, 3f, 9.5f));
                // pan so the graph sits left of the side panel
                Vector3 right = Vector3.Cross(Vector3.up, centre - eye).normalized;
                Vector3 pan = right * (radius * 0.28f);
                target = centre + pan;
                eye += pan;
                return;
            }
            default: // Orbit (and the camera-tour slot until it exists)
                target = CoupleCentre(hm, frame);
                eye = Spherical(target, -70f + since * 360f / 20f, 16f + 5f * Mathf.Sin(since * 2f * Mathf.PI / 30f), 3.1f);
                return;
        }
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
            ["blend"] = BlendSeconds > 0 ? Mathf.Clamp01((Time.time - blendStart) / BlendSeconds) : 1f,
            ["avatarAlpha"] = avatarAlphaNow, ["skeleton"] = skeletonNow, ["graphFade"] = graphNow,
            ["cameraDriven"] = cameraOwned && current != View.None, ["note"] = skippedNote,
            ["cameraTourAvailable"] = HasCameraVideos(), ["history"] = history.ToList()
        };
    }

    public string Status() => Newtonsoft.Json.JsonConvert.SerializeObject(State());
}
