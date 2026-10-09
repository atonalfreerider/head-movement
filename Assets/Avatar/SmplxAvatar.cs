using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// A dancer's shaped SMPL-X body as a runtime SkinnedMeshRenderer with 55 bones, built from the capture's skin
/// binary and driven per frame by the motion binary (SMPL-X local joint rotations). The math is SMPL-X linear
/// blend skinning: bones sit at the shaped rest joints with identity rest rotations, so bindpose_j = T(-J_j); the
/// root bone goes to J_0 + transl. Pose-corrective blendshapes are not applied (plain LBS).
/// Material (Resources/HM_AvatarLit, one texture, main light + SH: Quest-friendly): the photoreal albedo when the
/// skin is seam-split (version 2) and capture.json has smplx_albedo, else a neutral role tint (lead warm grey,
/// follow cool grey). Translucent per VIEWER_SPEC 3.2 (Opacity, ~0.3): the renderer carries a depth-prepass
/// material (queue 2990) and a colour material (queue 3000), so every pixel shows the front-most body surface once
/// over the opaque glowing skeleton inside. The two dancers are sorted back to front per camera: the nearer one's
/// translucent materials (body, shoes, hair: everything under it in the 2986-3009 band) move up by NearerQueueOffset,
/// so the farther dancer is drawn completely (prepass + colour) before the nearer one's prepass, and shows through
/// it. Optional (SkeletonsOverBodies, off by default): while translucent the colour pass skips the pixels of the
/// dancer's own glowing skeleton (stencil), so the body does not tint its skeleton (VIEWER_SPEC 3.3). HideFeet clips the
/// bare feet for the shoes (a per-vertex rest height
/// above the ankle, TEXCOORD1.x, so the cut rides on the skinned mesh). Attachments (shoes) share the materials'
/// opacity, visibility and lifetime via Attach(). Interface: the realism INTERFACES.md notes in the workspace repo (dancecap/work/<capture>/review/realism/).
/// </summary>
public class SmplxAvatar : MonoBehaviour
{
    const int MaxBonesPerVertex = 4;

    /// <summary>translucent depth prepasses run at 2990-2999 (avatar + attachments 2990, hair 2991), colour passes at
    /// 3000+ (avatar + attachments 3000, hair 3001, hair glow 3002)</summary>
    public const int DepthQueue = 2990, ColourQueue = 3000;

    const string DepthPass = "SRPDefaultUnlit", ColourPass = "UniversalForward";

    /// <summary>the nearer avatar's translucent materials draw this many queue slots later (farther: 2990/2991,
    /// 3000-3002; nearer: 3010/3011, 3020-3022), so each body hides its own back faces and the partner shows through</summary>
    public const int NearerQueueOffset = 20;

    /// <summary>materials under an avatar whose own queue lies in this band are re-queued by the back-to-front sort</summary>
    public const int SortBandMin = 2986, SortBandMax = 3009;

    /// <summary>off: every avatar keeps its base queues (one shared prepass: the partner is hidden behind a body).
    /// For before/after review shots only.</summary>
    public static bool SortBackToFront = true;

    /// <summary>the currently nearer avatar keeps its place until the other is this much nearer (m): no flicker</summary>
    const float SortHysteresis = 0.05f;

    /// <summary>VIEWER_SPEC 3.3 option: with SkeletonsOverBodies on and the body translucent (Opacity below this), the
    /// body's colour pass skips the pixels of its own glowing skeleton (stencil bit 1 lead / 2 follow, written by
    /// Resources/HM_SkeletonStencil on Dancer's skeleton lines), so the skeleton shows in its own colour, untinted by
    /// the body; the partner's body in front still blends over it. At the opaque look the body covers it.</summary>
    public const float SkeletonOverBodyBelowOpacity = 0.99f;

    static bool skeletonsOverBodies;

    /// <summary>how the colour pass turns the displayed Opacity into a blend alpha (VIEWER_SPEC 3.2):
    /// Pixel (default) per fragment from its shaded luminance (DisplayTransparency.AlphaLut: every part of a body shows
    /// 1 - Opacity on screen), Body one alpha per body (its median), Raw alpha = Opacity (before 2026-10-07)</summary>
    public enum AlphaModes
    {
        Pixel,
        Body,
        Raw
    }

    static AlphaModes alphaMode = AlphaModes.Pixel;

