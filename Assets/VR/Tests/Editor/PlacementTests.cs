using NUnit.Framework;
using UnityEngine;

namespace HmVr.Tests
{
    /// <summary>The passthrough placement maths: where the dance stands in the real room, and how a pinch-drag, a twist and a two-hand spread change it.</summary>
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
        public void OneHand_Drag_SlidesTheFloor_AndNeverTurns()
        {
            DancePlacement p0 = new() { Anchor = new Vector2(0f, 2f), YawDeg = 10f, Pivot = Vector3.zero };
            DancePlacement p = PlacementMath.OneHand(p0, new Vector2(0.2f, 1f), new Vector2(0.7f, 1.4f));
            Near(p.Anchor, new Vector2(0.5f, 2.4f));
            Assert.AreEqual(10f, p.YawDeg, 1e-3f);
        }

        [Test]
        public void TwoHands_RotatingTheLine_TurnsTheFloorAboutTheMidpoint()
        {
            DancePlacement p0 = new() { Anchor = new Vector2(0f, 2f), YawDeg = 0f, Pivot = Vector3.zero };
            DancePlacement p = PlacementMath.TwoHands(p0, new Vector2(-0.1f, 1f), new Vector2(0.1f, 1f), new Vector2(0f, 0.9f), new Vector2(0f, 1.1f));
            // the line from the left hand to the right hand turned from +X (bearing 90) to +Z (bearing 0): -90 degrees
            Assert.AreEqual(-90f, p.YawDeg, 1e-3f);
            // about the midpoint (0, 1): the pivot 1 m beyond it is now 1 m to its left
            Near(p.Anchor, new Vector2(-1f, 1f));
        }

        [Test]
        public void TwoHands_SpreadOrMove_NeverScalesOrSlides()
        {
            DancePlacement p0 = new() { Anchor = new Vector2(0f, 2f), YawDeg = 0f, Pivot = Vector3.zero };
            DancePlacement spread = PlacementMath.TwoHands(p0, new Vector2(-0.1f, 1f), new Vector2(0.1f, 1f), new Vector2(-1f, 1f), new Vector2(1f, 1f));
            Near(spread.Anchor, p0.Anchor);
            Assert.AreEqual(0f, spread.YawDeg, 1e-3f);
            DancePlacement moved = PlacementMath.TwoHands(p0, new Vector2(-0.1f, 1f), new Vector2(0.1f, 1f), new Vector2(0.9f, 3f), new Vector2(1.1f, 3f));
            Near(moved.Anchor, p0.Anchor);
            Assert.AreEqual(1f, moved.Scale);
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
