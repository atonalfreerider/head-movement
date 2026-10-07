using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>
/// A dancer's sneakers (VIEWER_SPEC 3.2 shoes; dancecap docs: shoes.json from python -m dancecap.shoes). Built at load
/// from the dancer's fitted rest feet and fitted to the capture's motion (SneakerBuilder + SneakerFit), one
/// SkinnedMeshRenderer for both shoes (one draw per pass, 6 bones, Bone2 GPU skinning) on the avatar's translucent
/// material (SmplxAvatar.Attach: same opacity, visibility and lifetime as the body), the bare SMPL-X feet clipped along
/// the shoe's top edge (SmplxAvatar.SetFootHeights / HideFeet).
///
/// Load: the skin read, the measure, the fit to the motion, the mesh data and the atlas run on a worker thread (pure
/// C#); the main thread then makes the mesh and the proxies and attaches them (a few ms) - until then the bare feet show.
/// The fit is baked once: per frame the shoe's pose relative to the ankle bone (floor contact from the whole outsole
/// edge: never under the floor, a stance planted by a slope-limited amount, a small pitch about the other end), the
/// cut under the shoe's top edge with a margin for the largest plant, the rear upper / collar sized to where the
/// visible leg goes over every frame. Per frame (LateUpdate, after the hair re-poses the avatar for its pre-roll):
/// three proxy bones per foot - the ankle bone times the baked correction; the toe bending about the outsole under the
/// ball line by the SMPL-X toe flexion only (scaled 0.8, clamped to -5..45 deg); the shin (the knee bone's rotation in
/// the ankle's frame) carrying the tongue's top. Stateless at runtime: scrubbing is deterministic.
/// </summary>
[DefaultExecutionOrder(100)]
public class SneakerPair : MonoBehaviour
{
    static readonly List<SneakerPair> all = new();
    public static IReadOnlyList<SneakerPair> All => all;

    [Header("Look")]
    public bool Visible = true;
    /// <summary>clip the body's bare feet while the shoes show (off: debugging / before shots)</summary>
    public bool ManageFeet = true;

    [Header("Toes")]
    public float ToeBendScale = 0.8f;
    public float ToeMinDeg = -5f, ToeMaxDeg = 45f;

    [Header("Floor contact (baked; a change re-bakes the contact, the shoe shape stays)")]
    public bool Contact = true;
    public bool Plant = true;
    public float MaxLift = 0.03f, MaxPitchDeg = 4f, PlantMax = 0.02f;

    public SmplxAvatar Avatar { get; private set; }
    public SneakerStyle Style { get; private set; }
    public string Warning { get; private set; }
    /// <summary>built and attached (the worker has finished and the main thread made the mesh)</summary>
    public bool Ready => build != null;
    /// <summary>main-thread cost of the load: starting the worker + making the mesh / proxies and attaching (ms)</summary>
    public double LoadMs { get; private set; }
    public double StartMs { get; private set; }
    public double FinishMs { get; private set; }
    public double WorkerMs { get; private set; } // worker thread: read + measure + fit + build + paint
    public double SkinReadMs { get; private set; }
    public double StyleMs { get; private set; }
    public double MeasureMs { get; private set; }
    public double MotionMs { get; private set; }
    public double BuildMs { get; private set; }
    public double PaintMs { get; private set; }
    public double AttachMs { get; private set; }
    public double UploadMs { get; private set; } // main thread, a frame later: mips + compression
    public double LateMsAvg { get; private set; }

    SneakerBuildResult build;
    FootMeasure[] feet;
    SneakerMotion motion;
    SneakerFitSettings baked;
    readonly Transform[] proxies = new Transform[SneakerBuilder.Bones];
    SkinnedMeshRenderer smr;
    Material colour;
    Texture2D atlas, placeholder;
    Color32[] atlasPixels;
    Task<Prepared> preparing;
    Func<int, float> frameTime;
    int posedFrame = -1;
    readonly Stopwatch watch = new();

    /// <summary>what the worker thread makes (pure C#)</summary>
    sealed class Prepared
    {
        public SneakerStyle Style;
        public string Warning;
        public FootMeasure[] Feet;
        public SneakerMotion Motion;
        public SneakerBuildResult Build;
        public Color32[] Atlas;
        public double ReadMs, StyleMs, MeasureMs, MotionMs, BuildMs, PaintMs, TotalMs;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Hook()
    {
        HeadMovement.AvatarsLoaded -= OnAvatarsLoaded;
        HeadMovement.AvatarsLoaded += OnAvatarsLoaded;
    }

