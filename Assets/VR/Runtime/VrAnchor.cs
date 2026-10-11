using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// Menus are ANCHORED in the world: a panel is placed once when it opens - in front of the user along the horizontal gaze, at a comfortable
    /// height, facing the user with a slight tilt - and then stays exactly there (no follow, no easing, no drift) until it is closed or
    /// recalled (palm up, the wrist button): a recall closes it and opens it again in front of the user. A panel never opens inside the dancers:
    /// if the spot is among them it is moved out to the user's side of the dance.
    /// </summary>
    public static class VrAnchor
    {
        static readonly InputFrame Scratch = new();

        /// <summary>
        /// The head pose NOW. A panel is placed in the frame the user was moved (a teleport, a viewpoint, the end of a lesson): the polled input
        /// frame still holds last frame's head, so placements read the rig's head directly.
        /// </summary>
        public static InputFrame Now(InputFrame polled)
        {
            VrRig rig = VrRig.Instance;
            if (rig == null || rig.Head == null) return polled;
            Scratch.HeadPosition = rig.Head.position;
            Scratch.HeadRotation = rig.Head.rotation;
            return Scratch;
        }

        /// <summary>the dancers' footprint for this purpose: a disc around the couple (m)</summary>
        public const float DancerRadius = 1.25f;

        /// <summary>
        /// The world position for a panel opened now: `distance` metres ahead of the head along the horizontal gaze (a head looking up or down
        /// uses its last horizontal heading), `drop` below the eyes (negative = above), then kept out of the dancers.
        /// </summary>
        public static Vector3 Spot(InputFrame f, float distance, float drop, float yawOffsetDeg = 0f)
        {
            Vector3 fwd = f.HeadYaw * Quaternion.Euler(0f, yawOffsetDeg, 0f) * Vector3.forward;
            Vector3 p = f.HeadPosition + fwd * distance + Vector3.down * drop;
            return KeepOutOfDancers(p, f.HeadPosition);
        }

        /// <summary>
        /// A panel never opens inside the dancers: a spot among them is moved out, to the user's side, just outside their disc. A user who stands
        /// among the dancers (or right at their edge) keeps the spot at the gaze - there is no "user's side" to move it to.
        /// </summary>
        public static Vector3 KeepOutOfDancers(Vector3 p, Vector3 head)
        {
            HeadMovement hm = HeadMovement.Instance;
            if (hm == null || hm.LeadDancer == null) return p;
            Vector3 c = VrViewpoints.CoupleCentre(hm);
            if (new Vector2(p.x - c.x, p.z - c.z).magnitude >= DancerRadius) return p;
            Vector2 toUser = new(head.x - c.x, head.z - c.z);
            if (toUser.magnitude < DancerRadius + 0.7f) return p;
            Vector2 q = new Vector2(c.x, c.z) + toUser.normalized * (DancerRadius + 0.1f);
            return new Vector3(q.x, p.y, q.y);
        }

        /// <summary>true when KeepOutOfDancers would move this spot</summary>
        public static bool WouldMove(Vector3 p, Vector3 head) => (KeepOutOfDancers(p, head) - p).sqrMagnitude > 1e-6f;

        /// <summary>a log line for every placement (adb logcat -s Unity | grep VrAnchor): what was placed, where, how far from the head and the couple</summary>
        public static void Log(string what, Vector3 spot, InputFrame f)
        {
            HeadMovement hm = HeadMovement.Instance;
            Vector3 c = hm != null && hm.LeadDancer != null ? VrViewpoints.CoupleCentre(hm) : Vector3.zero;
            Debug.Log($"VrAnchor: {what} placed at ({spot.x:0.00}, {spot.y:0.00}, {spot.z:0.00}), {new Vector2(spot.x - f.HeadPosition.x, spot.z - f.HeadPosition.z).magnitude:0.00} m ahead of the head " +
                $"({f.HeadPosition.x:0.00}, {f.HeadPosition.y:0.00}, {f.HeadPosition.z:0.00}), {new Vector2(spot.x - c.x, spot.z - c.z).magnitude:0.00} m from the couple");
        }

        /// <summary>stand the panel at the spot, facing the user's head (upright, with a slight tilt toward the eyes)</summary>
        public static void PlaceFacing(VrPanel panel, Vector3 spot, Vector3 head, float pitchFollow = 0.55f)
        {
            panel.FaceFrom(spot, head, pitchFollow);
        }
    }
}
