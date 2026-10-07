using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;
using VRTKLite.Controllers;

/// <summary>
/// Playtest surface for the Unity CLI (unity command hm_*). Each command calls the same public methods the
/// keyboard/VR bindings use, so a scripted playtest exercises the real code paths. Requires Play mode.
///
///   unity command hm_load --name 00_SyntheticDemo
///   unity command hm_transport --action play
///   unity command hm_transport --action seek --time 3.5
///   unity command hm_layer --layer tension --visible false
///   unity command hm_orbit --azimuth 45 --elevation 70 --radius 3
///   unity command hm_orbit --height 1.6 --look 0.95 --distance 2.9
///   unity command hm_opacity --value 0.35
///   unity command hm_camtrace --action start      (then hm_camtrace --action stop --path trace.csv)
///   unity command hm_state
/// </summary>
public static class HeadMovementCliCommands
{
    static HeadMovement Require()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("enter Play mode first (unity command editor_play)");
        if (HeadMovement.Instance == null) throw new InvalidOperationException("no HeadMovement in the open scene (open Assets/head-movement.unity)");
        return HeadMovement.Instance;
    }

    [CliCommand("hm_state", "HeadMovement playtest state as JSON: capture, frame, playback, layers, step timing, active partner connections")]
    public static string State() => JsonConvert.SerializeObject(Require().State());

    [CliCommand("hm_load", "Load a performance from StreamingAssets by folder name or index")]
    public static string Load(
        [CliArg("name", "capture folder name, e.g. 00_SyntheticDemo")] string name = null,
        [CliArg("index", "capture index (alphabetical), used when name is omitted")] int index = 0)
    {
        HeadMovement hm = Require();
        if (!string.IsNullOrEmpty(name))
        {
            if (!hm.LoadCapture(name)) throw new ArgumentException($"no capture '{name}'; have {string.Join(", ", hm.CaptureNames)}");
        }
        else
        {
            hm.LoadCapture(index);
        }

        return "loading (audio loads asynchronously; poll hm_state until audioLoaded=true)";
    }

    [CliCommand("hm_transport", "Lesson transport: play | pause | toggle | restart | beat+ | beat- | measure+ | measure- | measure (--measure N) | loop | loop_on | loop_off | faster | slower | speed (--speed x) | seek (--time s)")]
    public static string Transport(
        [CliArg("action", "play|pause|toggle|restart|beat+|beat-|measure+|measure-|measure|loop|loop_on|loop_off|faster|slower|speed|seek")] string action,
        [CliArg("time", "audio-timeline seconds for seek")] float time = 0f,
        [CliArg("measure", "1-based measure number for action=measure")] int measure = 1,
        [CliArg("speed", "playback speed for action=speed (0.5 | 0.75 | 1)")] float speed = 1f)
    {
        HeadMovement hm = Require();
        switch (action)
        {
            case "measure": hm.GotoMeasure(measure); break;
            case "speed": hm.SetPlaybackSpeed(speed); break;
            case "loop_on": hm.SetLoopMeasure(true); break;
            case "loop_off": hm.SetLoopMeasure(false); break;
            case "play": hm.Play(); break;
            case "pause": hm.Stop(); break;
            case "toggle": hm.TogglePlayPause(); break;
            case "restart": hm.Restart(); break;
            case "beat+": hm.StepBeat(1); break;
            case "beat-": hm.StepBeat(-1); break;
            case "measure+": hm.StepMeasure(1); break;
            case "measure-": hm.StepMeasure(-1); break;
            case "loop": hm.ToggleLoopMeasure(); break;
            case "faster": hm.SpeedUp(); break;
            case "slower": hm.SlowDown(); break;
            case "seek": hm.Seek(time); break;
            default: throw new ArgumentException($"unknown action '{action}'");
        }

        return JsonConvert.SerializeObject(hm.State());
    }

    [CliCommand("hm_layer", "Show or hide a visual layer: floor | tension | splats | room | cameras | hud | avatars | timing | physics | counterbalance | traces | graph")]
    public static string Layer(
        [CliArg("layer", "floor|tension|splats|room|cameras|hud|avatars|timing|physics|counterbalance|traces|graph")] string layer,
        [CliArg("visible", "true to show, false to hide")] bool visible = true)
    {
        Require().SetLayerVisible(layer, visible);
        return $"{layer}={visible}";
    }

    [CliCommand("hm_orbit", "Place the desktop camera (free-fly, VIEWER_SPEC 5.2): azimuth/elevation (deg) and radius (m) around the look point at the look height; or --height (eye y, m), --look (look y, m), --distance (horizontal, m); --mode director hands it to the view-state director (a cut), --mode toggle is the O key (blends). Omitted values keep the current camera; the follow snaps (deterministic stills).")]
    public static string Orbit(
        [CliArg("azimuth", "degrees around the vertical axis (from +x toward +z)")] float azimuth = float.NaN,
        [CliArg("elevation", "degrees above the horizon, seen from the look point")] float elevation = float.NaN,
        [CliArg("radius", "metres from the look point")] float radius = float.NaN,
        [CliArg("height", "eye height above the floor in metres (absolute; stays fixed while the dancers move)")] float height = float.NaN,
        [CliArg("look", "look-at height in metres (default 0.95)")] float look = float.NaN,
        [CliArg("distance", "horizontal distance from the couple in metres")] float distance = float.NaN,
        [CliArg("mode", "free (default) | director | toggle (the O key: director <-> free, blended)")] string mode = "free")
    {
        HeadMovement hm = Require();
        CameraControl control = hm.OrbitCamera;
        if (control == null)
        {
            GameObject simulator = GameObject.Find("Simulator");
            control = simulator != null ? simulator.GetComponent<CameraControl>() : null;
        }

        if (control == null) throw new InvalidOperationException("no Simulator/CameraControl in the scene");
        if (string.Equals(mode, "toggle", StringComparison.OrdinalIgnoreCase))
        {
            hm.ToggleDirector(); // exactly the O key
            return Describe(control);
        }

        if (string.Equals(mode, "director", StringComparison.OrdinalIgnoreCase))
        {
            DanceLayers layers = DanceLayers.Ensure();
            DanceTour tour = layers != null ? layers.Tour : null;
            if (tour == null) throw new InvalidOperationException("no dance layers / tour");
            control.SnapFollow();
            if (tour.Active) control.UseDirector(false);
            else tour.Hold(DanceTour.View.Orbit);
            return Describe(control);
        }

        // precedence: look height first, then the spherical values around it, then absolute height / distance
        if (!float.IsNaN(look)) control.SetFree(lookHeight: look);
        if (!float.IsNaN(azimuth) || !float.IsNaN(elevation) || !float.IsNaN(radius))
        {
            float phi = float.IsNaN(azimuth) ? control.Azimuth : azimuth * Mathf.Deg2Rad;
            float polar = float.IsNaN(elevation) ? control.Polar : (90f - elevation) * Mathf.Deg2Rad;
            control.SetOrbit(phi, polar, float.IsNaN(radius) ? control.Radius : radius);
        }

        // always free + snapped (also when only --mode free was given)
        control.SetFree(distance: distance, eyeHeight: height);
        return Describe(control);
    }

    static string Describe(CameraControl c) =>
        $"azimuth={c.Azimuth * Mathf.Rad2Deg:0} elevation={90f - c.Polar * Mathf.Rad2Deg:0} radius={c.Radius:0.0} " +
        $"height={c.EyeHeight:0.00} look={c.LookHeight:0.00} distance={c.Distance:0.00} mode={(c.Parked ? "parked" : c.Mode.ToString().ToLowerInvariant())}";

    [CliCommand("hm_opacity", "Avatar opacity (VIEWER_SPEC 3.2): --value sets the user default (0..1, view states scale it; hair = avatar + 0.15); no value reports it. --skeletons dimmed (default: each translucent body blends over its own skeleton) | over (the body skips its own skeleton's pixels: raw skeleton colour, VIEWER_SPEC 3.3 option)")]
    public static string Opacity([CliArg("value", "default avatar opacity 0..1")] float value = float.NaN,
        [CliArg("skeletons", "dimmed (default) | over: whether a translucent body blends over its own skeleton")] string skeletons = null)
    {
        HeadMovement hm = Require();
        if (!float.IsNaN(value)) hm.SetAvatarOpacity(value);
        if (!string.IsNullOrEmpty(skeletons))
        {
            SmplxAvatar.SkeletonsOverBodies = skeletons.ToLowerInvariant() switch
            {
                "over" or "true" or "on" => true,
                "dimmed" or "false" or "off" => false,
                _ => throw new ArgumentException($"--skeletons over|dimmed, not '{skeletons}'")
            };
        }
        Dictionary<string, object> avatars = new();
        foreach (KeyValuePair<Role, SmplxAvatar> kv in hm.Avatars)
        {
            avatars[kv.Key.ToString().ToLowerInvariant()] = new Dictionary<string, object>
            {
                ["opacity"] = kv.Value.Opacity, ["visible"] = kv.Value.Visible, ["feetHidden"] = kv.Value.FeetHidden,
                ["queueOffset"] = kv.Value.QueueOffset, ["queues"] = kv.Value.SortedQueues(),
                ["skeletonOverBody"] = kv.Value.SkeletonOverBody, ["skeletonStencilBit"] = kv.Value.SkeletonStencilBit
            };
        }

        return JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["avatarOpacity"] = hm.AvatarOpacity, ["effective"] = hm.EffectiveAvatarOpacity, ["sortBackToFront"] = SmplxAvatar.SortBackToFront,
            ["skeletonsOverBodies"] = SmplxAvatar.SkeletonsOverBodies, ["avatars"] = avatars
        });
    }

    static List<CameraControl.TraceSample> lastTrace = new();

    [CliCommand("hm_camtrace", "Per-frame trace of the rendered desktop camera (VIEWER_SPEC 5.2): start | stop (--path csv) | report. stop/report count one-frame jumps: a 'jump' is a frame that is not a cut (seek, loop wrap, load, hm_orbit) whose eye or look point moved more than --threshold m (default 0.5) either at a normal frame time (<= 40 ms) or as a speed spike (more than 2x faster than both neighbouring frames); 'pops' counts the spikes. A long editor frame during a fast blend is reported as slowSteps, not as a jump.")]
    public static string CamTrace(
        [CliArg("action", "start|stop|report")] string action = "report",
        [CliArg("path", "stop/report: also write the samples as CSV (absolute, or relative to the project)")] string path = null,
        [CliArg("threshold", "jump threshold in metres per frame (default 0.5)")] float threshold = 0.5f)
    {
        HeadMovement hm = Require();
        CameraControl control = hm.OrbitCamera;
        if (control == null) throw new InvalidOperationException("no Simulator/CameraControl in the scene");
        switch ((action ?? "report").ToLowerInvariant())
        {
            case "start":
                control.StartTrace();
                return "tracing (every rendered frame) - hm_camtrace --action stop to analyse";
            case "stop":
                lastTrace = control.StopTrace();
                break;
            case "report":
                if (control.Tracing) lastTrace = new List<CameraControl.TraceSample>(control.StopTrace());
                break;
            default: throw new ArgumentException($"unknown action '{action}' (start|stop|report)");
        }

        if (!string.IsNullOrEmpty(path)) WriteTrace(path, lastTrace);
        return JsonConvert.SerializeObject(Analyse(lastTrace, threshold));
    }

    static void WriteTrace(string path, List<CameraControl.TraceSample> samples)
    {
        if (!Path.IsPathRooted(path)) path = Path.Combine(Directory.GetCurrentDirectory(), path);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder sb = new("frame,t,dt,ex,ey,ez,lx,ly,lz,fov,cut,blending,director,parked,shot,state\n");
        foreach (CameraControl.TraceSample s in samples)
        {
            sb.Append(string.Format(c, "{0},{1:0.0000},{2:0.0000},{3:0.00000},{4:0.00000},{5:0.00000},{6:0.00000},{7:0.00000},{8:0.00000},{9:0.000},{10},{11},{12},{13},{14},{15}\n",
                s.Frame, s.Time, s.Dt, s.Eye.x, s.Eye.y, s.Eye.z, s.Look.x, s.Look.y, s.Look.z, s.Fov, s.Cut ? 1 : 0,
                s.Blending ? 1 : 0, s.Director ? 1 : 0, s.Parked ? 1 : 0, s.Shot, s.State));
        }

        File.WriteAllText(path, sb.ToString());
    }

    static Dictionary<string, object> Analyse(List<CameraControl.TraceSample> t, float threshold)
    {
        int n = t.Count, cuts = 0, shots = 0, jumps = 0, pops = 0, slowSteps = 0;
        float maxEye = 0, maxLook = 0, maxSpeed = 0;
        int maxEyeAt = -1;
        List<Dictionary<string, object>> bad = new();
        float[] speed = new float[n];
        float[] step = new float[n];
        for (int i = 1; i < n; i++)
        {
            float dt = Mathf.Max(1e-4f, t[i].Time - t[i - 1].Time);
            float se = (t[i].Eye - t[i - 1].Eye).magnitude, sl = (t[i].Look - t[i - 1].Look).magnitude;
            step[i] = Mathf.Max(se, sl);
            speed[i] = step[i] / dt;
        }

        for (int i = 1; i < n; i++)
        {
            if (t[i].Shot != t[i - 1].Shot) shots++;
            if (t[i].Cut || t[i].Frame == t[i - 1].Frame) // a cut, or the same frame rendered twice
            {
                if (t[i].Cut) cuts++;
                continue;
            }

            if (t[i].Parked || t[i - 1].Parked) continue; // hm_hair / hm_shoes frame: someone else placed the camera
            float se = (t[i].Eye - t[i - 1].Eye).magnitude, sl = (t[i].Look - t[i - 1].Look).magnitude;
            if (se > maxEye)
            {
                maxEye = se;
                maxEyeAt = i;
            }

            maxLook = Mathf.Max(maxLook, sl);
            maxSpeed = Mathf.Max(maxSpeed, speed[i]);
            if (step[i] <= threshold) continue;
            float before = i > 1 && !t[i - 1].Cut ? speed[i - 1] : 0f, after = i + 1 < n && !t[i + 1].Cut ? speed[i + 1] : 0f;
            bool spike = speed[i] > 2f * Mathf.Max(before, after);
            bool slowFrame = t[i].Time - t[i - 1].Time > 0.04f;
            if (spike) pops++;
            if (spike || !slowFrame) jumps++;
            else slowSteps++;
            if (bad.Count < 40)
            {
                bad.Add(new Dictionary<string, object>
                {
                    ["i"] = i, ["frame"] = t[i].Frame, ["eyeStep"] = se, ["lookStep"] = sl, ["dt"] = t[i].Time - t[i - 1].Time,
                    ["spike"] = spike, ["slowFrame"] = slowFrame, ["state"] = t[i].State, ["prevState"] = t[i - 1].State,
                    ["blending"] = t[i].Blending
                });
            }
        }

        return new Dictionary<string, object>
        {
            ["samples"] = n, ["seconds"] = n > 1 ? t[n - 1].Time - t[0].Time : 0f, ["cuts"] = cuts, ["shotChanges"] = shots,
            ["threshold"] = threshold, ["jumps"] = jumps, ["pops"] = pops, ["slowSteps"] = slowSteps, ["maxEyeStep"] = maxEye,
            ["maxLookStep"] = maxLook,
            ["maxEyeStepState"] = maxEyeAt > 0 ? t[maxEyeAt].State : null, ["maxSpeed"] = maxSpeed, ["bad"] = bad
        };
    }
}
