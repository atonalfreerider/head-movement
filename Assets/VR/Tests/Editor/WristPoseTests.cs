using NUnit.Framework;
using UnityEngine;

namespace HmVr.Tests
{
    /// <summary>The wrist menu's pose: over the back of the wrist, a little toward the elbow, facing the eyes, steady when the hand turns a little.</summary>
    public class WristPoseTests
    {
        static readonly Vector3 Wrist = new(-0.2f, 1.2f, 0.4f), Head = new(0f, 1.6f, 0f);

        static void Near(Vector3 a, Vector3 b, float eps = 1e-4f) => Assert.Less((a - b).magnitude, eps, $"{a} vs {b}");

        [Test]
        public void Panel_SitsOverTheBackOfTheWrist_TowardTheElbow()
        {
            // palm down, fingers forward: the back of the hand is up, the elbow behind
            WristPose.WristPanelPose(Wrist, Vector3.forward, Vector3.down, Head, 0.05f, 0.06f, out Vector3 pos, out _);
            Near(pos, Wrist + new Vector3(0f, 0.05f, -0.06f));

            // palm up: the back of the hand (and the panel) is below
            WristPose.WristPanelPose(Wrist, Vector3.forward, Vector3.up, Head, 0.05f, 0.06f, out pos, out _);
            Near(pos, Wrist + new Vector3(0f, -0.05f, -0.06f));

            // fingers tilted up out of the plane of the hand: the offset along the forearm stays in the plane of the hand
            WristPose.WristPanelPose(Wrist, new Vector3(0f, 0.5f, 1f), Vector3.down, Head, 0.05f, 0.06f, out pos, out _);
            Near(pos, Wrist + new Vector3(0f, 0.05f, -0.06f));

            // palm facing right (+X), fingers forward: the panel is to the left of the wrist
            WristPose.WristPanelPose(Wrist, Vector3.forward, Vector3.right, Head, 0.05f, 0.06f, out pos, out _);
            Near(pos, Wrist + new Vector3(-0.05f, 0f, -0.06f));
        }

        [Test]
        public void Panel_FacesTheHead_Upright()
        {
            WristPose.WristPanelPose(Wrist, Vector3.forward, Vector3.down, Head, 0.05f, 0.06f, out Vector3 pos, out Quaternion rot);
            Vector3 look = (pos - Head).normalized;
            Assert.Greater(Vector3.Dot(rot * Vector3.forward, look), 0.9999f);   // forward points from the head to the panel
            Assert.Greater((rot * Vector3.up).y, 0f);                              // upright
            Assert.AreEqual(0f, (rot * Vector3.right).y, 1e-4f);                   // no roll
        }

        [Test]
        public void Panel_StraightBelowTheHead_StillHasAPose()
        {
            Vector3 wrist = Head + new Vector3(0f, -0.5f, 0f);
            WristPose.WristPanelPose(wrist, Vector3.forward, Vector3.up, Head, 0.05f, 0f, out Vector3 pos, out Quaternion rot);
            Near(pos, wrist + new Vector3(0f, -0.05f, 0f));
            Assert.Greater(Vector3.Dot(rot * Vector3.forward, Vector3.down), 0.9999f);
            Assert.IsFalse(float.IsNaN(rot.x) || float.IsNaN(rot.w));
        }

        [Test]
        public void Panel_IsDeterministic_AndSteadyWhenTheHandTurnsALittle()
        {
            WristPose.WristPanelPose(Wrist, Vector3.forward, Vector3.down, Head, 0.05f, 0.06f, out Vector3 p0, out Quaternion r0);
            WristPose.WristPanelPose(Wrist, Vector3.forward, Vector3.down, Head, 0.05f, 0.06f, out Vector3 p1, out Quaternion r1);
            Assert.AreEqual(p0, p1);
            Assert.AreEqual(r0, r1);

            // roll the hand 5 degrees about the forearm: the panel moves by about 5 mm and turns by well under a degree
            Vector3 n = Quaternion.AngleAxis(5f, Vector3.forward) * Vector3.down;
            WristPose.WristPanelPose(Wrist, Vector3.forward, n, Head, 0.05f, 0.06f, out Vector3 p2, out Quaternion r2);
            Assert.Less((p2 - p0).magnitude, 0.006f);
            Assert.Less(Quaternion.Angle(r0, r2), 1f);
        }
    }
}