    public static AlphaModes AlphaMode
    {
        get => alphaMode;
        set
        {
            if (alphaMode == value) return;
            alphaMode = value;
            foreach (SmplxAvatar a in Live)
            {
                if (a != null) a.ApplyAppearance();
            }
        }
    }

    /// <summary>false (default, the reviewed look): each translucent body blends over its own skeleton (the skeleton
    /// keeps ~65 % of its colour, tinted by the body). true: the body skips its own skeleton's pixels, so the skeleton
    /// shows in its raw colour (crisp red lead; the follow's skeleton reads mid-grey between beats). Switch with
    /// hm_opacity --skeletons over|dimmed; integration review 2026-10-07 left it off (see INTERFACES.md).</summary>
    public static bool SkeletonsOverBodies
    {
        get => skeletonsOverBodies;
        set
        {
            if (skeletonsOverBodies == value) return;
            skeletonsOverBodies = value;
            Dancer.EnableSkeletonStencil(value); // the markers on the skeleton lines draw only while the option is on
            foreach (SmplxAvatar a in Live)
            {
                if (a != null) a.ApplyAppearance();
            }
        }
    }

    /// <summary>this dancer's skeleton stencil bit (Dancer.SkeletonStencilBit: lead 1, follow 2)</summary>
    public int SkeletonStencilBit => 1 << ((int)DancerRole & 1);

    SmplxData.Motion motion;
    Transform[] bones;
    Vector3[] restJoints;
    Vector3 restRoot;
    SkinnedMeshRenderer skinned;
    Material depthMaterial, colourMaterial;
    Texture2D albedo;
    DisplayTransparency display; // displayed opacity -> this body's blend alpha (VIEWER_SPEC 3.2)
    int frame = -1;
    float opacity = 1f;
    bool layerVisible = true, feetHidden, spanHidden;
    float footCut = -0.01f;
    readonly List<Attachment> attachments = new();

    // back-to-front sorting of the dancers (per camera, RenderPipelineManager.beginCameraRendering)
    sealed class SortEntry
    {
        public Material Material;
        public int Base, Applied;
    }

    static readonly List<SmplxAvatar> Live = new();
    static readonly List<Renderer> RendererBuffer = new();
    static readonly List<Material> MaterialBuffer = new();
    static readonly Comparison<SmplxAvatar> FartherFirst = (a, b) => b.sortKey.CompareTo(a.sortKey);
    static bool sortHooked;
    readonly List<SortEntry> sortEntries = new();
    int queueOffset, scannedHierarchy = -1, scannedAttachments = -1;
    float nextScan, sortKey;

    struct Attachment
    {
        public Renderer Renderer;
        public Material Depth, Colour;
    }

    public Role DancerRole { get; private set; }
    public int FrameCount => motion.FrameCount;
    public int BoneCount => bones.Length;
    public int VertexCount { get; private set; }
    public int CurrentFrame => frame;
    /// <summary>drawn now: the avatars layer is on and Opacity > 0.005</summary>
    public bool Visible => skinned != null && skinned.enabled;
    /// <summary>the avatars layer flag (hm_layer avatars), independent of opacity</summary>
    public bool LayerVisible => layerVisible;

    /// <summary>roleHidden (RoleHiddenSpans): this body is not drawn inside a hidden span - hair and shoes follow (Visible is false) -
    /// whatever the avatars layer and the view state's opacity say; the pose keeps being set</summary>
    public bool SpanHidden
    {
        get => spanHidden;
        set
        {
            if (spanHidden == value) return;
            spanHidden = value;
            ApplyAppearance();
        }
    }
    public Bounds WorldBounds => skinned.bounds;
    public bool Textured => albedo != null;
    public int TextureSize => albedo != null ? albedo.width : 0;
    public string TextureFormatName => albedo != null ? albedo.format.ToString() : null;
    public int TriangleCount { get; private set; }
    /// <summary>0 = drawn first (farther from the camera that rendered last), NearerQueueOffset = drawn after the partner</summary>
    public int QueueOffset => queueOffset;
    /// <summary>the skin binary this body was built from (attachments re-read it with SmplxData.ReadSkin)</summary>
    public string SkinPath { get; private set; }

    /// <summary>fires when Opacity, visibility or HideFeet change (hair, shoes)</summary>
    public event Action<SmplxAvatar> AppearanceChanged;

