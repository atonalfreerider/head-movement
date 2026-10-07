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
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>
/// The follow's hair on her skinned SMPL-X avatar: guide strands from the capture's hair_groom.json (dancecap
/// hair_groom.py: roots on the SMPL-X scalp, layered lengths, colour, collision capsules), simulated with Burst
/// jobs (HairJobs.StrandSimJob: Verlet + XPBD shape / bending / stretch, capsule collisions, substeps) and drawn as
/// ONE dynamic ribbon mesh (every guide + its children, view-facing ribbons expanded in the vertex shader) with
/// Resources/HM_HairStrands (Kajiya-Kay, alpha-to-coverage, the original head-movement tip bloom).
///
/// Time base: the hair advances with the CAPTURE frames the avatar shows, not with Time.deltaTime. Each new frame is
/// one step of the real frame interval (times.json), the head pose and capsules interpolated across the substeps, so
/// slow-motion lessons slow the hair down with the dance and a paused take shows frozen hair. A seek, a loop back or a
/// jump of more than MaxCatchUpFrames re-hangs the hair in its rest shape PreRollFrames earlier and simulates up to
/// the shown frame (deterministic: the same frame always shows the same hair).
/// </summary>
public class HairStrands : MonoBehaviour
{
    const int HeadJoint = 15;
    const int MaxCatchUpFrames = 8;

    public static HairStrands Active { get; private set; }

    [Header("Dynamics (from hair_groom.json; tweakable live)")]
    public int Substeps = 8;
    public int Iterations = 2;
    public float Damping = 1.6f;
    public float BendCompliance = 2e-5f;
    public float Friction = 0.3f;
    public float ParticleRadius = 0.008f;
    public float ShapeRoot = 0.35f;
    public float ShapeFalloff = 0.35f;
    public int PreRollFrames = 20;
    public float HeadStandoff = 0.6f; // fraction of the groom standoff kept on the head collider while dancing
    public float MaxStretch = 1.02f;
    public float MaxDeviation = 1.3f;   // x arc length from the rest target (see StrandSimJob.maxDeviation)
    public float SettleSeconds = 0.5f;  // static settle in the pose of a re-hang before the dynamic pre-roll

    [Header("Look")]
    public bool Visible = true;
    public float Volume = 1.2f;
    public float WaveAmplitude = 0.012f;
    public float WaveLength = 0.09f;
    public float TipGlow = 1f;
    public float Opacity = 1f;

    SmplxAvatar avatar;
    Func<int, float> frameTime;
    JObject groom;

    int guides, points, ribbonsPerGuide, ribbons;
    float spread, clump;
    Vector3 headCentreLocal; // head-bone space
    float headRadius, standoff, partX;

    struct ColliderDef
    {
        public string Name;
        public int A, B;
        public Vector3 Oa, Ob;
        public float R;
    }

    ColliderDef[] colliderDefs;

    struct Kin
    {
        public float3 HeadPos;
        public quaternion HeadRot;
        public float3[] A, B;
    }

    Kin kinPrev, kinCur;

    NativeArray<float3> pos, prev, restLocal;
    NativeArray<float> segLen, bendRest, shapeK;
    NativeArray<Capsule> capsules;
    NativeArray<int> resets;
    NativeArray<float2> childOffset, wavePhase;
    NativeArray<RibbonVertex> vertices;
    NativeArray<float3> ribbonMin, ribbonMax;
    float[] guideLength;

    Mesh mesh;
    Material material;
    MeshRenderer meshRenderer;

    int simFrame = -1;
    bool meshValid;

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
    public float RootMismatchMm { get; private set; }
    public float LengthScale { get; private set; } = 1f;
    public string Warning { get; private set; }

    public int Guides => guides;
    public int Points => points;
    public int Ribbons => ribbons;
    public int VertexCount => vertices.IsCreated ? vertices.Length : 0;
    public int TriangleCount => ribbons * (points - 1) * 2;
    public int SimFrame => simFrame;
    public SmplxAvatar Avatar => avatar;
    public bool RendererVisible => meshRenderer != null && meshRenderer.enabled;

