using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// Thumb-tip to index-tip pinch with hysteresis (closes under EnterM, opens over ExitM, scaled with the hand's size) and a short
    /// debounce on both edges, so tracking noise cannot flicker a press. A hand that stops being tracked releases after LostGraceS.
    /// Down and Up are true for the one Update call in which the pinch started / ended.
    /// </summary>
    public sealed class PinchDetector
    {
        public float EnterM = 0.024f, ExitM = 0.040f, LostGraceS = 0.12f;
        public bool Held => deb.Value;
        public bool Down { get; private set; }
        public bool Up { get; private set; }
        public float Distance { get; private set; } = 1f;
        public float HeldSeconds { get; private set; }
        readonly Debouncer deb = new(0.03f, 0.05f);
        float lost;

        public void Reset()
        {
            deb.Reset(false);
            Down = Up = false;
            HeldSeconds = 0f;
            lost = 0f;
        }

        public void Update(HandState h, float dt)
        {
            Down = Up = false;
            bool was = deb.Value;
            if (h != null && h.Tracked)
            {
                lost = 0f;
                float s = h.Scale;
                Distance = h.PinchDistance;
                deb.Update(deb.Value ? Distance < ExitM * s : Distance < EnterM * s, dt);
            }
            else
            {
                lost += dt; // a short loss keeps the pinch; a long one lets go
                if (lost >= LostGraceS) deb.Reset(false);
            }

            if (deb.Value && !was) Down = true;
            if (!deb.Value && was) Up = true;
            HeldSeconds = deb.Value ? HeldSeconds + dt : 0f;
        }
    }

    /// <summary>
    /// Palm facing up or toward the face, with the hand raised and in view: the gesture that shows the hand panel. Entering needs the
    /// palm within EnterAngle of the vertical (or of the face) for ShowDelayS and the hand inside a cone around the gaze; leaving
    /// needs it to fail the wider ExitAngle / cone for HideGraceS, so the panel does not vanish while the other hand pokes it.
    /// </summary>
    public sealed class PalmDetector
    {
        public float EnterUpDeg = 40f, ExitUpDeg = 62f, EnterFaceDeg = 35f, ExitFaceDeg = 55f;
        public float EnterGazeDeg = 55f, ExitGazeDeg = 80f, MinDistanceM = 0.12f, MaxDistanceM = 0.75f, MinBelowEyeM = -0.85f;
        public bool Facing => deb.Value;
        public Vector3 Normal { get; private set; } = Vector3.down;
        readonly Debouncer deb = new(0.18f, 0.45f);

        public void Reset() => deb.Reset(false);

        public void Update(HandState h, Vector3 headPos, Quaternion headRot, float dt)
        {
            bool raw = false;
            if (h != null && h.Tracked)
            {
                Vector3 n = h.PalmNormal;
                Normal = n;
                bool was = deb.Value;
                Vector3 toHand = h.Palm - headPos;
                float dist = toHand.magnitude;
                float up = Vector3.Angle(n, Vector3.up); // angle between the palm's normal and straight up
                Vector3 toHead = (headPos - h.Palm).normalized;
                float face = Vector3.Angle(n, toHead);
                float gaze = dist > 1e-3f ? Vector3.Angle(headRot * Vector3.forward, toHand) : 0f;
                bool palmOk = was ? (up < ExitUpDeg || face < ExitFaceDeg) : (up < EnterUpDeg || face < EnterFaceDeg);
                bool gazeOk = gaze < (was ? ExitGazeDeg : EnterGazeDeg);
                bool rangeOk = dist >= MinDistanceM && dist <= MaxDistanceM && toHand.y >= MinBelowEyeM;
                raw = palmOk && gazeOk && rangeOk;
            }

            deb.Update(raw, dt);
        }
    }

    /// <summary>
    /// The pointing ray of a hand: from an estimated shoulder through the hand's index knuckle (a point that does not move when the
    /// fingers pinch), filtered with a One Euro filter. The ray starts at the knuckle. This is how most hand-tracking apps point:
    /// the whole arm aims, the fingers only select.
    /// </summary>
    public sealed class AimRay
    {
        public Vector3 Origin { get; private set; }
        public Vector3 Direction { get; private set; } = Vector3.forward;
        public bool Valid { get; private set; }
        readonly OneEuroVector3 filter = new() { MinCutoff = 1.2f, Beta = 0.8f };
        readonly Hand side;

        public AimRay(Hand side)
        {
            this.side = side;
        }

        public static Vector3 ShoulderOf(Hand side, Vector3 headPos, Quaternion headYaw) =>
            headPos + headYaw * new Vector3(0.17f * side.Sign(), -0.22f, -0.03f);

        public void Reset()
        {
            filter.Reset();
            Valid = false;
        }

        public void Update(HandState h, Vector3 headPos, Quaternion headYaw, float dt)
        {
            if (h == null || !h.Tracked)
            {
                Valid = false;
                filter.Reset();
                return;
            }

            Vector3 shoulder = ShoulderOf(side, headPos, headYaw);
            Vector3 aimPoint = h.IndexKnuckle;
            Vector3 d = aimPoint - shoulder;
            if (d.sqrMagnitude < 1e-4f)
            {
                Valid = false;
                return;
            }

            Vector3 f = filter.Filter(d.normalized, dt);
            Direction = f.sqrMagnitude > 1e-6f ? f.normalized : d.normalized;
            Origin = aimPoint;
            Valid = true;
        }
    }

    /// <summary>
    /// A fingertip pushing into a surface. The press happens on the frame the tip crosses PressDepth (metres in front of the surface)
    /// coming from the front after having been clear of it (ArmDepth); it ends when the tip withdraws past ReleaseDepth, leaves the
    /// target, goes right through (MaxPenetration) or the hand is lost. A hand resting on the surface cannot press twice.
    /// </summary>
    public sealed class PokeDetector
    {
        public float PressDepth = 0.006f, ReleaseDepth = 0.022f, ArmDepth = 0.030f, MaxPenetration = 0.05f;
        public bool Pressed { get; private set; }
        public bool Armed { get; private set; } = true;
        float prev = float.NaN;

        public void Reset()
        {
            Pressed = false;
            Armed = true;
            prev = float.NaN;
        }

        /// <summary>depth = signed distance of the tip in front of the surface, inside = the tip is over the target; true on the press frame</summary>
        public bool Update(float depth, bool inside, bool tracked)
        {
            bool started = false;
            if (!tracked)
            {
                Reset();
                return false;
            }

            if (!inside)
            {
                Pressed = false;
                Armed = true;
                prev = depth;
                return false;
            }

            if (Pressed)
            {
                if (depth > ReleaseDepth)
                {
                    Pressed = false;
                    Armed = depth > ArmDepth;
                }
                else if (depth < -MaxPenetration)
                {
                    Pressed = false;
                    Armed = false;
                }
            }
            else
            {
                if (depth > ArmDepth) Armed = true;
                if (Armed && !float.IsNaN(prev) && prev > PressDepth && depth <= PressDepth && depth >= -MaxPenetration)
                {
                    Pressed = true;
                    Armed = false;
                    started = true;
                }
            }

            prev = depth;
            return started;
        }
    }

    /// <summary>
    /// Pinch-and-move to snap-turn: pinch with the (non-dominant) hand and move it to the side; a move of TriggerM turns one step, and
    /// the hand must come back within RearmM of where the pinch began before the next step. A cooldown keeps one flick one turn.
    /// </summary>
    public sealed class SnapTurnGesture
    {
        public float TriggerM = 0.10f, RearmM = 0.045f, CooldownS = 0.35f;
        float anchorX, cool;
        bool armed, active;

        public bool Active => active;

        public void Reset()
        {
            active = false;
            armed = false;
            cool = 0f;
        }

        /// <summary>handLocalX = the hand's position to the right of the head, in the head's yaw frame. Returns -1 (turn left), 0, +1 (turn right).</summary>
        public int Update(bool pinching, float handLocalX, float dt)
        {
            cool = Mathf.Max(0f, cool - dt);
            if (!pinching)
            {
                active = false;
                return 0;
            }

            if (!active)
            {
                active = true;
                anchorX = handLocalX;
                armed = true;
                return 0;
            }

            float dx = handLocalX - anchorX;
            if (!armed)
            {
                if (Mathf.Abs(dx) <= RearmM) armed = true;
                return 0;
            }

            if (cool > 0f || Mathf.Abs(dx) < TriggerM) return 0;
            armed = false;
            cool = CooldownS;
            return dx > 0f ? 1 : -1;
        }
    }
}
