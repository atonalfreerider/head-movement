using NUnit.Framework;
using UnityEngine;

namespace HmVr.Tests
{
    /// <summary>The passthrough placement maths: where the dance stands in the real room, and how a two-hand pinch slides and turns it, and the god's eye.</summary>
    public class PlacementTests
    {
        static void Near(Vector2 a, Vector2 b, float eps = 1e-3f) => Assert.Less((a - b).magnitude, eps, $"{a} vs {b}");

        [Test]
        public void Default_PutsThePivotInFrontOfTheUser_AndTheFrontFacesTheUser()
        {
            // the user stands at (1, 0, 2) looking along +X (bearing 90); the dance's front (start vantage) is at world bearing 200 from its pivot
            DancePlacement p = PlacementMath.Default(new Vector3(1f, 1.6f, 2f), 90f, new Vector3(0.3f, 0f, -0.2f), 200f, 2.2f);
            Near(p.ToReal(new Vector2(0.3f, -0.2f)), new Vector2(1f + 2.2f, 2f));
            // a point 3 m from the pivot toward the front bearing lands between the user and the pivot, on the user's line of sight
            Vector2 front = new Vector2(0.3f, -0.2f) + 3f * new Vector2(Mathf.Sin(200f * Mathf.Deg2Rad), Mathf.Cos(200f * Mathf.Deg2Rad));
            Near(p.ToReal(front), new Vector2(1f + 2.2f - 3f, 2f));
            Assert.AreEqual(1f, p.Scale);
        }

        [Test]
        public void RigPose_IsTheInverseOfTheMap_AtLifeSizeOnTheFloor()
        {
            DancePlacement p = new() { Anchor = new Vector2(0.7f, -1.2f), YawDeg = 37f, Pivot = new Vector3(0.4f, 0f, 0.9f) };
            p.RigPose(out Vector3 pos, out Quaternion rot, out float k);
            Assert.AreEqual(1f, k);                 // never scaled
            Assert.AreEqual(0f, pos.y);             // never off the floor
            Assert.AreEqual(1f, p.Scale);
            // a real floor point r, carried by the rig to world, is ToWorld(r)
            foreach (Vector2 r in new[] { new Vector2(0f, 0f), new Vector2(1.3f, 0.2f), new Vector2(-2f, 3f) })
            {
                Vector3 w = pos + rot * new Vector3(r.x, 0f, r.y);
                Near(new Vector2(w.x, w.z), p.ToWorld(r));
            }

            // the anchor (where the pivot stands in the room) maps back to the pivot
            Vector3 w0 = pos + rot * new Vector3(p.Anchor.x, 0f, p.Anchor.y);
            Near(new Vector2(w0.x, w0.z), new Vector2(0.4f, 0.9f));
        }

        [Test]
        public void ToWorld_ToReal_RoundTrip()
        {
            DancePlacement p = new() { Anchor = new Vector2(-1f, 2f), YawDeg = -120f, Pivot = new Vector3(1f, 0f, 1f) };
            Vector2 w = new(2.5f, -0.5f);
            Near(p.ToWorld(p.ToReal(w)), w);
            Assert.AreEqual((w - new Vector2(1f, 1f)).magnitude, (p.ToReal(w) - p.Anchor).magnitude, 1e-4f); // rigid: distances kept
        }

        [Test]
        public void TwoHands_DraggingBoth_SlidesTheFloor()
        {
            DancePlacement p0 = new() { Anchor = new Vector2(0f, 3f), YawDeg = 10f, Pivot = Vector3.zero };
            DancePlacement p = PlacementMath.TwoHands(p0, new Vector2(-0.15f, 0.4f), new Vector2(0.15f, 0.4f), new Vector2(0.35f, 0.3f), new Vector2(0.65f, 0.3f));
            Near(p.Anchor, new Vector2(0.5f, 2.9f));
            Assert.AreEqual(10f, p.YawDeg, 1e-3f);
            Assert.AreEqual(0f, p.Lift);
        }

        [Test]
        public void TwoHands_TurningTheLine_TurnsTheFloorAboutTheMidpoint()
        {
            DancePlacement p0 = new() { Anchor = new Vector2(0f, 2f), YawDeg = 0f, Pivot = Vector3.zero };
            DancePlacement p = PlacementMath.TwoHands(p0, new Vector2(-0.1f, 1f), new Vector2(0.1f, 1f), new Vector2(0f, 0.9f), new Vector2(0f, 1.1f));
            // the line from the left hand to the right hand turned from +X (bearing 90) to +Z (bearing 0): -90 degrees
            Assert.AreEqual(-90f, p.YawDeg, 1e-3f);
            // about the midpoint (0, 1): the pivot 1 m beyond it is now 1 m to its left
            Near(p.Anchor, new Vector2(-1f, 1f));
        }

