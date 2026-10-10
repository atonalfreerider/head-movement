using NUnit.Framework;
using UnityEngine;

namespace HmVr.Tests
{
    /// <summary>
    /// The interaction logic of the VR mode against simulated hands (no headset, no scene): pinch, palm, pointing ray, poke, snap turn,
    /// teleport arc, panel geometry, slider snapping and the viewpoint maths. The scene-level scenarios (menu, lesson, hand panel) run
    /// in Play mode: Tools/playtest_vr.ps1.
    /// </summary>
    public class GestureTests
    {
        const float Dt = 1f / 72f;

        static HandState MakeHand(HmVr.Hand side, Vector3 palm, Quaternion rot, float pinch = 0f, float curl = 0f, bool tracked = true)
        {
            HandState h = new(side);
            HandSynth.Fill(h, palm, rot, pinch, curl, tracked);
            return h;
        }

        // ------------------------------------------------------------------ hand model

        [Test]
        public void PalmNormal_RightHandPalmDown_PointsDown()
        {
            HandState h = MakeHand(HmVr.Hand.Right, new Vector3(0.2f, 1.2f, 0.4f), Quaternion.identity);
            Assert.Greater(Vector3.Dot(h.PalmNormal, Vector3.down), 0.99f);
        }

        [Test]
        public void PalmNormal_LeftHandPalmDown_PointsDown()
        {
            HandState h = MakeHand(HmVr.Hand.Left, new Vector3(-0.2f, 1.2f, 0.4f), Quaternion.identity);
            Assert.Greater(Vector3.Dot(h.PalmNormal, Vector3.down), 0.99f);
        }

        [Test]
        public void PalmNormal_HandRolledOver_PointsUp()
        {
            Quaternion rolled = Quaternion.AngleAxis(180f, Vector3.forward); // roll about the finger axis
            Assert.Greater(Vector3.Dot(MakeHand(HmVr.Hand.Left, Vector3.zero, rolled).PalmNormal, Vector3.up), 0.99f);
            Assert.Greater(Vector3.Dot(MakeHand(HmVr.Hand.Right, Vector3.zero, rolled).PalmNormal, Vector3.up), 0.99f);
        }

        [Test]
        public void HandSynth_OpenHandIsNotPinching_ClosedHandIs()
        {
            float open = MakeHand(HmVr.Hand.Right, Vector3.zero, Quaternion.identity, 0f).PinchDistance;
            float closed = MakeHand(HmVr.Hand.Right, Vector3.zero, Quaternion.identity, 1f).PinchDistance;
            Assert.Greater(open, 0.06f);
            Assert.Less(closed, 0.012f);
        }

        // ------------------------------------------------------------------ pinch

        [Test]
        public void Pinch_ClosingAndOpening_RaisesDownThenUpOnce()
        {
            PinchDetector p = new();
            int downs = 0, ups = 0;
            for (float k = 0f; k <= 1.001f; k += 0.02f) // closing over a second
            {
                p.Update(MakeHand(HmVr.Hand.Right, Vector3.zero, Quaternion.identity, k), Dt);
                if (p.Down) downs++;
                if (p.Up) ups++;
            }

            Assert.IsTrue(p.Held);
            for (float k = 1f; k >= -0.001f; k -= 0.02f)
            {
                p.Update(MakeHand(HmVr.Hand.Right, Vector3.zero, Quaternion.identity, k), Dt);
                if (p.Down) downs++;
                if (p.Up) ups++;
            }

            Assert.IsFalse(p.Held);
            Assert.AreEqual(1, downs);
            Assert.AreEqual(1, ups);
        }

