using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

namespace HmVr
{
    /// <summary>A hand with its recognisers: pinch, palm, pointing ray.</summary>
    public sealed class VrHand
    {
        public readonly Hand Side;
        public HandState State;
        public readonly PinchDetector Pinch = new();
        public readonly PalmDetector Palm = new();
        public readonly AimRay Aim;

        public VrHand(Hand side)
        {
            Side = side;
            Aim = new AimRay(side);
        }

        public bool Tracked => State != null && State.Tracked;
        public Vector3 IndexTip => State.IndexTip;
        public Vector3 PinchPoint => (State.ThumbTip + State.IndexTip) * 0.5f;

        public void Update(InputFrame f, float dt)
        {
            State = f.Get(Side);
            Pinch.Update(State, dt);
            Palm.Update(State, f.HeadPosition, f.HeadRotation, dt);
            Aim.Update(State, f.HeadPosition, f.HeadYaw, dt);
        }

        public void Reset()
        {
            Pinch.Reset();
            Palm.Reset();
            Aim.Reset();
        }
    }

    /// <summary>
    /// Polls the input source (the headset's hand tracking or the simulated hands) once per frame, before everything else, and runs the
    /// recognisers. The pointer system, the locomotion and the panels read their state from here.
    /// </summary>
    [DefaultExecutionOrder(-300)]
    [AddComponentMenu("")]
    public sealed class VrInput : MonoBehaviour
    {
        public static VrInput Instance { get; private set; }

        public readonly InputFrame Frame = new();
        public IInputSource Source;
        public readonly VrHand Left = new(Hand.Left), Right = new(Hand.Right);
        /// <summary>the dominant hand points, selects and teleports; the other holds the hand panel and turns</summary>
        public Hand Dominant = Hand.Right;
        public int FramesPolled { get; private set; }

        public VrHand Get(Hand h) => h == Hand.Left ? Left : Right;
        public VrHand DominantHand => Get(Dominant);
        public VrHand OffHand => Get(Dominant.Other());

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (Source == null) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            Frame.Time = Time.unscaledTimeAsDouble;
            Source.Poll(Frame, dt);
            Left.Update(Frame, dt);
            Right.Update(Frame, dt);
            FramesPolled++;
        }
    }

    /// <summary>Hand tracking from the headset through the XR Hands subsystem (OpenXR hand tracking: Quest Link, Quest standalone).</summary>
    public sealed class XrInputSource : IInputSource
    {
        readonly Transform origin, head;
        XRHandSubsystem hands;
        float nextSearch;
        readonly System.Collections.Generic.List<XRHandSubsystem> buffer = new();

        public XrInputSource(Transform origin, Transform head)
        {
            this.origin = origin;
            this.head = head;
        }

        public string Name => "XR hand tracking";
        public bool IsSimulated => false;
        public bool HandsRunning => hands != null && hands.running;
        public string HandsStatus => hands == null ? "no hand subsystem" : hands.running ? "running" : "stopped";

        public void Poll(InputFrame frame, float dt)
        {
            frame.HeadTracked = true;
            frame.HeadPosition = head.position;
            frame.HeadRotation = head.rotation;
            if (hands == null || !hands.running)
            {
                if (Time.unscaledTime >= nextSearch)
                {
                    nextSearch = Time.unscaledTime + 1f;
                    buffer.Clear();
                    SubsystemManager.GetSubsystems(buffer);
                    hands = null;
                    foreach (XRHandSubsystem s in buffer)
                    {
                        if (s.running) hands = s;
                    }
                }
            }

            if (hands == null || !hands.running)
            {
                frame.Left.Tracked = frame.Right.Tracked = false;
                return;
            }

            Read(hands.leftHand, frame.Left);
            Read(hands.rightHand, frame.Right);
        }

        void Read(XRHand src, HandState dst)
        {
            if (!src.isTracked)
            {
                dst.Tracked = false;
                return;
            }

            int ok = 0;
            for (int i = 0; i < HandJoint.Count; i++)
            {
                XRHandJoint j = src.GetJoint(XRHandJointIDUtility.FromIndex(i));
                if (j.TryGetPose(out Pose p))
                {
                    dst.Joints[i] = origin.TransformPoint(p.position);
                    ok++;
                }
            }

            dst.Tracked = ok >= HandJoint.Count - 2;
        }
    }
}
