using NUnit.Framework;
using UnityEngine;

namespace HmVr.Tests
{
    /// <summary>The timeline ring around the dance: the marker opposite the user, time on the ring and back, aiming at it, chapter snapping.</summary>
    public class RingTests
    {
        const float Radius = 3.5f, Height = 3.5f, Band = 0.3f;

        static void Near(Vector3 a, Vector3 b, float eps = 1e-3f) => Assert.Less((a - b).magnitude, eps, $"{a} vs {b}");

        static float CircularGap(float a, float b, float duration)
        {
            float d = Mathf.Abs(a - b) % duration;
            return Mathf.Min(d, duration - d);
        }

        [Test]
        public void Marker_IsOppositeTheUser_AcrossTheCentre()
        {
            Vector3 centre = new(1f, 0f, 1f);
            // the user south of the centre: the marker is due north (+Z, bearing 0)
            Assert.AreEqual(0f, RingMath.MarkerBearing(new Vector3(1f, 1.6f, -2f), centre), 1e-3f);
            // the user east of the centre (+X): the marker is west (bearing -90)
            Assert.AreEqual(-90f, RingMath.MarkerBearing(new Vector3(4f, 1.6f, 1f), centre), 1e-3f);
            // the user north-west: the marker south-east (bearing 135)
            Assert.AreEqual(135f, RingMath.MarkerBearing(new Vector3(-1f, 1.6f, 3f), centre), 1e-3f);
        }

        [Test]
        public void Now_SitsAtTheMarker()
        {
            Assert.AreEqual(40f, RingMath.BearingOfTime(30f, 30f, 100f, 40f), 1e-3f);
            Assert.AreEqual(-170f, RingMath.BearingOfTime(0f, 0f, 100f, 190f), 1e-3f); // wrapped to (-180, 180]
        }

        [Test]
        public void LaterTimes_AreClockwise_LargerBearings()
        {
            float marker = 170f;
            float ahead = RingMath.BearingOfTime(35f, 30f, 100f, marker);
            float behind = RingMath.BearingOfTime(25f, 30f, 100f, marker);
            Assert.AreEqual(18f, Mathf.DeltaAngle(marker, ahead), 1e-3f);   // 5 s of 100 s = 18 degrees clockwise (across the +-180 seam)
            Assert.AreEqual(-18f, Mathf.DeltaAngle(marker, behind), 1e-3f);
            // as the film plays, a fixed later time slides toward the marker
            float later = RingMath.BearingOfTime(35f, 32f, 100f, marker);
            Assert.Less(Mathf.Abs(Mathf.DeltaAngle(marker, later)), Mathf.Abs(Mathf.DeltaAngle(marker, ahead)));
        }

        [Test]
        public void BearingTime_RoundTrip_IncludingTheWrap()
        {
            const float dur = 100f, now = 30f, marker = 40f;
            foreach (float t in new[] { 0f, 0.01f, 12f, 29.5f, 30f, 50f, 79.9f, 80f, 81f, 99.99f })
            {
                float b = RingMath.BearingOfTime(t, now, dur, marker);
                float back = RingMath.TimeAtBearing(b, now, dur, marker);
                Assert.That(back, Is.GreaterThanOrEqualTo(0f).And.LessThan(dur));
                Assert.Less(CircularGap(back, t, dur), 1e-2f, $"t {t} -> bearing {b} -> {back}");
            }

            // just before the start on the ring is the end of the lesson
            float b0 = RingMath.BearingOfTime(0f, now, dur, marker);
            Assert.AreEqual(99.9f, RingMath.TimeAtBearing(b0 - 0.36f, now, dur, marker), 1e-2f);
            Assert.AreEqual(0.1f, RingMath.TimeAtBearing(b0 + 0.36f, now, dur, marker), 1e-2f);
            // the marker itself is now
            Assert.AreEqual(now, RingMath.TimeAtBearing(marker, now, dur, marker), 1e-3f);
        }

