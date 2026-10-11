using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.Hands.OpenXR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.Meta;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

namespace HmVr.Editor
{
    /// <summary>
    /// The XR project settings the VR mode needs, applied by script so they are reproducible (docs/QUEST.md):
    ///  - XR Plug-in Management: the OpenXR loader for Windows (Meta Quest Link / Air Link / Virtual Desktop through the active OpenXR runtime)
    ///    and for Android (Quest standalone). "Initialize XR on Startup" is OFF on both: the viewer starts XR itself (VrMode) only when a
    ///    runtime is registered or the user asks, so a PC without a headset never logs an XR error and the desktop viewer is untouched.
    ///  - OpenXR features: Hand Tracking Subsystem (XR Hands) on both; Meta Quest Support (Android: the manifest entries, Quest 2 listed
    ///    as a supported device), the Touch controller profiles (accepted by the Quest runtime; the viewer itself uses hands only).
    ///  - Single-pass instanced rendering (all the viewer's shaders support it).
    /// Menu: Head Movement / VR / Configure XR settings. CLI: unity command hm_vr_setup.
    /// </summary>
    public static class VrSetup
    {
        [MenuItem("Head Movement/VR/Configure XR settings (OpenXR + hands)")]
        public static void Menu() => Debug.Log(Apply());

        [CliCommand("hm_vr_setup", "Apply the VR project settings (OpenXR loader for Windows and Android, hand tracking, Meta Quest support); returns what it did")]
        public static string Cli() => Apply();