    /// <summary>shoes for every avatar of a freshly loaded capture (shoes.json optional: neutral sneakers without it)</summary>
    static void OnAvatarsLoaded(HeadMovement hm)
    {
        string json = hm.Manifest?.OptionalPath("shoes.json");
        foreach (KeyValuePair<Role, SmplxAvatar> kv in hm.Avatars)
        {
            if (kv.Value == null) continue;
            try
            {
                Create(kv.Value, kv.Value.SkinPath, json, hm.Timeline != null ? hm.Timeline.AudioTimeOf : null);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{hm.Manifest?.DisplayName}: {kv.Key} shoes failed: {e.Message}");
            }
        }
    }

    /// <param name="skinPath">the dancer's skin binary (SmplxAvatar.SkinPath; the feet are measured on its rest mesh)</param>
    /// <param name="shoesJsonPath">the capture's shoes.json or null</param>
    /// <param name="frameTime">audio seconds of a capture frame (contact speeds); null = 30 fps</param>
    /// <param name="wait">finish now (block on the worker) instead of in a later LateUpdate</param>
    public static SneakerPair Create(SmplxAvatar avatar, string skinPath, string shoesJsonPath, Func<int, float> frameTime = null, bool wait = false)
    {
        if (avatar == null) throw new ArgumentNullException(nameof(avatar));
        Stopwatch sw = Stopwatch.StartNew();
        foreach (SneakerPair old in avatar.GetComponentsInChildren<SneakerPair>()) Destroy(old.gameObject);
        GameObject go = new("Sneakers") { layer = avatar.gameObject.layer };
        go.transform.SetParent(avatar.transform, false);
        SneakerPair p = go.AddComponent<SneakerPair>();
        p.Avatar = avatar;
        p.frameTime = frameTime;
        // everything the worker needs from Unity objects is read here
        SmplxData.Motion mo = avatar.Motion;
        float[] times = null;
        if (mo != null && frameTime != null)
        {
            times = new float[mo.FrameCount];
            for (int k = 0; k < times.Length; k++) times[k] = frameTime(k);
        }

        Role role = avatar.DancerRole;
        SneakerFitSettings st = p.baked = p.Settings();
        p.preparing = Task.Run(() => Prepare(skinPath, shoesJsonPath, role, mo, times, st));
        all.Add(p);
        p.StartMs = sw.Elapsed.TotalMilliseconds;
        p.LoadMs = p.StartMs;
        if (wait) p.Finish(true);
        return p;
    }

    /// <summary>the worker's part (no Unity objects)</summary>
    static Prepared Prepare(string skinPath, string json, Role role, SmplxData.Motion mo, float[] times, SneakerFitSettings st)
    {
        Prepared r = new();
        Stopwatch sw = Stopwatch.StartNew();
        SmplxData.Skin skin = SmplxData.ReadSkin(skinPath);
        r.ReadMs = sw.Elapsed.TotalMilliseconds;
        double t = r.ReadMs;
        try
        {
            r.Style = SneakerStyle.FromJson(json, role);
        }
        catch (Exception e)
        {
            r.Style = SneakerStyle.Default(role);
            r.Warning = $"shoes.json unreadable ({e.Message}) - neutral sneakers";
        }

        r.StyleMs = sw.Elapsed.TotalMilliseconds - t;
        t = sw.Elapsed.TotalMilliseconds;
        r.Feet = new[] { SneakerBuilder.Measure(skin, true), SneakerBuilder.Measure(skin, false) };
        r.MeasureMs = sw.Elapsed.TotalMilliseconds - t;
        t = sw.Elapsed.TotalMilliseconds;
        try
        {
            r.Motion = mo != null ? new SneakerMotion(mo, skin, times != null ? k => times[k] : null) : null;
        }
        catch (Exception e)
        {
            r.Motion = null;
            r.Warning = $"no motion fit ({e.Message}) - rest-pose sneakers";
        }

        r.MotionMs = sw.Elapsed.TotalMilliseconds - t;
        t = sw.Elapsed.TotalMilliseconds;
        r.Build = SneakerBuilder.Build(r.Feet[0], r.Feet[1], r.Style, skin, r.Motion, st, false);
        r.BuildMs = sw.Elapsed.TotalMilliseconds - t;
        // CutExcessMm is measured against (the top edge - 4 mm): over 4 mm the cut edge may show above the collar
        if (r.Build.CutExcessMm > 4f) r.Warning = $"the body's cut edge may show {r.Build.CutExcessMm - 4f:0.0} mm above the collar on some frame";
        t = sw.Elapsed.TotalMilliseconds;
        r.Atlas = SneakerTexture.PaintPixels(r.Style, r.Build.Atlas);
        r.PaintMs = sw.Elapsed.TotalMilliseconds - t;
        r.TotalMs = sw.Elapsed.TotalMilliseconds;
        return r;
    }

