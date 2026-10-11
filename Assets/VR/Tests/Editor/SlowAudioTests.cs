using NUnit.Framework;

namespace HmVr.Tests
{
    /// <summary>The slow-motion sound rules: a dialogue lesson is faded out below 1x (never pitched down), a music lesson keeps its pitch behaviour.</summary>
    public class SlowAudioTests
    {
        const float Frame = 1f / 60f;

        /// <summary>runs the fade for `seconds` of real time at 60 fps with the film at `rate`, returns the gain and (via pitch) what the player runs at</summary>
        static float Run(SlowAudioMode mode, float rate, float gain, float seconds, out float pitch)
        {
            pitch = SlowAudioRules.PlayerPitch(mode, rate, gain);
            for (float t = 0f; t < seconds - 1e-4f; t += Frame)
            {
                gain = SlowAudioRules.Step(gain, SlowAudioRules.Muted(mode, rate), Frame);
                pitch = SlowAudioRules.PlayerPitch(mode, rate, gain);
            }

            return gain;
        }

        [Test]
        public void Dialogue_AtAQuarterSpeed_IsSilentWithin200Ms()
        {
            float g = Run(SlowAudioMode.Mute, 0.25f, 1f, 0.2f, out float pitch);
            Assert.AreEqual(0f, g, 1e-6f);
            Assert.AreEqual(0.25f, pitch, 1e-6f); // silent: the player may follow the rate (it stays in step with the film clock)
        }

        [Test]
        public void Dialogue_FadesOverAboutAHundredAndTwentyMilliseconds_AndNeverPlaysAtTheSlowPitchWhileAudible()
        {
            float g = 1f;
            bool mid = false;
            for (float t = 0f; t < 0.2f; t += Frame)
            {
                g = SlowAudioRules.Step(g, true, Frame);
                float pitch = SlowAudioRules.PlayerPitch(SlowAudioMode.Mute, 0.25f, g);
                if (g > 0.001f) Assert.AreEqual(1f, pitch, 1e-6f, $"audible at gain {g:0.00} but pitched to {pitch}"); // no swoop down the octaves
                if (t >= 0.05f && t <= 0.08f) mid |= g > 0.15f && g < 0.85f;
            }

            Assert.IsTrue(mid, "a fade, not a cut");
            Assert.AreEqual(0f, g, 1e-6f);
        }

        [TestCase(0.5f)]
        [TestCase(0.25f)]
        [TestCase(0.1f)]
        [TestCase(0.9f)]
        public void Dialogue_IsMutedInEverySlowMotionRate(float rate) => Assert.IsTrue(SlowAudioRules.Muted(SlowAudioMode.Mute, rate));

        [TestCase(1f)]
        [TestCase(1.5f)]
        [TestCase(2f)]
        [TestCase(0.95f)]
        public void Dialogue_IsHeardAtOneTimesAndFaster(float rate) => Assert.IsFalse(SlowAudioRules.Muted(SlowAudioMode.Mute, rate));

        [Test]
        public void Dialogue_ComesBackAtOneTimes_WithinAFadeAtNormalPitch()
        {
            float g = Run(SlowAudioMode.Mute, 0.25f, 1f, 0.3f, out _);
            Assert.AreEqual(0f, g, 1e-6f);
            g = Run(SlowAudioMode.Mute, 1f, g, 0.2f, out float pitch);
            Assert.AreEqual(1f, g, 1e-6f);
            Assert.AreEqual(1f, pitch, 1e-6f);
        }

        [Test]
        public void Dialogue_AboveOneTimes_KeepsItsPitchBehaviour()
        {
            float g = Run(SlowAudioMode.Mute, 2f, 1f, 0.5f, out float pitch);
            Assert.AreEqual(1f, g, 1e-6f);
            Assert.AreEqual(2f, pitch, 1e-6f);
        }

        [Test]
        public void Music_AtAQuarterSpeed_KeepsItsPitchBehaviour_AndStaysAudible()
        {
            float g = Run(SlowAudioMode.Pitch, 0.25f, 1f, 1f, out float pitch);
            Assert.AreEqual(1f, g, 1e-6f);
            Assert.AreEqual(0.25f, pitch, 1e-6f);
            Assert.IsFalse(SlowAudioRules.Muted(SlowAudioMode.Pitch, 0.1f));
        }

        [Test]
        public void ARateChangeMidFade_ReversesFromWhereItIs()
        {
            float g = Run(SlowAudioMode.Mute, 0.25f, 1f, 0.06f, out _); // half way down
            Assert.Greater(g, 0.3f);
            Assert.Less(g, 0.7f);
            float up = Run(SlowAudioMode.Mute, 1f, g, 0.03f, out _);
            Assert.Greater(up, g, "going back to 1x turns the fade around at once");
        }

        [Test]
        public void ANegativeOrZeroStep_ChangesNothing()
        {
            Assert.AreEqual(0.6f, SlowAudioRules.Step(0.6f, true, 0f), 1e-6f);
            Assert.AreEqual(0.6f, SlowAudioRules.Step(0.6f, true, -1f), 1e-6f);
        }
    }
}