        public static string Apply()
        {
            StringBuilder log = new();
            foreach (BuildTargetGroup group in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
            {
                XRGeneralSettingsPerBuildTarget perTarget = Store();
                XRGeneralSettings general = perTarget.SettingsForBuildTarget(group);
                if (general == null)
                {
                    perTarget.CreateDefaultSettingsForBuildTarget(group);
                    general = perTarget.SettingsForBuildTarget(group);
                }

                if (general == null)
                {
                    log.AppendLine($"{group}: could not create XR general settings");
                    continue;
                }

                if (general.Manager == null)
                {
                    perTarget.CreateDefaultManagerSettingsForBuildTarget(group);
                    general = perTarget.SettingsForBuildTarget(group);
                }

                XRManagerSettings manager = general.AssignedSettings;
                if (manager == null)
                {
                    log.AppendLine($"{group}: no manager settings");
                    continue;
                }

                bool had = XRPackageMetadataStore.IsLoaderAssigned("UnityEngine.XR.OpenXR.OpenXRLoader", group);
                if (!had) XRPackageMetadataStore.AssignLoader(manager, "UnityEngine.XR.OpenXR.OpenXRLoader", group);
                general.InitManagerOnStart = group == BuildTargetGroup.Android; // the headset app is VR from its first frame; the PC viewer starts XR itself, on demand
                manager.automaticLoading = false;
                manager.automaticRunning = false;
                EditorUtility.SetDirty(general);
                EditorUtility.SetDirty(manager);
                log.AppendLine($"{group}: OpenXR loader {(had ? "already assigned" : "assigned")}, Initialize XR on Startup {(general.InitManagerOnStart ? "on (the headset app is VR from the start)" : "off (the viewer starts XR itself)")}");

                OpenXRSettings oxr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
                if (oxr == null)
                {
                    log.AppendLine($"{group}: no OpenXR settings asset (open Project Settings > XR Plug-in Management > OpenXR once)");
                    continue;
                }

                Enable<HandTracking>(oxr, group, log);
                Enable<OculusTouchControllerProfile>(oxr, group, log);
                if (group == BuildTargetGroup.Android)
                {
                    Enable<MetaQuestFeature>(oxr, group, log);
                    Enable<FoveatedRenderingFeature>(oxr, group, log);
                    // passthrough mixed reality: AR Foundation's session + the Meta camera (passthrough) feature, and the refresh-rate request
                    Enable<ARSessionFeature>(oxr, group, log);
                    Enable<ARCameraFeature>(oxr, group, log);
                    Enable<DisplayUtilitiesFeature>(oxr, group, log);
                    ARCameraFeature cam = oxr.GetFeature<ARCameraFeature>();
                    if (cam != null && cam.enabled)
                    {
                        SerializedObject so = new(cam);
                        SerializedProperty pre = so.FindProperty("m_PassthroughPreSplashScreen");
                        if (pre != null && !pre.boolValue)
                        {
                            pre.boolValue = true; // the system splash is the real room too: no black flash before the app
                            so.ApplyModifiedProperties();
                            EditorUtility.SetDirty(cam);
                        }

                        log.AppendLine($"{group}: passthrough pre-splash screen on");
                    }
                }

                oxr.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
                oxr.depthSubmissionMode = OpenXRSettings.DepthSubmissionMode.None;
                EditorUtility.SetDirty(oxr);
                log.AppendLine($"{group}: OpenXR render mode single-pass instanced, no depth submission");
            }

            ApplyAndroidPlayer(log);
            AssetDatabase.SaveAssets();
            return log.ToString();
        }

        /// <summary>the Android player settings of a Quest 2 / 3 standalone build (they only matter for an Android build; the editor stays on its build target)</summary>
        static void ApplyAndroidPlayer(StringBuilder log)
        {
            PlayerSettings.productName = "Head Movement"; // the name in the headset's library (Unknown Sources); was the project's template name
            PlayerSettings.enableFrameTimingStats = true; // FrameTimingManager: GPU / CPU frame time for the on-device log (VrPerf)
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32; // the Meta passthrough (camera) feature needs Android 12L; Horizon OS on a Quest 2 and 3 is Android 14 (API 34)
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.headmovement.viewer");
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.Vulkan, UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
            log.AppendLine("Android player: product name 'Head Movement', min API 32, ARM64, IL2CPP, Vulkan then GLES3, ASTC textures, package com.headmovement.viewer, frame timing stats on");
        }

        static XRGeneralSettingsPerBuildTarget Store()
        {
            string[] found = AssetDatabase.FindAssets("t:XRGeneralSettingsPerBuildTarget");
            return found.Length == 0 ? null : AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(AssetDatabase.GUIDToAssetPath(found[0]));
        }

        static void Enable<T>(OpenXRSettings s, BuildTargetGroup group, StringBuilder log) where T : OpenXRFeature
        {
            T f = s.GetFeature<T>();
            if (f == null)
            {
                log.AppendLine($"{group}: feature {typeof(T).Name} not available");
                return;
            }

            if (!f.enabled)
            {
                f.enabled = true;
                EditorUtility.SetDirty(f);
            }

            log.AppendLine($"{group}: {typeof(T).Name} enabled");
        }

        /// <summary>what is configured now (for the report and the docs)</summary>
        [CliCommand("hm_vr_settings", "Report the XR project settings: loaders, OpenXR features, player settings relevant to Quest")]
        public static string Report()
        {
            StringBuilder sb = new();
            foreach (BuildTargetGroup group in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
            {
                XRGeneralSettings g = Store()?.SettingsForBuildTarget(group);
                sb.AppendLine($"[{group}] initializeOnStart={g?.InitManagerOnStart} loaders=" + string.Join(",", g?.AssignedSettings?.activeLoaders?.Select(l => l.name) ?? new string[0]));
                OpenXRSettings oxr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
                if (oxr == null) continue;
                sb.AppendLine($"  render mode {oxr.renderMode}, depth submission {oxr.depthSubmissionMode}");
                sb.AppendLine("  enabled features: " + string.Join(", ", oxr.GetFeatures().Where(f => f != null && f.enabled).Select(f => f.GetType().Name)));
            }

            sb.AppendLine($"active build target {EditorUserBuildSettings.activeBuildTarget}; Android: min SDK {PlayerSettings.Android.minSdkVersion}, scripting {PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android)}, arch {PlayerSettings.Android.targetArchitectures}");
            return sb.ToString();
        }
    }
}
