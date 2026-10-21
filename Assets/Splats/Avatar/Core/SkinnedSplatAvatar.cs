using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Gaussians.ThreeD;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace HmSplats
{
    /// <summary>
    /// One dancer as skinned 3D Gaussians, rendered by the net.kleinbeck.gaussians 3DGS renderer (ninjamode
    /// Unity-VR-Gaussian-Splatting, MIT) through its deformation hook: this component owns a GaussianSplat3DData frame
    /// (FP32, renderer layout) and, once per rendered frame (RenderPipelineManager.beginContextRendering, after every
    /// Update/LateUpdate posed the avatar), runs HM_SkinSplats.compute with the 55 skin matrices of the avatar's current
    /// bones (SplatSkinning). The renderer borrows the frame (TryGetFrame) and sorts it with the partner's Gaussians in
    /// one GaussiansGroup (SplatDancers), so arms and hands in contact are ordered correctly.
    ///
    /// The canonical asset is created at run time from the StreamingAssets data (GaussianSplat3DAsset.InitializeFloat:
    /// the renderer needs one with the same count); while a deformation frame is bound the renderer grows its culling
    /// bounds to include the camera, so the moving dancer is never culled with stale bounds.
    ///
    /// Hair: when the data has a hair binding (records' strand / segment / local frame, SPLAT_AVATAR_FORMAT.md section 6)
    /// and a hair source is attached (SetHairSource: the viewer's simulated HairStrands guides), a second kernel
    /// (SkinHair) moves the bound hair Gaussians with their strand segments after the LBS pass; the per-segment frames are
    /// computed on the GPU too (kernel HairSegments from the uploaded simulated points: 2720 points, the CPU work per frame
    /// is reading them); SplatHairBinding is the CPU reference of both. Without a source, or with HairMotion off, the bound
    /// hair keeps its LBS.
    ///
    /// Not done here (see SplatDancers): the 65 %-transparent display mode. In a player (the Quest APK) the renderer's shaders and
    /// this compute shader come from the Resources shader set (SplatShaderSet).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SkinnedSplatAvatar : MonoBehaviour, IGaussianSplat3DDeformation
    {
        public const string ComputePath = "Assets/Splats/Avatar/Core/Shaders/HM_SkinSplats.compute";

        [StructLayout(LayoutKind.Sequential)]
        struct Canon
        {
            public float4 MeanOpacity, ScaleFlags, Rotation, Weights;
            public uint4 Joints;
        }

        public enum HairModes { Skinned = 0, Hidden = 1, Only = 2 }

        [StructLayout(LayoutKind.Sequential)]
        struct HairBindGpu
        {
            public uint4 Ids;
            public float4 AlongAW, AlongNAttach, PerpAK, PerpN, QHb;
        }

        public SplatAvatarData Data { get; private set; }
        public GaussianSplat3DRenderer Renderer { get; private set; }
        public int SkinnedFrames { get; private set; }
        public HairModes HairMode { get; set; } = HairModes.Skinned;
        public float OpacityScale { get; set; } = 1f;
        public Bounds LastPosedBounds { get; private set; }

        /// <summary>true: the deformation frame keeps one position revision, so the renderer sorts on its own cadence (the group's
        /// m_SortNthFrame) instead of after every skinned frame (sort amortisation; false = the package's rule, sort every frame)</summary>
        public static bool HoldPositionRevision { get; set; }

        /// <summary>the data's hair binding (null: none)</summary>
        public SplatHairBinding HairBinding { get; private set; }
        public ISplatHairSource HairSource { get; private set; }
        /// <summary>false: the bound hair keeps its LBS skinning (A/B: rigid LHM++ hair)</summary>
        public bool HairMotion { get; set; } = true;
        /// <summary>the strands drove the bound hair in the last skinned frame</summary>
        public bool HairLive { get; private set; }
        public string HairStatus { get; private set; } = "no binding";
        public int HairFrames { get; private set; }
        /// <summary>the shown head of the last hair pass (rotation / position of the splat skeleton's head)</summary>
        public quaternion HairHeadRot { get; private set; } = quaternion.identity;
        public float3 HairHeadPos { get; private set; }

        /// <summary>this frame's strand segments computed on the CPU from the same inputs as the GPU pass (SplatHairBinding
        /// .Segments: the CPU reference; tests, CLI checks; also sets the binding's face-box head)</summary>
        public float4[] HairSegmentsCpu()
        {
            if (HairBinding == null || hairSim == null) return null;
            float4[] seg = new float4[2 * HairBinding.Guides * HairBinding.SegmentsPerGuide];
            HairBinding.Segments(hairSim, hairSimHeadPos, hairSimHeadRot, HairHeadPos, HairHeadRot, seg);
            return seg;
        }

        Transform[] bones;
        GaussianSplat3DAsset asset;
        GaussianSplat3DData frame;
        GraphicsBuffer canonBuffer, skinBuffer, hairBindBuffer, hairSegBuffer, hairSimBuffer, hairRestTBuffer;
        float3[] hairSim, hairSimLast, hairRest;
        float4[] hairSim4;
        float3 hairSimHeadPos, lastSimHeadPos;
        quaternion hairSimHeadRot, lastSimHeadRot;
        int hairKernel = -1, segKernel = -1;
        bool appliedHairLive;
        TextAsset floatSplats, floatSh;
        ComputeShader compute;
        int kernel = -1;
        readonly Matrix4x4[] skin = new Matrix4x4[SplatAvatarData.Joints];
        readonly Matrix4x4[] lastSkin = new Matrix4x4[SplatAvatarData.Joints];
        uint positionRevision = 1, attributeRevision = 1;
        int skinnedAtFrame = -1;
        HairModes appliedHairMode = (HairModes)(-1);
        float appliedOpacityScale = -1f;
        bool hasLast;

        static readonly List<SkinnedSplatAvatar> Live = new();
        static bool hooked;

        static readonly int CanonId = Shader.PropertyToID("_Canon"), SkinId = Shader.PropertyToID("_Skin"),
            OutId = Shader.PropertyToID("_Out"), CountId = Shader.PropertyToID("_Count"),
            HairModeId = Shader.PropertyToID("_HairMode"), OpacityScaleId = Shader.PropertyToID("_OpacityScale"),
            HairBindId = Shader.PropertyToID("_HairBind"), HairSegId = Shader.PropertyToID("_HairSeg"),
            HairCountId = Shader.PropertyToID("_HairCount"), HeadRotId = Shader.PropertyToID("_HeadRot"),
            HeadPosId = Shader.PropertyToID("_HeadPos"), Face0Id = Shader.PropertyToID("_Face0"), Face1Id = Shader.PropertyToID("_Face1"),
            Face2Id = Shader.PropertyToID("_Face2"), HairSimId = Shader.PropertyToID("_HairSim"),
            HairRestTId = Shader.PropertyToID("_HairRestT"), HairSegOutId = Shader.PropertyToID("_HairSegOut"),
            SegDimsId = Shader.PropertyToID("_SegDims"), SimHeadPosId = Shader.PropertyToID("_SimHeadPos"),
            SimHeadRotId = Shader.PropertyToID("_SimHeadRot");

        /// <summary>build the GameObject (inactive until the caller activates it): renderer + this provider</summary>
        public static SkinnedSplatAvatar Create(SplatAvatarData data, Transform[] bones, Transform parent, string name, ComputeShader compute)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (bones == null || bones.Length != SplatAvatarData.Joints) throw new ArgumentException("55 SMPL-X bones expected", nameof(bones));
            if (compute == null) throw new ArgumentNullException(nameof(compute), $"skinning compute shader missing ({ComputePath})");
            if (!SystemInfo.supportsComputeShaders) throw new NotSupportedException("compute shaders are not supported on this device");

            GameObject go = new(name);
            go.SetActive(false);
            if (parent != null) go.transform.SetParent(parent, false);
            // the posed centres are WORLD positions: the renderer's object transform stays identity
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = Vector3.one;
            SkinnedSplatAvatar s = go.AddComponent<SkinnedSplatAvatar>();
            s.Build(data, bones, compute);
            return s;
        }

        void Build(SplatAvatarData data, Transform[] avatarBones, ComputeShader cs)
        {
            Data = data;
            bones = avatarBones;
            compute = cs;
            kernel = compute.FindKernel("Skin");
            int n = data.Count;

            // canonical inputs of the compute pass
            NativeArray<Canon> canon = new(n, Allocator.Temp);
            NativeArray<float4> splats = new(n * 3, Allocator.Temp);   // the canonical float source of the asset
            NativeArray<float> sh = new(n * 3, Allocator.Temp);
            for (int i = 0; i < n; i++)
            {
                float opacity = 1f / (1f + math.exp(-data.OpacityLogit[i]));
                float3 scale = math.exp(data.LogScale[i]);
                uint flags = data.Hair[i] != 0 ? 1u : 0u;
                canon[i] = new Canon
                {
                    MeanOpacity = new float4(data.Mean[i], opacity),
                    ScaleFlags = new float4(scale, math.asfloat(flags)),
                    Rotation = data.Rotation[i],
                    Weights = data.Weight[i],
                    Joints = (uint4)data.JointIndex[i]
                };
                splats[i * 3] = new float4(data.Mean[i], opacity);
                splats[i * 3 + 1] = new float4(scale, 0);
                splats[i * 3 + 2] = data.Rotation[i];
                sh[i * 3] = data.Sh0[i].x;
                sh[i * 3 + 1] = data.Sh0[i].y;
                sh[i * 3 + 2] = data.Sh0[i].z;
            }

            canonBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, Marshal.SizeOf<Canon>()) { name = $"{name} canonical" };
            canonBuffer.SetData(canon);
            skinBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, SplatAvatarData.Joints, 64) { name = $"{name} skin matrices" };

            frame = new GaussianSplat3DData(n, 1, GaussianSplatSHStorage.Float32);
            frame.Splats.SetData(splats);
            frame.SH.SetData(sh);

            floatSplats = new TextAsset(splats.Reinterpret<byte>(16).AsReadOnlySpan()) { name = $"{name} canonical splats" };
            floatSh = new TextAsset(sh.Reinterpret<byte>(4).AsReadOnlySpan()) { name = $"{name} canonical sh" };
            canon.Dispose();
            splats.Dispose();
            sh.Dispose();

            asset = ScriptableObject.CreateInstance<GaussianSplat3DAsset>();
            asset.name = $"{name} (runtime splat avatar)";
            asset.hideFlags = HideFlags.DontSave;
            Hash128 id = Hash128.Compute($"{data.SourcePath}|{n}|{DateTime.UtcNow.Ticks}");
            asset.InitializeFloat(n, 0, floatSplats, floatSh, data.CanonicalBounds, id);

            Renderer = gameObject.AddComponent<GaussianSplat3DRenderer>();
            // a player has no AssetDatabase: the renderer's own shader references (it refuses to start without all five, also
            // inside a group) come from the Resources shader set (SplatShaderSet; the Editor resolves them itself)
            SplatShaderSet shaders = SplatShaderSet.Get();
            if (shaders != null)
            {
                if (Renderer.m_ShaderSplats == null) Renderer.m_ShaderSplats = shaders.splats;
                if (Renderer.m_ShaderComposite == null) Renderer.m_ShaderComposite = shaders.composite;
                if (Renderer.m_ShaderDebugPoints == null) Renderer.m_ShaderDebugPoints = shaders.debugPoints;
                if (Renderer.m_ShaderDebugBoxes == null) Renderer.m_ShaderDebugBoxes = shaders.debugBoxes;
                if (Renderer.m_CSSplatUtilities == null) Renderer.m_CSSplatUtilities = shaders.utilities;
            }

            Renderer.m_Asset = asset;
            Renderer.m_SHOrder = 0;
            Renderer.SetDeformation(this);

            try
            {
                HairBinding = SplatHairBinding.FromData(data);
                if (HairBinding != null)
                {
                    hairKernel = compute.FindKernel("SkinHair");
                    segKernel = compute.FindKernel("HairSegments");
                    HairStatus = $"bound {HairBinding.Bound}, no hair source";
                }
            }
            catch (Exception ex)
            {
                HairBinding = null;
                HairStatus = "binding error: " + ex.Message;
                Debug.LogError($"{name}: hair binding not used: {ex.Message}");
            }
        }

        /// <summary>attach the simulated strands that drive the bound hair (null: detach; the hair keeps its LBS)</summary>
        public void SetHairSource(ISplatHairSource source)
        {
            if (ReferenceEquals(source, HairSource)) return;
            HairSource = source;
            hairRest = null;
            if (HairBinding == null) return;
            HairStatus = source == null ? $"bound {HairBinding.Bound}, no hair source" : $"bound {HairBinding.Bound}, source attached";
        }

        /// <summary>the strands can drive the hair this frame: binding prepared on the source's rest, frame read</summary>
        bool PrepareHair()
        {
            if (HairBinding == null || HairSource == null || !HairMotion || !HairSource.Ready) return false;
            SplatHairBinding hb = HairBinding;
            if (hairRest == null)
            {
                if (HairSource.Guides != hb.Guides || HairSource.Points != hb.Points)
                {
                    HairStatus = $"source has {HairSource.Guides} x {HairSource.Points} points, binding {hb.Guides} x {hb.Points}: not used";
                    return false;
                }

                hairRest = new float3[hb.Guides * hb.Points];
                HairSource.GetRest(hairRest);
                hb.Prepare(hairRest);
                if (HairSource.TryGetFaceBox(out float4 f0, out float4 f1, out float4 f2)) hb.SetFaceBox(f0, f1, f2);
                else hb.SetFaceBox(float4.zero, float4.zero, float4.zero);
                hairSim = new float3[hairRest.Length];
                hairSimLast = new float3[hairRest.Length];
                hairSim4 = new float4[hairRest.Length];
                int segs = hb.Guides * hb.SegmentsPerGuide;
                hairSegBuffer?.Dispose();
                hairSegBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2 * segs, 16) { name = $"{name} hair segments" };
                hairSimBuffer?.Dispose();
                hairSimBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, hairRest.Length, 16) { name = $"{name} hair points" };
                hairRestTBuffer?.Dispose();
                hairRestTBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, segs, 16) { name = $"{name} hair rest tangents" };
                float4[] rt = new float4[segs];
                for (int gs = 0; gs < segs; gs++) rt[gs] = new float4(hb.RestTangent(gs), 0f);
                hairRestTBuffer.SetData(rt);
                hairBindBuffer?.Dispose();
                hairBindBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, hb.Bound), Marshal.SizeOf<HairBindGpu>()) { name = $"{name} hair binding" };
                HairBindGpu[] g = new HairBindGpu[hb.Bound];
                for (int b = 0; b < hb.Bound; b++)
                {
                    g[b] = new HairBindGpu
                    {
                        Ids = new uint4((uint)hb.Index[b], (uint)hb.SegA[b], (uint)hb.SegN[b], 0),
                        AlongAW = new float4(hb.AlongA[b], hb.W[b]),
                        AlongNAttach = new float4(hb.AlongN[b], hb.Attach[b]),
                        PerpAK = new float4(hb.PerpA[b], hb.K[b]),
                        PerpN = new float4(hb.PerpN[b], hb.Side[b]),
                        QHb = hb.QHb[b]
                    };
                }

                hairBindBuffer.SetData(g);
                if (hb.RestDiffMaxMm > 1f) Debug.LogWarning($"{name}: the hair simulation's rest differs from the binding's by {hb.RestDiffMaxMm:0.0} mm (re-bind: dancecap mono4d.recap_splat_hair)");
            }

            HairSource.GetFrame(hairSim, out hairSimHeadPos, out hairSimHeadRot);
            return true;
        }

        public bool TryGetFrame(GaussianSplat3DAsset canonical, out GaussianSplat3DFrame f)
        {
            if (frame == null || canonical != asset)
            {
                f = default;
                return false;
            }

            // the renderer re-sorts whenever the position revision changes (every skinned frame); with HoldPositionRevision the
            // revision stays fixed and the group's Sort Nth Frame sets the cadence (sort amortisation, the Quest: SplatDancers
            // "splat_sort_every"); the view data still follows every frame (the attribute revision changes)
            f = new GaussianSplat3DFrame(frame, HoldPositionRevision ? 1u : positionRevision, attributeRevision);
            return true;
        }

        void OnEnable()
        {
            if (!Live.Contains(this)) Live.Add(this);
            if (!hooked)
            {
                RenderPipelineManager.beginContextRendering += SkinAll;
                hooked = true;
            }

            skinnedAtFrame = -1;
        }

        void OnDisable() => Live.Remove(this);

        static void SkinAll(ScriptableRenderContext context, List<Camera> cameras)
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                if (Live[i] == null) Live.RemoveAt(i);
                else Live[i].SkinNow();
            }
        }

        /// <summary>skin this frame's pose into the renderer's frame buffer (once per Time.frameCount; skipped when the
        /// pose, hair mode and opacity scale did not change, so a paused take keeps its sort)</summary>
        public void SkinNow(bool force = false)
        {
            if (frame == null || bones == null) return;
            if (!force && skinnedAtFrame == Time.frameCount) return;
            for (int j = 0; j < bones.Length; j++)
            {
                if (bones[j] == null) return; // the avatar was destroyed (capture change); SplatDancers rebuilds
            }

            skinnedAtFrame = Time.frameCount;
            SplatSkinning.SkinMatrices(bones, Data.RestJoints, Data.Parents, skin);
            bool hairLive = PrepareHair();
            bool same = hasLast && appliedHairMode == HairMode && Mathf.Approximately(appliedOpacityScale, OpacityScale) && appliedHairLive == hairLive;
            for (int j = 0; same && j < skin.Length; j++) same = skin[j] == lastSkin[j];
            if (same && hairLive)
            {
                same = math.all(hairSimHeadPos == lastSimHeadPos) && math.all(hairSimHeadRot.value == lastSimHeadRot.value);
                for (int p = 0; same && p < hairSim.Length; p++) same = math.all(hairSim[p] == hairSimLast[p]);
            }

            if (same && !force) return;

            Array.Copy(skin, lastSkin, skin.Length);
            hasLast = true;
            appliedHairMode = HairMode;
            appliedOpacityScale = OpacityScale;
            appliedHairLive = hairLive;
            skinBuffer.SetData(skin);
            compute.SetBuffer(kernel, CanonId, canonBuffer);
            compute.SetBuffer(kernel, SkinId, skinBuffer);
            compute.SetBuffer(kernel, OutId, frame.Splats);
            compute.SetInt(CountId, Data.Count);
            compute.SetInt(HairModeId, (int)HairMode);
            compute.SetFloat(OpacityScaleId, OpacityScale);
            compute.Dispatch(kernel, (Data.Count + 127) / 128, 1, 1);
            HairLive = hairLive;
            if (hairLive)
            {
                // the shown head = the splat skeleton's head: J_15 posed by S_15, the rotation of S_15
                Matrix4x4 s15 = skin[SplatHairBinding.HeadJoint];
                float3 headPos = s15.MultiplyPoint3x4((Vector3)Data.RestJoints[SplatHairBinding.HeadJoint]);
                quaternion headRot = math.normalize((quaternion)s15.rotation);
                HairHeadRot = headRot;
                HairHeadPos = headPos;
                Array.Copy(hairSim, hairSimLast, hairSim.Length);
                lastSimHeadPos = hairSimHeadPos;
                lastSimHeadRot = hairSimHeadRot;
                for (int p = 0; p < hairSim.Length; p++) hairSim4[p] = new float4(hairSim[p], 0f);
                hairSimBuffer.SetData(hairSim4);
                int segs = HairBinding.Guides * HairBinding.SegmentsPerGuide;
                compute.SetBuffer(segKernel, HairSimId, hairSimBuffer);
                compute.SetBuffer(segKernel, HairRestTId, hairRestTBuffer);
                compute.SetBuffer(segKernel, HairSegOutId, hairSegBuffer);
                compute.SetInts(SegDimsId, segs, HairBinding.SegmentsPerGuide, HairBinding.Points, 0);
                compute.SetVector(SimHeadPosId, new Vector4(hairSimHeadPos.x, hairSimHeadPos.y, hairSimHeadPos.z, 0));
                compute.SetVector(SimHeadRotId, (Vector4)hairSimHeadRot.value);
                compute.SetVector(HeadRotId, (Vector4)headRot.value);
                compute.SetVector(HeadPosId, new Vector4(headPos.x, headPos.y, headPos.z, 0));
                compute.Dispatch(segKernel, (segs + 63) / 64, 1, 1);
                compute.SetBuffer(hairKernel, CanonId, canonBuffer);
                compute.SetBuffer(hairKernel, SkinId, skinBuffer);
                compute.SetBuffer(hairKernel, OutId, frame.Splats);
                compute.SetBuffer(hairKernel, HairBindId, hairBindBuffer);
                compute.SetBuffer(hairKernel, HairSegId, hairSegBuffer);
                compute.SetInt(HairCountId, HairBinding.Bound);
                compute.SetVector(HeadRotId, (Vector4)headRot.value);
                compute.SetVector(HeadPosId, new Vector4(headPos.x, headPos.y, headPos.z, 0));
                compute.SetVector(Face0Id, (Vector4)HairBinding.Face0);
                compute.SetVector(Face1Id, (Vector4)HairBinding.Face1);
                compute.SetVector(Face2Id, HairBinding.FaceOn ? (Vector4)HairBinding.Face2 : Vector4.zero);
                compute.Dispatch(hairKernel, (HairBinding.Bound + 63) / 64, 1, 1);
                HairFrames++;
                HairStatus = $"live: {HairBinding.Bound} hair Gaussians on {HairBinding.Guides} strands (rest diff {HairBinding.RestDiffMaxMm:0.000} mm)";
            }
            else if (HairBinding != null && HairSource != null && HairStatus.StartsWith("live"))
            {
                HairStatus = HairMotion ? $"bound {HairBinding.Bound}, source not ready (LBS)" : $"bound {HairBinding.Bound}, hair motion off (LBS)";
            }

            positionRevision++;
            attributeRevision++;
            SkinnedFrames++;
            if ((SkinnedFrames & 15) == 1) LastPosedBounds = SplatSkinning.PosedBounds(Data, skin, 256);
        }

        /// <summary>the current skin matrices (tests, CLI checks)</summary>
        public Matrix4x4[] CurrentSkin => skin;

        /// <summary>read back posed Gaussians [first, first + count) from the GPU frame (tests, CLI checks; slow)</summary>
        public float4[] ReadBack(int first, int count)
        {
            float4[] all = new float4[count * 3];
            frame.Splats.GetData(all, 0, first * 3, count * 3);
            return all;
        }

        void OnDestroy() => Dispose();

        /// <summary>release the GPU buffers and the run-time asset (idempotent; OnDestroy runs only for objects that were
        /// once active, so callers that destroy a never-shown dancer call this first)</summary>
        public void Dispose()
        {
            if (Renderer != null) Renderer.SetDeformation(null);
            Live.Remove(this);
            canonBuffer?.Dispose();
            skinBuffer?.Dispose();
            hairBindBuffer?.Dispose();
            hairSegBuffer?.Dispose();
            hairSimBuffer?.Dispose();
            hairRestTBuffer?.Dispose();
            frame?.Dispose();
            canonBuffer = skinBuffer = hairBindBuffer = hairSegBuffer = hairSimBuffer = hairRestTBuffer = null;
            HairSource = null;
            frame = null;
            Release(asset);
            Release(floatSplats);
            Release(floatSh);
            asset = null;
            floatSplats = floatSh = null;
        }

        static void Release(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
