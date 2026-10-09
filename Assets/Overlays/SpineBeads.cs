using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The follower's spine as a chain of small glowing white beads (user 2026-10-07: "a set of small white spheres, not a
/// line renderer"). Dancer.cs hands it her spine joints every pose frame (pelvis, spine1, spine2, spine3, neck) and one
/// colour per joint (SkeletonStyle: the beat pulse in rhythm mode, the estimated trunk load in physics mode; else the
/// legacy jerk glow). The beads sit at equal arc-length steps along the SAME Catmull-Rom curve the spine line used
/// (CatmullRomSpline, 20 samples per segment), pelvis to neck, and each takes the joint colours interpolated at its
/// place on the curve - exactly where the line's gradient keys were - so the rhythm pulse climbs the chain bead by bead.
///
/// Rendering: one GPU-instanced draw per camera (Graphics.RenderMeshInstanced from RenderPipelineManager
/// .beginCameraRendering, so the beads show in every Game / Scene / XR camera render, also manual renders and paused
/// frames; single-pass stereo draws both eyes at once), a shared 320-triangle icosphere, the per-bead colour in a
/// MaterialPropertyBlock array; Resources/HM_Bead.shader: opaque, Geometry queue like the skeleton lines, colour = the
/// line colour as the line's shader graph sees it (clamped, linearised) x its HDR gain LineGain (bloom), so a bead is
/// exactly as bright as the line was and the translucent avatars sort over it like over the lines.
/// Every buffer is preallocated: nothing is allocated per frame.
///
/// Visibility follows the follower's skeleton lines: the beads draw only while VisibilitySource (one of her other
/// skeleton LineRenderers) is enabled, and their radius scales with its widthMultiplier - so whatever hides or fades her
/// skeleton (the tour's graph views, review close-ups, probes that disable the dancers' renderers, a deactivated dancer)
/// hides or fades the beads with it, without knowing about them.
/// </summary>
public class SpineBeads : MonoBehaviour
{
    /// <summary>default bead radius (m): ~1.3 cm beads (user 2026-10-08: smaller dots; was 2.1 cm), 60 % of the first
    /// size - still wider than the 1 cm spine line they replace, and dots rather than a line at orbit distance</summary>
    public const float DefaultRadius = 0.0063f;

    /// <summary>default centre-to-centre spacing (m) along the curve: a ~1.4 cm gap between 1.26 cm beads (gap about one
    /// bead wide, so the smaller beads still read as separate dots and not as a dotted line)</summary>
    public const float DefaultSpacing = 0.027f;

    public const int MaxBeads = 64;

    /// <summary>the skeleton lines' shader graph (Assets/bloom-shader.shadergraph): screen HDR = linear(vertex colour) x
    /// its constant Float node 3.1 (BloomMat's _Color 6.42 is not wired in; measured 2026-10-07: line colour 1 / 0.5 /
    /// 0.22 renders 3.10 / 0.669 / 0.122)</summary>
    public const float LineGain = 3.1f;
    const int Joints = 5;
    const int SamplesPerSegment = 20;
    const int CurvePoints = (Joints - 1) * SamplesPerSegment + 1;

    static Mesh sphere;
    static Shader shader;
    static readonly int BeadColorId = Shader.PropertyToID("_BeadColor");
    static readonly int IntensityId = Shader.PropertyToID("_Intensity");
    static readonly int SkelRefId = Shader.PropertyToID("_SkelRef");
    static readonly int StencilPassId = Shader.PropertyToID("_StencilPass");

    readonly Matrix4x4[] matrices = new Matrix4x4[MaxBeads];
    readonly Vector4[] colours = new Vector4[MaxBeads];
    readonly Vector3[] curve = new Vector3[CurvePoints];
    readonly float[] arc = new float[CurvePoints];
    readonly Vector3[] beadPositions = new Vector3[MaxBeads];
    readonly float[] beadParam = new float[MaxBeads];

    Material material;
    MaterialPropertyBlock props;
    RenderParams renderParams;
    Bounds bounds;
    int count;
    bool hasPose;
    bool stencilOn;
    float radius = DefaultRadius, spacing = DefaultSpacing;
    float typicalLength = 0.5f;
    float lastScale = 1f;

