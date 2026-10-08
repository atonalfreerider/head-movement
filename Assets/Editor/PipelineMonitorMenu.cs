using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

// Head Movement > Pipeline Monitor: opens the capture pipeline monitor (stages, Runpod progress, ETA, budget,
// camera types, narration review) that lives in the workspace next to this project. Nothing machine-specific is
// stored in the project: the launcher is found through a per-user EditorPrefs path, the
// HEADMOVEMENT_PIPELINE_MONITOR environment variable, or ../tools/PipelineMonitor/ next to the project folder.
public static class PipelineMonitorMenu
{
    const string PrefKey = "HeadMovement.PipelineMonitor.Path";
    const string EnvKey = "HEADMOVEMENT_PIPELINE_MONITOR";

    [MenuItem("Head Movement/Pipeline Monitor", false, 0)]
    public static void Open()
    {
        string launcher = Resolve();
        if (launcher == null)
        {
            if (EditorUtility.DisplayDialog("Pipeline Monitor",
                    "The Pipeline Monitor launcher was not found next to this project (../tools/PipelineMonitor/).\n\n" +
                    "Build it with tools/PipelineMonitor/build.ps1, or locate PipelineMonitor.exe / PipelineMonitor.cmd.",
                    "Locate...", "Cancel"))
                Locate();
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(launcher);
            psi.UseShellExecute = true;
            psi.WorkingDirectory = Path.GetDirectoryName(launcher);
            Process.Start(psi); // never waits: the editor stays responsive; a second click only opens another window
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("Pipeline Monitor: could not start " + launcher + ": " + e.Message);
        }
    }

    [MenuItem("Head Movement/Pipeline Monitor Location...", false, 1)]
    public static void Locate()
    {
        string current = Resolve();
        string dir = current != null ? Path.GetDirectoryName(current) : WorkspaceRoot();
        string picked = EditorUtility.OpenFilePanel("Pipeline Monitor launcher", dir, "exe,cmd");
        if (!string.IsNullOrEmpty(picked))
        {
            EditorPrefs.SetString(PrefKey, picked);
            UnityEngine.Debug.Log("Pipeline Monitor launcher set (per-user editor preference).");
        }
    }

    static string WorkspaceRoot()
    {
        return Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
    }

    static string Resolve()
    {
        string root = WorkspaceRoot();
        string env = Environment.GetEnvironmentVariable(EnvKey);
        string[] candidates =
        {
            EditorPrefs.GetString(PrefKey, ""),
            env ?? "",
            Path.Combine(root, "tools/PipelineMonitor/PipelineMonitor.exe"),
            Path.Combine(root, "tools/PipelineMonitor/PipelineMonitor.cmd"),
        };
        foreach (string p in candidates)
            if (!string.IsNullOrEmpty(p) && File.Exists(p))
                return Path.GetFullPath(p);
        return null;
    }
}
