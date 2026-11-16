using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// The few interface sounds of the VR mode, generated here (no audio assets) and played where the widget is:
    /// a soft wooden pluck for a pinch (Click), a two-note pluck for a seek (Confirm), a faint hover tick (Tick)
    /// and a band-passed noise breath for a teleport (Whoosh). All clips are built once at start-up so the first
    /// pinch allocates nothing; a pool of three sources keeps overlapping sounds where they were started.
    /// </summary>
    public static class VrAudio
    {
        public const int Rate = 44100;
        const int PoolSize = 3;
        const float TickGap = 0.08f;   // at most one hover tick per 80 ms
        const float Peak = 0.35f;      // every clip is peak-normalised to this

        static AudioClip click, confirm, tick, whoosh;
        static readonly AudioSource[] pool = new AudioSource[PoolSize];
        static int next;
        static float lastTick = float.NegativeInfinity;
        public static int Clicks, Ticks;
        public static float Volume = 0.5f;

        // marimba-like bar modes: frequency ratios, amplitudes and decay times (s)
        static readonly float[] ModeRatio = { 1f, 3.93f, 10.6f };
        static readonly float[] ModeAmp = { 1f, 0.22f, 0.04f };
        static readonly float[] ModeTau = { 0.090f, 0.022f, 0.006f };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // fast enter-play-mode keeps statics: start each play session clean
            for (int i = 0; i < PoolSize; i++) pool[i] = null;
            next = 0;
            lastTick = float.NegativeInfinity;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void WarmUpOnLoad()
        {
            if (Application.isBatchMode) return;
            WarmUp();
        }

        /// <summary>Builds every clip and the source pool now, so no sound call allocates later.</summary>
        public static void WarmUp()
        {
            if (click == null) click = ToClip("vr click", SynthClick());
            if (confirm == null) confirm = ToClip("vr confirm", SynthConfirm());
            if (tick == null) tick = ToClip("vr tick", SynthTick());
            if (whoosh == null) whoosh = ToClip("vr whoosh", SynthWhoosh());
            for (int i = 0; i < PoolSize; i++)
                if (pool[i] == null) pool[i] = MakeSource(i);
        }

        // ---- the public sounds -------------------------------------------------------------

        /// <summary>A pinch or poke commit: one soft pluck at G4.</summary>
        public static void Click(Vector3 at)
        {
            Clicks++;
            if (Ready()) Play(click, at, 1f);
        }

        /// <summary>A seek or other "done" commit: G4 then D5 a moment later. Counts as a click.</summary>
        public static void Confirm(Vector3 at)
        {
            Clicks++;
            if (Ready()) Play(confirm, at, 1f);
        }

        /// <summary>Hover onto a widget: a barely-there tap, rate-limited so sweeping across a row stays quiet.</summary>
        public static void Tick(Vector3 at)
        {
            float now = Time.unscaledTime;
            if (now - lastTick < TickGap) return;
            lastTick = now;
            Ticks++;
            if (Ready()) Play(tick, at, 0.15f);
        }

        /// <summary>A teleport or viewpoint jump: a short rising breath of filtered noise.</summary>
        public static void Whoosh(Vector3 at)
        {
            if (Ready()) Play(whoosh, at, 0.6f);
        }

        /// <summary>False in batch mode (tests count sounds, never play them); otherwise makes sure clips and sources exist.</summary>
        static bool Ready()
        {
            if (Application.isBatchMode) return false;
            WarmUp();   // only null checks once warm: leaving play mode destroys the sources, this rebuilds them
            return true;
        }

        static void Play(AudioClip clip, Vector3 at, float volume)
        {
            // a free source if there is one, else the one started longest ago
            AudioSource s = null;
            for (int i = 0; i < PoolSize; i++)
            {
                AudioSource c = pool[(next + i) % PoolSize];
                if (!c.isPlaying) { s = c; next = (next + i) % PoolSize; break; }
            }
            if (s == null) s = pool[next];
            next = (next + 1) % PoolSize;

            s.Stop();
            s.transform.position = at;
            s.clip = clip;
            s.volume = volume * Volume;
            s.Play();
        }

        static AudioSource MakeSource(int i)
        {
            GameObject go = new($"VR sounds {i}");
            Object.DontDestroyOnLoad(go);
            AudioSource s = go.AddComponent<AudioSource>();
            s.spatialBlend = 0.6f;
            s.minDistance = 1f;
            s.maxDistance = 8f;
            s.playOnAwake = false;
            s.loop = false;
            s.dopplerLevel = 0f;
            s.bypassReverbZones = true;
            return s;
        }

        static AudioClip ToClip(string name, float[] d)
        {
            AudioClip c = AudioClip.Create(name, d.Length, 1, Rate, false);
            c.SetData(d, 0);
            return c;
        }

        // ---- synthesis: pure functions of their arguments, mono samples at Rate ----------------

        /// <summary>Click: one pluck at 392 Hz (G4), 220 ms.</summary>
        public static float[] SynthClick(int rate = Rate)
        {
            float[] d = new float[Samples(0.22f, rate)];
            AddPluck(d, rate, 0, 392f, 1f, 1u);
            return Finish(d, rate);
        }

        /// <summary>Confirm: 392 Hz at 0.6, then 587 Hz (D5) 45 ms later, 260 ms.</summary>
        public static float[] SynthConfirm(int rate = Rate)
        {
            float[] d = new float[Samples(0.26f, rate)];
            AddPluck(d, rate, 0, 392f, 0.6f, 2u);
            AddPluck(d, rate, Samples(0.045f, rate), 587f, 1f, 3u);
            return Finish(d, rate);
        }

        /// <summary>Tick: a single 784 Hz partial, 12 ms decay, 40 ms long, no noise.</summary>
        public static float[] SynthTick(int rate = Rate)
        {
            float[] d = new float[Samples(0.04f, rate)];
            int attack = Samples(0.0015f, rate);
            float w = 2f * Mathf.PI * 784f / rate;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)rate;
                d[i] = Mathf.Sin(w * i) * Mathf.Exp(-t / 0.012f) * RaisedCosineIn(i, attack);
            }
            return Finish(d, rate);
        }

        /// <summary>Whoosh: white noise through a resonant band-pass (Q 1.5) sweeping 300 to 1200 Hz over 220 ms, Hann envelope.</summary>
        public static float[] SynthWhoosh(int rate = Rate, uint seed = 0x9E3779B9u)
        {
            int n = Samples(0.22f, rate);
            float[] d = new float[n];
            const float q = 1.5f;
            uint s = seed == 0 ? 1u : seed;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)(n - 1);
                float fc = 300f * Mathf.Pow(4f, u);   // exponential sweep 300 -> 1200 Hz

                // RBJ band-pass (0 dB peak gain), coefficients follow the sweep
                double w0 = 2.0 * System.Math.PI * fc / rate;
                double alpha = System.Math.Sin(w0) / (2.0 * q);
                double a0 = 1.0 + alpha;
                double b0 = alpha / a0, b2 = -alpha / a0;
                double a1 = -2.0 * System.Math.Cos(w0) / a0, a2 = (1.0 - alpha) / a0;

                double x = Noise(ref s);
                double y = b0 * x + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;

                float hann = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * u);
                d[i] = (float)y * hann;
            }
            Normalise(d, Peak);
            return d;
        }

        /// <summary>
        /// Adds one modal pluck starting at sample <paramref name="start"/>: sum_k a_k sin(2 pi f0 r_k t) exp(-t/tau_k)
        /// with a 1.5 ms raised-cosine attack, plus a 3 ms low-passed (2.5 kHz) noise tock at 0.06 for the mallet.
        /// </summary>
        public static void AddPluck(float[] d, int rate, int start, float f0, float gain, uint seed)
        {
            int attack = Samples(0.0015f, rate);
            for (int i = start; i < d.Length; i++)
            {
                int k = i - start;
                float t = k / (float)rate;
                float v = 0f;
                for (int m = 0; m < ModeRatio.Length; m++)
                {
                    float f = f0 * ModeRatio[m];
                    if (f >= 0.45f * rate) continue;   // keep partials below Nyquist at low rates
                    v += ModeAmp[m] * Mathf.Sin(2f * Mathf.PI * f * t) * Mathf.Exp(-t / ModeTau[m]);
                }
                d[i] += gain * v * RaisedCosineIn(k, attack);
            }

            // the mallet: 3 ms of one-pole low-passed noise with a linear fade
            int tock = Samples(0.003f, rate);
            float lp = 1f - Mathf.Exp(-2f * Mathf.PI * 2500f / rate);
            float y = 0f;
            uint s = seed == 0 ? 1u : seed;
            for (int k = 0; k < tock && start + k < d.Length; k++)
            {
                y += lp * ((float)Noise(ref s) - y);
                d[start + k] += gain * 0.06f * y * (1f - k / (float)tock);
            }
        }

        /// <summary>4 ms raised-cosine end fade, then peak-normalise.</summary>
        static float[] Finish(float[] d, int rate)
        {
            int fade = Mathf.Min(Samples(0.004f, rate), d.Length);
            for (int k = 0; k < fade; k++)
            {
                float u = (k + 1) / (float)fade;   // last sample reaches exactly zero
                d[d.Length - fade + k] *= 0.5f + 0.5f * Mathf.Cos(Mathf.PI * u);
            }
            Normalise(d, Peak);
            return d;
        }

        public static void Normalise(float[] d, float peak)
        {
            float max = 0f;
            for (int i = 0; i < d.Length; i++) max = Mathf.Max(max, Mathf.Abs(d[i]));
            if (max <= 1e-9f) return;
            float g = peak / max;
            for (int i = 0; i < d.Length; i++) d[i] *= g;
        }

        static float RaisedCosineIn(int k, int len) =>
            k >= len ? 1f : 0.5f - 0.5f * Mathf.Cos(Mathf.PI * k / len);

        static int Samples(float seconds, int rate) => Mathf.Max(1, Mathf.RoundToInt(seconds * rate));

        /// <summary>Deterministic white noise in [-1, 1): xorshift32.</summary>
        static double Noise(ref uint s)
        {
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            return s / 2147483648.0 - 1.0;
        }
    }
}
