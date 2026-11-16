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
    ///
    /// SPLIT (2026-11 GPU audit; <c>SplatUrpFeature.LowResSplit</c>, "splat_split", default on): the prepare and the accumulation run
    /// BEFORE the prepasses (into the layer's own targets) and only the composite stays before the transparents, the layer's handles
    /// handed over in a ContextItem (LowResFrame) of the camera's frame data. With all three before the transparents the accumulation's
    /// own targets ended the eye buffer's native render pass in the middle: its colour and depth were stored and loaded back (~73 MB
    /// per frame on a Quest). Not BeforeRenderingOpaques: URP records the XR occlusion mesh into the eye colour + depth after the
    /// prepasses and before that event, so the accumulation there would split the eye buffer's render pass the same way.
    /// Off = the three steps together (as before; the bench's A/B).
    ///
    /// SCISSOR: the accumulation and the composite are scissored to the screen rect of the dancers (SkinnedSplatAvatar
    /// .TryLiveJointBounds: their posed joints this frame, padded by <c>ScissorPadMetres</c>), the union over both eyes (one rect serves
    /// single-pass instanced / multiview); a dancer pair a few metres away covers a small part of the eye buffer, and the full-screen
    /// composite (5 texture taps, discard, SV_Depth) was paid for every pixel. The rect is also mirrored about the horizontal centre, so
    /// it is right whichever vertical origin the target's scissor uses (top-left eye texture or bottom-left render texture). Nothing is
    /// accumulated or composited when the dancers are in no view; a camera inside the box draws the whole screen.
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

        /// <summary>the last frame's scissor: the fraction of the eye buffer the composite covered (1 = whole screen, 0 = off screen)</summary>
        public static float LastCoverage { get; private set; } = 1f;

        /// <summary>false: no scissor (the whole layer and screen, as before 2026-11; A/B)</summary>
        public static bool Scissor = true;

        /// <summary>metres added around the posed joints for the Gaussians that reach past them (hands past the wrist, the head top, long
        /// hair swinging, skirts)</summary>
        public const float ScissorPadMetres = 0.5f;

        static readonly int LowResDepthId = Shader.PropertyToID("_HmSplatLowResDepth");

        LowResPass accumulatePass, compositePass;

        public override void Create()
        {
            pass = new LowResPass(LowResPass.Mode.Full) { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
            accumulatePass = new LowResPass(LowResPass.Mode.Accumulate) { renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses };
            compositePass = new LowResPass(LowResPass.Mode.Composite) { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            Camera cam = renderingData.cameraData.camera;
            if (cam == null || cam.cameraType == CameraType.Preview) return;
            if (SplatUrpFeature.LowResSplit)
            {
                accumulatePass.requiresIntermediateTexture = false;
                compositePass.requiresIntermediateTexture = false;
                renderer.EnqueuePass(accumulatePass);
                renderer.EnqueuePass(compositePass);
                return;
            }

            pass.requiresIntermediateTexture = false;
            renderer.EnqueuePass(pass);
        }

        /// <summary>the low-res layer of this camera's frame, handed from the accumulation to the composite (frame data: reset after
        /// every camera, so a composite without this frame's accumulation draws nothing)</summary>
        sealed class LowResFrame : ContextItem
        {
            internal bool Ready, Depth, UseScissor;
            internal TextureHandle Acc, AccDepth;
            internal Material Composite;
            internal Rect TargetScissor;

            public override void Reset()
            {
                Ready = Depth = UseScissor = false;
                Acc = TextureHandle.nullHandle;
                AccDepth = TextureHandle.nullHandle;
                Composite = null;
                TargetScissor = default;
            }
        }

        /// <summary>
        /// The dancers' screen rect as a fraction of the target (0..1 both ways), the union over every view of this camera, mirrored
        /// about the horizontal centre (see SCISSOR). Returns false when the dancers are in no view. <paramref name="full"/> is true
        /// when the rect is the whole screen (no scissor needed: scissor off, no live dancer, or a camera inside the padded box).
        /// </summary>
        static bool ScreenRect(UniversalCameraData camera, out Rect r, out bool full)
        {
            r = new Rect(0f, 0f, 1f, 1f);
            full = true;
            // a camera with a sub-rect viewport: the rect below is relative to the viewport, the scissor to the target - no scissor
            if (!Scissor || !camera.isDefaultViewport || !SkinnedSplatAvatar.TryLiveJointBounds(ScissorPadMetres, out Bounds b)) return true;
            bool xr = camera.xr != null && camera.xr.enabled;
            int views = xr ? Mathf.Max(1, camera.xr.viewCount) : 1;
            float x0 = 1f, y0 = 1f, x1 = 0f, y1 = 0f;
            Vector3 c = b.center, e = b.extents;
            for (int v = 0; v < views; v++)
            {
                Matrix4x4 vp = camera.GetProjectionMatrix(v) * camera.GetViewMatrix(v);
                float nx0 = float.MaxValue, ny0 = float.MaxValue, nx1 = float.MinValue, ny1 = float.MinValue;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 p = c + Vector3.Scale(e, new Vector3((k & 1) == 0 ? -1f : 1f, (k & 2) == 0 ? -1f : 1f, (k & 4) == 0 ? -1f : 1f));
                    Vector4 clip = vp * new Vector4(p.x, p.y, p.z, 1f);
                    if (clip.w <= 1e-3f) return true; // a corner at or behind the eye: the projection of the box is unbounded
                    float nx = clip.x / clip.w, ny = clip.y / clip.w;
                    nx0 = Mathf.Min(nx0, nx);
                    ny0 = Mathf.Min(ny0, ny);
                    nx1 = Mathf.Max(nx1, nx);
                    ny1 = Mathf.Max(ny1, ny);
                }

                if (nx1 < -1f || nx0 > 1f || ny1 < -1f || ny0 > 1f) continue; // not in this view
                x0 = Mathf.Min(x0, Mathf.Clamp01(nx0 * 0.5f + 0.5f));
                y0 = Mathf.Min(y0, Mathf.Clamp01(ny0 * 0.5f + 0.5f));
                x1 = Mathf.Max(x1, Mathf.Clamp01(nx1 * 0.5f + 0.5f));
                y1 = Mathf.Max(y1, Mathf.Clamp01(ny1 * 0.5f + 0.5f));
            }

            full = false;
            if (x1 <= x0 || y1 <= y0)
            {
                r = Rect.zero;
                return false;
            }

            float my0 = Mathf.Min(y0, 1f - y1), my1 = Mathf.Max(y1, 1f - y0);
            r = Rect.MinMaxRect(x0, my0, x1, my1);
            full = r.width >= 0.999f && r.height >= 0.999f;
            return true;
        }

        /// <summary>the fraction rect in pixels of a w x h target, grown by pad pixels (the bilinear reach, the depth dilation)</summary>
        static Rect Pixels(Rect r, int w, int h, int pad)
        {
            float px0 = Mathf.Max(0f, Mathf.Floor(r.xMin * w) - pad), py0 = Mathf.Max(0f, Mathf.Floor(r.yMin * h) - pad);
            float px1 = Mathf.Min(w, Mathf.Ceil(r.xMax * w) + pad), py1 = Mathf.Min(h, Mathf.Ceil(r.yMax * h) + pad);
            return Rect.MinMaxRect(px0, py0, px1, py1);
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
            /// <summary>Full: prepare, accumulate and composite in one pass (splat_split off); Accumulate: prepare + accumulate, the
            /// layer handed on in LowResFrame (before the prepasses); Composite: the composite of this frame's LowResFrame</summary>
            internal enum Mode
            {
                Full,
                Accumulate,
                Composite
            }

            readonly Mode mode;

            internal LowResPass(Mode m) => mode = m;

            sealed class PrepareData
            {
                internal Camera Camera;
                internal GaussianSplatCameraState State;
            }

            sealed class AccumulateData
            {
                internal readonly List<Draw> Draws = new();
                internal bool Depth, UseScissor;
                internal Rect Scissor;
            }

            sealed class CompositeData
            {
                internal TextureHandle Source, Target;
                internal Material Material;
                internal int Pass;
                internal bool UseScissor;
                internal Rect Scissor;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (missing != null)
                {
                    Status = "package internals not found: " + missing;
                    return;
                }

                if (mode != Mode.Composite) RecordAccumulate(renderGraph, frameData);
                if (mode != Mode.Accumulate) RecordComposite(renderGraph, frameData);
            }

            void RecordAccumulate(RenderGraph renderGraph, ContextContainer frameData)
            {
                LowResFrame frame = frameData.GetOrCreate<LowResFrame>();
                frame.Reset();
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
                string xrNote = camera.xr != null && camera.xr.enabled ? $"xr {camera.xr.viewCount} views" : "mono";
                if (!ScreenRect(camera, out Rect onScreen, out bool fullScreen))
                {
                    // the dancers are in no view: the prepare above keeps their sort going, nothing is drawn
                    LastCoverage = 0f;
                    if ((Time.frameCount & 31) == 0 || !Status.StartsWith("ok"))
                        Status = $"ok: off screen ({w}x{h} per eye, {draws.Count} draw(s), {xrNote}, {(SplatUrpFeature.LowResSplit ? "split" : "one pass")}): nothing accumulated or composited";
                    return;
                }

                LastCoverage = fullScreen ? 1f : onScreen.width * onScreen.height;
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
                    data.UseScissor = !fullScreen;
                    data.Scissor = Pixels(onScreen, w, h, 2);
                    builder.EnableFoveatedRasterization(false);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (AccumulateData p, RasterGraphContext context) =>
                    {
                        // the clear (the attachment's load action) is not scissored: outside the rect the layer stays transparent / far
                        if (p.UseScissor) context.cmd.EnableScissorRect(p.Scissor);
                        DrawAll(context.cmd, p.Draws, 0); // SplatColor: premultiplied over, ZWrite Off
                        if (p.Depth) DrawAll(context.cmd, p.Draws, 1); // SplatDepth: ColorMask 0, ZWrite On (after the colour: it must not reject it)
                        if (p.UseScissor) context.cmd.DisableScissorRect();
                    });
                }

                frame.Ready = true;
                frame.Acc = acc;
                frame.AccDepth = accDepth;
                frame.Depth = depth;
                frame.Composite = composite;
                frame.UseScissor = !fullScreen;
                frame.TargetScissor = Pixels(onScreen, cd.width, cd.height, Mathf.CeilToInt(3f / scale));
                if ((Time.frameCount & 31) == 0 || !Status.StartsWith("ok"))
                    Status = $"ok: {w}x{h} per eye ({scale:0.00} of {cd.width}x{cd.height}), {draws.Count} draw(s), {xrNote}, " +
                             $"target {(resources.isActiveTargetBackBuffer ? "back buffer" : "intermediate")}, composite {LastOrientation}, {(depth ? "splat depth (low-res, depth-tested composite)" : "no splat depth")}, " +
                             $"{(SplatUrpFeature.LowResSplit ? "split (accumulate before prepasses)" : "one pass")}, scissor {(fullScreen ? "whole screen" : $"{LastCoverage * 100f:0}% of the screen")}";
            }

            void RecordComposite(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!frameData.Contains<LowResFrame>()) return;
                LowResFrame frame = frameData.Get<LowResFrame>();
                if (!frame.Ready || frame.Composite == null || !frame.Acc.IsValid()) return;
                UniversalCameraData camera = frameData.Get<UniversalCameraData>();
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                bool depth = frame.Depth && resources.activeDepthTexture.IsValid() && frame.AccDepth.IsValid();
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("HmSplats.LowRes.Composite", out CompositeData data);
                data.Source = frame.Acc;
                data.Target = resources.activeColorTexture;
                data.Material = frame.Composite;
                data.Pass = depth ? 2 : 1;
                data.UseScissor = frame.UseScissor;
                data.Scissor = frame.TargetScissor;
                builder.UseTexture(frame.Acc);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                if (depth)
                {
                    builder.UseTexture(frame.AccDepth);
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
                    // the scissor is switched off again right after the blit: the passes merged into this native render pass
                    // (the transparents, the overlays) must not inherit it
                    if (p.UseScissor) context.cmd.EnableScissorRect(p.Scissor);
                    Blitter.BlitTexture(context.cmd, source, scaleBias, p.Material, p.Pass);
                    if (p.UseScissor) context.cmd.DisableScissorRect();
                });
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
