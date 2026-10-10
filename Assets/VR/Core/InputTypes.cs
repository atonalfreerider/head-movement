using UnityEngine;

namespace HmVr
{
    public enum Hand
    {
        Left = 0,
        Right = 1
    }

    public static class HandExt
    {
        public static Hand Other(this Hand h) => h == Hand.Left ? Hand.Right : Hand.Left;

        /// <summary>+1 for the right hand, -1 for the left (the side of the body the hand lives on)</summary>
        public static float Sign(this Hand h) => h == Hand.Right ? 1f : -1f;
    }

    /// <summary>The 26 joints of a tracked hand, in the OpenXR order (XRHandJointID minus one).</summary>
    public static class HandJoint
    {
        public const int Count = 26;

        public const int Wrist = 0, Palm = 1,
            ThumbMetacarpal = 2, ThumbProximal = 3, ThumbDistal = 4, ThumbTip = 5,
            IndexMetacarpal = 6, IndexProximal = 7, IndexIntermediate = 8, IndexDistal = 9, IndexTip = 10,
            MiddleMetacarpal = 11, MiddleProximal = 12, MiddleIntermediate = 13, MiddleDistal = 14, MiddleTip = 15,
            RingMetacarpal = 16, RingProximal = 17, RingIntermediate = 18, RingDistal = 19, RingTip = 20,
            LittleMetacarpal = 21, LittleProximal = 22, LittleIntermediate = 23, LittleDistal = 24, LittleTip = 25;

        /// <summary>the bones of a hand as joint pairs, for drawing</summary>
        public static readonly int[] Bones =
        {
            Wrist, ThumbMetacarpal, ThumbMetacarpal, ThumbProximal, ThumbProximal, ThumbDistal, ThumbDistal, ThumbTip,
            Wrist, IndexMetacarpal, IndexMetacarpal, IndexProximal, IndexProximal, IndexIntermediate, IndexIntermediate, IndexDistal, IndexDistal, IndexTip,
            Wrist, MiddleMetacarpal, MiddleMetacarpal, MiddleProximal, MiddleProximal, MiddleIntermediate, MiddleIntermediate, MiddleDistal, MiddleDistal, MiddleTip,
            Wrist, RingMetacarpal, RingMetacarpal, RingProximal, RingProximal, RingIntermediate, RingIntermediate, RingDistal, RingDistal, RingTip,
            Wrist, LittleMetacarpal, LittleMetacarpal, LittleProximal, LittleProximal, LittleIntermediate, LittleIntermediate, LittleDistal, LittleDistal, LittleTip
        };
    }

    /// <summary>
    /// One hand for one frame, in WORLD space. Whatever supplies it (the headset's hand tracking or the simulated hands of the tests)
    /// fills the joints; everything the viewer needs - pinch, palm direction, the pointing ray, the fingertip - is derived from them
    /// by the gesture classes, so the interaction logic is the same on a headset and in the editor.
    /// </summary>
    public sealed class HandState
    {
        public readonly Hand Side;
        public bool Tracked;
        public readonly Vector3[] Joints = new Vector3[HandJoint.Count];

        public HandState(Hand side)
        {
            Side = side;
        }

        public Vector3 Wrist => Joints[HandJoint.Wrist];
        public Vector3 Palm => Joints[HandJoint.Palm];
        public Vector3 ThumbTip => Joints[HandJoint.ThumbTip];
        public Vector3 IndexTip => Joints[HandJoint.IndexTip];
        public Vector3 IndexKnuckle => Joints[HandJoint.IndexProximal];
        public Vector3 MiddleKnuckle => Joints[HandJoint.MiddleProximal];
        public Vector3 LittleKnuckle => Joints[HandJoint.LittleProximal];

        /// <summary>the direction of the fingers: wrist to the middle knuckle</summary>
        public Vector3 Forward
        {
            get
            {
                Vector3 f = MiddleKnuckle - Wrist;
                return f.sqrMagnitude > 1e-8f ? f.normalized : Vector3.forward;
            }
        }

        /// <summary>distance between the thumb tip and the index tip</summary>
        public float PinchDistance => Vector3.Distance(ThumbTip, IndexTip);

        /// <summary>hand size relative to an average adult hand (wrist to middle knuckle = 9 cm), clamped</summary>
        public float Scale => Mathf.Clamp(Vector3.Distance(Wrist, MiddleKnuckle) / 0.09f, 0.7f, 1.3f);

        /// <summary>
        /// The direction out of the palm, from the joints themselves (so it does not depend on a runtime's joint-axis convention):
        /// the normal of the wrist - index knuckle - little knuckle triangle, signed for the hand. A right hand held palm down
        /// with the fingers forward gives (0, -1, 0).
        /// </summary>
        public Vector3 PalmNormal
        {
            get
            {
                Vector3 a = IndexKnuckle - Wrist, b = LittleKnuckle - Wrist;
                Vector3 n = Vector3.Cross(a, b);
                if (n.sqrMagnitude < 1e-10f) return Vector3.down;
                n.Normalize();
                return Side == Hand.Right ? -n : n;
            }
        }

        public void CopyFrom(HandState o)
        {
            Tracked = o.Tracked;
            System.Array.Copy(o.Joints, Joints, HandJoint.Count);
        }
    }

    /// <summary>The head and both hands for one frame, world space.</summary>
    public sealed class InputFrame
    {
        public double Time;
        public bool HeadTracked;
        public Vector3 HeadPosition = new(0f, 1.6f, 0f);
        public Quaternion HeadRotation = Quaternion.identity;
        public readonly HandState Left = new(Hand.Left);
        public readonly HandState Right = new(Hand.Right);

        public HandState Get(Hand h) => h == Hand.Left ? Left : Right;

        public Vector3 HeadForward => HeadRotation * Vector3.forward;

        /// <summary>the head's yaw alone (a rotation about the vertical axis): the frame the body lives in</summary>
        public Quaternion HeadYaw
        {
            get
            {
                Vector3 f = HeadForward;
                f.y = 0f;
                if (f.sqrMagnitude < 1e-4f)
                {
                    // looking straight down: the top of the head points where the body faces; straight up: the opposite way
                    Vector3 up = HeadRotation * Vector3.up;
                    up.y = 0f;
                    if (up.sqrMagnitude < 1e-4f) return Quaternion.identity;
                    f = HeadForward.y > 0f ? -up : up;
                }

                return Quaternion.LookRotation(f.normalized, Vector3.up);
            }
        }

        public void CopyFrom(InputFrame o)
        {
            Time = o.Time;
            HeadTracked = o.HeadTracked;
            HeadPosition = o.HeadPosition;
            HeadRotation = o.HeadRotation;
            Left.CopyFrom(o.Left);
            Right.CopyFrom(o.Right);
        }
    }

    /// <summary>Whatever supplies the head and hands: the headset (XR hand tracking) or the simulated hands.</summary>
    public interface IInputSource
    {
        string Name { get; }
        bool IsSimulated { get; }

        /// <summary>fill the frame with the latest poses (world space); the frame keeps its previous values for what the source cannot see</summary>
        void Poll(InputFrame frame, float dt);
    }
}
