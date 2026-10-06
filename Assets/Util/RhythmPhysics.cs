using System;
using UnityEngine;

namespace Util
{
    public static class RhythmPhysics
    {
        /// <summary>
        /// Jerk magnitude (|d3x/dt3|, m/s^3) at every sample, from three passes of the non-uniform three-point
        /// central difference on the real frame timestamps. Central differences are unbiased in time (forward
        /// differences shifted jerk ~1.5 frames early); the two end samples use one-sided differences.
        /// Prefer the spline jerk from timing.json (dancecap timing stage) when a capture provides it.
        /// </summary>
        /// <param name="positions">positions over time</param>
        /// <param name="times">strictly increasing sample times in seconds (same length)</param>
        public static float[] CalculateJerk(Vector3[] positions, float[] times)
        {
            if (positions == null || times == null || positions.Length != times.Length)
            {
                Debug.LogWarning("CalculateJerk: positions and times must have the same length.");
                return Array.Empty<float>();
            }

            int n = positions.Length;
            if (n < 3) return new float[n];

            for (int i = 1; i < n; i++)
            {
                if (!(times[i] > times[i - 1]))
                {
                    Debug.LogWarning($"CalculateJerk: times must increase (sample {i}).");
                    return new float[n];
                }
            }

            Vector3[] jerk = Derivative(Derivative(Derivative(positions, times), times), times);
            float[] magnitude = new float[n];
            for (int i = 0; i < n; i++) magnitude[i] = jerk[i].magnitude;
            return magnitude;
        }

        /// <summary>uniform-grid convenience overload: n samples spread evenly over totalTime seconds</summary>
        public static float[] CalculateJerk(Vector3[] positions, float totalTime)
        {
            if (positions == null || positions.Length < 2 || totalTime <= 0f)
            {
                Debug.LogWarning("CalculateJerk: need >= 2 positions and totalTime > 0.");
                return Array.Empty<float>();
            }

            float[] times = new float[positions.Length];
            for (int i = 0; i < times.Length; i++) times[i] = totalTime * i / (times.Length - 1);
            return CalculateJerk(positions, times);
        }

        /// <summary>
        /// First derivative on a non-uniform grid. Interior: the second-order three-point formula
        /// f'(t_i) = -h2/(h1(h1+h2)) f_{i-1} + (h2-h1)/(h1 h2) f_i + h1/(h2(h1+h2)) f_{i+1},
        /// h1 = t_i - t_{i-1}, h2 = t_{i+1} - t_i (equals (f_{i+1}-f_{i-1})/2h on a uniform grid). Ends: one-sided.
        /// </summary>
        public static Vector3[] Derivative(Vector3[] x, float[] t)
        {
            int n = x.Length;
            Vector3[] d = new Vector3[n];
            if (n < 2) return d;

            for (int i = 1; i < n - 1; i++)
            {
                float h1 = t[i] - t[i - 1];
                float h2 = t[i + 1] - t[i];
                d[i] = x[i - 1] * (-h2 / (h1 * (h1 + h2))) + x[i] * ((h2 - h1) / (h1 * h2)) +
                       x[i + 1] * (h1 / (h2 * (h1 + h2)));
            }

            d[0] = (x[1] - x[0]) / (t[1] - t[0]);
            d[n - 1] = (x[n - 1] - x[n - 2]) / (t[n - 1] - t[n - 2]);
            return d;
        }
    }
}
