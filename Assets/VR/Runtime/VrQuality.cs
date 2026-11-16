using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR.Features.Meta;

namespace HmVr
{
    /// <summary>
    /// The render settings of the VR mode. Every tier: no post effects on the head camera (the camera sets that itself), no V-sync fight with
    /// the runtime, fixed foveation where the display supports it.
    ///
    /// Tier Quest (the default on Android, or -hmvr-tier quest / HM_VR_TIER=quest to try it on a PC; docs/QUEST.md): for the Quest's
    /// mobile GPU - no HDR, no shadows, no extra lights, MSAA 4x on a Quest 3 (hair alpha-to-coverage needs it; 2x on a Quest 2), the
    /// intermediate colour texture only where something needs it (Meta's passthrough advice), the eye buffer at 1.0 (0.9 on a Quest 2), the
    /// refresh rate asked for from the runtime (72 Hz), and the HAIR kept on: HairLevel Full / Light / Off (VrSettings, the menu button, the
    /// adaptive step-down when a lesson keeps missing the frame budget). It changes the pipeline asset at run time, so every value is restored
    /// when VR mode ends. Tier Pc touches nothing.
    /// </summary>
    public static class VrQuality
    {
        public enum Tier
        {
            Pc,
            Quest
        }

        public static Tier Current { get; private set; } = Tier.Pc;
        public static bool Applied => applied;
        public static HairLevel Hair { get; private set; } = HairLevel.Full;
        public static string Notes { get; private set; } = "";
        public static float RefreshHz { get; private set; }
        public static int HairSteps { get; private set; }

        static bool applied, hairHooked;
        static int vsync, msaa;
        static int targetFps;
        static float renderScale, shadowDistance, eyeScale;
        static bool hdr;
        static int extraLights;
        static UniversalRenderPipelineAsset asset;
        static object intermediateOwner;
        static object intermediateBefore;
        static PropertyInfo intermediateProp;

        public static Tier DefaultTier()
        {
            string want = Environment.GetEnvironmentVariable("HM_VR_TIER");
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-hmvr-tier", StringComparison.OrdinalIgnoreCase)) want = args[i + 1];
            }

            if (!string.IsNullOrEmpty(want)) return string.Equals(want, "quest", StringComparison.OrdinalIgnoreCase) ? Tier.Quest : Tier.Pc;
            return Application.platform == RuntimePlatform.Android ? Tier.Quest : Tier.Pc;
        }

        public static void Apply(bool xr, Tier? tier = null)
        {
            if (applied) Restore();
            applied = true;
            Current = tier ?? DefaultTier();
            VrSettings settings = VrSettings.Load();
            vsync = QualitySettings.vSyncCount;
            targetFps = Application.targetFrameRate;
            Notes = "";
            if (xr)
            {
                QualitySettings.vSyncCount = 0; // the runtime paces the frames
                Application.targetFrameRate = -1;
                ForDisplays(d =>
                {
                    d.foveatedRenderingLevel = 1f; // fixed foveation where the runtime supports it (Quest); ignored elsewhere
                });
            }

            if (Current != Tier.Quest) return;
            bool q3 = VrDevice.Quest3Class;
            asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset != null)
            {
                msaa = asset.msaaSampleCount;
                hdr = asset.supportsHDR;
                renderScale = asset.renderScale;
                shadowDistance = asset.shadowDistance;
                extraLights = asset.maxAdditionalLightsCount;
                asset.msaaSampleCount = settings.Msaa > 0 ? settings.Msaa : q3 ? 4 : 2;
                asset.supportsHDR = false;
                asset.shadowDistance = 0f;
                asset.maxAdditionalLightsCount = 0;
                SetIntermediateAuto(asset);
            }

            eyeScale = XRSettings.eyeTextureResolutionScale;
            if (xr)
            {
                float eye = settings.EyeScale > 0f ? settings.EyeScale : q3 ? 1.0f : 0.9f;
                XRSettings.eyeTextureResolutionScale = eye;
                // URP calls XRSystem.SetRenderScale(asset.renderScale) at every camera render, which sets every XR display's
                // scaleOfAllRenderTargets and overrides the line above: with the asset at 1.0 a Quest 2 kept its full 1440x1584 eye
                // buffer. The asset's render scale is the value that sticks (restored in Restore)
                if (asset != null) asset.renderScale = Mathf.Clamp(eye, 0.1f, 2f);
                LogEyeSizeNextFrame();
            }