        [Test]
        public void Pinch_NoiseAroundTheThreshold_DoesNotFlicker()
        {
            PinchDetector p = new();
            HandState h = new(HmVr.Hand.Right);
            for (int i = 0; i < 30; i++) p.Update(MakeHand(HmVr.Hand.Right, Vector3.zero, Quaternion.identity, 1f), Dt);
            Assert.IsTrue(p.Held);
            int ups = 0;
            Random.InitState(7);
            for (int i = 0; i < 200; i++)
            {
                // fingertip noise of +-3 mm around a distance between the enter and exit thresholds
                h = MakeHand(HmVr.Hand.Right, Vector3.zero, Quaternion.identity, 1f);
                h.Joints[HandJoint.ThumbTip] = h.IndexTip + new Vector3(0.030f + Random.Range(-0.003f, 0.003f), 0f, 0f);
                p.Update(h, Dt);
                if (p.Up) ups++;
            }

            Assert.AreEqual(0, ups, "hysteresis keeps the pinch inside the band");
            Assert.IsTrue(p.Held);
        }

        [Test]
        public void Pinch_SingleFrameOfPinch_IsIgnored()
        {
            PinchDetector p = new();
            for (int i = 0; i < 10; i++) p.Update(MakeHand(HmVr.Hand.Right, Vector3.zero, Quaternion.identity, 0f), Dt);
            p.Update(MakeHand(HmVr.Hand.Right, Vector3.zero, Quaternion.identity, 1f), Dt);
            p.Update(MakeHand(HmVr.Hand.Right, Vector3.zero, Quaternion.identity, 0f), Dt);
            Assert.IsFalse(p.Held);
            Assert.IsFalse(p.Down);
        }

        [Test]
        public void Pinch_LostTracking_ShortGapKeepsItLongGapReleases()
        {
            PinchDetector p = new();
            for (int i = 0; i < 20; i++) p.Update(MakeHand(HmVr.Hand.Right, Vector3.zero, Quaternion.identity, 1f), Dt);
            Assert.IsTrue(p.Held);
            HandState lost = MakeHand(HmVr.Hand.Right, Vector3.zero, Quaternion.identity, 1f, 0f, false);
            for (int i = 0; i < 4; i++) p.Update(lost, Dt); // 55 ms
            Assert.IsTrue(p.Held);
            bool up = false;
            for (int i = 0; i < 20; i++)
            {
                p.Update(lost, Dt);
                up |= p.Up;
            }

            Assert.IsFalse(p.Held);
            Assert.IsTrue(up);
        }

        // ------------------------------------------------------------------ palm

        static readonly Vector3 Head = new(0f, 1.65f, 0f);

        [Test]
        public void Palm_RaisedPalmUpInView_ShowsAfterTheDwell_AndHidesAfterTheGrace()
        {
            PalmDetector d = new();
            Quaternion up = Quaternion.AngleAxis(180f, Vector3.forward);
            HandState h = MakeHand(HmVr.Hand.Left, new Vector3(-0.12f, 1.25f, 0.38f), up);
            d.Update(h, Head, Quaternion.identity, Dt);
            Assert.IsFalse(d.Facing, "not on the first frame");
            for (int i = 0; i < 20; i++) d.Update(h, Head, Quaternion.identity, Dt);
            Assert.IsTrue(d.Facing);
            // the hand drops out of the view cone for a moment: still shown (grace), then hidden
            HandState low = MakeHand(HmVr.Hand.Left, new Vector3(-0.12f, 0.6f, 0.1f), up);
            d.Update(low, Head, Quaternion.identity, Dt);
            Assert.IsTrue(d.Facing);
            for (int i = 0; i < 60; i++) d.Update(low, Head, Quaternion.identity, Dt);
            Assert.IsFalse(d.Facing);
        }

        [Test]
        public void Palm_PalmDown_NeverShows()
        {
            PalmDetector d = new();
            HandState h = MakeHand(HmVr.Hand.Left, new Vector3(-0.12f, 1.35f, 0.38f), Quaternion.identity);
            for (int i = 0; i < 100; i++) d.Update(h, Head, Quaternion.identity, Dt);
            Assert.IsFalse(d.Facing);
        }

