using System.Collections.Generic;
using UnityEngine;

public static class CatmullRomSpline
{
    /// <summary>
    /// Creates a Catmull-Rom spline that interpolates all points in 'points'
    /// and returns a list of sampled positions along that spline.
    /// </summary>
    /// <param name="points">Array of points to interpolate (at least 2).</param>
    /// <param name="samplesPerSegment">How many samples per segment? (higher = smoother)</param>
    /// <param name="loop">
    ///   Whether to 'loop' around so that the end connects back to the first point.
    /// </param>
    /// <returns>A new array of sampled positions along the Catmull-Rom curve.</returns>
    public static Vector3[] Generate(Vector3[] points, int samplesPerSegment = 20, bool loop = false)
    {
        if (points == null || points.Length < 2)
            return points ?? new Vector3[0];

        List<Vector3> result = new List<Vector3>();

        // We'll do one spline 'segment' from i to i+1 for each i
        // But Catmull-Rom uses 4 points for each segment: p0, p1, p2, p3
        // We'll carefully pick these neighbors. 

        int last = points.Length - 1;
        for (int i = 0; i < points.Length - (loop ? 0 : 1); i++)
        {
            // For each segment, the "middle" of the segment is from points[i] to points[i+1].
            // But we also need p0 = points[i-1] and p3 = points[i+2], with safe indexing.

            Vector3 p0 = GetPoint(points, i - 1, loop);
            Vector3 p1 = GetPoint(points, i,     loop);
            Vector3 p2 = GetPoint(points, i + 1, loop);
            Vector3 p3 = GetPoint(points, i + 2, loop);

            // We want 'samplesPerSegment' divisions for [0..1].
            // If i > 0, we can skip t=0 to avoid duplicating the exact same point
            // that got added at the end of the previous segment.
            int sampleStart = (i == 0) ? 0 : 1;
            for (int s = sampleStart; s <= samplesPerSegment; s++)
            {
                float t = s / (float)samplesPerSegment;
                Vector3 pt = CatmullRomPosition(p0, p1, p2, p3, t);
                result.Add(pt);
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Retrieves a point from the array, wrapping around if 'loop' is true,
    /// or clamping to the boundary if 'loop' is false.
    /// </summary>
    private static Vector3 GetPoint(Vector3[] points, int index, bool loop)
    {
        int count = points.Length;
        if (loop)
        {
            // Wrap around (mod)
            index = (index % count + count) % count;
            return points[index];
        }
        else
        {
            // Clamp
            index = Mathf.Clamp(index, 0, count - 1);
            return points[index];
        }
    }

    /// <summary>
    /// Evaluates the position on the Catmull-Rom spline at parameter t in [0..1],
    /// given four control points p0, p1, p2, p3.
    /// The curve passes exactly through p1 (t=0) and p2 (t=1).
    /// </summary>
    private static Vector3 CatmullRomPosition(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        // Standard Catmull-Rom (uniform) formula with a 0.5 factor
        float t2 = t * t;
        float t3 = t2 * t;

        Vector3 a = 2f * p1;
        Vector3 b = -p0 + p2;
        Vector3 c = 2f * p0 - 5f * p1 + 4f * p2 - p3;
        Vector3 d = -p0 + 3f * p1 - 3f * p2 + p3;

        // 0.5 * [ a + b*t + c*t^2 + d*t^3 ]
        return 0.5f * (a + (b * t) + (c * t2) + (d * t3));
    }
}
