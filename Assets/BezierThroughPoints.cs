using UnityEngine;
using System;
using System.Collections.Generic;

public class BezierThroughPoints
{
    /// <summary>
    /// Generates a piecewise Bézier curve that passes through each point in 'points'.
    /// Each consecutive pair of points becomes a cubic Bézier segment.
    /// </summary>
    public static Vector3[] BezierCurve(Vector3[] points, int resolutionPerSegment = 20)
    {
        // If fewer than 2 points, just return the array itself
        if (points == null || points.Length < 2)
            return points;

        List<Vector3> curvePoints = new List<Vector3> {
            // Always include the very first point
            points[0] };

        // Build a cubic Bézier segment between each pair of consecutive points
        for (int i = 0; i < points.Length - 1; i++)
        {
            Vector3 p0 = points[i];
            Vector3 p3 = points[i + 1];

            // Very simple control points:
            // p1 is 1/3 of the way from p0 to p3
            // p2 is 2/3 of the way from p0 to p3
            // This ensures the segment begins at p0 and ends at p3 exactly.
            Vector3 p1 = p0 + (p3 - p0) / 3f;
            Vector3 p2 = p0 + 2f * (p3 - p0) / 3f;

            // Sample this segment at the requested resolution
            // (We already have p0 in the list, so skip t=0 for subsequent segments)
            for (int r = 1; r <= resolutionPerSegment; r++)
            {
                float t = r / (float)resolutionPerSegment;
                Vector3 sample = EvaluateCubicBezier(p0, p1, p2, p3, t);
                curvePoints.Add(sample);
            }
        }

        return curvePoints.ToArray();
    }

    /// <summary>
    /// Evaluates a cubic Bézier at parameter t.
    /// p0 and p3 are endpoints, p1 and p2 are control points.
    /// </summary>
    private static Vector3 EvaluateCubicBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1f - t;
        float t2 = t * t;
        float t3 = t2 * t;
        float u2 = u * u;
        float u3 = u2 * u;

        // Cubic Bézier formula
        return u3 * p0
               + 3f * u2 * t * p1
               + 3f * u * t2 * p2
               + t3 * p3;
    }
}