        [Test]
        public void Palm_HandBehindTheHead_NeverShows()
        {
            PalmDetector d = new();
            HandState h = MakeHand(HmVr.Hand.Left, new Vector3(-0.2f, 1.3f, -0.4f), Quaternion.AngleAxis(180f, Vector3.forward));
            for (int i = 0; i < 100; i++) d.Update(h, Head, Quaternion.identity, Dt);
            Assert.IsFalse(d.Facing);
        }

        // ------------------------------------------------------------------ aim ray

        [Test]
        public void AimRay_GoesFromTheShoulderThroughTheKnuckle()
        {
            AimRay ray = new(HmVr.Hand.Right);
            Quaternion yaw = Quaternion.identity;
            Vector3 shoulder = AimRay.ShoulderOf(HmVr.Hand.Right, Head, yaw);
            HandState h = MakeHand(HmVr.Hand.Right, shoulder + new Vector3(0.05f, 0f, 0.35f), Quaternion.identity);
            for (int i = 0; i < 30; i++) ray.Update(h, Head, yaw, Dt);
            Assert.IsTrue(ray.Valid);
            Vector3 want = (h.IndexKnuckle - shoulder).normalized;
            Assert.Greater(Vector3.Dot(ray.Direction, want), 0.9999f);
            Assert.Less((ray.Origin - h.IndexKnuckle).magnitude, 1e-4f);
        }

        [Test]
        public void AimRay_DoesNotMoveWhenTheFingersPinch()
        {
            AimRay ray = new(HmVr.Hand.Right);
            Vector3 shoulder = AimRay.ShoulderOf(HmVr.Hand.Right, Head, Quaternion.identity);
            Vector3 palm = shoulder + new Vector3(0.05f, 0f, 0.35f);
            for (int i = 0; i < 30; i++) ray.Update(MakeHand(HmVr.Hand.Right, palm, Quaternion.identity, 0f), Head, Quaternion.identity, Dt);
            Vector3 open = ray.Direction;
            for (int i = 0; i < 30; i++) ray.Update(MakeHand(HmVr.Hand.Right, palm, Quaternion.identity, 1f), Head, Quaternion.identity, Dt);
            Assert.Greater(Vector3.Dot(open, ray.Direction), 0.9995f, "the knuckle is the aim point: closing the fingers does not swing the ray");
        }

        // ------------------------------------------------------------------ poke

        [Test]
        public void Poke_PressesOnceWhenTheTipReachesTheSurface_AndAgainAfterWithdrawing()
        {
            PokeDetector p = new();
            int presses = 0;
            for (float d = 0.08f; d > -0.02f; d -= 0.004f) if (p.Update(d, true, true)) presses++;
            Assert.AreEqual(1, presses);
            Assert.IsTrue(p.Pressed);
            for (int i = 0; i < 30; i++) if (p.Update(-0.01f, true, true)) presses++; // resting in the surface
            Assert.AreEqual(1, presses);
            for (float d = -0.01f; d < 0.06f; d += 0.005f) p.Update(d, true, true); // withdraw
            Assert.IsFalse(p.Pressed);
            for (float d = 0.06f; d > -0.01f; d -= 0.005f) if (p.Update(d, true, true)) presses++;
            Assert.AreEqual(2, presses);
        }

        [Test]
        public void Poke_SlidingInFromTheSide_OrThroughTheSurface_DoesNotPress()
        {
            PokeDetector p = new();
            int presses = 0;
            // outside the target while deep, then sliding over it at a depth of 3 mm
            for (int i = 0; i < 10; i++) p.Update(0.003f, false, true);
            for (int i = 0; i < 10; i++) if (p.Update(0.003f, true, true)) presses++;
            Assert.AreEqual(0, presses);
            // a hand flung through the surface (more than 5 cm past it in one frame)
            p.Reset();
            p.Update(0.05f, true, true);
            Assert.IsFalse(p.Update(-0.09f, true, true));
        }

        // ------------------------------------------------------------------ snap turn

