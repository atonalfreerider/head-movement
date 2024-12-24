using System;
using UnityEngine;

namespace Util
{
    public static class RhythmPhysics
    {
        /// <summary>
        /// Computes the jerk for each position in the array.
        /// Jerk is the third derivative of position with respect to time.
        /// Specifically, it returns the magnitude of the jerk at each sample.
        /// </summary>
        /// <param name="positions">Array of positions over time.</param>
        /// <param name="totalTime">Total time span of the positions array.</param>
        /// <returns>An array of jerk magnitudes at each time sample.</returns>
        public static float[] CalculateJerk(Vector3[] positions, float totalTime)
        {
            // --- SAFETY CHECKS ---
            if (positions == null || positions.Length < 2)
            {
                Debug.LogWarning("Positions array is null or too short to calculate jerk.");
                return Array.Empty<float>();
            }
            if (totalTime <= 0f)
            {
                Debug.LogWarning("totalTime must be greater than 0 to avoid division by zero.");
                return Array.Empty<float>();
            }

            int n = positions.Length;

            // The time step assuming uniform intervals
            float dt = totalTime / (n - 1);

            // If dt is 0 or extremely small, this will cause numerical problems
            if (dt <= Mathf.Epsilon)
            {
                Debug.LogWarning($"Invalid time step dt = {dt}. Check totalTime or positions length.");
                return Array.Empty<float>();
            }

            // --- ARRAYS ---
            Vector3[] velocity = new Vector3[n];
            Vector3[] acceleration = new Vector3[n];
            float[] jerk = new float[n];

            // --- Calculate velocity (forward difference) ---
            // velocity[i] = (positions[i+1] - positions[i]) / dt   for i in [0..n-2]
            for (int i = 0; i < n - 1; i++)
            {
                velocity[i] = (positions[i + 1] - positions[i]) / dt;
            }
            // Copy the second-to-last velocity into the last slot
            velocity[n - 1] = velocity[n - 2];

            // --- Calculate acceleration (forward difference) ---
            // acceleration[i] = (velocity[i+1] - velocity[i]) / dt   for i in [0..n-2]
            for (int i = 0; i < n - 1; i++)
            {
                acceleration[i] = (velocity[i + 1] - velocity[i]) / dt;
            }
            // Copy the second-to-last acceleration into the last slot
            acceleration[n - 1] = acceleration[n - 2];

            // --- Calculate jerk (forward difference) ---
            // jerk[i] = (acceleration[i+1] - acceleration[i]) / dt   for i in [0..n-2]
            for (int i = 0; i < n - 1; i++)
            {
                Vector3 jerkVector = (acceleration[i + 1] - acceleration[i]) / dt;
                jerk[i] = jerkVector.magnitude;
            }
            // Copy the second-to-last jerk into the last slot
            jerk[n - 1] = jerk[n - 2];

            return jerk;
        }
    }
}
