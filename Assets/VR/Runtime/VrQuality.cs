using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace HmVr
{
    /// <summary>
    /// The render settings of the VR mode. Every tier: no post effects on the head camera (the camera sets that itself), no V-sync fight with
    /// the runtime, fixed foveation where the display supports it.
    ///
    /// Tier Quest (the default on Android, or -hmvr-tier quest / HM_VR_TIER=quest to try it on a PC; docs/QUEST.md): for the Quest's
    /// 72 Hz mobile GPU - MSAA 2x, no HDR, no shadows, no extra lights, 0.9 eye resolution, the hair simulation and strands off (the
    /// avatars' hair is the most expensive part of a frame); the Quest 2 runs its 72 Hz mode by default. It changes the pipeline asset at run
    /// time, so every value is restored when VR mode ends. Tier Pc touches nothing.
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

        static bool applied, hairHooked;
        static int vsync, msaa;
        static int targetFps;
        static float renderScale, shadowDistance, eyeScale;
        static bool hdr;
        static int extraLights;
        static readonly List<Behaviour> hairOff = new();
        static UniversalRenderPipelineAsset asset;

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
            vsync = QualitySettings.vSyncCount;
            targetFps = Application.targetFrameRate;
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
            asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset != null)
            {
                msaa = asset.msaaSampleCount;
                hdr = asset.supportsHDR;
                renderScale = asset.renderScale;
                shadowDistance = asset.shadowDistance;
                extraLights = asset.maxAdditionalLightsCount;
                asset.msaaSampleCount = 2;
                asset.supportsHDR = false;
                asset.shadowDistance = 0f;
                asset.maxAdditionalLightsCount = 0;
            }

            eyeScale = XRSettings.eyeTextureResolutionScale;
            if (xr) XRSettings.eyeTextureResolutionScale = 0.9f;
            DisableHair();
            if (!hairHooked)
            {
                hairHooked = true;
                HeadMovement.AvatarsLoaded += OnAvatars;
            }
        }

        static void OnAvatars(HeadMovement hm)
        {
            if (applied && Current == Tier.Quest) DisableHair();
        }

        static void DisableHair()
        {
            foreach (HairStrands h in UnityEngine.Object.FindObjectsByType<HairStrands>())
            {
                if (h.enabled)
                {
                    h.enabled = false;
                    hairOff.Add(h);
                }
            }

            foreach (HairSimulation h in UnityEngine.Object.FindObjectsByType<HairSimulation>())
            {
                if (h.enabled)
                {
                    h.enabled = false;
                    hairOff.Add(h);
                }
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

                XRSettings.eyeTextureResolutionScale = eyeScale;
                foreach (Behaviour b in hairOff)
                {
                    if (b != null) b.enabled = true;
                }

                hairOff.Clear();
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
            ["tier"] = Current.ToString().ToLowerInvariant(), ["applied"] = applied, ["hairOff"] = hairOff.Count,
            ["msaa"] = asset != null && Current == Tier.Quest ? asset.msaaSampleCount : (GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset)?.msaaSampleCount ?? -1,
            ["hdr"] = (GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset)?.supportsHDR ?? false
        };
    }
}