    /// <summary>body opacity shown now, 0..1 (VIEWER_SPEC 3.2: ~0.3 default, per view state; 1 = opaque look). Written
    /// by HeadMovement (user default, hm_opacity) and DanceTour (per state); hair and shoes read it.</summary>
    public float Opacity
    {
        get => opacity;
        set
        {
            float o = Mathf.Clamp01(float.IsFinite(value) ? value : 1f);
            if (Mathf.Abs(o - opacity) < 1e-4f) return;
            opacity = o;
            ApplyAppearance();
        }
    }

    public bool FeetHidden => feetHidden;

    /// <summary>foot cut height in metres relative to the ankle joint centre, measured in the rest pose (negative =
    /// below the ankle). Only active while HideFeet(true).</summary>
    public float FootCut
    {
        get => footCut;
        set
        {
            footCut = Mathf.Clamp(value, -0.1f, 0.1f);
            ApplyAppearance();
        }
    }

    /// <summary>clip the bare feet below FootCut in every pass (shoes replace them)</summary>
    public void HideFeet(bool hide)
    {
        if (hide == feetHidden) return;
        feetHidden = hide;
        ApplyAppearance();
    }

    /// <summary>avatar-local rest-pose y of the foot cut (side 0 = left, 1 = right; with the level foot mask)</summary>
    public float FootCutRestHeight(int side) => restJoints[side == 0 ? (int)SmplJoint.L_Ankle : (int)SmplJoint.R_Ankle].y + footCut;

    /// <summary>
    /// Shoes (Assets/Shoes): replace the per-vertex foot-mask heights (TEXCOORD1.x, one per skin vertex in the skin's
    /// order) so the cut can follow a shoe's top edge instead of a level plane; the fragments whose interpolated height
    /// is below FootCut are clipped while HideFeet is on. null restores the level mask (rest height above the ankle).
    /// </summary>
    public void SetFootHeights(float[] heights)
    {
        if (skinned == null || skinned.sharedMesh == null || footMask == null) return;
        if (heights != null && heights.Length != footMask.Count)
        {
            throw new ArgumentException($"{heights.Length} foot heights for {footMask.Count} vertices");
        }

        List<Vector2> uv = footMask;
        if (heights != null)
        {
            uv = new List<Vector2>(footMask.Count);
            for (int i = 0; i < heights.Length; i++) uv.Add(new Vector2(heights[i], footMask[i].y));
        }

        skinned.sharedMesh.SetUVs(1, uv);
        CustomFootHeights = heights != null;
    }

    /// <summary>SetFootHeights replaced the level foot mask</summary>
    public bool CustomFootHeights { get; private set; }

    List<Vector2> footMask; // the level mask built with the mesh (SetFootHeights(null) restores it)

    /// <summary>avatar-local rest-pose position of a SMPL-X joint</summary>
    public Vector3 RestJoint(int joint) => restJoints[joint];

    /// <param name="albedoPath">optional photoreal albedo PNG on the SMPL-X UV layout (needs a seam-split skin)</param>
    public static SmplxAvatar Create(string skinPath, string motionPath, Role role, Transform parent = null,
        string albedoPath = null)
    {
        SmplxData.Skin skin = SmplxData.ReadSkin(skinPath);
        SmplxData.Motion motion = SmplxData.ReadMotion(motionPath);

        GameObject go = new($"{role} SMPL-X Avatar");
        if (parent != null) go.transform.SetParent(parent, false);
        SmplxAvatar avatar = go.AddComponent<SmplxAvatar>();
        avatar.SkinPath = skinPath;
        avatar.Build(skin, motion, role, albedoPath);
        return avatar;
    }

