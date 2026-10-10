using UnityEngine;

namespace HmVr
{
    /// <summary>A boolean that only changes after the raw value has stayed different for a while (tracking noise, one-frame glitches).</summary>
    public sealed class Debouncer
    {
        public float OnDelay, OffDelay;
        public bool Value { get; private set; }
        float pending;

        public Debouncer(float onDelay, float offDelay)
        {
            OnDelay = onDelay;
            OffDelay = offDelay;
        }

        public void Reset(bool value = false)
        {
            Value = value;
            pending = 0f;
        }

        /// <summary>true when the output changed this call</summary>
        public bool Update(bool raw, float dt)
        {
            if (raw == Value)
            {
                pending = 0f;
                return false;
            }

            pending += dt;
            if (pending < (raw ? OnDelay : OffDelay)) return false;
            Value = raw;
            pending = 0f;
            return true;
        }
    }

    /// <summary>The One Euro filter (Casiez et al. 2012): a low-pass whose cut-off rises with speed, so a still hand is steady and a moving one has no lag.</summary>
    public sealed class OneEuroVector3
    {
        public float MinCutoff = 1.5f, Beta = 0.6f, DerivativeCutoff = 1f;
        Vector3 x, dx;
        bool has;

        public Vector3 Value => x;

        public void Reset() => has = false;

        public Vector3 Filter(Vector3 v, float dt)
        {
            if (!has || dt <= 1e-5f)
            {
                has = true;
                x = v;
                dx = Vector3.zero;
                return x;
            }

            Vector3 rate = (v - x) / dt;
            float ad = Alpha(DerivativeCutoff, dt);
            dx = Vector3.Lerp(dx, rate, ad);
            float cutoff = MinCutoff + Beta * dx.magnitude;
            x = Vector3.Lerp(x, v, Alpha(cutoff, dt));
            return x;
        }

        static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }
    }
}
