using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HeadMovementHair;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>
/// The follow's hair on her skinned SMPL-X avatar, done the way a game does realistic, performant hair:
///
///   * Guides: 160 strands x 16 segments from the capture's hair_groom.json (dancecap hair_groom.py: roots on the SMPL-X
///     scalp, layered lengths, colour, style, colliders), simulated with Burst jobs (HairJobs.StrandSim: Verlet + XPBD
///     shape / bending / stretch, capsule collisions incl. frictionless arm capsules, a head-attached face box,
///     deviation cone, follow-the-leader, substeps). Rest shape = sleek and straight like her portrait: deep side part
///     on her left, swept to her right, the right front section over the temple and down in front of the right
///     shoulder, the left front behind the ear, ~1 cm over the scalp; the face box keeps every strand off her face in
///     the settle AND every simulation step.
///   * Render: guide-driven hair CARDS (HairCardLayout: inner / mid / outer / part / flyaway layers, each card a blend
///     of 3 neighbouring guides) + a scalp cap, textured by a procedural strand atlas (HairAtlas, coverage-preserving
///     mips) and shaded with Kajiya-Kay (white primary + coloured secondary lobe, Karis transmission) in
///     Resources/HM_HairCards: a depth pass with alpha-to-coverage (MSAA 4x; alpha test without MSAA) at queue 2991 and
///     a colour pass at 3001 with ZTest Equal + premultiplied opacity, so the opacity is a real, uniform fade over the
///     skeleton and the translucent bodies (front-most hair surface, order independent). Opacity = the avatar's
///     + 0.15 x smoothstep(0, 0.2, avatar) (VIEWER_SPEC 3.2: +0.15 at the normal opacities; fades out WITH the body,
///     no floating wig) or an override (hm_hair --opacity). The cap reaches down to the avatar texture's painted
///     hairline (groom cards.cap; no dark band of baked hair under the cards) and the front cards' roots drop toward it
///     (cards.root_drop).
///   * Stylisation: the original head-movement tip bloom as sparse soft glows at the ends of the outer cards and
///     flyaways (Resources/HM_HairTips: additive HDR white along the original 6-point curve, a Gaussian core >= 2 px +
///     a halo + a round end, each on a real atlas strand and ending where it ends; faded out with the avatar's
///     opacity, not with the hair's).
///   * LOD by her head's height on screen (all cards / no flyaways, part cards and every second mid card / inner +
///     outer only).
///
/// Time base: the hair advances with the CAPTURE frames the avatar shows, not with Time.deltaTime. Each new frame is one
/// step of the real frame interval (times.json), the head pose and capsules interpolated across the substeps, so
/// slow-motion lessons slow the hair down with the dance and a paused take shows frozen hair. The whole take is BAKED in
/// the background after load (HairBake.cs: chunks of frames, one Burst job per chunk over strands): seek, loop and
/// slow motion are then a lookup (no re-hang hitch). Until a frame is baked, the live path steps it (a seek, a loop
/// back or a jump of more than MaxCatchUpFrames re-hangs the hair PreRollFrames earlier, as before).
/// </summary>
public partial class HairStrands : MonoBehaviour
{
    public enum RenderMode { Cards, Ribbons }

    const int HeadJoint = 15;
    const int MaxCatchUpFrames = 8;

    /// <summary>translucent queue convention (INTERFACES.md): hair depth prepass, hair colour, hair tip glow</summary>
    public const int DepthQueue = 2991, ColourQueue = 3001, GlowQueue = 3002;
    /// <summary>hair opacity = avatar opacity + this x smoothstep(0, OpacityRamp, avatar opacity) (VIEWER_SPEC 3.2)</summary>
    public const float OpacityOffset = 0.15f, OpacityRamp = 0.2f;
    /// <summary>the tip glow fades out with the avatar: x smoothstep(0, GlowFadeRamp, avatar opacity)</summary>
    public const float GlowFadeRamp = 0.1f;

    public static HairStrands Active { get; private set; }

    [Header("Dynamics (from hair_groom.json; tweakable live)")]
    public int Substeps = 8;
    public int Iterations = 2;
    public float Damping = 2.4f;
    public float BendCompliance = 1e-6f;
    public float Friction = 0.35f;
    public float ParticleRadius = 0.005f;
    public float ShapeRoot = 0.6f;
    public float ShapeFalloff = 0.22f;
    public int PreRollFrames = 20;
    public float HeadStandoff = 0.6f;  // fraction of the groom standoff kept on the head collider while dancing
    public float MaxStretch = 1.02f;
    public float MaxDeviationBack = 1.0f;   // x arc length from the rest target (StrandSim deviation cone)
    public float MaxDeviationFront = 0.6f;  // front guides (|az| < front_dev_abs_az_deg)
    public float SettleSeconds = 0.5f;      // static settle in the pose of a re-hang before the dynamic pre-roll
    [Tooltip("front-right section (draped in front of the right shoulder): held to its rest shape further down the strand, a tighter cone - no hooks under the chin in fast turns")]
    public float FrontRightShapeRoot = 0.6f;
    public float FrontRightFalloff = 0.5f;
    public float MaxDeviationFrontRight = 0.35f;
    [Tooltip("the front-right dynamics zone: guides right of the part with |azimuth| below this (deg)")]
    public float FrontRightAbsAz = 60f;
    [Tooltip("the right side beyond it (|azimuth| below SideAbsAz, right of the part: drapes forward over the right shoulder and swings around the neck in fast turns) gets a moderate hold - a strong one folds the strands where a raised arm pushes them")]
    public float SideAbsAz = 60f;
    public float SideShapeRoot = 0.6f, SideFalloff = 0.22f, MaxDeviationSide = 0.6f;
    public bool FaceGuard = true;
    public bool ArmCollisions = true;

    [Header("Look")]
    public bool Visible = true;
    public RenderMode Mode = RenderMode.Cards;
    public float TipGlow = 1f;
    /// <summary>&lt; 0: follow the avatar (avatar.Opacity + 0.15 ramped); 0..1: fixed (review)</summary>
    public float OpacityOverride = -1f;
    /// <summary>-1 = automatic (head height on screen), 0..2 = forced</summary>
    public int LodOverride = -1;
    /// <summary>bit per HairCardLayout layer id (cap, inner, mid, outer, part, flyaway)</summary>
    public int LayerMask = 0x3F;
    public bool UseBake = true;
    [Header("Legacy ribbons (hm_hair --mode ribbons, A/B only)")]
    public float Volume = 0.15f;
    public float WaveAmplitude = 0f;
    public float WaveLength = 0.09f;

    SmplxAvatar avatar;
    Func<int, float> frameTime;
    JObject groom;

    int guides, points, ribbonsPerGuide, ribbons;
    float spread, clump;
    Vector3 headCentreLocal; // head-bone space
    float headRadius, standoff, partX;
    float[] guideAz, guideEl;
    int[] guideSide;
    bool[] frontRight; // settle: the front-right drape (|az| < style front_abs_az_deg[0], right of the part)
    int[] dynZone;     // dynamics, right of the part: 1 = front-right (|az| < FrontRightAbsAz), 2 = side (|az| < SideAbsAz), else 0
    FaceBox faceBox;

    // style (hair_groom.json "style"; defaults = the portrait's sleek side-swept look)
    Vector3 combFrontRight = new(-1f, -0.2f, -0.1f), combFrontLeft = new(0.5f, -0.3f, 0.9f), combBack = new(-0.2f, -1f, 0.5f);
    float frontAz0 = 60f, frontAz1 = 150f, rootLift = 0.15f, outwardBias = 0.06f, downSlerp = 0.30f, frontDevAz = 100f;
    float settleShapeRoot = 0.08f, settleShapeFalloff = 0.12f, settleShapeFalloffFrontRight = 0.3f; // per settle iteration
    float hugVolume = 0.045f, hugBelow = 0.3f, faceClear = 0.02f, frontRightForward = 0.5f;
    float lod0Px = 160f, lod1Px = 60f;

    struct ColliderDef
    {
        public string Name;
        public int A, B;
        public Vector3 Oa, Ob;
        public float R;
        public int FirstPoint;
        public float Friction, MaxPush;
        public bool Arm, Head;
    }

    ColliderDef[] colliderDefs;

    struct Kin
    {
        public float3 HeadPos;
        public quaternion HeadRot;
        public float3[] A, B;
    }

    Kin kinPrev, kinCur;
    float3 curHeadPos;
    quaternion curHeadRot = quaternion.identity;

    NativeArray<float3> pos, prev, restLocal;
    NativeArray<float> segLen, bendRest, shapeK;
    NativeArray<float2> guideInfo;
    NativeArray<Capsule> capsules;
    NativeArray<int> resets;
    float[] guideLength;

    // cards
    HairCardLayout layout;
    NativeArray<CardDesc> cardDescs;
    NativeArray<CardVertex> cardVertices;
    NativeArray<float3> chunkMin, chunkMax, capPos, capNormal, capTangent;
    NativeArray<GlowDesc> glowDescs;
    NativeArray<GlowVertex> glowVertices;
    int cardCount, capVertexCount, capChunks, rows;
    int[] capTriangles = Array.Empty<int>();
    readonly int[] lodIndexCount = new int[3], glowLodIndexCount = new int[3];
    int lod = -1, appliedMask = -1;
    Mesh cardMesh, glowMesh;
    Material depthMaterial, colourMaterial, glowMaterial;
    MeshRenderer cardRenderer, glowRenderer;
    Texture2D atlas;
    string colourSource = "video";

    // legacy ribbons (built on demand)
    NativeArray<float2> childOffset, wavePhase;
    NativeArray<RibbonVertex> ribbonVertices;
    NativeArray<float3> ribbonMin, ribbonMax;
    Mesh ribbonMesh;
    Material ribbonMaterial;
    MeshRenderer ribbonRenderer;

    int simFrame = -1;
    bool meshValid, meshDirty, liveValid;

    // stats
    readonly Stopwatch watch = new();
    readonly Stopwatch part = new();
    public double SimMsTotal { get; private set; }
    public double MeshMsTotal { get; private set; }
    public int Steps { get; private set; }
    public int Rehangs { get; private set; }
    public double StepMsAvg { get; private set; }
    public double StepMsMax { get; private set; }
    public double RenderFrameMsTotal { get; private set; }
    public int RenderFrames { get; private set; }
    public double LastUpdateMs { get; private set; }
    public bool LastUpdateBaked { get; private set; }
    public float RootMismatchMm { get; private set; }
    public float LengthScale { get; private set; } = 1f;
    public string Warning { get; private set; }
    public int GroomVersion { get; private set; }
    public double BuildMs { get; private set; }
    Dictionary<string, object> restStats = new();

    /// <summary>effective hair opacity shown now (override or avatar + 0.15 ramped)</summary>
    public float Opacity { get; private set; } = 1f;
    /// <summary>tip glow fade with the avatar's opacity (1 with an opacity override)</summary>
    public float GlowFade { get; private set; } = 1f;
    public string OpacitySource => OpacityOverride >= 0f ? "override" : "avatar";