    void Build(SmplxData.Skin skin, SmplxData.Motion m, Role role, string albedoPath)
    {
        DancerRole = role;
        motion = m;

        // bone hierarchy at the shaped rest pose (identity rotations)
        bones = new Transform[SmplxData.Joints];
        Matrix4x4[] bindposes = new Matrix4x4[SmplxData.Joints];
        for (int j = 0; j < SmplxData.Joints; j++)
        {
            int p = skin.Parents[j];
            Transform bone = new GameObject(SmplxData.JointNames[j]).transform;
            bone.SetParent(p < 0 ? transform : bones[p], false);
            bone.localPosition = p < 0 ? skin.RestJoints[j] : skin.RestJoints[j] - skin.RestJoints[p];
            bone.localRotation = Quaternion.identity;
            bones[j] = bone;
            bindposes[j] = Matrix4x4.Translate(-skin.RestJoints[j]);
        }

        restRoot = skin.RestJoints[0];
        restJoints = (Vector3[])skin.RestJoints.Clone();

        Mesh mesh = new() { name = $"{role} SMPL-X", indexFormat = IndexFormat.UInt32 };
        mesh.vertices = skin.RestVertices;
        mesh.triangles = skin.Triangles;
        TriangleCount = skin.Triangles.Length / 3;
        if (skin.Textured) mesh.uv = skin.Uv;
        // the seam-split mesh carries the unsplit normals; recalculating would crease every UV seam
        if (skin.Normals != null) mesh.normals = skin.Normals;
        else mesh.RecalculateNormals();
        SetBoneWeights(mesh, skin.Weights, skin.RestVertices.Length);
        footMask = FootMask(skin);
        mesh.SetUVs(1, footMask);
        mesh.bindposes = bindposes;
        mesh.RecalculateBounds();
        VertexCount = skin.RestVertices.Length;

        skinned = gameObject.AddComponent<SkinnedMeshRenderer>();
        skinned.sharedMesh = mesh;
        skinned.bones = bones;
        skinned.rootBone = bones[0];
        skinned.updateWhenOffscreen = true; // bounds follow the dance, never culled
        skinned.quality = SkinQuality.Bone4;
        skinned.shadowCastingMode = ShadowCastingMode.On;
        Texture tex = null;
        Color tint = Color.white;
        float lightInfluence = 0.35f; // the photo albedo carries the studio lighting
        if (skin.Textured && !string.IsNullOrEmpty(albedoPath) && System.IO.File.Exists(albedoPath))
        {
            albedo = LoadTexture(albedoPath, $"{role} albedo",
                readable => display = DisplayTransparency.FromTexture(readable, skin.RestVertices, skin.Triangles, skin.Uv, Color.white));
            tex = albedo;
        }
        else
        {
            if (!string.IsNullOrEmpty(albedoPath) && !skin.Textured)
            {
                Debug.LogWarning($"{role}: albedo given but the skin has no UVs (re-export with textures)");
            }

            // neutral shaded body: lead warm grey, follow cool grey (VIEWER_SPEC 3.2), shaped by the scene light
            tint = role == Role.Lead ? new Color(0.74f, 0.64f, 0.58f) : new Color(0.60f, 0.66f, 0.76f);
            lightInfluence = 0.9f;
        }

        display ??= DisplayTransparency.FromColour(tint);

        (depthMaterial, colourMaterial) = NewMaterials($"{role} avatar{(albedo != null ? " (textured)" : "")}", tex, tint, lightInfluence);
        skinned.sharedMaterials = new[] { depthMaterial, colourMaterial }; // one submesh, drawn once per material
        ApplyAppearance();
    }

    /// <summary>per split vertex (x) its rest-pose height above its own side's ankle joint in metres, +1 off the lower
    /// legs; (y) its lower-leg weight. HM_AvatarLit clips x < _FootCut when the feet are hidden.</summary>
    static List<Vector2> FootMask(SmplxData.Skin skin)
    {
        int n = skin.RestVertices.Length, J = SmplxData.Joints;
        float left = skin.RestJoints[(int)SmplJoint.L_Ankle].y, right = skin.RestJoints[(int)SmplJoint.R_Ankle].y;
        List<Vector2> mask = new(n);
        for (int v = 0; v < n; v++)
        {
            int o = v * J;
            float wl = skin.Weights[o + 4] + skin.Weights[o + 7] + skin.Weights[o + 10];
            float wr = skin.Weights[o + 5] + skin.Weights[o + 8] + skin.Weights[o + 11];
            float h = wl + wr < 0.25f ? 1f : skin.RestVertices[v].y - (wl >= wr ? left : right);
            mask.Add(new Vector2(h, wl + wr));
        }

        return mask;
    }

    static Shader AvatarShader()
    {
        Shader shader = Resources.Load<Shader>("HM_AvatarLit");
        if (shader == null) shader = Shader.Find("HeadMovement/AvatarLit");
        return shader;
    }

