using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// Builds a plausible 26-joint hand from a palm pose and a few numbers, for the simulated hands of the tests and the desktop
    /// preview. The palm frame: forward = along the fingers, up = out of the back of the hand (so the palm faces -up). With the
    /// identity rotation a right hand is held palm down, fingers forward, thumb to the left.
    /// </summary>
    public static class HandSynth
    {
        // palm-local layout (metres), right hand, thumb on -x; the left hand mirrors x
        const float WristZ = -0.085f;

        static readonly float[] KnuckleX = { -0.030f, -0.011f, 0.009f, 0.027f }; // index, middle, ring, little (right hand)
        static readonly float[] KnuckleZ = { 0.045f, 0.050f, 0.046f, 0.038f };
        static readonly float[][] Lengths =
        {
            new[] { 0.040f, 0.024f, 0.020f }, // index: proximal, intermediate, distal phalanx lengths
            new[] { 0.044f, 0.028f, 0.021f },
            new[] { 0.041f, 0.026f, 0.020f },
            new[] { 0.033f, 0.019f, 0.017f }
        };

        /// <summary>
        /// pinch 0..1 (0 = open, 1 = thumb tip on index tip); curl 0..1 curls the middle, ring and little fingers; indexExtend keeps the
        /// index straight (a pointing hand) even when the others curl. Joints are written into the hand in world space.
        /// </summary>
        public static void Fill(HandState h, Vector3 palmPos, Quaternion rot, float pinch, float curl, bool tracked)
        {
            h.Tracked = tracked;
            float s = h.Side.Sign(); // +1 right: the thumb is on the -x side, so mirror for the left hand with x * s
            Vector3[] local = new Vector3[HandJoint.Count];
            local[HandJoint.Wrist] = new Vector3(0f, 0f, WristZ);
            local[HandJoint.Palm] = Vector3.zero;

            // fingers
            for (int f = 0; f < 4; f++)
            {
                int baseIdx = HandJoint.IndexMetacarpal + f * 5;
                Vector3 knuckle = new(KnuckleX[f] * s, 0f, KnuckleZ[f]);
                Vector3 meta = Vector3.Lerp(local[HandJoint.Wrist], knuckle, 0.35f);
                float curlF = f == 0 ? pinch * 0.35f : curl;
                // flexion angles (rad) of the proximal, intermediate and distal segments, accumulated
                float a1 = curlF * 80f * Mathf.Deg2Rad, a2 = a1 + curlF * 95f * Mathf.Deg2Rad, a3 = a2 + curlF * 60f * Mathf.Deg2Rad;
                float[] angles = { a1, a2, a3 };
                local[baseIdx] = meta;
                local[baseIdx + 1] = knuckle;
                Vector3 p = knuckle;
                for (int k = 0; k < 3; k++)
                {
                    // flexion bends the finger toward the palm (-up)
                    Vector3 dir = new(0f, -Mathf.Sin(angles[k]), Mathf.Cos(angles[k]));
                    p += dir * Lengths[f][k];
                    local[baseIdx + 2 + k] = p;
                }
            }

            // thumb: from the base near the wrist, out and forward; pinch brings the tip to the index tip
            Vector3 cmc = new(-0.020f * s, -0.012f, -0.060f);
            Vector3 mc = cmc + new Vector3(-0.018f * s, -0.008f, 0.030f);
            Vector3 prox = mc + new Vector3(-0.012f * s, -0.010f, 0.030f);
            Vector3 dist = prox + new Vector3(-0.004f * s, -0.012f, 0.026f);
            Vector3 tip = dist + new Vector3(-0.002f * s, -0.010f, 0.020f);
            Vector3 indexTip = local[HandJoint.IndexTip];
            Vector3 target = indexTip + new Vector3(0f, -0.004f, 0f); // the pads meet, a few millimetres below the index tip
            float k1 = Mathf.SmoothStep(0f, 1f, pinch);
            tip = Vector3.Lerp(tip, target, k1);
            dist = Vector3.Lerp(dist, Vector3.Lerp(dist, target, 0.55f), k1);
            prox = Vector3.Lerp(prox, Vector3.Lerp(prox, target, 0.30f), k1);
            // (the XR thumb has metacarpal, proximal, distal, tip: map the points onto them)
            local[HandJoint.ThumbMetacarpal] = cmc;
            local[HandJoint.ThumbProximal] = mc;
            local[HandJoint.ThumbDistal] = Vector3.Lerp(prox, dist, 0.5f);
            local[HandJoint.ThumbTip] = tip;

            for (int i = 0; i < HandJoint.Count; i++) h.Joints[i] = palmPos + rot * local[i];
        }
    }
}
