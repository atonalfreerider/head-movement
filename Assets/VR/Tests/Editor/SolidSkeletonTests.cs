using NUnit.Framework;
using UnityEngine;

namespace HmVr.Tests
{
    /// <summary>The solid skeleton colour model (VR mode, Resources/HM_SkeletonSolid.shader): always bright, hue kept, fades kept, alpha 1.</summary>
    public class SolidSkeletonTests
    {
        // the skeleton's line colours as the shader receives them: linear(gradient colour). SkeletonStyle: base x (0.22 + 1.9 pulse), base red (1, 0.05, 0) / white
        static Color Vertex(Color gradientColour) => gradientColour.linear;

        [Test]
        public void BetweenBeats_TheSkeletonIsBright_NotTheDimRhythmIdle()
        {
            Color leadIdle = Vertex(new Color(1f, 0.05f, 0f) * 0.22f);
            Color followIdle = Vertex(Color.white * 0.22f);
            Assert.Less(Mathf.Max(leadIdle.r, leadIdle.g, leadIdle.b), 0.05f, "the viewer's own idle colour is dim (what looked see-through)");
            Assert.GreaterOrEqual(SolidSkeleton.Brightness(leadIdle), 0.74f);
            Assert.GreaterOrEqual(SolidSkeleton.Brightness(followIdle), 0.74f);
            Color f = SolidSkeleton.Map(followIdle);
            Assert.AreEqual(f.r, f.g, 1e-4f, "white stays neutral");
            Assert.AreEqual(f.g, f.b, 1e-4f);
        }

        [Test]
        public void ABeatRaisesItToFull_AndAddsAFlash()
        {
            Color idle = SolidSkeleton.Map(Vertex(new Color(1f, 0.05f, 0f) * 0.22f));
            Color beat = SolidSkeleton.Map(Vertex(new Color(1f, 0.05f, 0f)));
            Assert.Greater(beat.r, idle.r);
            Assert.AreEqual(1f, beat.r, 1e-4f);
            Assert.Greater(beat.g, idle.g + 0.2f, "the flash whitens the top of the beat");
        }

        [Test]
        public void HuePreserved_ForTheLoadColours()
        {
            Color tension = Vertex(new Color(1f, 0.22f, 0.02f) * 0.4f);
            Color compression = Vertex(new Color(0.08f, 0.28f, 1f) * 0.4f);
            Color t = SolidSkeleton.Map(tension), c = SolidSkeleton.Map(compression);
            Assert.Greater(t.r, t.g * 3f);
            Assert.Greater(c.b, c.g * 2f);
            Assert.Greater(c.b, c.r * 5f);
        }

        [Test]
        public void ColourMonotonicInBrightness_AndNeverAboveOne()
        {
            float previous = 0f;
            for (float g = 0.01f; g <= 1f; g += 0.01f)
            {
                Color m = SolidSkeleton.Map(Vertex(new Color(g, g, g)));
                float b = Mathf.Max(m.r, m.g, m.b);
                Assert.GreaterOrEqual(b, previous - 1e-4f, $"gradient {g}");
                Assert.LessOrEqual(b, 1f);
                Assert.AreEqual(1f, m.a);
                previous = b;
            }
        }

        [Test]
        public void AFadeToBlack_StillFadesOut()
        {
            Color half = Vertex(Color.white * 0.22f * 0.5f);
            Color none = Color.black;
            Assert.Less(SolidSkeleton.Brightness(half), SolidSkeleton.Brightness(Vertex(Color.white * 0.22f)));
            Assert.AreEqual(0f, SolidSkeleton.Brightness(none), 1e-4f);
        }
    }
}
