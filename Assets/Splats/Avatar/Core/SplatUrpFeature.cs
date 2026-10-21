using System;
using System.Collections.Generic;
using System.Reflection;
using Gaussians;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HmSplats
{
    /// <summary>
    /// The Gaussian renderer's URP feature (net.kleinbeck.gaussians GaussiansURPFeature), added to the active URP
    /// renderers AT RUN TIME while splat dancers are on, and removed again when they go off, on Play-mode exit and before
    /// any asset save (Editor/SplatFeatureGuard). The project's renderer asset (Assets/Bloom_Renderer.asset, shared with
    /// the Quest build) is never changed on disk: the Quest build and its settings stay exactly as they were.
    /// </summary>
    public static class SplatUrpFeature
    {
        static GaussiansURPFeature feature;
        static SplatLowResFeature lowRes;
        static SplatOverlayFeature overlayFeature;

        /// <summary>the splat layer's resolution as a fraction of the eye buffer per side: 1 (default) = the package's own composite
        /// (GaussiansURPFeature, full resolution, the camera's MSAA, splat depth); below 1 = SplatLowResFeature instead (the Quest:
        /// quest_settings.json "splat_scale"). Takes effect at the next Ensure (SplatDancers builds the dancers after the tier).</summary>
        public static float LowResScale = 1f;

        /// <summary>the low-res layer is the one in the renderer now</summary>
        public static bool LowResActive => lowRes != null && Targets.Count > 0;
        static readonly List<ScriptableRendererData> Targets = new();
        static bool hooked;

        /// <summary>
        /// Renderers drawn AGAIN right after the splat composite and its depth, without a depth buffer: an x-ray of what lies
        /// inside the opaque splat bodies (the skeleton lines, the hand-contact lights), so they show over the dancers as they
        /// show through the 75 %-transparent mesh avatars and over the phones' video. Filled every frame by SplatDancers (only
        /// for dancers whose splats are shown); each renderer is drawn with its own first material (its forward pass), so the
        /// lines keep their colours, widths and fades. Empty = nothing drawn.
        /// </summary>
        public static readonly List<Renderer> OverSplats = new();

        /// <summary>renderers drawn ONLY by the over-the-splats pass this frame: their own draw is switched off
        /// (Renderer.forceRenderingOff, by SplatDancers) so an additive glow (the balance axes) is not drawn twice where it
        /// is in front of the bodies; the pass draws them even though forceRenderingOff is set</summary>
        public static readonly List<Renderer> OverSplatsOnly = new();

        /// <summary>an instanced draw over the splats (the follower's spine beads: SpineBeads draws them itself with
        /// Graphics.RenderMeshInstanced, so there is no Renderer to hand over): the same mesh, material, matrices and
        /// per-instance colours, read when the pass records (after the beads' own beginCameraRendering update)</summary>
        public sealed class InstancedDraw
        {
            public Mesh Mesh;
            public Material Material;
            public Matrix4x4[] Matrices;
            public Func<int> Count;
            public MaterialPropertyBlock Properties;
        }

        /// <summary>instanced draws over the splats this frame (filled by SplatDancers with OverSplats)</summary>
        public static readonly List<InstancedDraw> OverSplatsInstanced = new();

        /// <summary>the layer of the splat dancers: a camera draws the over-the-splats renderers only when its culling mask
        /// has this layer (and the renderer's)</summary>
        public static int SplatLayer = -1;

        /// <summary>
        /// true (SplatDancers' default): the renderer's splat-depth pass (approximate splat-centre depth, written only
        /// when the group's Write Depth is on) runs right after the splat composite, BEFORE URP draws the transparent
        /// queue, instead of after it (the package's choice, for XR reprojection). The floor marks, arrows and labels
        /// then depth-test against the dancers: a ring behind a foot is hidden by it, an arrow in front stays in front.
        /// Set through reflection on the package's internal pass (net.kleinbeck.gaussians 0.4.0
        /// GaussiansURPFeature.m_ThreeD.m_DepthPass); DepthPatch says whether it took.
        /// </summary>
        public static bool DepthBeforeTransparents = true;

        /// <summary>"applied" / "package default" / the reason the reflection failed</summary>
        public static string DepthPatch { get; private set; } = "not applied";

        static readonly FieldInfo ThreeDField = typeof(GaussiansURPFeature).GetField("m_ThreeD", BindingFlags.NonPublic | BindingFlags.Instance);

        public static bool Injected => (feature != null || lowRes != null) && Targets.Count > 0;

        /// <summary>the renderers the feature is in now, by name (CLI state)</summary>
        public static IEnumerable<string> TargetNames
        {
            get
            {
                foreach (ScriptableRendererData d in Targets)
                {
                    if (d != null) yield return d.name;
                }
            }
        }

        /// <summary>true when every renderer of the active URP asset has a Gaussians feature (ours or a saved one)</summary>
        public static bool Ensure()
        {
            if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset urp) return false;
            bool ok = true;
            bool low = LowResScale < 0.999f;
            foreach (ScriptableRendererData data in urp.rendererDataList)
            {
                if (data == null) continue;
                if (HasSavedFeature(data)) continue;
                if (!low && feature == null)
                {
                    feature = ScriptableObject.CreateInstance<GaussiansURPFeature>();
                    feature.name = "Gaussians (run time, HmSplats)";
                    feature.hideFlags = HideFlags.DontSave;
                    feature.SetActive(true);
                }

                if (low && lowRes == null)
                {
                    lowRes = ScriptableObject.CreateInstance<SplatLowResFeature>();
                    lowRes.name = "Gaussians low-res layer (run time, HmSplats)";
                    lowRes.hideFlags = HideFlags.DontSave;
                    lowRes.SetActive(true);
                }

                ScriptableRendererFeature main = low ? lowRes : feature;
                ScriptableRendererFeature other = low ? feature : lowRes;

                if (overlayFeature == null)
                {
                    overlayFeature = ScriptableObject.CreateInstance<SplatOverlayFeature>();
                    overlayFeature.name = "Over the splat dancers (run time, HmSplats)";
                    overlayFeature.hideFlags = HideFlags.DontSave;
                    overlayFeature.SetActive(true);
                }

                bool dirty = false;
                if (other != null && data.rendererFeatures.Remove(other)) dirty = true; // the mode changed (scale 1 <-> below 1)
                if (!data.rendererFeatures.Contains(main))
                {
                    data.rendererFeatures.Add(main);
                    dirty = true;
                }

                if (!data.rendererFeatures.Contains(overlayFeature))
                {
                    data.rendererFeatures.Add(overlayFeature);
                    dirty = true;
                }

                if (dirty)
                {
                    data.SetDirty(); // URP rebuilds this renderer (with the features) before the next camera
                    if (!Targets.Contains(data)) Targets.Add(data);
                }

                ok &= data.rendererFeatures.Contains(main);
            }

            if (!hooked)
            {
                RenderPipelineManager.beginCameraRendering += PatchDepthPass;
                hooked = true;
            }

            return ok;
        }

        /// <summary>URP re-creates the feature's passes whenever it rebuilds the renderer: re-apply the event every camera</summary>
        static void PatchDepthPass(ScriptableRenderContext context, Camera camera)
        {
            if (feature == null) return;
            try
            {
                object passes = ThreeDField?.GetValue(feature);
                FieldInfo depthField = passes?.GetType().GetField("m_DepthPass", BindingFlags.NonPublic | BindingFlags.Instance);
                if (depthField?.GetValue(passes) is not ScriptableRenderPass depth)
                {
                    DepthPatch = passes == null ? "feature passes not created yet" : "m_DepthPass not found (package changed?)";
                    return;
                }

                RenderPassEvent want = DepthBeforeTransparents ? RenderPassEvent.BeforeRenderingTransparents : RenderPassEvent.AfterRenderingTransparents;
                if (depth.renderPassEvent != want) depth.renderPassEvent = want;
                DepthPatch = DepthBeforeTransparents ? "applied: splat depth before transparents" : "package default: after transparents";
            }
            catch (Exception e)
            {
                DepthPatch = "failed: " + e.Message;
            }
        }

        /// <summary>take the run-time feature out of every renderer it was added to</summary>
        public static void Remove()
        {
            foreach (ScriptableRendererData data in Targets)
            {
                if (data == null) continue;
                bool removed = data.rendererFeatures.Remove(feature);
                if (lowRes != null) removed |= data.rendererFeatures.Remove(lowRes);
                if (overlayFeature != null) removed |= data.rendererFeatures.Remove(overlayFeature);
                if (removed) data.SetDirty();
            }

            OverSplats.Clear();
            OverSplatsInstanced.Clear();
            OverSplatsOnly.Clear();
            if (overlayFeature != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(overlayFeature);
                else UnityEngine.Object.DestroyImmediate(overlayFeature);
            }

            overlayFeature = null;

            Targets.Clear();
            if (hooked)
            {
                RenderPipelineManager.beginCameraRendering -= PatchDepthPass;
                hooked = false;
            }

            if (feature != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(feature);
                else UnityEngine.Object.DestroyImmediate(feature);
            }

            feature = null;
            if (lowRes != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(lowRes);
                else UnityEngine.Object.DestroyImmediate(lowRes);
            }

            lowRes = null;
        }

        static bool HasSavedFeature(ScriptableRendererData data)
        {
            foreach (ScriptableRendererFeature f in data.rendererFeatures)
            {
                if (f is GaussiansURPFeature && f != feature) return true;
            }

            return false;
        }
    }
}