            if (xr) RequestRefreshRate(settings.RefreshHz);
            SetHeadCameraFlags();
            Hair = settings.Hair;
            HairSteps = 0;
            HairUndos = 0;
            ApplyHair();
            if (!hairHooked)
            {
                hairHooked = true;
                HeadMovement.AvatarsLoaded += OnAvatars;
            }

            Notes = $"device '{VrDevice.Model}' ({(q3 ? "quest 3 class" : "quest 2 class")}), msaa {asset?.msaaSampleCount}, eye {XRSettings.eyeTextureResolutionScale:0.00}, hair {Hair.ToString().ToLowerInvariant()}, settings {settings.Source}";
        }

        // ------------------------------------------------------------------ hair

        static void OnAvatars(HeadMovement hm)
        {
            if (applied && Current == Tier.Quest) ApplyHair();
        }

        /// <summary>change the hair level (the menu button, the adaptive step-down) and apply it to the hair that exists</summary>
        public static void SetHair(HairLevel level, bool save = true)
        {
            Hair = level;
            Debug.Log("VrQuality: hair " + level.ToString().ToLowerInvariant());
            ApplyHair();
            if (save)
            {
                VrSettings.Current.Hair = level;
                VrSettings.Current.Save();
            }
        }

        public static HairLevel NextHairLevel(HairLevel l) => l == HairLevel.Full ? HairLevel.Light : l == HairLevel.Light ? HairLevel.Off : HairLevel.Full;

        /// <summary>one level cheaper if there is one (the adaptive controller); false when already off</summary>
        public static bool StepHairDown()
        {
            if (Hair == HairLevel.Off) return false;
            HairSteps++;
            SetHair(Hair == HairLevel.Full ? HairLevel.Light : HairLevel.Off, false);
            return true;
        }

        /// <summary>the adaptive controller takes back a step that gained no frame time</summary>
        public static void UndoHairStep(HairLevel back)
        {
            HairUndos++;
            SetHair(back, false);
        }

        public static int HairUndos { get; private set; }

        /// <summary>a hair whose simulation drives a splat dancer's bound hair (SplatDancers.DrivesSplatHair): never switched off -
        /// at level Off it keeps simulating with the Light profile (its cards are hidden under the splats anyway), so the splat hair
        /// keeps swinging; the HairSimulation components and every other hair follow the level as before</summary>
        public static bool SplatHairNeedsSimulation(HairStrands h = null)
        {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
            return SplatDancers.DrivesSplatHair(h);
#else
            return false;
#endif
        }

        static void ApplyHair()
        {
            if (Current != Tier.Quest) return;
            VrSettings.HairProfile p = VrSettings.Current.ProfileOf(Hair);
            bool on = Hair != HairLevel.Off;
            VrSettings st = VrSettings.Current;
            foreach (HairStrands h in UnityEngine.Object.FindObjectsByType<HairStrands>(FindObjectsInactive.Include))
            {
                h.ApplyBudget(st.HairBake, st.HairBakeChunk, st.HairSubsteps, st.HairIterations);
                bool keep = !on && SplatHairNeedsSimulation(h);
                VrSettings.HairProfile hp = keep ? st.Light : p;
                h.ApplyQuality(on || keep, hp.LodScale, hp.MinLod, hp.Tips);
                if (keep) Debug.Log("HM-PERF hair: level off, but this hair drives the splat dancer's bound hair: its simulation keeps running (light profile; the cards stay hidden under the splats)");
            }

            foreach (HairSimulation h in UnityEngine.Object.FindObjectsByType<HairSimulation>(FindObjectsInactive.Include)) h.enabled = on;
        }

        /// <summary>called about once a second (VrPerf): the splat dancers are built a moment after the avatars (their files load in the
        /// background), so a hair that was switched off before its splat binding existed is switched back on here; true when it changed</summary>
        public static bool EnforceSplatHairFloor()
        {
            if (!applied || Current != Tier.Quest || Hair != HairLevel.Off) return false;
            bool changed = false;
            foreach (HairStrands h in UnityEngine.Object.FindObjectsByType<HairStrands>(FindObjectsInactive.Include))
            {
                if (h.enabled || !SplatHairNeedsSimulation(h)) continue;
                VrSettings.HairProfile lp = VrSettings.Current.Light;
                h.ApplyQuality(true, lp.LodScale, lp.MinLod, lp.Tips);
                changed = true;
            }

            if (changed) Debug.Log("HM-PERF hair: level off, but the splat dancer's bound hair needs the simulation: switched back on (light profile)");
            return changed;
        }

        static void RestoreHair()
        {
            foreach (HairStrands h in UnityEngine.Object.FindObjectsByType<HairStrands>(FindObjectsInactive.Include))
            {
                h.ApplyBudget(true, 24, 0, 0);
                h.ApplyQuality(true, 1f, 0, true);
            }

            foreach (HairSimulation h in UnityEngine.Object.FindObjectsByType<HairSimulation>(FindObjectsInactive.Include)) h.enabled = true;
        }

        // ------------------------------------------------------------------ pipeline details

        /// <summary>
        /// Meta advises "Intermediate Texture: Auto" for passthrough (an Always-on intermediate colour target costs a full-screen copy every
        /// frame). The renderer data is not public API on the asset, so it is found by reflection and put back on exit; a failure only
        /// means the project's own setting stays.
        /// </summary>
        static void SetIntermediateAuto(UniversalRenderPipelineAsset a)
        {
            try
            {
                FieldInfo f = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance);
                if (f?.GetValue(a) is not ScriptableRendererData[] list)
                {
                    Debug.LogError("HM-PERF intermediate texture: URP asset field m_RendererDataList not found (URP changed?): the renderer keeps its own mode (Always costs a full-screen copy per eye)");
                    return;
                }

                foreach (ScriptableRendererData d in list)
                {
                    if (d is not UniversalRendererData ur) continue;
                    PropertyInfo p = typeof(UniversalRendererData).GetProperty("intermediateTextureMode", BindingFlags.Public | BindingFlags.Instance);
                    if (p == null) continue;
                    intermediateOwner = ur;
                    intermediateProp = p;
                    intermediateBefore = p.GetValue(ur);
                    p.SetValue(ur, Enum.ToObject(p.PropertyType, 0)); // IntermediateTextureMode.Auto
                    Notes += "intermediate texture auto; ";
                    return;
                }

                Debug.LogError("HM-PERF intermediate texture: no UniversalRendererData with an intermediateTextureMode property found: the renderer keeps its own mode");
            }
            catch (Exception e)
            {
                Debug.LogError("HM-PERF intermediate texture: mode not changed: " + e.Message);
            }
        }

        static void RestoreIntermediate()
        {
            try
            {
                if (intermediateOwner != null && intermediateProp != null) intermediateProp.SetValue(intermediateOwner, intermediateBefore);
            }
            catch (Exception)
            {
                // leaving
            }

            intermediateOwner = null;
            intermediateProp = null;
        }

        // ------------------------------------------------------------------ the VR head camera

        static Camera flaggedCam;
        static bool camHdr, camShadows, camStopNaN, camDither;
        static CameraOverrideOption camDepth, camColor;
        static LayerMask camVolumeMask;
        static VolumeFrameworkUpdateMode camVolumeMode;

        /// <summary>
        /// The Quest tier's head camera flags (2026-11 GPU audit, insurance): no HDR target, no shadow pass, no depth / opaque-colour copies
        /// asked by the camera, no volume evaluation (no post effects in VR, so the volume stack only costs CPU), no NaN stop and no dither
        /// (both are post-process work). The head camera is VrRig's (read here, never changed in VrRig); the previous values come back in
        /// Restore when the camera still exists.
        /// </summary>
        static void SetHeadCameraFlags()
        {
            Camera cam = VrRig.Instance != null && VrRig.Instance.Cam != null ? VrRig.Instance.Cam : Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("HM-PERF head camera: none found, its flags stay as they are");
                return;
            }

            flaggedCam = cam;
            camHdr = cam.allowHDR;
            cam.allowHDR = false;
            UniversalAdditionalCameraData d = cam.GetUniversalAdditionalCameraData();
            if (d != null)
            {
                camShadows = d.renderShadows;
                camDepth = d.requiresDepthOption;
                camColor = d.requiresColorOption;
                camVolumeMask = d.volumeLayerMask;
                camVolumeMode = cam.GetVolumeFrameworkUpdateMode();
                camStopNaN = d.stopNaN;
                camDither = d.dithering;
                d.renderShadows = false;
                d.requiresDepthOption = CameraOverrideOption.Off;
                d.requiresColorOption = CameraOverrideOption.Off;
                d.volumeLayerMask = 0;
                cam.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.ViaScripting);
                d.stopNaN = false;
                d.dithering = false;
            }

            Debug.Log($"HM-PERF head camera '{cam.name}': hdr off, shadows off, depth/colour textures off, volumes off, stopNaN/dither off");
        }

        static void RestoreHeadCameraFlags()
        {
            Camera cam = flaggedCam;
            flaggedCam = null;
            if (cam == null) return; // the rig (and its camera) is gone already
            cam.allowHDR = camHdr;
            UniversalAdditionalCameraData d = cam.GetUniversalAdditionalCameraData();
            if (d == null) return;
            d.renderShadows = camShadows;
            d.requiresDepthOption = camDepth;
            d.requiresColorOption = camColor;
            d.volumeLayerMask = camVolumeMask;
            cam.SetVolumeFrameworkUpdateMode(camVolumeMode);
            d.stopNaN = camStopNaN;
            d.dithering = camDither;
        }

        // ------------------------------------------------------------------ eye buffer size log

        static int eyeLogAfterFrame = -1;
        static bool eyeLogHooked;

        /// <summary>the eye buffer size is known only after URP applied the render scale at a camera render: logged once, a frame later</summary>
        static void LogEyeSizeNextFrame()
        {
            eyeLogAfterFrame = Time.frameCount;
            if (eyeLogHooked) return;
            eyeLogHooked = true;
            RenderPipelineManager.beginContextRendering += LogEyeSize;
        }

        static void LogEyeSize(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (Time.frameCount <= eyeLogAfterFrame + 1) return;
            StopEyeSizeLog();
            Debug.Log($"HM-PERF eye buffer {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} (eye scale {XRSettings.eyeTextureResolutionScale:0.00}, URP render scale {(asset != null ? asset.renderScale : -1f):0.00})");
        }

        static void StopEyeSizeLog()
        {
            if (!eyeLogHooked) return;
            eyeLogHooked = false;
            RenderPipelineManager.beginContextRendering -= LogEyeSize;
        }

        static void RequestRefreshRate(int hz)
        {
            try
            {
                List<XRDisplaySubsystem> displays = new();
                SubsystemManager.GetSubsystems(displays);
                foreach (XRDisplaySubsystem d in displays)
                {
                    if (!d.running) continue;
                    if (d.TryGetDisplayRefreshRate(out float before)) RefreshHz = before;
                    if (hz > 0 && d.TryGetSupportedDisplayRefreshRates(Unity.Collections.Allocator.Temp, out Unity.Collections.NativeArray<float> rates))
                    {
                        string list = "";
                        float best = -1f;
                        foreach (float r in rates)
                        {
                            list += r.ToString("0") + " ";
                            if (Mathf.Abs(r - hz) < 0.5f) best = r;
                        }

                        rates.Dispose();
                        Debug.Log($"VrQuality: display refresh rates [{list.Trim()}], now {RefreshHz:0}, asking for {hz}");
                        if (best > 0f && Mathf.Abs(best - RefreshHz) > 0.5f && d.TryRequestDisplayRefreshRate(best)) RefreshHz = best;
                    }

                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("VrQuality: refresh rate not changed: " + e.Message);
            }
        }

        static void ForDisplays(Action<XRDisplaySubsystem> act)
        {
            List<XRDisplaySubsystem> displays = new();
            SubsystemManager.GetSubsystems(displays);
            foreach (XRDisplaySubsystem d in displays)
            {
                if (!d.running) continue;
                try
                {
                    act(d);
                }
                catch (Exception)
                {
                    // not supported by this display
                }
            }
        }

        public static void Restore()
        {
            if (!applied) return;
            applied = false;
            QualitySettings.vSyncCount = vsync;
            Application.targetFrameRate = targetFps;
            if (Current == Tier.Quest)
            {
                if (asset != null)
                {
                    asset.msaaSampleCount = msaa;
                    asset.supportsHDR = hdr;
                    asset.renderScale = renderScale;
                    asset.shadowDistance = shadowDistance;
                    asset.maxAdditionalLightsCount = extraLights;
                }

                RestoreIntermediate();
                RestoreHeadCameraFlags();
                StopEyeSizeLog();
                XRSettings.eyeTextureResolutionScale = eyeScale;
                RestoreHair();
                if (hairHooked)
                {
                    hairHooked = false;
                    HeadMovement.AvatarsLoaded -= OnAvatars;
                }
            }

            Current = Tier.Pc;
        }

        public static Dictionary<string, object> State() => new()
        {
            ["tier"] = Current.ToString().ToLowerInvariant(), ["applied"] = applied, ["hair"] = Hair.ToString().ToLowerInvariant(), ["hairSteps"] = HairSteps, ["hairUndos"] = HairUndos, ["splatHairNeedsSimulation"] = SplatHairNeedsSimulation(),
            ["msaa"] = asset != null && Current == Tier.Quest ? asset.msaaSampleCount : (GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset)?.msaaSampleCount ?? -1,
            ["hdr"] = (GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset)?.supportsHDR ?? false,
            ["refreshHz"] = RefreshHz, ["notes"] = Notes
        };
    }
}