    // draw accounting (playtests): instanced draws issued per frame (one per camera render) and the instances per draw
    int drawFrame = -1, drawsThisFrame, drawsLastFrame, instancesLastDraw;
    readonly List<Camera> camerasLastFrame = new(8), camerasThisFrame = new(8);

    /// <summary>her skeleton line whose enabled flag and widthMultiplier the beads mirror (see the class summary)</summary>
    public Renderer VisibilitySource;

    /// <summary>off: no beads drawn (playtests: A/B draw-call counts); the skeleton visibility still applies when on</summary>
    public bool Enabled = true;

    /// <summary>roleHidden (RoleHiddenSpans): her skeleton is hidden in a span, so the bead chain is too (Dancer.SpanHidden sets it)</summary>
    public bool SpanHidden;

    /// <summary>roleHidden fade 0..1: the bead colours are scaled by it, like the skeleton lines' (Dancer.RoleAlpha; the chain is re-set when it changes)</summary>
    public float RoleAlpha = 1f;

    public Role DancerRole { get; private set; } = Role.Follow;
    public int Count => count;
    public float Radius => radius;
    public float Spacing => spacing;

    /// <summary>median pelvis -> neck curve length over the capture (m); the bead count is fixed from it</summary>
    public float TypicalLength => typicalLength;

    public Material Material => material;
    public bool Instanced => material != null && material.enableInstancing;
    /// <summary>instanced draws of the last COMPLETED frame that drew beads (one per camera render)</summary>
    public int DrawsLastFrame => drawsLastFrame;

    public int InstancesLastDraw => instancesLastDraw;

    /// <summary>Time.frameCount of the latest bead draw (-1: never drawn)</summary>
    public int LastDrawFrame => drawFrame;

    public IReadOnlyList<Camera> CamerasLastFrame => camerasLastFrame;

    /// <summary>beads are drawn now: a pose is set, enabled, and the skeleton they belong to is shown</summary>
    public bool Visible => isActiveAndEnabled && Enabled && hasPose && count > 0 && SkeletonScale() > 0.01f;

    public static SpineBeads Create(Transform parent, Role role)
    {
        GameObject go = new("spine beads");
        go.transform.SetParent(parent, false);
        SpineBeads b = go.AddComponent<SpineBeads>();
        b.Init(role);
        return b;
    }

    void Init(Role role)
    {
        DancerRole = role;
        if (shader == null) shader = Resources.Load<Shader>("HM_Bead");
        if (shader == null) shader = Shader.Find("HeadMovement/Bead");
        material = new Material(shader) { name = $"{role} spine beads", enableInstancing = true };
        // the skeleton lines' HDR gain: the beads glow exactly as bright as the lines
        material.SetFloat(IntensityId, LineGain);
        material.SetFloat(SkelRefId, Dancer.SkeletonStencilBit(role));
        ApplyStencil(true);
        props = new MaterialPropertyBlock();
        props.SetVectorArray(BeadColorId, colours); // fixes the array length at MaxBeads
        renderParams = new RenderParams(material)
        {
            matProps = props,
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
            lightProbeUsage = LightProbeUsage.Off,
            reflectionProbeUsage = ReflectionProbeUsage.Off,
            motionVectorMode = MotionVectorGenerationMode.Camera,
            layer = gameObject.layer
        };
        EnsureSphere();
    }

    /// <summary>bead size and spacing (m); the count follows TypicalLength / spacing</summary>
    public void Configure(float beadRadius, float beadSpacing)
    {
        radius = Mathf.Clamp(beadRadius, 0.002f, 0.05f);
        spacing = Mathf.Clamp(beadSpacing, 0.01f, 0.2f);
        Recount();
        hasPose = false; // re-placed on the next SetChain
    }

    /// <summary>fix the bead count from the median pelvis -> neck curve length of the capture (no popping beads when the
    /// fitted spine stretches a little between frames: the beads keep their place along it, like vertebrae)</summary>
    public void SetTypicalLength(float metres)
    {
        if (float.IsFinite(metres) && metres > 0.05f) typicalLength = metres;
        Recount();
    }

    void Recount() => count = Mathf.Clamp(Mathf.RoundToInt(typicalLength / spacing) + 1, 2, MaxBeads);

