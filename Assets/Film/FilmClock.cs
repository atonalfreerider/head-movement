using UnityEngine;

/// <summary>
/// Film time -> dance time (reference seconds) for a direction. Each segment maps its film span onto its dance span
/// (constant speed, linear speed ramp, hold); a segment boundary where the dance time jumps (a replay, a seek) is a CUT.
/// Elsewhere the mapping is smoothed with a moving average (box window, EaseSeconds wide, narrowed near cuts and the
/// film ends) so every speed change - 1.0x into 0.36x, a hold into 0.25x - eases instead of stepping: the dance time is
/// continuous with a continuous speed, and a pure function of film time (seeks and screenshots are deterministic).
/// </summary>
public class FilmClock
{
    public const float CutJump = 0.02f;
    public float EaseSeconds = 0.8f;
    const int Samples = 12;

    readonly FilmDirection direction;
    readonly float[] cuts; // film times of the cuts

    public FilmClock(FilmDirection d)
    {
        direction = d;
        System.Collections.Generic.List<float> c = new();
        for (int i = 1; i < d.Segments.Count; i++)
        {
            FilmSegment a = d.Segments[i - 1], b = d.Segments[i];
            if (Mathf.Abs(a.D1 - b.D0) > CutJump) c.Add(b.F0);
        }

        cuts = c.ToArray();
    }

    public float[] Cuts => cuts;

    /// <summary>true when a cut lies in (a, b] (film times)</summary>
    public bool CutBetween(float a, float b)
    {
        if (b < a) (a, b) = (b, a);
        foreach (float c in cuts)
        {
            if (c > a && c <= b) return true;
        }

        return false;
    }

    public float RawDance(float film)
    {
        int i = direction.SegmentIndexAt(film);
        return i < 0 ? 0f : direction.Segments[i].RawDance(film);
    }

    /// <summary>half-width of the smoothing window at film time f: EaseSeconds / 2, narrowed so the window never
    /// reaches across a cut or past the film's ends</summary>
    float HalfWindow(float f)
    {
        float h = EaseSeconds * 0.5f;
        h = Mathf.Min(h, Mathf.Max(0f, f), Mathf.Max(0f, direction.FilmDuration - f));
        foreach (float c in cuts)
        {
            float d = f < c ? c - f - 1e-4f : f - c;
            if (d < h) h = Mathf.Max(0f, d);
        }

        return h;
    }

    public float Dance(float film)
    {
        float h = HalfWindow(film);
        if (h < 1e-3f) return RawDance(film);
        // midpoint rule on a piecewise linear/quadratic function: plenty for a C1 ease
        float sum = 0f;
        for (int i = 0; i < Samples; i++)
        {
            float f = film - h + (i + 0.5f) * (2f * h / Samples);
            sum += RawDance(f);
        }

        return sum / Samples;
    }

    /// <summary>dance seconds per film second now (numeric derivative of the smoothed mapping)</summary>
    public float Speed(float film)
    {
        const float e = 1f / 120f;
        float a = Mathf.Max(0f, film - e), b = Mathf.Min(direction.FilmDuration, film + e);
        if (b - a < 1e-5f || CutBetween(a, b)) return direction.Segments.Count > 0 ? direction.Segments[direction.SegmentIndexAt(film)].S0 : 1f;
        return (Dance(b) - Dance(a)) / (b - a);
    }
}