    /// <summary>hair opacity for an avatar opacity: a + 0.15 x smoothstep(0, 0.2, a), clamped (0.35 -> 0.5, 0 -> 0)</summary>
    public static float HairOpacityFor(float a) => Mathf.Clamp01(a + OpacityOffset * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(a / OpacityRamp)));
    public string ColourSource => colourSource;
    public int Lod => Mathf.Max(lod, 0);

    public int Guides => guides;
    public int Points => points;
    public int Ribbons => ribbons;
    public int Cards => cardCount;
    public int Glows => glowDescs.IsCreated ? glowDescs.Length : 0;
    public int GlowsAnchored { get; private set; }
    public int CardsDropped { get; private set; }
    public int VertexCount => Mode == RenderMode.Cards ? (cardVertices.IsCreated ? cardVertices.Length : 0) + (glowVertices.IsCreated ? glowVertices.Length : 0)
        : ribbonVertices.IsCreated ? ribbonVertices.Length : 0;
    public int TriangleCount => Mode == RenderMode.Cards ? (lodIndexCount[Lod] + (TipGlow > 0f ? glowLodIndexCount[Lod] : 0)) / 3 : ribbons * (points - 1) * 2;
    public int SimFrame => simFrame;
    public SmplxAvatar Avatar => avatar;
    public bool RendererVisible => Mode == RenderMode.Cards ? cardRenderer != null && cardRenderer.enabled : ribbonRenderer != null && ribbonRenderer.enabled;
    public bool GlowVisible => glowRenderer != null && glowRenderer.enabled;
    public float HeadRadius => headRadius;
    public Bounds HairBounds => cardRenderer != null && Mode == RenderMode.Cards ? cardRenderer.bounds : ribbonRenderer != null ? ribbonRenderer.bounds : default;

    /// <param name="skinPath">the follow's smplx skin binary (the roots are placed on its rest vertices)</param>
    /// <param name="groomPath">hair_groom.json in the capture folder</param>
    /// <param name="frameTime">audio-clock seconds of a capture frame (CaptureTimeline.AudioTimeOf)</param>
    public static HairStrands Create(SmplxAvatar avatar, string skinPath, string groomPath, Func<int, float> frameTime)
    {
        JObject groom = JObject.Parse(File.ReadAllText(groomPath));
        int version = groom.Value<int>("version");
        if (version != 1 && version != 2) throw new InvalidDataException($"{groomPath}: unsupported version {groom["version"]}");
        SmplxData.Skin skin = SmplxData.ReadSkin(skinPath);
        GameObject go = new($"{avatar.DancerRole} Hair") { layer = avatar.gameObject.layer };
        go.transform.SetParent(avatar.transform, false);
        HairStrands hair = go.AddComponent<HairStrands>();
        hair.GroomVersion = version;
        try
        {
            hair.Build(avatar, skin, groom, frameTime);
        }
        catch
        {
            Destroy(go); // never leave a half-built hair behind (its LateUpdate would run on missing data)
            throw;
        }

        Active = hair;
        return hair;
    }

    static Vector3 V3(JToken t) => new(t[0].Value<float>(), t[1].Value<float>(), t[2].Value<float>());
    static float F(JToken t, string key, float fallback) => t?[key] != null && t[key].Type != JTokenType.Null ? t[key].Value<float>() : fallback;

    void Build(SmplxAvatar av, SmplxData.Skin skin, JObject g, Func<int, float> times)
    {
        Stopwatch buildWatch = Stopwatch.StartNew();
        avatar = av;
        frameTime = times;
        groom = g;

        JObject strands = (JObject)g["strands"];
        JArray guideList = (JArray)g["guides"];
        guides = guideList.Count;
        points = strands.Value<int>("segments") + 1;
        rows = points;
        ribbonsPerGuide = 1 + strands.Value<int>("children");
        ribbons = guides * ribbonsPerGuide;
        clump = strands.Value<float>("tip_clump");

        JObject model = (JObject)g["model"];
        float skinHeight = float.MinValue, skinLow = float.MaxValue;
        foreach (Vector3 v in skin.RestVertices)
        {
            skinHeight = Mathf.Max(skinHeight, v.y);
            skinLow = Mathf.Min(skinLow, v.y);
        }

        LengthScale = (skinHeight - skinLow) / model.Value<float>("rest_height_m");
        spread = strands.Value<float>("child_spread_m") * LengthScale;
        headCentreLocal = V3(model["head_sphere_centre_unity"]) * LengthScale;
        headRadius = model.Value<float>("head_radius_m") * LengthScale;
        JObject shape = (JObject)g["shape"];
        standoff = shape.Value<float>("standoff_m") * LengthScale;
        partX = F(g["style"], "part_x_unity", shape.Value<float>("part_x_unity")) * LengthScale;

        if (g["style"] is JObject style)
        {
            if (style["comb_unity"] is JObject comb)
            {
                if (comb["front_right"] is JArray fr) combFrontRight = V3(fr);
                if (comb["front_left"] is JArray fl) combFrontLeft = V3(fl);
                if (comb["back"] is JArray bk) combBack = V3(bk);
            }

            if (style["front_abs_az_deg"] is JArray fa) (frontAz0, frontAz1) = (fa[0].Value<float>(), fa[1].Value<float>());
            rootLift = F(style, "root_lift", rootLift);
            outwardBias = F(style, "outward_bias", outwardBias);
            downSlerp = F(style, "down_slerp_per_segment", downSlerp);
            frontDevAz = F(style, "front_dev_abs_az_deg", frontDevAz);
            settleShapeRoot = F(style, "settle_comb_hold", settleShapeRoot);
            settleShapeFalloff = F(style, "settle_comb_falloff", settleShapeFalloff);
            settleShapeFalloffFrontRight = F(style, "settle_comb_falloff_front_right", settleShapeFalloffFrontRight);
            hugVolume = F(style, "hug_volume_m", hugVolume);
            hugBelow = F(style, "hug_below_r", hugBelow);
            faceClear = F(style, "face_clear_m", faceClear);
            frontRightForward = F(style, "front_right_forward", frontRightForward);
        }

        hugVolume *= LengthScale;
        faceClear *= LengthScale;
        if (g["cards"]?["lod_head_px"] is JArray lodPx && lodPx.Count == 2) (lod0Px, lod1Px) = (lodPx[0].Value<float>(), lodPx[1].Value<float>());

        JToken fb = g["face_box"];
        faceBox = new FaceBox
        {
            centre = headCentreLocal, r = headRadius, fwdMin = F(fb, "fwd_min_r", 0.25f), yLo = F(fb, "y_lo_r", -1.55f),
            yHi = F(fb, "y_hi_r", 0.45f), halfWidth = F(fb, "half_width_r", 0.80f), margin = F(fb, "margin_m", 0.006f) * LengthScale,
            sideBias = F(fb, "side_bias_r", 0.35f), enabled = 1
        };

        JObject dyn = (JObject)g["dynamics"];
        Substeps = Mathf.Clamp(dyn.Value<int>("substeps"), 1, 64);
        Iterations = dyn.Value<int>("iterations");
        Damping = dyn.Value<float>("damping_per_s");
        BendCompliance = dyn.Value<float>("bend_compliance");
        Friction = dyn.Value<float>("friction");
        ParticleRadius = dyn.Value<float>("particle_radius_m");
        ShapeRoot = dyn.Value<float>("shape_stiffness_root");
        ShapeFalloff = dyn.Value<float>("shape_falloff");
        MaxStretch = F(dyn, "max_stretch", MaxStretch);
        MaxDeviationBack = F(dyn, "max_deviation_back", GroomVersion >= 2 ? MaxDeviationBack : 1.3f);
        MaxDeviationFront = F(dyn, "max_deviation_front", GroomVersion >= 2 ? MaxDeviationFront : 1.3f);
        HeadStandoff = F(dyn, "head_standoff_fraction", HeadStandoff);
        SettleSeconds = F(dyn, "settle_s", SettleSeconds);
        PreRollFrames = (int)F(dyn, "preroll_frames", PreRollFrames);
        JToken frDyn = dyn["front_right"];
        FrontRightShapeRoot = F(frDyn, "shape_stiffness_root", ShapeRoot);
        FrontRightFalloff = F(frDyn, "shape_falloff", frDyn != null ? FrontRightFalloff : ShapeFalloff);
        MaxDeviationFrontRight = F(frDyn, "max_deviation", frDyn != null ? MaxDeviationFrontRight : MaxDeviationFront);
        FrontRightAbsAz = F(frDyn, "abs_az_max", frontAz0);
        JToken sideDyn = frDyn?["side"];
        SideAbsAz = F(sideDyn, "abs_az_max", FrontRightAbsAz);
        SideShapeRoot = F(sideDyn, "shape_stiffness_root", ShapeRoot);
        SideFalloff = F(sideDyn, "shape_falloff", ShapeFalloff);
        MaxDeviationSide = F(sideDyn, "max_deviation", MaxDeviationFront);

        // roots on the exported skin: SMPL-X vertex ids -> split vertices (skin v2) or identity (v1)
        Dictionary<int, int> split = new();
        if (skin.VertexIds != null)
        {
            for (int i = skin.VertexIds.Length - 1; i >= 0; i--) split[skin.VertexIds[i]] = i;
        }

        Vector3 headJ = skin.RestJoints[HeadJoint];
        Vector3[] roots = new Vector3[guides];
        Vector3[] normals = new Vector3[guides];
        guideLength = new float[guides];
        guideAz = new float[guides];
        guideEl = new float[guides];
        guideSide = new int[guides];
        float mismatch = 0f;
        for (int k = 0; k < guides; k++)
        {
            JObject gd = (JObject)guideList[k];
            Vector3 p = Vector3.zero;
            for (int c = 0; c < 3; c++)
            {
                int vid = gd["vid"][c].Value<int>();
                int sv = skin.VertexIds == null ? vid : split.TryGetValue(vid, out int s) ? s : -1;
                if (sv < 0 || sv >= skin.RestVertices.Length) throw new InvalidDataException($"hair root vertex {vid} not in the skin");
                p += gd["bary"][c].Value<float>() * skin.RestVertices[sv];
            }

            roots[k] = p - headJ;
            mismatch = Mathf.Max(mismatch, (roots[k] - V3(gd["pos_unity"]) * LengthScale).magnitude);
            normals[k] = V3(gd["normal_unity"]).normalized;
            guideLength[k] = gd.Value<float>("len") * LengthScale;
            Vector3 d = roots[k] - headCentreLocal;
            guideAz[k] = gd["az"] != null ? gd.Value<float>("az") : Mathf.Atan2(d.x, -d.z) * Mathf.Rad2Deg;
            guideEl[k] = gd["el"] != null ? gd.Value<float>("el") : Mathf.Asin(Mathf.Clamp(d.y / Mathf.Max(d.magnitude, 1e-6f), -1f, 1f)) * Mathf.Rad2Deg;
            guideSide[k] = roots[k].x >= partX ? 1 : -1;
        }

        frontRight = new bool[guides];
        for (int k = 0; k < guides; k++) frontRight[k] = Mathf.Abs(guideAz[k]) < frontAz0 && guideSide[k] < 0;
        UpdateFrontRightZone();

        RootMismatchMm = mismatch * 1000f;
        if (RootMismatchMm > 5f) Warning = $"hair roots {RootMismatchMm:0.0} mm off the groom's scalp (different SMPL-X model or betas?)";

        JArray cols = (JArray)g["colliders"];
        colliderDefs = new ColliderDef[cols.Count];
        for (int c = 0; c < cols.Count; c++)
        {
            string name = cols[c].Value<string>("name");
            bool arm = name.Contains("arm");
            colliderDefs[c] = new ColliderDef
            {
                Name = name, A = cols[c].Value<int>("a"), B = cols[c].Value<int>("b"),
                Oa = V3(cols[c]["oa_unity"]) * LengthScale, Ob = V3(cols[c]["ob_unity"]) * LengthScale,
                R = cols[c].Value<float>("r") * LengthScale, Arm = arm, Head = name == "head",
                FirstPoint = (int)F(cols[c], "first_point", arm ? 5 : 0), Friction = F(cols[c], "friction", arm ? 0f : -1f),
                MaxPush = F(cols[c], "max_push_m", arm ? 0.01f : 0f) * LengthScale
            };
        }

        Allocate();
        for (int k = 0; k < guides; k++) segLen[k] = guideLength[k] / (points - 1);
        Settle(roots, normals, skin.RestJoints);
        BuildCards(roots, skin);
        BuildMaterials();
        kinPrev = NewKin();
        kinCur = NewKin();
        BuildMs = buildWatch.Elapsed.TotalMilliseconds;
    }

    Kin NewKin() => new() { A = new float3[colliderDefs.Length], B = new float3[colliderDefs.Length] };

    void Allocate()
    {
        int n = guides * points;
        pos = new NativeArray<float3>(n, Allocator.Persistent);
        prev = new NativeArray<float3>(n, Allocator.Persistent);
        restLocal = new NativeArray<float3>(n, Allocator.Persistent);
        bendRest = new NativeArray<float>(n, Allocator.Persistent);
        segLen = new NativeArray<float>(guides, Allocator.Persistent);
        shapeK = new NativeArray<float>(guides * points, Allocator.Persistent); // per guide (the front-right section differs)
        guideInfo = new NativeArray<float2>(guides, Allocator.Persistent);
        resets = new NativeArray<int>(guides, Allocator.Persistent);
        capsules = new NativeArray<Capsule>(colliderDefs.Length + 1, Allocator.Persistent);
    }

    /// <summary>comb direction of a root (head-bone axes): front right over the temple, front left behind the ear,
    /// back down with a right bias; blended by |azimuth|</summary>
    Vector3 Comb(float az, int side)
    {
        Vector3 front = (side > 0 ? combFrontLeft : combFrontRight).normalized;
        float wb = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(frontAz0, frontAz1, Mathf.Abs(az)));
        return Vector3.Lerp(front, combBack.normalized, wb);
    }

    /// <summary>
    /// Rest shape, sleek and straight like her portrait. Every strand leaves its root along the comb direction of its
    /// zone; above the skull's equator it runs ALONG the skull (direction projected onto the head collider's tangent
    /// plane, kept at a hug distance that grows with the root's elevation: strands from the crown lie over the lower
    /// ones, a few cm of layered volume at the sides, none on top); while in front of the face it keeps sweeping
    /// sideways (the side-swept front never falls over the face); below that it turns down by downSlerp per segment -
    /// the front-right section toward the front of the right shoulder - with a small outward bias. Then it settles
    /// under gravity on her REST (T-pose) body with the body capsules and the face box, the comb held near the roots
    /// (longer for the front-right sweep), in head-bone space. The settled shape is the shape-constraint target.
    /// (Offline replica for tuning: the hair builder's settle_sim.py; numbers in hair_groom.json "style".)
    /// </summary>
    void Settle(Vector3[] roots, Vector3[] normals, Vector3[] restJoints)
    {
        Vector3 headJ = restJoints[HeadJoint];
        ColliderDef headDef = colliderDefs.FirstOrDefault(c => c.Head);
        Vector3 ha = restJoints[headDef.A] - headJ + headDef.Oa, hb = restJoints[headDef.B] - headJ + headDef.Ob;
        float hr = headDef.R > 0f ? headDef.R + standoff : headRadius + standoff;
        if (headDef.R <= 0f) ha = hb = headCentreLocal;
        float equator = 0.5f * (ha.y + hb.y) - hugBelow * hr;
        float shoulderY = restJoints[17].y - headJ.y;
        float faceHalf = faceBox.halfWidth * headRadius + faceClear;
        Vector3 Closest(Vector3 p)
        {
            Vector3 ab = hb - ha;
            float t = Mathf.Clamp01(Vector3.Dot(p - ha, ab) / Mathf.Max(ab.sqrMagnitude, 1e-12f));
            return ha + ab * t;
        }

        for (int k = 0; k < guides; k++)
        {
            Vector3 n = normals[k];
            Vector3 comb = Comb(guideAz[k], guideSide[k]);
            comb -= n * Vector3.Dot(comb, n);
            comb = comb.sqrMagnitude > 1e-8f ? comb.normalized : Vector3.down;
            Vector3 d1 = (n * rootLift + comb).normalized;
            float L = segLen[k];
            int o = k * points;
            Vector3 p = roots[k];
            pos[o] = p;
            p += d1 * L;
            pos[o + 1] = p;
            Vector3 dir = d1;
            float arc = L;
            float layer = Mathf.Clamp01((guideEl[k] + 30f) / 120f) * hugVolume;
            Vector3 drape = frontRight[k] ? (Vector3.down + Vector3.back * frontRightForward).normalized : Vector3.down;
            for (int i = 2; i < points; i++)
            {
                arc += L;
                float hug = hr + ParticleRadius + layer * Mathf.Clamp01(arc / (0.08f * LengthScale));
                Vector3 rel = p - headCentreLocal;
                bool faceColumn = -rel.z > faceBox.fwdMin * headRadius && Mathf.Abs(rel.x) < faceHalf && rel.y > faceBox.yLo * headRadius;
                Vector3 target = p.y < shoulderY ? Vector3.down : drape;
                if (!faceColumn) dir = Vector3.Slerp(dir, target, downSlerp);
                Vector3 q = Closest(p);
                Vector3 nn = (p - q).normalized;
                bool onSkull = p.y > equator;
                Vector3 step;
                if (onSkull)
                {
                    Vector3 t = dir - nn * Vector3.Dot(dir, nn);
                    dir = t.sqrMagnitude > 1e-10f ? t.normalized : dir;
                    step = dir;
                }
                else
                {
                    step = (dir + rel.normalized * outwardBias).normalized;
                }

                p += step * L;
                q = Closest(p);
                Vector3 v = p - q;
                float dist = v.magnitude;
                if (dist > 1e-6f && ((onSkull && p.y > equator) || dist < hug)) p = q + v / dist * hug;
                pos[o + i] = p;
            }

            for (int i = 0; i < points; i++)
            {
                restLocal[o + i] = pos[o + i];
                prev[o + i] = pos[o + i];
            }

            guideInfo[k] = new float2(DeviationOf(k), guideSide[k]);
        }

        // settle colliders in head-bone space (rest pose: every bone has the identity rotation)
        List<Capsule> caps = new();
        foreach (ColliderDef c in colliderDefs)
        {
            if (c.Arm) continue; // T-pose arms stick out sideways: not where the hair hangs while dancing
            float3 a = restJoints[c.A] - headJ + c.Oa, b = restJoints[c.B] - headJ + c.Ob;
            float r = c.Head ? c.R + standoff : c.R + 0.5f * standoff;
            caps.Add(new Capsule { a0 = a, b0 = b, a1 = a, b1 = b, r = r, friction = -1f });
        }

        using NativeArray<Capsule> settleCaps = new(caps.ToArray(), Allocator.TempJob);
        for (int i = 0; i < guides * points; i++) bendRest[i] = 0f;
        FaceBox face = faceBox;
        face.enabled = FaceGuard ? 1 : 0;
        NativeArray<float2> info = guideInfo;
        float2[] keepInfo = info.ToArray();
        for (int k = 0; k < guides; k++) info[k] = new float2(0f, keepInfo[k].y); // no deviation cone in the settle
        // the comb held near the roots (falloff), the front-right sweep held longer (it drapes forward): per guide
        for (int k = 0; k < guides; k++)
        {
            float falloff = frontRight[k] ? settleShapeFalloffFrontRight : settleShapeFalloff;
            for (int i = 0; i < points; i++)
            {
                float s = (float)i / (points - 1);
                shapeK[k * points + i] = i < 2 ? 0f : Mathf.Clamp01(settleShapeRoot * Mathf.Exp(-s / Mathf.Max(falloff, 1e-3f)));
            }
        }

        StrandSimJob job = new()
        {
            pos = pos, prev = prev, restLocal = restLocal, segLen = segLen, bendRest = bendRest, shapeK = shapeK,
            guideInfo = guideInfo, capsules = settleCaps, resets = resets,
            p = new SimParams
            {
                points = points, substeps = 360, iterations = 2, dt = 1f / 120f, damping = 6f, bendCompliance = 1f,
                friction = 0.6f, particleRadius = ParticleRadius, maxSpeed = 5f, maxStretch = MaxStretch,
                gravity = new float3(0, -9.81f, 0), head0 = float3.zero, head1 = float3.zero, rot0 = quaternion.identity,
                rot1 = quaternion.identity, face = face, capStart = 0, capCount = settleCaps.Length
            }
        };
        job.Schedule(guides, 4).Complete();
        info.CopyFrom(keepInfo);
        prev.CopyFrom(pos);
        for (int k = 0; k < guides; k++)
        {
            int o = k * points;
            for (int i = 0; i < points; i++) restLocal[o + i] = pos[o + i];
            for (int i = 2; i < points; i++) bendRest[o + i] = math.distance(restLocal[o + i], restLocal[o + i - 2]);
            resets[k] = 0;
        }

        UpdateShapeStiffness();
        RestStats(restJoints);
    }

    /// <summary>rest-shape checks of the style (head-bone space, T-pose): face box clear, the right front section in
    /// front of the right shoulder, the left front behind the ear</summary>
    void RestStats(Vector3[] restJoints)
    {
        Vector3 headJ = restJoints[HeadJoint];
        Vector3 rsh = restJoints[17] - headJ;
        int inBox = 0, fr = 0, frOk = 0, fl = 0, flOk = 0;
        float earY = headCentreLocal.y - 0.35f * headRadius;
        for (int k = 0; k < guides; k++)
        {
            int o = k * points;
            for (int i = 1; i < points; i++)
                if (faceBox.Inside(restLocal[o + i])) inBox++;
            if (Mathf.Abs(guideAz[k]) >= frontAz0) continue;
            float3 tip = restLocal[o + points - 1];
            if (guideSide[k] < 0)
            {
                fr++;
                if (tip.z < rsh.z - 0.02f * LengthScale) frOk++;
            }
            else
            {
                fl++;
                int best = 1;
                for (int i = 1; i < points; i++)
                    if (Mathf.Abs(restLocal[o + i].y - earY) < Mathf.Abs(restLocal[o + best].y - earY)) best = i;
                if (restLocal[o + best].z > headCentreLocal.z + 0.02f * LengthScale) flOk++;
            }
        }

        restStats = new Dictionary<string, object>
        {
            ["faceBoxPoints"] = inBox, ["frontRightGuides"] = fr, ["frontRightTipsForwardOfShoulder"] = fr > 0 ? (float)frOk / fr : float.NaN,
            ["frontLeftGuides"] = fl, ["frontLeftBehindEar"] = fl > 0 ? (float)flOk / fl : float.NaN
        };
    }

    /// <summary>shape stiffness = fraction of the way back to the rest shape per 1/30 s at the root, decaying
    /// exponentially along the strand (falloff = arc fraction per e-fold); converted to a per-iteration factor so the
    /// look does not depend on the substep / iteration counts. Per guide: the front-right section (draped in front of
    /// the right shoulder) has its own root / falloff - held further down, it swings as a sheet in fast turns instead
    /// of curling into hooks under the chin.</summary>
    void UpdateShapeStiffness()
    {
        int n = Mathf.Max(1, Substeps * Iterations);
        for (int g = 0; g < guides; g++)
        {
            int z = dynZone != null ? dynZone[g] : 0;
            float root = z == 1 ? FrontRightShapeRoot : z == 2 ? SideShapeRoot : ShapeRoot;
            float falloff = z == 1 ? FrontRightFalloff : z == 2 ? SideFalloff : ShapeFalloff;
            for (int i = 0; i < points; i++)
            {
                float s = (float)i / (points - 1);
                float perFrame = Mathf.Clamp(root * Mathf.Exp(-s / Mathf.Max(falloff, 1e-3f)), 0f, 0.99f);
                shapeK[g * points + i] = i < 2 ? 0f : 1f - Mathf.Pow(1f - perFrame, 1f / n);
            }
        }
    }

    /// <summary>deviation cone (x arc length) of a guide: front-right section, other front guides, back</summary>
    /// <summary>the dynamics zones of the front-right / side settings (call after changing FrontRightAbsAz / SideAbsAz)</summary>
    public void UpdateFrontRightZone()
    {
        if (guideAz == null) return;
        dynZone ??= new int[guides];
        for (int k = 0; k < guides; k++)
        {
            float a = Mathf.Abs(guideAz[k]);
            dynZone[k] = guideSide[k] >= 0 ? 0 : a < FrontRightAbsAz ? 1 : a < SideAbsAz ? 2 : 0;
        }
    }

    float DeviationOf(int k) => dynZone != null && dynZone[k] == 1 ? MaxDeviationFrontRight
        : dynZone != null && dynZone[k] == 2 ? MaxDeviationSide
        : Mathf.Abs(guideAz[k]) < frontDevAz ? MaxDeviationFront : MaxDeviationBack;

    // ------------------------------------------------------------------------------------------------ cards

    void BuildCards(Vector3[] roots, SmplxData.Skin skin)
    {
        HairCardLayout.Layer[] layers = HairCardLayout.FromGroom(groom["cards"] as JObject);
        Vector3[] rest = new Vector3[guides * points];
        for (int i = 0; i < rest.Length; i++) rest[i] = restLocal[i];
        // the atlas first: the tip glows follow its strands (HairAtlas.GlowAnchor)
        atlas = HairAtlas.Get((int)F(groom["cards"]?["atlas"], "seed", 1));
        JToken tg = groom["render"]?["tip_glow"];
        bool anchored = tg?["anchor_to_strands"] == null || tg.Value<bool>("anchor_to_strands");
        layout = HairCardLayout.Build(layers, roots, rest, points, guideAz, guideEl, guideSide, guideLength, partX, LengthScale, 7,
            (int)F(tg, "per_outer_card", 1), (int)F(tg, "outer_card_stride", 1), anchored ? HairAtlas.GlowAnchor : null, F(tg, "spacing_m", 0f),
            F(tg, "min_abs_az_deg", 0f));
        GlowsAnchored = layout.Glows.Count(gd => gd.vEnd > 0f);
        cardCount = layout.Cards.Length;
        BuildCap(skin);
        RootDrops(roots);
        cardDescs = new NativeArray<CardDesc>(cardCount, Allocator.Persistent);
        for (int c = 0; c < cardCount; c++) cardDescs[c] = layout.Cards[c].Desc;
        capChunks = (capVertexCount + CardMeshJob.CapChunk - 1) / CardMeshJob.CapChunk;
        int vcount = cardCount * rows * 3 + capVertexCount;
        cardVertices = new NativeArray<CardVertex>(vcount, Allocator.Persistent);
        chunkMin = new NativeArray<float3>(cardCount + capChunks, Allocator.Persistent);
        chunkMax = new NativeArray<float3>(cardCount + capChunks, Allocator.Persistent);
        glowDescs = new NativeArray<GlowDesc>(layout.Glows, Allocator.Persistent);
        glowVertices = new NativeArray<GlowVertex>(layout.Glows.Length * GlowVerts * 2, Allocator.Persistent);

        // card mesh: dynamic stream (position, shading normal, tangent) + static stream (atlas uv, s, random, layer |
        // card length, edge fade, ao, alpha)
        cardMesh = new Mesh { name = "Hair cards", indexFormat = vcount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        cardMesh.MarkDynamic();
        cardMesh.SetVertexBufferParams(vcount,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float16, 4, 0),
            new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float16, 4, 0),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4, 1));
        using (NativeArray<float4> st = new(vcount * 2, Allocator.Temp))
        {
            NativeArray<float4> s = st;
            for (int c = 0; c < cardCount; c++)
            {
                HairCardLayout.Card cd = layout.Cards[c];
                for (int k = 0; k < rows; k++)
                for (int j = 0; j < 3; j++)
                {
                    int v = (c * rows + k) * 3 + j;
                    s[v * 2] = new float4(HairAtlas.AtlasU(cd.Tile, j * 0.5f), (float)k / (rows - 1), cd.Random, cd.Layer);
                    s[v * 2 + 1] = new float4(cd.Length, cd.EdgeFade ? 1f : 0f, cd.Ao, 1f);
                }
            }

            for (int v = 0; v < capVertexCount; v++)
            {
                int i = cardCount * rows * 3 + v;
                s[i * 2] = capUv0[v];
                s[i * 2 + 1] = capUv1[v];
            }

            cardMesh.SetVertexBufferData(s, 0, 0, vcount * 2, 1, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        }

        BuildCardIndices();

        // glow ribbons: one dynamic stream
        int gv = glowVertices.Length;
        glowMesh = new Mesh { name = "Hair tip glow", indexFormat = gv > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        glowMesh.MarkDynamic();
        glowMesh.SetVertexBufferParams(gv,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float16, 4, 0),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float16, 4, 0));
        int gi = layout.Glows.Length * (GlowVerts - 1) * 6;
        int[] gidx = new int[gi];
        int w = 0;
        for (int r = 0; r < layout.Glows.Length; r++)
        for (int i = 0; i < GlowVerts - 1; i++)
        {
            int v0 = (r * GlowVerts + i) * 2;
            gidx[w++] = v0;
            gidx[w++] = v0 + 1;
            gidx[w++] = v0 + 3;
            gidx[w++] = v0;
            gidx[w++] = v0 + 3;
            gidx[w++] = v0 + 2;
        }

        SetIndices(glowMesh, gidx);
        glowLodIndexCount[0] = gi;
        glowLodIndexCount[1] = glowLodIndexCount[2] = layout.GlowsLowLod * (GlowVerts - 1) * 6;

        GameObject cardsGo = new("Cards") { layer = gameObject.layer };
        cardsGo.transform.SetParent(transform, false);
        cardsGo.AddComponent<MeshFilter>().sharedMesh = cardMesh;
        cardRenderer = cardsGo.AddComponent<MeshRenderer>();
        GameObject tipsGo = new("Tip glow") { layer = gameObject.layer };
        tipsGo.transform.SetParent(transform, false);
        tipsGo.AddComponent<MeshFilter>().sharedMesh = glowMesh;
        glowRenderer = tipsGo.AddComponent<MeshRenderer>();
        foreach (MeshRenderer mr in new[] { cardRenderer, glowRenderer })
        {
            mr.shadowCastingMode = ShadowCastingMode.Off; // Quest: no hair shadow pass
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.BlendProbes;
            mr.enabled = false; // until the first simulated frame: never a mesh at the origin
        }
    }

    const int GlowPoints = 9, GlowVerts = GlowPoints + 1; // + the round end cap
    float4[] capUv0 = Array.Empty<float4>(), capUv1 = Array.Empty<float4>();
    // head vertices (head-bone space, normal, azimuth, elevation): root-drop targets
    readonly List<(Vector3 p, Vector3 n, float az, float el)> headVerts = new();
    float[] capTableAz = Array.Empty<float>(), capTableEl = Array.Empty<float>();

    static float Table(float[] xs, float[] ys, float x)
    {
        if (xs.Length == 0) return 0f;
        if (x <= xs[0]) return ys[0];
        for (int i = 1; i < xs.Length; i++)
            if (x <= xs[i]) return Mathf.Lerp(ys[i - 1], ys[i], Mathf.InverseLerp(xs[i - 1], xs[i], x));
        return ys[^1];
    }

    static (float[] az, float[] el) ReadTable(JToken t, (float, float)[] fallback)
    {
        List<float> a = new(), e = new();
        if (t is JArray arr)
            foreach (JToken row in arr)
            {
                a.Add(row[0].Value<float>());
                e.Add(row[1].Value<float>());
            }

        if (a.Count == 0)
            foreach ((float x, float y) in fallback)
            {
                a.Add(x);
                e.Add(y);
            }

        return (a.ToArray(), e.ToArray());
    }

    static readonly (float, float)[] DefaultHairline = { (0, 50), (100, 20), (180, -34) };

    /// <summary>
    /// Front cards whose roots are in the front row of the groom (within near_hairline_deg of the groom's hairline,
    /// |az| up to abs_az_max) start lower: their root is offset down toward the cap's (painted) hairline - to
    /// above_cap_deg above it, never below min_el_deg in front of the face (|az| &lt; face_abs_az: the face box) - and
    /// the offset fades out over fade_m of the card (CardMeshJob). The front of the hair then begins at the hairline
    /// painted into the avatar texture and sweeps up into the combed sheet, like the portrait's soft swept hairline.
    /// </summary>
    void RootDrops(Vector3[] roots)
    {
        CardsDropped = 0;
        JToken cfg = groom["cards"]?["root_drop"];
        if (cfg == null || headVerts.Count == 0 || (cfg["enabled"] != null && !cfg.Value<bool>("enabled"))) return;
        (float[] ga, float[] ge) = ReadTable(groom["model"]?["hairline_el_deg_vs_abs_az"], DefaultHairline);
        float azMax = F(cfg, "abs_az_max", 70f), near = F(cfg, "near_hairline_deg", 8f), above = F(cfg, "above_cap_deg", 3f);
        float minEl = F(cfg, "min_el_deg", 31f), faceAz = F(cfg, "face_abs_az", 55f), fade = F(cfg, "fade_m", 0.07f) * LengthScale;
        float lift = F(cfg, "offset_m", 0.002f) * LengthScale;
        for (int c = 0; c < layout.Cards.Length; c++)
        {
            HairCardLayout.Card card = layout.Cards[c];
            if (card.Layer == HairCardLayout.LayerFlyaway || card.Layer == HairCardLayout.LayerCap) continue;
            int g = card.Desc.guide.x;
            float az = guideAz[g], el = guideEl[g], aaz = Mathf.Abs(az);
            if (aaz > azMax || el > Table(ga, ge, aaz) + near) continue;
            float target = Table(capTableAz, capTableEl, aaz) + above;
            if (aaz < faceAz) target = Mathf.Max(target, minEl);
            if (el - target < 1f) continue;
            float best = float.MaxValue;
            Vector3 hit = Vector3.zero;
            foreach ((Vector3 hp, Vector3 hn, float haz, float hel) in headVerts)
            {
                float dAz = Mathf.DeltaAngle(haz, az) * Mathf.Cos(target * Mathf.Deg2Rad), dEl = hel - target;
                float d = dAz * dAz + dEl * dEl;
                if (d < best)
                {
                    best = d;
                    hit = hp + hn * lift;
                }
            }

            if (best > 36f) continue; // no head vertex within 6 deg
            CardDesc d0 = card.Desc;
            Vector3 root = roots[d0.guide.x] * d0.weight.x + (d0.weight.y > 0f ? roots[d0.guide.y] * d0.weight.y : Vector3.zero) +
                           (d0.weight.z > 0f ? roots[d0.guide.z] * d0.weight.z : Vector3.zero);
            float len = guideLength[g];
            d0.rootDrop = hit - root;
            d0.dropS = Mathf.Clamp(fade / Mathf.Max(len, 1e-3f), 0.02f, 0.9f);
            card.Desc = d0;
            layout.Cards[c] = card;
            CardsDropped++;
        }
    }

    static void SetIndices(Mesh mesh, int[] idx)
    {
        mesh.SetIndexBufferParams(idx.Length, mesh.indexFormat);
        if (mesh.indexFormat == IndexFormat.UInt16)
        {
            ushort[] i16 = new ushort[idx.Length];
            for (int i = 0; i < idx.Length; i++) i16[i] = (ushort)idx[i];
            mesh.SetIndexBufferData(i16, 0, 0, idx.Length, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        }
        else
        {
            mesh.SetIndexBufferData(idx, 0, 0, idx.Length, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        }

        mesh.subMeshCount = 1;
        mesh.SetSubMesh(0, new SubMeshDescriptor(0, idx.Length), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
    }

    /// <summary>index buffer in LOD order: cap, inner, outer | mid (every second) | mid (rest), part, flyaway; only the
    /// layers in LayerMask</summary>
    void BuildCardIndices()
    {
        List<int> idx = new();
        bool On(int layer) => (LayerMask & (1 << layer)) != 0;
        if (On(HairCardLayout.LayerCap))
        {
            int baseV = cardCount * rows * 3;
            foreach (int t in capTriangles) idx.Add(baseV + t);
        }

        void AddCards(Func<HairCardLayout.Card, bool> pick)
        {
            for (int c = 0; c < cardCount; c++)
            {
                if (!pick(layout.Cards[c]) || !On(layout.Cards[c].Layer)) continue;
                for (int k = 0; k < rows - 1; k++)
                for (int j = 0; j < 2; j++)
                {
                    int v0 = (c * rows + k) * 3 + j;
                    idx.Add(v0);
                    idx.Add(v0 + 3);
                    idx.Add(v0 + 1);
                    idx.Add(v0 + 1);
                    idx.Add(v0 + 3);
                    idx.Add(v0 + 4);
                }
            }
        }

        AddCards(c => c.Layer == HairCardLayout.LayerInner);
        AddCards(c => c.Layer == HairCardLayout.LayerOuter);
        lodIndexCount[2] = idx.Count;
        AddCards(c => c.Layer == HairCardLayout.LayerMid && !c.MidOdd);
        lodIndexCount[1] = idx.Count;
        AddCards(c => c.Layer == HairCardLayout.LayerMid && c.MidOdd);
        AddCards(c => c.Layer == HairCardLayout.LayerPart);
        AddCards(c => c.Layer == HairCardLayout.LayerFlyaway);
        lodIndexCount[0] = idx.Count;
        Bounds keepBounds = cardMesh.bounds;
        SetIndices(cardMesh, idx.ToArray());
        cardMesh.bounds = keepBounds;
        appliedMask = LayerMask;
        lod = -1;
    }

    /// <summary>
    /// Scalp cap: the SMPL-X head triangles above the cap's hairline, 1.5 mm off the scalp, rigid with the head bone. Its
    /// hairline (cards.cap.hairline_el_deg_vs_abs_az: where the avatar texture's painted hair ends, measured by
    /// hair_groom.py; else the groom's root hairline) is a fringed, anti-aliased edge in the shader: uv1.w = 0.5 + (el -
    /// hairline) / fade_deg (coverage 0.5 at the hairline, +- per-strand jitter). Shaded like the cards: comb tangent,
    /// procedural strands across the comb (uv0.x = cross-comb coordinate: height in front, sideways at the back),
    /// occlusion lighter at the hairline than under the cards (ao [hairline, deep]). It closes the gaps between the
    /// cards and covers the texture's baked hair (no dark band under the hairline, no dark notch at the parting).
    /// </summary>
    void BuildCap(SmplxData.Skin skin)
    {
        JToken capCfg = groom["cards"]?["cap"];
        float offset = F(capCfg, "offset_m", 0.0015f) * LengthScale;
        float fade = Mathf.Max(F(capCfg, "fade_deg", 5f), 0.5f);
        (float[] ga, float[] ge) = ReadTable(groom["model"]?["hairline_el_deg_vs_abs_az"], DefaultHairline);
        JToken capTable = capCfg?["hairline_el_deg_vs_abs_az"];
        (capTableAz, capTableEl) = capTable is JArray ? ReadTable(capTable, DefaultHairline) : (ga, ge);
        float aoHairline = 0.45f, aoDeep = 0.45f;
        if (capCfg?["ao"] is JArray aoArr && aoArr.Count == 2) (aoHairline, aoDeep) = (aoArr[0].Value<float>(), aoArr[1].Value<float>());
        else if (capCfg?["ao"] != null) aoHairline = aoDeep = capCfg.Value<float>("ao");
        float Hairline(float absAz) => Table(capTableAz, capTableEl, absAz);

        Vector3 headJ = skin.RestJoints[HeadJoint];
        int nv = skin.RestVertices.Length;
        int joints = skin.Weights.Length / Mathf.Max(nv, 1);
        int[] map = new int[nv];
        List<Vector3> P = new(), N = new(), T = new();
        List<float> alpha = new();
        List<float4> uv0 = new(), uv1 = new();
        headVerts.Clear();
        for (int v = 0; v < nv; v++)
        {
            map[v] = -1;
            int best = 0;
            float bw = -1f;
            for (int j = 0; j < joints; j++)
            {
                float w = skin.Weights[v * joints + j];
                if (w > bw)
                {
                    bw = w;
                    best = j;
                }
            }

            if (best != 15 && best != 23 && best != 24) continue;
            Vector3 local = skin.RestVertices[v] - headJ;
            Vector3 d = local - headCentreLocal;
            float az = Mathf.Atan2(d.x, -d.z) * Mathf.Rad2Deg;
            float el = Mathf.Asin(Mathf.Clamp(d.y / Mathf.Max(d.magnitude, 1e-6f), -1f, 1f)) * Mathf.Rad2Deg;
            float h = Hairline(Mathf.Abs(az));
            Vector3 n = skin.Normals != null && skin.Normals.Length == nv ? skin.Normals[v].normalized : d.normalized;
            headVerts.Add((local, n, az, el));
            float x = 0.5f + (el - h) / fade;
            if (x < -1f) continue;
            Vector3 comb = Comb(az, local.x >= partX ? 1 : -1);
            comb -= n * Vector3.Dot(comb, n);
            map[v] = P.Count;
            P.Add(local + n * offset);
            N.Add(n);
            T.Add(comb.sqrMagnitude > 1e-8f ? comb.normalized : Vector3.down);
            alpha.Add(x);
            // strands across the comb: height where the comb runs sideways / back (front), sideways where it runs down (back)
            float wb = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(frontAz0, frontAz1, Mathf.Abs(az)));
            float cross = Mathf.Lerp(d.y, d.x, wb); // metres (the shader: strands_per_m / LengthScale)
            float ao = Mathf.Lerp(aoHairline, aoDeep, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((x - 0.5f) / 3f)));
            uv0.Add(new float4(cross, 0.5f, 0.5f, HairCardLayout.LayerCap));
            uv1.Add(new float4(0.1f, 0f, ao, x));
        }

        List<int> tris = new();
        for (int t = 0; t + 2 < skin.Triangles.Length; t += 3)
        {
            int a = map[skin.Triangles[t]], b = map[skin.Triangles[t + 1]], c = map[skin.Triangles[t + 2]];
            if (a < 0 || b < 0 || c < 0) continue;
            if (Mathf.Max(alpha[a], Mathf.Max(alpha[b], alpha[c])) <= 0.15f) continue; // fully below the hairline
            tris.Add(a);
            tris.Add(b);
            tris.Add(c);
        }

        capVertexCount = P.Count;
        capTriangles = tris.ToArray();
        capUv0 = uv0.ToArray();
        capUv1 = uv1.ToArray();
        capPos = new NativeArray<float3>(Math.Max(1, P.Count), Allocator.Persistent);
        capNormal = new NativeArray<float3>(Math.Max(1, P.Count), Allocator.Persistent);
        capTangent = new NativeArray<float3>(Math.Max(1, P.Count), Allocator.Persistent);
        for (int i = 0; i < P.Count; i++)
        {
            capPos[i] = P[i];
            capNormal[i] = N[i];
            capTangent[i] = T[i];
        }
    }

    void BuildMaterials()
    {
        Shader cards = Resources.Load<Shader>("HM_HairCards");
        if (cards == null) cards = Shader.Find("HeadMovement/HairCards");
        Shader tips = Resources.Load<Shader>("HM_HairTips");
        if (tips == null) tips = Shader.Find("HeadMovement/HairTips");
        if (cards == null || tips == null) throw new InvalidOperationException("HM_HairCards / HM_HairTips shader missing");
        if (atlas == null) atlas = HairAtlas.Get((int)F(groom["cards"]?["atlas"], "seed", 1));

        depthMaterial = new Material(cards) { name = "Hair cards depth (A2C)", renderQueue = DepthQueue };
        depthMaterial.SetShaderPassEnabled("UniversalForward", false);
        colourMaterial = new Material(cards) { name = "Hair cards (Kajiya-Kay)", renderQueue = ColourQueue };
        colourMaterial.SetShaderPassEnabled("SRPDefaultUnlit", false);
        bool msaa = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp && urp.msaaSampleCount > 1;
        foreach (Material m in new[] { depthMaterial, colourMaterial })
        {
            m.SetTexture("_Atlas", atlas);
            m.SetFloat("_AlphaToMask", msaa ? 1f : 0f);
        }

        JObject render = groom["render"] as JObject;
        JObject kk = render?["kajiya_kay"] as JObject;
        if (kk != null && kk.Value<int?>("version") >= 2)
        {
            colourMaterial.SetFloat("_Shift1", F(kk, "primary_shift", 0.04f));
            colourMaterial.SetFloat("_Exp1", F(kk, "primary_exponent", 220f));
            colourMaterial.SetFloat("_Spec1", F(kk, "primary_strength", 0.18f));
            if (kk["primary_tint"] is JArray pt) colourMaterial.SetVector("_Tint1", (Vector4)V3(pt));
            colourMaterial.SetFloat("_Shift2", F(kk, "secondary_shift", -0.10f));
            colourMaterial.SetFloat("_Exp2", F(kk, "secondary_exponent", 36f));
            colourMaterial.SetFloat("_Spec2", F(kk, "secondary_strength", 0.38f));
            colourMaterial.SetFloat("_ShiftJitter", F(kk, "shift_jitter", 0.015f));
            if (kk["sparkle"] is JArray sp) colourMaterial.SetFloat("_SparkleLo", sp[0].Value<float>());
        }

        if (render?["tt"] is JObject tt)
        {
            if (tt["colour"] is JArray tc) colourMaterial.SetVector("_TTColor", (Vector4)V3(tc));
            colourMaterial.SetFloat("_TTStrength", F(tt, "strength", 0.8f));
            colourMaterial.SetFloat("_TTBeta", F(tt, "beta", 0.1f));
            colourMaterial.SetFloat("_TTAlpha", F(tt, "alpha", 0.035f));
        }

        if (render?["body_soft_clamp"] is JArray bc && bc.Count == 2)
        {
            colourMaterial.SetFloat("_ClampKnee", bc[0].Value<float>());
            colourMaterial.SetFloat("_ClampMax", bc[1].Value<float>());
        }

        if (render?["copper_lift"] is JArray cl)
            colourMaterial.SetVector("_CopperLift", new Vector4(cl[0].Value<float>(), cl[1].Value<float>(), cl[2].Value<float>(), F(render, "copper_lift_m", 0.1f) * LengthScale));
        colourMaterial.SetFloat("_VarLo", F(render, "var_lo", 0.88f));
        colourMaterial.SetFloat("_VarHi", F(render, "var_hi", 1.12f));
        if (groom["cards"]?["cap"] is JObject capCfg)
        {
            colourMaterial.SetFloat("_CapStrands", F(capCfg, "strands_per_m", 1400f) / Mathf.Max(LengthScale, 1e-3f));
            depthMaterial.SetFloat("_CapStrands", F(capCfg, "strands_per_m", 1400f) / Mathf.Max(LengthScale, 1e-3f));
            colourMaterial.SetFloat("_CapJitter", F(capCfg, "hairline_jitter", 0.6f));
            depthMaterial.SetFloat("_CapJitter", F(capCfg, "hairline_jitter", 0.6f));
            depthMaterial.SetFloat("_CapSoft", F(capCfg, "softness", 0.25f));
        }

        depthMaterial.SetFloat("_RootFade", F(groom["cards"], "root_fade_m", 0.015f) * LengthScale);

        string source = render?.Value<string>("albedo_default") ?? "video";
        if (!SetColourSource(source)) SetColourSource("video");

        glowMaterial = new Material(tips) { name = "Hair tip glow (additive)", renderQueue = GlowQueue };
        if (render?["tip_emission"]?["e"] is JArray e && e.Count == 6)
        {
            glowMaterial.SetVector("_Emit0123", new Vector4(e[0].Value<float>(), e[1].Value<float>(), e[2].Value<float>(), e[3].Value<float>()));
            glowMaterial.SetVector("_Emit45", new Vector4(e[4].Value<float>(), e[5].Value<float>(), 0, 0));
        }

        JToken tg = render?["tip_glow"];
        glowMaterial.SetFloat("_CorePx", F(tg, "core_px", 2f));
        if (tg?["halo_px"] is JArray hp && hp.Count == 2)
        {
            glowMaterial.SetFloat("_HaloMinPx", hp[0].Value<float>());
            glowMaterial.SetFloat("_HaloPx", hp[1].Value<float>());
        }

        glowMaterial.SetFloat("_HaloWorld", F(tg, "halo_m", 0.004f) * LengthScale);
        glowMaterial.SetFloat("_Halo", F(tg, "halo", 0.3f));
        glowMaterial.SetFloat("_Nudge", F(tg, "camera_nudge_m", 0.003f) * LengthScale);
        glowWindow = F(tg, "window_m", 0.32f) * LengthScale;
        glowSpan = F(tg, "ribbon_m", 0.20f) * LengthScale;
        glowEnd = F(tg, "end_fraction", glowEnd);
        if (tg?["width_m"] is JArray gw && gw.Count == 2) (glowWidthRoot, glowWidthTip) = (gw[0].Value<float>() * LengthScale, gw[1].Value<float>() * LengthScale);

        cardRenderer.sharedMaterials = new[] { depthMaterial, colourMaterial };
        glowRenderer.sharedMaterial = glowMaterial;
        ApplyLook();
    }

    float glowWindow = 0.32f, glowSpan = 0.20f, glowWidthRoot = 0.0025f, glowWidthTip = 0.0008f, glowEnd = 0.92f;

    /// <summary>albedo source: "video" (the demo video's observed colour x gain) or "portrait" (her portrait's crimson,
    /// white-balanced the same way); the groom's render.albedo_default picks the one shown at load</summary>
    public bool SetColourSource(string source)
    {
        if (colourMaterial == null) return false;
        JObject render = groom["render"] as JObject;
        if (source == "portrait" && render?["kajiya_kay"]?["highlight_portrait_linear"] is JArray hpl && render["albedo_portrait_linear"] is JArray apl)
        {
            colourMaterial.SetVector("_BaseColor", new Vector4(apl[0].Value<float>(), apl[1].Value<float>(), apl[2].Value<float>(), 1));
            colourMaterial.SetVector("_HighlightColor", (Vector4)V3(hpl));
            colourSource = source;
            return true;
        }

        JArray a = source == "portrait" ? render?["albedo_portrait_linear"] as JArray : render?["albedo_linear"] as JArray;
        Vector4 albedo;
        if (a != null) albedo = new Vector4(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>(), 1);
        else if (source == "portrait") return false;
        else if (groom["colour"]?["mid"]?["linear_srgb"] is JArray mid)
            albedo = new Vector4(mid[0].Value<float>(), mid[1].Value<float>(), mid[2].Value<float>(), 1) * F(render, "albedo_gain", 1.85f);
        else albedo = new Vector4(0.30f, 0.10f, 0.06f, 1); // generic auburn (no groom colour)
        colourMaterial.SetVector("_BaseColor", albedo);
        JArray hl = render?["kajiya_kay"]?["highlight_linear"] as JArray;
        Vector4 h = hl != null ? (Vector4)V3(hl) : new Vector4(Mathf.Min(1f, albedo.x * 1.8f), Mathf.Min(1f, albedo.y * 2f), Mathf.Min(1f, albedo.z * 2.5f), 1);
        if (source == "portrait") h = new Vector4(Mathf.Min(1f, albedo.x * 2f), Mathf.Min(1f, albedo.y * 2f), Mathf.Min(1f, albedo.z * 2f), 1);
        colourMaterial.SetVector("_HighlightColor", h);
        colourSource = source;
        return true;
    }

    public void ApplyLook()
    {
        if (colourMaterial != null) colourMaterial.SetFloat("_Opacity", Opacity);
        if (glowMaterial != null) glowMaterial.SetFloat("_TipGlow", TipGlow * GlowFade);
        if (ribbonMaterial != null)
        {
            ribbonMaterial.SetFloat("_TipGlow", TipGlow);
            ribbonMaterial.SetFloat("_Opacity", Opacity);
        }
    }

    // ------------------------------------------------------------------------------------------------ simulation

    void ReadKinematics(ref Kin k)
    {
        Transform root = avatar.transform;
        Transform head = avatar.Bone(HeadJoint);
        k.HeadPos = root.InverseTransformPoint(head.position);
        k.HeadRot = Quaternion.Inverse(root.rotation) * head.rotation;
        for (int c = 0; c < colliderDefs.Length; c++)
        {
            ColliderDef d = colliderDefs[c];
            k.A[c] = root.InverseTransformPoint(avatar.Bone(d.A).TransformPoint(d.Oa));
            k.B[c] = root.InverseTransformPoint(avatar.Bone(d.B).TransformPoint(d.Ob));
        }
    }

    static void CopyKin(in Kin from, ref Kin to)
    {
        to.HeadPos = from.HeadPos;
        to.HeadRot = from.HeadRot;
        Array.Copy(from.A, to.A, from.A.Length);
        Array.Copy(from.B, to.B, from.B.Length);
    }

    Capsule MakeCapsule(int c, float3 a0, float3 b0, float3 a1, float3 b1)
    {
        ColliderDef d = colliderDefs[c];
        // the head collider keeps part of the groom's standoff (the hair layer over the scalp); arms can be switched off
        float r = d.Arm && !ArmCollisions ? 0f : d.R + (d.Head ? HeadStandoff * standoff : 0f);
        return new Capsule { a0 = a0, b0 = b0, a1 = a1, b1 = b1, r = r, firstPoint = d.FirstPoint, friction = d.Friction, maxPush = d.MaxPush };
    }

    /// <summary>solver settings shared by the live step and the bake (head pose / dt / capsule range set per step)</summary>
    SimParams BaseParams()
    {
        FaceBox face = faceBox;
        face.enabled = FaceGuard ? 1 : 0;
        return new SimParams
        {
            points = points, substeps = Mathf.Clamp(Substeps, 1, 64), iterations = Mathf.Clamp(Iterations, 1, 8),
            damping = Damping, bendCompliance = BendCompliance, friction = Friction, particleRadius = ParticleRadius,
            maxSpeed = 30f, maxStretch = MaxStretch, gravity = (float3)(avatar.transform.InverseTransformDirection(Vector3.down) * 9.81f),
            face = face, capStart = 0, capCount = colliderDefs.Length + 1
        };
    }

    void UpdateGuideInfo()
    {
        for (int k = 0; k < guides; k++) guideInfo[k] = new float2(DeviationOf(k), guideSide[k]);
    }

    /// <summary>strands in their rest shape on the head at the current kinematics, velocities zero</summary>
    void Rehang(in Kin k)
    {
        for (int g = 0; g < guides; g++)
        {
            int o = g * points;
            for (int i = 0; i < points; i++)
            {
                float3 p = k.HeadPos + math.mul(k.HeadRot, restLocal[o + i]);
                pos[o + i] = p;
                prev[o + i] = p;
            }
        }
    }

    /// <summary>one capture-frame step from kinPrev to kinCur; settle = a static, heavily damped relaxation in the
    /// pose of kinCur (after a re-hang: the rest shape finds its equilibrium in the current pose without being flung)</summary>
    void Step(float dt, bool settle = false)
    {
        for (int c = 0; c < colliderDefs.Length; c++)
            capsules[c] = MakeCapsule(c, kinPrev.A[c], kinPrev.B[c], kinCur.A[c], kinCur.B[c]);

        // a zero-size dummy keeps the array length fixed (capsules has one spare slot)
        capsules[colliderDefs.Length] = default;
        int sub = settle ? Mathf.Max(1, Mathf.RoundToInt(SettleSeconds * 120f)) : Mathf.Clamp(Substeps, 1, 64);
        if (settle) dt = SettleSeconds;
        // q and -q are the same rotation: interpolate along the short arc (nlerp across a sign flip passes near zero
        // and flings the roots and shape targets around)
        quaternion r1 = kinCur.HeadRot;
        if (math.dot(kinPrev.HeadRot.value, r1.value) < 0f) r1 = new quaternion(-r1.value);
        SimParams p = BaseParams();
        p.substeps = sub;
        p.dt = dt / sub;
        p.damping = settle ? 10f : Damping;
        p.head0 = kinPrev.HeadPos;
        p.head1 = kinCur.HeadPos;
        p.rot0 = kinPrev.HeadRot;
        p.rot1 = r1;
        StrandSimJob job = new()
        {
            pos = pos, prev = prev, restLocal = restLocal, segLen = segLen, bendRest = bendRest, shapeK = shapeK,
            guideInfo = guideInfo, capsules = capsules, resets = resets, p = p
        };
        part.Restart();
        job.Schedule(guides, 8).Complete();
        part.Stop();
        SimMsTotal += part.Elapsed.TotalMilliseconds;
        Steps++;
    }

    float FrameDt(int k)
    {
        float dt = k > 0 ? frameTime(k) - frameTime(k - 1) : 1f / 30f;
        return Mathf.Clamp(float.IsFinite(dt) ? dt : 1f / 30f, 1f / 240f, 0.1f);
    }

    void LateUpdate()
    {
        // a copy of this component (Instantiate of the avatar) or a half-built one has no data: draw nothing
        if (avatar == null || avatar.Motion == null || !pos.IsCreated || !cardVertices.IsCreated) return;
        int f = avatar.CurrentFrame;
        if (UseBake && f >= 0)
        {
            if (!BakeStarted) StartBake();
            PumpBake(false);
        }

        if (f >= 0 && (f != simFrame || meshDirty)) Advance(f);
        Refresh();
    }

    /// <summary>opacity (override or the avatar's + 0.15 ramped), tip glow fade, visibility, materials and LOD now
    /// (LateUpdate; the CLI calls it before an off-screen render)</summary>
    public void Refresh()
    {
        if (avatar == null) return;
        Opacity = OpacityOverride >= 0f ? Mathf.Clamp01(OpacityOverride) : HairOpacityFor(avatar.Opacity);
        GlowFade = OpacityOverride >= 0f ? 1f : Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(avatar.Opacity / GlowFadeRamp));
        bool show = Visible && avatar.Visible && Opacity > 0.005f && avatar.CurrentFrame >= 0;
        UpdateRenderers(show);
    }

    void UpdateRenderers(bool show)
    {
        bool cards = Mode == RenderMode.Cards;
        if (cards && appliedMask != LayerMask) BuildCardIndices();
        if (cardRenderer != null) cardRenderer.enabled = show && cards && meshValid;
        if (glowRenderer != null) glowRenderer.enabled = show && cards && meshValid && TipGlow * GlowFade > 0.001f;
        if (ribbonRenderer != null) ribbonRenderer.enabled = show && !cards && meshValid;
        ApplyLook();
        if (show && cards) UpdateLod();
    }

    /// <summary>LOD from her head's height on screen (pixels of the main camera; hysteresis 10 %)</summary>
    void UpdateLod()
    {
        int want = LodOverride >= 0 ? Mathf.Clamp(LodOverride, 0, 2) : AutoLod();
        if (want == lod) return;
        lod = want;
        cardMesh.SetSubMesh(0, new SubMeshDescriptor(0, lodIndexCount[lod]), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        glowMesh.SetSubMesh(0, new SubMeshDescriptor(0, glowLodIndexCount[lod]), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
    }

    static Camera viewCamera;
    static int viewCameraFrame = -1000;
    static Camera[] cameraBuffer = new Camera[8];

    /// <summary>the camera that draws the desktop view (Camera.main, else the deepest camera rendering to the screen;
    /// the search result is kept for 30 frames, no per-frame allocation)</summary>
    public static Camera ViewCamera()
    {
        Camera cam = Camera.main;
        if (cam != null && cam.isActiveAndEnabled) return cam;
        if (viewCamera != null && viewCamera.isActiveAndEnabled && viewCamera.targetTexture == null && Time.frameCount - viewCameraFrame < 30) return viewCamera;
        int n = Camera.allCamerasCount;
        if (cameraBuffer.Length < n) cameraBuffer = new Camera[n * 2];
        n = Camera.GetAllCameras(cameraBuffer);
        Camera best = null;
        for (int i = 0; i < n; i++)
        {
            Camera c = cameraBuffer[i];
            if (c != null && c.targetTexture == null && c.isActiveAndEnabled && (best == null || c.depth > best.depth)) best = c;
            cameraBuffer[i] = null;
        }

        viewCamera = best;
        viewCameraFrame = Time.frameCount;
        return best;
    }

    public float HeadPixels()
    {
        Camera cam = ViewCamera();
        if (cam == null) return float.NaN;
        Vector3 hc = avatar.Bone(HeadJoint).TransformPoint(headCentreLocal);
        float dist = Mathf.Max(0.05f, Vector3.Dot(hc - cam.transform.position, cam.transform.forward));
        return 2f * headRadius / (dist * 2f * Mathf.Tan(0.5f * cam.fieldOfView * Mathf.Deg2Rad)) * cam.pixelHeight;
    }

    int AutoLod()
    {
        float px = HeadPixels();
        if (float.IsNaN(px)) return 0;
        int cur = Mathf.Max(lod, 0);
        float h0 = cur == 0 ? 0.9f : 1.1f, h1 = cur <= 1 ? 0.9f : 1.1f;
        // thresholds are Quest eye-buffer pixels; a desktop view is about half as many pixels per degree
        float k = Application.isMobilePlatform ? 1f : 0.5f;
        return px >= lod0Px * k * h0 ? 0 : px >= lod1Px * k * h1 ? 1 : 2;
    }

    /// <summary>bring the hair to frame f (called from LateUpdate; the CLI calls it to sync before a screenshot)</summary>
    public void Advance(int f)
    {
        watch.Restart();
        int steps0 = Steps;
        bool rehang = false;
        if (BakeHas(f))
        {
            BakeLoad(f);
            liveValid = false;
            LastUpdateBaked = true;
        }
        else
        {
            LastUpdateBaked = false;
            rehang = !liveValid || simFrame < 0 || f < simFrame || f - simFrame > MaxCatchUpFrames;
            int from;
            UpdateGuideInfo();
            if (rehang)
            {
                from = Mathf.Max(0, f - PreRollFrames);
                avatar.PoseBones(from);
                ReadKinematics(ref kinCur);
                Rehang(kinCur);
                Rehangs++;
                if (SettleSeconds > 0f)
                {
                    CopyKin(kinCur, ref kinPrev);
                    UpdateShapeStiffness();
                    Step(0f, settle: true);
                    Steps--; // not a capture-frame step
                    for (int i = 0; i < prev.Length; i++) prev[i] = pos[i]; // start the pre-roll at rest
                }
            }
            else
            {
                from = simFrame; // kinCur still holds the kinematics of simFrame
            }

            UpdateShapeStiffness();
            for (int k = from + 1; k <= f; k++)
            {
                CopyKin(kinCur, ref kinPrev);
                avatar.PoseBones(k);
                ReadKinematics(ref kinCur);
                Step(FrameDt(k));
            }

            avatar.PoseBones(f); // the shown frame (no-op pose when k ended at f)
            curHeadPos = kinCur.HeadPos;
            curHeadRot = kinCur.HeadRot;
            liveValid = true;
        }

        simFrame = f;
        meshDirty = false;
        WriteMesh();
        watch.Stop();
        LastUpdateMs = watch.Elapsed.TotalMilliseconds;
        int n = Steps - steps0;
        if (n > 0 && !rehang)
        {
            double per = LastUpdateMs / n;
            StepMsAvg = StepMsAvg <= 0 ? per : StepMsAvg * 0.95 + per * 0.05;
            StepMsMax = Math.Max(StepMsMax, per);
        }

        RenderFrameMsTotal += LastUpdateMs;
    }

    void Update()
    {
        if (avatar != null && avatar.CurrentFrame >= 0) RenderFrames++;
    }

    void WriteMesh()
    {
        if (Mode == RenderMode.Ribbons)
        {
            if (ribbonMesh == null) BuildRibbons();
            WriteRibbons();
            return;
        }

        float3 hc = curHeadPos + math.mul(curHeadRot, (float3)headCentreLocal);
        float3 down = (float3)avatar.transform.InverseTransformDirection(Vector3.down);
        CardMeshJob cj = new()
        {
            pos = pos, cards = cardDescs, capPos = capPos, capNormal = capNormal, capTangent = capTangent,
            vertices = cardVertices, chunkMin = chunkMin, chunkMax = chunkMax, points = points, rows = rows,
            cardCount = cardCount, headCentre = hc, down = math.normalize(down), headPos = curHeadPos, headRot = curHeadRot,
            axisLength = 0.5f * LengthScale
        };
        GlowRibbonJob gj = new()
        {
            cardVertices = cardVertices, glows = glowDescs, vertices = glowVertices, rows = rows, points = GlowPoints, endCap = 1,
            window = glowWindow, span = glowSpan, widthRoot = glowWidthRoot, widthTip = glowWidthTip, endFraction = glowEnd
        };
        part.Restart();
        JobHandle h = cj.Schedule(cardCount + capChunks, 8);
        gj.Schedule(glowDescs.Length, 16, h).Complete();
        part.Stop();
        MeshMsTotal += part.Elapsed.TotalMilliseconds;
        float3 mn = new(float.MaxValue), mx = new(float.MinValue);
        for (int c = 0; c < chunkMin.Length; c++)
        {
            if (c >= cardCount && capVertexCount == 0) break;
            mn = math.min(mn, chunkMin[c]);
            mx = math.max(mx, chunkMax[c]);
        }

        if (!math.all(math.isfinite(mn)) || !math.all(math.isfinite(mx)))
        {
            meshValid = false;
            return;
        }

        const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
        cardMesh.SetVertexBufferData(cardVertices, 0, 0, cardVertices.Length, 0, flags);
        glowMesh.SetVertexBufferData(glowVertices, 0, 0, glowVertices.Length, 0, flags);
        Bounds b = new();
        b.SetMinMax((Vector3)mn - Vector3.one * 0.03f, (Vector3)mx + Vector3.one * 0.03f);
        cardMesh.bounds = b;
        glowMesh.bounds = b;
        meshValid = true;
    }

    // ------------------------------------------------------------------------------------------------ legacy ribbons

    /// <summary>switch the renderer (cards = the game-style hair; ribbons = the previous view-facing ribbons, A/B)</summary>
    public void SetMode(RenderMode mode)
    {
        Mode = mode;
        if (mode == RenderMode.Ribbons && ribbonMesh == null) BuildRibbons();
        meshDirty = true;
        if (avatar != null && avatar.CurrentFrame >= 0) Advance(avatar.CurrentFrame);
    }

    void BuildRibbons()
    {
        childOffset = new NativeArray<float2>(ribbons, Allocator.Persistent);
        wavePhase = new NativeArray<float2>(ribbons, Allocator.Persistent);
        ribbonVertices = new NativeArray<RibbonVertex>(ribbons * points * 2, Allocator.Persistent);
        ribbonMin = new NativeArray<float3>(ribbons, Allocator.Persistent);
        ribbonMax = new NativeArray<float3>(ribbons, Allocator.Persistent);
        System.Random rng = new(7);
        for (int k = 0; k < guides; k++)
        {
            for (int c = 0; c < ribbonsPerGuide; c++)
                wavePhase[k * ribbonsPerGuide + c] = new float2((float)(rng.NextDouble() * 2 * Math.PI), (float)(rng.NextDouble() * 2 * Math.PI));
            for (int c = 1; c < ribbonsPerGuide; c++)
            {
                double ang = 2 * Math.PI * (c - 1) / (ribbonsPerGuide - 1) + rng.NextDouble() * 1.2;
                float rad = 0.6f + 0.4f * (float)rng.NextDouble();
                childOffset[k * ribbonsPerGuide + c] = new float2(0.25f * rad * (float)Math.Cos(ang), 0.5f * rad * (float)Math.Sin(ang));
            }
        }

        int vcount = ribbons * points * 2;
        ribbonMesh = new Mesh { name = "Hair ribbons (legacy)", indexFormat = vcount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        ribbonMesh.MarkDynamic();
        ribbonMesh.SetVertexBufferParams(vcount,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float16, 4, 0),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, 1));
        System.Random rng2 = new(11);
        using (NativeArray<float4> uv = new(vcount, Allocator.Temp))
        {
            NativeArray<float4> u = uv;
            for (int r = 0; r < ribbons; r++)
            {
                float rnd = (float)rng2.NextDouble();
                float width = r % ribbonsPerGuide == 0 ? 1f : 0.8f;
                for (int i = 0; i < points; i++)
                {
                    float s = (float)i / (points - 1);
                    int v = (r * points + i) * 2;
                    u[v] = new float4(0, s, rnd, width);
                    u[v + 1] = new float4(1, s, rnd, width);
                }
            }

            ribbonMesh.SetVertexBufferData(u, 0, 0, vcount, 1, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        }

        int[] idx = new int[ribbons * (points - 1) * 6];
        int w = 0;
        for (int r = 0; r < ribbons; r++)
        for (int i = 0; i < points - 1; i++)
        {
            int v0 = (r * points + i) * 2;
            idx[w++] = v0;
            idx[w++] = v0 + 1;
            idx[w++] = v0 + 3;
            idx[w++] = v0;
            idx[w++] = v0 + 3;
            idx[w++] = v0 + 2;
        }

        SetIndices(ribbonMesh, idx);
        GameObject go = new("Ribbons (legacy)") { layer = gameObject.layer };
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = ribbonMesh;
        ribbonRenderer = go.AddComponent<MeshRenderer>();
        ribbonRenderer.shadowCastingMode = ShadowCastingMode.Off;
        ribbonRenderer.receiveShadows = false;
        ribbonRenderer.enabled = false;
        Shader shader = Resources.Load<Shader>("HM_HairStrands");
        if (shader == null) shader = Shader.Find("HeadMovement/HairStrands");
        ribbonMaterial = new Material(shader) { name = "Hair ribbons (legacy)" };
        Vector4 albedo = colourMaterial.GetVector("_BaseColor") / 1.85f;
        ribbonMaterial.SetVector("_RootColor", albedo);
        ribbonMaterial.SetVector("_MidColor", albedo);
        ribbonMaterial.SetVector("_TipColor", albedo);
        ribbonMaterial.SetVector("_HighlightColor", colourMaterial.GetVector("_HighlightColor"));
        ribbonMaterial.SetFloat("_VarLo", 0.88f);
        ribbonMaterial.SetFloat("_VarHi", 1.12f);
        JObject strands = (JObject)groom["strands"];
        ribbonMaterial.SetFloat("_WidthRoot", strands.Value<float>("width_root_m") * LengthScale);
        ribbonMaterial.SetFloat("_WidthTip", strands.Value<float>("width_tip_m") * LengthScale);
        if (groom["render"]?["tip_emission"]?["e"] is JArray e && e.Count == 6)
        {
            ribbonMaterial.SetVector("_Emit0123", new Vector4(e[0].Value<float>(), e[1].Value<float>(), e[2].Value<float>(), e[3].Value<float>()));
            ribbonMaterial.SetVector("_Emit45", new Vector4(e[4].Value<float>(), e[5].Value<float>(), 0, 0));
        }

        ribbonRenderer.sharedMaterial = ribbonMaterial;
    }

    void WriteRibbons()
    {
        Vector3 hc = (Vector3)curHeadPos + (Quaternion)curHeadRot * headCentreLocal;
        RibbonMeshJob job = new()
        {
            pos = pos, childOffset = childOffset, wavePhase = wavePhase, segLen = segLen, vertices = ribbonVertices,
            ribbonMin = ribbonMin, ribbonMax = ribbonMax, points = points, ribbonsPerGuide = ribbonsPerGuide,
            spread = spread * 0.5f, clump = clump, volume = Volume, waveAmp = WaveAmplitude * LengthScale,
            waveLength = WaveLength * LengthScale, headCentre = hc
        };
        part.Restart();
        job.Schedule(ribbons, 16).Complete();
        part.Stop();
        MeshMsTotal += part.Elapsed.TotalMilliseconds;
        float3 mn = new(float.MaxValue), mx = new(float.MinValue);
        for (int r = 0; r < ribbons; r++)
        {
            mn = math.min(mn, ribbonMin[r]);
            mx = math.max(mx, ribbonMax[r]);
        }

        if (!math.all(math.isfinite(mn)) || !math.all(math.isfinite(mx)))
        {
            meshValid = false;
            return;
        }

        ribbonMesh.SetVertexBufferData(ribbonVertices, 0, 0, ribbonVertices.Length, 0,
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
        Bounds b = new();
        b.SetMinMax((Vector3)mn - Vector3.one * 0.05f, (Vector3)mx + Vector3.one * 0.05f);
        ribbonMesh.bounds = b;
        meshValid = true;
    }

    // ------------------------------------------------------------------------------------------------ control

    /// <summary>dynamics changed (hm_hair set): re-bake in the background, re-hang the live hair at the shown frame</summary>
    public void ResetSimulation()
    {
        StopBake();
        liveValid = false;
        simFrame = -1;
        if (avatar != null && avatar.CurrentFrame >= 0)
        {
            if (UseBake) StartBake();
            Advance(avatar.CurrentFrame);
        }
    }

    public void ResetStats()
    {
        StepMsAvg = 0;
        StepMsMax = 0;
        RenderFrameMsTotal = 0;
        RenderFrames = 0;
    }

    /// <summary>guide points (index &gt;= 1) inside the face box (no margin) at a head pose</summary>
    int FaceIntrusions(float3 hp, quaternion hr)
    {
        quaternion inv = math.conjugate(hr);
        int n = 0;
        for (int g = 0; g < guides; g++)
        {
            int o = g * points;
            for (int i = 1; i < points; i++)
                if (faceBox.Inside(math.mul(inv, pos[o + i] - hp))) n++;
        }

        return n;
    }

    /// <summary>card vertices (rendered geometry, cap excluded) inside the face box</summary>
    int FaceIntrusionsCards(float3 hp, quaternion hr)
    {
        if (!cardVertices.IsCreated || Mode != RenderMode.Cards) return 0;
        quaternion inv = math.conjugate(hr);
        int n = 0, end = cardCount * rows * 3;
        for (int v = 0; v < end; v++)
            if (faceBox.Inside(math.mul(inv, cardVertices[v].position - hp))) n++;
        return n;
    }

    /// <summary>
    /// Right-front guides (right of the part, |az| &lt; 120: they hang in front of / over the right shoulder): the straightness of their lower
    /// halves, the worst guide's |p_last - p_mid| / arc length (1 = straight, a hook under the chin ~0.6), the most
    /// upward tip segment (dot with the avatar's up: -1 hanging, &gt; 0 a hook turning up) and the sharpest bend
    /// between two segments of a lower half (deg; the draped rest shape bends up to ~35 deg over the shoulder, a hook
    /// at the tip 45+)
    /// </summary>
    const float FrontRightMetricAbsAz = 120f;

    float FrontRightStraightness(out float tipUpMax, out float bendMax)
    {
        float worst = 1f;
        tipUpMax = -1f;
        bendMax = 0f;
        if (guideAz == null) return worst;
        int mid = points / 2, last = points - 1;
        for (int g = 0; g < guides; g++)
        {
            // a fixed set (not the dynamics zone): every guide right of the part with |az| < MetricAbsAz
            if (guideSide[g] >= 0 || Mathf.Abs(guideAz[g]) >= FrontRightMetricAbsAz) continue;
            int o = g * points;
            float arc = 0f;
            for (int i = mid + 1; i <= last; i++)
            {
                arc += math.distance(pos[o + i], pos[o + i - 1]);
                if (i < last)
                {
                    float3 a = math.normalizesafe(pos[o + i] - pos[o + i - 1]), b = math.normalizesafe(pos[o + i + 1] - pos[o + i]);
                    bendMax = Mathf.Max(bendMax, math.degrees(math.acos(math.clamp(math.dot(a, b), -1f, 1f))));
                }
            }

            if (arc > 1e-5f) worst = Mathf.Min(worst, math.distance(pos[o + last], pos[o + mid]) / arc);
            float3 t = pos[o + last] - pos[o + last - 2];
            float tl = math.length(t);
            if (tl > 1e-6f) tipUpMax = Mathf.Max(tipUpMax, t.y / tl);
        }

        return worst;
    }

    /// <summary>playtest / review numbers in avatar-local metres</summary>
    public Dictionary<string, object> State()
    {
        float maxStretch = 0f, maxReach = 0f, maxReachRatio = 0f, minY = float.MaxValue;
        int nonFinite = 0, tipsBelowHead = 0, resetCount = 0;
        float3 head = curHeadPos;
        quaternion hrot = curHeadRot;
        float maxRootErr = 0f;
        for (int g = 0; g < guides; g++)
        {
            int o = g * points;
            resetCount += resets[g];
            float L = segLen[g];
            float3 expectRoot = head + math.mul(hrot, restLocal[o]);
            maxRootErr = Mathf.Max(maxRootErr, math.distance(expectRoot, pos[o]));
            for (int i = 0; i < points; i++)
            {
                float3 p = pos[o + i];
                if (!math.all(math.isfinite(p)))
                {
                    nonFinite++;
                    continue;
                }

                if (i > 0) maxStretch = Mathf.Max(maxStretch, math.distance(p, pos[o + i - 1]) / L);
                float reach = math.distance(p, head);
                maxReach = Mathf.Max(maxReach, reach);
                maxReachRatio = Mathf.Max(maxReachRatio, reach / (L * (points - 1) + 0.25f));
                minY = Mathf.Min(minY, avatar.transform.TransformPoint(p).y);
            }

            if (pos[o + points - 1].y < head.y) tipsBelowHead++;
        }

        // silhouette at the shoulders: lateral extent (p3..p97) of the hair 0.25-0.35 m below the crown, head frame
        float3 up = math.mul(hrot, new float3(0, 1, 0));
        float3 right = math.mul(hrot, new float3(1, 0, 0));
        float3 crown = head + math.mul(hrot, (float3)headCentreLocal) + up * headRadius;
        List<float> lateral = new(), drops = new();
        for (int g = 0; g < guides; g++)
        {
            int o = g * points;
            for (int i = 0; i < points; i++)
            {
                float d = math.dot(crown - pos[o + i], up);
                if (d > 0.25f && d < 0.35f) lateral.Add(math.dot(pos[o + i] - crown, right));
            }

            drops.Add(math.dot(crown - pos[o + points - 1], up));
        }

        lateral.Sort();
        drops.Sort();
        // the strands furthest from their rest shape (tip vs its head-relative rest target): outlier diagnostics
        List<(float d, int g)> dev = new();
        for (int g = 0; g < guides; g++)
        {
            int o = g * points + points - 1;
            float3 target = head + math.mul(hrot, restLocal[o]);
            dev.Add((math.distance(target, pos[o]), g));
        }

        dev.Sort((a, b) => b.d.CompareTo(a.d));
        List<object> outliers = new();
        for (int j = 0; j < Mathf.Min(5, dev.Count); j++)
        {
            outliers.Add(new Dictionary<string, object>
            {
                ["guide"] = dev[j].g, ["tipFromRestM"] = dev[j].d, ["az"] = guideAz[dev[j].g], ["el"] = guideEl[dev[j].g],
                ["len"] = guideLength[dev[j].g]
            });
        }

        float widthShoulder = lateral.Count > 10 ? lateral[(int)(lateral.Count * 0.97f)] - lateral[(int)(lateral.Count * 0.03f)] : float.NaN;
        float tipDropMax = drops.Count > 0 ? drops[(int)(drops.Count * 0.95f)] : float.NaN;
        Vector3 headWorld = avatar.transform.TransformPoint(head);
        Bounds hb = HairBounds;
        int[] tris = { lodIndexCount[0] / 3 + glowLodIndexCount[0] / 3, lodIndexCount[1] / 3 + glowLodIndexCount[1] / 3, lodIndexCount[2] / 3 + glowLodIndexCount[2] / 3 };
        Dictionary<string, int> layerCards = new();
        if (layout != null)
            foreach (HairCardLayout.Card c in layout.Cards)
            {
                string k = HairCardLayout.LayerNames[c.Layer];
                layerCards[k] = layerCards.TryGetValue(k, out int x) ? x + 1 : 1;
            }

        return new Dictionary<string, object>
        {
            ["present"] = true, ["visible"] = RendererVisible, ["glowVisible"] = GlowVisible, ["mode"] = Mode.ToString().ToLowerInvariant(),
            ["groomVersion"] = GroomVersion, ["guides"] = guides, ["segments"] = points - 1, ["cards"] = cardCount,
            ["cardsPerLayer"] = layerCards, ["capTriangles"] = capTriangles.Length / 3, ["glowRibbons"] = Glows,
            ["ribbons"] = ribbons, ["vertices"] = VertexCount, ["triangles"] = TriangleCount, ["trianglesPerLod"] = tris,
            ["lod"] = Lod, ["lodOverride"] = LodOverride, ["headPixels"] = HeadPixels(), ["layerMask"] = LayerMask,
            ["frame"] = simFrame, ["avatarFrame"] = avatar.CurrentFrame, ["steps"] = Steps, ["rehangs"] = Rehangs,
            ["strandResets"] = resetCount, ["nonFinite"] = nonFinite, ["maxStretch"] = maxStretch,
            ["maxReachM"] = maxReach, ["maxReachRatio"] = maxReachRatio, ["minWorldY"] = minY,
            ["tipsBelowHeadFraction"] = guides > 0 ? (float)tipsBelowHead / guides : 0f,
            ["rootPinErrorMm"] = maxRootErr * 1000f, ["rootMismatchMm"] = RootMismatchMm, ["lengthScale"] = LengthScale,
            ["faceIntrusions"] = FaceIntrusions(head, hrot), ["faceIntrusionsCards"] = FaceIntrusionsCards(head, hrot),
            ["faceGuard"] = FaceGuard, ["armCollisions"] = ArmCollisions, ["rest"] = restStats,
            ["substeps"] = Substeps, ["iterations"] = Iterations, ["damping"] = Damping, ["bendCompliance"] = BendCompliance,
            ["shapeRoot"] = ShapeRoot, ["shapeFalloff"] = ShapeFalloff, ["tipGlow"] = TipGlow,
            ["opacity"] = Opacity, ["opacitySource"] = OpacitySource, ["opacityOverride"] = OpacityOverride, ["glowFade"] = GlowFade,
            ["glowAnchored"] = GlowsAnchored, ["cardsRootDropped"] = CardsDropped, ["frontRightGuides"] = dynZone?.Count(x => x == 1) ?? 0,
            ["sideZoneGuides"] = dynZone?.Count(x => x == 2) ?? 0, ["sideAbsAz"] = SideAbsAz, ["sideShapeRoot"] = SideShapeRoot,
            ["sideFalloff"] = SideFalloff, ["maxDeviationSide"] = MaxDeviationSide,
            ["frontRightDrapeGuides"] = frontRight?.Count(x => x) ?? 0, ["frontRightAbsAz"] = FrontRightAbsAz,
            ["frontRightShapeRoot"] = FrontRightShapeRoot, ["frontRightFalloff"] = FrontRightFalloff, ["maxDeviationFrontRight"] = MaxDeviationFrontRight,
            ["frontRightStraightness"] = FrontRightStraightness(out float tipUp, out float bend), ["frontRightTipUpMax"] = tipUp, ["frontRightBendMaxDeg"] = bend,
            ["avatarOpacity"] = avatar.Opacity, ["colourSource"] = colourSource,
            ["alphaToMask"] = depthMaterial != null && depthMaterial.GetFloat("_AlphaToMask") > 0.5f,
            ["stepMsAvg"] = StepMsAvg, ["stepMsMax"] = StepMsMax, ["lastUpdateMs"] = LastUpdateMs, ["lastUpdateBaked"] = LastUpdateBaked,
            ["renderFrames"] = RenderFrames, ["msPerRenderFrame"] = RenderFrames > 0 ? RenderFrameMsTotal / RenderFrames : 0.0,
            ["bake"] = BakeState(), ["buildMs"] = BuildMs, ["atlasMs"] = HairAtlas.LastGenerateMs, ["atlasBytes"] = HairAtlas.Bytes,
            ["boundsCenter"] = new[] { hb.center.x, hb.center.y, hb.center.z },
            ["boundsSize"] = new[] { hb.size.x, hb.size.y, hb.size.z },
            ["head"] = new[] { headWorld.x, headWorld.y, headWorld.z }, ["warning"] = Warning,
            ["guideWidthShoulderM"] = widthShoulder, ["tipDropBelowCrownP95M"] = tipDropMax,
            ["tipFromRestMedianM"] = dev.Count > 0 ? dev[dev.Count / 2].d : float.NaN, ["outliers"] = outliers
        };
    }

    /// <summary>
    /// Plays the whole capture through the hair, frame by frame (the avatar is driven directly and restored to its
    /// frame afterwards): CPU cost per frame update (baked: a lookup + the card mesh; live: simulation steps),
    /// stability, face clearance (guide points and card vertices in the face box, every frame) and the motion numbers
    /// of the hair reference (dancecap docs/HAIR_REFERENCE.md 3): spread radius r95 of the hair about the neck's
    /// vertical axis on calm and turn frames (|head yaw rate| below calmDps / above turnDps) and the trailing angle of
    /// the hair mass behind the head in turns (positive = trailing the turn).
    /// </summary>
    public Dictionary<string, object> Sweep(bool live = false, float calmDps = 90f, float turnDps = 180f)
    {
        int keep = avatar.CurrentFrame;
        int n = avatar.FrameCount;
        bool useBake = UseBake;
        double bakeWaitMs = 0;
        if (live) UseBake = false;
        else if (UseBake)
        {
            Stopwatch bw = Stopwatch.StartNew();
            if (!BakeStarted) StartBake();
            PumpBake(true);
            bakeWaitMs = bw.Elapsed.TotalMilliseconds;
        }

        double sim0 = SimMsTotal, mesh0 = MeshMsTotal;
        int steps0 = Steps;
        float[] spreadR = new float[n], massAz = new float[n], yawRate = new float[n], frStraight = new float[n], frTipUp = new float[n], frBend = new float[n];
        Vector3[] facing = new Vector3[n];
        List<double> ms = new();
        float maxStretch = 0f;
        int stretchFrame = -1, stretchGuide = -1, stretchPoint = -1;
        int nonFinite = 0, resets0 = 0, faceMax = 0, faceTotal = 0, faceFrames = 0, cardFaceMax = 0, cardFaceFrames = 0;
        for (int g = 0; g < guides; g++) resets0 += resets[g];
        Transform root = avatar.transform;
        simFrame = -1;
        liveValid = false;
        List<float> r = new();
        for (int k = 0; k < n; k++)
        {
            avatar.SetFrame(k);
            Advance(k);
            if (k > 0) ms.Add(LastUpdateMs);
            int fi = FaceIntrusions(curHeadPos, curHeadRot), fc = FaceIntrusionsCards(curHeadPos, curHeadRot);
            faceMax = Math.Max(faceMax, fi);
            faceTotal += fi;
            if (fi > 0) faceFrames++;
            cardFaceMax = Math.Max(cardFaceMax, fc);
            if (fc > 0) cardFaceFrames++;
            frStraight[k] = FrontRightStraightness(out frTipUp[k], out frBend[k]);
            Vector3 neck = root.InverseTransformPoint(avatar.Bone(12).position);
            Vector3 f = root.InverseTransformDirection(avatar.Bone(HeadJoint).rotation * new Vector3(0, 0, -1));
            f.y = 0;
            facing[k] = f.sqrMagnitude > 1e-8f ? f.normalized : Vector3.forward;
            r.Clear();
            Vector3 mass = Vector3.zero;
            for (int g = 0; g < guides; g++)
            {
                int o = g * points;
                for (int i = 1; i < points; i++)
                {
                    float3 p = pos[o + i];
                    if (!math.all(math.isfinite(p)))
                    {
                        nonFinite++;
                        continue;
                    }

                    float st = math.distance(p, pos[o + i - 1]) / segLen[g];
                    if (st > maxStretch) (maxStretch, stretchFrame, stretchGuide, stretchPoint) = (st, k, g, i);
                    Vector3 h = (Vector3)p - neck;
                    h.y = 0;
                    r.Add(h.magnitude);
                    if (i >= points / 2) mass += h;
                }
            }

            r.Sort();
            spreadR[k] = r.Count > 0 ? r[(int)(0.95f * (r.Count - 1))] : float.NaN;
            massAz[k] = Vector3.SignedAngle(-facing[k], mass, Vector3.up);
        }

        for (int k = 0; k < n; k++)
        {
            int a = Mathf.Max(k - 1, 0), b = Mathf.Min(k + 1, n - 1);
            float dt = frameTime(b) - frameTime(a);
            yawRate[k] = b > a && dt > 1e-4f ? Vector3.SignedAngle(facing[a], facing[b], Vector3.up) / dt : 0f;
        }

        List<float> calm = new(), turn = new(), trail = new();
        for (int k = 1; k < n; k++)
        {
            float w = Mathf.Abs(yawRate[k]);
            if (w < calmDps) calm.Add(spreadR[k]);
            if (w > turnDps)
            {
                turn.Add(spreadR[k]);
                trail.Add(-massAz[k] * Mathf.Sign(yawRate[k]));
            }
        }

        int resets1 = 0;
        for (int g = 0; g < guides; g++) resets1 += resets[g];
        UseBake = useBake;
        avatar.SetFrame(keep);
        simFrame = -1;
        liveValid = false;
        Advance(keep);
        ms.Sort();
        return new Dictionary<string, object>
        {
            ["frames"] = n, ["live"] = live, ["baked"] = !live && useBake, ["calmFrames"] = calm.Count, ["turnFrames"] = turn.Count,
            ["spreadR95CalmMedianM"] = Q(calm, 0.5f), ["spreadR95TurnMedianM"] = Q(turn, 0.5f),
            ["spreadR95TurnP90M"] = Q(turn, 0.9f), ["trailingDegTurnMedian"] = Q(trail, 0.5f),
            ["trailingDegTurnP25"] = Q(trail, 0.25f), ["trailingDegTurnP75"] = Q(trail, 0.75f),
            ["maxStretch"] = maxStretch, ["nonFinite"] = nonFinite, ["strandResets"] = resets1 - resets0,
            ["maxStretchAt"] = new Dictionary<string, object>
            {
                ["frame"] = stretchFrame, ["guide"] = stretchGuide, ["point"] = stretchPoint,
                ["az"] = stretchGuide >= 0 ? guideAz[stretchGuide] : float.NaN, ["el"] = stretchGuide >= 0 ? guideEl[stretchGuide] : float.NaN
            },
            ["faceIntrusionsMax"] = faceMax, ["faceIntrusionsTotal"] = faceTotal, ["faceIntrusionFrames"] = faceFrames,
            ["faceIntrusionsCardsMax"] = cardFaceMax, ["faceIntrusionCardFrames"] = cardFaceFrames,
            ["frontRightStraightMin"] = frStraight.Length > 0 ? frStraight.Min() : float.NaN,
            ["frontRightStraightMinFrame"] = frStraight.Length > 0 ? Array.IndexOf(frStraight, frStraight.Min()) : -1,
            ["frontRightStraightP10"] = Q(frStraight.ToList(), 0.1f), ["frontRightHookFrames"] = frStraight.Count(x => x < 0.8f),
            ["frontRightTipUpMax"] = frTipUp.Length > 0 ? frTipUp.Max() : float.NaN, ["frontRightTipUpFrames"] = frTipUp.Count(x => x > -0.2f),
            ["frontRightBendMaxDeg"] = frBend.Length > 0 ? frBend.Max() : float.NaN, ["frontRightBendMaxFrame"] = frBend.Length > 0 ? Array.IndexOf(frBend, frBend.Max()) : -1,
            ["frontRightBendP90Deg"] = Q(frBend.ToList(), 0.9f), ["frontRightSharpBendFrames"] = frBend.Count(x => x > 45f),
            ["stepMsMean"] = ms.Count > 0 ? ms.Average() : 0.0, ["stepMsP95"] = ms.Count > 0 ? ms[(int)(0.95 * (ms.Count - 1))] : 0.0,
            ["stepMsMax"] = ms.Count > 0 ? ms[^1] : 0.0, ["simJobMsMean"] = (SimMsTotal - sim0) / Mathf.Max(1, Steps - steps0),
            ["meshJobMsMean"] = (MeshMsTotal - mesh0) / Mathf.Max(1, n), ["burst"] = Unity.Burst.BurstCompiler.IsEnabled,
            ["substeps"] = Substeps, ["iterations"] = Iterations, ["bakeWaitMs"] = bakeWaitMs, ["bake"] = BakeState(),
            ["particles"] = guides * points, ["vertices"] = VertexCount, ["triangles"] = TriangleCount
        };
    }

    /// <summary>guides (rest shape in head-bone space, current points in avatar space), head pose, cards and the face
    /// box as JSON (offline review plots of the groom)</summary>
    public string Dump()
    {
        JObject o = new()
        {
            ["points"] = points, ["guides"] = guides, ["partX"] = partX, ["headCentre"] = new JArray(headCentreLocal.x, headCentreLocal.y, headCentreLocal.z),
            ["headRadius"] = headRadius, ["headPos"] = new JArray(curHeadPos.x, curHeadPos.y, curHeadPos.z),
            ["headRot"] = new JArray(curHeadRot.value.x, curHeadRot.value.y, curHeadRot.value.z, curHeadRot.value.w),
            ["faceBox"] = new JObject { ["fwdMin"] = faceBox.fwdMin, ["yLo"] = faceBox.yLo, ["yHi"] = faceBox.yHi, ["halfWidth"] = faceBox.halfWidth, ["margin"] = faceBox.margin },
            ["az"] = new JArray(guideAz), ["el"] = new JArray(guideEl), ["side"] = new JArray(guideSide), ["length"] = new JArray(guideLength)
        };
        JArray rest = new(), cur = new();
        for (int i = 0; i < guides * points; i++)
        {
            rest.Add(new JArray(restLocal[i].x, restLocal[i].y, restLocal[i].z));
            cur.Add(new JArray(pos[i].x, pos[i].y, pos[i].z));
        }

        o["rest"] = rest;
        o["pos"] = cur;
        JArray cards = new();
        if (layout != null)
            foreach (HairCardLayout.Card c in layout.Cards)
                cards.Add(new JObject
                {
                    ["layer"] = c.Layer, ["g"] = new JArray(c.Desc.guide.x, c.Desc.guide.y, c.Desc.guide.z),
                    ["w"] = new JArray(c.Desc.weight.x, c.Desc.weight.y, c.Desc.weight.z), ["sEnd"] = c.Desc.sEnd
                });
        o["cards"] = cards;
        JArray verts = new();
        if (cardVertices.IsCreated)
            for (int v = 0; v < cardCount * rows * 3; v++) verts.Add(new JArray(cardVertices[v].position.x, cardVertices[v].position.y, cardVertices[v].position.z));
        o["cardVertices"] = verts;
        o["rows"] = rows;
        return o.ToString(Newtonsoft.Json.Formatting.None);
    }

    static float Q(List<float> v, float q)
    {
        if (v.Count == 0) return float.NaN;
        List<float> s = new(v);
        s.Sort();
        return s[Mathf.Clamp((int)(q * (s.Count - 1)), 0, s.Count - 1)];
    }

    void OnDestroy()
    {
        if (Active == this) Active = null;
        StopBake();
        foreach (IDisposable d in new IDisposable[]
                 {
                     pos, prev, restLocal, segLen, bendRest, shapeK, guideInfo, resets, capsules, cardDescs, cardVertices, chunkMin,
                     chunkMax, capPos, capNormal, capTangent, glowDescs, glowVertices, childOffset, wavePhase, ribbonVertices,
                     ribbonMin, ribbonMax
                 })
        {
            try
            {
                d.Dispose();
            }
            catch (Exception)
            {
                // not allocated (default NativeArray)
            }
        }

        foreach (UnityEngine.Object o in new UnityEngine.Object[] { cardMesh, glowMesh, ribbonMesh, depthMaterial, colourMaterial, glowMaterial, ribbonMaterial })
            if (o != null) Destroy(o);
    }
}
