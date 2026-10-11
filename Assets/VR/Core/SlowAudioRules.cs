using UnityEngine;

namespace HmVr
{
    /// <summary>what a lesson's soundtrack does while the film plays slower than real time (the playback bar's 0.5x, the VR hand panel's 0.5x, 0.25x, 0.1x)</summary>
    public enum SlowAudioMode
    {
        /// <summary>the sound slows with the picture: the player's pitch follows the rate (music: a lower, slower song)</summary>
        Pitch,

        /// <summary>the sound fades out (about 120 ms) below 1x and fades back in at 1x or faster; its pitch never drops (speech at 0.25x is two octaves down)</summary>
        Mute
    }

    /// <summary>
    /// The pure rules of the slow-motion sound (FilmSoundtrack applies them to its AudioSource; the EditMode tests check them without audio).
    /// A film in dialogue mode is never heard in slow motion: under <see cref="SlowBelow"/> its gain goes to 0 in <see cref="FadeSeconds"/> and
    /// back to 1 in the same time at 1x or faster, and the player keeps pitch 1 for as long as anything is audible, so the fade is a fade and not
    /// a swoop down the octaves. Rates above 1x are not touched.
    /// </summary>
    public static class SlowAudioRules
    {
        /// <summary>rates under this are slow motion (the bar offers 0.5x 1x 1.5x 2x, the VR panel 1x 0.5x 0.25x 0.1x)</summary>
        public const float SlowBelow = 0.95f;

        /// <summary>the fade out / in, seconds</summary>
        public const float FadeSeconds = 0.12f;

        /// <summary>the sound is to be silent at this rate</summary>
        public static bool Muted(SlowAudioMode mode, float rate) => mode == SlowAudioMode.Mute && rate < SlowBelow;

        /// <summary>the gain one step (dt real seconds) later: a linear fade of <see cref="FadeSeconds"/> toward 0 (muted) or 1</summary>
        public static float Step(float gain, bool muted, float dt) => Mathf.MoveTowards(gain, muted ? 0f : 1f, Mathf.Max(0f, dt) / FadeSeconds);

        /// <summary>the pitch the player runs at: the rate, except that a sound that is about to be muted (still audible) stays at 1</summary>
        public static float PlayerPitch(SlowAudioMode mode, float rate, float gain) => Muted(mode, rate) && gain > 0.001f ? 1f : rate;
    }
}
