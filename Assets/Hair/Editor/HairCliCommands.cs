using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Unity CLI surface for the follow's simulated hair (Assets/Hair/HairStrands). Play mode, a capture with
/// hair_groom.json loaded.
///
///   unity command hm_hair                                   # state: counts, stretch, reach, CPU ms, legacy hair
///   unity command hm_hair --action peaks --count 3          # fastest head moments (audio times) of the follow
///   unity command hm_hair --action set --tipglow 0 --opacity 1 --visible true --damping 1.6
///   unity command hm_hair --action reset | sync | stats_reset
/// </summary>
public static class HairCliCommands
{
    static HairStrands Require()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("enter Play mode first (unity command editor_play)");
        HairStrands hair = HairStrands.Active;
        if (hair == null) throw new InvalidOperationException("no hair: load a capture with hair_groom.json (python -m dancecap.hair_groom <take>)");
        return hair;
    }

    [CliCommand("hm_hair", "Follow hair: state | peaks (fastest head moments) | set (--tipglow --opacity --visible --damping --substeps) | frame (--azimuth --elevation --radius around her head) | release | sweep (whole capture: CPU ms per step, spread / trailing vs the hair reference) | sync | reset | stats_reset")]
    public static string Hair(
        [CliArg("action", "state|peaks|set|frame|release|sweep|sync|reset|stats_reset")] string action = "state",
        [CliArg("count", "peaks: how many moments")] int count = 3,
        [CliArg("tipglow", "set: tip emission scale (1 = the original bloom, 0 = off)")] float tipglow = float.NaN,
        [CliArg("opacity", "set: hair opacity 0..1")] float opacity = float.NaN,
        [CliArg("visible", "set: show the hair (true|false)")] string visible = null,
        [CliArg("damping", "set: velocity damping 1/s")] float damping = float.NaN,
        [CliArg("substeps", "set: substeps per capture frame")] int substeps = 0,
        [CliArg("shape", "set: shape stiffness at the root (fraction back to the rest shape per 1/30 s)")] float shape = float.NaN,
        [CliArg("falloff", "set: shape stiffness falloff (arc fraction per e-fold)")] float falloff = float.NaN,
        [CliArg("bend", "set: bending compliance")] float bend = float.NaN,
        [CliArg("preroll", "set: frames simulated before the shown frame after a seek")] int preroll = -1,
        [CliArg("iterations", "set: constraint iterations per substep")] int iterations = 0,
        [CliArg("azimuth", "frame: degrees around her head, 0 = in front of her face")] float azimuth = 0f,
        [CliArg("elevation", "frame: degrees above her head's horizon")] float elevation = 10f,
        [CliArg("radius", "frame: metres from her head")] float radius = 1.4f)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("enter Play mode first (unity command editor_play)");
        HairStrands hair = HairStrands.Active;
        Dictionary<string, object> state;
        switch ((action ?? "state").ToLowerInvariant())
        {
            case "state":
                state = hair != null ? hair.State() : new Dictionary<string, object> { ["present"] = false };
                break;
            case "sync":
                hair = Require();
                hair.Advance(hair.Avatar.CurrentFrame);
                state = hair.State();
                break;
            case "reset":
                hair = Require();
                hair.ResetSimulation();
                state = hair.State();
                break;
            case "stats_reset":
                hair = Require();
                hair.ResetStats();
                state = hair.State();
                break;
            case "set":
                hair = Require();
                if (!float.IsNaN(tipglow)) hair.TipGlow = tipglow;
                if (!float.IsNaN(opacity)) hair.Opacity = Mathf.Clamp01(opacity);
                if (visible != null) hair.Visible = bool.Parse(visible);
                if (!float.IsNaN(damping)) hair.Damping = damping;
                if (substeps > 0) hair.Substeps = substeps;
                if (!float.IsNaN(shape)) hair.ShapeRoot = shape;
                if (!float.IsNaN(falloff)) hair.ShapeFalloff = falloff;
                if (!float.IsNaN(bend)) hair.BendCompliance = bend;
                if (preroll >= 0) hair.PreRollFrames = preroll;
                if (iterations > 0) hair.Iterations = iterations;
                hair.ApplyLook();
                state = hair.State();
                break;
            case "peaks":
                hair = Require();
                return JsonConvert.SerializeObject(new Dictionary<string, object> { ["peaks"] = Peaks(hair, count) });
            case "frame":
                hair = Require();
                return Frame(hair, azimuth, elevation, radius);
            case "release":
                return Release();
            case "sweep":
                hair = Require();
                return JsonConvert.SerializeObject(hair.Sweep());
            default:
                throw new ArgumentException($"unknown action '{action}' (state|peaks|set|frame|release|sweep|sync|reset|stats_reset)");
        }

        // the original LineRenderer hair must be gone on v3 captures (it drew a white bar from the world origin)
        state["legacyHairObjects"] = UnityEngine.Object.FindObjectsByType<HairSimulation>(FindObjectsSortMode.None).Length;
        // ... and no enabled LineRenderer may still hold Unity's default (0,0,0)-(0,0,1) points (a bar at the origin)
        state["defaultLineRenderers"] = UnityEngine.Object.FindObjectsByType<LineRenderer>(FindObjectsSortMode.None)
            .Count(l => l.enabled && l.gameObject.activeInHierarchy && l.positionCount == 2 && l.GetPosition(0) == Vector3.zero &&
                        l.GetPosition(1) == Vector3.forward);
        return JsonConvert.SerializeObject(state);
    }

    /// <summary>
    /// Review camera: park the desktop orbit camera (its CameraControl paused) at azimuth / elevation / radius around
    /// the follow's head, azimuth measured from her facing direction, looking at a point 0.25 m below the head so the
    /// hair down her back is in frame. "release" hands the camera back to the orbit control.
    /// </summary>
    static string Frame(HairStrands hair, float azimuth, float elevation, float radius)
    {
        GameObject simulator = GameObject.Find("Simulator");
        VRTKLite.Controllers.CameraControl control = simulator != null ? simulator.GetComponent<VRTKLite.Controllers.CameraControl>() : null;
        if (control == null) throw new InvalidOperationException("no Simulator/CameraControl in the scene");
        control.enabled = false;
        Transform head = hair.Avatar.Bone(15);
        Vector3 facing = head.rotation * new Vector3(0, 0, -1); // the skin faces -z at rest
        facing.y = 0;
        facing = facing.sqrMagnitude > 1e-6f ? facing.normalized : Vector3.forward;
        Vector3 dir = Quaternion.AngleAxis(azimuth, Vector3.up) * facing;
        dir = Quaternion.AngleAxis(-elevation, Vector3.Cross(Vector3.up, dir)) * dir;
        Vector3 target = head.position + Vector3.down * 0.25f;
        control.transform.position = target + dir.normalized * radius;
        control.transform.LookAt(target);
        return JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["camera"] = new[] { control.transform.position.x, control.transform.position.y, control.transform.position.z },
            ["target"] = new[] { target.x, target.y, target.z }, ["azimuth"] = azimuth, ["elevation"] = elevation,
            ["radius"] = radius
        });
    }

    static string Release()
    {
        GameObject simulator = GameObject.Find("Simulator");
        VRTKLite.Controllers.CameraControl control = simulator != null ? simulator.GetComponent<VRTKLite.Controllers.CameraControl>() : null;
        if (control != null)
        {
            control.enabled = true;
            control.SetOrbit(control.Azimuth, control.Polar, control.Radius);
        }

        return "released";
    }

    /// <summary>
    /// Frames of the fastest head rotation of the hair's avatar: |angular velocity| of the head bone's global
    /// rotation (product of the pelvis..head chain of local rotations) by central differences over the capture
    /// frame times; local maxima at least 0.5 s apart.
    /// </summary>
    static List<Dictionary<string, object>> Peaks(HairStrands hair, int count)
    {
        SmplxData.Motion m = hair.Avatar.Motion;
        HeadMovement hm = HeadMovement.Instance;
        int[] chain = { 0, 3, 6, 9, 12, 15 };
        int n = m.FrameCount;
        Quaternion[] g = new Quaternion[n];
        for (int k = 0; k < n; k++)
        {
            Quaternion q = Quaternion.identity;
            foreach (int j in chain) q *= m.Rotations[k * SmplxData.Joints + j];
            g[k] = q;
        }

        float T(int k) => hm != null && hm.Timeline != null ? hm.Timeline.AudioTimeOf(k) : k / 30f;
        float[] w = new float[n];
        for (int k = 0; k < n; k++)
        {
            int a = Mathf.Max(k - 1, 0), b = Mathf.Min(k + 1, n - 1);
            if (b == a) continue;
            float ang = Quaternion.Angle(g[a], g[b]);
            w[k] = ang / Mathf.Max(T(b) - T(a), 1e-4f);
        }

        List<Dictionary<string, object>> peaks = new();
        foreach (int k in Enumerable.Range(0, n).OrderByDescending(k => w[k]))
        {
            if (peaks.Any(p => Mathf.Abs((float)p["audioTime"] - T(k)) < 0.5f)) continue;
            peaks.Add(new Dictionary<string, object> { ["frame"] = k, ["audioTime"] = T(k), ["degPerS"] = w[k] });
            if (peaks.Count >= count) break;
        }

        return peaks;
    }
}
