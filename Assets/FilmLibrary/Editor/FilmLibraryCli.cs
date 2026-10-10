using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

/// <summary>
/// CLI for the film library screen and the playback bar (Play mode; Tools/playtest_library.ps1 drives it):
///
///   unity command hm_library --action state                      # library + bar + film + audio state (JSON)
///   unity command hm_library --action show | hide | back
///   unity command hm_library --action play --id <film id or capture folder>
///   unity command hm_library --action seek --at 93.7             # through the bar (the same call as a click on the scrubber)
///   unity command hm_library --action pause | resume | toggle
///   unity command hm_library --action chapter --index 3 | next | prev
///   unity command hm_library --action rate --value 1.5
///   unity command hm_library --action bar --flag true            # keep the bar on screen (screenshots)
///
/// The pointer and keys of a real session are simulated with the editor tools simulate_pointer / simulate_key; this command
/// reads the geometry they need (the cards, the scrubber track and its chapter ticks, the buttons) from the state.
/// </summary>
public static class FilmLibraryCli
{
    [CliCommand("hm_library", "Film library screen + playback bar (Play mode): state | show | hide | back | play --id | seek --at | pause | resume | toggle | chapter --index | next | prev | rate --value | bar --flag | focus")]
    public static string Library(
        [CliArg("action", "state | show | hide | back | play | seek | pause | resume | toggle | chapter | next | prev | rate | bar | focus")] string action = "state",
        [CliArg("id", "play: a film id or capture folder of the library")] string id = null,
        [CliArg("at", "seek: film seconds")] float at = 0f,
        [CliArg("index", "chapter: 0-based chapter index")] int index = 0,
        [CliArg("value", "rate: 0.5 | 1 | 1.5 | 2")] float value = 1f,
        [CliArg("flag", "bar: true = keep the bar visible")] bool flag = true)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("enter Play mode first (unity command editor_play)");
        string a = (action ?? "state").Trim().ToLowerInvariant();
        FilmLibrary lib = FilmLibrary.Instance;
        FilmPlaybackBar bar = FilmPlaybackBar.Instance;
        switch (a)
        {
            case "state":
                break;
            case "show":
                Need(lib).Show();
                break;
            case "hide":
                Need(lib).Hide();
                break;
            case "back":
                if (bar != null) bar.Back();
                else Need(lib).Back();
                break;
            case "play":
                if (!Need(lib).Play(id)) throw new InvalidOperationException(lib.Error ?? $"could not play '{id}'");
                break;
            case "seek":
                Bar(bar).SeekTo(at);
                break;
            case "pause":
                Bar(bar).Director.SetPaused(true);
                break;
            case "resume":
                Bar(bar).Director.SetPaused(false);
                break;
            case "toggle":
                Bar(bar).TogglePlay();
                break;
            case "chapter":
            {
                FilmPlaybackBar b = Bar(bar);
                if (index < 0 || index >= b.Chapters.Count) throw new ArgumentException($"chapter {index} of {b.Chapters.Count}");
                b.SeekTo(b.Chapters[index].StartS);
                break;
            }
            case "next":
                Bar(bar).NextChapter();
                break;
            case "prev":
                Bar(bar).PreviousChapter();
                break;
            case "rate":
            {
                FilmPlaybackBar b = Bar(bar);
                for (int i = 0; i < 4 && !Mathf.Approximately(b.Director.Rate, value); i++) b.CycleRate();
                break;
            }
            case "bar":
                FilmPlaybackBar.ForceVisible = flag;
                break;
            case "focus":
                // the editor hands keyboard and pointer events to the game only while the Game view has the focus (the Input System's
                // PointersAndKeyboardsRespectGameViewFocus): the playtest calls this before it simulates input
                EditorWindow.FocusWindowIfItsOpen(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
                break;
            default:
                throw new ArgumentException($"unknown action '{action}'");
        }

        return State();
    }

    static FilmLibrary Need(FilmLibrary l) => l != null ? l : throw new InvalidOperationException("no film library in this session (StreamingAssets/library/library.json missing?)");

    static FilmPlaybackBar Bar(FilmPlaybackBar b) => b != null ? b : throw new InvalidOperationException("no film is playing with the bar (hm_library --action play --id <film>)");

    public static string State()
    {
        FilmLibrary lib = FilmLibrary.Instance;
        FilmPlaybackBar bar = FilmPlaybackBar.Instance;
        FilmDirector d = FilmDirector.Instance;
        Dictionary<string, object> s = new()
        {
            ["library"] = lib != null ? lib.StateDict() : new Dictionary<string, object> { ["mode"] = "none" },
            ["bar"] = bar != null ? bar.StateDict() : null,
            ["recording"] = FilmLibrary.RecordingActive,
            ["libraryOwnsScreen"] = HeadMovement.LibraryOwnsScreen
        };
        if (d != null)
        {
            Dictionary<string, object> f = d.State();
            Dictionary<string, object> pick = new();
            foreach (string k in new[]
                     {
                         "name", "capture", "aspect", "screen", "ready", "running", "paused", "finished", "filmTime", "filmDuration", "danceTime", "speed",
                         "rate", "scrubbing", "segment", "viewState", "cameraMode", "pov", "povVideo", "videoShowing", "audio", "avatar", "skeleton",
                         "frame", "error", "overlay", "camera"
                     })
            {
                if (f.TryGetValue(k, out object v)) pick[k] = v;
            }

            s["film"] = pick;
        }

        return JsonConvert.SerializeObject(s, Formatting.None, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
    }
}