    /// <summary>make the mesh and the proxies once the worker is done and attach them (main thread); wait = block on the
    /// worker (the wait is not counted as main-thread load cost)</summary>
    public bool Finish(bool wait)
    {
        if (build != null) return true;
        if (preparing == null) return false;
        if (!preparing.IsCompleted)
        {
            if (!wait) return false;
            try
            {
                preparing.Wait();
            }
            catch (Exception)
            {
                // reported below
            }
        }

        Stopwatch sw = Stopwatch.StartNew();
        Prepared r;
        try
        {
            r = preparing.Result;
        }
        catch (Exception e)
        {
            Warning = $"shoes failed: {e.InnerException?.Message ?? e.Message}";
            Debug.LogWarning($"{Avatar?.DancerRole} {Warning}");
            preparing = null;
            enabled = false;
            return false;
        }

        preparing = null;
        Style = r.Style;
        Warning = r.Warning;
        feet = r.Feet;
        motion = r.Motion;
        build = r.Build;
        atlasPixels = r.Atlas;
        SkinReadMs = r.ReadMs;
        StyleMs = r.StyleMs;
        MeasureMs = r.MeasureMs;
        MotionMs = r.MotionMs;
        BuildMs = r.BuildMs;
        PaintMs = r.PaintMs;
        WorkerMs = r.TotalMs;
        if (Warning != null) Debug.LogWarning($"{Avatar.DancerRole} shoes: {Warning}");

        for (int f = 0; f < 2; f++)
        {
            string side = f == 0 ? "left" : "right";
            Transform ankle = new GameObject($"{side} ankle proxy").transform;
            ankle.SetParent(transform, false);
            ankle.localPosition = build.BoneRest[SneakerBuilder.Bone(f, SneakerBuilder.Ankle)];
            Transform toe = new GameObject($"{side} toe proxy").transform;
            toe.SetParent(ankle, false);
            toe.localPosition = build.BoneRest[SneakerBuilder.Bone(f, SneakerBuilder.Toe)] - build.BoneRest[SneakerBuilder.Bone(f, SneakerBuilder.Ankle)];
            Transform shin = new GameObject($"{side} shin proxy").transform; // turns with the shin about the ankle (the tongue)
            shin.SetParent(ankle, false);
            proxies[SneakerBuilder.Bone(f, SneakerBuilder.Ankle)] = ankle;
            proxies[SneakerBuilder.Bone(f, SneakerBuilder.Toe)] = toe;
            proxies[SneakerBuilder.Bone(f, SneakerBuilder.Shin)] = shin;
        }

        build.Mesh = SneakerBuilder.CreateMesh(build);
        smr = gameObject.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = build.Mesh;
        smr.bones = proxies;
        smr.rootBone = proxies[0];
        smr.updateWhenOffscreen = true;
        smr.quality = SkinQuality.Bone2;
        smr.lightProbeUsage = LightProbeUsage.Off;
        smr.reflectionProbeUsage = ReflectionProbeUsage.Off;

        // a flat upper colour for the frame until the atlas is uploaded (next LateUpdate)
        placeholder = new Texture2D(4, 4, TextureFormat.RGB24, false, false) { name = "Sneaker placeholder" };
        Color32[] flat = new Color32[16];
        for (int i = 0; i < 16; i++) flat[i] = Style.Upper;
        placeholder.SetPixels32(flat);
        placeholder.Apply(false, true);
        double ta = sw.Elapsed.TotalMilliseconds;
        colour = Avatar.Attach(smr, Color.white, placeholder, false, Style.LightInfluence);
        colour.SetFloat("_Specular", Style.Runner ? 0.06f : 0.16f);
        colour.SetFloat("_SpecPower", Style.Runner ? 16f : 40f);
        Avatar.SetFootHeights(build.FootHeights); // the cut follows the shoe's top edge (null: the level cut)
        Avatar.FootCut = build.FootCut;
        if (ManageFeet) Avatar.HideFeet(Visible);
        AttachMs = sw.Elapsed.TotalMilliseconds - ta;
        if (Avatar.CurrentFrame >= 0) Pose(Avatar.CurrentFrame);
        FinishMs = sw.Elapsed.TotalMilliseconds;
        LoadMs = StartMs + FinishMs;
        return true;
    }