    /// <param name="skinPath">the follow's smplx skin binary (the roots are placed on its rest vertices)</param>
    /// <param name="groomPath">hair_groom.json in the capture folder</param>
    /// <param name="frameTime">audio-clock seconds of a capture frame (CaptureTimeline.AudioTimeOf)</param>
    public static HairStrands Create(SmplxAvatar avatar, string skinPath, string groomPath, Func<int, float> frameTime)
    {
        JObject groom = JObject.Parse(File.ReadAllText(groomPath));
        if (groom.Value<int>("version") != 1) throw new InvalidDataException($"{groomPath}: unsupported version {groom["version"]}");
        SmplxData.Skin skin = SmplxData.ReadSkin(skinPath);
        GameObject go = new($"{avatar.DancerRole} Hair") { layer = avatar.gameObject.layer };
        go.transform.SetParent(avatar.transform, false);
        HairStrands hair = go.AddComponent<HairStrands>();
        hair.Build(avatar, skin, groom, frameTime);
        Active = hair;
        return hair;
    }

    static Vector3 V3(JToken t) => new(t[0].Value<float>(), t[1].Value<float>(), t[2].Value<float>());

    void Build(SmplxAvatar av, SmplxData.Skin skin, JObject g, Func<int, float> times)
    {
        avatar = av;
        frameTime = times;
        groom = g;

        JObject strands = (JObject)g["strands"];
        JArray guideList = (JArray)g["guides"];
        guides = guideList.Count;
        points = strands.Value<int>("segments") + 1;
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
        partX = shape.Value<float>("part_x_unity") * LengthScale;

        JObject dyn = (JObject)g["dynamics"];
        Substeps = Mathf.Clamp(dyn.Value<int>("substeps"), 1, 64);
        Iterations = dyn.Value<int>("iterations");
        Damping = dyn.Value<float>("damping_per_s");
        BendCompliance = dyn.Value<float>("bend_compliance");
        Friction = dyn.Value<float>("friction");
        ParticleRadius = dyn.Value<float>("particle_radius_m");
        ShapeRoot = dyn.Value<float>("shape_stiffness_root");
        ShapeFalloff = dyn.Value<float>("shape_falloff");

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
        }

        RootMismatchMm = mismatch * 1000f;
        if (RootMismatchMm > 5f) Warning = $"hair roots {RootMismatchMm:0.0} mm off the groom's scalp (different SMPL-X model or betas?)";

        JArray cols = (JArray)g["colliders"];
        colliderDefs = new ColliderDef[cols.Count];
        for (int c = 0; c < cols.Count; c++)
        {
            colliderDefs[c] = new ColliderDef
            {
                Name = cols[c].Value<string>("name"), A = cols[c].Value<int>("a"), B = cols[c].Value<int>("b"),
                Oa = V3(cols[c]["oa_unity"]) * LengthScale, Ob = V3(cols[c]["ob_unity"]) * LengthScale,
                R = cols[c].Value<float>("r") * LengthScale
            };
        }

