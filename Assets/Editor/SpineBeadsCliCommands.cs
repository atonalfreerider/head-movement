using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Unity CLI surface for the follower's spine bead chain (Assets/Overlays/SpineBeads.cs, user 2026-10-07). Play mode,
/// a capture loaded.
///
///   unity command hm_spine                                         # state: beads, size, colours, draws, line renderers
///   unity command hm_spine --action set --radius 0.0063 --spacing 0.027 [--enabled false]   # the defaults
///   unity command hm_spine --action frame --view front|side|back [--distance 0.9 --elevation 8 --partner hide]
///   unity command hm_spine --action release                        # camera back to the rig, partner shown again
///   unity command hm_spine --action pulse                          # seek to the rhythm pulse mid-travel up her spine
/// </summary>
public static class SpineBeadsCliCommands
{
    static SmplxAvatar hiddenPartner;
    static readonly List<Renderer> hiddenPartnerLines = new();

    static (HeadMovement hm, SpineBeads beads) Require()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("enter Play mode first (unity command editor_play)");
        HeadMovement hm = HeadMovement.Instance;
        if (hm == null) throw new InvalidOperationException("no HeadMovement in the open scene");
        SpineBeads beads = hm.FollowDancer != null ? hm.FollowDancer.Spine : null;
        if (beads == null) throw new InvalidOperationException("no follower spine beads: load a capture first (hm_load)");
        return (hm, beads);
    }

    [CliCommand("hm_spine", "Follower spine beads (user 2026-10-07: small white spheres instead of a line): state (count, radius, spacing, per-bead colour / brightness / place along the spine curve, instanced draws per frame, the follower's remaining LineRenderers and whether any is a spine line) | set (--radius --spacing m, --enabled) | frame (park the camera on her spine: --view front|side|back, --distance, --elevation, --partner hide|show) | release | pulse (seek to the frame where the rhythm pulse is mid-travel up her spine; reports the bead brightness profile of the frames around it)")]
    public static string Spine(
        [CliArg("action", "state|set|frame|release|pulse")] string action = "state",
        [CliArg("radius", "set: bead radius in m")] float radius = float.NaN,
        [CliArg("spacing", "set: bead centre spacing along the curve in m")] float spacing = float.NaN,
        [CliArg("enabled", "set: draw the beads (true|false; A/B draw-call checks)")] string enabled = null,
        [CliArg("view", "frame: front | side (her right) | back | left")] string view = "front",
        [CliArg("distance", "frame: camera distance from her mid-spine in m")] float distance = 0.9f,
        [CliArg("elevation", "frame: degrees above her mid-spine")] float elevation = 8f,
        [CliArg("partner", "frame: hide | show the partner (avatar and skeleton) for the close-up")] string partner = "show")
    {
        (HeadMovement hm, SpineBeads beads) = Require();
        switch ((action ?? "state").ToLowerInvariant())
        {
            case "state":
                return JsonConvert.SerializeObject(State(hm, beads));
            case "set":
                if (!float.IsNaN(radius) || !float.IsNaN(spacing))
                    beads.Configure(float.IsNaN(radius) ? beads.Radius : radius, float.IsNaN(spacing) ? beads.Spacing : spacing);
                if (enabled != null) beads.Enabled = bool.Parse(enabled);
                hm.RefreshSkeletons(); // re-place the beads on the shown frame
                return JsonConvert.SerializeObject(State(hm, beads));
            case "frame":
                return Frame(hm, beads, view, distance, elevation, string.Equals(partner, "hide", StringComparison.OrdinalIgnoreCase));
            case "release":
                ShowPartner(hm);
                GameObject simulator = GameObject.Find("Simulator");
                if (simulator != null)
                {
                    SneakerReviewCamera.Release(simulator.transform);
                    VRTKLite.Controllers.CameraControl control = simulator.GetComponent<VRTKLite.Controllers.CameraControl>();
                    if (control != null) control.enabled = true;
                }

                return "released";
            case "pulse":
                return JsonConvert.SerializeObject(Pulse(hm, beads));
            default:
                throw new ArgumentException($"unknown action '{action}' (state|set|frame|release|pulse)");
        }
    }

    static float Brightness(Color c) => Mathf.Max(c.r, Mathf.Max(c.g, c.b));

    static Dictionary<string, object> State(HeadMovement hm, SpineBeads beads)
    {
        Dancer follow = hm.FollowDancer, lead = hm.LeadDancer;
        int frame = hm.CurrentFrame;
        LineRenderer[] followLines = follow.GetComponentsInChildren<LineRenderer>(true);
        LineRenderer[] leadLines = lead != null ? lead.GetComponentsInChildren<LineRenderer>(true) : new LineRenderer[0];
        // a spine line = a follower LineRenderer that starts at her pelvis and ends at her neck
        bool spineLine = false;
        if (frame >= 0)
        {
            Vector3 pelvis = follow.Joint(frame, SmplJoint.Pelvis), neck = follow.Joint(frame, SmplJoint.Neck);
            foreach (LineRenderer l in followLines)
            {
                if (l.positionCount < 2) continue;
                Vector3 a = l.transform.TransformPoint(l.GetPosition(0)), b = l.transform.TransformPoint(l.GetPosition(l.positionCount - 1));
                if (((a - pelvis).magnitude < 0.02f && (b - neck).magnitude < 0.02f) || ((b - pelvis).magnitude < 0.02f && (a - neck).magnitude < 0.02f))
                    spineLine = true;
            }
        }

        int n = beads.Count;
        float[] bright = new float[n], param = new float[n];
        float[][] rgb = new float[n][];
        float maxGapMm = 0, minGapMm = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            Color c = beads.BeadColour(i);
            rgb[i] = new[] { c.r, c.g, c.b };
            bright[i] = Brightness(c);
            param[i] = beads.BeadParam(i);
            if (i > 0)
            {
                float d = (beads.BeadPosition(i) - beads.BeadPosition(i - 1)).magnitude * 1000f;
                maxGapMm = Mathf.Max(maxGapMm, d);
                minGapMm = Mathf.Min(minGapMm, d);
            }
        }

        Dictionary<string, object> s = new()
        {
            ["frame"] = frame,
            ["count"] = n,
            ["radiusM"] = beads.Radius,
            ["spacingM"] = beads.Spacing,
            ["typicalLengthM"] = beads.TypicalLength,
            ["centreStepMm"] = new[] { n > 1 ? minGapMm : 0, maxGapMm },
            ["visible"] = beads.Visible,
            ["enabled"] = beads.Enabled,
            ["visibilitySourceEnabled"] = beads.VisibilitySource != null && beads.VisibilitySource.enabled,
            ["instanced"] = beads.Instanced,
            ["shader"] = beads.Material != null ? beads.Material.shader.name : null,
            ["renderQueue"] = beads.Material != null ? beads.Material.renderQueue : -1,
            ["intensity"] = beads.Material != null ? beads.Material.GetFloat("_Intensity") : 0f,
            ["stencilPass"] = beads.Material != null ? beads.Material.GetFloat("_StencilPass") : -1f,
            ["skeletonStencilOn"] = Dancer.SkeletonStencilOn,
            ["drawsLastFrame"] = beads.DrawsLastFrame,
            ["drawsThisFrame"] = beads.DrawsThisFrame,
            ["instancesLastDraw"] = beads.InstancesLastDraw,
            ["lastDrawFrame"] = beads.LastDrawFrame,
            ["frameCount"] = Time.frameCount,
            ["camerasLastFrame"] = beads.CamerasLastFrame.Where(c => c != null).Select(c => $"{c.name} ({c.cameraType})").ToArray(),
            ["followLineRenderers"] = followLines.Length,
            ["leadLineRenderers"] = leadLines.Length,
            ["followSpineLineRenderer"] = spineLine,
            ["skeletonMode"] = hm.SkeletonMode.ToString().ToLowerInvariant(),
            ["brightness"] = bright,
            ["param"] = param,
            ["rgb"] = rgb
        };
        if (frame >= 0 && n > 0)
        {
            s["pelvisErrorMm"] = (beads.BeadPosition(0) - follow.Joint(frame, SmplJoint.Pelvis)).magnitude * 1000f;
            s["neckErrorMm"] = (beads.BeadPosition(n - 1) - follow.Joint(frame, SmplJoint.Neck)).magnitude * 1000f;
            Color[] joints = hm.Skeletons?.Colours(Role.Follow, frame);
            if (joints != null)
            {
                s["jointRgb"] = new[] { SmplJoint.Pelvis, SmplJoint.Spine1, SmplJoint.Spine2, SmplJoint.Spine3, SmplJoint.Neck }
                    .Select(j => new[] { Mathf.Clamp01(joints[(int)j].r), Mathf.Clamp01(joints[(int)j].g), Mathf.Clamp01(joints[(int)j].b) }).ToArray();
                // the shown frame's colours again (Colours() shares its array with the dancer)
            }
        }

        return s;
    }

    /// <summary>park the desktop camera on her mid-spine (SneakerReviewCamera hold; "release" hands it back)</summary>
    static string Frame(HeadMovement hm, SpineBeads beads, string view, float distance, float elevation, bool hidePartner)
    {
        GameObject simulator = GameObject.Find("Simulator");
        VRTKLite.Controllers.CameraControl control = simulator != null ? simulator.GetComponent<VRTKLite.Controllers.CameraControl>() : null;
        if (control == null) throw new InvalidOperationException("no Simulator/CameraControl in the scene");
        Dancer d = hm.FollowDancer;
        int f = Mathf.Max(0, hm.CurrentFrame);
        Vector3 pelvis = d.Joint(f, SmplJoint.Pelvis), neck = d.Joint(f, SmplJoint.Neck);
        Vector3 right = (d.Joint(f, SmplJoint.R_Hip) - d.Joint(f, SmplJoint.L_Hip)) + (d.Joint(f, SmplJoint.R_Shoulder) - d.Joint(f, SmplJoint.L_Shoulder));
        Vector3 up = Vector3.up;
        Vector3 forward = Vector3.Cross(right, up); // Unity (left-handed): her facing
        forward.y = 0;
        forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
        float azimuth = (view ?? "front").ToLowerInvariant() switch
        {
            "front" => 0f,
            "side" or "right" => 90f,
            "back" => 180f,
            "left" => -90f,
            _ => throw new ArgumentException($"--view front|side|back|left, not '{view}'")
        };
        Vector3 target = 0.5f * (pelvis + neck);
        Vector3 dir = Quaternion.AngleAxis(azimuth, Vector3.up) * forward;
        dir = Quaternion.AngleAxis(-elevation, Vector3.Cross(Vector3.up, dir)) * dir;
        control.enabled = false;
        SneakerReviewCamera.Hold(control.transform, target + dir.normalized * distance, target);
        ShowPartner(hm);
        if (hidePartner) HidePartner(hm);
        Camera cam = control.GetComponentInChildren<Camera>();
        if (cam == null) cam = Camera.main;
        Vector3 cp = cam != null ? cam.transform.position : control.transform.position;
        return JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["camera"] = new[] { cp.x, cp.y, cp.z }, ["target"] = new[] { target.x, target.y, target.z }, ["view"] = view,
            ["distance"] = distance, ["elevation"] = elevation, ["frame"] = f, ["partnerHidden"] = hidePartner,
            ["fov"] = cam != null ? cam.fieldOfView : float.NaN,
            ["beadPx"] = cam != null ? 2f * beads.Radius / (2f * distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)) * Screen.height : float.NaN
        });
    }

    static void HidePartner(HeadMovement hm)
    {
        if (hm.Avatars.TryGetValue(Role.Lead, out SmplxAvatar lead) && lead != null && lead.Visible)
        {
            lead.SetVisible(false);
            hiddenPartner = lead;
        }

        if (hm.LeadDancer == null) return;
        foreach (Renderer r in hm.LeadDancer.GetComponentsInChildren<Renderer>())
        {
            if (!r.enabled) continue;
            r.enabled = false;
            hiddenPartnerLines.Add(r);
        }
    }

    static void ShowPartner(HeadMovement hm)
    {
        if (hiddenPartner != null) hiddenPartner.SetVisible(hm.LayerVisible("avatars"));
        hiddenPartner = null;
        foreach (Renderer r in hiddenPartnerLines)
        {
            if (r != null) r.enabled = true;
        }

        hiddenPartnerLines.Clear();
    }

    /// <summary>the frame where the rhythm pulse is mid-travel up her spine: the largest pelvis - neck brightness
    /// difference of her joint colours (line colours, clamped like Color32) with the mid-spine in between; the bead
    /// profiles of the frames around it show the pulse climbing bead by bead</summary>
    static Dictionary<string, object> Pulse(HeadMovement hm, SpineBeads beads)
    {
        if (hm.Skeletons == null || hm.Timeline == null) throw new InvalidOperationException("no skeleton colours");
        if (hm.SkeletonMode != SkeletonStyle.Mode.Rhythm) hm.SetSkeletonMode(SkeletonStyle.Mode.Rhythm);
        int n = Mathf.Min(hm.FollowDancer.FrameCount, hm.Timeline.Count);
        int best = -1;
        float bestScore = float.MinValue;
        for (int f = 1; f < n - 3; f++)
        {
            Color[] c = hm.Skeletons.Colours(Role.Follow, f);
            float p = Brightness(Clamp(c[(int)SmplJoint.Pelvis])), m = Brightness(Clamp(c[(int)SmplJoint.Spine2])),
                k = Brightness(Clamp(c[(int)SmplJoint.Neck]));
            // the front between pelvis and neck: pelvis lit, neck still dark, the mid-spine in between
            float score = (p - k) - Mathf.Abs(m - 0.5f * (p + k));
            if (score > bestScore)
            {
                bestScore = score;
                best = f;
            }
        }

        if (best < 0) throw new InvalidOperationException("no pulse found");
        Dictionary<string, object> profiles = new();
        foreach (int df in new[] { -1, 0, 1, 2 })
        {
            int f = Mathf.Clamp(best + df, 0, n - 1);
            hm.Seek(hm.Timeline.AudioTimeOf(f));
            profiles[df.ToString("+0;-0;0")] = new Dictionary<string, object>
            {
                ["frame"] = hm.CurrentFrame,
                ["brightness"] = Enumerable.Range(0, beads.Count).Select(i => Brightness(beads.BeadColour(i))).ToArray()
            };
        }

        hm.Seek(hm.Timeline.AudioTimeOf(best));
        return new Dictionary<string, object>
        {
            ["frame"] = hm.CurrentFrame, ["audioTime"] = hm.Timeline.AudioTimeOf(best), ["score"] = bestScore,
            ["count"] = beads.Count, ["profiles"] = profiles
        };
    }

    static Color Clamp(Color c) => new(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
}
