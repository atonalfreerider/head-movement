using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// The colour model of the solid skeleton (Resources/HM_SkeletonSolid.shader, VR mode): the viewer's skeleton colours are a dim base
    /// (SkeletonStyle.IdleLevel 0.22 of the line colour between beats) that only the beat pulse and the HDR bloom light up; the Quest tier has
    /// neither, so the skeleton looked faint and see-through on a bright room. Map() is the shader's function in C# (the shader cannot be
    /// unit-tested): hue kept, brightness mapped into [Idle, 1] in linear light, a short white flash at the top of a beat, fades (colour
    /// scaled towards black) kept.
    /// </summary>
    public static class SolidSkeleton
    {
        /// <summary>linear brightness of a skeleton between beats (the shader's _Idle)</summary>
        public const float Idle = 0.85f;

        /// <summary>vertex brightness (linear) that counts as a full beat (_PulseAt)</summary>
        public const float PulseAt = 0.5f;

        /// <summary>white added at the top of a beat (_Flash)</summary>
        public const float Flash = 0.3f;

        /// <summary>vertex brightness (linear) of the line colour between beats: SkeletonStyle.IdleLevel 0.22 as the gradient hands it to the shader (_IdleVertex)</summary>
        public const float IdleVertex = 0.0397f;

        /// <summary>vertex colour (linear, as the shader receives it) -> displayed linear colour, every channel 0..1</summary>
        public static Color Map(Color v)
        {
            float m = Mathf.Max(v.r, Mathf.Max(v.g, v.b));
            float mm = Mathf.Max(m, 1e-5f);
            float beat = Mathf.Clamp01((m - IdleVertex) / Mathf.Max(PulseAt - IdleVertex, 1e-4f));
            float f = m < IdleVertex ? Idle * (m / IdleVertex) : Mathf.Lerp(Idle, 1f, beat);
            float flash = m >= IdleVertex ? Flash * beat * beat : 0f;
            return new Color(Mathf.Clamp01(v.r / mm * f + flash), Mathf.Clamp01(v.g / mm * f + flash), Mathf.Clamp01(v.b / mm * f + flash), 1f);
        }

        /// <summary>the brightest channel of the result (what the eye reads as the line's brightness)</summary>
        public static float Brightness(Color v)
        {
            Color c = Map(v);
            return Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        }
    }
}
