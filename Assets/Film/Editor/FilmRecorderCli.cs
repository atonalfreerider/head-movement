using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

/// <summary>
/// CLI for the film recorder (FilmRecorder; works from Edit mode - it enters and leaves Play mode itself):
///
///   unity command hm_film --action record --aspect vertical --capture <capture folder>       # the whole film
///   unity command hm_film --action record --aspect horizontal --size 4k
///   unity command hm_film --action record --aspect vertical --start 30 --duration 12          # a test excerpt
///   unity command hm_film --action record --plain true --mix true --start 40 --duration 8   # pipeline test without a
///                                               # director: capture playback under the film soundtrack + A/V sync check
///   unity command hm_film --action status       # poll until state is done | failed | aborted (Play mode entry
///                                               # reloads the domain: the CLI server is away for about a minute)
///   unity command hm_film --action stop
///   unity command hm_film --action list         # captures with a film, soundtrack state, tools
/// </summary>
public static class FilmRecorderCli
{
    [CliCommand("hm_film", "Film recording (VIEWER_SPEC 8, Unity Recorder): record | status | stop | list. record --aspect vertical|horizontal [--capture <folder>] [--size hd|4k] [--start s --duration s] [--plain true [--mix true]]; then poll status")]
    public static string Film(
        [CliArg("action", "record | status | stop | list")] string action = "status",
        [CliArg("aspect", "vertical (1080x1920) | horizontal (1920x1080)")] string aspect = "vertical",
        [CliArg("capture", "capture folder in StreamingAssets (default: the newest capture that has a film direction)")] string capture = null,
        [CliArg("size", "hd (default) | 4k (2160x3840 / 3840x2160)")] string size = "hd",
        [CliArg("start", "film seconds to start at (test excerpts; needs FilmDirector.Begin(path, aspect, start))")] float start = 0f,
        [CliArg("duration", "film seconds to record (0 = to the end of the film; plain test default 10)")] float duration = 0f,
        [CliArg("fps", "30 (default) | 60")] int fps = 30,
        [CliArg("plain", "true = record HeadMovement's own playback (pipeline test) even when a director exists")] bool plain = false,
        [CliArg("mix", "plain test only: true = play the film soundtrack (from --start, frame clock) instead of the capture's song; checks audio capture + A/V sync")] bool mix = false)
    {
        switch ((action ?? "status").Trim().ToLowerInvariant())
        {
            case "record":
            {
                string err = FilmRecorder.Queue(new FilmRecorder.Options
                {
                    aspect = aspect, size = size, capture = capture, start = start, duration = duration, fps = fps, plain = plain, mix = mix, source = "cli"
                }, false);
                if (err != null) throw new InvalidOperationException(err);
                return Status();
            }
            case "status":
                return Status();
            case "stop":
                return JsonConvert.SerializeObject(new { result = FilmRecorder.Stop("stopped from the CLI"), status = JObject.Parse(Status()) });
            case "list":
                return List();
            default:
                throw new ArgumentException($"unknown action '{action}' (record | status | stop | list)");
        }
    }

    public static string Status()
    {
        FilmRecorder.Job j = FilmRecorder.Current;
        if (j == null) return JsonConvert.SerializeObject(new { state = "idle" });
        JObject o = JObject.FromObject(j);
        if (j.expectedFrames > 0)
        {
            o["progress"] = Math.Round(Math.Min(1.0, j.frames / (double)j.expectedFrames), 4);
            if (j.state == "recording" && j.realElapsed > 1 && j.frames > 0)
            {
                double rate = j.frames / j.realElapsed;
                o["render_fps"] = Math.Round(rate, 2);
                o["eta_s"] = Math.Round(Math.Max(0, j.expectedFrames - j.frames) / rate, 0);
            }
        }

        if (j.report != null && File.Exists(j.report))
        {
            try
            {
                JObject r = JObject.Parse(File.ReadAllText(j.report));
                o["report_pass"] = r["pass"];
                o["report_checks"] = new JArray(((JArray)r["checks"] ?? new JArray()).Select(c =>
                    $"{c["name"]}: {((bool?)c["pass"] == true ? "pass" : "FAIL")} - {c["detail"]}"));
            }
            catch (Exception e)
            {
                o["report_error"] = e.Message;
            }
        }

        o["playMode"] = EditorApplication.isPlaying;
        return o.ToString(Formatting.None);
    }

    static string List()
    {
        List<object> films = new();
        foreach (string c in FilmRecorder.CaptureNames())
        {
            string d = FilmRecorder.FindDirection(c);
            if (d == null) continue;
            string name = null;
            try
            {
                name = JObject.Parse(File.ReadAllText(d)).Value<string>("name");
            }
            catch
            {
                // unreadable
            }

            string mix = name != null ? Path.Combine(Path.GetDirectoryName(d)!, "audio", $"{name}_mix.wav") : null;
            films.Add(new
            {
                capture = c, direction = d, name, mix, mixExists = mix != null && File.Exists(mix),
                mixTime = mix != null && File.Exists(mix) ? File.GetLastWriteTime(mix).ToString("yyyy-MM-dd HH:mm:ss") : null
            });
        }

        return JsonConvert.SerializeObject(new
        {
            captures = FilmRecorder.CaptureNames(), films, defaultCapture = FilmRecorder.DefaultCapture(),
            selectedCapture = EditorPrefs.GetString(FilmRecorder.CapturePref, ""),
            director = FilmRecorder.DirectorType()?.FullName ?? "missing (plain pipeline test only)",
            ffprobe = FilmRenderCheck.Tool("ffprobe"), ffmpeg = FilmRenderCheck.Tool("ffmpeg"), python = FilmRecorder.Python(),
            recordings = FilmRecorder.RecordingsDir
        });
    }
}
