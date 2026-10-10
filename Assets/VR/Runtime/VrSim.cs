using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// Simulated head and hands for the editor and the tests: the same joints a headset delivers (HandSynth), kept in the rig's space
    /// like a tracked hand (so a teleport or a snap turn carries them along), moved toward their targets at a finite speed so the
    /// recognisers see the same kind of motion as from a real hand. Used by Tools/playtest_vr.ps1 through `hm_vr`, and by the desktop
    /// preview of the VR mode.
    /// </summary>
    public sealed class SimInputSource : IInputSource
    {
        public string Name => "simulated hands";
        public bool IsSimulated => true;

        readonly Transform origin;
        public Vector3 HeadLocalPos = new(0f, 1.6f, 0f);
        public float HeadYawLocalDeg, HeadPitchLocalDeg;
        public bool HeadTracked = true;
        /// <summary>m/s a simulated hand moves at (a quick but human reach)</summary>
        public float HandSpeed = 4f;
        public float TurnSpeedDeg = 720f;
        public bool Instant;

        class Sim
        {
            public Vector3 Pos, TargetPos;
            public Quaternion Rot = Quaternion.identity, TargetRot = Quaternion.identity;
            public float Pinch, TargetPinch, Curl, TargetCurl;
            public bool Tracked;
            public bool Initialised;
        }

        readonly Sim[] hands = { new(), new() };

        public SimInputSource(Transform origin)
        {
            this.origin = origin;
            Rest(Hand.Left);
            Rest(Hand.Right);
            Settle();
        }

        public Quaternion HeadLocalRot => Quaternion.Euler(HeadPitchLocalDeg, HeadYawLocalDeg, 0f);

        public Vector3 HeadWorldPos => origin.TransformPoint(HeadLocalPos);
        public Quaternion HeadWorldRot => origin.rotation * HeadLocalRot;

        public Quaternion HeadWorldYaw => Quaternion.Euler(0f, origin.eulerAngles.y + HeadYawLocalDeg, 0f);

        // ------------------------------------------------------------------ driving the simulation (world coordinates in, rig-local kept)

        public void Settle()
        {
            foreach (Sim s in hands)
            {
                s.Pos = s.TargetPos;
                s.Rot = s.TargetRot;
                s.Pinch = s.TargetPinch;
                s.Curl = s.TargetCurl;
                s.Initialised = true;
            }
        }

        /// <summary>the head looks at a world point (yaw and pitch), staying where it is</summary>
        public void LookAt(Vector3 worldPoint)
        {
            Vector3 d = origin.InverseTransformPoint(worldPoint) - HeadLocalPos;
            HeadYawLocalDeg = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            HeadPitchLocalDeg = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
        }

        public void SetHandWorld(Hand side, Vector3 palm, Quaternion rot, float pinch, float curl, bool tracked = true)
        {
            Sim s = hands[(int)side];
            s.TargetPos = origin.InverseTransformPoint(palm);
            s.TargetRot = Quaternion.Inverse(origin.rotation) * rot;
            s.TargetPinch = pinch;
            s.TargetCurl = curl;
            s.Tracked = tracked;
            if (!s.Initialised || Instant) Settle();
        }

        public void SetPinch(Hand side, float pinch) => hands[(int)side].TargetPinch = pinch;

        public void SetTracked(Hand side, bool tracked) => hands[(int)side].Tracked = tracked;

        public bool IsTracked(Hand side) => hands[(int)side].Tracked;

        /// <summary>a hand hanging by the hip</summary>
        public void Rest(Hand side)
        {
            Vector3 p = HeadWorldPos + HeadWorldYaw * new Vector3(0.24f * side.Sign(), -0.78f, 0.06f);
            Quaternion r = HeadWorldYaw * Quaternion.Euler(70f, 0f, side == Hand.Right ? 90f : -90f); // fingers down, palm toward the leg
            SetHandWorld(side, p, r, 0f, 0.3f);
        }

        /// <summary>point at a world position: the arm aims so that the shoulder-to-knuckle ray goes through it</summary>
        public void AimAt(Hand side, Vector3 worldTarget, float pinch, float reach = 0.42f)
        {
            Vector3 shoulder = AimRay.ShoulderOf(side, HeadWorldPos, HeadWorldYaw);
            Vector3 dir = (worldTarget - shoulder).normalized;
            Quaternion r = Quaternion.LookRotation(dir, Vector3.up);
            Vector3 knuckleLocal = KnuckleOffset(side);
            Vector3 knuckle = shoulder + dir * reach;
            SetHandWorld(side, knuckle - r * knuckleLocal, r, pinch, 0f);
        }

        /// <summary>an index fingertip at a point, the finger pointing into the surface whose outward normal is given; depth = metres in front of it</summary>
        public void PokeAt(Hand side, Vector3 worldPoint, Vector3 surfaceNormal, float depth)
        {
            Vector3 n = surfaceNormal.normalized;
            Vector3 tip = worldPoint + n * depth;
            Quaternion r = Quaternion.LookRotation(-n, Vector3.up);
            if (Mathf.Abs(Vector3.Dot(n, Vector3.up)) > 0.95f) r = Quaternion.LookRotation(-n, Vector3.forward);
            SetHandWorld(side, tip - r * TipOffset(side), r, 0f, 1f);
        }

        /// <summary>the hand raised in front of the face with the palm up (to look at it)</summary>
        public void PalmUp(Hand side, bool on)
        {
            if (!on)
            {
                Rest(side);
                return;
            }

            Quaternion yaw = HeadWorldYaw;
            Vector3 p = HeadWorldPos + yaw * new Vector3(0.11f * side.Sign(), -0.34f, 0.40f);
            Quaternion r = yaw * Quaternion.Euler(-14f, 0f, 180f); // fingers forward and slightly up, the back of the hand down
            SetHandWorld(side, p, r, 0f, 0.15f);
        }

        /// <summary>the hand held out to the side at head height, palm down (pinch-and-turn starts here)</summary>
        public void HoldOut(Hand side, float lateral, float pinch)
        {
            Quaternion yaw = HeadWorldYaw;
            Vector3 p = HeadWorldPos + yaw * new Vector3(lateral, -0.35f, 0.40f);
            SetHandWorld(side, p, yaw, pinch, 0f);
        }

        static Vector3 KnuckleOffset(Hand side)
        {
            HandState tmp = new(side);
            HandSynth.Fill(tmp, Vector3.zero, Quaternion.identity, 0f, 0f, true);
            return tmp.IndexKnuckle;
        }

        static Vector3 TipOffset(Hand side)
        {
            HandState tmp = new(side);
            HandSynth.Fill(tmp, Vector3.zero, Quaternion.identity, 0f, 1f, true);
            return tmp.IndexTip;
        }

        // ------------------------------------------------------------------ IInputSource

        public void Poll(InputFrame frame, float dt)
        {
            frame.HeadTracked = HeadTracked;
            frame.HeadPosition = HeadWorldPos;
            frame.HeadRotation = HeadWorldRot;
            for (int i = 0; i < 2; i++)
            {
                Sim s = hands[i];
                if (Instant)
                {
                    s.Pos = s.TargetPos;
                    s.Rot = s.TargetRot;
                    s.Pinch = s.TargetPinch;
                    s.Curl = s.TargetCurl;
                }
                else
                {
                    s.Pos = Vector3.MoveTowards(s.Pos, s.TargetPos, HandSpeed * dt);
                    s.Rot = Quaternion.RotateTowards(s.Rot, s.TargetRot, TurnSpeedDeg * dt);
                    s.Pinch = Mathf.MoveTowards(s.Pinch, s.TargetPinch, 8f * dt);
                    s.Curl = Mathf.MoveTowards(s.Curl, s.TargetCurl, 6f * dt);
                }

                HandSynth.Fill(frame.Get((Hand)i), origin.TransformPoint(s.Pos), origin.rotation * s.Rot, s.Pinch, s.Curl, s.Tracked);
            }
        }
    }
}
