using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Zouk time from beat_this_analyzer.py: [seconds, type] pairs, 8 per measure. Type 1 = eighths 0 and 4,
/// type 2 = eighths 3 and 6, type 3 = the rest. Times are on the audio timeline.
/// </summary>
public class BeatGrid
{
    public const int BeatsPerMeasure = 8;

    /// <summary>
    /// AudioSource.time is sample-quantised, so seeking to a beat can land microseconds before it. Lookups treat
    /// anything within this window as on the beat (an eighth note is ~340 ms at zouk tempo).
    /// </summary>
    public const float SnapTolerance = 0.005f;

    public readonly float[] Times;
    public readonly int[] Types;

    public BeatGrid(List<List<float>> zoukTime)
    {
        Times = new float[zoukTime.Count];
        Types = new int[zoukTime.Count];
        for (int i = 0; i < zoukTime.Count; i++)
        {
            Times[i] = zoukTime[i][0];
            Types[i] = (int)zoukTime[i][1];
        }
    }

    public int Count => Times.Length;

    /// <summary>index of the last beat at or before t, -1 if t precedes the first beat</summary>
    public int IndexAtOrBefore(float t)
    {
        int i = Array.BinarySearch(Times, t);
        return i >= 0 ? i : ~i - 1;
    }

    /// <summary>nearest beat to t among the given types (mask bit n = type n), or -1</summary>
    public int Nearest(float t, int typeMask = ~0)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        int start = Mathf.Max(0, IndexAtOrBefore(t) - BeatsPerMeasure);
        int end = Mathf.Min(Count - 1, start + 2 * BeatsPerMeasure + 1);
        for (int i = start; i <= end; i++)
        {
            if ((typeMask & (1 << Types[i])) == 0) continue;
            float distance = Mathf.Abs(Times[i] - t);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    public float NextBeat(float t, float epsilon = 0.01f)
    {
        int i = IndexAtOrBefore(t + epsilon) + 1;
        return i < Count ? Times[i] : t;
    }

    public float PreviousBeat(float t, float epsilon = 0.01f)
    {
        int i = IndexAtOrBefore(t - epsilon);
        return i >= 0 ? Times[i] : 0;
    }

    /// <summary>index of the beat at or before t, snapping onto a beat within SnapTolerance</summary>
    public int BeatIndex(float t) => Mathf.Max(0, IndexAtOrBefore(t + SnapTolerance));

    public int MeasureIndex(float t) => BeatIndex(t) / BeatsPerMeasure;

    public float MeasureStart(int measure)
    {
        int i = Mathf.Clamp(measure * BeatsPerMeasure, 0, Count - 1);
        return Times[i];
    }

    public float MeasureEnd(int measure)
    {
        int i = (measure + 1) * BeatsPerMeasure;
        if (i < Count) return Times[i];

        // last measure: extrapolate one eighth past the final beat
        return Times[Count - 1] + (Count > 1 ? Times[Count - 1] - Times[Count - 2] : 0.5f);
    }

    public int MeasureCount => (Count + BeatsPerMeasure - 1) / BeatsPerMeasure;
}
