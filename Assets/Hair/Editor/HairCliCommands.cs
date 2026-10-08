using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Unity CLI surface for the follow's hair (Assets/Hair/HairStrands). Play mode, a capture with hair_groom.json loaded.
///
///   unity command hm_hair                                   # state: counts, LOD, face clearance, opacity, bake, CPU ms
///   unity command hm_hair --action peaks --count 3          # fastest head moments (audio times) of the follow
///   unity command hm_hair --action set --tipglow 0 --opacity 0.65 --visible true --damping 2.4 --mode cards --lod auto
///   unity command hm_hair --action set --opacity -1         # back to the avatar's opacity + 0.15 (ramped near 0)
///   unity command hm_hair --action set --frshape 0.6 --frfalloff 0.5 --frdev 0.35   # front-right section dynamics
///   unity command hm_hair --action frame --azimuth 0 --elevation 5 --radius 0.9 --solo true   # park the review camera
///   unity command hm_hair --action sweep [--live true]      # whole take: stability, face box, spread, CPU
///   unity command hm_hair --action bake                     # re-bake now (blocking): ms, memory
///   unity command hm_hair --action probe --opacities 1,0.6,0.45,0.3   # off-screen HDR renders (post off): fade, tip glow, colour
///   unity command hm_hair --action release | sync | reset | stats_reset
/// </summary>
public static class HairCliCommands
{
    static readonly List<GameObject> soloHidden = new();

