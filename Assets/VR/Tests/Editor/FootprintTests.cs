using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace HmVr.Tests
{
    /// <summary>Where the dance is on the floor: the robust smallest circle around the dancers' floor points, and how far to stand from it.</summary>
    public class FootprintTests
    {
        static void Near(Vector2 a, Vector2 b, float eps = 1e-3f) => Assert.Less((a - b).magnitude, eps, $"{a} vs {b}");

        static List<Vector2> Circle(Vector2 centre, float r, int n)
        {
            List<Vector2> pts = new();
            for (int i = 0; i < n; i++)
            {
                float a = i * 2f * Mathf.PI / n;
                pts.Add(centre + r * new Vector2(Mathf.Cos(a), Mathf.Sin(a)));
            }

            return pts;
        }

        [Test]
        public void CircleOfPoints_GivesItsCentreAndRadius()
        {
            DanceFootprint fp = FootprintMath.Compute(Circle(new Vector2(1f, -1f), 2f, 72));
            Near(fp.Centre, new Vector2(1f, -1f));
            Assert.AreEqual(2f, fp.Radius, 1e-3f);
            Assert.AreEqual(2f, fp.RadiusP98, 1e-3f);
            Assert.AreEqual(72, fp.Samples);
            Near(fp.Box.min, new Vector2(-1f, -3f));
            Near(fp.Box.max, new Vector2(3f, 1f));
        }

        [Test]
        public void SmallestCircle_NotTheMean()
        {
            // a square's corners plus a cluster near one corner: the circle is still the square's circumcircle
            List<Vector2> pts = new() { new(0f, 0f), new(2f, 0f), new(0f, 2f), new(2f, 2f) };
            for (int i = 0; i < 20; i++) pts.Add(new Vector2(0.1f + 0.01f * i, 0.2f));
            DanceFootprint fp = FootprintMath.Compute(pts, 1f);
            Near(fp.Centre, new Vector2(1f, 1f));
            Assert.AreEqual(Mathf.Sqrt(2f), fp.Radius, 1e-3f);

            // an obtuse triangle: the circle on its longest side
            fp = FootprintMath.Compute(new List<Vector2> { new(0f, 0f), new(4f, 0f), new(1f, 1f) }, 1f);
            Near(fp.Centre, new Vector2(2f, 0f));
            Assert.AreEqual(2f, fp.Radius, 1e-3f);
        }

        [Test]
        public void ASpike_IsDroppedFromTheCentre_ButCountsInTheRadius()
        {
            List<Vector2> pts = Circle(new Vector2(1f, -1f), 2f, 72);
            Vector2 spike = new(50f, 50f);
            pts.Add(spike);
            DanceFootprint fp = FootprintMath.Compute(pts);
            Near(fp.Centre, new Vector2(1f, -1f));
            Assert.AreEqual((spike - new Vector2(1f, -1f)).magnitude, fp.Radius, 1e-2f);
            Assert.AreEqual(2f, fp.RadiusP98, 1e-3f);
            Assert.AreEqual(73, fp.Samples);
        }

        [Test]
        public void NaNAndInfinity_AreSkipped()
        {
            List<Vector2> pts = Circle(new Vector2(1f, -1f), 2f, 72);
            pts.Insert(5, new Vector2(float.NaN, 0f));
            pts.Add(new Vector2(0f, float.PositiveInfinity));
            pts.Add(new Vector2(float.NegativeInfinity, float.NaN));
            DanceFootprint fp = FootprintMath.Compute(pts);
            Assert.AreEqual(72, fp.Samples);
            Near(fp.Centre, new Vector2(1f, -1f));
            Assert.AreEqual(2f, fp.Radius, 1e-3f);

            Assert.AreEqual(0, FootprintMath.Compute(new List<Vector2> { new(float.NaN, 1f) }).Samples);
            Assert.AreEqual(0, FootprintMath.Compute(null).Samples);
        }

        [Test]
        public void Compute_IsDeterministic_AndEnclosesTheKeptPoints()
        {
            List<Vector2> pts = new();
            uint s = 12345u;
            for (int i = 0; i < 400; i++)
            {
                s = s * 1664525u + 1013904223u;
                float x = (s >> 8) / 16777216f * 3f - 1f;
                s = s * 1664525u + 1013904223u;
                float y = (s >> 8) / 16777216f * 2f + 0.5f;
                pts.Add(new Vector2(x, y));
            }

            DanceFootprint a = FootprintMath.Compute(pts, 1f), b = FootprintMath.Compute(pts, 1f);
            Assert.AreEqual(a.Centre, b.Centre);
            Assert.AreEqual(a.Radius, b.Radius);
            // the smallest circle: no point outside, and at least two points on it
            int onIt = 0;
            foreach (Vector2 p in pts)
            {
                float d = (p - a.Centre).magnitude;
                Assert.LessOrEqual(d, a.Radius + 1e-4f);
                if (d > a.Radius - 1e-3f) onIt++;
            }

            Assert.GreaterOrEqual(onIt, 2);
        }

        [Test]
        public void ViewDistance_StaysOutsideTheRing_AndIsClamped()
        {
            Assert.AreEqual(4.1f, FootprintMath.ViewDistance(new DanceFootprint { RadiusP98 = 1f }), 1e-4f);   // small dance: ring + clearance
            Assert.AreEqual(4.9f, FootprintMath.ViewDistance(new DanceFootprint { RadiusP98 = 4f }), 1e-4f);   // wide dance: 4 + 0.3 + 0.6
            Assert.AreEqual(6f, FootprintMath.ViewDistance(new DanceFootprint { RadiusP98 = 10f }), 1e-4f);    // never farther than max
            Assert.AreEqual(3.5f, FootprintMath.ViewDistance(new DanceFootprint { RadiusP98 = 1f }, 3f, 0.5f, 8f), 1e-4f);
        }
    }
}
