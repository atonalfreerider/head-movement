using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.Recorder.Input;
using UnityEngine;

/// <summary>
/// Film director on the desktop, without recording (Play mode):
///
///   unity command hm_film_show --action start --aspect vertical [--capture <capture folder>] [--at 86.4] [--audio true]
///   unity command hm_film_show --action seek --at 93.7 [--pause true]
///   unity command hm_film_show --action pause | resume | stop | state
///   unity command hm_film_show --action size --aspect vertical          # the Game view at the preset (1080x1920)
///   unity command hm_film_show --action shots --aspect vertical --times 93.7,46.5 --out C:/.../review [--prefix v]
///                                           # paused stills at film times (PNG, the Game view at the preset size)
///   then poll --action state until shots.pending == 0
///
/// The menu "Head Movement/Film Preview Vertical | Horizontal" enters Play mode and plays the newest film with its
/// soundtrack (Head Movement/Record ... renders it with the Unity Recorder).
/// </summary>
[InitializeOnLoad]
public static class FilmDirectorCli
{
    const string PreviewKey = "HM.Film.Preview";

    static FilmDirectorCli()
    {
        EditorApplication.playModeStateChanged -= OnPlayMode;
        EditorApplication.playModeStateChanged += OnPlayMode;
    }

    [CliCommand("hm_film_show", "Film director preview (Play mode, no recording): start | seek | pause | resume | stop | state | size | shots. --aspect vertical|horizontal --capture <folder> --at <film s> --times a,b,c --out <dir>")]
    public static string Show(
        [CliArg("action", "start | seek | pause | resume | stop | state | size | shots")] string action = "state",
        [CliArg("aspect", "vertical | horizontal")] string aspect = "vertical",
        [CliArg("capture", "capture folder (default: the newest capture with a film direction)")] string capture = null,
        [CliArg("direction", "direction json path (default: found for the capture)")] string direction = null,
        [CliArg("at", "film seconds")] float at = 0f,
        [CliArg("pause", "true = hold the frame")] bool pause = false,
        [CliArg("audio", "start: play the film soundtrack")] bool audio = false,
        [CliArg("times", "shots: comma-separated film seconds")] string times = null,
        [CliArg("out", "shots: output folder")] string @out = null,
        [CliArg("prefix", "shots: file name prefix")] string prefix = null,
        [CliArg("settle", "shots: frames to wait before each still (default 8)")] int settle = 8)
    {
        string a = (action ?? "state").Trim().ToLowerInvariant();
        if (a == "size")
        {
            SetGameViewSize(aspect);
            return JsonConvert.SerializeObject(new { size = $"{Screen.width}x{Screen.height}", requested = Preset(aspect) });
        }

        if (!EditorApplication.isPlaying) throw new InvalidOperationException("enter Play mode first (unity command editor_play)");
        FilmDirector d = FilmDirector.Instance;
        switch (a)
        {
            case "start":
            {
                string path = direction ?? FindDirection(capture);
                if (path == null) throw new InvalidOperationException($"no film direction for '{capture ?? "(newest)"}'");
                SetGameViewSize(aspect);
                d = audio ? FilmDirector.Show(path, aspect, at) : FilmDirector.Begin(path, aspect, at);
                if (d == null) throw new InvalidOperationException($"FilmDirector failed: {FilmDirector.LastStaticError}");
                if (pause) d.SetPaused(true);
                break;
            }
            case "seek":
                Need(d).Seek(at);
                if (pause) d.SetPaused(true);
                break;
            case "pause":
                Need(d).SetPaused(true);
                break;
            case "resume":
                Need(d).SetPaused(false);
                break;
            case "stop":
                Need(d).Stop();
                return JsonConvert.SerializeObject(new { stopped = true });
            case "shots":
            {
                if (string.IsNullOrEmpty(times)) throw new ArgumentException("--times a,b,c");
                if (d == null)
                {
                    string path = direction ?? FindDirection(capture);
                    if (path == null) throw new InvalidOperationException("no film direction");
                    d = FilmDirector.Begin(path, aspect, 0f);
                    if (d == null) throw new InvalidOperationException($"FilmDirector failed: {FilmDirector.LastStaticError}");
                }

                SetGameViewSize(aspect);
                string dir = @out ?? Path.Combine(FilmRecorder.ProjectRoot, "Recordings", "stills");
                Directory.CreateDirectory(dir);
                foreach (string t in times.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    float ft = float.Parse(t, CultureInfo.InvariantCulture);
                    string name = $"{prefix ?? FilmAspect.Normalise(aspect)}_{ft.ToString("000.00", CultureInfo.InvariantCulture)}.png";
                    Queue.Add(new Shot { Time = ft, Path = Path.Combine(dir, name), Aspect = FilmAspect.Normalise(aspect), Settle = Math.Max(2, settle) });
                }

                if (!pumping)
                {
                    EditorApplication.update += Pump;
                    pumping = true;
                }

                break;
            }
            case "state":
                break;
            default:
                throw new ArgumentException($"unknown action '{action}'");
        }

        return StateJson();
    }