    SneakerFitSettings Settings() => new()
    {
        ToeBendScale = ToeBendScale, ToeMinDeg = ToeMinDeg, ToeMaxDeg = ToeMaxDeg, Contact = Contact, Plant = Plant,
        MaxLift = MaxLift, MaxPitchDeg = MaxPitchDeg, PlantMax = PlantMax
    };

    static bool Same(SneakerFitSettings a, SneakerFitSettings b) =>
        a != null && b != null && a.ToeBendScale == b.ToeBendScale && a.ToeMinDeg == b.ToeMinDeg && a.ToeMaxDeg == b.ToeMaxDeg &&
        a.Contact == b.Contact && a.Plant == b.Plant && a.MaxLift == b.MaxLift && a.MaxPitchDeg == b.MaxPitchDeg && a.PlantMax == b.PlantMax;

    /// <summary>rebuild the pair on its avatar and finish it now (CLI: the warm load cost; the first load pays the JIT)</summary>
    public SneakerPair Rebuild(string shoesJsonPath)
    {
        SmplxAvatar a = Avatar;
        Func<int, float> t = frameTime;
        Destroy(gameObject);
        return Create(a, a.SkinPath, shoesJsonPath, t, true);
    }

    /// <summary>re-bake the floor contact when its settings changed (CLI hm_shoes set; ~1 ms; the shape stays)</summary>
    void EnsureBaked()
    {
        if (motion == null || build?.Fits == null) return;
        SneakerFitSettings now = Settings();
        if (Same(now, baked)) return;
        for (int f = 0; f < 2; f++)
        {
            build.Fits[f] = SneakerFit.Bake(motion, feet[f], build.Shapes[f], build.Sole[f], build.BoneRest[SneakerBuilder.Bone(f, SneakerBuilder.Ankle)],
                build.BoneRest[SneakerBuilder.Bone(f, SneakerBuilder.Toe)], now);
        }

        baked = now;
    }

    /// <summary>upload the worker's atlas (main thread: mips + block compression)</summary>
    public bool FinishAtlas()
    {
        if (atlasPixels == null || smr == null) return atlas != null;
        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            atlas = SneakerTexture.ToTexture(atlasPixels, Style);
            UploadMs = sw.Elapsed.TotalMilliseconds;
            foreach (Material m in smr.sharedMaterials)
            {
                if (m != null) m.SetTexture("_BaseMap", atlas);
            }
        }
        catch (Exception e)
        {
            Warning = $"sneaker atlas failed: {e.Message}";
            Debug.LogWarning(Warning);
        }

        atlasPixels = null;
        if (placeholder != null) Destroy(placeholder);
        placeholder = null;
        return atlas != null;
    }

    void LateUpdate()
    {
        if (build == null)
        {
            // a script reload in play mode drops the build (and the worker): stand down until rebuilt
            if (preparing == null || Avatar == null)
            {
                enabled = false;
                return;
            }

            Finish(false);
            return; // the atlas follows next frame
        }

        if (feet == null || smr == null || Avatar == null)
        {
            enabled = false;
            return;
        }

        if (atlasPixels != null) FinishAtlas();
        watch.Restart();
        int f = Avatar.CurrentFrame;
        bool show = Visible && Avatar.Visible && f >= 0;
        if (ManageFeet && Avatar.FeetHidden != Visible) Avatar.HideFeet(Visible);
        smr.enabled = show; // after HideFeet (Attach re-enables attachments on appearance changes)
        if (show) Pose(f);
        watch.Stop();
        double ms = watch.Elapsed.TotalMilliseconds;
        LateMsAvg = LateMsAvg <= 0 ? ms : LateMsAvg * 0.95 + ms * 0.05;
    }