        [Test]
        public void TwoHands_SpreadingTheHands_NeverScales()
        {
            DancePlacement p0 = new() { Anchor = new Vector2(0f, 2f), YawDeg = 0f, Pivot = Vector3.zero };
            DancePlacement spread = PlacementMath.TwoHands(p0, new Vector2(-0.1f, 1f), new Vector2(0.1f, 1f), new Vector2(-1f, 1f), new Vector2(1f, 1f));
            Near(spread.Anchor, p0.Anchor);
            Assert.AreEqual(0f, spread.YawDeg, 1e-3f);
            Assert.AreEqual(1f, spread.Scale);
            // the distances between world points are kept whatever the hands do (rigid)
            DancePlacement any = PlacementMath.TwoHands(p0, new Vector2(-0.1f, 1f), new Vector2(0.1f, 1f), new Vector2(0.4f, 0.5f), new Vector2(0.3f, 1.4f));
            Vector2 w1 = new(1f, 1f), w2 = new(-2f, 0.5f);
            Assert.AreEqual((w1 - w2).magnitude, (any.ToReal(w1) - any.ToReal(w2)).magnitude, 1e-4f);
        }

        [Test]
        public void GodsEye_PutsTheCentreBelowTheUser()
        {
            DancePlacement p0 = new() { Anchor = new Vector2(0f, 3f), YawDeg = 15f, Pivot = new Vector3(1f, 0f, 1f) };
            Vector3 head = new(0.2f, 1.6f, 0.1f);
            DancePlacement up = PlacementMath.GodsEye(p0, head, 0f, 4.8f, 0.35f);
            Near(up.Anchor, new Vector2(0.2f, 0.45f));                     // just ahead of the user, below the eyes
            Assert.AreEqual(1.6f - 4.8f, up.Lift, 1e-4f);                  // the dance floor 4.8 m below the eyes
            Assert.AreEqual(15f, up.YawDeg, 1e-4f);                         // not turned
            up.RigPose(out Vector3 pos, out _, out float k);
            Assert.AreEqual(4.8f - 1.6f, pos.y, 1e-4f);                    // the rig rises: the user looks down on the dance
            Assert.AreEqual(1f, k);
        }

        [Test]
        public void Lerp_GoesTheShortWayRound()
        {
            DancePlacement a = new() { Anchor = Vector2.zero, YawDeg = 170f, Lift = 0f };
            DancePlacement b = new() { Anchor = new Vector2(2f, 0f), YawDeg = -170f, Lift = -2f };
            DancePlacement m = PlacementMath.Lerp(a, b, 0.5f);
            Near(m.Anchor, new Vector2(1f, 0f));
            Assert.AreEqual(-1f, m.Lift, 1e-4f);
            Assert.AreEqual(180f, Mathf.Abs(m.YawDeg), 1e-3f);
        }

        [Test]
        public void HandHeading_IgnoresAHandPointingStraightUp()
        {
            HandState h = new(Hand.Right);
            HandSynth.Fill(h, new Vector3(0f, 1.2f, 0.4f), Quaternion.identity, 0f, 0f, true);
            Assert.IsTrue(PlacementMath.HandHeading(h, out float yaw));
            Assert.AreEqual(0f, yaw, 8f); // the synthetic hand's middle knuckle sits a little off its wrist axis
            HandSynth.Fill(h, new Vector3(0f, 1.2f, 0.4f), Quaternion.Euler(-90f, 0f, 0f), 0f, 0f, true);
            Assert.IsFalse(PlacementMath.HandHeading(h, out _));
        }

        [Test]
        public void HandScale_FollowsTheWorldScale()
        {
            HandState h = new(Hand.Right);
            HandSynth.Fill(h, new Vector3(0f, 1.2f, 0.4f), Quaternion.identity, 0f, 0f, true);
            float one = h.Scale;
            Assert.AreEqual(1f, HandState.WorldScale);
            // the same hand seen through a rig scaled by 0.5: its joints are half as far apart, and the hysteresis scales with it
            HandState small = new(Hand.Right);
            for (int i = 0; i < HandJoint.Count; i++) small.Joints[i] = h.Joints[i] * 0.5f;
            small.Tracked = true;
            HandState.WorldScale = 0.5f;
            try
            {
                Assert.AreEqual(one * 0.5f, small.Scale, 0.02f);
            }
            finally
            {
                HandState.WorldScale = 1f;
            }
        }
    }
}
