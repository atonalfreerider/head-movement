using System;
using System.IO;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HmVr.Editor
{
    /// <summary>
    /// Scripted builds of the viewer for VR (docs/QUEST.md), into a git-ignored folder (Builds/): the viewer scene only, whatever the build
    /// settings list. The Windows build is the PC VR build (the same exe runs the desktop viewer; it enters VR by itself when an OpenXR
    /// runtime is registered, or with -hmvr on). The Android build is the Quest standalone APK: building it switches the editor's active
    /// build target (a long re-import), so the menu / command refuses to do it unless asked for explicitly and the editor is idle.
    ///
    ///   unity command hm_vr_build --target windows --output Builds/vr --development true --timeout 1800
    ///
    /// Data (captures, library, films) is not part of the APK: Tools/deploy_data.ps1 lays it out and pushes it with adb.
    /// </summary>
    public static class VrBuild
    {
        const string Scene = "Assets/Scenes/head-movement.unity";

        [MenuItem("Head Movement/VR/Build Windows (PC VR)")]
        public static void MenuWindows() => Debug.Log(Build("windows", "Builds/vr", false, false));

        [CliCommand("hm_vr_build", "Build the viewer: --target windows|android --output <folder under Builds/> --development true|false --allow_switch true (android only: switches the build target)")]
        public static string Cli(
            [CliArg("target", "windows | android")] string target = "windows",
            [CliArg("output", "output folder (git-ignored: keep it under Builds/)")] string output = "Builds/vr",
            [CliArg("development", "development build")] bool development = true,
            [CliArg("allow_switch", "android: allow switching the editor's active build target")] bool allowSwitch = false) =>
            Build(target, output, development, allowSwitch);

        public static string Build(string target, string output, bool development, bool allowSwitch)
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("leave Play mode first");
            string root = Directory.GetParent(Application.dataPath)!.FullName;
            string full = Path.GetFullPath(Path.Combine(root, output));
            if (!full.StartsWith(Path.Combine(root, "Builds"), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("the output must be under Builds/ (git-ignored)");
            bool android = string.Equals(target, "android", StringComparison.OrdinalIgnoreCase);
            BuildTarget bt = android ? BuildTarget.Android : BuildTarget.StandaloneWindows64;
            if (android && EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android && !allowSwitch)
                throw new InvalidOperationException("an Android build switches the active build target (a long re-import); pass --allow_switch true to do it");
            Directory.CreateDirectory(full);
            string exe = android ? Path.Combine(full, "headmovement.apk") : Path.Combine(full, "HeadMovement.exe");
            BuildPlayerOptions o = new()
            {
                scenes = new[] { Scene },
                locationPathName = exe,
                target = bt,
                targetGroup = android ? BuildTargetGroup.Android : BuildTargetGroup.Standalone,
                options = development ? BuildOptions.Development : BuildOptions.None
            };
            DateTime t0 = DateTime.Now;
            BuildReport rep = BuildPipeline.BuildPlayer(o);
            BuildSummary s = rep.summary;
            string result = $"{s.result} in {(DateTime.Now - t0).TotalSeconds:0} s: {exe} ({s.totalSize / 1048576.0:0} MB, {s.totalErrors} error(s), {s.totalWarnings} warning(s))";
            if (s.result != BuildResult.Succeeded)
            {
                string errs = string.Join("\n", rep.steps.SelectMany(st => st.messages).Where(m => m.type == LogType.Error || m.type == LogType.Exception).Select(m => m.content).Take(12));
                throw new InvalidOperationException(result + "\n" + errs);
            }

            return result;
        }
    }
}
