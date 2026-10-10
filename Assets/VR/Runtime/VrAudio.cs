using UnityEngine;

namespace HmVr
{
    /// <summary>The few interface sounds of the VR mode: a click and a hover tick, generated here (no audio assets). Played where the widget is.</summary>
    public static class VrAudio
    {
        static AudioClip click, tick, whoosh;
        static AudioSource source;
        public static int Clicks, Ticks;
        public static float Volume = 0.5f;

        static AudioSource Source
        {
            get
            {
                if (source != null) return source;
                GameObject go = new("VR sounds");
                Object.DontDestroyOnLoad(go);
                source = go.AddComponent<AudioSource>();
                source.spatialBlend = 0.8f;
                source.minDistance = 0.4f;
                source.maxDistance = 8f;
                source.playOnAwake = false;
                source.dopplerLevel = 0f;
                source.bypassReverbZones = true;
                return source;
            }
        }

        static AudioClip Make(string name, float seconds, float f0, float f1, float amp, float decay)
        {
            const int rate = 44100;
            int n = Mathf.RoundToInt(seconds * rate);
            float[] d = new float[n];
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float f = Mathf.Lerp(f0, f1, t);
                phase += 2f * Mathf.PI * f / rate;
                float env = Mathf.Min(1f, i / (0.002f * rate)) * Mathf.Exp(-decay * t);
                d[i] = Mathf.Sin(phase) * env * amp + Mathf.Sin(phase * 2f) * env * amp * 0.25f;
            }

            AudioClip c = AudioClip.Create(name, n, 1, rate, false);
            c.SetData(d, 0);
            return c;
        }

        public static void Click(Vector3 at)
        {
            click ??= Make("vr click", 0.075f, 1500f, 900f, 0.8f, 5f);
            Clicks++;
            Play(click, at, 1f);
        }

        public static void Tick(Vector3 at)
        {
            tick ??= Make("vr tick", 0.03f, 2300f, 2100f, 0.5f, 4f);
            Ticks++;
            Play(tick, at, 0.35f);
        }

        public static void Whoosh(Vector3 at)
        {
            whoosh ??= Make("vr whoosh", 0.2f, 500f, 1400f, 0.5f, 3f);
            Play(whoosh, at, 0.6f);
        }

        static void Play(AudioClip clip, Vector3 at, float volume)
        {
            if (Application.isBatchMode) return;
            AudioSource s = Source;
            s.transform.position = at;
            s.PlayOneShot(clip, volume * Volume);
        }
    }
}