    static FilmDirector Need(FilmDirector d) => d != null ? d : throw new InvalidOperationException("no film is playing (--action start)");

    static string StateJson()
    {
        FilmDirector d = FilmDirector.Instance;
        Dictionary<string, object> s = d != null ? d.State() : new Dictionary<string, object> { ["state"] = "idle", ["error"] = FilmDirector.LastStaticError };
        // the last few stills only (the full list used to be repeated in every state call: 10k+ tokens)
        s["shots"] = new Dictionary<string, object>
        {
            ["pending"] = Queue.Count, ["doneCount"] = Done.Count, ["done"] = Done.Skip(Math.Max(0, Done.Count - 6)).ToList(), ["current"] = current?.Path
        };
        return JsonConvert.SerializeObject(s, Formatting.None, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
    }

    // ------------------------------------------------------------------ stills

    class Shot
    {
        public float Time;
        public string Path, Aspect;
        public int Settle, Waited = -1;
        public bool Captured;
        public int CaptureFrame;
    }

    static readonly List<Shot> Queue = new();
    static readonly List<string> Done = new();
    static Shot current;
    static bool pumping;

    static void Pump()
    {
        if (!EditorApplication.isPlaying)
        {
            Queue.Clear();
            current = null;
            EditorApplication.update -= Pump;
            pumping = false;
            return;
        }

        FilmDirector d = FilmDirector.Instance;
        if (d == null) return;
        if (current == null)
        {
            if (Queue.Count == 0)
            {
                EditorApplication.update -= Pump;
                pumping = false;
                return;
            }

            current = Queue[0];
            Queue.RemoveAt(0);
            if (d.Aspect != current.Aspect) FilmDirector.Prepare(d.DirectionPath, current.Aspect);
            d.SetPaused(true);
            d.Seek(current.Time);
            current.Waited = Time.frameCount;
            return;
        }

        if (!d.IsReady) return;
        if (!current.Captured)
        {
            if (Time.frameCount - current.Waited < current.Settle) return;
            if (File.Exists(current.Path)) File.Delete(current.Path);
            ScreenCapture.CaptureScreenshot(current.Path);
            current.Captured = true;
            current.CaptureFrame = Time.frameCount;
            return;
        }

        if (!File.Exists(current.Path) && Time.frameCount - current.CaptureFrame < 120) return;
        Done.Add(File.Exists(current.Path) ? current.Path : $"FAILED {current.Path}");
        current = null;
    }

    // ------------------------------------------------------------------ helpers

    public static string FindDirection(string capture)
    {
        if (string.IsNullOrEmpty(capture)) capture = FilmRecorder.DefaultCapture();
        return string.IsNullOrEmpty(capture) ? null : FilmRecorder.FindDirection(capture);
    }

    static string Preset(string aspect) => FilmAspect.Normalise(aspect) == "vertical" ? "1080x1920" : "1920x1080";

    /// <summary>the Game view at the preset size (the Recorder's own helper, internal: reflection)</summary>
    public static void SetGameViewSize(string aspect)
    {
        bool v = FilmAspect.Normalise(aspect) == "vertical";
        int w = v ? 1080 : 1920, h = v ? 1920 : 1080;
        try
        {
            Type t = typeof(GameViewInputSettings).Assembly.GetType("UnityEditor.Recorder.Input.GameViewSize");
            t?.GetMethod("SwapMainPlayViewToGameView", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
            t?.GetMethod("SetCustomSize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(int), typeof(int) }, null)
                ?.Invoke(null, new object[] { w, h });
        }
        catch (Exception e)
        {
            Debug.LogWarning($"hm_film_show: could not size the Game view ({e.Message})");
        }
    }

    // ------------------------------------------------------------------ preview menu

    [MenuItem("Head Movement/Film Preview Vertical", false, 310)]
    static void PreviewVertical() => Preview("vertical");

    [MenuItem("Head Movement/Film Preview Horizontal", false, 311)]
    static void PreviewHorizontal() => Preview("horizontal");

    [MenuItem("Head Movement/Stop Film Preview", false, 312)]
    static void StopPreview()
    {
        if (FilmDirector.Instance != null) FilmDirector.Instance.Stop();
    }

    [MenuItem("Head Movement/Stop Film Preview", true)]
    static bool CanStop() => EditorApplication.isPlaying && FilmDirector.Instance != null;

    static void Preview(string aspect)
    {
        string path = FindDirection(EditorPrefs.GetString(FilmRecorder.CapturePref, ""));
        path ??= FindDirection(null);
        if (path == null)
        {
            EditorUtility.DisplayDialog("Head Movement - Film Preview", "No capture with a film direction (direction*.json) was found.", "OK");
            return;
        }

        SessionState.SetString(PreviewKey, $"{aspect}|{path}");
        if (EditorApplication.isPlaying)
        {
            StartPending();
            return;
        }

        if (!UnityEngine.SceneManagement.SceneManager.GetActiveScene().path.EndsWith("head-movement.unity", StringComparison.OrdinalIgnoreCase))
        {
            if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/head-movement.unity");
        }

        SetGameViewSize(aspect);
        EditorApplication.EnterPlaymode();
    }

    static void OnPlayMode(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.EnteredPlayMode && !string.IsNullOrEmpty(SessionState.GetString(PreviewKey, "")))
        {
            EditorApplication.update -= StartWhenReady;
            EditorApplication.update += StartWhenReady;
        }
        else if (change == PlayModeStateChange.ExitingPlayMode)
        {
            SessionState.EraseString(PreviewKey);
            EditorApplication.update -= StartWhenReady;
        }
    }

    static double waitStart = -1;

    static void StartWhenReady()
    {
        if (!EditorApplication.isPlaying) return;
        if (waitStart < 0) waitStart = EditorApplication.timeSinceStartup;
        if (HeadMovement.Instance == null)
        {
            if (EditorApplication.timeSinceStartup - waitStart > 60)
            {
                EditorApplication.update -= StartWhenReady;
                waitStart = -1;
                Debug.LogError("Film Preview: no HeadMovement in the scene");
            }

            return;
        }

        EditorApplication.update -= StartWhenReady;
        waitStart = -1;
        StartPending();
    }

    static void StartPending()
    {
        string req = SessionState.GetString(PreviewKey, "");
        SessionState.EraseString(PreviewKey);
        int bar = req.IndexOf('|');
        if (bar < 0) return;
        string aspect = req.Substring(0, bar), path = req.Substring(bar + 1);
        SetGameViewSize(aspect);
        if (FilmDirector.Show(path, aspect, 0f) == null) Debug.LogError($"Film Preview: {FilmDirector.LastStaticError}");
    }
}
