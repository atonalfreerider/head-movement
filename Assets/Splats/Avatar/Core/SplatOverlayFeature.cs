using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace HmSplats
{
    /// <summary>
    /// "Over the splat dancers": draws SplatUrpFeature.OverSplats again right after the Gaussian composite and its depth
    /// (BeforeRenderingTransparents + 1, after the package's two passes at BeforeRenderingTransparents), into the camera
    /// colour WITHOUT a depth attachment, so no depth test or write: the skeleton lines and the hand-contact lights that lie
    /// inside the opaque splat bodies show over them (as they show through the translucent mesh avatars and over the phones'
    /// video in the film). Each renderer is drawn with its own first material and that material's forward pass, so colours,
    /// widths, fades and the bloom afterwards are the viewer's own. The transparent queue (arrows, labels, floor marks) still
    /// comes after and still depth-tests against the splat depth. Added to the URP renderer at run time only, with the
    /// Gaussians feature (SplatUrpFeature.Ensure / Remove); never saved into Assets/Bloom_Renderer.asset.
    /// </summary>
    public sealed class SplatOverlayFeature : ScriptableRendererFeature
    {
        OverSplatsPass pass;

        /// <summary>renderers drawn by the last frame's pass (CLI state)</summary>
        public static int LastDrawn { get; internal set; }

        public override void Create()
        {
            pass = new OverSplatsPass { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents + 1 };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (SplatUrpFeature.OverSplats.Count == 0 && SplatUrpFeature.OverSplatsInstanced.Count == 0 && SplatUrpFeature.OverSplatsOnly.Count == 0) return;
            Camera cam = renderingData.cameraData.camera;
            if (cam == null || cam.cameraType is not (CameraType.Game or CameraType.SceneView)) return;
            if (SplatUrpFeature.SplatLayer >= 0 && (cam.cullingMask & (1 << SplatUrpFeature.SplatLayer)) == 0) return;
            renderer.EnqueuePass(pass);
        }

        sealed class OverSplatsPass : ScriptableRenderPass
        {
            sealed class PassData
            {
                internal readonly List<Renderer> Renderers = new();
                internal readonly List<Material> Materials = new();
                internal readonly List<int> Passes = new();
                internal readonly List<SplatUrpFeature.InstancedDraw> Instanced = new();
                internal readonly List<int> InstancedPasses = new();
                internal readonly List<int> InstancedCounts = new();
            }

            static readonly Dictionary<Shader, int> ForwardPass = new();
            static readonly ShaderTagId LightMode = new("LightMode");
            static readonly ShaderTagId[] Forward = { new("UniversalForward"), new("UniversalForwardOnly"), new("SRPDefaultUnlit") };

            /// <summary>the material's forward pass (UniversalForward / UniversalForwardOnly / SRPDefaultUnlit), else pass 0</summary>
            static int PassOf(Material m)
            {
                Shader s = m.shader;
                if (ForwardPass.TryGetValue(s, out int p)) return p;
                p = 0;
                for (int i = 0; i < s.passCount && p == 0; i++)
                {
                    ShaderTagId tag = s.FindPassTagValue(i, LightMode);
                    foreach (ShaderTagId f in Forward)
                    {
                        if (tag == f)
                        {
                            p = i;
                            break;
                        }
                    }
                }

                ForwardPass[s] = p;
                return p;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalCameraData camera = frameData.Get<UniversalCameraData>();
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                int mask = camera.camera.cullingMask;
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("HmSplats.OverSplats", out PassData data);
                data.Renderers.Clear();
                data.Materials.Clear();
                data.Passes.Clear();
                data.Instanced.Clear();
                data.InstancedPasses.Clear();
                data.InstancedCounts.Clear();
                foreach (Renderer r in SplatUrpFeature.OverSplats)
                {
                    if (r == null || !r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy) continue;
                    if ((mask & (1 << r.gameObject.layer)) == 0) continue;
                    if (r is LineRenderer line && (line.positionCount < 2 || line.widthMultiplier <= 1e-4f)) continue;
                    Material m = r.sharedMaterial;
                    if (m == null) continue;
                    data.Renderers.Add(r);
                    data.Materials.Add(m);
                    data.Passes.Add(PassOf(m));
                }

                foreach (Renderer r in SplatUrpFeature.OverSplatsOnly)
                {
                    // switched off for the regular loop on purpose (forceRenderingOff): only enabled / active count here
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    if ((mask & (1 << r.gameObject.layer)) == 0) continue;
                    Material m = r.sharedMaterial;
                    if (m == null) continue;
                    data.Renderers.Add(r);
                    data.Materials.Add(m);
                    data.Passes.Add(PassOf(m));
                }

                foreach (SplatUrpFeature.InstancedDraw d in SplatUrpFeature.OverSplatsInstanced)
                {
                    int n = d?.Count != null ? Mathf.Min(d.Count(), d.Matrices?.Length ?? 0) : 0;
                    if (n <= 0 || d.Mesh == null || d.Material == null) continue;
                    data.Instanced.Add(d);
                    data.InstancedPasses.Add(PassOf(d.Material));
                    data.InstancedCounts.Add(n);
                }

                LastDrawn = data.Renderers.Count + data.Instanced.Count;
                // the camera colour + a depth of our own, cleared to far: the lines' materials keep their LEqual test, which then
                // only sorts the lines among themselves; the camera depth (with the splat depth in it) is neither tested nor
                // written. (A colour-only pass is not enough: the render graph merges it into the previous native pass and the
                // camera depth stays bound - measured 2026-10-20, the lines stayed hidden inside the bodies.)
                TextureDesc colourDesc = renderGraph.GetTextureDesc(resources.activeColorTexture);
                TextureDesc depthDesc = colourDesc;
                depthDesc.name = "HmSplats.OverSplatsDepth";
                depthDesc.format = UnityEngine.Experimental.Rendering.GraphicsFormat.D32_SFloat;
                depthDesc.clearBuffer = true;
                depthDesc.clearColor = Color.black;
                depthDesc.bindTextureMS = false;
                depthDesc.enableRandomWrite = false;
                TextureHandle ownDepth = renderGraph.CreateTexture(depthDesc);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(ownDepth, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext context) =>
                {
                    for (int i = 0; i < d.Renderers.Count; i++)
                    {
                        if (d.Renderers[i] != null) context.cmd.DrawRenderer(d.Renderers[i], d.Materials[i], 0, d.Passes[i]);
                    }

                    for (int i = 0; i < d.Instanced.Count; i++)
                    {
                        SplatUrpFeature.InstancedDraw x = d.Instanced[i];
                        context.cmd.DrawMeshInstanced(x.Mesh, 0, x.Material, d.InstancedPasses[i], x.Matrices, d.InstancedCounts[i], x.Properties);
                    }
                });
            }
        }
    }
}
