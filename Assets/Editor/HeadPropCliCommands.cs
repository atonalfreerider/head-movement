using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Unity CLI surface for the head-worn props (Assets/Avatar/HeadProps.cs, VIEWER_SPEC 3.2b). Play mode, a capture loaded.
///
///   unity command hm_prop                                              # state of every prop: spec, clearances to the skin (mm), anchors
///   unity command hm_prop --action set --role follow --visible false   # hide / show the prop (review A/B)
///   unity command hm_prop --action set --role follow --standoff 0.008 --bow 0.005 --boomlength 0.11 --capsulelength 0.024   # rebuild with new numbers
///   unity command hm_prop --action frame --role follow --azimuth -35 --elevation 4 --radius 0.5 [--view body|head --clean true]   # review camera on a head
///   unity command hm_prop --action release                              # camera back to the rig, partner / skeletons shown again
///   unity command hm_prop --action rebuild                             # re-lay the props on the head with the same numbers (after the head mesh changed)
/// </summary>
public static class HeadPropCliCommands
{
    [CliCommand("hm_prop", "Head-worn props (the teacher's headset): state | set (--role --visible; or rebuild with --side --standoff --bow --boomlength --thickness --capsulelength --capsuleradius --hook --boost, metres) | rebuild (re-lay the props on the head with the same numbers) | frame (park a review camera around the head: --role --azimuth (deg, + = toward her right, - = her left) --elevation --radius --drop --view body|head --clean) | release")]
    public static string Prop(
        [CliArg("action", "state|set|rebuild")] string action = "state",
        [CliArg("role", "lead|follow (default: every prop)")] string role = null,
        [CliArg("visible", "set: show the prop (true|false)")] string visible = null,
        [CliArg("side", "set: left|right (the dancer's own side)")] string side = null,
        [CliArg("standoff", "set: boom axis above the skin along the cheek (m)")] float standoff = float.NaN,
        [CliArg("bow", "set: extra standoff at the middle of the boom (m)")] float bow = float.NaN,
        [CliArg("boomlength", "set: arc length ear hook -> capsule centre (m; 0 = to the mouth corner)")] float boomlength = float.NaN,
        [CliArg("thickness", "set: boom diameter (m)")] float thickness = float.NaN,
        [CliArg("capsulelength", "set: capsule length (m)")] float capsulelength = float.NaN,
        [CliArg("capsuleradius", "set: capsule radius (m)")] float capsuleradius = float.NaN,
        [CliArg("hook", "set: ear hook on|off (true|false)")] string hook = null,
        [CliArg("boost", "set: opacity boost over the avatar's displayed opacity")] float boost = float.NaN,
        [CliArg("azimuth", "frame: degrees around the head, 0 = in front of the face, + = toward the dancer's right")] float azimuth = 0f,
        [CliArg("elevation", "frame: degrees above the horizon")] float elevation = 5f,
        [CliArg("radius", "frame: metres from the head centre")] float radius = 0.5f,
        [CliArg("drop", "frame: look-at point below the head bone (m)")] float drop = 0.02f,
        [CliArg("view", "frame: body (azimuth about the vertical through the horizontal facing of the head) | head (about the head's own up axis: a profile stays a profile when she looks down)")] string view = "body",
        [CliArg("clean", "frame: hide the partner avatar and every glowing skeleton until release (true|false)")] string clean = "true")
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("enter Play mode first (unity command editor_play)");
        HeadMovement hm = HeadMovement.Instance;
        if (hm == null) throw new InvalidOperationException("no HeadMovement in the open scene");
        List<HeadProps> props = HeadProps.All.Where(p => p != null && p.Avatar != null &&
            (string.IsNullOrEmpty(role) || string.Equals(p.Spec.Role, role, StringComparison.OrdinalIgnoreCase))).ToList();
        switch ((action ?? "state").ToLowerInvariant())
        {
            case "state":
                return JsonConvert.SerializeObject(new { count = props.Count, props = props.Select(p => p.State()).ToList() });
            case "set":
                if (props.Count == 0) throw new InvalidOperationException("no prop for that role (the capture declares none: capture.json props)");
                List<Dictionary<string, object>> states = new();
                foreach (HeadProps p0 in props)
                {
                    HeadProps p = p0;
                    HeadPropSpec s = p.Spec;
                    bool rebuild = false;
                    if (side != null) { s.Right = string.Equals(side, "right", StringComparison.OrdinalIgnoreCase); rebuild = true; }
                    if (!float.IsNaN(standoff)) { s.Standoff = Mathf.Clamp(standoff, 0f, 0.05f); rebuild = true; }
                    if (!float.IsNaN(bow)) { s.Bow = Mathf.Clamp(bow, 0f, 0.05f); rebuild = true; }
                    if (!float.IsNaN(boomlength)) { s.BoomLength = boomlength > 0f ? Mathf.Clamp(boomlength, 0.02f, 0.3f) : -1f; rebuild = true; }
                    if (!float.IsNaN(thickness)) { s.BoomThickness = Mathf.Clamp(thickness, 0.0005f, 0.01f); rebuild = true; }
                    if (!float.IsNaN(capsulelength)) { s.CapsuleLength = Mathf.Clamp(capsulelength, 0.005f, 0.06f); rebuild = true; }
                    if (!float.IsNaN(capsuleradius)) { s.CapsuleRadius = Mathf.Clamp(capsuleradius, 0.002f, 0.03f); rebuild = true; }
                    if (hook != null) { s.Hook = bool.Parse(hook); rebuild = true; }
                    if (!float.IsNaN(boost)) { s.OpacityBoost = Mathf.Clamp01(boost); rebuild = true; }
                    bool vis = visible == null ? p.Visible : bool.Parse(visible);
                    if (rebuild) p = HeadProps.Create(p.Avatar, s);
                    if (p != null)
                    {
                        p.Visible = vis;
                        states.Add(p.State());
                    }
                }

                return JsonConvert.SerializeObject(new { count = states.Count, props = states });
            case "frame":
                if (props.Count == 0) throw new InvalidOperationException("no prop for that role");
                Clean(props[0].Avatar, clean == null || bool.Parse(clean));
                return Frame(props[0].Avatar, azimuth, elevation, radius, drop, string.Equals(view, "head", StringComparison.OrdinalIgnoreCase));
            case "release":
                Clean(null, false);
                return Release();
            case "rebuild":
                foreach (HeadProps p in props.ToList()) HeadProps.Create(p.Avatar, p.Spec);
                return JsonConvert.SerializeObject(new { count = HeadProps.All.Count, props = HeadProps.All.Where(p => p != null).Select(p => p.State()).ToList() });
            default:
                throw new ArgumentException($"unknown action '{action}' (state|set|rebuild)");
        }
    }

    static readonly List<Renderer> hidden = new();
    static SmplxAvatar hiddenPartner;

    /// <summary>review close-ups: hide the partner's avatar and every glowing skeleton (restored by "release")</summary>
    static void Clean(SmplxAvatar keep, bool on)
    {
        foreach (Renderer r in hidden)
        {
            if (r != null) r.enabled = true;
        }

        hidden.Clear();
        HeadMovement hm = HeadMovement.Instance;
        if (hiddenPartner != null) hiddenPartner.SetVisible(hm == null || hm.LayerVisible("avatars"));
        hiddenPartner = null;
        if (!on || keep == null || hm == null) return;
        foreach (KeyValuePair<Role, SmplxAvatar> kv in hm.Avatars)
        {
            if (kv.Value == null || kv.Value == keep) continue;
            kv.Value.SetVisible(false);
            hiddenPartner = kv.Value;
        }

        foreach (Dancer d in new[] { hm.LeadDancer, hm.FollowDancer })
        {
            if (d == null) continue;
            foreach (Renderer r in d.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled) continue;
                r.enabled = false;
                hidden.Add(r);
            }
        }
    }

    static VRTKLite.Controllers.CameraControl Control()
    {
        GameObject simulator = GameObject.Find("Simulator");
        VRTKLite.Controllers.CameraControl control = simulator != null ? simulator.GetComponent<VRTKLite.Controllers.CameraControl>() : null;
        if (control == null) throw new InvalidOperationException("no Simulator/CameraControl in the scene");
        return control;
    }

    static string Frame(SmplxAvatar avatar, float azimuth, float elevation, float radius, float drop, bool headFrame)
    {
        VRTKLite.Controllers.CameraControl control = Control();
        control.enabled = false;
        Transform head = avatar.Bone((int)SmplJoint.Head);
        Vector3 facing = head.rotation * new Vector3(0, 0, -1); // the skin faces -z at rest
        Vector3 up = Vector3.up;
        if (headFrame)
        {
            up = head.rotation * Vector3.up;
        }
        else
        {
            facing.y = 0;
        }

        facing = facing.sqrMagnitude > 1e-6f ? facing.normalized : Vector3.forward;
        // + azimuth = toward her right (Unity's left-handed rotation about +y turns her facing -z toward -x = her right)
        Vector3 dir = Quaternion.AngleAxis(azimuth, up) * facing;
        dir = Quaternion.AngleAxis(-elevation, Vector3.Cross(up, dir)) * dir;
        Vector3 target = head.position + Vector3.down * drop;
        control.transform.position = target + dir.normalized * radius;
        control.transform.LookAt(target, up);
        return JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["camera"] = new[] { control.transform.position.x, control.transform.position.y, control.transform.position.z },
            ["target"] = new[] { target.x, target.y, target.z }, ["azimuth"] = azimuth, ["elevation"] = elevation, ["radius"] = radius
        });
    }

    static string Release()
    {
        VRTKLite.Controllers.CameraControl control = Control();
        control.enabled = true;
        control.SetOrbit(control.Azimuth, control.Polar, control.Radius);
        return "released";
    }
}
