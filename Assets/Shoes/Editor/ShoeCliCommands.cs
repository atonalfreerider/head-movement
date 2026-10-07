using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Unity CLI surface for the dancers' procedural sneakers (Assets/Shoes/SneakerPair). Play mode, a v3 capture loaded.
///
///   unity command hm_shoes                                              # state of both pairs
///   unity command hm_shoes --action set --visible false --toebend 0.8 --contact true --plant true --managefeet true
///   unity command hm_shoes --action frame --role follow --azimuth 35 --elevation 12 --radius 0.9 [--foot both|left|right]
///   unity command hm_shoes --action release | sync | sweep
/// </summary>
public static class ShoeCliCommands
{
    static readonly List<Renderer> hidden = new();
    static SmplxAvatar hiddenPartner;

    [CliCommand("hm_shoes", "Sneakers: state | set (--visible --toebend --contact --plant --managefeet --footcut --specular --light) | frame (--role --azimuth --elevation --radius --foot --height --clean: park the camera at the feet; clean hides the partner and the skeletons) | release | sync (finish a pending load) | rebuild (warm load timing) | sweep (whole capture on the real bones: shoe mesh vs floor, every visible leg point vs the shoe and the tongue, cut edge, contact jumps, stance hover, toe bend, review moments)")]
    public static string Shoes(
        [CliArg("action", "state|set|frame|release|sync|sweep")] string action = "state",
        [CliArg("role", "lead|follow (frame; set/sweep: one pair, default both)")] string role = null,
        [CliArg("visible", "set: show the shoes (true|false)")] string visible = null,
        [CliArg("toebend", "set: toe bend scale")] float toebend = float.NaN,
        [CliArg("contact", "set: floor contact correction (true|false)")] string contact = null,
        [CliArg("plant", "set: plant snap (true|false)")] string plant = null,
        [CliArg("managefeet", "set: clip the bare feet while the shoes show (true|false)")] string managefeet = null,
        [CliArg("footcut", "set: SmplxAvatar.FootCut in m relative to the ankle joint")] float footcut = float.NaN,
        [CliArg("specular", "set: shoe material sheen 0..1")] float specular = float.NaN,
        [CliArg("light", "set: shoe material light influence 0..1")] float light = float.NaN,
        [CliArg("azimuth", "frame: degrees around the feet, 0 = in front of the dancer")] float azimuth = 35f,
        [CliArg("elevation", "frame: degrees above the horizon")] float elevation = 12f,
        [CliArg("radius", "frame: metres from the feet (0 = fit both shoes)")] float radius = 0f,
        [CliArg("foot", "frame: both|left|right")] string foot = "both",
        [CliArg("height", "frame: look-at height above the floor (m)")] float height = 0.07f,
        [CliArg("clean", "frame: hide the partner avatar and the glowing skeletons until release (true|false)")] string clean = "true")
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("enter Play mode first (unity command editor_play)");
        List<SneakerPair> pairs = SneakerPair.All.Where(p => p != null && p.Avatar != null &&
            (string.IsNullOrEmpty(role) || string.Equals(p.Avatar.DancerRole.ToString(), role, StringComparison.OrdinalIgnoreCase))).ToList();
        switch ((action ?? "state").ToLowerInvariant())
        {
            case "state":
                return JsonConvert.SerializeObject(Report(pairs, p => p.State()));
            case "sync":
                foreach (SneakerPair p in pairs) p.Sync();
                return JsonConvert.SerializeObject(Report(pairs, p => p.State()));
            case "set":
                foreach (SneakerPair p in pairs)
                {
                    p.Sync(); // finish a pending load first
                    if (visible != null) p.Visible = bool.Parse(visible);
                    if (!float.IsNaN(toebend)) p.ToeBendScale = toebend;
                    if (contact != null) p.Contact = bool.Parse(contact);
                    if (plant != null) p.Plant = bool.Parse(plant);
                    if (managefeet != null)
                    {
                        p.ManageFeet = bool.Parse(managefeet);
                        if (!p.ManageFeet) p.Avatar.HideFeet(false);
                    }

                    if (!float.IsNaN(footcut)) p.Avatar.FootCut = footcut;
                    Renderer r = p.GetComponent<SkinnedMeshRenderer>();
                    foreach (Material m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        if (!float.IsNaN(specular)) m.SetFloat("_Specular", specular);
                        if (!float.IsNaN(light)) m.SetFloat("_LightInfluence", light);
                    }

                    // apply now (the CLI may screenshot before the next LateUpdate)
                    if (p.ManageFeet && p.Avatar.FeetHidden != p.Visible) p.Avatar.HideFeet(p.Visible);
                    r.enabled = p.Visible && p.Avatar.Visible && p.Avatar.CurrentFrame >= 0;
                    p.Sync();
                }

                return JsonConvert.SerializeObject(Report(pairs, p => p.State()));
            case "frame":
                if (pairs.Count == 0) throw new InvalidOperationException("no shoes for that role (load a capture with avatars)");
                Clean(pairs[0], clean == null || bool.Parse(clean));
                return Frame(pairs[0], azimuth, elevation, radius, foot, height);
            case "release":
                Clean(null, false);
                return Release();
            case "rebuild":
            {
                // warm load cost: rebuild each pair 3 times, report the fastest (editor GC pauses are not load cost)
                string json = HeadMovement.Instance != null ? HeadMovement.Instance.Manifest?.OptionalPath("shoes.json") : null;
                Dictionary<string, object> report = new() { ["pairs"] = pairs.Count };
                foreach (SneakerPair p0 in pairs)
                {
                    SneakerPair p = p0;
                    Dictionary<string, object> best = null;
                    for (int rep = 0; rep < 3; rep++)
                    {
                        p = p.Rebuild(json);
                        Dictionary<string, object> st = p.State();
                        if (best == null || (double)st["loadMs"] < (double)best["loadMs"]) best = st;
                    }

                    p.Sync();
                    report[p.Avatar.DancerRole.ToString().ToLowerInvariant()] = best;
                }

                return JsonConvert.SerializeObject(report);
            }
            case "sweep":
                return JsonConvert.SerializeObject(Report(pairs, p => p.Sweep()));
            default:
                throw new ArgumentException($"unknown action '{action}' (state|set|frame|release|sync|sweep)");
        }
    }

    /// <summary>review close-ups: hide the partner's avatar and every glowing skeleton (restored by "release")</summary>
    static void Clean(SneakerPair p, bool on)
    {
        foreach (Renderer r in hidden)
        {
            if (r != null) r.enabled = true;
        }

        hidden.Clear();
        HeadMovement hm = HeadMovement.Instance;
        if (hiddenPartner != null) hiddenPartner.SetVisible(hm == null || hm.LayerVisible("avatars"));
        hiddenPartner = null;
        if (!on || p == null || hm == null) return;
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

        foreach (KeyValuePair<Role, SmplxAvatar> kv in hm.Avatars)
        {
            if (kv.Value == null || kv.Value == p.Avatar) continue;
            kv.Value.SetVisible(false);
            hiddenPartner = kv.Value;
        }
    }

    static Dictionary<string, object> Report(List<SneakerPair> pairs, Func<SneakerPair, Dictionary<string, object>> f)
    {
        Dictionary<string, object> d = new() { ["pairs"] = pairs.Count };
        foreach (SneakerPair p in pairs) d[p.Avatar.DancerRole.ToString().ToLowerInvariant()] = f(p);
        return d;
    }

    /// <summary>
    /// Review camera: park the desktop camera (CameraControl disabled) at azimuth / elevation / radius around a
    /// dancer's feet, azimuth from the dancer's facing (pelvis forward), looking at the feet's midpoint at the given
    /// height. "release" hands the camera back.
    /// </summary>
    static string Frame(SneakerPair p, float azimuth, float elevation, float radius, string foot, float height)
    {
        GameObject simulator = GameObject.Find("Simulator");
        VRTKLite.Controllers.CameraControl control = simulator != null ? simulator.GetComponent<VRTKLite.Controllers.CameraControl>() : null;
        if (control == null) throw new InvalidOperationException("no Simulator/CameraControl in the scene");
        p.Sync();
        control.enabled = false;
        SmplxAvatar a = p.Avatar;
        Transform rig = control.transform;
        Vector3 l = p.ShoeCentre(0), r = p.ShoeCentre(1);
        string which = (foot ?? "both").ToLowerInvariant();
        Vector3 target = which switch { "left" => l, "right" => r, _ => 0.5f * (l + r) };
        target.y = Mathf.Max(target.y, a.transform.position.y + height);
        if (!(radius > 0f))
        {
            Vector3 d = l - r;
            d.y = 0f;
            radius = which == "both" ? Mathf.Clamp(0.40f + 1.0f * d.magnitude, 0.5f, 1.1f) : 0.5f;
        }
        Vector3 facing = a.Bone(0).rotation * new Vector3(0, 0, -1); // the skin faces -z at rest
        facing.y = 0;
        facing = facing.sqrMagnitude > 1e-6f ? facing.normalized : Vector3.forward;
        Vector3 dir = Quaternion.AngleAxis(azimuth, Vector3.up) * facing;
        dir = Quaternion.AngleAxis(-elevation, Vector3.Cross(Vector3.up, dir)) * dir;
        SneakerReviewCamera.Hold(rig, target + dir.normalized * radius, target);
        Camera cam = Camera.main;
        Vector3 cp = cam != null ? cam.transform.position : rig.position;
        float offAxis = cam != null ? Vector3.Angle(cam.transform.forward, target - cp) : float.NaN;
        return JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["camera"] = new[] { cp.x, cp.y, cp.z }, ["offAxisDeg"] = offAxis,
            ["target"] = new[] { target.x, target.y, target.z }, ["azimuth"] = azimuth, ["elevation"] = elevation,
            ["radius"] = radius, ["role"] = a.DancerRole.ToString().ToLowerInvariant()
        });
    }

    static string Release()
    {
        GameObject simulator = GameObject.Find("Simulator");
        VRTKLite.Controllers.CameraControl control = simulator != null ? simulator.GetComponent<VRTKLite.Controllers.CameraControl>() : null;
        if (simulator != null) SneakerReviewCamera.Release(simulator.transform);
        if (control != null) control.enabled = true;
        return "released";
    }
}