        Allocate();
        for (int k = 0; k < guides; k++) segLen[k] = guideLength[k] / (points - 1);
        Settle(roots, normals, skin.RestJoints);
        BuildMesh();
        BuildMaterial();
        kinPrev = NewKin();
        kinCur = NewKin();
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
        shapeK = new NativeArray<float>(points, Allocator.Persistent);
        resets = new NativeArray<int>(guides, Allocator.Persistent);
        capsules = new NativeArray<Capsule>(colliderDefs.Length + 1, Allocator.Persistent);
        childOffset = new NativeArray<float2>(ribbons, Allocator.Persistent);
        wavePhase = new NativeArray<float2>(ribbons, Allocator.Persistent);
        vertices = new NativeArray<RibbonVertex>(ribbons * points * 2, Allocator.Persistent);
        ribbonMin = new NativeArray<float3>(ribbons, Allocator.Persistent);
        ribbonMax = new NativeArray<float3>(ribbons, Allocator.Persistent);
    }

    /// <summary>
    /// Rest shape: the strands combed away from a side part (front sections to the sides of the face, the crown
    /// backward), then settled under gravity on her REST (T-pose) body with the head sphere inflated by the groom's
    /// standoff (hair volume) and a face guard, in head-bone space. The settled shape is the shape-constraint target.
    /// </summary>
    void Settle(Vector3[] roots, Vector3[] normals, Vector3[] restJoints)
    {
        Vector3 headJ = restJoints[HeadJoint];
        Vector3 forward = new(0, 0, -1); // the avatar faces -z in Unity (+z of the right-handed world)
        System.Random rng = new(7);
        for (int k = 0; k < guides; k++)
        {
            Vector3 n = normals[k];
            Vector3 d = roots[k] - headCentreLocal;
            float az = Mathf.Abs(Mathf.Atan2(d.x, Vector3.Dot(d, forward)) * Mathf.Rad2Deg);
            float side = roots[k].x >= partX ? 1f : -1f;
            float wl = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(60f, 150f, az));
            float wd = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(90f, 170f, az));
            Vector3 comb = new Vector3(side, 0, 0) * wl + -forward * 0.5f + Vector3.down * Mathf.Lerp(0.2f, 1f, wd);
            comb -= n * Vector3.Dot(comb, n);
            comb = comb.sqrMagnitude > 1e-8f ? comb.normalized : Vector3.down;
            Vector3 d1 = (n * 0.55f + comb).normalized;
            float L = segLen[k];
            int o = k * points;
            Vector3 p = roots[k];
            pos[o] = p;
            p += d1 * L;
            pos[o + 1] = p;
            Vector3 dir = d1;
            for (int i = 2; i < points; i++)
            {
                dir = Vector3.Slerp(dir, Vector3.down, 0.35f);
                Vector3 outward = (p - headCentreLocal).normalized;
                p += (dir + outward * 0.15f).normalized * L;
                pos[o + i] = p;
            }

            for (int i = 0; i < points; i++)
            {
                restLocal[o + i] = pos[o + i];
                prev[o + i] = pos[o + i];
            }

            for (int c = 0; c < ribbonsPerGuide; c++)
            {
                wavePhase[k * ribbonsPerGuide + c] = new float2((float)(rng.NextDouble() * 2 * Math.PI), (float)(rng.NextDouble() * 2 * Math.PI));
            }

            for (int c = 1; c < ribbonsPerGuide; c++)
            {
                double ang = 2 * Math.PI * (c - 1) / (ribbonsPerGuide - 1) + rng.NextDouble() * 1.2;
                float rad = 0.6f + 0.4f * (float)rng.NextDouble();
                childOffset[k * ribbonsPerGuide + c] = new float2(0.5f * rad * (float)Math.Cos(ang), rad * (float)Math.Sin(ang));
            }
        }

        // settle colliders in head-bone space (rest pose: every bone has the identity rotation)
        List<Capsule> caps = new();
        foreach (ColliderDef c in colliderDefs)
        {
            if (c.Name.Contains("arm")) continue; // T-pose arms stick out sideways: not where the hair hangs while dancing
            float3 a = restJoints[c.A] - headJ + c.Oa, b = restJoints[c.B] - headJ + c.Ob;
            float r = c.Name == "head" ? c.R + standoff : c.R + 0.5f * standoff;
            caps.Add(new Capsule { a0 = a, b0 = b, a1 = a, b1 = b, r = r });
        }

        float3 face = (float3)(headCentreLocal + forward * (0.5f * headRadius) + Vector3.down * (0.45f * headRadius));
        caps.Add(new Capsule { a0 = face, b0 = face, a1 = face, b1 = face, r = headRadius });
        using NativeArray<Capsule> settleCaps = new(caps.ToArray(), Allocator.TempJob);
        for (int i = 0; i < points; i++) shapeK[i] = 0f;
        for (int i = 0; i < guides * points; i++) bendRest[i] = 0f;

        StrandSimJob job = new()
        {
            pos = pos, prev = prev, restLocal = restLocal, segLen = segLen, bendRest = bendRest, shapeK = shapeK,
            capsules = settleCaps, resets = resets, points = points, substeps = 360, iterations = 2, dt = 1f / 120f,
            damping = 6f, bendCompliance = 1f, friction = 0.6f, particleRadius = ParticleRadius, maxSpeed = 5f,
            maxStretch = MaxStretch, maxDeviation = 0f, gravity = new float3(0, -9.81f, 0), head0 = float3.zero, head1 = float3.zero,
            rot0 = quaternion.identity, rot1 = quaternion.identity
        };
        job.Schedule(guides, 4).Complete();

        for (int k = 0; k < guides; k++)
        {
            int o = k * points;
            for (int i = 0; i < points; i++) restLocal[o + i] = pos[o + i];
            for (int i = 2; i < points; i++) bendRest[o + i] = math.distance(restLocal[o + i], restLocal[o + i - 2]);
            resets[k] = 0;
        }

        UpdateShapeStiffness();
    }

    /// <summary>shape stiffness = fraction of the way back to the rest shape per 1/30 s at the root, decaying
    /// exponentially along the strand (falloff = arc fraction per e-fold); converted to a per-iteration factor so the
    /// look does not depend on the substep / iteration counts</summary>
    void UpdateShapeStiffness()
    {
        int n = Mathf.Max(1, Substeps * Iterations);
        for (int i = 0; i < points; i++)
        {
            float s = (float)i / (points - 1);
            float perFrame = Mathf.Clamp(ShapeRoot * Mathf.Exp(-s / Mathf.Max(ShapeFalloff, 1e-3f)), 0f, 0.99f);
            shapeK[i] = i < 2 ? 0f : 1f - Mathf.Pow(1f - perFrame, 1f / n);
        }
    }

    void BuildMesh()
    {
        int vcount = ribbons * points * 2;
        mesh = new Mesh { name = "Hair ribbons", indexFormat = vcount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        mesh.MarkDynamic();
        VertexAttributeDescriptor[] layout =
        {
            new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
            new(VertexAttribute.Tangent, VertexAttributeFormat.Float16, 4, 0),
            new(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, 1)
        };
        mesh.SetVertexBufferParams(vcount, layout);

        // static stream: (side 0|1, s along the strand, per-ribbon random, width scale)
        System.Random rng = new(11);
        using (NativeArray<float4> uv = new(vcount, Allocator.Temp))
        {
            NativeArray<float4> u = uv;
            for (int r = 0; r < ribbons; r++)
            {
                float rnd = (float)rng.NextDouble();
                float width = r % ribbonsPerGuide == 0 ? 1f : 0.8f;
                for (int i = 0; i < points; i++)
                {
                    float s = (float)i / (points - 1);
                    int v = (r * points + i) * 2;
                    u[v] = new float4(0, s, rnd, width);
                    u[v + 1] = new float4(1, s, rnd, width);
                }
            }

            mesh.SetVertexBufferData(u, 0, 0, vcount, 1, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        }

        int icount = ribbons * (points - 1) * 6;
        int[] idx = new int[icount];
        int w = 0;
        for (int r = 0; r < ribbons; r++)
        {
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
        }

        mesh.SetIndexBufferParams(icount, mesh.indexFormat);
        if (mesh.indexFormat == IndexFormat.UInt16)
        {
            ushort[] i16 = new ushort[icount];
            for (int i = 0; i < icount; i++) i16[i] = (ushort)idx[i];
            mesh.SetIndexBufferData(i16, 0, 0, icount, MeshUpdateFlags.DontRecalculateBounds);
        }
        else
        {
            mesh.SetIndexBufferData(idx, 0, 0, icount, MeshUpdateFlags.DontRecalculateBounds);
        }

        mesh.subMeshCount = 1;
        mesh.SetSubMesh(0, new SubMeshDescriptor(0, icount), MeshUpdateFlags.DontRecalculateBounds);
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one);

        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        meshRenderer = gameObject.AddComponent<MeshRenderer>();
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off; // Quest: no hair shadow pass
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.BlendProbes;
        meshRenderer.enabled = false; // until the first simulated frame: never a mesh at the origin
    }

    void BuildMaterial()
    {
        Shader shader = Resources.Load<Shader>("HM_HairStrands");
        if (shader == null) shader = Shader.Find("HeadMovement/HairStrands");
        if (shader == null) throw new InvalidOperationException("HM_HairStrands shader missing");
        material = new Material(shader) { name = "Hair (Kajiya-Kay)" };
        JObject col = (JObject)groom["colour"];
        Vector4 Lin(string zone, Vector4 fallback) =>
            col[zone] is JObject z && z["linear_srgb"] is JArray a ? new Vector4(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>(), 1) : fallback;
        Vector4 mid = Lin("mid", new Vector4(0.175f, 0.025f, 0.017f, 1));
        material.SetVector("_RootColor", Lin("root", mid));
        material.SetVector("_MidColor", mid);
        material.SetVector("_TipColor", Lin("tip", mid));
        material.SetVector("_HighlightColor", Lin("highlight", mid * 3f));
        // per-strand variation: the luminance range of the measured strand palette relative to the mid colour
        float midY = Lum(mid), lo = 0.75f, hi = 1.25f;
        if (col["strand_palette_linear"] is JArray pal && pal.Count >= 2 && midY > 1e-4f)
        {
            lo = Lum(V3(pal[0])) / midY;
            hi = Lum(V3(pal[pal.Count - 1])) / midY;
        }

        material.SetFloat("_VarLo", Mathf.Clamp(lo, 0.3f, 1f));
        material.SetFloat("_VarHi", Mathf.Clamp(hi, 1f, 2.5f));
        JObject strands = (JObject)groom["strands"];
        material.SetFloat("_WidthRoot", strands.Value<float>("width_root_m") * LengthScale);
        material.SetFloat("_WidthTip", strands.Value<float>("width_tip_m") * LengthScale);
        JObject kk = (JObject)groom["render"]?["kajiya_kay"];
        if (kk != null)
        {
            material.SetFloat("_Shift1", kk.Value<float>("primary_shift"));
            material.SetFloat("_Shift2", kk.Value<float>("secondary_shift"));
            material.SetFloat("_Exp1", kk.Value<float>("primary_exponent"));
            material.SetFloat("_Exp2", kk.Value<float>("secondary_exponent"));
            material.SetFloat("_Specular", kk.Value<float>("specular"));
        }

        if (groom["render"]?["tip_emission"]?["e"] is JArray e && e.Count == 6)
        {
            material.SetVector("_Emit0123", new Vector4(e[0].Value<float>(), e[1].Value<float>(), e[2].Value<float>(), e[3].Value<float>()));
            material.SetVector("_Emit45", new Vector4(e[4].Value<float>(), e[5].Value<float>(), 0, 0));
        }

        meshRenderer.sharedMaterial = material;
        ApplyLook();
    }

    static float Lum(Vector3 c) => 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;

    public void ApplyLook()
    {
        if (material == null) return;
        material.SetFloat("_TipGlow", TipGlow);
        material.SetFloat("_Opacity", Opacity);
    }

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
        {
            // the head collider keeps part of the groom's standoff (hair volume over the scalp)
            float r = colliderDefs[c].R + (colliderDefs[c].Name == "head" ? HeadStandoff * standoff : 0f);
            capsules[c] = new Capsule { a0 = kinPrev.A[c], b0 = kinPrev.B[c], a1 = kinCur.A[c], b1 = kinCur.B[c], r = r };
        }

        // a zero-size dummy keeps the array length fixed (capsules has one spare slot)
        float3 far = kinCur.HeadPos + new float3(0, 1000, 0);
        capsules[colliderDefs.Length] = new Capsule { a0 = far, b0 = far, a1 = far, b1 = far, r = 0 };
        int sub = settle ? Mathf.Max(1, Mathf.RoundToInt(SettleSeconds * 120f)) : Mathf.Clamp(Substeps, 1, 64);
        if (settle) dt = SettleSeconds;
        float3 gravity = (float3)(avatar.transform.InverseTransformDirection(Vector3.down) * 9.81f);
        // q and -q are the same rotation: interpolate along the short arc (nlerp across a sign flip passes near zero
        // and flings the roots and shape targets around)
        quaternion r1 = kinCur.HeadRot;
        if (math.dot(kinPrev.HeadRot.value, r1.value) < 0f) r1 = new quaternion(-r1.value);
        StrandSimJob job = new()
        {
            pos = pos, prev = prev, restLocal = restLocal, segLen = segLen, bendRest = bendRest, shapeK = shapeK,
            capsules = capsules, resets = resets, points = points, substeps = sub, iterations = Mathf.Clamp(Iterations, 1, 8),
            dt = dt / sub, damping = settle ? 10f : Damping, bendCompliance = BendCompliance, friction = Friction,
            particleRadius = ParticleRadius, maxSpeed = 30f, maxStretch = MaxStretch, maxDeviation = MaxDeviation, gravity = gravity,
            head0 = kinPrev.HeadPos, head1 = kinCur.HeadPos, rot0 = kinPrev.HeadRot, rot1 = r1
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
        if (avatar == null) return;
        int f = avatar.CurrentFrame;
        bool show = Visible && avatar.Visible;
        if (f < 0)
        {
            meshRenderer.enabled = false;
            return;
        }

        if (f != simFrame) Advance(f);
        meshRenderer.enabled = show && meshValid;
        if (material != null)
        {
            Vector3 hc = avatar.Bone(HeadJoint).TransformPoint(headCentreLocal);
            material.SetVector("_HeadCenterWS", hc);
        }
    }

    /// <summary>bring the simulation to frame f (called from LateUpdate; the CLI calls it to sync before a screenshot)</summary>
    public void Advance(int f)
    {
        watch.Restart();
        int steps0 = Steps;
        bool rehang = simFrame < 0 || f < simFrame || f - simFrame > MaxCatchUpFrames;
        int from;
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
        simFrame = f;
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
        Vector3 hc = (Vector3)kinCur.HeadPos + (Quaternion)kinCur.HeadRot * headCentreLocal;
        RibbonMeshJob job = new()
        {
            pos = pos, childOffset = childOffset, wavePhase = wavePhase, segLen = segLen, vertices = vertices,
            ribbonMin = ribbonMin, ribbonMax = ribbonMax, points = points, ribbonsPerGuide = ribbonsPerGuide,
            spread = spread, clump = clump, volume = Volume, waveAmp = WaveAmplitude * LengthScale,
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

        mesh.SetVertexBufferData(vertices, 0, 0, vertices.Length, 0,
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
        float pad = 0.05f;
        Bounds b = new();
        b.SetMinMax((Vector3)mn - Vector3.one * pad, (Vector3)mx + Vector3.one * pad);
        mesh.bounds = b;
        meshValid = true;
    }

    /// <summary>force a full re-hang + pre-roll at the shown frame (e.g. after changing the dynamics)</summary>
    public void ResetSimulation()
    {
        simFrame = -1;
        if (avatar != null && avatar.CurrentFrame >= 0) Advance(avatar.CurrentFrame);
    }

    public void ResetStats()
    {
        StepMsAvg = 0;
        StepMsMax = 0;
        RenderFrameMsTotal = 0;
        RenderFrames = 0;
    }

    /// <summary>playtest / review numbers in avatar-local metres</summary>
    public Dictionary<string, object> State()
    {
        float maxStretch = 0f, maxReach = 0f, maxReachRatio = 0f, minY = float.MaxValue;
        int nonFinite = 0, tipsBelowHead = 0, resetCount = 0;
        float3 head = kinCur.HeadPos;
        float maxRootErr = 0f;
        for (int g = 0; g < guides; g++)
        {
            int o = g * points;
            resetCount += resets[g];
            float L = segLen[g];
            float3 expectRoot = kinCur.HeadPos + math.mul(kinCur.HeadRot, restLocal[o]);
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
        float3 up = math.mul(kinCur.HeadRot, new float3(0, 1, 0));
        float3 right = math.mul(kinCur.HeadRot, new float3(1, 0, 0));
        float3 crown = kinCur.HeadPos + math.mul(kinCur.HeadRot, (float3)headCentreLocal) + up * headRadius;
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
            float3 target = kinCur.HeadPos + math.mul(kinCur.HeadRot, restLocal[o]);
            dev.Add((math.distance(target, pos[o]), g));
        }

        dev.Sort((a, b) => b.d.CompareTo(a.d));
        List<object> outliers = new();
        for (int j = 0; j < Mathf.Min(5, dev.Count); j++)
        {
            JObject gd = (JObject)groom["guides"][dev[j].g];
            outliers.Add(new Dictionary<string, object>
            {
                ["guide"] = dev[j].g, ["tipFromRestM"] = dev[j].d, ["az"] = gd.Value<float>("az"), ["el"] = gd.Value<float>("el"),
                ["len"] = gd.Value<float>("len")
            });
        }
        float widthShoulder = lateral.Count > 10 ? lateral[(int)(lateral.Count * 0.97f)] - lateral[(int)(lateral.Count * 0.03f)] : float.NaN;
        float tipDropMax = drops.Count > 0 ? drops[(int)(drops.Count * 0.95f)] : float.NaN;
        Vector3 headWorld = avatar.transform.TransformPoint(head);
        return new Dictionary<string, object>
        {
            ["present"] = true, ["visible"] = RendererVisible, ["guides"] = guides, ["segments"] = points - 1,
            ["ribbons"] = ribbons, ["vertices"] = VertexCount, ["triangles"] = TriangleCount, ["frame"] = simFrame,
            ["avatarFrame"] = avatar.CurrentFrame, ["steps"] = Steps, ["rehangs"] = Rehangs,
            ["strandResets"] = resetCount, ["nonFinite"] = nonFinite, ["maxStretch"] = maxStretch,
            ["maxReachM"] = maxReach, ["maxReachRatio"] = maxReachRatio, ["minWorldY"] = minY,
            ["tipsBelowHeadFraction"] = guides > 0 ? (float)tipsBelowHead / guides : 0f,
            ["rootPinErrorMm"] = maxRootErr * 1000f, ["rootMismatchMm"] = RootMismatchMm, ["lengthScale"] = LengthScale,
            ["substeps"] = Substeps, ["iterations"] = Iterations, ["damping"] = Damping, ["tipGlow"] = TipGlow,
            ["opacity"] = Opacity, ["stepMsAvg"] = StepMsAvg, ["stepMsMax"] = StepMsMax, ["lastUpdateMs"] = LastUpdateMs,
            ["renderFrames"] = RenderFrames,
            ["msPerRenderFrame"] = RenderFrames > 0 ? RenderFrameMsTotal / RenderFrames : 0.0,
            ["boundsCenter"] = new[] { meshRenderer.bounds.center.x, meshRenderer.bounds.center.y, meshRenderer.bounds.center.z },
            ["boundsSize"] = new[] { meshRenderer.bounds.size.x, meshRenderer.bounds.size.y, meshRenderer.bounds.size.z },
            ["head"] = new[] { headWorld.x, headWorld.y, headWorld.z }, ["warning"] = Warning,
            ["guideWidthShoulderM"] = widthShoulder, ["tipDropBelowCrownP95M"] = tipDropMax,
            ["tipFromRestMedianM"] = dev.Count > 0 ? dev[dev.Count / 2].d : float.NaN, ["outliers"] = outliers
        };
    }

    /// <summary>
    /// Plays the whole capture through the hair, frame by frame (the avatar is driven directly and restored to its
    /// frame afterwards): CPU cost per simulation step, stability, and the motion numbers of the hair reference
    /// (dancecap docs/HAIR_REFERENCE.md 3): spread radius r95 of the hair about the neck's vertical axis on calm and
    /// turn frames (|head yaw rate| below calmDps / above turnDps) and the trailing angle of the hair mass behind
    /// the head in turns (positive = trailing the turn).
    /// </summary>
    public Dictionary<string, object> Sweep(float calmDps = 90f, float turnDps = 180f)
    {
        int keep = avatar.CurrentFrame;
        int n = avatar.FrameCount;
        double sim0 = SimMsTotal, mesh0 = MeshMsTotal;
        int steps0 = Steps;
        float[] spread = new float[n], massAz = new float[n], yawRate = new float[n];
        Vector3[] facing = new Vector3[n];
        List<double> ms = new();
        float maxStretch = 0f;
        int nonFinite = 0, resets0 = 0;
        for (int g = 0; g < guides; g++) resets0 += resets[g];
        Transform root = avatar.transform;
        simFrame = -1;
        List<float> r = new();
        for (int k = 0; k < n; k++)
        {
            avatar.SetFrame(k);
            Advance(k);
            if (k > 0) ms.Add(LastUpdateMs);
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

                    maxStretch = Mathf.Max(maxStretch, math.distance(p, pos[o + i - 1]) / segLen[g]);
                    Vector3 h = (Vector3)p - neck;
                    h.y = 0;
                    r.Add(h.magnitude);
                    if (i >= points / 2) mass += h;
                }
            }

            r.Sort();
            spread[k] = r.Count > 0 ? r[(int)(0.95f * (r.Count - 1))] : float.NaN;
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
            if (w < calmDps) calm.Add(spread[k]);
            if (w > turnDps)
            {
                turn.Add(spread[k]);
                trail.Add(-massAz[k] * Mathf.Sign(yawRate[k]));
            }
        }

        int resets1 = 0;
        for (int g = 0; g < guides; g++) resets1 += resets[g];
        avatar.SetFrame(keep);
        simFrame = -1;
        Advance(keep);
        ms.Sort();
        return new Dictionary<string, object>
        {
            ["frames"] = n, ["calmFrames"] = calm.Count, ["turnFrames"] = turn.Count,
            ["spreadR95CalmMedianM"] = Q(calm, 0.5f), ["spreadR95TurnMedianM"] = Q(turn, 0.5f),
            ["spreadR95TurnP90M"] = Q(turn, 0.9f), ["trailingDegTurnMedian"] = Q(trail, 0.5f),
            ["trailingDegTurnP25"] = Q(trail, 0.25f), ["trailingDegTurnP75"] = Q(trail, 0.75f),
            ["maxStretch"] = maxStretch, ["nonFinite"] = nonFinite, ["strandResets"] = resets1 - resets0,
            ["stepMsMean"] = ms.Count > 0 ? ms.Average() : 0.0, ["stepMsP95"] = ms.Count > 0 ? ms[(int)(0.95 * (ms.Count - 1))] : 0.0,
            ["stepMsMax"] = ms.Count > 0 ? ms[^1] : 0.0, ["simJobMsMean"] = (SimMsTotal - sim0) / Mathf.Max(1, Steps - steps0),
            ["meshJobMsMean"] = (MeshMsTotal - mesh0) / Mathf.Max(1, n), ["burst"] = Unity.Burst.BurstCompiler.IsEnabled,
            ["substeps"] = Substeps, ["iterations"] = Iterations,
            ["particles"] = guides * points, ["vertices"] = VertexCount
        };
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
        if (pos.IsCreated) pos.Dispose();
        if (prev.IsCreated) prev.Dispose();
        if (restLocal.IsCreated) restLocal.Dispose();
        if (segLen.IsCreated) segLen.Dispose();
        if (bendRest.IsCreated) bendRest.Dispose();
        if (shapeK.IsCreated) shapeK.Dispose();
        if (resets.IsCreated) resets.Dispose();
        if (capsules.IsCreated) capsules.Dispose();
        if (childOffset.IsCreated) childOffset.Dispose();
        if (wavePhase.IsCreated) wavePhase.Dispose();
        if (vertices.IsCreated) vertices.Dispose();
        if (ribbonMin.IsCreated) ribbonMin.Dispose();
        if (ribbonMax.IsCreated) ribbonMax.Dispose();
        if (mesh != null) Destroy(mesh);
        if (material != null) Destroy(material);
    }
}
