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
        public float LoadMaxFrameMs { get; private set; }
        public int LoadFrames { get; private set; }
        public int LoadFramesOver100 { get; private set; }
        public int LoadFramesOver40 { get; private set; }
        public string LoadedLesson { get; private set; }

        readonly List<float> dts = new(512);
        float windowStart;
        double gpuSum, cpuSum;
        int timingN;
        FrameTiming[] timings = new FrameTiming[1];
        (float when, float to)? seekAt;
        float lessonSince = -1f, adaptCooldown;
        float loadStart = -1f;
        bool wasLesson;
        float overBudgetFrames, adaptWindowFrames, adaptWindowStart;
        float skeletonProbeAt = -1f;
        float noAdaptUntil, nextHairScan;
        HairStrands hairCached;
        string lastAdaptReason = "";

        /// <summary>a seek / chapter jump: the frames right after it (poses, hair cache, textures) are not steady state, so the adaptive step-down ignores them</summary>
        public static void NoteSeek()
        {
            if (Instance != null) Instance.noAdaptUntil = Time.unscaledTime + 6f;
        }

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
            if (dt > 0.1f) Debug.Log($"HM-STALL frame {dt * 1000f:0} ms (last load mark: '{LoadTrace.Last}', {(VrFilm.InLesson ? "lesson " + VrFilm.Entry?.Id : FilmLibrary.Instance != null ? FilmLibrary.Instance.State.ToString().ToLowerInvariant() : "no library")})");
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

            FilmLibrary fl = FilmLibrary.Instance;
            if (fl != null && fl.State == FilmLibrary.Mode.Loading)
            {
                // frames while a lesson loads: the loading screen must keep moving (no frame over 100 ms, ideally under 40)
                LoadFrames++;
                if (dt * 1000f > LoadMaxFrameMs) LoadMaxFrameMs = dt * 1000f;
                if (dt > 0.1f) LoadFramesOver100++;
                if (dt > 0.04f) LoadFramesOver40++;
            }

            TrackLesson();
            Adapt(dt);
            if (Time.unscaledTime - windowStart >= 5f) Flush();
        }

        void TrackLesson()
        {
            FilmLibrary lib = FilmLibrary.Instance;
            bool lesson = VrFilm.InLesson;
            if (lib != null && lib.State == FilmLibrary.Mode.Loading && loadStart < 0f)
            {
                loadStart = Time.unscaledTime;
                LoadMaxFrameMs = 0f;
                LoadFrames = LoadFramesOver100 = LoadFramesOver40 = 0;
            }
            if (lesson && !wasLesson)
            {
                lessonSince = Time.unscaledTime;
                LoadedLesson = VrFilm.Entry?.Id;
                if (loadStart >= 0f)
                {
                    LoadSeconds = Time.unscaledTime - loadStart;
                    Debug.Log($"HM-PERF lesson-load id={LoadedLesson} seconds={LoadSeconds:0.0} frames={LoadFrames} max-frame={LoadMaxFrameMs:0}ms frames>40ms={LoadFramesOver40} frames>100ms={LoadFramesOver100} (longest build step '{LoadTrace.LongestStep}' {LoadTrace.LongestStepMs:0}ms)");
                }

                adaptWindowStart = Time.unscaledTime;
                overBudgetFrames = adaptWindowFrames = 0f;
                skeletonProbeAt = Time.unscaledTime + 3f; // the avatars, lines and beads exist a moment after the lesson starts
                LogSkeletonProbe("lesson start");
                string seek = VrArgs.Get("-hmseek");
                if (seek != null && float.TryParse(seek, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float at)) seekAt = (Time.unscaledTime + 4f, at);
            }

            if (skeletonProbeAt >= 0f && lesson && Time.unscaledTime >= skeletonProbeAt)
            {
                skeletonProbeAt = -1f;
                LogSkeletonProbe("lesson +3 s");
            }

            if (seekAt.HasValue && Time.unscaledTime >= seekAt.Value.when && lesson)
            {
                Debug.Log($"HM-PERF seek to {seekAt.Value.to:0} s (command line)");
                VrFilm.SeekTo(seekAt.Value.to);
                seekAt = null;
            }

            if (!lesson) lessonSince = -1f;
            if (lib != null && lib.State != FilmLibrary.Mode.Loading && lesson) loadStart = -1f;
            if (lib != null && lib.State == FilmLibrary.Mode.Library) loadStart = -1f;
            wasLesson = lesson;
        }

        /// <summary>logcat (adb logcat -s Unity | grep "HM-VR skeleton"): what the skeleton is drawn with (shader, queue, blend, z state, colour alpha) and the
        /// eye buffer it is drawn into (colour / depth-stencil format, MSAA, foveation): the evidence for "full opacity" on the headset itself</summary>
        public static void LogSkeletonProbe(string when)
        {
            try
            {
                string report = Dancer.SkeletonReport(out bool ok);
                UnityEngine.XR.XRDisplaySubsystem xr = null;
                System.Collections.Generic.List<UnityEngine.XR.XRDisplaySubsystem> list = new();
                SubsystemManager.GetSubsystems(list);
                if (list.Count > 0) xr = list[0];
                RenderTextureDescriptor d = UnityEngine.XR.XRSettings.eyeTextureDesc;
                var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                Debug.Log($"HM-VR skeleton ({when}): {(ok ? "SOLID OK" : "NOT SOLID")}: {report} | eye buffer {d.width}x{d.height} colour {d.graphicsFormat} depth/stencil {d.depthStencilFormat} (stencil plane {UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsStencilFormat(d.depthStencilFormat)}) msaa {d.msaaSamples} | urp hdr {(urp != null ? urp.supportsHDR : false)} msaa {(urp != null ? urp.msaaSampleCount : 0)} | foveation {(xr != null ? xr.foveatedRenderingLevel : -1f):0.0} | avatar opacity {AvatarOpacityNow():0.00}");
            }
            catch (Exception e)
            {
                Debug.LogWarning("HM-VR skeleton probe failed: " + e.Message);
            }
        }

        static float AvatarOpacityNow()
        {
            foreach (SmplxAvatar a in FindObjectsByType<SmplxAvatar>(FindObjectsInactive.Exclude)) return a.Opacity;
            return -1f;
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

            // not steady state: the hair bake runs on the main thread in chunks for the first ~25 s of the recap (it logs bake=n/N), and a seek refills the caches.
            // A step-down in those windows would trade the hair for a transient (X4 2026-10-11: HM-PERF adaptive fired at 47 % over budget in a recap session)
            if (Time.unscaledTime >= nextHairScan)
            {
                nextHairScan = Time.unscaledTime + 1f;
                hairCached = FindAnyObjectByType<HairStrands>();
            }

            bool baking = hairCached != null && hairCached.Avatar != null && VrSettings.Current.HairBake && hairCached.BakedFrames < hairCached.Avatar.FrameCount;
            if (baking || Time.unscaledTime < noAdaptUntil)
            {
                lastAdaptReason = baking ? "bake" : "seek";
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
                    Debug.Log($"HM-PERF adaptive: {share * 100f:0}% of frames over budget, hair {was.ToString().ToLowerInvariant()} -> {VrQuality.Hair.ToString().ToLowerInvariant()} (steady state: bake done {(hairCached != null ? hairCached.BakedFrames : -1)}/{(hairCached != null && hairCached.Avatar != null ? hairCached.Avatar.FrameCount : 0)}, last window gpu {(timingN > 0 ? gpuSum / timingN : -1):0.0} ms cpu {(timingN > 0 ? cpuSum / timingN : -1):0.0} ms, lod {(hairCached != null ? hairCached.Lod : -1)}, solid skeleton {Dancer.SolidSkeletonOn}, mode {(VrDisplay.Instance != null && VrDisplay.Instance.PassthroughActive ? "passthrough" : "immersive")}, last skipped window: '{lastAdaptReason}')");
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