    /// <summary>the two HM_AvatarLit materials of one translucent renderer: depth prepass (queue 2990) + colour (3000)</summary>
    static (Material depth, Material colour) NewMaterials(string name, Texture tex, Color tint, float lightInfluence)
    {
        Shader shader = AvatarShader();
        Material depth = new(shader) { name = $"{name} depth" };
        Material colour = new(shader) { name = name };
        foreach (Material m in new[] { depth, colour })
        {
            if (tex != null) m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", tint);
            m.SetFloat("_LightInfluence", lightInfluence);
        }

        depth.SetShaderPassEnabled(ColourPass, false);
        depth.renderQueue = DepthQueue;
        colour.SetShaderPassEnabled(DepthPass, false);
        colour.SetShaderPassEnabled("ShadowCaster", false); // shadows come from the depth material only
        colour.renderQueue = ColourQueue;
        return (depth, colour);
    }

    void ApplyAppearance()
    {
        if (skinned == null) return;
        bool draw = layerVisible && !spanHidden && opacity > 0.005f;
        // translucent bodies cast no shadow (a solid shadow under a ghost reads wrong, and it saves a pass on Quest)
        ShadowCastingMode shadows = opacity > 0.99f ? ShadowCastingMode.On : ShadowCastingMode.Off;
        SetPair(depthMaterial, colourMaterial, feetHidden);
        skinned.enabled = draw;
        skinned.shadowCastingMode = shadows;
        for (int i = attachments.Count - 1; i >= 0; i--)
        {
            Attachment a = attachments[i];
            if (a.Renderer == null)
            {
                DestroyPair(a);
                attachments.RemoveAt(i);
                continue;
            }

            SetPair(a.Depth, a.Colour, false);
            a.Renderer.enabled = draw;
            a.Renderer.shadowCastingMode = shadows;
        }

        AppearanceChanged?.Invoke(this);
    }

    void SetPair(Material depth, Material colour, bool hideFeet)
    {
        // stencil test of the colour pass (HM_AvatarLit): NotEqual on this dancer's skeleton bit, or Always (off)
        bool overSkeleton = skeletonsOverBodies && opacity < SkeletonOverBodyBelowOpacity;
        foreach (Material m in new[] { depth, colour })
        {
            if (m == null) continue;
            m.SetFloat("_Opacity", opacity);
            // what the colour pass blends with for the displayed opacity: per pixel (LUT), this body's alpha, or raw
            m.SetFloat("_BlendAlpha", alphaMode == AlphaModes.Raw ? -1f : BlendAlpha);
            m.SetFloat("_PerPixelAlpha", alphaMode == AlphaModes.Pixel ? 1f : 0f);
            m.SetTexture("_AlphaLut", DisplayTransparency.AlphaLut());
            m.SetFloat("_HideFeet", hideFeet ? 1f : 0f);
            m.SetFloat("_FootCut", footCut);
            m.SetFloat("_SkelRef", SkeletonStencilBit);
            m.SetFloat("_SkelComp", overSkeleton ? (float)CompareFunction.NotEqual : (float)CompareFunction.Always);
        }
    }

    /// <summary>VIEWER_SPEC 3.2: Opacity is the opacity the SCREEN shows (0.35 = 65 % transparent); the colour pass blends
    /// with this body's alpha for it (DisplayTransparency: a bright body needs a lower alpha than a dark one)</summary>
    public float BlendAlpha => display != null ? display.BlendAlpha(opacity) : opacity;

    /// <summary>predicted displayed transparency at the current opacity: 1 - Opacity per pixel; the body model's median
    /// in Body mode; at alpha = Opacity in Raw mode</summary>
    public float PredictedTransparency => alphaMode == AlphaModes.Pixel || display == null ? 1f - opacity
        : display.TransparencyAt(alphaMode == AlphaModes.Raw ? opacity : BlendAlpha);

    public DisplayTransparency Display => display;

    /// <summary>the colour pass skips this dancer's own skeleton pixels now (translucent and SkeletonsOverBodies)</summary>
    public bool SkeletonOverBody => skeletonsOverBodies && opacity < SkeletonOverBodyBelowOpacity;

    static void DestroyPair(Attachment a)
    {
        if (a.Depth != null) Destroy(a.Depth);
        if (a.Colour != null) Destroy(a.Colour);
    }

