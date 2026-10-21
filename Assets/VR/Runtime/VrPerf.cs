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
    /// the library's card count. With Adaptive on (quest_settings.json "adaptive", -hmadaptive on|off) a lesson that keeps missing its
    /// budget TRIES the hair one level down (Full to Light to Off), once per 10 seconds at most: the next steady 4-s window is compared
    /// with the one that triggered it, and a step that gained no frame time (mean frame and GPU time both within noise) is undone and
    /// not tried again in that lesson. A hair whose simulation drives a splat dancer's bound hair is never taken to Off (the splat hair
    /// would freeze: VrQuality.SplatHairNeedsSimulation). Every decision is an HM-PERF adaptive line. It never steps up by itself
    /// (the menu does; an undo only takes back its own trial).
    /// 2026-10-21: the settings are loaded before this component starts (VrSession.Init), and Adaptive follows VrSettings.Current
    /// unless a caller sets it: "adaptive": false used to be read from the defaults (the file was loaded after Awake).
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

        /// <summary>the adaptive hair step: VrSettings.Current.Adaptive (the file, -hmadaptive) unless set here (the editor scenario)</summary>
        public bool Adaptive
        {
            get => adaptiveForced ?? VrSettings.Current.Adaptive;
            set => adaptiveForced = value;
        }

        bool? adaptiveForced;
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
        float viewAt = -1f;
        string viewId;
        float shotAt = -1f;
        int shotsLeft;

        // the bench sequence (desk tests over adb, Tools/quest_splat_bench.py cycle): -hmviews a,b,c and -hmsplatscales s1,s2,..
        // step through views x -hmbenchreps x scales after the first view is taken, one step every -hmbenchdwell seconds
        readonly List<(string View, float Scale)> benchSteps = new();
        int benchIndex = -1;
        float benchNextAt = -1f, benchDwell = 8f, benchShotAt = -1f;
        bool benchShots;
        float lessonSince = -1f, adaptCooldown;
        float loadStart = -1f;
        bool wasLesson;
        float overBudgetFrames, adaptWindowFrames, adaptWindowStart;
        float skeletonProbeAt = -1f;
        float noAdaptUntil, nextHairScan;
        HairStrands hairCached;
        string lastAdaptReason = "";
        double adaptDtSum, adaptGpuSum;
        int adaptGpuN;
        float frameGpuMs = -1f, adaptSettleUntil, nextFloorCheck;

        /// <summary>a hair step on trial: the window that triggered it, judged against the next steady window</summary>
        struct Trial
        {
            public HairLevel From, To;
            public float MeanMs, GpuMs, Share;
        }

        Trial? trial;
        readonly HashSet<(HairLevel, HairLevel)> noGain = new();
        public int AdaptSteps { get; private set; }
        public int AdaptUndos { get; private set; }
        public string LastAdaptDecision { get; private set; } = "";

        /// <summary>a seek / chapter jump: the frames right after it (poses, hair cache, textures) are not steady state, so the adaptive step-down ignores them</summary>
        public static void NoteSeek()
        {
            if (Instance != null) Instance.noAdaptUntil = Time.unscaledTime + 6f;
        }

        void Awake()
        {
            Instance = this;
            VrSettings s = VrSettings.Current.Loaded ? VrSettings.Current : VrSettings.Load();
            Enabled = s.PerfLog;
            Debug.Log($"HM-PERF settings: adaptive {(s.Adaptive ? "on" : "off")}, perf log {(s.PerfLog ? "on" : "off")}, hair {s.Hair.ToString().ToLowerInvariant()}, settings {s.Source}");
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
                frameGpuMs = -1f;
                if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
                {
                    if (timings[0].gpuFrameTime > 0.0) frameGpuMs = (float)timings[0].gpuFrameTime;
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
            if (Time.unscaledTime >= nextFloorCheck)
            {
                nextFloorCheck = Time.unscaledTime + 1f;
                VrQuality.EnforceSplatHairFloor(); // the splat dancers are built a moment after the avatars
            }

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

                ResetAdaptWindow();
                trial = null;
                noGain.Clear();
                Debug.Log($"HM-PERF adaptive: {(Adaptive ? "on" : "off")} for lesson {LoadedLesson} (settings {VrSettings.Current.Source}{(adaptiveForced.HasValue ? ", set by a caller" : "")}), hair {VrQuality.Hair.ToString().ToLowerInvariant()}");
                skeletonProbeAt = Time.unscaledTime + 3f; // the avatars, lines and beads exist a moment after the lesson starts
                LogSkeletonProbe("lesson start");
                string seek = VrArgs.Get("-hmseek");
                if (seek != null && float.TryParse(seek, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float at)) seekAt = (Time.unscaledTime + 4f, at);
                // -hmview start|front|side|overhead|phone:N (a desk test of a vantage point, immersive display): taken after the seek
                viewId = VrArgs.Get("-hmview");
                viewAt = viewId != null ? Time.unscaledTime + 5.5f : -1f;
                // -hmshots N: N stereo stills of the eye buffers (both eyes side by side, the scene layer without the passthrough), one every
                // 6 s from 9 s after the lesson starts, written to the app's files/hmshots/ (adb pull): what each eye is given
                shotsLeft = int.TryParse(VrArgs.Get("-hmshots"), out int shots) ? Mathf.Clamp(shots, 0, 20) : 0;
                // -hmshotfrom S: the first still S seconds after the lesson start instead of 9 (e.g. after the recap's hair bake)
                float shotFrom = float.TryParse(VrArgs.Get("-hmshotfrom"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sf) ? Mathf.Max(1f, sf) : 9f;
                shotAt = shotsLeft > 0 ? Time.unscaledTime + shotFrom : -1f;
                BuildBench();
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

            if (viewAt >= 0f && Time.unscaledTime >= viewAt && lesson)
            {
                viewAt = -1f;
                VrLocomotion loc = VrLocomotion.Instance;
                bool ok = false;
                if (loc != null)
                {
                    // the viewpoints are relative to where the couple is now (after the seek)
                    FilmDirector d = VrFilm.Director;
                    float t = d != null && !float.IsNaN(d.DanceTime) ? d.DanceTime : 0f;
                    loc.SetViewpoints(VrViewpoints.Build(HeadMovement.Instance, VrFilm.Entry, t));
                    ok = loc.GoTo(viewId, false);
                }

                Debug.Log($"HM-PERF view '{viewId}' (command line): {(ok ? "taken" : "not found")}");
                if (benchSteps.Count > 0) StartBenchStep(0);
            }

            if (benchIndex >= 0 && lesson && Time.unscaledTime >= benchNextAt)
            {
                if (benchIndex + 1 < benchSteps.Count) StartBenchStep(benchIndex + 1);
                else
                {
                    benchIndex = -1;
                    Debug.Log($"HM-PERF bench done ({benchSteps.Count} steps)");
                }
            }

            if (benchShotAt >= 0f && lesson && Time.unscaledTime >= benchShotAt)
            {
                benchShotAt = -1f;
                StartCoroutine(StereoShot());
            }

            if (shotAt >= 0f && Time.unscaledTime >= shotAt && lesson)
            {
                shotsLeft--;
                shotAt = shotsLeft > 0 ? Time.unscaledTime + 6f : -1f;
                StartCoroutine(StereoShot());
            }

            if (!lesson) lessonSince = -1f;
            if (lib != null && lib.State != FilmLibrary.Mode.Loading && lesson) loadStart = -1f;
            if (lib != null && lib.State == FilmLibrary.Mode.Library) loadStart = -1f;
            wasLesson = lesson;
        }

        /// <summary>-hmviews / -hmsplatscales (with -hmbenchreps N, default 2, -hmbenchdwell S, default 8, -hmbenchshots 1): the bench
        /// sequence, one step per view x rep x scale; nothing without those switches</summary>
        void BuildBench()
        {
            benchSteps.Clear();
            benchIndex = -1;
            benchShotAt = -1f;
            string views = VrArgs.Get("-hmviews");
            string scales = VrArgs.Get("-hmsplatscales");
            if (views == null && scales == null) return;
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            List<string> vl = new();
            foreach (string v in (views ?? viewId ?? "start").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) vl.Add(v.Trim());
            List<float> sl = new();
            if (scales != null)
            {
                foreach (string x in scales.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (float.TryParse(x.Trim(), System.Globalization.NumberStyles.Float, inv, out float f)) sl.Add(f);
                }
            }

            if (sl.Count == 0) sl.Add(float.NaN);
            int reps = int.TryParse(VrArgs.Get("-hmbenchreps"), out int r) ? Mathf.Clamp(r, 1, 10) : sl.Count > 1 ? 2 : 1;
            benchDwell = float.TryParse(VrArgs.Get("-hmbenchdwell"), System.Globalization.NumberStyles.Float, inv, out float dw) ? Mathf.Clamp(dw, 2f, 600f) : 8f;
            benchShots = VrArgs.Get("-hmbenchshots") == "1";
            foreach (string v in vl)
            {
                for (int k = 0; k < reps; k++)
                {
                    foreach (float f in sl) benchSteps.Add((v, f));
                }
            }

            if (viewId == null)
            {
                viewId = vl[0];
                viewAt = Time.unscaledTime + 5.5f;
            }

            Debug.Log($"HM-PERF bench: {benchSteps.Count} steps (views {string.Join(",", vl)} x {reps} x scales {scales ?? "as set"}), {benchDwell:0.#} s each{(benchShots ? ", a still 0.3 s into each step of the first round" : "")}");
        }

        void StartBenchStep(int i)
        {
            benchIndex = i;
            (string v, float f) = benchSteps[i];
            if (i > 0 && v != benchSteps[i - 1].View)
            {
                VrLocomotion loc = VrLocomotion.Instance;
                bool ok = false;
                if (loc != null)
                {
                    FilmDirector d = VrFilm.Director;
                    float t = d != null && !float.IsNaN(d.DanceTime) ? d.DanceTime : 0f;
                    loc.SetViewpoints(VrViewpoints.Build(HeadMovement.Instance, VrFilm.Entry, t));
                    ok = loc.GoTo(v, false);
                }

                Debug.Log($"HM-PERF view '{v}' (bench): {(ok ? "taken" : "not found")}");
            }

            string applied = ApplyBenchScale(f);
            benchNextAt = Time.unscaledTime + benchDwell;
            // one still per view and scale (the first round): 0.3 s in, inside the bench tool's settle time
            bool firstRound = true;
            for (int k = 0; k < i; k++)
            {
                if (benchSteps[k].View == v && (benchSteps[k].Scale.Equals(f))) firstRound = false;
            }

            if (benchShots && firstRound) benchShotAt = Time.unscaledTime + 0.3f;
            Debug.Log($"HM-PERF bench step {i + 1}/{benchSteps.Count} view '{v}' scale {applied} dwell {benchDwell.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s");
        }

        static string ApplyBenchScale(float f)
        {
            if (float.IsNaN(f)) return "as-set";
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
            // only inside the low-res layer (a change across 1 would swap the URP feature in the middle of a run)
            if (!HmSplats.SplatUrpFeature.LowResActive || f >= 0.999f || HmSplats.SplatUrpFeature.LowResScale >= 0.999f) return "not-applied(needs-the-low-res-layer)";
            HmSplats.SplatUrpFeature.LowResScale = Mathf.Clamp(f, 0.2f, 0.99f);
            return HmSplats.SplatUrpFeature.LowResScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
#else
            return "not-applied";
#endif
        }

        /// <summary>a still of both eyes' images (ScreenCapture, StereoScreenCaptureMode.BothEyes) to files/hmshots/ (desk tests: -hmshots N)</summary>
        System.Collections.IEnumerator StereoShot()
        {
            yield return new WaitForEndOfFrame();
            SaveStereoShot();
        }

        static void SaveStereoShot()
        {
            try
            {
                Texture2D t = ScreenCapture.CaptureScreenshotAsTexture(ScreenCapture.StereoScreenCaptureMode.BothEyes);
                if (t == null)
                {
                    Debug.LogWarning("HM-PERF shot: no image (ScreenCapture returned null)");
                    return;
                }

                string dir = System.IO.Path.Combine(Application.persistentDataPath, "hmshots");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, $"hm_{DateTime.Now:HHmmss}_film{VrFilm.Time:000.0}.png");
                System.IO.File.WriteAllBytes(file, t.EncodeToPNG());
                Debug.Log($"HM-PERF shot {t.width}x{t.height} -> {file}");
                Destroy(t);
            }
            catch (Exception e)
            {
                Debug.LogWarning("HM-PERF shot failed: " + e.Message);
            }
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

        void ResetAdaptWindow()
        {
            adaptWindowStart = Time.unscaledTime;
            overBudgetFrames = adaptWindowFrames = 0f;
            adaptDtSum = adaptGpuSum = 0.0;
            adaptGpuN = 0;
        }

        void Adapt(float dt)
        {
            if (!Adaptive || lessonSince < 0f || VrQuality.Current != VrQuality.Tier.Quest || (VrQuality.Hair == HairLevel.Off && trial == null))
            {
                ResetAdaptWindow();
                return;
            }

            if (Time.unscaledTime - lessonSince < 12f || VrFilm.Paused || Time.unscaledTime < adaptSettleUntil) // startup hitches (the hair bake begins, textures upload) are not a reason
            {
                ResetAdaptWindow();
                return;
            }

            // not steady state: the hair bake runs on the main thread in chunks for the first ~25 s of the recap (it logs bake=n/N), and a seek refills the caches.
            // A step-down in those windows would trade the hair for a transient (X4 2026-10-11: HM-PERF adaptive fired at 47 % over budget in a recap session).
            // A trial step waits for the next steady window too.
            if (Time.unscaledTime >= nextHairScan)
            {
                nextHairScan = Time.unscaledTime + 1f;
                hairCached = FindAnyObjectByType<HairStrands>();
            }

            bool baking = hairCached != null && hairCached.Avatar != null && VrSettings.Current.HairBake && hairCached.BakedFrames < hairCached.Avatar.FrameCount;
            if (baking || Time.unscaledTime < noAdaptUntil)
            {
                lastAdaptReason = baking ? "bake" : "seek";
                ResetAdaptWindow();
                return;
            }

            adaptWindowFrames++;
            adaptDtSum += dt;
            if (frameGpuMs > 0f)
            {
                adaptGpuSum += frameGpuMs;
                adaptGpuN++;
            }

            if (dt > BudgetSeconds * 1.35f) overBudgetFrames++;
            if (Time.unscaledTime - adaptWindowStart < 4f) return;
            float share = overBudgetFrames / Mathf.Max(1f, adaptWindowFrames);
            float mean = (float)(adaptDtSum / Mathf.Max(1f, adaptWindowFrames) * 1000.0);
            float gpu = adaptGpuN > 0 ? (float)(adaptGpuSum / adaptGpuN) : -1f;
            ResetAdaptWindow();

            if (trial.HasValue)
            {
                Trial t = trial.Value;
                trial = null;
                Judge(t, share, mean, gpu);
                return;
            }

            if (share <= 0.18f || Time.unscaledTime < adaptCooldown) return;
            HairLevel was = VrQuality.Hair;
            HairLevel next = was == HairLevel.Full ? HairLevel.Light : HairLevel.Off;
            string window = $"{share * 100f:0}% of frames over budget (mean frame {mean:0.0} ms, gpu {gpu:0.0} ms)";
            if (next == HairLevel.Off && VrQuality.SplatHairNeedsSimulation())
            {
                adaptCooldown = Time.unscaledTime + 30f;
                Decide($"HM-PERF adaptive: {window}, hair stays {Name(was)}: its simulation drives the splat dancer's bound hair (off would freeze it; splat hair {SplatHairNow()})");
                return;
            }

            if (noGain.Contains((was, next)))
            {
                adaptCooldown = Time.unscaledTime + 30f;
                Decide($"HM-PERF adaptive: {window}, hair stays {Name(was)}: the step to {Name(next)} gained nothing earlier in this lesson");
                return;
            }

            if (!VrQuality.StepHairDown()) return;
            AdaptSteps++;
            trial = new Trial { From = was, To = next, MeanMs = mean, GpuMs = gpu, Share = share };
            adaptCooldown = Time.unscaledTime + 10f;
            adaptSettleUntil = Time.unscaledTime + 1f; // the frame after the change is not the new steady state
            Decide($"HM-PERF adaptive: {window}, trying hair {Name(was)} -> {Name(next)} (kept only if the next steady window gains frame time; " +
                   $"bake done {(hairCached != null ? hairCached.BakedFrames : -1)}/{(hairCached != null && hairCached.Avatar != null ? hairCached.Avatar.FrameCount : 0)}, lod {(hairCached != null ? hairCached.Lod : -1)}, " +
                   $"splat hair {SplatHairNow()}, solid skeleton {Dancer.SolidSkeletonOn}, mode {(VrDisplay.Instance != null && VrDisplay.Instance.PassthroughActive ? "passthrough" : "immersive")}, last skipped window: '{lastAdaptReason}')");
        }

        /// <summary>a trial step against the window that triggered it: kept when the mean frame time or the GPU time fell by more than
        /// the noise (0.5 ms and 4 % / 5 %), else undone and not tried again in this lesson</summary>
        void Judge(Trial t, float share, float mean, float gpu)
        {
            string numbers = $"mean frame {t.MeanMs:0.0} -> {mean:0.0} ms, gpu {t.GpuMs:0.0} -> {gpu:0.0} ms, over budget {t.Share * 100f:0}% -> {share * 100f:0}%";
            if (VrQuality.Hair != t.To)
            {
                Decide($"HM-PERF adaptive: trial {Name(t.From)} -> {Name(t.To)} dropped: the hair level was changed to {Name(VrQuality.Hair)} meanwhile ({numbers})");
                return;
            }

            bool gpuKnown = t.GpuMs > 0f && gpu > 0f;
            bool gained = t.MeanMs - mean >= Mathf.Max(0.5f, 0.04f * t.MeanMs) || (gpuKnown && t.GpuMs - gpu >= Mathf.Max(0.5f, 0.05f * t.GpuMs));
            if (gained)
            {
                Decide($"HM-PERF adaptive: kept hair {Name(t.To)}: it gained frame time ({numbers})");
                return;
            }

            VrQuality.UndoHairStep(t.From);
            AdaptUndos++;
            noGain.Add((t.From, t.To));
            adaptCooldown = Time.unscaledTime + 10f;
            adaptSettleUntil = Time.unscaledTime + 1f;
            Decide($"HM-PERF adaptive: undo hair {Name(t.To)} -> {Name(t.From)}: no frame time gained ({numbers}); not tried again in this lesson (splat hair {SplatHairNow()})");
        }

        void Decide(string line)
        {
            LastAdaptDecision = line;
            Debug.Log(line);
        }

        static string Name(HairLevel l) => l.ToString().ToLowerInvariant();

        static string SplatHairNow()
        {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
            return SplatDancers.SplatHairState();
#else
            return "-";
#endif
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
                sb.Append(" hairSim=").Append(hair.isActiveAndEnabled ? "on" : "off");
            }

            sb.Append(" adaptive=").Append(Adaptive ? "on" : "off");
            if (AdaptSteps > 0) sb.Append(" steps=").Append(AdaptSteps).Append(" undos=").Append(AdaptUndos);
            if (trial.HasValue) sb.Append(" trial=").Append(Name(trial.Value.From)).Append("->").Append(Name(trial.Value.To));
            string splatHair = SplatHairNow();
            if (splatHair != "-") sb.Append(" splatHair=").Append(splatHair.Replace(' ', '_'));

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
