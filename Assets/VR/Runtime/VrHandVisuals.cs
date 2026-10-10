using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// Draws the tracked hands: a small sphere per joint and a line along each finger (hand tracking gives the app no hand model), the
    /// fingertip cursor, and the pinch point lit when the fingers close. The hands are drawn from the same joints the recognisers read.
    /// </summary>
    [DefaultExecutionOrder(50)]
    [AddComponentMenu("")]
    public sealed class VrHandVisuals : MonoBehaviour
    {
        sealed class HandView
        {
            public Transform Root;
            public Transform[] Joints = new Transform[HandJoint.Count];
            public MeshRenderer[] Spheres = new MeshRenderer[HandJoint.Count];
            public LineRenderer[] Fingers = new LineRenderer[5];
            public Transform PinchDot;
            public MeshRenderer PinchRenderer;
        }

        readonly HandView[] views = new HandView[2];
        Material skin, skinHot, lineMat;
        public bool Visible = true;

        static readonly int[][] FingerChains =
        {
            new[] { HandJoint.Wrist, HandJoint.ThumbMetacarpal, HandJoint.ThumbProximal, HandJoint.ThumbDistal, HandJoint.ThumbTip },
            new[] { HandJoint.Wrist, HandJoint.IndexMetacarpal, HandJoint.IndexProximal, HandJoint.IndexIntermediate, HandJoint.IndexDistal, HandJoint.IndexTip },
            new[] { HandJoint.Wrist, HandJoint.MiddleMetacarpal, HandJoint.MiddleProximal, HandJoint.MiddleIntermediate, HandJoint.MiddleDistal, HandJoint.MiddleTip },
            new[] { HandJoint.Wrist, HandJoint.RingMetacarpal, HandJoint.RingProximal, HandJoint.RingIntermediate, HandJoint.RingDistal, HandJoint.RingTip },
            new[] { HandJoint.Wrist, HandJoint.LittleMetacarpal, HandJoint.LittleProximal, HandJoint.LittleIntermediate, HandJoint.LittleDistal, HandJoint.LittleTip }
        };

        void Awake()
        {
            skin = VrDraw.Material(new Color(0.78f, 0.86f, 1f, 0.85f));
            skinHot = VrDraw.Material(new Color(1f, 0.839f, 0.039f, 1f));
            lineMat = VrDraw.Material(new Color(0.7f, 0.8f, 1f, 0.55f));
            for (int h = 0; h < 2; h++) views[h] = Build(h == 0 ? "Left hand" : "Right hand");
        }

        void OnDestroy()
        {
            if (skin != null) Destroy(skin);
            if (skinHot != null) Destroy(skinHot);
            if (lineMat != null) Destroy(lineMat);
        }

        HandView Build(string name)
        {
            HandView v = new() { Root = new GameObject(name).transform };
            v.Root.SetParent(transform, false);
            v.Root.gameObject.layer = VrStyle.Layer;
            for (int i = 0; i < HandJoint.Count; i++)
            {
                GameObject s = new($"joint {i}", typeof(MeshFilter), typeof(MeshRenderer));
                s.layer = VrStyle.Layer;
                s.transform.SetParent(v.Root, false);
                s.GetComponent<MeshFilter>().sharedMesh = VrDraw.Sphere;
                MeshRenderer r = s.GetComponent<MeshRenderer>();
                r.sharedMaterial = skin;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.sortingOrder = 31000;
                bool tip = i == HandJoint.ThumbTip || i == HandJoint.IndexTip || i == HandJoint.MiddleTip || i == HandJoint.RingTip || i == HandJoint.LittleTip;
                s.transform.localScale = Vector3.one * (i == HandJoint.IndexTip ? 0.014f : tip ? 0.011f : i == HandJoint.Palm ? 0.02f : 0.008f);
                v.Joints[i] = s.transform;
                v.Spheres[i] = r;
            }

            for (int f = 0; f < 5; f++) v.Fingers[f] = VrDraw.Line(v.Root, $"finger {f}", lineMat, 0.005f);
            GameObject dot = new("pinch point", typeof(MeshFilter), typeof(MeshRenderer));
            dot.layer = VrStyle.Layer;
            dot.transform.SetParent(v.Root, false);
            dot.GetComponent<MeshFilter>().sharedMesh = VrDraw.Sphere;
            v.PinchRenderer = dot.GetComponent<MeshRenderer>();
            v.PinchRenderer.sharedMaterial = skinHot;
            v.PinchRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            v.PinchRenderer.sortingOrder = 31000;
            v.PinchDot = dot.transform;
            v.PinchDot.localScale = Vector3.one * 0.02f;
            return v;
        }

        void LateUpdate()
        {
            VrInput input = VrInput.Instance;
            for (int h = 0; h < 2; h++)
            {
                HandView v = views[h];
                VrHand hand = input != null ? input.Get((Hand)h) : null;
                bool on = Visible && hand != null && hand.Tracked;
                if (v.Root.gameObject.activeSelf != on) v.Root.gameObject.SetActive(on);
                if (!on) continue;
                for (int i = 0; i < HandJoint.Count; i++) v.Joints[i].position = hand.State.Joints[i];
                for (int f = 0; f < 5; f++)
                {
                    int[] chain = FingerChains[f];
                    LineRenderer l = v.Fingers[f];
                    l.enabled = true;
                    l.positionCount = chain.Length;
                    for (int k = 0; k < chain.Length; k++) l.SetPosition(k, hand.State.Joints[chain[k]]);
                }

                bool pinch = hand.Pinch.Held;
                v.PinchRenderer.enabled = pinch;
                if (pinch) v.PinchDot.position = hand.PinchPoint;
                v.Spheres[HandJoint.IndexTip].sharedMaterial = pinch ? skinHot : skin;
                v.Spheres[HandJoint.ThumbTip].sharedMaterial = pinch ? skinHot : skin;
            }
        }
    }
}
