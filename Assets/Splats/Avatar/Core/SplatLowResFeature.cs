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
    /// cleared to transparent, with no depth attachment (the splats are not depth-tested against the scene: the dancers have
    /// nothing opaque in front of them in the lesson scene), and the composite samples it bilinearly into the camera colour (our
    /// HM_SplatComposite pass 1: premultiplied, gamma to linear, the fade opacity). The camera is NOT forced to an intermediate
    /// texture (the package forces one because its accumulation shares the camera depth). No splat depth is written in this mode:
    /// floor marks and overlays draw over the dancers (the package's depth pass is full resolution and costs ~25 % of the splat
    /// time). The view data is prepared for the low-res size (the renderer's pixel-size terms, the 0.3 px^2 low-pass, follow it).
    /// The package's internals (the render system, its camera selection and draw list) are reached by reflection; Status says
    /// whether they were found. Added to the URP renderer at run time only (SplatUrpFeature.Ensure / Remove).
    /// </summary>
    public sealed class SplatLowResFeature : ScriptableRendererFeature
    {
        LowResPass pass;

        /// <summary>"ok ..." or why the pass did not draw (logcat HM-SPLATS status)</summary>
        public static string Status { get; internal set; } = "not run";

        /// <summary>the low-res layer of the last frame (width x height per eye)</summary>
        public static Vector2Int LastSize { get; internal set; }

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
            }

            sealed class CompositeData
            {
                internal TextureHandle Source;
                internal Material Material;
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
                LastSize = new Vector2Int(w, h);

                using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("HmSplats.LowRes.Accumulate", out AccumulateData data))
                {
                    data.Draws.Clear();
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
                    builder.EnableFoveatedRasterization(false);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (AccumulateData p, RasterGraphContext context) =>
                    {
                        foreach (Draw d in p.Draws)
                        {
                            if (d.Material == null || d.Indices == null) continue;
                            if (d.IndirectArgs != null)
                                context.cmd.DrawProceduralIndirect(d.Indices, d.Matrix, d.Material, 0, MeshTopology.Triangles, d.IndirectArgs, 20, d.Properties);
                            else
                                context.cmd.DrawProcedural(d.Indices, d.Matrix, d.Material, 0, MeshTopology.Triangles, d.IndexCount, d.Count, d.Properties);
                        }
                    });
                }

                using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("HmSplats.LowRes.Composite", out CompositeData data))
                {
                    data.Source = acc;
                    data.Material = composite;
                    builder.UseTexture(acc);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    ConfigureFoveation(builder, camera, resources.isActiveTargetBackBuffer);
                    builder.SetRenderFunc(static (CompositeData p, RasterGraphContext context) =>
                    {
                        RTHandle source = p.Source;
                        Vector4 s = source.useScaling ? source.rtHandleProperties.rtHandleScale : Vector4.one;
                        Blitter.BlitTexture(context.cmd, source, new Vector4(s.x, s.y, 0, 0), p.Material, 1);
                    });
                }

                if ((Time.frameCount & 31) == 0 || !Status.StartsWith("ok"))
                    Status = $"ok: {w}x{h} per eye ({scale:0.00} of {cd.width}x{cd.height}), {draws.Count} draw(s), {(camera.xr != null && camera.xr.enabled ? $"xr {camera.xr.viewCount} views" : "mono")}, no splat depth";
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