    /// <summary>finish (blocking), upload the atlas and pose the proxies for the avatar's current bones (CLI before
    /// screenshots)</summary>
    public void Sync()
    {
        if (!Finish(true)) return;
        FinishAtlas();
        if (Avatar != null && Avatar.CurrentFrame >= 0) Pose(Avatar.CurrentFrame);
    }

    /// <summary>proxies from the avatar's bones as posed now + the baked correction of that motion frame</summary>
    void Pose(int frame)
    {
        EnsureBaked();
        for (int i = 0; i < 2; i++)
        {
            FootMeasure m = feet[i];
            Transform ankleBone = Avatar.Bone(m.AnkleJoint);
            Transform aP = proxies[SneakerBuilder.Bone(i, SneakerBuilder.Ankle)], tP = proxies[SneakerBuilder.Bone(i, SneakerBuilder.Toe)];
            // the shin relative to the foot (the knee bone's rotation in the ankle bone's frame), about the ankle joint
            proxies[SneakerBuilder.Bone(i, SneakerBuilder.Shin)].localRotation = Quaternion.Inverse(ankleBone.localRotation);
            SneakerFootFit fit = build.Fits?[i];
            if (fit != null && frame >= 0 && frame < fit.N)
            {
                aP.SetPositionAndRotation(ankleBone.TransformPoint(fit.DPos[frame]), ankleBone.rotation * fit.DRot[frame]);
                tP.localRotation = Quaternion.AngleAxis(fit.Toe[frame], m.FlexAxis);
            }
            else
            {
                aP.SetPositionAndRotation(ankleBone.position, ankleBone.rotation);
                float raw = SneakerFit.TwistDeg(Avatar.Bone(m.BallJoint).localRotation, m.FlexAxis);
                tP.localRotation = Quaternion.AngleAxis(Mathf.Clamp(raw * ToeBendScale, ToeMinDeg, ToeMaxDeg), m.FlexAxis);
            }
        }

        posedFrame = frame;
    }

    /// <summary>world centre of a shoe (foot 0 left, 1 right) at the posed frame: mid-length, 4 cm up (review framing)</summary>
    public Vector3 ShoeCentre(int foot)
    {
        Finish(true);
        SneakerShape sh = build.Shapes[foot];
        Vector3 rest = feet[foot].Rest(0.5f * (sh.X0 + sh.X1), 0f, 0.04f);
        Transform aP = proxies[SneakerBuilder.Bone(foot, SneakerBuilder.Ankle)];
        return aP.localToWorldMatrix.MultiplyPoint3x4(rest - build.BoneRest[SneakerBuilder.Bone(foot, SneakerBuilder.Ankle)]);
    }

    // ------------------------------------------------------------------------------------------------ reports

    static float[] V3(Vector3 v) => new[] { v.x, v.y, v.z };