    /// <summary>
    /// Put a renderer (one submesh, e.g. a procedural shoe parented under Bone(7)/Bone(8)) on this avatar's translucent
    /// material: HM_AvatarLit depth + colour materials that follow the avatar's Opacity, visibility and lifetime.
    /// Returns the colour material for tuning (_Specular, _SpecPower, _Wrap, _Exposure).
    /// </summary>
    public Material Attach(Renderer r, Color tint, Texture2D texture = null, bool vertexColors = false, float lightInfluence = 0.85f)
    {
        if (r == null) throw new ArgumentNullException(nameof(r));
        Detach(r);
        (Material depth, Material colour) = NewMaterials($"{DancerRole} {r.name}", texture, tint, lightInfluence);
        foreach (Material m in new[] { depth, colour }) m.SetFloat("_UseVertexColor", vertexColors ? 1f : 0f);
        r.sharedMaterials = new[] { depth, colour };
        r.receiveShadows = false;
        attachments.Add(new Attachment { Renderer = r, Depth = depth, Colour = colour });
        ApplyAppearance();
        return colour;
    }

    /// <summary>stop syncing a renderer attached with Attach (its materials are destroyed)</summary>
    public void Detach(Renderer r)
    {
        for (int i = attachments.Count - 1; i >= 0; i--)
        {
            if (attachments[i].Renderer != r) continue;
            DestroyPair(attachments[i]);
            attachments.RemoveAt(i);
        }
    }

    // ---------------------------------------------------------------- back-to-front sorting of the two dancers

    void OnEnable()
    {
        if (!Live.Contains(this)) Live.Add(this);
        if (sortHooked) return;
        RenderPipelineManager.beginCameraRendering += SortForCamera;
        sortHooked = true;
    }

    void OnDisable()
    {
        Live.Remove(this);
        SetQueueOffset(0);
    }

    /// <summary>
    /// Per camera, before culling: rank the visible avatars by camera distance (pelvis) and give the farther one the
    /// base queues and the nearer one +NearerQueueOffset. Every translucent depth prepass + colour pass of the farther
    /// dancer (body, shoes, hair, tip glow) then runs before the nearer dancer's prepass: each body still shows only
    /// its own front-most surface, and the partner behind it shows through. Hysteresis keeps the order while the two
    /// are about equally far. Popping is limited to the overlap region at the moment they swap depth order.
    /// </summary>
    static void SortForCamera(ScriptableRenderContext context, Camera camera)
    {
        if (camera == null || (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)) return;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            if (Live[i] == null) Live.RemoveAt(i);
        }

        if (Live.Count == 0) return;
        Vector3 eye = camera.transform.position;
        for (int i = 0; i < Live.Count; i++)
        {
            SmplxAvatar a = Live[i];
            Vector3 p = a.bones != null && a.bones.Length > 0 && a.bones[0] != null ? a.bones[0].position : a.transform.position;
            a.sortKey = (p - eye).magnitude - (a.queueOffset > 0 ? SortHysteresis : 0f);
        }

