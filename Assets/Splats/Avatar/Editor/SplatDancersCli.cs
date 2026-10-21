using System;
using System.Collections.Generic;
using System.Linq;
using HmSplats;
using Newtonsoft.Json;
using Unity.Mathematics;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Splat dancers on the desktop (Play mode; Assets/Splats/Avatar/SplatDancers.cs):
///
///   unity command hm_splatdancers --action state
///   unity command hm_splatdancers --action on | off            # the "splat dancers" viewer toggle (default off)
///   unity command hm_splatdancers --action hair --mode skinned | hidden | only
///   unity command hm_splatdancers --action opacity --mode opaque | avatars | scale   # avatars = fade the splat image to the avatars' opacity
///   unity command hm_splatdancers --action path --mode composite | direct          # composite (default): overlays depth-test against the splats
///   unity command hm_splatdancers --action depth --mode before | after             # splat depth before (default) / after the transparent queue
///   unity command hm_splatdancers --action skeleton --mode physics | always | never  # the skeleton lines with the opaque splats: physics
///                                                                     # (default) = over the splats in the physics view, off elsewhere while the
///                                                                     # splats show / always over / never (depth-correct, inside the bodies)
///   unity command hm_splatdancers --action check [--samples 4096]   # GPU skinning vs the CPU reference (posed centres)
///   unity command hm_splatdancers --action hairmotion --mode strands | rigid      # bound hair on the simulated strands (default) / LHM++ LBS
///   unity command hm_splatdancers --action haircheck [--from s --to s]   # the hair pass: GPU vs CPU reference, swing, intrusion (head,
///                                                                     # own torso, partner); --from/--to: every frame of a span
///   unity command hm_splatdancers --action hairshot --out C:/abs/prefix --azimuths 0,90,180,270 [--radius 1.2 --elevation 8 --width 720 --height 960]
///                                                                     # off-screen stills around the follow's head (what is shown now: splats or avatars)
///   unity command hm_splatdancers --action perf_start | perf_report   # frame time and the Gaussian passes' GPU ms
/// </summary>
public static class SplatDancersCli
{
    [CliCommand("hm_splatdancers", "Splat dancers (skinned LHM++ Gaussians instead of the mesh avatars; default off): state | on | off | hair --mode skinned|hidden|only | hairmotion --mode strands|rigid | opacity --mode opaque|avatars|scale | path --mode composite|direct | depth --mode before|after | skeleton --mode physics|always|never (skeleton lines with the splats) | check [--samples N] (GPU skinning vs CPU reference) | haircheck (hair pass vs CPU reference, swing, head intrusion) | perf_start | perf_report (frame + Gaussian pass GPU ms)")]
    public static string Command(
        [CliArg("action", "state | on | off | hair | hairmotion | opacity | path | depth | skeleton | check | haircheck | perf_start | perf_report")] string action = "state",
        [CliArg("mode", "hair: skinned|hidden|only; hairmotion: strands|rigid; opacity: opaque|avatars|scale; path: composite|direct; depth: before|after (the transparents); skeleton: physics|always|never")] string mode = null,
        [CliArg("samples", "check: Gaussians per dancer")] int samples = 4096,
        [CliArg("from", "haircheck sweep: first dance second (omit: the shown frame only)")] float from = float.NaN,
        [CliArg("to", "haircheck sweep: last dance second")] float to = float.NaN,
        [CliArg("out", "hairshot: absolute output path prefix (<out>_az<deg>.png)")] string @out = null,
        [CliArg("azimuths", "hairshot: degrees around her head, 0 = in front of her body, + = toward her left")] string azimuths = "0,90,180,270",
        [CliArg("radius", "hairshot: metres from her head")] float radius = 1.2f,
        [CliArg("elevation", "hairshot: degrees above her head")] float elevation = 8f,
        [CliArg("drop", "hairshot: look-at point below her head joint (m)")] float drop = 0.22f,
        [CliArg("fov", "hairshot: vertical field of view (deg)")] float fov = 40f,
        [CliArg("width", "hairshot: pixels")] int width = 720,
        [CliArg("height", "hairshot: pixels")] int height = 960)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("enter Play mode first (unity command editor_play)");
        switch ((action ?? "state").Trim().ToLowerInvariant())
        {
            case "on":
                SplatDancers.SetEnabled(true);
                break;
            case "off":
                SplatDancers.SetEnabled(false);
                break;
            case "hair":
                SplatDancers.HairMode = (mode ?? "skinned").ToLowerInvariant() switch
                {
                    "hidden" => SkinnedSplatAvatar.HairModes.Hidden,
                    "only" => SkinnedSplatAvatar.HairModes.Only,
                    _ => SkinnedSplatAvatar.HairModes.Skinned
                };
                break;
            case "hairmotion":
                SplatDancers.HairMotion = (mode ?? "strands").ToLowerInvariant() is not ("rigid" or "lbs" or "off");
                break;
            case "haircheck":
                return JsonConvert.SerializeObject(HairCheck(from, to));
            case "hairshot":
                return JsonConvert.SerializeObject(HairShot(@out, azimuths, radius, elevation, drop, fov, width, height));
            case "opacity":
                SplatDancers.OpacityMode = (mode ?? "opaque").ToLowerInvariant() switch
                {
                    "scale" => SplatDancers.OpacityModes.ScaleSplats,
                    "avatars" or "follow" => SplatDancers.OpacityModes.FollowAvatars,
                    _ => SplatDancers.OpacityModes.Opaque
                };
                break;
            case "path":
                SplatDancers.RenderPath = (mode ?? "composite").ToLowerInvariant() == "direct" ? SplatDancers.Paths.Direct : SplatDancers.Paths.Composite;
                break;
            case "depth":
                SplatUrpFeature.DepthBeforeTransparents = (mode ?? "before").ToLowerInvariant() != "after";
                break;
            case "skeleton":
                SplatDancers.SkeletonOver = (mode ?? "physics").ToLowerInvariant() switch
                {
                    "always" => SplatDancers.SkeletonOverModes.Always,
                    "never" or "off" or "inside" => SplatDancers.SkeletonOverModes.Never,
                    _ => SplatDancers.SkeletonOverModes.Physics
                };
                break;
            case "check":
                return JsonConvert.SerializeObject(Check(Math.Max(16, samples)));
            case "perf_start":
                Perf.Start();
                return JsonConvert.SerializeObject(new { started = true, samplers = Perf.Names });
            case "perf_report":
                return JsonConvert.SerializeObject(Perf.Report());
            case "state":
                break;
            default:
                throw new ArgumentException($"unknown action '{action}'");
        }