    public Dictionary<string, object> State()
    {
        if (build == null)
        {
            return new Dictionary<string, object>
            {
                ["present"] = true, ["ready"] = false, ["pending"] = preparing != null, ["role"] = Avatar != null ? Avatar.DancerRole.ToString().ToLowerInvariant() : null,
                ["warning"] = Warning
            };
        }

        Material depth = smr != null && smr.sharedMaterials.Length > 0 ? smr.sharedMaterials[0] : null;
        bool weightsOk = true;
        foreach (BoneWeight w in build.Weights)
        {
            if (Mathf.Abs(w.weight0 + w.weight1 + w.weight2 + w.weight3 - 1f) > 1e-3f) weightsOk = false;
        }

        Dictionary<string, object> perFoot(int i)
        {
            FootMeasure m = feet[i];
            SneakerShape sh = build.Shapes[i];
            SneakerFootFit fit = build.Fits?[i];
            int k = posedFrame;
            bool ok = fit != null && k >= 0 && k < fit.N;
            return new Dictionary<string, object>
            {
                ["shift"] = ok ? fit.Shift[k] : 0f, ["pitchDeg"] = ok ? fit.Pitch[k] : 0f, ["toeDeg"] = ok ? fit.Toe[k] : float.NaN,
                ["toeRawDeg"] = ok ? fit.ToeRaw[k] : float.NaN, ["lowestSoleY"] = ok ? fit.Min[k] : float.NaN,
                ["lowestSoleYRaw"] = ok ? fit.RawMin[k] : float.NaN, ["plantWeight"] = ok ? fit.Score[k] : float.NaN,
                ["ankle"] = V3(proxies[SneakerBuilder.Bone(i, SneakerBuilder.Ankle)].position), ["footLength"] = m.L, ["ballX"] = m.BallX,
                ["ankleH"] = m.AnkleH, ["shoeLength"] = sh.X1 - sh.X0, ["soleHeel"] = Style.SoleHeelM, ["soleFore"] = Style.SoleForeM,
                ["xOpen"] = sh.XOpen, ["xEye"] = sh.XEye, ["kOpen"] = sh.KOpen, ["wallPushMaxMm"] = build.WallPushMaxMm[i],
                ["maxDrop"] = fit != null ? fit.MaxDrop : 0f,
                ["slice"] = new[] { m.SliceXMin, m.SliceXMax, m.SliceHalfMed, m.SliceHalfLat }
            };
        }

        return new Dictionary<string, object>
        {
            ["present"] = true, ["ready"] = true, ["role"] = Avatar.DancerRole.ToString().ToLowerInvariant(), ["style"] = Style.Style,
            ["source"] = Style.Source, ["visible"] = Visible, ["rendererEnabled"] = smr.enabled,
            ["avatarVisible"] = Avatar.Visible, ["feetHidden"] = Avatar.FeetHidden, ["footCut"] = Avatar.FootCut,
            ["builtFootCut"] = build.FootCut, ["cutMargin"] = build.CutMarginM, ["footHeights"] = Avatar.CustomFootHeights,
            ["avatarOpacity"] = Avatar.Opacity,
            ["opacity"] = colour != null ? colour.GetFloat("_Opacity") : float.NaN,
            ["depthOpacity"] = depth != null ? depth.GetFloat("_Opacity") : float.NaN,
            ["renderQueues"] = smr.sharedMaterials.Select(mt => mt != null ? mt.renderQueue : -1).ToArray(),
            ["avatarQueueOffset"] = Avatar.QueueOffset,
            ["shadowCasting"] = smr.shadowCastingMode.ToString(), ["layer"] = gameObject.layer,
            ["avatarLayer"] = Avatar.gameObject.layer,
            ["trianglesPerShoe"] = build.TrianglesPerShoeMax, ["verticesPerShoe"] = build.VerticesPerShoeMax,
            ["triangles"] = build.Triangles.Length / 3, ["vertices"] = build.RestVertices.Length, ["bones"] = smr.bones.Length,
            ["weightsSumToOne"] = weightsOk, ["hiddenFootVertices"] = build.HiddenVertices,
            ["hiddenOutsideShoe"] = build.HiddenOutside, ["frame"] = posedFrame, ["avatarFrame"] = Avatar.CurrentFrame,
            ["fitted"] = build.Fits != null, ["cutPasses"] = build.CutPasses, ["cutExcessMm"] = build.CutExcessMm,
            ["maxCollarDrop"] = build.MaxDrop, ["legSamples"] = build.Legs != null ? build.Legs[0].Count + build.Legs[1].Count : 0,
            ["toeBendScale"] = ToeBendScale, ["contact"] = Contact, ["plant"] = Plant, ["plantMax"] = PlantMax,
            ["loadMs"] = LoadMs, ["startMs"] = StartMs, ["finishMs"] = FinishMs, ["attachMs"] = AttachMs, ["workerMs"] = WorkerMs,
            ["skinReadMs"] = SkinReadMs, ["styleMs"] = StyleMs, ["measureMs"] = MeasureMs, ["motionMs"] = MotionMs,
            ["buildMs"] = BuildMs, ["fitMs"] = build.FitMs, ["envelopeMs"] = build.EnvelopeMs, ["meshMs"] = build.MeshMs,
            ["paintMsWorker"] = PaintMs, ["uploadMs"] = UploadMs, ["lateUpdateMs"] = LateMsAvg,
            ["atlas"] = atlas != null ? $"{atlas.width}x{atlas.height} {atlas.format}" : null,
            ["palette"] = new Dictionary<string, string>
            {
                ["upper"] = SneakerStyle.ToHex(Style.Upper), ["sole"] = SneakerStyle.ToHex(Style.Sole),
                ["soleHeel"] = SneakerStyle.ToHex(Style.SoleHeel), ["outsole"] = SneakerStyle.ToHex(Style.Outsole),
                ["accent"] = SneakerStyle.ToHex(Style.Accent), ["heelTab"] = SneakerStyle.ToHex(Style.HeelTab)
            },
            ["left"] = perFoot(0), ["right"] = perFoot(1), ["warning"] = Warning, ["notes"] = build.Notes,
            ["bounds"] = new Dictionary<string, object> { ["center"] = V3(smr.bounds.center), ["size"] = V3(smr.bounds.size) }
        };
    }