        [Test]
        public void Raycast_FarSide_HitsThePlaneOfTheRing()
        {
            Vector3 head = new(0f, 1.6f, -2f), target = new(0f, Height, Radius);
            Assert.IsTrue(RingMath.Raycast(head, target - head, Vector3.zero, Radius, Height, Band, out float bearing, out Vector3 p, out float t));
            Near(p, target);
            Assert.AreEqual(0f, bearing, 1e-2f);
            Assert.AreEqual((target - head).magnitude, t, 1e-3f);
        }

        [Test]
        public void Raycast_SteepNearSide_Misses()
        {
            // nearly straight up: meets the plane 1.9 m from the centre, far inside the ring, and never meets the band of the cylinder
            Vector3 head = new(0f, 1.6f, -2f);
            Assert.IsFalse(RingMath.Raycast(head, new Vector3(0f, 1f, 0.05f), Vector3.zero, Radius, Height, Band, out _, out _, out _));
            Assert.IsFalse(RingMath.Raycast(head, Vector3.up, Vector3.zero, Radius, Height, Band, out _, out _, out _));
        }

        [Test]
        public void Raycast_LevelWithTheRing_FallsBackToTheCylinder()
        {
            Vector3 o = new(0f, 3.4f, 0f);
            Assert.IsTrue(RingMath.Raycast(o, Vector3.right, Vector3.zero, Radius, Height, Band, out float bearing, out Vector3 p, out float t));
            Near(p, new Vector3(Radius, 3.4f, 0f));
            Assert.AreEqual(90f, bearing, 1e-2f);
            Assert.AreEqual(Radius, t, 1e-3f);
            // from outside the ring: the near side of the cylinder is the nearest forward hit
            Assert.IsTrue(RingMath.Raycast(new Vector3(-6f, 3.6f, 0f), Vector3.right, Vector3.zero, Radius, Height, Band, out bearing, out p, out t));
            Near(p, new Vector3(-Radius, 3.6f, 0f));
            Assert.AreEqual(-90f, bearing, 1e-2f);
            Assert.AreEqual(2.5f, t, 1e-3f);
        }

        [Test]
        public void Raycast_OutsideTheBand_IsRejected()
        {
            // through the plane 4.5 m out (1 m beyond the ring) and through the cylinder at y = 3.08 (below the band)
            Vector3 o = new(0f, 1.6f, 0f);
            Assert.IsFalse(RingMath.Raycast(o, new Vector3(4.5f, 1.9f, 0f), Vector3.zero, Radius, Height, Band, out _, out _, out _));
            // pointing away and down: nothing
            Assert.IsFalse(RingMath.Raycast(o, new Vector3(1f, -1f, 0f), Vector3.zero, Radius, Height, Band, out _, out _, out _));
        }

        [Test]
        public void SnapTime_PicksTheNearestChapterStartWithinReach()
        {
            float[] starts = { 0f, 10f, 50f };
            Assert.AreEqual(10f, RingMath.SnapTime(11f, starts, 100f, 2f), 1e-4f);
            Assert.AreEqual(50f, RingMath.SnapTime(48.5f, starts, 100f, 2f), 1e-4f);
            Assert.AreEqual(30f, RingMath.SnapTime(30f, starts, 100f, 2f), 1e-4f); // too far from any start
            Assert.AreEqual(0f, RingMath.SnapTime(99f, starts, 100f, 2f), 1e-4f);  // the end wraps to the start
            Assert.AreEqual(7f, RingMath.SnapTime(7f, null, 100f, 2f), 1e-4f);
        }

        [Test]
        public void Reachable_IsTheArcAroundTheMarker()
        {
            Assert.IsTrue(RingMath.Reachable(170f, -170f, 30f));  // 20 degrees across the seam
            Assert.IsTrue(RingMath.Reachable(-60f, 0f, 60f));
            Assert.IsFalse(RingMath.Reachable(90f, 0f, 60f));
            Assert.IsFalse(RingMath.Reachable(0f, 180f, 120f));
        }
    }
}