    /// <summary>pelvis -> neck curve length of these joints (the spline the beads sit on)</summary>
    public static float CurveLength(IReadOnlyList<Vector3> joints)
    {
        float length = 0;
        Vector3 prev = joints[0];
        for (int seg = 0; seg < Joints - 1; seg++)
        {
            Vector3 p0 = joints[Mathf.Max(0, seg - 1)], p1 = joints[seg], p2 = joints[seg + 1], p3 = joints[Mathf.Min(Joints - 1, seg + 2)];
            for (int s = 1; s <= SamplesPerSegment; s++)
            {
                Vector3 p = CatmullRomSpline.Position(p0, p1, p2, p3, s / (float)SamplesPerSegment);
                length += Vector3.Distance(prev, p);
                prev = p;
            }
        }

        return length;
    }

    /// <summary>place the beads on this frame's spine (5 joints, pelvis first) and colour them from the 5 joint colours
    /// (LDR like the line's Color32 vertex colours). No allocation.</summary>
    public void SetChain(Vector3[] joints, Color[] jointColours)
    {
        if (count == 0) Recount();
        // the spine curve, as CatmullRomSpline.Generate samples it (clamped end points, 20 samples per segment)
        int k = 0;
        for (int seg = 0; seg < Joints - 1; seg++)
        {
            Vector3 p0 = joints[Mathf.Max(0, seg - 1)], p1 = joints[seg], p2 = joints[seg + 1], p3 = joints[Mathf.Min(Joints - 1, seg + 2)];
            for (int s = seg == 0 ? 0 : 1; s <= SamplesPerSegment; s++) curve[k++] = CatmullRomSpline.Position(p0, p1, p2, p3, s / (float)SamplesPerSegment);
        }

        arc[0] = 0;
        for (int i = 1; i < CurvePoints; i++) arc[i] = arc[i - 1] + Vector3.Distance(curve[i - 1], curve[i]);
        float length = arc[CurvePoints - 1];
        if (!float.IsFinite(length) || length < 1e-4f)
        {
            hasPose = false;
            return;
        }

        // equal arc-length steps, pelvis to neck
        int j = 1;
        Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
        for (int b = 0; b < count; b++)
        {
            float target = length * b / (count - 1);
            while (j < CurvePoints - 1 && arc[j] < target) j++;
            float segLen = arc[j] - arc[j - 1];
            float f = segLen > 1e-7f ? Mathf.Clamp01((target - arc[j - 1]) / segLen) : 0f;
            Vector3 p = Vector3.LerpUnclamped(curve[j - 1], curve[j], f);
            beadPositions[b] = p;
            beadParam[b] = (j - 1 + f) / (CurvePoints - 1); // 0 pelvis .. 1 neck; joint i at i / 4 (the line's gradient keys)
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);

            float u = beadParam[b] * (Joints - 1);
            int i0 = Mathf.Clamp(Mathf.FloorToInt(u), 0, Joints - 2);
            Color c = Color.LerpUnclamped(jointColours[i0], jointColours[i0 + 1], u - i0);
            if (RoleAlpha < 0.9999f) c = new Color(c.r * RoleAlpha, c.g * RoleAlpha, c.b * RoleAlpha, c.a);
            colours[b] = LineColour(c);
        }

