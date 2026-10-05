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

    [CliCommand("hm_transport", "Lesson transport: play | pause | toggle | restart | beat+ | beat- | measure+ | measure- | loop | faster | slower | seek")]
    public static string Transport(
        [CliArg("action", "play|pause|toggle|restart|beat+|beat-|measure+|measure-|loop|faster|slower|seek")] string action,
        [CliArg("time", "audio-timeline seconds for seek")] float time = 0f)
    {
        HeadMovement hm = Require();
        switch (action)
        {
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

    [CliCommand("hm_layer", "Show or hide a visual layer: floor | tension | splats | cameras | hud")]
    public static string Layer(
        [CliArg("layer", "floor|tension|splats|cameras|hud")] string layer,
        [CliArg("visible", "true to show, false to hide")] bool visible = true)
    {
        Require().SetLayerVisible(layer, visible);
        return $"{layer}={visible}";
    }

    [CliCommand("hm_orbit", "Place the desktop orbit camera: azimuth/elevation in degrees, radius in metres (centre follows the dancers)")]
    public static string Orbit(
        [CliArg("azimuth", "degrees around the vertical axis")] float azimuth = 90f,
        [CliArg("elevation", "degrees above the horizon")] float elevation = 15f,
        [CliArg("radius", "metres from the dancers")] float radius = 2.5f)
    {
        Require();
        GameObject simulator = GameObject.Find("Simulator");
        CameraControl control = simulator != null ? simulator.GetComponent<CameraControl>() : null;
        if (control == null) throw new InvalidOperationException("no Simulator/CameraControl in the scene");
        control.SetOrbit(azimuth * Mathf.Deg2Rad, (90f - elevation) * Mathf.Deg2Rad, radius);
        return $"azimuth={azimuth} elevation={elevation} radius={radius}";
    }
}