        if (Live.Count > 1) Live.Sort(FartherFirst);
        for (int i = 0; i < Live.Count; i++)
        {
            Live[i].SetQueueOffset(SortBackToFront ? i * NearerQueueOffset : 0);
        }
    }

    /// <summary>re-queue every material in the sort band under this avatar (its own, Attach'ed shoes, hair) to its base
    /// queue + offset. A base the owner changes later is picked up (the material no longer shows what we applied).</summary>
    void SetQueueOffset(int offset)
    {
        bool rescan = transform.hierarchyCount != scannedHierarchy || attachments.Count != scannedAttachments ||
                      Time.unscaledTime >= nextScan;
        if (rescan) Rescan();
        if (!rescan && offset == queueOffset) return;
        queueOffset = offset;
        foreach (SortEntry e in sortEntries)
        {
            if (e.Material == null) continue;
            int q = e.Material.renderQueue;
            if (q != e.Base + e.Applied) e.Base = q; // the owner set a new base queue
            if (e.Base < SortBandMin || e.Base > SortBandMax)
            {
                e.Applied = 0;
                continue;
            }

            e.Applied = offset;
            if (q != e.Base + offset) e.Material.renderQueue = e.Base + offset;
        }
    }

    void Rescan()
    {
        scannedHierarchy = transform.hierarchyCount;
        scannedAttachments = attachments.Count;
        nextScan = Time.unscaledTime + 1f;
        sortEntries.RemoveAll(e => e.Material == null);
        GetComponentsInChildren(true, RendererBuffer);
        foreach (Renderer r in RendererBuffer)
        {
            r.GetSharedMaterials(MaterialBuffer);
            foreach (Material m in MaterialBuffer)
            {
                if (m == null || HasEntry(m)) continue;
                sortEntries.Add(new SortEntry { Material = m, Base = m.renderQueue, Applied = 0 });
            }
        }

        RendererBuffer.Clear();
        MaterialBuffer.Clear();
    }

    bool HasEntry(Material m)
    {
        foreach (SortEntry e in sortEntries)
        {
            if (e.Material == m) return true;
        }

        return false;
    }

    /// <summary>the queues of this avatar's sorted materials now, name -> queue (hm_opacity / playtests)</summary>
    public Dictionary<string, int> SortedQueues()
    {
        Dictionary<string, int> q = new();
        foreach (SortEntry e in sortEntries)
        {
            if (e.Material != null) q[e.Material.name] = e.Material.renderQueue;
        }

        return q;
    }

    /// <summary>PNG -> sRGB texture with mipmaps, block-compressed on the device (DXT/BC on desktop, ETC2/ASTC on
    /// Quest) and released from CPU memory</summary>
    public static Texture2D LoadTexture(string path, string name, Action<Texture2D> whileReadable = null)
    {
        Texture2D tex = new(2, 2, TextureFormat.RGBA32, true, false) { name = name };
        if (!tex.LoadImage(System.IO.File.ReadAllBytes(path), false))
        {
            Destroy(tex);
            throw new System.IO.InvalidDataException($"{path}: not a PNG/JPEG");
        }

        whileReadable?.Invoke(tex); // before compression: the pixels are still readable

        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 2;
        if (tex.width % 4 == 0 && tex.height % 4 == 0) tex.Compress(true);
        tex.Apply(true, true);
        return tex;
    }

    /// <summary>top-4 LBS weights per vertex, renormalised (SMPL-X vertices rarely carry more than 4)</summary>
    static void SetBoneWeights(Mesh mesh, float[] dense, int vertexCount)
    {
        BoneWeight[] weights = new BoneWeight[vertexCount];
        List<(int bone, float w)> top = new(SmplxData.Joints);
        for (int v = 0; v < vertexCount; v++)
        {
            top.Clear();
            for (int j = 0; j < SmplxData.Joints; j++)
            {
                float w = dense[v * SmplxData.Joints + j];
                if (w > 1e-5f) top.Add((j, w));
            }

            top.Sort((a, b) => b.w.CompareTo(a.w));
            if (top.Count == 0) top.Add((0, 1f));
            int k = Mathf.Min(MaxBonesPerVertex, top.Count);
            float sum = 0;
            for (int i = 0; i < k; i++) sum += top[i].w;
            BoneWeight bw = new();
            for (int i = 0; i < k; i++)
            {
                float w = top[i].w / sum;
                switch (i)
                {
                    case 0: bw.boneIndex0 = top[i].bone; bw.weight0 = w; break;
                    case 1: bw.boneIndex1 = top[i].bone; bw.weight1 = w; break;
                    case 2: bw.boneIndex2 = top[i].bone; bw.weight2 = w; break;
                    default: bw.boneIndex3 = top[i].bone; bw.weight3 = w; break;
                }
            }

            weights[v] = bw;
        }

        mesh.boneWeights = weights;
    }

    public void SetFrame(int frameNumber)
    {
        frameNumber = Mathf.Clamp(frameNumber, 0, motion.FrameCount - 1);
        if (frameNumber == frame && blendTo < 0) return;
        frame = frameNumber;
        blendTo = -1;
        PoseBones(frame);
    }

    // sub-frame pose (Assets/Film slow motion): between blendFrom and blendTo at blendK; CurrentFrame is the nearer frame
    int blendFrom = -1, blendTo = -1;
    float blendK;

    /// <summary>true while the bones show a pose between two motion frames (SetFrameBlend)</summary>
    public bool Blended => blendTo >= 0;

    /// <summary>pose between two motion frames (film slow motion, HeadMovement.DriveExternally): root translation lerp,
    /// joint rotations slerp; CurrentFrame becomes the nearer frame (hair, face, shoes key on it) and PoseBones of that
    /// frame restores this blended pose (the hair's pre-roll ends on "the shown pose")</summary>
    public void SetFrameBlend(int f0, int f1, float k)
    {
        f0 = Mathf.Clamp(f0, 0, motion.FrameCount - 1);
        f1 = Mathf.Clamp(f1, 0, motion.FrameCount - 1);
        if (f1 == f0 || !(k > 1e-4f))
        {
            SetFrame(f0);
            return;
        }

        if (k >= 1f - 1e-4f)
        {
            SetFrame(f1);
            return;
        }

        if (blendTo == f1 && blendFrom == f0 && Mathf.Abs(blendK - k) < 1e-6f) return;
        frame = k < 0.5f ? f0 : f1;
        blendFrom = f0;
        blendTo = f1;
        blendK = k;
        PoseBlend();
    }

    void PoseBlend()
    {
        int o0 = blendFrom * SmplxData.Joints, o1 = blendTo * SmplxData.Joints;
        bones[0].localPosition = restRoot + Vector3.Lerp(motion.Transl[blendFrom], motion.Transl[blendTo], blendK);
        for (int j = 0; j < SmplxData.Joints; j++)
        {
            bones[j].localRotation = Quaternion.Slerp(motion.Rotations[o0 + j], motion.Rotations[o1 + j], blendK);
        }
    }

    /// <summary>pose the bones at a frame without changing CurrentFrame (hair pre-roll / catch-up reads earlier frames
    /// and then poses the shown frame again; while a sub-frame blend is shown, posing CurrentFrame restores the blend)</summary>
    public void PoseBones(int frameNumber)
    {
        frameNumber = Mathf.Clamp(frameNumber, 0, motion.FrameCount - 1);
        if (blendTo >= 0 && frameNumber == frame)
        {
            PoseBlend();
            return;
        }

        int o = frameNumber * SmplxData.Joints;
        bones[0].localPosition = restRoot + motion.Transl[frameNumber];
        for (int j = 0; j < SmplxData.Joints; j++)
        {
            bones[j].localRotation = motion.Rotations[o + j];
        }
    }

    /// <summary>a SMPL-X bone (e.g. 15 = head) - for attachments like the hair</summary>
    public Transform Bone(int joint) => bones[joint];

    public SmplxData.Motion Motion => motion;

    /// <summary>the avatars layer flag; the body draws when it is on and Opacity > 0</summary>
    public void SetVisible(bool visible)
    {
        layerVisible = visible;
        ApplyAppearance();
    }

    /// <summary>max distance (mm) between the driven bones and the exported joint positions at the current frame -
    /// a live check that rotations, hierarchy and coordinate conversion agree</summary>
    public float FkErrorMm()
    {
        if (frame < 0) return float.NaN;
        int o = frame * SmplxData.Joints;
        float max = 0;
        for (int j = 0; j < SmplxData.Joints; j++)
        {
            Vector3 expected = transform.TransformPoint(motion.JointPositions[o + j]);
            max = Mathf.Max(max, (bones[j].position - expected).magnitude);
        }

        return max * 1000f;
    }

    /// <summary>world position of a SMPL-X joint at the current frame (e.g. 15 = head)</summary>
    public Vector3 BonePosition(int joint) => bones[joint].position;

    /// <summary>posed vertex bounds from a CPU bake (for playtests: height, extent)</summary>
    public Bounds BakedBounds()
    {
        Mesh baked = new();
        skinned.BakeMesh(baked, true);
        Vector3[] v = baked.vertices;
        Destroy(baked);
        if (v.Length == 0) return default;
        Bounds b = new(transform.TransformPoint(v[0]), Vector3.zero);
        foreach (Vector3 p in v.Skip(1)) b.Encapsulate(transform.TransformPoint(p));
        return b;
    }

    void OnDestroy()
    {
        if (skinned != null && skinned.sharedMesh != null) Destroy(skinned.sharedMesh);
        if (depthMaterial != null) Destroy(depthMaterial);
        if (colourMaterial != null) Destroy(colourMaterial);
        foreach (Attachment a in attachments) DestroyPair(a);
        attachments.Clear();
        if (albedo != null) Destroy(albedo);
    }
}
