using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Gaussians.Core;
using Gaussians.ThreeD;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace HmSplats
{
    /// <summary>
    /// The Quest's LOWER-RESOLUTION splat layer (2026-10-21; measured on a Quest 3: the splat dancers are fill bound - 2 x 160,000
    /// Gaussians at the floor edge cost ~190 ms of GPU per frame with the package's composite, half of it from the eye buffer's
    /// 4x MSAA that the package's accumulation texture inherits). Used INSTEAD of the package's GaussiansURPFeature when
    /// SplatUrpFeature.LowResScale &lt; 1 (quest_settings.json "splat_scale", -hmsplatscale): the same three steps as the package's
    /// composite path (net.kleinbeck.gaussians 0.4.0 GaussianSplat3DURPPasses: prepare = view data + sort, accumulate, composite),
    /// but the accumulation goes into a texture of its own at <c>LowResScale</c> of the eye buffer per side, 1x MSAA, RGBA16F,
    /// cleared to transparent, with a low-res depth attachment of its own (cleared to far: the splats' ZTest LEqual always has a
    /// depth buffer, as in the package's pass, which binds the camera depth), and the composite samples it bilinearly into the
    /// camera colour (our HM_SplatComposite pass 1: premultiplied, gamma to linear, the fade opacity). The camera is NOT forced to
    /// an intermediate texture (the package forces one because its accumulation shares the camera depth), so on the Quest the
    /// composite writes the XR eye texture directly.
    ///
    /// ORIENTATION (fixed 2026-10-21 after the headset showed the low-res splats upside down over an upright floor): the renderer's
    /// view data always uses the render-texture projection (GL.GetGPUProjectionMatrix(p, true)), so the low-res layer holds the image
    /// in Unity's texture convention (UV origin bottom-left). The package's composite copies in memory order (its vert flips the uv
    /// under UNITY_UV_STARTS_AT_TOP, and Blitter's fullscreen triangle undoes it), which is right only when the target has the same
    /// origin - the package's forced intermediate. The eye texture (Vulkan, D3D: the back buffer) has a TOP-left origin, so the
    /// copy came out mirrored vertically. The composite now asks the render graph for the target's UV origin
    /// (RasterGraphContext.GetTextureUVOrigin, the test URP's own final blit uses) and passes a y-flipped _BlitScaleBias when it
    /// differs from the layer's: upright into the eye texture (Vulkan) and into a D3D12 back buffer, unchanged into an
    /// intermediate texture (the PC editor's camera, which renders through one). OpenGL-like APIs report bottom-left for both.
    ///
    /// DEPTH (<c>SplatUrpFeature.LowResDepth</c>, "splat_depth" on): after the colour, the renderer's SplatDepth pass (approximate
    /// splat-centre depth) writes the low-res depth, and the composite (HM_SplatComposite pass 2) outputs it as SV_Depth with ZTest
    /// LEqual / ZWrite On against the camera depth: splats behind nearer scene geometry are hidden, and the overlays drawn after it
    /// (floor marks, the contact disc, the transparent queue) depth-test against the dancers as on the package's composite path.
    /// Without it the composite is ZTest Always with no depth and the floor marks draw over the feet.
    ///
    /// The view data is prepared for the low-res size (the renderer's pixel-size terms, the 0.3 px^2 low-pass, follow it). The
    /// package's internals (the render system, its camera selection and draw list) are reached by reflection; Status says whether
    /// they were found. Added to the URP renderer at run time only (SplatUrpFeature.Ensure / Remove).
    /// </summary>
    public sealed class SplatLowResFeature : ScriptableRendererFeature
    {
        LowResPass pass;

        /// <summary>"ok ..." or why the pass did not draw (logcat HM-SPLATS status)</summary>
        public static string Status { get; internal set; } = "not run";

        /// <summary>the low-res layer of the last frame (width x height per eye)</summary>
        public static Vector2Int LastSize { get; internal set; }

        /// <summary>the composite's last orientation decision: "flip (target top-left)" / "no flip (target bottom-left)" (Status)</summary>
        public static string LastOrientation { get; private set; } = "-";

        static readonly int LowResDepthId = Shader.PropertyToID("_HmSplatLowResDepth");

        public override void Create()
        {
            pass = new LowResPass { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            Camera cam = renderingData.cameraData.camera;
            if (cam == null || cam.cameraType == CameraType.Preview) return;
            pass.requiresIntermediateTexture = false;
            renderer.EnqueuePass(pass);
        }

        // ------------------------------------------------------------------------------------------------ reflection bridge
        static class Bridge
        {
            internal static readonly Type System = typeof(GaussiansGroup).Assembly.GetType("Gaussians.ThreeD.GaussianSplat3DRenderSystem");
            internal static readonly PropertyInfo Instance = System?.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            internal static readonly MethodInfo Collect = System?.GetMethod("CollectForCamera", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            internal static readonly MethodInfo Prepare = System?.GetMethod("PrepareCompositeSplats", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            internal static readonly MethodInfo Gather = System?.GetMethod("GatherCompositeDraws", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            static readonly Type SelectionType = System?.GetNestedType("CameraSelection", BindingFlags.Public | BindingFlags.NonPublic);
            internal static readonly PropertyInfo HasSplats = SelectionType?.GetProperty("HasSplats", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            static readonly Type DrawType = System?.GetNestedType("SplatDraw", BindingFlags.Public | BindingFlags.NonPublic);
            internal static readonly FieldInfo Indices = Field("Indices"), IndirectArgs = Field("IndirectArgs"), Matrix = Field("Matrix"),
                Material = Field("Material"), Properties = Field("Properties"), IndexCount = Field("IndexCount"), Count = Field("Count");

            static FieldInfo Field(string n) => DrawType?.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            internal static string Missing()
            {
                List<string> m = new();
                if (Instance == null) m.Add("render system");
                if (Collect == null) m.Add("CollectForCamera");
                if (Prepare == null) m.Add("PrepareCompositeSplats");
                if (Gather == null) m.Add("GatherCompositeDraws");
                if (HasSplats == null) m.Add("CameraSelection.HasSplats");
                if (Indices == null || IndirectArgs == null || Matrix == null || Material == null || Properties == null || IndexCount == null || Count == null) m.Add("SplatDraw fields");
                return m.Count == 0 ? null : string.Join(", ", m);
            }
        }

        static readonly string missing = Bridge.Missing();

        struct Draw
        {
            internal GraphicsBuffer Indices, IndirectArgs;
            internal Matrix4x4 Matrix;
            internal Material Material;
            internal MaterialPropertyBlock Properties;
            internal int IndexCount, Count;
        }

        static GaussianSplatCameraState Capture(UniversalCameraData camera, float scale)
        {
            bool xr = camera.xr != null && camera.xr.enabled;
            int viewCount = xr ? camera.xr.viewCount : 1;
            Vector2 size = xr ? camera.xr.GetViewport(0).size : new Vector2(camera.cameraTargetDescriptor.width, camera.cameraTargetDescriptor.height);
            Vector2 rightSize = viewCount > 1 ? camera.xr.GetViewport(1).size : size;
            Matrix4x4 view = camera.GetViewMatrix();
            Matrix4x4 projection = camera.GetProjectionMatrix();
            return new GaussianSplatCameraState(view, projection,
                viewCount > 1 ? camera.GetViewMatrix(1) : view,
                viewCount > 1 ? camera.GetProjectionMatrix(1) : projection,
                Vector2Int.Max(Vector2Int.one, Vector2Int.RoundToInt(size * scale)), Vector2Int.Max(Vector2Int.one, Vector2Int.RoundToInt(rightSize * scale)), viewCount,
                indirectInstanceMultiplier: viewCount > 1 && !SystemInfo.supportsMultiview ? viewCount : 1);
        }

        sealed class LowResPass : ScriptableRenderPass
        {
            sealed class PrepareData
            {
                internal Camera Camera;
                internal GaussianSplatCameraState State;
            }

            sealed class AccumulateData
            {
                internal readonly List<Draw> Draws = new();
                internal bool Depth;
            }

            sealed class CompositeData
            {
                internal TextureHandle Source, Target;
                internal Material Material;
                internal int Pass;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (missing != null)
                {
                    Status = "package internals not found: " + missing;
                    return;
                }

                UniversalCameraData camera = frameData.Get<UniversalCameraData>();
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                float scale = Mathf.Clamp(SplatUrpFeature.LowResScale, 0.2f, 1f);
                GaussianSplatCameraState state = Capture(camera, scale);
                using GaussianSplatCameraState.Scope scope = state.Apply(camera.camera);
                object system = Bridge.Instance.GetValue(null);
                object selection = Bridge.Collect.Invoke(system, new object[] { camera.camera });
                if (selection == null || !(bool)Bridge.HasSplats.GetValue(selection)) return;
                object[] args = { selection, null, true };
                IList draws = Bridge.Gather.Invoke(system, args) as IList;
                Material composite = args[1] as Material;

                using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("HmSplats.LowRes.Prepare", out PrepareData data))
                {
                    data.Camera = camera.camera;
                    data.State = state;
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc(static (PrepareData p, UnsafeGraphContext context) =>
                    {
                        using GaussianSplatCameraState.Scope s = p.State.Apply(p.Camera);
                        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        object sys = Bridge.Instance.GetValue(null);
                        object sel = Bridge.Collect.Invoke(sys, new object[] { p.Camera });
                        Bridge.Prepare.Invoke(sys, new[] { sel, cmd });
                    });
                }

                if (draws == null || draws.Count == 0 || composite == null)
                {
                    Status = "no composite draws (is the group on the Composite path?)";
                    return;
                }

                RenderTextureDescriptor cd = camera.cameraTargetDescriptor;
                int w = Mathf.Max(1, Mathf.RoundToInt(cd.width * scale)), h = Mathf.Max(1, Mathf.RoundToInt(cd.height * scale));
                TextureDesc td = new(w, h)
                {
                    dimension = cd.dimension,
                    slices = cd.volumeDepth,
                    vrUsage = cd.vrUsage,
                    format = GraphicsFormat.R16G16B16A16_SFloat,
                    msaaSamples = MSAASamples.None,
                    clearBuffer = true,
                    clearColor = Color.clear,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    name = "_HmSplatLowRes"
                };
                TextureHandle acc = renderGraph.CreateTexture(td);
                // the layer's own depth (cleared to far): the splat colour pass tests LEqual like the package's (which binds the camera
                // depth), so a depth buffer is always bound; with LowResDepth the SplatDepth pass fills it and the composite reads it
                TextureDesc dd = new(w, h)
                {
                    dimension = cd.dimension,
                    slices = cd.volumeDepth,
                    vrUsage = cd.vrUsage,
                    format = GraphicsFormat.D32_SFloat,
                    msaaSamples = MSAASamples.None,
                    clearBuffer = true,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    name = "_HmSplatLowResDepth"
                };
                TextureHandle accDepth = renderGraph.CreateTexture(dd);
                bool depth = SplatUrpFeature.LowResDepth && resources.activeDepthTexture.IsValid();
                LastSize = new Vector2Int(w, h);

                using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("HmSplats.LowRes.Accumulate", out AccumulateData data))
                {
                    data.Draws.Clear();
                    data.Depth = depth;
                    foreach (object d in draws)
                    {
                        data.Draws.Add(new Draw
                        {
                            Indices = Bridge.Indices.GetValue(d) as GraphicsBuffer,
                            IndirectArgs = Bridge.IndirectArgs.GetValue(d) as GraphicsBuffer,
                            Matrix = (Matrix4x4)Bridge.Matrix.GetValue(d),
                            Material = Bridge.Material.GetValue(d) as Material,
                            Properties = Bridge.Properties.GetValue(d) as MaterialPropertyBlock,
                            IndexCount = (int)Bridge.IndexCount.GetValue(d),
                            Count = (int)Bridge.Count.GetValue(d)
                        });
                    }

                    builder.SetRenderAttachment(acc, 0, AccessFlags.Write);
                    builder.SetRenderAttachmentDepth(accDepth, AccessFlags.Write);
                    if (depth) builder.SetGlobalTextureAfterPass(accDepth, LowResDepthId);
                    builder.EnableFoveatedRasterization(false);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (AccumulateData p, RasterGraphContext context) =>
                    {
                        DrawAll(context.cmd, p.Draws, 0); // SplatColor: premultiplied over, ZWrite Off
                        if (p.Depth) DrawAll(context.cmd, p.Draws, 1); // SplatDepth: ColorMask 0, ZWrite On (after the colour: it must not reject it)
                    });
                }

                using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("HmSplats.LowRes.Composite", out CompositeData data))
                {
                    data.Source = acc;
                    data.Target = resources.activeColorTexture;
                    data.Material = composite;
                    data.Pass = depth ? 2 : 1;
                    builder.UseTexture(acc);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    if (depth)
                    {
                        builder.UseTexture(accDepth);
                        builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);
                    }

                    ConfigureFoveation(builder, camera, resources.isActiveTargetBackBuffer);
                    builder.SetRenderFunc(static (CompositeData p, RasterGraphContext context) =>
                    {
                        RTHandle source = p.Source;
                        Vector4 s = source.useScaling ? source.rtHandleProperties.rtHandleScale : Vector4.one;
                        // the layer is in texture order (bottom-left origin: the renderer's render-texture projection); flip when the
                        // target is not (the XR eye texture / a back buffer on Vulkan and D3D), as URP's final blit does
                        bool flip = context.GetTextureUVOrigin(in p.Source) != context.GetTextureUVOrigin(in p.Target);
                        LastOrientation = flip ? "flip (target top-left)" : "no flip (target bottom-left)";
                        Vector4 scaleBias = flip ? new Vector4(s.x, -s.y, 0f, s.y) : new Vector4(s.x, s.y, 0f, 0f);
                        Blitter.BlitTexture(context.cmd, source, scaleBias, p.Material, p.Pass);
                    });
                }

                if ((Time.frameCount & 31) == 0 || !Status.StartsWith("ok"))
                    Status = $"ok: {w}x{h} per eye ({scale:0.00} of {cd.width}x{cd.height}), {draws.Count} draw(s), {(camera.xr != null && camera.xr.enabled ? $"xr {camera.xr.viewCount} views" : "mono")}, " +
                             $"target {(resources.isActiveTargetBackBuffer ? "back buffer" : "intermediate")}, composite {LastOrientation}, {(depth ? "splat depth (low-res, depth-tested composite)" : "no splat depth")}";
            }

            static void DrawAll(RasterCommandBuffer cmd, List<Draw> draws, int pass)
            {
                foreach (Draw d in draws)
                {
                    if (d.Material == null || d.Indices == null) continue;
                    if (d.IndirectArgs != null)
                        cmd.DrawProceduralIndirect(d.Indices, d.Matrix, d.Material, pass, MeshTopology.Triangles, d.IndirectArgs, 20, d.Properties);
                    else
                        cmd.DrawProcedural(d.Indices, d.Matrix, d.Material, pass, MeshTopology.Triangles, d.IndexCount, d.Count, d.Properties);
                }
            }

            /// <summary>the package's foveation policy for the composite into the camera target (GaussiansURPUtilities.ConfigureFoveation)</summary>
            static void ConfigureFoveation(IRasterRenderGraphBuilder builder, UniversalCameraData camera, bool backBuffer)
            {
                if (camera.xr == null || !camera.xr.enabled || !camera.xr.supportsFoveatedRendering)
                {
                    builder.EnableFoveatedRasterization(false);
                    return;
                }

                bool mobileXR = Application.platform == RuntimePlatform.Android;
                bool ok = backBuffer || !mobileXR || Mathf.Approximately(XRSystem.GetRenderViewportScale(), 1f);
                builder.EnableFoveatedRasterization(ok);
            }
        }
    }
}
