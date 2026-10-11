using System.Diagnostics;
using UnityEngine;

/// <summary>
/// Timing marks of a lesson load (logcat / the Unity console: lines starting HM-LOAD): the time since the previous mark, so the long
/// stalls of a headset load can be found and sliced. Enabled in every build; cheap (one Stopwatch).
/// </summary>
public static class LoadTrace
{
    static readonly Stopwatch Total = new(), Step = new();
    static string label = "";
    public static string Last { get; private set; } = "";
    public static double LongestStepMs { get; private set; }
    public static string LongestStep { get; private set; } = "";
    public static bool Enabled = true;

    public static void Begin(string what)
    {
        label = what;
        Total.Restart();
        Step.Restart();
        LongestStepMs = 0;
        LongestStep = "";
        Last = "begin";
        if (Enabled) UnityEngine.Debug.Log($"HM-LOAD begin {what}");
    }

    public static void Mark(string stage)
    {
        if (!Total.IsRunning) return;
        double ms = Step.Elapsed.TotalMilliseconds;
        Step.Restart();
        Last = stage;
        if (ms > LongestStepMs)
        {
            LongestStepMs = ms;
            LongestStep = stage;
        }

        if (Enabled && ms >= 5.0) UnityEngine.Debug.Log($"HM-LOAD {stage}: {ms:0} ms (t={Total.Elapsed.TotalSeconds:0.0} s)");
    }

    public static void End()
    {
        if (!Total.IsRunning) return;
        Mark("end");
        UnityEngine.Debug.Log($"HM-LOAD done {label}: {Total.Elapsed.TotalSeconds:0.0} s, longest single step '{LongestStep}' {LongestStepMs:0} ms");
        Total.Stop();
    }
}