    /// <summary>
    /// The whole capture through the shoes on the viewer's real bones and proxies (the avatar's bones are posed per
    /// frame and restored; SneakerChecks): the skinned shoe mesh against the floor, every visible leg point (vertices,
    /// edge midpoints, face centres, the cut edge - skinned with the avatar's own bones) against the actual shoe surface
    /// and the tongue, correction jumps on a still ankle, stance hover, toe bend, CPU per pose; plus review moments.
    /// </summary>
    public Dictionary<string, object> Sweep()
    {
        if (!Finish(true)) return new Dictionary<string, object> { ["ready"] = false, ["warning"] = Warning };
        int keep = Avatar.CurrentFrame;
        int n = Avatar.FrameCount;
        Transform root = Avatar.transform;
        List<float> ms = new();
        Stopwatch sw = new();
        Vector3 Skin(LegSamples legs, int i)
        {
            Vector3 w = Vector3.zero;
            for (int c = 0; c < 4; c++)
            {
                float wt = legs.Weight[4 * i + c];
                if (wt <= 0f) continue;
                int j = legs.Joint[4 * i + c];
                w += wt * Avatar.Bone(j).localToWorldMatrix.MultiplyPoint3x4(legs.Rest[i] - Avatar.RestJoint(j));
            }

            return w;
        }

        Stopwatch total = Stopwatch.StartNew();
        Dictionary<string, object> report = SneakerChecks.Run(build, n,
            k =>
            {
                Avatar.PoseBones(k);
                sw.Restart();
                Pose(k);
                sw.Stop();
                ms.Add((float)sw.Elapsed.TotalMilliseconds);
            },
            (int foot, out Vector3 pa, out Quaternion ra, out Quaternion toe, out Quaternion shin) =>
            {
                Transform aP = proxies[SneakerBuilder.Bone(foot, SneakerBuilder.Ankle)];
                pa = root.InverseTransformPoint(aP.position);
                ra = Quaternion.Inverse(root.rotation) * aP.rotation;
                toe = proxies[SneakerBuilder.Bone(foot, SneakerBuilder.Toe)].localRotation;
                shin = proxies[SneakerBuilder.Bone(foot, SneakerBuilder.Shin)].localRotation;
            },
            (foot, outRest) =>
            {
                LegSamples legs = build.Legs[foot];
                Matrix4x4 toShoe = proxies[SneakerBuilder.Bone(foot, SneakerBuilder.Ankle)].worldToLocalMatrix;
                Vector3 aRest = build.BoneRest[SneakerBuilder.Bone(foot, SneakerBuilder.Ankle)];
                for (int i = 0; i < legs.Count; i++) outRest[i] = toShoe.MultiplyPoint3x4(Skin(legs, i)) + aRest;
            },
            0f, frameTime);
        Avatar.PoseBones(keep);
        if (keep >= 0) Pose(keep);
        ms.Sort();
        report["sweepMs"] = total.Elapsed.TotalMilliseconds;
        report["poseMsMean"] = ms.Count > 0 ? ms.Average() : 0.0;
        report["poseMsP95"] = ms.Count > 0 ? ms[(int)(0.95f * (ms.Count - 1))] : 0f;
        report["footCut"] = build.FootCut;
        report["cutMargin"] = build.CutMarginM;
        report["cutExcessMm"] = build.CutExcessMm;
        report["maxCollarDrop"] = build.MaxDrop;
        report["wallPushMaxMm"] = build.WallPushMaxMm;
        return report;
    }

    void OnDestroy()
    {
        all.Remove(this);
        if (Avatar != null && build != null)
        {
            if (smr != null) Avatar.Detach(smr);
            // a rebuild's new pair already owns the feet (this runs at the end of the frame)
            if (!all.Any(p => p != null && p.Avatar == Avatar))
            {
                if (ManageFeet) Avatar.HideFeet(false);
                Avatar.SetFootHeights(null);
            }
        }

        if (build?.Mesh != null) Destroy(build.Mesh);
        if (atlas != null) Destroy(atlas);
        if (placeholder != null) Destroy(placeholder);
    }
}
