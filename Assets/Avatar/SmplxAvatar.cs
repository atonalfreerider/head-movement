using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// A dancer's shaped SMPL-X body as a runtime SkinnedMeshRenderer with 55 bones, built from the capture's skin
/// binary and driven per frame by the motion binary (SMPL-X local joint rotations). The math is SMPL-X linear
/// blend skinning: bones sit at the shaped rest joints with identity rest rotations, so bindpose_j = T(-J_j); the
/// root bone goes to J_0 + transl. Pose-corrective blendshapes are not applied (plain LBS).
/// Textured (skin version 2 + capture.json smplx_albedo): the mesh is split at the SMPL-X UV seams, carries the
/// unsplit rest normals, and uses Resources/HM_AvatarLit (opaque, one texture, main light + SH: Quest-friendly) with
/// the photoreal albedo; otherwise a translucent role-tinted URP Lit body (synthetic captures).
/// </summary>
public class SmplxAvatar : MonoBehaviour
{
    const int MaxBonesPerVertex = 4;

    SmplxData.Motion motion;
    Transform[] bones;
    Vector3 restRoot;
    SkinnedMeshRenderer skinned;
    Material material;
    Texture2D albedo;
    int frame = -1;

    public Role DancerRole { get; private set; }
    public int FrameCount => motion.FrameCount;
    public int BoneCount => bones.Length;
    public int VertexCount { get; private set; }
    public int CurrentFrame => frame;
    public bool Visible => skinned != null && skinned.enabled;
    public Bounds WorldBounds => skinned.bounds;
    public bool Textured => albedo != null;
    public int TextureSize => albedo != null ? albedo.width : 0;
    public string TextureFormatName => albedo != null ? albedo.format.ToString() : null;
    public int TriangleCount { get; private set; }

    /// <param name="albedoPath">optional photoreal albedo PNG on the SMPL-X UV layout (needs a seam-split skin)</param>
    public static SmplxAvatar Create(string skinPath, string motionPath, Role role, Transform parent = null,
        string albedoPath = null)
    {
        SmplxData.Skin skin = SmplxData.ReadSkin(skinPath);
        SmplxData.Motion motion = SmplxData.ReadMotion(motionPath);

        GameObject go = new($"{role} SMPL-X Avatar");
        if (parent != null) go.transform.SetParent(parent, false);
        SmplxAvatar avatar = go.AddComponent<SmplxAvatar>();
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

        Mesh mesh = new() { name = $"{role} SMPL-X", indexFormat = IndexFormat.UInt32 };
        mesh.vertices = skin.RestVertices;
        mesh.triangles = skin.Triangles;
        TriangleCount = skin.Triangles.Length / 3;
        if (skin.Textured) mesh.uv = skin.Uv;
        // the seam-split mesh carries the unsplit normals; recalculating would crease every UV seam
        if (skin.Normals != null) mesh.normals = skin.Normals;
        else mesh.RecalculateNormals();
        SetBoneWeights(mesh, skin.Weights, skin.RestVertices.Length);
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
        if (skin.Textured && !string.IsNullOrEmpty(albedoPath) && System.IO.File.Exists(albedoPath))
        {
            albedo = LoadTexture(albedoPath, $"{role} albedo");
            material = TexturedMaterial(role, albedo);
        }

        if (material == null)
        {
            if (!string.IsNullOrEmpty(albedoPath) && !skin.Textured)
            {
                Debug.LogWarning($"{role}: albedo given but the skin has no UVs (re-export with textures)");
            }

            material = NewMaterial(role);
        }

        skinned.sharedMaterial = material;
    }

    /// <summary>PNG -> sRGB texture with mipmaps, block-compressed on the device (DXT/BC on desktop, ETC2/ASTC on
    /// Quest) and released from CPU memory</summary>
    public static Texture2D LoadTexture(string path, string name)
    {
        Texture2D tex = new(2, 2, TextureFormat.RGBA32, true, false) { name = name };
        if (!tex.LoadImage(System.IO.File.ReadAllBytes(path), false))
        {
            Object.Destroy(tex);
            throw new System.IO.InvalidDataException($"{path}: not a PNG/JPEG");
        }

        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 2;
        if (tex.width % 4 == 0 && tex.height % 4 == 0) tex.Compress(true);
        tex.Apply(true, true);
        return tex;
    }

    static Material TexturedMaterial(Role role, Texture2D tex)
    {
        Shader shader = Resources.Load<Shader>("HM_AvatarLit");
        if (shader == null) shader = Shader.Find("HeadMovement/AvatarLit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
        Material mat = new(shader) { name = $"{role} avatar (textured)" };
        mat.SetTexture("_BaseMap", tex);
        mat.SetColor("_BaseColor", Color.white);
        mat.renderQueue = (int)RenderQueue.Geometry;
        return mat;
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

    /// <summary>simple lit material, tinted per role (lead warm, follow cool), slightly transparent so the glowing
    /// skeleton stays readable inside the body</summary>
    static Material NewMaterial(Role role)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        Material mat = new(shader) { name = $"{role} avatar" };
        Color tint = role == Role.Lead ? new Color(0.78f, 0.36f, 0.26f, 0.62f) : new Color(0.62f, 0.72f, 0.92f, 0.62f);
        mat.SetColor("_BaseColor", tint);
        mat.SetColor("_Color", tint);
        mat.SetFloat("_Smoothness", 0.35f);
        mat.SetFloat("_Metallic", 0f);
        // URP Lit transparent (alpha blend), keeping depth writes so the body sorts against itself
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend", 0f);
        mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite", 1f);
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.SetShaderPassEnabled("DepthOnly", false);
        mat.SetShaderPassEnabled("ShadowCaster", true);
        mat.renderQueue = (int)RenderQueue.Transparent - 10; // before the additive glow lines
        return mat;
    }

    public void SetFrame(int frameNumber)
    {
        frameNumber = Mathf.Clamp(frameNumber, 0, motion.FrameCount - 1);
        if (frameNumber == frame) return;
        frame = frameNumber;
        PoseBones(frame);
    }

    /// <summary>pose the bones at a frame without changing CurrentFrame (hair pre-roll / catch-up reads earlier frames
    /// and then poses the shown frame again)</summary>
    public void PoseBones(int frameNumber)
    {
        frameNumber = Mathf.Clamp(frameNumber, 0, motion.FrameCount - 1);
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

    public void SetVisible(bool visible)
    {
        if (skinned != null) skinned.enabled = visible;
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
        if (material != null) Destroy(material);
        if (albedo != null) Destroy(albedo);
    }
}
