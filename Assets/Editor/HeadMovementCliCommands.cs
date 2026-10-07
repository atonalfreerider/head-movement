using System;
using System.Collections.Generic;
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

    [CliCommand("hm_orbit", "Place the desktop orbit camera: azimuth/elevation in degrees, radius in metres (centre follows the dancers). Omitted values keep the current orbit.")]
    public static string Orbit(
        [CliArg("azimuth", "degrees around the vertical axis")] float azimuth = float.NaN,
        [CliArg("elevation", "degrees above the horizon")] float elevation = float.NaN,
        [CliArg("radius", "metres from the dancers")] float radius = float.NaN)
    {
        Require();
        GameObject simulator = GameObject.Find("Simulator");
        CameraControl control = simulator != null ? simulator.GetComponent<CameraControl>() : null;
        if (control == null) throw new InvalidOperationException("no Simulator/CameraControl in the scene");
        float phi = float.IsNaN(azimuth) ? control.Azimuth : azimuth * Mathf.Deg2Rad;
        float alpha = float.IsNaN(elevation) ? control.Polar : (90f - elevation) * Mathf.Deg2Rad;
        control.SetOrbit(phi, alpha, float.IsNaN(radius) ? control.Radius : radius);
        return $"azimuth={control.Azimuth * Mathf.Rad2Deg:0} elevation={90f - control.Polar * Mathf.Rad2Deg:0} radius={control.Radius:0.0}";
    }
}