        return JsonConvert.SerializeObject(SplatDancers.Instance != null ? SplatDancers.Instance.State() : new Dictionary<string, object> { ["enabled"] = SplatDancers.Enabled, ["instance"] = false });
    }

    /// <summary>read back posed Gaussians from the GPU frame and compare with SplatSkinning on the CPU (same bones)</summary>
    static object Check(int samples)
    {
        if (SplatDancers.Instance == null) throw new InvalidOperationException("splat dancers are not built (hm_splatdancers --action on)");
        List<object> rows = new();
        foreach (Role role in new[] { Role.Lead, Role.Follow })
        {
            SkinnedSplatAvatar s = SplatDancers.Instance.Splat(role);
            if (s == null) continue;
            s.SkinNow(true);
            SplatAvatarData d = s.Data;
            int stride = Math.Max(1, d.Count / samples);
            List<float> posErr = new(), rotErr = new();
            for (int i = 0; i < d.Count; i += stride)
            {
                float4[] g = s.ReadBack(i, 1);
                SplatSkinning.Pose(d, s.CurrentSkin, i, out float3 p, out float4 q);
                posErr.Add(math.length(g[0].xyz - p) * 1000f);
                float dot = math.abs(math.dot(math.normalize(g[2]), q));
                rotErr.Add(math.degrees(2f * math.acos(math.min(1f, dot))));
            }

            posErr.Sort();
            rotErr.Sort();
            // FK against the avatar: the splat skeleton's posed joints vs the avatar's bones (rest-joint offsets of the
            // two skeletons, 10 vs 16 betas)
            HeadMovement hm = HeadMovement.Instance;
            SmplxAvatar avatar = hm != null && hm.Avatars.TryGetValue(role, out SmplxAvatar a) ? a : null;
            float fkMax = 0f, fkMedian = 0f;
            if (avatar != null)
            {
                List<float> fk = new();
                for (int j = 0; j < SplatAvatarData.Joints; j++)
                {
                    Vector3 splatJoint = s.CurrentSkin[j].MultiplyPoint3x4((Vector3)d.RestJoints[j]);
                    fk.Add((splatJoint - avatar.BonePosition(j)).magnitude * 1000f);
                }

                fk.Sort();
                fkMax = fk[^1];
                fkMedian = fk[fk.Count / 2];
            }

            rows.Add(new
            {
                role = role.ToString(), samples = posErr.Count,
                gpuVsCpuMm = new { median = posErr[posErr.Count / 2], p99 = posErr[(int)(posErr.Count * 0.99)], max = posErr[^1] },
                gpuVsCpuRotDeg = new { median = rotErr[rotErr.Count / 2], max = rotErr[^1] },
                splatJointsVsAvatarBonesMm = new { median = fkMedian, max = fkMax },
                frame = avatar != null ? avatar.CurrentFrame : -1
            });
        }

        return rows;
    }

    /// <summary>the hair pass of the dancer with bound hair (the follow): GPU (read back) vs SplatHairBinding.Pose on the
    /// CPU with the same segments; how far the strands moved the hair from the head-rigid / LBS pose; bound centres inside
    /// her head (a sphere of 0.85 x the groom's head radius about the splat head), inside her own torso / neck capsules and
    /// inside the PARTNER (capsules on his bones: head, neck, torso, arms; the simulation has no partner colliders), each
    /// also for the rigid LBS hair as the baseline of the crude capsules. sweep: from..to dance seconds, every frame posed
    /// and simulated here (both avatars' bones, the hair's Advance), then the viewer is put back on its frame.</summary>
    static object HairCheck(float from = float.NaN, float to = float.NaN)
    {
        if (SplatDancers.Instance == null) throw new InvalidOperationException("splat dancers are not built (hm_splatdancers --action on)");
        SkinnedSplatAvatar s = SplatDancers.Instance.Splat(Role.Follow);
        if (s == null || s.HairBinding == null) throw new InvalidOperationException("the follow has no bound hair");
        HeadMovement hm = HeadMovement.Instance;
        SmplxAvatar follow = hm.Avatars.TryGetValue(Role.Follow, out SmplxAvatar fa) ? fa : null;
        SmplxAvatar lead = hm.Avatars.TryGetValue(Role.Lead, out SmplxAvatar la) ? la : null;
        HairStrands hair = (s.HairSource as HairStrandsSplatSource)?.Hair;
        if (follow == null || hair == null) throw new InvalidOperationException("no follow avatar / hair source");
        if (float.IsNaN(from)) return OneFrame(s, hair, follow, lead, null, true);

        int f0 = Mathf.Max(0, hm.Timeline.FrameAt(from)), f1 = Mathf.Max(f0, hm.Timeline.FrameAt(to));
        int shown = follow.CurrentFrame;
        float4[] prev = null;
        List<float> stepMax = new(), stepP99 = new(), guideStepMax = new();
        int headIn = 0, partnerIn = 0, partnerInRigid = 0, ownIn = 0, ownInRigid = 0, frames = 0, worstPartnerFrame = -1, worstPartner = 0;
        int worstHeadFrame = -1, worstOwnFrame = -1;
        List<int[]> perFrame = new();
        float3[] gPrev = null;
        try
        {
            for (int f = f0; f <= f1; f++)
            {
                lead?.PoseBones(f);
                hair.Advance(f);
                follow.PoseBones(f);
                Stats st = (Stats)OneFrame(s, hair, follow, lead, prev, false);
                float4[] cur = st.Positions;
                float3[] g = new float3[hair.Guides * hair.Points];
                hair.SplatFrame(g, out _, out _);
                if (prev != null)
                {
                    stepMax.Add(st.StepMax);
                    stepP99.Add(st.StepP99);
                    float gm = 0;
                    for (int i = 0; i < g.Length; i++) gm = math.max(gm, math.length(g[i] - gPrev[i]));
                    guideStepMax.Add(gm * 1000f);
                }

                prev = cur;
                gPrev = g;
                if (st.InsideHead > headIn) worstHeadFrame = f;
                if (st.InsideOwn > ownIn) worstOwnFrame = f;
                perFrame.Add(new[] { f, st.InsideHead, st.InsideOwn, st.InsideOwnRigid, st.InsidePartner, st.InsidePartnerRigid });
                headIn = Math.Max(headIn, st.InsideHead);
                ownIn = Math.Max(ownIn, st.InsideOwn);
                ownInRigid = Math.Max(ownInRigid, st.InsideOwnRigid);
                partnerInRigid = Math.Max(partnerInRigid, st.InsidePartnerRigid);
                if (st.InsidePartner > worstPartner)
                {
                    worstPartner = st.InsidePartner;
                    worstPartnerFrame = f;
                }

                partnerIn = Math.Max(partnerIn, st.InsidePartner);
                frames++;
            }
        }
        finally
        {
            lead?.PoseBones(shown);
            hair.Advance(shown);
            follow.PoseBones(shown);
            s.SkinNow(true);
        }

        return new
        {
            frames, from = f0, to = f1, bound = s.HairBinding.Bound,
            stepMaxMm = Q(stepMax), stepP99Mm = Q(stepP99), guidePointStepMaxMm = Q(guideStepMax),
            maxInsideHead = headIn, maxInsideOwnTorso = ownIn, maxInsideOwnTorsoRigid = ownInRigid,
            maxInsidePartner = partnerIn, maxInsidePartnerRigid = partnerInRigid, worstPartnerFrame, worstHeadFrame, worstOwnFrame,
            framesWithHeadIntrusion = perFrame.Count(r => r[1] > 0), framesWithOwnTorso = perFrame.Count(r => r[2] > 0),
            perFrame = "frame, insideHead, insideOwnTorso, insideOwnTorsoRigid, insidePartner, insidePartnerRigid",
            rows = perFrame
        };
    }

    sealed class Stats
    {
        public float4[] Positions;
        public float StepMax, StepP99;
        public int InsideHead, InsideOwn, InsideOwnRigid, InsidePartner, InsidePartnerRigid;
        public object Report;
    }

    static float CapsuleDepth(Vector3 p, Vector3 a, Vector3 b, float r)
    {
        Vector3 ab = b - a;
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-9f));
        return r - (p - (a + ab * t)).magnitude;
    }

    static readonly (int a, int b, float r)[] PartnerCapsules =
    {
        (15, 15, 0.09f), (12, 15, 0.06f), (0, 3, 0.13f), (3, 9, 0.13f), (9, 12, 0.12f),
        (16, 18, 0.045f), (17, 19, 0.045f), (18, 20, 0.035f), (19, 21, 0.035f)
    };

    // her own torso / neck (the hair hangs over them: a Gaussian deep inside is through the body); radii a bit inside
    // the groom's colliders
    static readonly (int a, int b, float r)[] OwnCapsules = { (3, 9, 0.08f), (9, 12, 0.07f), (12, 15, 0.04f) };

    static object OneFrame(SkinnedSplatAvatar s, HairStrands hair, SmplxAvatar follow, SmplxAvatar lead, float4[] prev, bool report)
    {
        s.SkinNow(true);
        SplatHairBinding hb = s.HairBinding;
        SplatAvatarData d = s.Data;
        float4[] all = s.ReadBack(0, d.Count);
        float4[] segCpu = s.HairSegmentsCpu();
        Matrix4x4 s15 = s.CurrentSkin[SplatHairBinding.HeadJoint];
        Vector3 headCentre = s15.MultiplyPoint3x4((Vector3)(d.RestJoints[SplatHairBinding.HeadJoint] + (float3)hair.SplatHeadCentreLocal));
        float headR = hair.HeadRadius;
        List<float> gpuCpu = new(), rotErr = new(), swingRigid = new(), swingLbs = new(), attachTop = new(), steps = new();
        Stats st = new() { Positions = new float4[hb.Bound] };
        Vector3 J(SmplxAvatar a, int j) => a.BonePosition(j);
        Vector3 headUp(SmplxAvatar a) => a.Bone(15).TransformPoint(new Vector3(0, 0.06f, 0));
        for (int b = 0; b < hb.Bound; b++)
        {
            int i = hb.Index[b];
            float3 gp = all[i * 3].xyz;
            float4 gq = all[i * 3 + 2];
            st.Positions[b] = new float4(gp, 0);
            float3 rigid = s15.MultiplyPoint3x4((Vector3)d.Mean[i]);
            SplatSkinning.Pose(d, s.CurrentSkin, i, out float3 lbs, out _);
            if (report && s.HairLive)
            {
                hb.Pose(b, segCpu, rigid, s.HairHeadRot, out float3 p, out float4 q);
                gpuCpu.Add(math.length(gp - p) * 1000f);
                float dot = math.abs(math.dot(math.normalize(gq), q));
                rotErr.Add(math.degrees(2f * math.acos(math.min(1f, dot))));
            }

            if (prev != null) steps.Add(math.length(gp - prev[b].xyz) * 1000f);
            swingRigid.Add(math.length(gp - rigid) * 1000f);
            swingLbs.Add(math.length(gp - lbs) * 1000f);
            if (hb.Attach[b] > 0f && hb.Attach[b] < 0.2f) attachTop.Add(math.length(gp - rigid) * 1000f);
            if (((Vector3)gp - headCentre).magnitude < 0.85f * headR) st.InsideHead++;
            foreach ((int a, int c, float r) in OwnCapsules)
            {
                if (CapsuleDepth(gp, J(follow, a), J(follow, c), r) > 0) { st.InsideOwn++; break; }
            }

            foreach ((int a, int c, float r) in OwnCapsules)
            {
                if (CapsuleDepth(lbs, J(follow, a), J(follow, c), r) > 0) { st.InsideOwnRigid++; break; }
            }

            if (lead != null)
            {
                foreach ((int a, int c, float r) in PartnerCapsules)
                {
                    Vector3 pa = a == 15 && c == 15 ? headUp(lead) : J(lead, a), pc = a == 15 && c == 15 ? headUp(lead) : J(lead, c);
                    if (CapsuleDepth(gp, pa, pc, r) > 0) { st.InsidePartner++; break; }
                }

                foreach ((int a, int c, float r) in PartnerCapsules)
                {
                    Vector3 pa = a == 15 && c == 15 ? headUp(lead) : J(lead, a), pc = a == 15 && c == 15 ? headUp(lead) : J(lead, c);
                    if (CapsuleDepth(lbs, pa, pc, r) > 0) { st.InsidePartnerRigid++; break; }
                }
            }
        }

        if (steps.Count > 0)
        {
            steps.Sort();
            st.StepMax = steps[^1];
            st.StepP99 = steps[(int)(steps.Count * 0.99)];
        }

        if (!report) return st;
        return new
        {
            role = "Follow", bound = hb.Bound, live = s.HairLive, status = s.HairStatus, frame = hair.SimFrame,
            gpuVsCpuMm = Q(gpuCpu), gpuVsCpuRotDeg = Q(rotErr),
            moveVsHeadRigidMm = Q(swingRigid), moveVsLbsMm = Q(swingLbs), attachTopMoveMm = Q(attachTop),
            insideHead = st.InsideHead, insideOwnTorso = st.InsideOwn, insideOwnTorsoRigid = st.InsideOwnRigid,
            insidePartner = st.InsidePartner, insidePartnerRigid = st.InsidePartnerRigid, headRadiusM = headR,
            restDiffMm = hb.RestDiffMaxMm, canonResidualMm = hb.CanonResidualMaxMm
        };
    }

    /// <summary>off-screen stills of the follow from a camera parked around her head (azimuth relative to her pelvis'
    /// facing), rendered by a temporary copy of the main camera into a RenderTexture: the splats (when on) or the avatars
    /// as the viewer shows them now; screen-space UI is not drawn</summary>
    static object HairShot(string prefix, string azimuths, float radius, float elevation, float drop, float fov, int width, int height)
    {
        if (string.IsNullOrEmpty(prefix) || !System.IO.Path.IsPathRooted(prefix)) throw new ArgumentException("--out <absolute path prefix> required");
        HeadMovement hm = HeadMovement.Instance;
        if (hm == null || !hm.Avatars.TryGetValue(Role.Follow, out SmplxAvatar f) || f == null) throw new InvalidOperationException("no follow avatar");
        HairStrands hair = f.GetComponentInChildren<HairStrands>(true);
        if (hair != null && f.CurrentFrame >= 0) hair.Advance(f.CurrentFrame);
        Camera main = Camera.main != null ? Camera.main : HairStrands.ViewCamera();
        if (main == null) throw new InvalidOperationException("no camera to copy");
        Transform head = f.Bone(SplatHairBinding.HeadJoint), pelvis = f.Bone(0);
        Vector3 fwd = pelvis.rotation * Vector3.back; // her forward (Unity -Z of the SMPL-X bones)
        fwd.y = 0;
        fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
        Vector3 target = head.position + Vector3.down * drop;
        GameObject go = new("hairshot camera") { hideFlags = HideFlags.HideAndDontSave };
        Camera cam = go.AddComponent<Camera>();
        cam.CopyFrom(main);
        var urp = main.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
        var urpCopy = go.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
        if (urp != null)
        {
            urpCopy.renderPostProcessing = urp.renderPostProcessing;
            urpCopy.antialiasing = urp.antialiasing;
        }

        cam.fieldOfView = fov;
        cam.nearClipPlane = 0.05f;
        RenderTexture rt = new(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
        Texture2D tex = new(width, height, TextureFormat.RGB24, false);
        List<string> files = new();
        try
        {
            cam.targetTexture = rt;
            foreach (string a in azimuths.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                float az = float.Parse(a, System.Globalization.CultureInfo.InvariantCulture);
                Vector3 dir = Quaternion.AngleAxis(-az, Vector3.up) * fwd;
                Vector3 pos = target + Quaternion.AngleAxis(elevation, Vector3.Cross(Vector3.up, dir)) * dir * radius;
                cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(target - pos, Vector3.up));
                cam.Render();
                cam.Render(); // second render: the splat sort of this camera is current
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                string file = $"{prefix}_az{az:000}.png";
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file) ?? ".");
                System.IO.File.WriteAllBytes(file, tex.EncodeToPNG());
                files.Add(file);
            }
        }
        finally
        {
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
        }

        return new { files, frame = f.CurrentFrame, splats = SplatDancers.Enabled, hairMotion = SplatDancers.HairMotion };
    }

    static object Q(List<float> v)
    {
        if (v.Count == 0) return null;
        v.Sort();
        return new { n = v.Count, median = v[v.Count / 2], p95 = v[(int)(v.Count * 0.95)], max = v[^1] };
    }

    /// <summary>frame time + the GPU time of the renderer's passes (Profiling.Recorder on the render-graph pass samplers)</summary>
    static class Perf
    {
        public static readonly string[] Names =
        {
            "Gaussians.3D.Prepare", "Gaussians.3D.Accumulate", "Gaussians.3D.Composite", "Gaussians.3D.Depth",
            "Gaussians.3D.Direct", "GaussianSplat3D.Prepare", "GaussianSplat3D.SortSplats", "GaussianSplat3D.CalcViewData",
            "Render Camera", "UniversalRenderTotal", "DrawTransparentObjects", "DrawOpaqueObjects"
        };

        static readonly List<float> Frames = new();
        static readonly Dictionary<string, List<float>> Gpu = new(), Cpu = new();
        static bool hooked;
        static int startFrame;

        public static void Start()
        {
            Frames.Clear();
            Gpu.Clear();
            Cpu.Clear();
            foreach (string n in Names)
            {
                Recorder r = Recorder.Get(n);
                if (r.isValid) r.enabled = true;
                Gpu[n] = new List<float>();
                Cpu[n] = new List<float>();
            }

            startFrame = Time.frameCount;
            if (!hooked)
            {
                EditorApplication.update += Sample;
                hooked = true;
            }
        }

        static void Sample()
        {
            if (!EditorApplication.isPlaying || Frames.Count >= 2000) return;
            if (Time.frameCount == startFrame) return;
            Frames.Add(Time.unscaledDeltaTime * 1000f);
            foreach (string n in Names)
            {
                Recorder r = Recorder.Get(n);
                if (!r.isValid) continue;
                Gpu[n].Add(r.gpuElapsedNanoseconds / 1e6f);
                Cpu[n].Add(r.elapsedNanoseconds / 1e6f);
            }
        }

        static object Stats(List<float> v)
        {
            if (v == null || v.Count == 0) return null;
            List<float> s = v.OrderBy(x => x).ToList();
            return new { n = s.Count, median = s[s.Count / 2], p90 = s[(int)(s.Count * 0.9)], mean = s.Average() };
        }

        public static object Report() => new
        {
            frameMs = Stats(Frames),
            // GPU samples arrive a few frames late and read 0 on frames without a resolved timer: count the resolved ones
            gpuMs = Names.Where(n => Gpu.TryGetValue(n, out List<float> l) && l.Any(x => x > 0)).ToDictionary(n => n, n => Stats(Gpu[n].Where(x => x > 0).ToList())),
            cpuMs = Names.Where(n => Cpu.TryGetValue(n, out List<float> l) && l.Any(x => x > 0)).ToDictionary(n => n, n => Stats(Cpu[n])),
            device = SystemInfo.graphicsDeviceName, api = SystemInfo.graphicsDeviceType.ToString(),
            screen = $"{Screen.width}x{Screen.height}"
        };
    }
}
