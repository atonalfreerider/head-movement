using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

/// <summary>
/// Film recording (VIEWER_SPEC 8, film/INTERFACE.md): Head Movement / Record Vertical | Record Horizontal (+ 4K) and the
/// CLI (hm_film, FilmRecorderCli) render a capture's directed film with the Unity Recorder:
///
///   1. pick the capture (EditorPrefs HM.Film.Capture; empty = the newest capture in StreamingAssets that has a film
///      direction) and its direction file (StreamingAssets/&lt;capture&gt;/film/direction*.json, then the workspace's
///      dancecap/work/*/film/direction*.json; the first whose "capture" is that folder); (re)build the film soundtrack
///      &lt;film dir&gt;/audio/&lt;direction&gt;_mix.wav with film/audio/make_film_mix.py when it is missing or older than the
///      direction / narration timeline;
///   2. open Assets/head-movement.unity when the open scene has no HeadMovement, enter Play mode (the job lives in
///      SessionState and survives the domain reload), load the capture, wait for its audio, warm up, set the Game view
///      to the preset size, FilmDirector.Prepare (by reflection) and wait IsReady;
///   3. in ONE game frame: start the Movie Recorder (Game view input, MP4 H.264 High + AAC from the AudioListener mix,
///      constant 30 fps, frame-locked), FilmDirector.Begin(direction, aspect[, start]) and FilmSoundtrack.Play(mix);
///   4. stop when the director reports IsFinished (or the requested duration is recorded), exit Play mode, validate the
///      MP4 with ffprobe/ffmpeg (FilmRenderCheck: size, fps, frame count, duration, audio stream + level, A/V sync
///      against the mix) and write &lt;name&gt;.report.json + .report.md next to it in head-movement/Recordings/
///      (git-ignored: renders contain the copyrighted song and the narration voice - private).
///
/// Without a FilmDirector type the recorder runs a PLAIN pipeline test: HeadMovement plays the capture with its own song
/// for the requested duration (default 10 s); the report says director: missing.
/// </summary>
[InitializeOnLoad]
public static class FilmRecorder
{
    const string Menu = "Head Movement/";
    const string JobKey = "HM.Film.Job";
    public const string CapturePref = "HM.Film.Capture";
    public const string PythonPref = "HM.Film.Python";
    public const string ScenePath = "Assets/head-movement.unity";
    const float PlainDefaultSeconds = 10f;
    const int WarmupFrames = 45;
    const double WarmupSeconds = 1.5;
    const double PrepareTimeout = 150;
    const double LoadTimeout = 180;

    public static readonly string[] Terminal = { "done", "failed", "aborted" };

    // ------------------------------------------------------------------ job (SessionState JSON)

    [Serializable]
    public class Job
    {
        public string id, state, message, source, created, finished;
        public string capture, aspect, size, preset, direction, directionName, filmDir, mix, mixLog, output, report;
        public int width, height, fps;
        public float start, duration, filmDuration, filmEnd;
        public string mode; // director | plain
        public bool plainMix; // plain test that plays the film soundtrack (frame clock) instead of the capture's song
        public int[] flashAt; // plain test: recorded-frame indices painted white (video frame alignment check)
        public int armFrame = -1, recorderFramesAtBegin = -1, recorderFrames = -1;
        public string stopReason;
        public bool needsMix, restartPlay, enterRequested, loadRequested, prepareCalled, begin3, gameViewSet;
        public bool aborted;
        public float filmTime, danceTime, lastFilmTime;
        public string segment, directorError, directorType;
        public int frames, expectedFrames, startFrame, stopFrame = -1, stallFrames, warmFrames;
        public double phaseStart, recordStartReal, recordEndReal, realElapsed;
        public float soundtrackStartedAt = float.NaN;
        public int soundtrackStartFrame = -1;
        public string soundtrack;
        public int screenW, screenH;
        public float videoMbps;
        public string gpu, unity, recorderVersion;
        public List<string> warnings = new();

        public bool Active => state != null && Array.IndexOf(Terminal, state) < 0;
    }

    static Job cached;