        [Test]
        public void SnapTurn_PinchAndMoveSideways_TurnsOneStepEachWayAfterReturning()
        {
            SnapTurnGesture g = new();
            Assert.AreEqual(0, g.Update(true, 0.0f, Dt)); // pinch begins at x = 0
            Assert.AreEqual(0, g.Update(true, 0.05f, Dt));
            Assert.AreEqual(1, g.Update(true, 0.12f, Dt), "12 cm to the right turns right");
            Assert.AreEqual(0, g.Update(true, 0.15f, Dt), "no second step until the hand comes back");
            for (int i = 0; i < 40; i++) Assert.AreEqual(0, g.Update(true, 0.02f, Dt));
            Assert.AreEqual(-1, g.Update(true, -0.12f, Dt), "back past the middle and 12 cm to the left turns left");
            Assert.AreEqual(0, g.Update(false, 0f, Dt));
            Assert.IsFalse(g.Active);
        }

        [Test]
        public void SnapTurn_SmallDrift_DoesNothing()
        {
            SnapTurnGesture g = new();
            g.Update(true, 0f, Dt);
            for (int i = 0; i < 200; i++) Assert.AreEqual(0, g.Update(true, 0.03f * Mathf.Sin(i * 0.1f), Dt));
        }

        // ------------------------------------------------------------------ teleport

        [Test]
        public void TeleportArc_LandsOnTheFloorInFrontOfTheHand()
        {
            Vector3[] pts = new Vector3[80];
            Vector3 dir = (Vector3.forward + Vector3.up * 0.2f).normalized;
            bool ok = TeleportMath.Arc(new Vector3(0f, 1.1f, 0f), dir, 0f, pts, out int n, out Vector3 hit);
            Assert.IsTrue(ok);
            Assert.AreEqual(0f, hit.y, 1e-4f);
            Assert.Greater(hit.z, 2f);
            Assert.Less(hit.z, 9f);
            Assert.Greater(n, 5);
        }

        [Test]
        public void TeleportArc_PointedStraightUpLandsBesideTheUser_PointedDownIsClose()
        {
            Vector3[] pts = new Vector3[80];
            Assert.IsTrue(TeleportMath.Arc(new Vector3(0f, 1.1f, 0f), Vector3.up, 0f, pts, out _, out Vector3 straightUp));
            Assert.Less(new Vector2(straightUp.x, straightUp.z).magnitude, 0.5f, "straight up lands back beside the user");
            Assert.IsTrue(TeleportMath.Arc(new Vector3(0f, 1.1f, 0f), (Vector3.forward + Vector3.down).normalized, 0f, pts, out _, out Vector3 near));
            Assert.Less(near.z, 2.5f);
        }

        [Test]
        public void TeleportAllowed_RespectsTheJumpAndTheDisc()
        {
            Assert.IsTrue(TeleportMath.Allowed(new Vector3(3f, 0f, 0f), Vector3.zero, Vector3.zero, 12f, 8f));
            Assert.IsFalse(TeleportMath.Allowed(new Vector3(9f, 0f, 0f), Vector3.zero, Vector3.zero, 12f, 8f));
            Assert.IsFalse(TeleportMath.Allowed(new Vector3(20f, 0f, 0f), new Vector3(15f, 0f, 0f), Vector3.zero, 12f, 8f));
        }

        // ------------------------------------------------------------------ panels, sliders, viewpoints

