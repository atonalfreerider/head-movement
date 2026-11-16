using UnityEngine;

namespace HmVr
{
    /// <summary>The pose of the small menu that rides on the left wrist.</summary>
    public static class WristPose
    {
        /// <summary>
        /// The panel sits above the BACK of the wrist: upM out of the hand on the side opposite palmNormal, and alongM along the forearm
        /// toward the elbow (opposite forward, the wrist-to-fingers direction, flattened against the palm normal). Its forward points from the
        /// head to the panel (a world-space canvas read from the -Z side), its up is the world up as far as the view allows. Only positions
        /// and directions go in, no previous state: the same joints always give the same pose, and a small turn of the hand moves the panel
        /// a little (the rotation follows the head, not the hand).
        /// </summary>
        public static void WristPanelPose(Vector3 wrist, Vector3 forward, Vector3 palmNormal, Vector3 head, float upM, float alongM,
            out Vector3 position, out Quaternion rotation)
        {
            Vector3 n = palmNormal.sqrMagnitude > 1e-10f ? palmNormal.normalized : Vector3.down;
            Vector3 f = forward - Vector3.Dot(forward, n) * n; // along the forearm, in the plane of the hand
            f = f.sqrMagnitude > 1e-10f ? f.normalized : Vector3.zero;
            position = wrist - n * upM - f * alongM;

            Vector3 look = position - head;
            if (look.sqrMagnitude < 1e-10f) look = -n; // the head at the panel: face away from the back of the hand
            look.Normalize();
            Vector3 up = Vector3.up;
            if (Vector3.Cross(look, up).sqrMagnitude < 1e-4f)
            {
                // looking straight up or down at it: the top of the panel points along the fingers (or +Z)
                up = f.sqrMagnitude > 0f ? f : Vector3.forward;
                if (Vector3.Cross(look, up).sqrMagnitude < 1e-4f) up = Vector3.forward;
            }

            rotation = Quaternion.LookRotation(look, up);
        }
    }
}