    public static Job Current
    {
        get
        {
            if (cached != null) return cached;
            string s = SessionState.GetString(JobKey, "");
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                cached = JsonConvert.DeserializeObject<Job>(s);
            }
            catch
            {
                cached = null;
            }

            return cached;
        }
    }

    static void Save(Job j)
    {
        cached = j;
        SessionState.SetString(JobKey, JsonConvert.SerializeObject(j));
    }

    static void SetState(Job j, string state, string message = null)
    {
        j.state = state;
        if (message != null) j.message = message;
        j.phaseStart = EditorApplication.timeSinceStartup;
        Save(j);
        string line = $"[film] {state}{(message != null ? ": " + message : "")}";
        if (state is "failed" or "aborted") Debug.LogWarning(line);
        else Debug.Log(line);
    }

    static void Fail(Job j, string message)
    {
        j.finished = Now();
        SetState(j, "failed", message);
    }

    static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ lifecycle hooks

    static FilmRecorder()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.update += EditorTick;
        // a script reload inside Play mode loses the Recorder session: a running job cannot continue
        EditorApplication.delayCall += () =>
        {
            Job j = Current;
            if (j == null || !EditorApplication.isPlaying) return;
            if (j.state is "loading" or "preparing" or "arming" or "recording" or "stopping" && FilmRecordingDriver.Instance != null &&
                FilmRecordingDriver.Instance.OnFrame == null)
            {
                j.warnings.Add("scripts reloaded during Play mode - the recording was interrupted");
                j.aborted = true;
                SetState(j, "stopping", "interrupted by a script reload");
                EditorApplication.ExitPlaymode();
            }
        };
    }

    static void OnPlayModeChanged(PlayModeStateChange change)
    {
        Job j = Current;
        if (j == null || !j.Active) return;
        switch (change)
        {
            case PlayModeStateChange.EnteredPlayMode:
                if (j.state == "entering")
                {
                    j.phaseStart = EditorApplication.timeSinceStartup;
                    SetState(j, "loading", $"loading {j.capture}");
                    FilmRecordingDriver.Ensure().OnFrame = PlayTick;
                }

                break;
            case PlayModeStateChange.ExitingPlayMode:
                if (j.state is "loading" or "preparing" or "arming" or "recording")
                {
                    // the user (or something else) left Play mode: the Recorder finalises the file on its own
                    StopRecorder(j);
                    j.aborted = true;
                    j.warnings.Add($"Play mode was exited while {j.state}");
                    SetState(j, "stopping", "Play mode exited early");
                }

                break;
            case PlayModeStateChange.EnteredEditMode:
                if (j.state == "stopping")
                {
                    if (File.Exists(j.output)) BeginValidation(j);
                    else Fail(j, j.message ?? "no output file");
                }
                else if (j.state is "loading" or "preparing" or "arming" or "recording")
                {
                    Fail(j, $"Play mode ended while {j.state}");
                }

                break;
        }
    }

    /// <summary>edit-mode steps: soundtrack mix process, (re)entering Play mode, validation</summary>
    static void EditorTick()
    {
        Job j = Current;
        if (j == null || !j.Active) return;
        switch (j.state)
        {
            case "mixing":
                TickMix(j);
                break;
            case "restarting":
                if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    j.enterRequested = false;
                    SetState(j, j.needsMix ? "mixing" : "entering");
                }

                break;
            case "entering":
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) break;
                if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode && !j.enterRequested)
                {
                    j.enterRequested = true;
                    Save(j);
                    EditorApplication.EnterPlaymode();
                }
                else if (!EditorApplication.isPlayingOrWillChangePlaymode && j.enterRequested &&
                         EditorApplication.timeSinceStartup - j.phaseStart > 240)
                {
                    Fail(j, "Play mode did not start within 240 s");
                }

                break;
            case "validating":
                FilmRenderCheck.Tick(j, OnValidated);
                break;
            case "loading":
            case "preparing":
            case "arming":
            case "recording":
            case "stopping":
                if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    if (j.state == "stopping" && File.Exists(j.output)) BeginValidation(j);
                    else Fail(j, $"not in Play mode while {j.state}");
                }

                break;
        }
    }

    // ------------------------------------------------------------------ queueing a job (menus, window, CLI)

    public class Options
    {
        public string aspect = "vertical", size = "hd", capture, source = "menu";
        public float start, duration;
        public int fps = 30;
        public bool plain; // force the plain pipeline test even when a director exists

        // plain test only: play the capture's film soundtrack (direction mix, from film time `start`, on a frame clock)
        // instead of HeadMovement's song - tests the soundtrack player, the Recorder's audio capture and the A/V sync check
        public bool mix;
    }

    /// <summary>queue a recording; returns null on success or an error message</summary>
    public static string Queue(Options o, bool interactive)
    {
        Job running = Current;
        if (running != null && running.Active) return $"a film recording is already {running.state} ({running.output}); stop it first";
        if (EditorApplication.isCompiling) return "scripts are compiling - try again when they finish";

        string aspect = (o.aspect ?? "vertical").Trim().ToLowerInvariant();
        aspect = aspect is "v" or "9:16" or "9x16" or "portrait" ? "vertical" : aspect is "h" or "16:9" or "16x9" or "landscape" ? "horizontal" : aspect;
        if (aspect != "vertical" && aspect != "horizontal") return $"aspect must be vertical or horizontal, not '{o.aspect}'";
        string size = (o.size ?? "hd").Trim().ToLowerInvariant();
        if (size != "hd" && size != "4k") return $"size must be hd or 4k, not '{o.size}'";
        if (o.fps != 30 && o.fps != 60) return "fps must be 30 or 60";
        (int w, int h) = size == "4k" ? (aspect == "vertical" ? (2160, 3840) : (3840, 2160)) : (aspect == "vertical" ? (1080, 1920) : (1920, 1080));

        string capture = string.IsNullOrWhiteSpace(o.capture) ? DefaultCapture() : o.capture.Trim();
        if (capture == null) return "no capture in StreamingAssets has a film direction (direction*.json with its \"capture\")";
        if (!Directory.Exists(Path.Combine(Application.streamingAssetsPath, capture)))
            return $"no capture '{capture}' in StreamingAssets (have {string.Join(", ", CaptureNames())})";

        Job j = new()
        {
            id = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture), created = Now(), source = o.source,
            capture = capture, aspect = aspect, size = size, width = w, height = h, fps = o.fps,
            preset = $"{aspect}_{size}", start = Mathf.Max(0f, o.start), duration = Mathf.Max(0f, o.duration),
            unity = Application.unityVersion, recorderVersion = RecorderVersion(), gpu = SystemInfo.graphicsDeviceName
        };

        bool director = DirectorType() != null && !o.plain;
        string directionPath = FindDirection(capture);
        if ((director || o.mix) && directionPath == null) return $"no film direction for {capture} (looked in {string.Join("; ", FilmDirs(capture))})";
        j.mode = director ? "director" : "plain";
        j.plainMix = !director && o.mix;
        j.directorType = DirectorType()?.AssemblyQualifiedName;

        if (directionPath != null)
        {
            JObject d;
            try
            {
                d = JObject.Parse(File.ReadAllText(directionPath));
            }
            catch (Exception e)
            {
                return $"{directionPath}: {e.Message}";
            }

            j.direction = directionPath;
            j.filmDir = Path.GetDirectoryName(directionPath);
            j.directionName = d.Value<string>("name") ?? Path.GetFileNameWithoutExtension(directionPath);
            float tl = d["timeline"]?.Value<float?>("film_duration_s") ?? 0f;
            float last = d["segments"] is JArray segs && segs.Count > 0 ? segs.Last["film"]?[1]?.Value<float>() ?? 0f : 0f;
            j.filmDuration = Mathf.Max(tl, last);
            j.mix = Path.Combine(j.filmDir, "audio", $"{j.directionName}_mix.wav");
            j.mixLog = Path.Combine(j.filmDir, "audio", $"{j.directionName}_mix.log");
        }

        if (director || j.plainMix)
        {
            if (j.filmDuration <= 0) return $"{directionPath}: no film duration (timeline.film_duration_s / segments)";
            if (j.start >= j.filmDuration) return $"start {j.start} s is past the film's end ({j.filmDuration:0.##} s)";
            string stale = MixStaleReason(j);
            if (stale != null)
            {
                if (MixScript(j) == null || Python() == null)
                    return $"the film soundtrack is {stale} and cannot be rebuilt here (python {Python() ?? "missing"}, " +
                           $"script {MixScript(j) ?? "missing"}): run film/audio/make_film_mix.py";
                j.needsMix = true;
                j.warnings.Add($"soundtrack {stale}: rebuilt with make_film_mix.py");
            }
        }

        if (director)
        {
            if (j.start > 0 && DirectorType().GetMethod("Begin", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(string), typeof(float) }, null) == null)
            {
                j.warnings.Add("FilmDirector has no Begin(path, aspect, start): recorded from film time 0");
                j.start = 0f;
            }

            j.filmEnd = j.duration > 0 ? Mathf.Min(j.filmDuration, j.start + j.duration) : j.filmDuration;
        }
        else
        {
            if (!j.plainMix) j.start = 0f;
            float len = j.duration > 0 ? j.duration : PlainDefaultSeconds;
            j.filmEnd = j.plainMix ? Mathf.Min(j.filmDuration, j.start + len) : len;
            string what = j.plainMix
                ? $"HeadMovement playback (its song muted) under the film soundtrack from film {j.start:0.##} s on a frame clock"
                : "HeadMovement playback with the capture's own song";
            j.warnings.Add(DirectorType() == null
                ? $"director: missing (no FilmDirector type) - PLAIN pipeline test: {what}"
                : $"plain mode requested - {what}");
        }

        j.expectedFrames = Mathf.RoundToInt((j.filmEnd - j.start) * j.fps);
        j.output = OutputPath(j);

        // the scene must contain HeadMovement
        if (!EditorApplication.isPlaying && Object.FindAnyObjectByType<HeadMovement>() == null)
        {
            if (interactive)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return "cancelled";
            }
            else if (Enumerable.Range(0, EditorSceneManager.sceneCount).Any(i => EditorSceneManager.GetSceneAt(i).isDirty))
            {
                return $"the open scene has unsaved changes and no HeadMovement: open {ScenePath} first";
            }

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (Object.FindAnyObjectByType<HeadMovement>() == null) return $"{ScenePath} has no HeadMovement";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(j.output));
        j.restartPlay = EditorApplication.isPlaying;
        Save(j);
        if (j.restartPlay)
        {
            SetState(j, "restarting", "leaving Play mode to start from a clean scene");
            EditorApplication.ExitPlaymode();
        }
        else
        {
            SetState(j, j.needsMix ? "mixing" : "entering", $"{j.capture} {j.preset} -> {Path.GetFileName(j.output)}");
        }

        return null;
    }

    public static string Stop(string why = "stopped by the user")
    {
        Job j = Current;
        if (j == null || !j.Active) return "no film recording is running";
        switch (j.state)
        {
            case "arming":
            case "recording":
                StopAll(j, why, aborted: true);
                return "stopping";
            case "loading":
            case "preparing":
                j.aborted = true;
                SetState(j, "stopping", why);
                EditorApplication.ExitPlaymode();
                return "stopping";
            case "validating":
                return "already validating";
            default:
                j.finished = Now();
                SetState(j, "aborted", why);
                return "aborted";
        }
    }

    // ------------------------------------------------------------------ menus

    [MenuItem(Menu + "Record Vertical", false, 300)]
    static void RecordVertical() => FromMenu("vertical", "hd");

    [MenuItem(Menu + "Record Horizontal", false, 301)]
    static void RecordHorizontal() => FromMenu("horizontal", "hd");

    [MenuItem(Menu + "Record Vertical 4K", false, 302)]
    static void RecordVertical4K() => FromMenu("vertical", "4k");

    [MenuItem(Menu + "Record Horizontal 4K", false, 303)]
    static void RecordHorizontal4K() => FromMenu("horizontal", "4k");

    [MenuItem(Menu + "Record Vertical", true)]
    [MenuItem(Menu + "Record Horizontal", true)]
    [MenuItem(Menu + "Record Vertical 4K", true)]
    [MenuItem(Menu + "Record Horizontal 4K", true)]
    static bool CanRecord() => !(Current?.Active ?? false) && !EditorApplication.isCompiling;

    [MenuItem(Menu + "Stop Recording", false, 304)]
    static void StopMenu() => Debug.Log($"[film] {Stop()}");

    [MenuItem(Menu + "Stop Recording", true)]
    static bool CanStop() => Current?.Active ?? false;

    [MenuItem(Menu + "Record Settings...", false, 305)]
    static void SettingsMenu() => FilmRecorderWindow.Open();

    [MenuItem(Menu + "Open Recordings Folder", false, 306)]
    static void OpenRecordings()
    {
        Directory.CreateDirectory(RecordingsDir);
        EditorUtility.RevealInFinder(RecordingsDir + Path.DirectorySeparatorChar);
    }

    static void FromMenu(string aspect, string size)
    {
        string err = Queue(new Options { aspect = aspect, size = size, capture = EditorPrefs.GetString(CapturePref, ""), source = "menu" }, true);
        if (err != null && err != "cancelled") EditorUtility.DisplayDialog("Head Movement - Record", err, "OK");
    }

    // ------------------------------------------------------------------ discovery

    public static string ProjectRoot => Directory.GetParent(Application.dataPath)!.FullName;
    public static string WorkspaceRoot => Directory.GetParent(ProjectRoot)!.FullName;
    public static string RecordingsDir => Path.Combine(ProjectRoot, "Recordings");

    public static string[] CaptureNames() =>
        Directory.Exists(Application.streamingAssetsPath)
            ? Directory.GetDirectories(Application.streamingAssetsPath).Select(Path.GetFileName)
                .Where(n => !string.Equals(n, LibraryData.FolderName, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();

    public static IEnumerable<string> FilmDirs(string capture)
    {
        yield return Path.Combine(Application.streamingAssetsPath, capture, "film");
        string work = Path.Combine(WorkspaceRoot, "dancecap", "work");
        if (!Directory.Exists(work)) yield break;
        foreach (string d in Directory.GetDirectories(work).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string f = Path.Combine(d, "film");
            if (Directory.Exists(f)) yield return f;
        }
    }

    /// <summary>the direction*.json whose "capture" is this capture folder (StreamingAssets first, then the workspace)</summary>
    public static string FindDirection(string capture)
    {
        foreach (string dir in FilmDirs(capture))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string f in Directory.GetFiles(dir, "direction*.json").OrderBy(x => x.Length).ThenBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using JsonTextReader r = new(new StreamReader(f));
                    // "capture" is near the top of the file: stop reading at the first match
                    while (r.Read())
                    {
                        if (r.Depth == 1 && r.TokenType == JsonToken.PropertyName && (string)r.Value == "capture")
                        {
                            if (string.Equals(r.ReadAsString(), capture, StringComparison.OrdinalIgnoreCase)) return f;
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    // not a direction file
                }
            }
        }

        return null;
    }

    /// <summary>captures with a film direction, newest (highest number) first</summary>
    public static string[] FilmCaptures()
    {
        // the recorder window's capture list and the default capture: with a film library, the library's captures only
        // (--capture <folder> and the other developer commands still reach every capture)
        string[] only = LibraryData.PickerCaptureNames();
        return CaptureNames().Where(c => (only == null || only.Contains(c, StringComparer.OrdinalIgnoreCase)) && FindDirection(c) != null)
            .OrderByDescending(c => c, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string DefaultCapture() => FilmCaptures().FirstOrDefault();

    static Type directorType;

    public static Type DirectorType()
    {
        if (directorType != null) return directorType;
        // global namespace, any assembly (INTERFACE.md section 1); TypeCache only lists the loaded domain's types
        return directorType = TypeCache.GetTypesDerivedFrom<MonoBehaviour>()
            .FirstOrDefault(t => t.Name == "FilmDirector" && string.IsNullOrEmpty(t.Namespace));
    }

    static string OutputPath(Job j)
    {
        string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string part = j.mode == "director" && (j.start > 0 || j.duration > 0)
            ? $"_t{j.start.ToString("0.#", CultureInfo.InvariantCulture)}-{j.filmEnd.ToString("0.#", CultureInfo.InvariantCulture)}"
            : j.mode == "plain" ? (j.plainMix ? $"_plainmix_t{j.start.ToString("0.#", CultureInfo.InvariantCulture)}" : "_plain") : "";
        string stem = $"{j.capture}_{j.aspect}{(j.size == "4k" ? "_4k" : "")}{(j.fps != 30 ? $"_{j.fps}fps" : "")}_{date}{part}";
        string path = Path.Combine(RecordingsDir, stem + ".mp4");
        for (int k = 2; File.Exists(path) || File.Exists(Path.ChangeExtension(path, ".report.json")); k++)
            path = Path.Combine(RecordingsDir, $"{stem}_{k}.mp4");
        return path;
    }

    static string RecorderVersion()
    {
        try
        {
            UnityEditor.PackageManager.PackageInfo p = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(RecorderController).Assembly);
            return p != null ? p.version : "?";
        }
        catch
        {
            return "?";
        }
    }

    // ------------------------------------------------------------------ soundtrack mix (film/audio/make_film_mix.py)

    static string NarrationTimeline(Job j)
    {
        string dir = Path.Combine(j.filmDir, "narration");
        if (!Directory.Exists(dir)) return null;
        foreach (string f in Directory.GetFiles(dir, "timeline*.json").OrderBy(x => x.Length))
        {
            try
            {
                if (JObject.Parse(File.ReadAllText(f)).Value<string>("direction") == j.directionName) return f;
            }
            catch
            {
                // skip
            }
        }

        return null;
    }

    static string MixStaleReason(Job j)
    {
        if (!File.Exists(j.mix)) return "missing";
        DateTime mixTime = File.GetLastWriteTimeUtc(j.mix);
        if (File.GetLastWriteTimeUtc(j.direction) > mixTime) return "older than the direction";
        string tl = NarrationTimeline(j);
        if (tl != null && File.GetLastWriteTimeUtc(tl) > mixTime) return "older than the narration timeline";
        return null;
    }

    static string MixScript(Job j)
    {
        string s = Path.Combine(j.filmDir, "audio", "make_film_mix.py");
        return File.Exists(s) ? s : null;
    }

    public static string Python()
    {
        string p = EditorPrefs.GetString(PythonPref, "");
        if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
        foreach (string venv in new[] { ".venv-nopo4d", ".venv" })
        {
            string c = Path.Combine(WorkspaceRoot, venv, "Scripts", "python.exe");
            if (File.Exists(c)) return c;
        }

        return null;
    }

    static System.Diagnostics.Process mixProcess;

    static void TickMix(Job j)
    {
        if (mixProcess == null)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new(Python(), $"\"{MixScript(j)}\" \"{j.direction}\"")
                {
                    WorkingDirectory = Path.GetDirectoryName(MixScript(j)), UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                mixProcess = System.Diagnostics.Process.Start(psi);
                try
                {
                    mixProcess!.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
                }
                catch
                {
                    // best effort
                }

                j.message = "building the film soundtrack (make_film_mix.py)";
                Save(j);
            }
            catch (Exception e)
            {
                mixProcess = null;
                Fail(j, $"could not run make_film_mix.py: {e.Message}");
            }

            return;
        }

        if (!mixProcess.HasExited)
        {
            if (EditorApplication.timeSinceStartup - j.phaseStart > 600)
            {
                try
                {
                    mixProcess.Kill();
                }
                catch
                {
                    // ignore
                }

                mixProcess = null;
                Fail(j, "make_film_mix.py took more than 10 min");
            }

            return;
        }

        string log = mixProcess.StandardOutput.ReadToEnd() + mixProcess.StandardError.ReadToEnd();
        int code = mixProcess.ExitCode;
        mixProcess.Dispose();
        mixProcess = null;
        File.WriteAllText(j.mixLog, log);
        if (code != 0 || MixStaleReason(j) != null)
        {
            Fail(j, $"make_film_mix.py failed (exit {code}); see {j.mixLog}");
            return;
        }

        j.needsMix = false;
        SetState(j, "entering", "soundtrack ready");
    }

    // ------------------------------------------------------------------ Play mode (one call per game frame)

    static RecorderController controller;
    static Object directorInstance;
    static double nextStatePoll;

    static void PlayTick()
    {
        Job j = Current;
        if (j == null || !j.Active) return;
        double now = EditorApplication.timeSinceStartup;
        HeadMovement hm = HeadMovement.Instance;
        switch (j.state)
        {
            case "loading":
                if (hm == null)
                {
                    if (now - j.phaseStart > 60) StopAll(j, "no HeadMovement in the scene", failed: true);
                    return;
                }

                if (!j.loadRequested)
                {
                    if (!hm.LoadCapture(j.capture))
                    {
                        StopAll(j, $"HeadMovement has no capture '{j.capture}'", failed: true);
                        return;
                    }

                    j.loadRequested = true;
                    j.warmFrames = -1;
                    Save(j);
                    if (j.mode == "director" || j.plainMix) FilmSoundtrack.Preload(j.mix);
                    return;
                }

                if (j.warmFrames < 0)
                {
                    if (now < nextStatePoll) return;
                    nextStatePoll = now + 0.25;
                    if (!(hm.State().TryGetValue("audioLoaded", out object al) && al is true))
                    {
                        if (now - j.phaseStart > LoadTimeout) StopAll(j, $"{j.capture} did not finish loading in {LoadTimeout} s", failed: true);
                        return;
                    }

                    j.warmFrames = 0;
                    j.phaseStart = now;
                    if (j.mode == "director") hm.Stop(); // quiet until the film starts (the director owns playback)
                    SetGameViewSize(j);
                    Save(j);
                    return;
                }

                j.warmFrames++;
                if (j.warmFrames < WarmupFrames || now - j.phaseStart < WarmupSeconds) return;
                if (Screen.width != j.width || Screen.height != j.height)
                {
                    if (now - j.phaseStart > 20)
                    {
                        j.warnings.Add($"Game view is {Screen.width}x{Screen.height} before the start (wanted {j.width}x{j.height}); the Recorder sets it on start");
                    }
                    else
                    {
                        if (j.warmFrames % 30 == 0) SetGameViewSize(j);
                        return;
                    }
                }

                if (j.mode == "director" || j.plainMix)
                {
                    FilmSoundtrack st = FilmSoundtrack.Current;
                    if (st == null || st.Error != null)
                    {
                        StopAll(j, $"soundtrack: {st?.Error ?? "not loaded"}", failed: true);
                        return;
                    }

                    if (!st.IsLoaded)
                    {
                        if (now - j.phaseStart > 60) StopAll(j, "soundtrack did not load in 60 s", failed: true);
                        return;
                    }

                    MethodInfo prepare = j.mode != "director" ? null : DirectorType()?.GetMethod("Prepare",BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null);
                    if (prepare != null)
                    {
                        try
                        {
                            directorInstance = prepare.Invoke(null, new object[] { j.direction, j.aspect }) as Object;
                        }
                        catch (Exception e)
                        {
                            StopAll(j, $"FilmDirector.Prepare threw: {(e.InnerException ?? e).Message}", failed: true);
                            return;
                        }

                        j.prepareCalled = true;
                        if (directorInstance == null)
                        {
                            StopAll(j, $"FilmDirector.Prepare returned null ({DirectorString("LastError") ?? "see the Console"})", failed: true);
                            return;
                        }
                    }
                }

                SetState(j, "preparing", j.prepareCalled ? "waiting for FilmDirector.IsReady" : "starting");
                return;

            case "preparing":
                if (j.mode == "director" && j.prepareCalled && !DirectorBool("IsReady", true))
                {
                    if (now - j.phaseStart < PrepareTimeout) return;
                    j.warnings.Add($"FilmDirector.IsReady still false after {PrepareTimeout} s - started anyway");
                }

                StartAll(j);
                return;

            case "arming":
                BeginFilm(j);
                return;

            case "recording":
                RecordingTick(j, now);
                return;

            case "stopping":
                if (j.stopFrame < 0) j.stopFrame = Time.frameCount;
                if (Time.frameCount - j.stopFrame > 2 && now - j.phaseStart > 0.5)
                {
                    FilmRecordingDriver.Instance.OnFrame = null;
                    EditorApplication.ExitPlaymode();
                }

                return;
        }
    }

    static void SetGameViewSize(Job j)
    {
        // UnityEditor.Recorder.Input.GameViewSize is internal; the Recorder calls the same on start - doing it during
        // the warm-up lets the layout (director captions, safe areas) settle before frame 0
        try
        {
            Type t = typeof(GameViewInputSettings).Assembly.GetType("UnityEditor.Recorder.Input.GameViewSize");
            t?.GetMethod("SwapMainPlayViewToGameView", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
            t?.GetMethod("SetCustomSize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(int), typeof(int) }, null)
                ?.Invoke(null, new object[] { j.width, j.height });
            j.gameViewSet = t != null;
        }
        catch (Exception e)
        {
            j.warnings.Add($"could not pre-size the Game view: {e.Message}");
        }
    }

    /// <summary>frame S: start the Movie Recorder and the soundtrack (the film itself begins on S+1, see BeginFilm)</summary>
    static void StartAll(Job j)
    {
        j.screenW = Screen.width;
        j.screenH = Screen.height;

        RecorderControllerSettings settings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
        MovieRecorderSettings movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
        movie.name = "HM Film";
        movie.Enabled = true;
        // H.264 High profile at an explicit bitrate (the preset "High" quality gives Constrained Baseline at ~8 Mbps on
        // Windows, too little for hair, thin skeleton lines and caption text): 1 s GOP, 2 B-frames
        j.videoMbps = (j.size == "4k" ? 60f : 20f) * (j.fps > 30 ? 1.5f : 1f);
        movie.EncoderSettings = new CoreEncoderSettings
        {
            Codec = CoreEncoderSettings.OutputCodec.MP4,
            EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.Custom,
            EncodingProfile = CoreEncoderSettings.H264EncodingProfile.High,
            TargetBitRate = j.videoMbps,
            GopSize = (uint)j.fps,
            NumConsecutiveBFrames = 2
        };
        movie.CaptureAlpha = false;
        movie.CaptureAudio = true;
        movie.ImageInputSettings = new GameViewInputSettings { OutputWidth = j.width, OutputHeight = j.height };
        movie.OutputFile = Path.Combine(Path.GetDirectoryName(j.output)!, Path.GetFileNameWithoutExtension(j.output)).Replace('\\', '/');
        settings.AddRecorderSettings(movie);
        settings.SetRecordModeToManual();
        settings.FrameRatePlayback = FrameRatePlayback.Constant;
        settings.FrameRate = j.fps;
        settings.CapFrameRate = true;
        settings.ExitPlayMode = false;
        controller = new RecorderController(settings);
        try
        {
            controller.PrepareRecording();
            if (!controller.StartRecording())
            {
                StopAll(j, "the Unity Recorder did not start (see the Console)", failed: true);
                return;
            }
        }
        catch (Exception e)
        {
            StopAll(j, $"the Unity Recorder failed to start: {e.Message}", failed: true);
            return;
        }

        // Frame S (this one): the Recorder starts but does NOT capture frame S - its first video frame is S+1 (measured
        // with the marker self-test, hm_film --plain true --mix true). The soundtrack starts here at the film start: the
        // offline AudioRenderer delivers it from sample 0 whichever way it counts frame S. The director begins on S+1
        // (BeginFilm), so video frame 0 = film start = audio sample 0.
        j.armFrame = Time.frameCount;
        j.recordStartReal = EditorApplication.timeSinceStartup;
        j.frames = 0;
        if (j.mode == "director" || j.plainMix)
        {
            float t0 = j.start;
            // the clock is read once (StartNow); while Recording the player never re-syncs (AudioRenderer keeps it locked)
            FilmSoundtrack st = FilmSoundtrack.Play(j.mix, () => t0);
            st.Recording = true;
            j.soundtrack = st.Describe();
            j.soundtrackStartedAt = st.StartedAt;
            j.soundtrackStartFrame = st.StartFrame;
            if (!st.IsPlaying) j.warnings.Add($"the soundtrack did not start on the arm frame ({st.Describe()})");
        }

        SetState(j, "arming", "recorder started; the film begins on the next frame");
    }

    /// <summary>frame S+1: the first frame the Recorder captures - the director (or the plain playback) starts here</summary>
    static void BeginFilm(Job j)
    {
        HeadMovement hm = HeadMovement.Instance;
        j.startFrame = Time.frameCount;
        j.recorderFramesAtBegin = RecordedFrames();
        if (j.recorderFramesAtBegin > 0)
            j.warnings.Add($"the Recorder captured {j.recorderFramesAtBegin} frame(s) before the film began: the video starts with " +
                           "pre-roll and the picture is LATE vs. the audio by that many frames");

        if (j.mode == "director")
        {
            Type t = DirectorType();
            MethodInfo begin3 = t.GetMethod("Begin", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string), typeof(float) }, null);
            MethodInfo begin2 = t.GetMethod("Begin", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null);
            try
            {
                if (begin3 != null && (j.start > 0 || begin2 == null))
                {
                    directorInstance = begin3.Invoke(null, new object[] { j.direction, j.aspect, j.start }) as Object;
                    j.begin3 = true;
                }
                else if (begin2 != null)
                {
                    directorInstance = begin2.Invoke(null, new object[] { j.direction, j.aspect }) as Object;
                }
                else
                {
                    StopAll(j, "FilmDirector has no static Begin(string, string[, float])", failed: true);
                    return;
                }
            }
            catch (Exception e)
            {
                StopAll(j, $"FilmDirector.Begin threw: {(e.InnerException ?? e).Message}", failed: true);
                return;
            }

            if (directorInstance == null) directorInstance = StaticInstance();
            if (directorInstance == null)
            {
                StopAll(j, $"FilmDirector.Begin returned null ({DirectorString("LastError") ?? "see the Console"})", failed: true);
                return;
            }
        }
        else
        {
            hm.Restart();
            hm.Play();
            if (j.plainMix)
            {
                foreach (AudioSource s in hm.GetComponents<AudioSource>()) s.mute = true;
                // white frames 1 s and 4 s into the film: the render check must find them at video frames fps and 4 fps
                // (video frame 0 = this frame = the soundtrack's film start), else picture and sound disagree
                j.flashAt = new[] { j.fps, 4 * j.fps };
                int f0 = j.startFrame;
                FilmRecordingDriver.Instance.FlashFrames = j.flashAt.Select(k => f0 + k).ToArray();
            }
        }

        j.lastFilmTime = float.NaN;
        SetState(j, "recording", $"{j.capture} {j.preset}: film {j.start:0.##}-{j.filmEnd:0.##} s, {j.expectedFrames} frames -> {Path.GetFileName(j.output)}");
    }

    /// <summary>frames the Movie Recorder has captured so far (internal counter, read by reflection; -1 if unavailable)</summary>
    static int RecordedFrames()
    {
        try
        {
            if (controller == null) return -1;
            if (typeof(RecorderController).GetField("m_RecordingSessions", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(controller)
                is not System.Collections.IEnumerable sessions) return -1;
            foreach (object session in sessions)
            {
                object rec = session?.GetType().GetField("recorder", BindingFlags.Public | BindingFlags.Instance)?.GetValue(session);
                PropertyInfo p = rec?.GetType().GetProperty("RecordedFramesCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) return Convert.ToInt32(p.GetValue(rec));
            }
        }
        catch
        {
            // Recorder internals changed: the frame math still works
        }

        return -1;
    }

    static void RecordingTick(Job j, double now)
    {
        j.frames = Time.frameCount - j.startFrame; // frames completed since the start frame
        string why = null;
        bool failed = false;
        if (controller == null || !controller.IsRecording())
        {
            why = "the Unity Recorder stopped on its own";
            failed = true;
        }
        else if (j.mode == "director")
        {
            if (directorInstance == null)
            {
                why = "the FilmDirector was destroyed";
                failed = true;
            }
            else
            {
                float ft = DirectorFloat("FilmTime", float.NaN);
                j.filmTime = ft;
                j.danceTime = DirectorFloat("DanceTime", float.NaN);
                j.segment = DirectorString("SegmentId");
                string err = DirectorString("LastError");
                if (!string.IsNullOrEmpty(err) && err != j.directorError)
                {
                    j.directorError = err;
                    j.warnings.Add($"director (film {ft:0.00} s): {err}");
                }

                if (!float.IsNaN(ft) && !float.IsNaN(j.lastFilmTime) && ft <= j.lastFilmTime) j.stallFrames++;
                else j.stallFrames = 0;
                j.lastFilmTime = ft;
                if (DirectorBool("IsFinished", false)) why = "the director finished";
                else if (!float.IsNaN(ft) && ft >= j.filmDuration + 0.5f) why = "film time passed the film's end";
                else if (j.duration > 0 && j.frames >= j.expectedFrames) why = "requested duration recorded";
                else if (j.stallFrames > 150)
                {
                    why = $"the director's film clock stalled at {ft:0.000} s";
                    failed = true;
                }
            }
        }
        else
        {
            if (j.plainMix) j.filmTime = j.start + j.frames / (float)j.fps;
            if (j.frames >= j.expectedFrames) why = "plain test duration recorded";
        }

        double maxReal = Math.Max(900, j.expectedFrames * 3.0);
        if (why == null && now - j.recordStartReal > maxReal)
        {
            why = $"watchdog: {maxReal:0} s real time";
            failed = true;
        }

        if (why != null)
        {
            StopAll(j, why, aborted: failed);
            return;
        }

        if (j.frames % 15 == 0)
        {
            j.realElapsed = now - j.recordStartReal;
            if (j.frames == 15) CheckSongSilent(j);
            Save(j);
        }
    }

    /// <summary>INTERFACE.md: the director keeps HeadMovement's own song silent in film mode (the soundtrack carries it)</summary>
    static void CheckSongSilent(Job j)
    {
        if (j.mode != "director" || HeadMovement.Instance == null) return;
        foreach (AudioSource s in HeadMovement.Instance.GetComponents<AudioSource>())
        {
            if (s.isPlaying && !s.mute && s.volume > 0f)
            {
                s.mute = true;
                j.warnings.Add("HeadMovement's song AudioSource was audible during the film - muted by the recorder (the director should keep it silent)");
            }
        }
    }

    static void StopAll(Job j, string why, bool failed = false, bool aborted = false)
    {
        StopRecorder(j);
        j.stopReason = why;
        if (FilmRecordingDriver.Instance != null) FilmRecordingDriver.Instance.FlashFrames = null;
        j.recordEndReal = EditorApplication.timeSinceStartup;
        if (j.recordStartReal > 0) j.realElapsed = j.recordEndReal - j.recordStartReal;
        FilmSoundtrack.StopAll();
        if (directorInstance != null)
        {
            try
            {
                directorInstance.GetType().GetMethod("Stop", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(directorInstance, null);
            }
            catch (Exception e)
            {
                j.warnings.Add($"FilmDirector.Stop threw: {(e.InnerException ?? e).Message}");
            }
        }

        directorInstance = null;
        if (failed && !File.Exists(j.output))
        {
            j.finished = Now();
            SetState(j, "failed", why);
            if (FilmRecordingDriver.Instance != null) FilmRecordingDriver.Instance.OnFrame = null;
            EditorApplication.ExitPlaymode();
            return;
        }

        j.aborted |= failed || aborted;
        j.stopFrame = Time.frameCount;
        SetState(j, "stopping", why);
    }

    static void StopRecorder(Job j)
    {
        if (controller == null) return;
        j.recorderFrames = RecordedFrames();
        try
        {
            if (controller.IsRecording()) controller.StopRecording();
        }
        catch (Exception e)
        {
            j.warnings.Add($"StopRecording threw: {e.Message}");
        }

        controller = null;
    }

    static Object StaticInstance()
    {
        Type t = DirectorType();
        if (t == null) return null;
        PropertyInfo p = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        if (p != null) return p.GetValue(null) as Object;
        FieldInfo f = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
        return f?.GetValue(null) as Object;
    }

    static object DirectorMember(string name)
    {
        Object inst = directorInstance != null ? directorInstance : StaticInstance();
        if (inst == null) return null;
        Type t = inst.GetType();
        PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p != null) return p.GetValue(inst);
        FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        return f?.GetValue(inst);
    }

    static bool DirectorBool(string name, bool fallback) => DirectorMember(name) is bool b ? b : fallback;

    static float DirectorFloat(string name, float fallback)
    {
        object v = DirectorMember(name);
        return v is float f ? f : v is double d ? (float)d : fallback;
    }

    static string DirectorString(string name) => DirectorMember(name) as string;

    // ------------------------------------------------------------------ validation + report

    static void BeginValidation(Job j)
    {
        j.finished = Now();
        SetState(j, "validating", $"checking {Path.GetFileName(j.output)}");
    }

    static void OnValidated(Job j, string reportPath, bool pass)
    {
        j.report = reportPath;
        j.finished = Now();
        string state = j.aborted ? "aborted" : pass ? "done" : "failed";
        SetState(j, state, $"{(pass ? "PASS" : "CHECK FAILED")}: {Path.GetFileName(j.output)} (report {Path.GetFileName(reportPath)})");
    }
}