        for (int b = count; b < MaxBeads; b++) colours[b] = Vector4.zero;
        props.SetVectorArray(BeadColorId, colours);
        bounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one * (4f * radius));
        hasPose = true;
        lastScale = -1f; // matrices rebuilt below / at the next draw
        UpdateMatrices(SkeletonScale());
    }

    /// <summary>the colour the skeleton line's shader graph receives for a gradient colour: clamped to 0..1 (Color32 vertex
    /// colours), sRGB -> linear (the LineRenderer's vertex colours are linearised in the linear colour space), alpha 1</summary>
    static Vector4 LineColour(Color c)
    {
        Color l = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f).linear;
        return new Vector4(l.r, l.g, l.b, 1f);
    }

    void UpdateMatrices(float scale)
    {
        if (Mathf.Approximately(scale, lastScale)) return;
        lastScale = scale;
        float r = radius * scale;
        for (int b = 0; b < count; b++)
        {
            Vector3 p = beadPositions[b];
            Matrix4x4 m = default;
            m.m00 = r;
            m.m11 = r;
            m.m22 = r;
            m.m03 = p.x;
            m.m13 = p.y;
            m.m23 = p.z;
            m.m33 = 1f;
            matrices[b] = m;
        }
    }

    float SkeletonScale()
    {
        if (SpanHidden) return 0f;
        if (VisibilitySource == null) return 1f;
        if (!VisibilitySource.enabled || !VisibilitySource.gameObject.activeInHierarchy) return 0f;
        return VisibilitySource is LineRenderer line ? Mathf.Max(0f, line.widthMultiplier) : 1f;
    }

    void ApplyStencil(bool force)
    {
        bool on = Dancer.SkeletonStencilOn;
        if (!force && on == stencilOn) return;
        stencilOn = on;
        material.SetFloat(StencilPassId, on ? (float)StencilOp.Replace : (float)StencilOp.Keep);
    }

    /// <summary>the colour of bead i now as a line colour (sRGB 0..1, like the joint colours SkeletonStyle hands out)</summary>
    public Color BeadColour(int i) => i >= 0 && i < count ? ((Color)colours[i]).gamma : Color.clear;

    public Vector3 BeadPosition(int i) => i >= 0 && i < count ? beadPositions[i] : Vector3.zero;

    /// <summary>place of bead i along the curve, 0 = pelvis, 1 = neck (joint k at k / 4)</summary>
    public float BeadParam(int i) => i >= 0 && i < count ? beadParam[i] : 0f;

    void OnEnable() => RenderPipelineManager.beginCameraRendering += OnBeginCamera;

    void OnDisable() => RenderPipelineManager.beginCameraRendering -= OnBeginCamera;

    void OnBeginCamera(ScriptableRenderContext context, Camera cam)
    {
        if (material == null || props == null || !Visible) return;
        if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView && cam.cameraType != CameraType.VR) return;
        if ((cam.cullingMask & (1 << gameObject.layer)) == 0) return;
        ApplyStencil(false);
        UpdateMatrices(SkeletonScale());
        renderParams.camera = cam;
        renderParams.worldBounds = bounds;
        renderParams.layer = gameObject.layer;
        Graphics.RenderMeshInstanced(renderParams, sphere, 0, matrices, count);

        if (drawFrame != Time.frameCount)
        {
            drawFrame = Time.frameCount;
            drawsLastFrame = drawsThisFrame;
            camerasLastFrame.Clear();
            camerasLastFrame.AddRange(camerasThisFrame);
            drawsThisFrame = 0;
            camerasThisFrame.Clear();
        }

        drawsThisFrame++;
        instancesLastDraw = count;
        if (camerasThisFrame.Count < 8) camerasThisFrame.Add(cam);
    }

    /// <summary>draws issued for the CURRENT frame so far (drawsLastFrame is the completed previous frame)</summary>
    public int DrawsThisFrame => drawFrame == Time.frameCount ? drawsThisFrame : 0;

    void OnDestroy()
    {
        if (material != null) Destroy(material);
    }

    /// <summary>shared unit icosphere (subdivided twice: 162 vertices, 320 triangles, smooth normals)</summary>
    static void EnsureSphere()
    {
        if (sphere != null) return;
        float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
        List<Vector3> v = new()
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1)
        };
        for (int i = 0; i < v.Count; i++) v[i] = v[i].normalized;
        List<int> tris = new()
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1
        };
        for (int level = 0; level < 2; level++)
        {
            Dictionary<long, int> mid = new();
            List<int> next = new(tris.Count * 4);
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (mid.TryGetValue(key, out int m)) return m;
                v.Add(((v[a] + v[b]) * 0.5f).normalized);
                mid[key] = v.Count - 1;
                return v.Count - 1;
            }

            for (int i = 0; i < tris.Count; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
            }

            tris = next;
        }

        // outward winding (Unity: clockwise seen from outside, i.e. cross(b - a, c - a) points away from the centre)
        for (int i = 0; i < tris.Count; i += 3)
        {
            Vector3 a = v[tris[i]], b = v[tris[i + 1]], c = v[tris[i + 2]];
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), a + b + c) < 0) (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
        }
        sphere = new Mesh { name = "skeleton bead (icosphere 2)", hideFlags = HideFlags.DontSave };
        sphere.SetVertices(v);
        sphere.SetNormals(v);
        sphere.SetTriangles(tris, 0);
        sphere.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
        sphere.UploadMeshData(true);
    }
}
