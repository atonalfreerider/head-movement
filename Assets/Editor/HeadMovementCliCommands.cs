using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
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
///   unity command hm_orbit --mode park --at 0.5,-0.2 --azimuth 210 --elevation 55 --radius 2.4   # fixed still camera
///   unity command hm_orbit --height 1.6 --look 0.95 --distance 2.9
///   unity command hm_opacity --value 0.35                          # displayed opacity (65 % transparent)
///   unity command hm_opacity --probe both --opacities 0.35 --hair true   # measured transparency as displayed
///   unity command hm_neckaxis [--threshold 15 --full 35 --length 0.5 --reference neutral|torso|vertical]
///   unity command hm_skeleton [--mode rhythm|physics]        # skeleton colours: beat pulse / estimated load
///   unity command hm_floor [--footprints off|recent|all] [--plane 0.6]   # floor grid, footprints, floor craft
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

    [CliCommand("hm_layer", "Show or hide a visual layer: floor | tension | splats | room | cameras | hud | avatars | timing | physics | counterbalance | traces | neck | graph")]
    public static string Layer(
        [CliArg("layer", "floor|tension|splats|room|cameras|hud|avatars|timing|physics|counterbalance|traces|neck|graph")] string layer,
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
        [CliArg("mode", "free (default) | director | toggle (the O key: director <-> free, blended) | park (freeze the camera: no follow, no director, until the next hm_orbit)")] string mode = "free",
        [CliArg("at", "park: aim the parked camera at this floor point 'x,z' (world, after the origin offset) from --azimuth / --elevation / --radius (look height --look, default 0.3 m)")] string at = null)
    {
        HeadMovement hm = Require();
        CameraControl control = hm.OrbitCamera;
        if (control == null)
        {
            GameObject simulator = GameObject.Find("Simulator");
            control = simulator != null ? simulator.GetComponent<CameraControl>() : null;
        }

        if (control == null) throw new InvalidOperationException("no Simulator/CameraControl in the scene");
        if (string.Equals(mode, "park", StringComparison.OrdinalIgnoreCase))
        {
            // a fixed viewpoint for review stills over time (e.g. the leader's T while he walks on it)
            if (!string.IsNullOrEmpty(at))
            {
                string[] xz = at.Split(',');
                if (xz.Length != 2) throw new ArgumentException("--at x,z");
                float x = float.Parse(xz[0], CultureInfo.InvariantCulture), z = float.Parse(xz[1], CultureInfo.InvariantCulture);
                float phi = float.IsNaN(azimuth) ? control.Azimuth : azimuth * Mathf.Deg2Rad;
                float el = (float.IsNaN(elevation) ? 90f - control.Polar * Mathf.Rad2Deg : elevation) * Mathf.Deg2Rad;
                float r = float.IsNaN(radius) ? control.Radius : radius;
                Vector3 target = new(x, float.IsNaN(look) ? 0.3f : look, z);
                control.enabled = false;
                control.transform.position = target + new Vector3(Mathf.Cos(phi) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Sin(phi) * Mathf.Cos(el)) * r;
                control.transform.LookAt(target);
            }
            else
            {
                control.enabled = false;
            }

            return Describe(control);
        }

        if (!control.enabled) control.enabled = true; // un-park
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

    [CliCommand("hm_opacity", "Avatar opacity (VIEWER_SPEC 3.2): --value sets the user default (0..1 = the opacity the screen SHOWS: 0.35 = 65 % transparent; each body blends with its own alpha for it, reported as blendAlpha; view states scale it; hair = avatar + 0.15); no value reports it. --skeletons dimmed (default: each translucent body blends over its own skeleton) | over (the body skips its own skeleton's pixels: raw skeleton colour, VIEWER_SPEC 3.3 option). --probe follow|lead|both measures the transparency AS DISPLAYED at --opacities (one body alone, parked camera, black and grey backgrounds, the Game-view pipeline with post-processing, and linear HDR with it off)")]
    public static string Opacity([CliArg("value", "default avatar opacity 0..1")] float value = float.NaN,
        [CliArg("skeletons", "dimmed (default) | over: whether a translucent body blends over its own skeleton")] string skeletons = null,
        [CliArg("probe", "follow | lead | both: measure the displayed transparency of that body (restores everything after)")] string probe = null,
        [CliArg("opacities", "probe: comma-separated opacities (default: the current default)")] string opacities = null,
        [CliArg("hair", "probe: also measure the follow's hair at the hair opacity of each avatar opacity (true|false)")] bool hair = false,
        [CliArg("mode", "pixel (default: per-fragment alpha, every part shows the opacity) | body (one alpha per body) | raw (alpha = opacity, before 2026-10-07)")] string mode = null)
    {
        HeadMovement hm = Require();
        if (!string.IsNullOrEmpty(mode))
        {
            SmplxAvatar.AlphaMode = mode.ToLowerInvariant() switch
            {
                "pixel" => SmplxAvatar.AlphaModes.Pixel,
                "body" => SmplxAvatar.AlphaModes.Body,
                "raw" => SmplxAvatar.AlphaModes.Raw,
                _ => throw new ArgumentException($"--mode pixel|body|raw, not '{mode}'")
            };
        }

        if (!string.IsNullOrEmpty(probe))
        {
            float[] ops = string.IsNullOrEmpty(opacities)
                ? new[] { hm.AvatarOpacity }
                : opacities.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
            Dictionary<string, object> probes = new();
            foreach (Role role in probe.ToLowerInvariant() switch
                     {
                         "both" => new[] { Role.Follow, Role.Lead },
                         "lead" => new[] { Role.Lead },
                         "follow" => new[] { Role.Follow },
                         _ => throw new ArgumentException($"--probe follow|lead|both, not '{probe}'")
                     })
            {
                probes[role.ToString().ToLowerInvariant()] = OpacityProbe.Measure(hm, role, ops, includeHair: hair);
            }

            probes["defaultOpacity"] = hm.AvatarOpacity;
            probes["alphaMode"] = SmplxAvatar.AlphaMode.ToString().ToLowerInvariant();
            return JsonConvert.SerializeObject(probes);
        }

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
                ["opacity"] = kv.Value.Opacity, ["blendAlpha"] = kv.Value.BlendAlpha,
                ["predictedTransparency"] = kv.Value.PredictedTransparency, ["visible"] = kv.Value.Visible, ["feetHidden"] = kv.Value.FeetHidden,
                ["queueOffset"] = kv.Value.QueueOffset, ["queues"] = kv.Value.SortedQueues(),
                ["skeletonOverBody"] = kv.Value.SkeletonOverBody, ["skeletonStencilBit"] = kv.Value.SkeletonStencilBit
            };
        }

        return JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["avatarOpacity"] = hm.AvatarOpacity, ["effective"] = hm.EffectiveAvatarOpacity, ["sortBackToFront"] = SmplxAvatar.SortBackToFront,
            ["alphaMode"] = SmplxAvatar.AlphaMode.ToString().ToLowerInvariant(),
            ["skeletonsOverBodies"] = SmplxAvatar.SkeletonsOverBodies, ["avatars"] = avatars
        });
    }

    [CliCommand("hm_neckaxis", "Follower neck axis (VIEWER_SPEC 3.7, layer 'neck'): a white axis from her neck along her head's up direction, shown only while her neck is off-axis by more than --threshold deg (angle between her head's and her chest's up axes, SMPL-X rotations, smoothed), fading in and growing to --length m by --full deg. Reports the shown frame's angle / alpha / length, the frames over the threshold and example upright / ramp / full frames; the leader never has one.")]
    public static string NeckAxis(
        [CliArg("threshold", "deg: no axis up to this off-axis angle (default 15)")] float threshold = float.NaN,
        [CliArg("full", "deg: full alpha and length from this angle (default 35)")] float full = float.NaN,
        [CliArg("length", "m: length at full (default 0.5)")] float length = float.NaN,
        [CliArg("reference", "neutral (head on chest relative to her calibrated neutral carriage, default) | torso (relative to the chest's rest axis) | vertical (head up vs world up)")] string reference = null)
    {
        Require();
        DanceLayers layers = DanceLayers.Ensure();
        NeckAxisOverlay axis = layers != null ? layers.NeckAxis : null;
        if (axis == null) throw new InvalidOperationException("no neck axis: load a capture first (hm_load)");
        if (!float.IsNaN(threshold)) axis.ThresholdDeg = Mathf.Clamp(threshold, 0f, 89f);
        if (!float.IsNaN(full)) axis.FullDeg = Mathf.Clamp(full, axis.ThresholdDeg + 1f, 90f);
        if (!float.IsNaN(length)) axis.MaxLength = Mathf.Clamp(length, 0.05f, 2f);
        if (!string.IsNullOrEmpty(reference))
        {
            axis.Mode = reference.ToLowerInvariant() switch
            {
                "neutral" or "calibrated" => NeckAxisOverlay.Reference.Neutral,
                "torso" or "chest" => NeckAxisOverlay.Reference.Torso,
                "vertical" or "world" => NeckAxisOverlay.Reference.Vertical,
                _ => throw new ArgumentException($"--reference neutral|torso|vertical, not '{reference}'")
            };
        }

        axis.MarkDirty();
        if (layers.Traces != null) layers.Traces.SetHeadGate(axis.Gate()); // the head trail follows the axis
        layers.Refresh();
        return JsonConvert.SerializeObject(axis.State());
    }

    [CliCommand("hm_skeleton", "Skeleton colours (VIEWER_SPEC 3.3 / 3.8): --mode rhythm (default view: a pulse travels from the stance feet up through the body on every beat, strength by beat type and by how well each body part's jerk accent lands on the beat) | physics (limbs coloured by the ESTIMATED axial load: tension orange, neutral, compression blue; turns the physics layer on). Reports the mode, beat / accent / load sources, per-joint pulse, stance, load and colour of the shown frame and each dancer's accent hit rate / mean lag per body part.")]
    public static string Skeleton([CliArg("mode", "rhythm | physics (omit: report only)")] string mode = null)
    {
        HeadMovement hm = Require();
        if (!string.IsNullOrEmpty(mode))
        {
            hm.SetSkeletonMode(mode.ToLowerInvariant() switch
            {
                "rhythm" or "beat" or "default" => SkeletonStyle.Mode.Rhythm,
                "physics" or "load" => SkeletonStyle.Mode.Physics,
                _ => throw new ArgumentException($"--mode rhythm|physics, not '{mode}'")
            });
        }

        if (hm.Skeletons == null) throw new InvalidOperationException("no skeleton colours: load a capture first (hm_load)");
        Dictionary<string, object> s = hm.Skeletons.State(hm.CurrentFrame);
        s["layerPhysics"] = hm.LayerVisible("physics");
        return JsonConvert.SerializeObject(s);
    }

    [CliCommand("hm_floor", "Floor (VIEWER_SPEC 3.1 / 3.5 / 3.6): --footprints off | recent (default: each print fades out ~1.2 s after its touchdown) | all; --plane sets the floor plane alpha (0.6 default, 0.25 passthrough). Reports the grid (teal 1 m crosses), the footprints, the leader's live floor axis and the floor-craft record (plants / moves / rotations / pivots shown now).")]
    public static string Floor(
        [CliArg("footprints", "off | recent | all")] string footprints = null,
        [CliArg("plane", "floor plane alpha 0..1")] float plane = float.NaN)
    {
        HeadMovement hm = Require();
        if (!string.IsNullOrEmpty(footprints))
        {
            hm.Footprints.SetMode(footprints.ToLowerInvariant() switch
            {
                "off" or "none" => FloorPatterns.FootprintMode.Off,
                "recent" => FloorPatterns.FootprintMode.Recent,
                "all" => FloorPatterns.FootprintMode.All,
                _ => throw new ArgumentException($"--footprints off|recent|all, not '{footprints}'")
            });
        }

        if (!float.IsNaN(plane)) hm.Grid.SetPlaneAlpha(plane);
        DanceLayers layers = DanceLayers.Ensure();
        if (layers != null)
        {
            if (layers.FloorCraft != null) layers.FloorCraft.MarkDirty();
            layers.Refresh();
        }

        Dictionary<string, object> st = hm.State();
        return JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["grid"] = st.GetValueOrDefault("grid"), ["footprints"] = st.GetValueOrDefault("footprints"),
            ["floorCraft"] = st.GetValueOrDefault("floorCraft"), ["frame"] = hm.CurrentFrame
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