    static HairStrands Require()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("enter Play mode first (unity command editor_play)");
        HairStrands hair = HairStrands.Active;
        if (hair == null) throw new InvalidOperationException("no hair: load a capture with hair_groom.json (python -m dancecap.hair_groom <take>)");
        return hair;
    }

    [CliCommand("hm_hair", "Follow hair: state | peaks (fastest head moments) | set (--tipglow --glow --opacity --visible --damping --substeps --mode --lod --layers --colour --faceguard --arms --bake --frshape --frfalloff --frdev --fraz --sdaz --sdshape --sdfalloff --sddev) | frame (--azimuth --elevation --radius --solo around her head) | release | sweep (--live) | bake (re-bake, blocking) | probe (--opacities: off-screen HDR renders with post-processing off: fade, glow, colour) | sync | reset | stats_reset")]
    public static string Hair(
        [CliArg("action", "state|peaks|set|frame|release|sweep|bake|probe|sync|reset|stats_reset")] string action = "state",
        [CliArg("count", "peaks: how many moments")] int count = 3,
        [CliArg("tipglow", "set: tip emission scale (1 = the original bloom, 0 = off)")] float tipglow = float.NaN,
        [CliArg("glow", "set: tip glow on|off")] string glow = null,
        [CliArg("opacity", "set: hair opacity override 0..1; negative = follow the avatar (+0.15)")] float opacity = float.NaN,
        [CliArg("visible", "set: show the hair (true|false)")] string visible = null,
        [CliArg("damping", "set: velocity damping 1/s")] float damping = float.NaN,
        [CliArg("substeps", "set: substeps per capture frame")] int substeps = 0,
        [CliArg("shape", "set: shape stiffness at the root (fraction back to the rest shape per 1/30 s)")] float shape = float.NaN,
        [CliArg("falloff", "set: shape stiffness falloff (arc fraction per e-fold)")] float falloff = float.NaN,
        [CliArg("bend", "set: bending compliance")] float bend = float.NaN,
        [CliArg("preroll", "set: frames simulated before the shown frame after a seek (live path)")] int preroll = -1,
        [CliArg("iterations", "set: constraint iterations per substep")] int iterations = 0,
        [CliArg("mode", "set: cards (game-style hair) | ribbons (the previous ribbons, A/B)")] string mode = null,
        [CliArg("lod", "set: auto | 0 | 1 | 2")] string lod = null,
        [CliArg("layers", "set: layer mask (int, or names: cap,inner,mid,outer,part,flyaway | all)")] string layers = null,
        [CliArg("colour", "set: albedo source video | portrait")] string colour = null,
        [CliArg("faceguard", "set: face exclusion box on|off (true|false)")] string faceguard = null,
        [CliArg("arms", "set: arm capsules on|off (true|false)")] string arms = null,
        [CliArg("bake", "set: use the bake (true|false)")] string bake = null,
        [CliArg("frshape", "set: front-right section shape stiffness at the root")] float frshape = float.NaN,
        [CliArg("frfalloff", "set: front-right section shape falloff (arc fraction per e-fold)")] float frfalloff = float.NaN,
        [CliArg("frdev", "set: front-right section deviation cone (x arc length)")] float frdev = float.NaN,
        [CliArg("fraz", "set: front-right dynamics zone: right of the part, |azimuth| below this (deg)")] float fraz = float.NaN,
        [CliArg("sdaz", "set: right-side dynamics zone beyond the front-right one: |azimuth| below this (deg)")] float sdaz = float.NaN,
        [CliArg("sdshape", "set: right-side zone shape stiffness at the root")] float sdshape = float.NaN,
        [CliArg("sdfalloff", "set: right-side zone shape falloff")] float sdfalloff = float.NaN,
        [CliArg("sddev", "set: right-side zone deviation cone")] float sddev = float.NaN,
        [CliArg("azimuth", "frame: degrees around her head, 0 = in front of her face, + = toward her right")] float azimuth = 0f,
        [CliArg("elevation", "frame: degrees above her head's horizon")] float elevation = 10f,
        [CliArg("radius", "frame: metres from her head")] float radius = 1.4f,
        [CliArg("drop", "frame: look-at point below the head centre (m)")] float drop = 0.25f,
        [CliArg("solo", "frame: hide the lead while the camera is parked (true|false)")] string solo = null,
        [CliArg("live", "sweep: simulate live instead of reading the bake (true|false)")] string live = null,
        [CliArg("opacities", "probe: comma-separated hair opacities")] string opacities = "1,0.6,0.45,0.3",
        [CliArg("width", "probe: render width (px)")] int width = 960,
        [CliArg("height", "probe: render height (px)")] int height = 540)
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
                if (hair.UseBake)
                {
                    if (!hair.BakeStarted) hair.Rebake(true);
                    else hair.PumpBake(true);
                }

                hair.Advance(hair.Avatar.CurrentFrame);
                hair.Refresh();
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
            case "bake":
                hair = Require();
                return JsonConvert.SerializeObject(hair.Rebake(true));
            case "set":
                hair = Require();
                bool dyn = false;
                if (!float.IsNaN(tipglow)) hair.TipGlow = Mathf.Max(0f, tipglow);
                if (glow != null) hair.TipGlow = OnOff(glow) ? 1f : 0f;
                if (!float.IsNaN(opacity)) hair.OpacityOverride = opacity < 0f ? -1f : Mathf.Clamp01(opacity);
                if (visible != null) hair.Visible = OnOff(visible);
                if (!float.IsNaN(damping)) (hair.Damping, dyn) = (damping, true);
                if (substeps > 0) (hair.Substeps, dyn) = (substeps, true);
                if (!float.IsNaN(shape)) (hair.ShapeRoot, dyn) = (shape, true);
                if (!float.IsNaN(falloff)) (hair.ShapeFalloff, dyn) = (falloff, true);
                if (!float.IsNaN(bend)) (hair.BendCompliance, dyn) = (bend, true);
                if (preroll >= 0) hair.PreRollFrames = preroll;
                if (iterations > 0) (hair.Iterations, dyn) = (iterations, true);
                if (faceguard != null) (hair.FaceGuard, dyn) = (OnOff(faceguard), true);
                if (arms != null) (hair.ArmCollisions, dyn) = (OnOff(arms), true);
                if (bake != null) (hair.UseBake, dyn) = (OnOff(bake), true);
                if (!float.IsNaN(frshape)) (hair.FrontRightShapeRoot, dyn) = (frshape, true);
                if (!float.IsNaN(frfalloff)) (hair.FrontRightFalloff, dyn) = (frfalloff, true);
                if (!float.IsNaN(frdev)) (hair.MaxDeviationFrontRight, dyn) = (frdev, true);
                if (!float.IsNaN(fraz)) (hair.FrontRightAbsAz, dyn) = (fraz, true);
                if (!float.IsNaN(sdaz)) (hair.SideAbsAz, dyn) = (sdaz, true);
                if (!float.IsNaN(sdshape)) (hair.SideShapeRoot, dyn) = (sdshape, true);
                if (!float.IsNaN(sdfalloff)) (hair.SideFalloff, dyn) = (sdfalloff, true);
                if (!float.IsNaN(sddev)) (hair.MaxDeviationSide, dyn) = (sddev, true);
                if (!float.IsNaN(fraz) || !float.IsNaN(sdaz)) hair.UpdateFrontRightZone();
                if (mode != null) hair.SetMode(mode.ToLowerInvariant().StartsWith("rib") ? HairStrands.RenderMode.Ribbons : HairStrands.RenderMode.Cards);
                if (lod != null) hair.LodOverride = lod.ToLowerInvariant() == "auto" ? -1 : Mathf.Clamp(int.Parse(lod, CultureInfo.InvariantCulture), 0, 2);
                if (layers != null) hair.LayerMask = ParseLayers(layers);
                if (colour != null && !hair.SetColourSource(colour.ToLowerInvariant()))
                    throw new ArgumentException($"no '{colour}' albedo in hair_groom.json (python -m dancecap.hair_groom <take> --restyle)");
                if (dyn) hair.ResetSimulation();
                hair.Refresh();
                state = hair.State();
                break;
            case "peaks":
                hair = Require();
                return JsonConvert.SerializeObject(new Dictionary<string, object> { ["peaks"] = Peaks(hair, count) });
            case "frame":
                hair = Require();
                if (solo != null) Solo(OnOff(solo));
                return Frame(hair, azimuth, elevation, radius, drop);
            case "release":
                Solo(false);
                return Release();
            case "sweep":
                hair = Require();
                return JsonConvert.SerializeObject(hair.Sweep(live != null && OnOff(live)));
            case "dump":
                hair = Require();
                string file = System.IO.Path.Combine(Application.temporaryCachePath, "hair_dump.json");
                System.IO.File.WriteAllText(file, hair.Dump());
                return JsonConvert.SerializeObject(new Dictionary<string, object> { ["path"] = file });
            case "probe":
                hair = Require();
                return JsonConvert.SerializeObject(Probe(hair, opacities, width, height));
            default:
                throw new ArgumentException($"unknown action '{action}' (state|peaks|set|frame|release|sweep|bake|probe|sync|reset|stats_reset)");
        }

        // the original LineRenderer hair must be gone on v3 captures (it drew a white bar from the world origin)
        state["legacyHairObjects"] = UnityEngine.Object.FindObjectsByType<HairSimulation>().Length;
        // ... and no enabled LineRenderer may still hold Unity's default (0,0,0)-(0,0,1) points (a bar at the origin)
        state["defaultLineRenderers"] = UnityEngine.Object.FindObjectsByType<LineRenderer>()
            .Count(l => l.enabled && l.gameObject.activeInHierarchy && l.positionCount == 2 && l.GetPosition(0) == Vector3.zero &&
                        l.GetPosition(1) == Vector3.forward);
        return JsonConvert.SerializeObject(state);
    }

    static bool OnOff(string s) => s.ToLowerInvariant() is "true" or "on" or "1" or "yes";

    static int ParseLayers(string s)
    {
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)) return m;
        if (s.ToLowerInvariant() == "all") return 0x3F;
        int mask = 0;
        foreach (string part in s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
        {
            int i = Array.IndexOf(HairCardLayout.LayerNames, part.ToLowerInvariant());
            if (i < 0) throw new ArgumentException($"unknown hair layer '{part}' ({string.Join(", ", HairCardLayout.LayerNames)})");
            mask |= 1 << i;
        }

        return mask;
    }

    /// <summary>hide (or restore) the lead dancer for face-on review shots of the follow's hair</summary>
    static void Solo(bool on)
    {
        if (!on)
        {
            foreach (GameObject go in soloHidden)
                if (go != null) go.SetActive(true);
            soloHidden.Clear();
            return;
        }

        HeadMovement hm = HeadMovement.Instance;
        if (hm == null || hm.Avatars == null) return;
        foreach (KeyValuePair<Role, SmplxAvatar> kv in hm.Avatars)
        {
            if (kv.Value == null || kv.Value == HairStrands.Active?.Avatar || !kv.Value.gameObject.activeSelf) continue;
            kv.Value.gameObject.SetActive(false);
            soloHidden.Add(kv.Value.gameObject);
        }
    }

    /// <summary>
    /// Review camera: park the desktop orbit camera (its CameraControl paused) at azimuth / elevation / radius around
    /// the follow's head, azimuth measured from her facing direction (+ = toward her right), looking at a point `drop`
    /// below the head. "release" hands the camera back to the orbit control.
    /// </summary>
    static string Frame(HairStrands hair, float azimuth, float elevation, float radius, float drop)
    {
        GameObject simulator = GameObject.Find("Simulator");
        VRTKLite.Controllers.CameraControl control = simulator != null ? simulator.GetComponent<VRTKLite.Controllers.CameraControl>() : null;
        if (control == null) throw new InvalidOperationException("no Simulator/CameraControl in the scene");
        control.enabled = false;
        Transform head = hair.Avatar.Bone(15);
        Vector3 facing = head.rotation * new Vector3(0, 0, -1); // the skin faces -z at rest
        facing.y = 0;
        facing = facing.sqrMagnitude > 1e-6f ? facing.normalized : Vector3.forward;
        // + azimuth = toward her right (Unity's left-handed rotation about +y turns her facing -z toward -x = her right)
        Vector3 dir = Quaternion.AngleAxis(azimuth, Vector3.up) * facing;
        dir = Quaternion.AngleAxis(-elevation, Vector3.Cross(Vector3.up, dir)) * dir;
        Vector3 target = head.position + Vector3.down * drop;
        control.transform.position = target + dir.normalized * radius;
        control.transform.LookAt(target);
        return JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["camera"] = new[] { control.transform.position.x, control.transform.position.y, control.transform.position.z },
            ["target"] = new[] { target.x, target.y, target.z }, ["azimuth"] = azimuth, ["elevation"] = elevation,
            ["radius"] = radius, ["solo"] = soloHidden.Count > 0
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
    /// Off-screen HDR renders of the current camera (the review camera of --action frame), with the camera's
    /// post-processing OFF (no bloom, no tonemapping: the scene colour as the bloom pass would see it, values above 1.0
    /// kept): the hair hidden, then the hair at each opacity (glow off), then the glow on. Hair pixels = where the hair
    /// at opacity 1 differs from the hidden render. Reports the mean hair-pixel luminance per opacity (the fade must be
    /// monotonic and close to proportional over a dark background), HDR pixels above 1.0 with the glow on / off (only the
    /// tips may bloom), the glow's soft halo (pixels it brightens by 0.02..1 that are more than 2 px from its core), the
    /// brightest lit-body pixel (soft clamp) and the hair colour p10 / p50 / p90 by luminance (linear + sRGB hex).
    /// </summary>
    static Dictionary<string, object> Probe(HairStrands hair, string opacityList, int w, int h)
    {
        Camera cam = HairStrands.ViewCamera();
        if (cam == null) throw new InvalidOperationException("no camera");
        float[] ops = opacityList.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
        float keepOverride = hair.OpacityOverride, keepGlow = hair.TipGlow;
        bool keepVisible = hair.Visible;
        int msaa = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp ? Mathf.Max(1, urp.msaaSampleCount) : 1;
        RenderTexture rt = new(w, h, 24, RenderTextureFormat.ARGBHalf) { antiAliasing = msaa };
        Texture2D tex = new(w, h, TextureFormat.RGBAHalf, false, true);
        RenderTexture keepTarget = cam.targetTexture;
        UniversalAdditionalCameraData camData = cam.GetUniversalAdditionalCameraData();
        bool keepPost = camData != null && camData.renderPostProcessing;
        bool keepHdr = cam.allowHDR;
        if (camData != null) camData.renderPostProcessing = false;
        cam.allowHDR = true;

        Color[] Grab()
        {
            hair.Refresh();
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = keepTarget;
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = prevActive;
            return tex.GetPixels();
        }

        static float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        static float Max3(Color c) => Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        // the floor grid, floor craft and other glow overlays are HDR too: hidden while the hair is measured
        HeadMovement hm = HeadMovement.Instance;
        string[] overlayLayers = { "grid", "axis", "floorcraft", "traces", "neck", "counterbalance", "floor", "timing", "physics" };
        Dictionary<string, bool> keepLayers = new();
        if (hm != null)
        {
            foreach (string l in overlayLayers)
            {
                keepLayers[l] = hm.LayerVisible(l);
                if (keepLayers[l]) hm.SetLayerVisible(l, false);
            }

            DanceLayers.Instance?.Refresh();
        }

        // the glowing skeletons are HDR (bloom material) and pulse with the beat: hidden while the hair is measured
        GameObject[] skeletons = hm != null ? new[] { hm.LeadDancer != null ? hm.LeadDancer.gameObject : null, hm.FollowDancer != null ? hm.FollowDancer.gameObject : null } : new GameObject[0];
        bool[] skeletonsActive = skeletons.Select(g => g != null && g.activeSelf).ToArray();
        foreach (GameObject g in skeletons)
        {
            if (g != null) g.SetActive(false);
        }

        try
        {
            hair.Visible = false;
            Color[] bg = Grab();
            hair.Visible = true;
            hair.TipGlow = 0f;
            hair.OpacityOverride = 1f;
            Color[] full = Grab();
            List<int> mask = new();
            for (int i = 0; i < full.Length; i++)
                if (Mathf.Abs(Lum(full[i]) - Lum(bg[i])) > 0.004f || Max3(full[i] - bg[i]) > 0.01f) mask.Add(i);
            // the lit hair body over a dark background (A2C edge pixels mixed with a bright body excluded): p99.5
            List<float> body = mask.Where(i => Max3(bg[i]) < 0.15f).Select(i => Max3(full[i])).OrderBy(x => x).ToList();
            float bodyMax = body.Count > 0 ? body[(int)(0.995f * (body.Count - 1))] : 0f;
            int overOneNoGlow = full.Count(c => Max3(c) > 1f);

            // what is behind the hair: the hair at opacity 0.01 (its depth prepass still hides the body behind it)
            hair.OpacityOverride = 0.01f;
            Color[] behind = Grab();
            float lumBehind = mask.Count > 0 ? mask.Average(i => Lum(behind[i])) : 0f;
            Dictionary<string, object> fade = new();
            float lumFull = mask.Count > 0 ? mask.Average(i => Lum(full[i])) : 0f;
            float span = lumFull - lumBehind;
            List<float> ratios = new();
            foreach (float o in ops)
            {
                hair.OpacityOverride = o;
                Color[] img = Grab();
                float l = mask.Count > 0 ? mask.Average(i => Lum(img[i])) : 0f;
                float ratio = span > 1e-6f ? (l - lumBehind) / span : float.NaN;
                fade[o.ToString("0.###", CultureInfo.InvariantCulture)] = new Dictionary<string, object>
                {
                    ["meanLum"] = l, ["ratioToOpaque"] = lumFull > 1e-6f ? l / lumFull : float.NaN, ["hairShare"] = ratio
                };
                ratios.Add(ratio);
            }

            hair.OpacityOverride = keepOverride >= 0f ? keepOverride : -1f;
            hair.TipGlow = 1f;
            Color[] glowImg = Grab();
            int overOneGlow = glowImg.Count(c => Max3(c) > 1f);
            float maxGlowOn = glowImg.Max(Max3), maxGlowOff = full.Max(Max3);
            // the camera's off-screen output is clamped to 1 here, so the glow is measured as the pixels it lifts to
            // (near) white, and the glow-off hair must have none of those
            int glowDelta = 0, glowWhite = 0, whiteNoGlow = 0, glowCore = 0, glowHalo = 0;
            bool[] core = new bool[glowImg.Length];
            for (int i = 0; i < glowImg.Length; i++)
            {
                float dl = Max3(glowImg[i]) - Max3(full[i]);
                if (dl > 0.25f) glowDelta++;
                if (Max3(glowImg[i]) > 0.9f && Max3(full[i]) < 0.7f) glowWhite++;
                if (dl > 1f) (core[i], glowCore) = (true, glowCore + 1);
            }

            // the halo: brightened 0.02..1 and more than 2 px from any core pixel (above 1.0)
            for (int i = 0; i < glowImg.Length; i++)
            {
                float dl = Max3(glowImg[i]) - Max3(full[i]);
                if (dl <= 0.02f || dl > 1f) continue;
                int x = i % w, y = i / w;
                bool nearCore = false;
                for (int dy = -2; dy <= 2 && !nearCore; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h || !core[yy * w + xx]) continue;
                    nearCore = true;
                    break;
                }

                if (!nearCore) glowHalo++;
            }

            foreach (int i in mask)
                if (Max3(full[i]) > 0.9f && Max3(bg[i]) < 0.15f) whiteNoGlow++;

            // white balance as HAIR_REFERENCE 4: the bright reference region (bright, unsaturated body pixels of the hidden-hair render
            // near the hair) scaled to an albedo of 0.80
            int minX = w, maxX = 0, minY = h, maxY = 0;
            foreach (int i in mask)
            {
                minX = Mathf.Min(minX, i % w);
                maxX = Mathf.Max(maxX, i % w);
                minY = Mathf.Min(minY, i / w);
                maxY = Mathf.Max(maxY, i / w);
            }

            List<Color> top = new();
            int padX = (maxX - minX) / 2, padY = (maxY - minY) / 2;
            HashSet<int> hairSet = new(mask);
            for (int y = Mathf.Max(0, minY - padY); y <= Mathf.Min(h - 1, maxY + padY); y++)
            for (int x = Mathf.Max(0, minX - padX); x <= Mathf.Min(w - 1, maxX + padX); x++)
            {
                int i = y * w + x;
                Color c = bg[i];
                float mx = Max3(c), mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                if (!hairSet.Contains(i) && mx > 0.15f && mx < 0.98f && (mx - mn) / mx < 0.25f) top.Add(c);
            }

            Color white = new(1, 1, 1, 1);
            if (top.Count > 50)
            {
                top.Sort((a, b) => Lum(a).CompareTo(Lum(b)));
                List<Color> upper = top.GetRange(top.Count / 2, top.Count / 2);
                white = new Color(upper.Average(c => c.r), upper.Average(c => c.g), upper.Average(c => c.b), 1);
            }

            Color gain = new(0.8f / Mathf.Max(white.r, 1e-3f), 0.8f / Mathf.Max(white.g, 1e-3f), 0.8f / Mathf.Max(white.b, 1e-3f), 1);

            // colour statistics of the opaque hair (glow off): p10 / p50 / p90 by luminance
            List<Color> px = mask.Select(i => full[i]).OrderBy(Lum).ToList();
            Dictionary<string, object> stats = new();
            foreach ((string k, float q) in new[] { ("p10", 0.1f), ("p50", 0.5f), ("p90", 0.9f) })
            {
                if (px.Count == 0) break;
                int a = Mathf.Clamp((int)(q * (px.Count - 1)) - px.Count / 40, 0, px.Count - 1), b = Mathf.Clamp((int)(q * (px.Count - 1)) + px.Count / 40, 0, px.Count - 1);
                Color m = new(0, 0, 0, 0);
                for (int i = a; i <= b; i++) m += px[i];
                m /= Mathf.Max(1, b - a + 1);
                Color wb = new(m.r * gain.r, m.g * gain.g, m.b * gain.b, 1);
                stats[k] = new Dictionary<string, object>
                {
                    ["linear"] = new[] { m.r, m.g, m.b }, ["hex"] = "#" + ColorUtility.ToHtmlStringRGB(m.gamma),
                    ["whiteBalancedLinear"] = new[] { wb.r, wb.g, wb.b }, ["whiteBalancedHex"] = "#" + ColorUtility.ToHtmlStringRGB(wb.gamma)
                };
            }

            bool monotonic = true;
            for (int i = 1; i < ops.Length; i++)
                if ((ops[i] - ops[i - 1]) * (ratios[i] - ratios[i - 1]) < 0f) monotonic = false;
            float maxDev = 0f;
            for (int i = 0; i < ops.Length; i++) maxDev = Mathf.Max(maxDev, Mathf.Abs(ratios[i] - ops[i]));
            return new Dictionary<string, object>
            {
                ["hairPixels"] = mask.Count, ["pixels"] = full.Length, ["msaa"] = msaa, ["fade"] = fade, ["fadeMonotonic"] = monotonic,
                ["lumBehindHair"] = lumBehind, ["lumOpaqueHair"] = lumFull,
                ["fadeMaxDeviationFromProportional"] = maxDev, ["hdrPixelsGlowOn"] = overOneGlow, ["hdrPixelsGlowOff"] = overOneNoGlow,
                ["litBodyMaxLinear"] = bodyMax, ["colour"] = stats, ["maxGlowOn"] = maxGlowOn, ["maxGlowOff"] = maxGlowOff,
                ["glowBrightenedPixels"] = glowDelta, ["glowWhitePixels"] = glowWhite, ["whiteHairPixelsGlowOff"] = whiteNoGlow,
                ["glowCorePixelsAboveOne"] = glowCore, ["glowHaloPixels"] = glowHalo, ["postProcessing"] = false,
                ["whiteRef"] = new[] { white.r, white.g, white.b }, ["whiteRefPixels"] = top.Count, ["colourSource"] = hair.ColourSource
            };
        }
        finally
        {
            if (hm != null)
            {
                foreach (KeyValuePair<string, bool> kv in keepLayers)
                {
                    if (kv.Value) hm.SetLayerVisible(kv.Key, true);
                }
            }

            for (int i = 0; i < skeletons.Length; i++)
            {
                if (skeletons[i] != null && skeletonsActive[i]) skeletons[i].SetActive(true);
            }

            hair.OpacityOverride = keepOverride;
            hair.TipGlow = keepGlow;
            hair.Visible = keepVisible;
            hair.Refresh();
            cam.targetTexture = keepTarget;
            if (camData != null) camData.renderPostProcessing = keepPost;
            cam.allowHDR = keepHdr;
            UnityEngine.Object.Destroy(rt);
            UnityEngine.Object.Destroy(tex);
        }
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