        [Test]
        public void RectPlane_RayFromTheFront_HitsAtTheRightLocalPoint_FromBehind_Misses()
        {
            // a panel 1 m in front of the origin, facing back toward it (as a canvas that looks at the user does)
            GameObject go = new("panel");
            go.transform.position = new Vector3(0f, 1.5f, 1f);
            go.transform.rotation = Quaternion.LookRotation(Vector3.forward); // forward points away from the reader
            RectPlane plane = RectPlane.OfCanvas(go.transform, new Vector2(0.4f, 0.3f));
            Assert.Greater(Vector3.Dot(plane.Normal, Vector3.back), 0.99f);
            Assert.IsTrue(plane.Raycast(new Vector3(0f, 1.5f, 0f), (new Vector3(0.1f, 1.55f, 1f) - new Vector3(0f, 1.5f, 0f)).normalized, out float t, out Vector2 local));
            Assert.AreEqual(0.1f, local.x, 1e-3f); // +x is to the viewer's right
            Assert.AreEqual(0.05f, local.y, 1e-3f);
            Assert.Greater(t, 0.99f);
            Assert.IsFalse(plane.Raycast(new Vector3(0f, 1.5f, 2f), Vector3.back, out _, out _), "from behind the panel");
            Assert.AreEqual(0.25f, plane.Depth(new Vector3(0f, 1.5f, 0.75f)), 1e-4f);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Slider_PositionAndChapterSnap()
        {
            Assert.AreEqual(0.5f, SliderMath.U(0f, 0.3f), 1e-5f);
            Assert.AreEqual(0f, SliderMath.U(-1f, 0.3f), 1e-5f);
            Assert.AreEqual(1f, SliderMath.U(1f, 0.3f), 1e-5f);
            float[] ticks = { 0.1f, 0.4f, 0.8f };
            Assert.AreEqual(0.4f, SliderMath.Snap(0.41f, ticks, 0.02f), 1e-5f);
            Assert.AreEqual(-1f, SliderMath.Snap(0.5f, ticks, 0.02f), 1e-5f);
        }

        [Test]
        public void Viewpoint_FacingPutsTheUserAtTheDistanceLookingAtTheCentre()
        {
            Viewpoint v = ViewpointMath.Facing("a", "A", "edge", new Vector3(1f, 0f, 2f), 180f, 3f);
            Assert.AreEqual(1f, v.Head.x, 1e-4f);
            Assert.AreEqual(-1f, v.Head.z, 1e-4f);
            Assert.AreEqual(1.6f, v.Head.y, 1e-4f);
            Assert.AreEqual(0f, v.YawDeg, 1e-3f); // facing +z from z = -1 toward z = 2
        }

        [Test]
        public void RigPose_PutsTheHeadWhereAskedWhateverTheUserStandsInTheirRoom()
        {
            Vector3 headLocal = new(0.7f, 1.6f, -0.4f); // the user is standing off to one side of their room
            ViewpointMath.RigPose(new Vector3(2f, 1.6f, -3f), 135f, 0f, headLocal, 20f, out Vector3 rigPos, out float rigYaw);
            Quaternion rig = Quaternion.Euler(0f, rigYaw, 0f);
            Vector3 headWorld = rigPos + rig * headLocal;
            Assert.AreEqual(2f, headWorld.x, 1e-3f);
            Assert.AreEqual(-3f, headWorld.z, 1e-3f);
            Assert.AreEqual(135f, ViewpointMath.Wrap(rigYaw + 20f), 1e-3f); // the head's own yaw adds to the rig's
        }

        [Test]
        public void OneEuro_StillInputIsSteady_FastMotionFollowsClosely()
        {
            OneEuroVector3 f = new();
            Vector3 v = Vector3.zero;
            Random.InitState(3);
            float jitter = 0f;
            for (int i = 0; i < 200; i++)
            {
                v = f.Filter(new Vector3(Random.Range(-0.002f, 0.002f), 0f, 0f), Dt);
                if (i > 50) jitter = Mathf.Max(jitter, Mathf.Abs(v.x));
            }

            Assert.Less(jitter, 0.0015f);
            Vector3 target = Vector3.zero;
            for (int i = 0; i < 20; i++)
            {
                target += new Vector3(0.02f, 0f, 0f); // 1.4 m/s
                v = f.Filter(target, Dt);
            }

            Assert.Less(Mathf.Abs(target.x - v.x), 0.1f);
        }
    }
}
