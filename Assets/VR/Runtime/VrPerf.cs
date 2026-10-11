using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// A light frame-time logger for the headset (adb logcat -s Unity | grep HM-PERF): every 5 seconds one line with the frame rate and the
    /// mean / 95th percentile / worst frame time, the share of frames that overran the display budget, the GPU and CPU frame time from
    /// FrameTimingManager when the device reports them, the lesson, the display mode and the hair level; plus the lesson's load time and
    /// the library's card count. With Adaptive on (quest_settings.json) a lesson that keeps missing its budget steps the HAIR down one
    /// level (Full to Light to Off), once per 10 seconds at most, and says so; it never steps up by itself (the menu does).
    /// </summary>
    [DefaultExecutionOrder(-500)]
    [AddComponentMenu("")]
    public sealed class VrPerf : MonoBehaviour
    {
        public static VrPerf Instance { get; private set; }

        public struct Sample
        {
            public string Lesson, Mode, Hair;
            public float Seconds, Fps, MeanMs, P95Ms, MaxMs, OverPct, GpuMs, CpuMs, BudgetMs;
            public int Frames, HairLod;
        }

        public Sample Last { get; private set; }
        public readonly List<Sample> History = new();
        public bool Enabled = true;
        public bool Adaptive = true;
        public float LoadSeconds { get; private set; } = -1f;
        public string LoadedLesson { get; private set; }

        readonly List<float> dts = new(512);
        float windowStart;
        double gpuSum, cpuSum;
        int timingN;
        FrameTiming[] timings = new FrameTiming[1];
        float lessonSince = -1f, adaptCooldown;
        float loadStart = -1f;
        bool wasLesson;
        float overBudgetFrames, adaptWindowFrames, adaptWindowStart;

        void Awake()
        {
            Instance = this;
            VrSettings s = VrSettings.Current;
            Enabled = s.PerfLog;
            Adaptive = s.Adaptive;
            windowStart = Time.unscaledTime;
            adaptWindowStart = Time.unscaledTime;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        float BudgetSeconds
        {
            get
            {
                float hz = VrQuality.RefreshHz > 1f ? VrQuality.RefreshHz : Mathf.Max(30f, VrSettings.Current.RefreshHz);
                return 1f / hz;
            }
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            dts.Add(dt);
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
                {
                    if (timings[0].gpuFrameTime > 0.0) gpuSum += timings[0].gpuFrameTime;
                    if (timings[0].cpuFrameTime > 0.0) cpuSum += timings[0].cpuFrameTime;
                    timingN++;
                }
            }
            catch (Exception)
            {
                // frame timing not available on this device
            }

            TrackLesson();
            Adapt(dt);
            if (Time.unscaledTime - windowStart >= 5f) Flush();
        }

        void TrackLesson()
        {
            FilmLibrary lib = FilmLibrary.Instance;
            bool lesson = VrFilm.InLesson;
            if (lib != null && lib.State == FilmLibrary.Mode.Loading && loadStart < 0f) loadStart = Time.unscaledTime;
            if (lesson && !wasLesson)
            {
                lessonSince = Time.unscaledTime;
                LoadedLesson = VrFilm.Entry?.Id;
                if (loadStart >= 0f)
                {
                    LoadSeconds = Time.unscaledTime - loadStart;
                    Debug.Log($"HM-PERF lesson-load id={LoadedLesson} seconds={LoadSeconds:0.0}");
                }

                adaptWindowStart = Time.unscaledTime;
                overBudgetFrames = adaptWindowFrames = 0f;
            }

            if (!lesson) lessonSince = -1f;
            if (lib != null && lib.State != FilmLibrary.Mode.Loading && lesson) loadStart = -1f;
            if (lib != null && lib.State == FilmLibrary.Mode.Library) loadStart = -1f;
            wasLesson = lesson;
        }

        void Adapt(float dt)
        {
            if (!Adaptive || lessonSince < 0f || VrQuality.Current != VrQuality.Tier.Quest || VrQuality.Hair == HairLevel.Off)
            {
                adaptWindowStart = Time.unscaledTime;
                overBudgetFrames = adaptWindowFrames = 0f;
                return;
            }

            if (Time.unscaledTime - lessonSince < 12f || VrFilm.Paused) // startup hitches (the hair bake begins, textures upload) are not a reason
            {
                adaptWindowStart = Time.unscaledTime;
                overBudgetFrames = adaptWindowFrames = 0f;
                return;
            }

            adaptWindowFrames++;
            if (dt > BudgetSeconds * 1.35f) overBudgetFrames++;
            if (Time.unscaledTime - adaptWindowStart < 4f) return;
            float share = overBudgetFrames / Mathf.Max(1f, adaptWindowFrames);
            adaptWindowStart = Time.unscaledTime;
            overBudgetFrames = adaptWindowFrames = 0f;
            if (share > 0.18f && Time.unscaledTime >= adaptCooldown)
            {
                HairLevel was = VrQuality.Hair;
                if (VrQuality.StepHairDown())
                {
                    adaptCooldown = Time.unscaledTime + 10f;
                    Debug.Log($"HM-PERF adaptive: {share * 100f:0}% of frames over budget, hair {was.ToString().ToLowerInvariant()} -> {VrQuality.Hair.ToString().ToLowerInvariant()}");
                }
            }
        }

        void Flush()
        {
            float seconds = Time.unscaledTime - windowStart;
            windowStart = Time.unscaledTime;
            int n = dts.Count;
            if (n == 0) return;
            float sum = 0f, max = 0f;
            float budget = BudgetSeconds;
            int over = 0;
            foreach (float d in dts)
            {
                sum += d;
                if (d > max) max = d;
                if (d > budget * 1.35f) over++;
            }

            dts.Sort();
            float p95 = dts[Mathf.Min(n - 1, (int)(n * 0.95f))];
            HairStrands hair = FindAnyObjectByType<HairStrands>();
            VrDisplay disp = VrDisplay.Instance;
            Sample s = new()
            {
                Lesson = VrFilm.InLesson ? VrFilm.Entry?.Id : "menu", Mode = disp == null ? "none" : disp.PassthroughActive ? "passthrough" : "immersive",
                Hair = VrQuality.Hair.ToString().ToLowerInvariant(), Seconds = seconds, Frames = n, Fps = n / Mathf.Max(0.001f, seconds), MeanMs = sum / n * 1000f,
                P95Ms = p95 * 1000f, MaxMs = max * 1000f, OverPct = over * 100f / n, BudgetMs = budget * 1000f,
                GpuMs = timingN > 0 ? (float)(gpuSum / timingN) : -1f, CpuMs = timingN > 0 ? (float)(cpuSum / timingN) : -1f, HairLod = hair != null ? hair.Lod : -1
            };
            dts.Clear();
            gpuSum = cpuSum = 0;
            timingN = 0;
            Last = s;
            History.Add(s);
            if (History.Count > 400) History.RemoveAt(0);
            if (!Enabled) return;
            StringBuilder sb = new();
            sb.Append("HM-PERF lesson=").Append(s.Lesson).Append(" mode=").Append(s.Mode).Append(" hair=").Append(s.Hair).Append(" lod=").Append(s.HairLod)
                .Append(" fps=").Append(s.Fps.ToString("0.0")).Append(" mean=").Append(s.MeanMs.ToString("0.0")).Append("ms p95=").Append(s.P95Ms.ToString("0.0"))
                .Append("ms max=").Append(s.MaxMs.ToString("0.0")).Append("ms over=").Append(s.OverPct.ToString("0")).Append("% budget=").Append(s.BudgetMs.ToString("0.0")).Append("ms");
            if (s.GpuMs >= 0f) sb.Append(" gpu=").Append(s.GpuMs.ToString("0.0")).Append("ms cpu=").Append(s.CpuMs.ToString("0.0")).Append("ms");
            if (hair != null)
            {
                sb.Append(" hairTris=").Append(hair.TriangleCount).Append(" bake=").Append(hair.BakedFrames).Append('/').Append(hair.Avatar != null ? hair.Avatar.FrameCount : 0);
            }

            sb.Append(" mem=").Append((UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576L).ToString()).Append("MB");
            Debug.Log(sb.ToString());
        }

        /// <summary>mean of the logged windows of one lesson / mode / hair level (the report)</summary>
        public Dictionary<string, object> Summary(string lesson = null)
        {
            double fps = 0, mean = 0, p95 = 0, max = 0, over = 0, gpu = 0, cpu = 0;
            int n = 0, g = 0;
            foreach (Sample s in History)
            {
                if (lesson != null && s.Lesson != lesson) continue;
                n++;
                fps += s.Fps;
                mean += s.MeanMs;
                p95 += s.P95Ms;
                over += s.OverPct;
                max = Math.Max(max, s.MaxMs);
                if (s.GpuMs >= 0f)
                {
                    g++;
                    gpu += s.GpuMs;
                    cpu += s.CpuMs;
                }
            }

            return new Dictionary<string, object>
            {
                ["windows"] = n, ["fps"] = n > 0 ? fps / n : 0.0, ["meanMs"] = n > 0 ? mean / n : 0.0, ["p95Ms"] = n > 0 ? p95 / n : 0.0, ["maxMs"] = max,
                ["overPct"] = n > 0 ? over / n : 0.0, ["gpuMs"] = g > 0 ? gpu / g : -1.0, ["cpuMs"] = g > 0 ? cpu / g : -1.0
            };
        }
    }
}
