using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>one chapter of a film, in FILM seconds (the film clock, the playback bar's scale)</summary>
public class LibraryChapter
{
    public string Title;
    public float StartS, EndS;
    public float Duration => EndS - StartS;
}

/// <summary>
/// One film of the library (docs/FILM_LIBRARY.md): the card on the library screen and everything the playback bar needs.
/// Data only; the file is StreamingAssets/library/library.json (git-ignored: it may hold names and paths).
/// </summary>
public class LibraryEntry
{
    public string Id, Title, Subtitle, Capture, Thumbnail, Direction, ChaptersFile, Note;
    public float DurationS = float.NaN;
    public bool AvailableFlag = true;
    public readonly List<LibraryChapter> Chapters = new();

    // resolved by Probe() (absolute paths; null = not resolvable)
    public string ThumbnailPath, DirectionPath;
    public bool Ready;
    public string WhyNot;

    /// <summary>the film's length: the direction's own (once the film exists) is authoritative at play time</summary>
    public string DurationText => float.IsFinite(DurationS) && DurationS > 0f ? LibraryData.Clock(DurationS) : "";

    /// <summary>"Ready" when the capture folder, the film direction and the film's mix exist; otherwise the reason it does not
    /// (the card shows "Processing" with a disabled Play button and flips by itself when the files appear)</summary>
    public void Probe()
    {
        DirectionPath = LibraryData.Resolve(Direction);
        ThumbnailPath = LibraryData.Resolve(Thumbnail);
        WhyNot = null;
        if (!AvailableFlag) WhyNot = "not released yet";
        else if (string.IsNullOrEmpty(Capture)) WhyNot = "no capture named";
        else if (!File.Exists(Path.Combine(Application.streamingAssetsPath, Capture, "capture.json"))) WhyNot = $"capture {Capture} is not exported yet";
        else if (DirectionPath == null || !File.Exists(DirectionPath)) WhyNot = "the film is not built yet";
        else if (!HasMix(DirectionPath)) WhyNot = "the film's soundtrack is not mixed yet";
        Ready = WhyNot == null;
    }

    static bool HasMix(string directionPath)
    {
        try
        {
            string audio = Path.Combine(Path.GetDirectoryName(directionPath) ?? "", "audio");
            return Directory.Exists(audio) && Directory.GetFiles(audio, "*_mix.wav").Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>chapters sorted, with the last one ending at the film's end</summary>
    public void NormaliseChapters(float filmDuration)
    {
        Chapters.Sort((a, b) => a.StartS.CompareTo(b.StartS));
        float end = float.IsFinite(filmDuration) && filmDuration > 0f ? filmDuration : float.IsFinite(DurationS) ? DurationS : float.NaN;
        for (int i = 0; i < Chapters.Count; i++)
        {
            // chapters are contiguous: each ends where the next begins, the last at the film's end (an explicit endS is kept only
            // for the last one when the film's length is not known yet)
            float next = i + 1 < Chapters.Count ? Chapters[i + 1].StartS : end;
            if (float.IsFinite(next)) Chapters[i].EndS = next;
            else if (!float.IsFinite(Chapters[i].EndS)) Chapters[i].EndS = Chapters[i].StartS;
        }
    }

    public int ChapterIndexAt(float t)
    {
        int idx = -1;
        for (int i = 0; i < Chapters.Count; i++)
        {
            if (Chapters[i].StartS <= t + 1e-3f) idx = i;
        }

        return idx;
    }
}

/// <summary>StreamingAssets/library/library.json: {"version": 1, "films": [ {id, title, subtitle, capture, thumbnail, direction,
/// durationS, chapters | chaptersFile, available} ]}. Missing fields are tolerated; a file that does not parse leaves the list
/// empty and says why in <see cref="Error"/> (the viewer then shows that, and still hides every other capture).</summary>
public class LibraryData
{
    public const string FolderName = "library";
    public const int SupportedVersion = 1;

    public string Path { get; private set; }
    public DateTime Stamp { get; private set; }
    public string Error { get; private set; }
    public int Version { get; private set; }
    public readonly List<LibraryEntry> Films = new();

    public static string Dir => System.IO.Path.Combine(Application.streamingAssetsPath, FolderName);
    public static string FilePath => System.IO.Path.Combine(Dir, "library.json");
    public static string ProjectRoot => Directory.GetParent(Application.dataPath)!.FullName;
    public static string WorkspaceRoot => Directory.GetParent(ProjectRoot)!.FullName;

    public static bool Exists => File.Exists(FilePath);

    public static DateTime StampOf() => File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;

    public static LibraryData Load()
    {
        LibraryData d = new() { Path = FilePath, Stamp = StampOf() };
        try
        {
            if (!File.Exists(FilePath))
            {
                d.Error = "library.json not found";
                return d;
            }

            JObject root = JObject.Parse(File.ReadAllText(FilePath));
            d.Version = root.Value<int?>("version") ?? 1;
            if (d.Version > SupportedVersion) d.Error = $"library.json version {d.Version} is newer than this viewer reads ({SupportedVersion})";
            JArray films = root["films"] as JArray ?? root["entries"] as JArray;
            if (films == null)
            {
                d.Error ??= "library.json has no \"films\" list";
                return d;
            }

            foreach (JToken t in films)
            {
                if (t is not JObject o) continue;
                LibraryEntry e = new()
                {
                    Id = o.Value<string>("id"), Title = o.Value<string>("title"), Subtitle = o.Value<string>("subtitle"),
                    Capture = o.Value<string>("capture"), Thumbnail = o.Value<string>("thumbnail"), Direction = o.Value<string>("direction"),
                    ChaptersFile = o.Value<string>("chaptersFile"), Note = o.Value<string>("note")
                };
                if (string.IsNullOrEmpty(e.Id)) e.Id = e.Capture ?? $"film{d.Films.Count + 1}";
                if (string.IsNullOrEmpty(e.Title)) e.Title = e.Id;
                if (o["durationS"] is JToken dur && (dur.Type == JTokenType.Float || dur.Type == JTokenType.Integer)) e.DurationS = dur.Value<float>();
                if (o["available"] is JToken av && av.Type == JTokenType.Boolean) e.AvailableFlag = av.Value<bool>();
                ReadChapters(o["chapters"] as JArray, e);
                if (e.Chapters.Count == 0 && !string.IsNullOrEmpty(e.ChaptersFile)) ReadChaptersFile(e);
                e.NormaliseChapters(e.DurationS);
                e.Probe();
                d.Films.Add(e);
            }
        }
        catch (Exception ex)
        {
            d.Films.Clear();
            d.Error = $"library.json unreadable: {ex.Message}";
        }

        return d;
    }

    static void ReadChapters(JArray arr, LibraryEntry e)
    {
        if (arr == null) return;
        foreach (JToken c in arr)
        {
            if (c is not JObject co) continue;
            float start = Num(co["startS"], float.NaN);
            if (!float.IsFinite(start)) continue;
            e.Chapters.Add(new LibraryChapter { Title = co.Value<string>("title") ?? "", StartS = start, EndS = Num(co["endS"], float.NaN) });
        }
    }

    static void ReadChaptersFile(LibraryEntry e)
    {
        string p = Resolve(e.ChaptersFile);
        if (p == null || !File.Exists(p)) return;
        try
        {
            JToken root = JToken.Parse(File.ReadAllText(p));
            ReadChapters(root as JArray ?? (root as JObject)?["chapters"] as JArray, e);
        }
        catch (Exception)
        {
            // a bad chapters file: no chapters (the bar still seeks)
        }
    }

    static float Num(JToken t, float fallback) => t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? t.Value<float>() : fallback;

    /// <summary>an absolute path stays; a relative one is looked up beside library.json, then in the project, then in the workspace
    /// (the folder that holds the project); null for an empty path</summary>
    public static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string p = path.Trim().Replace('\\', '/');
        if (System.IO.Path.IsPathRooted(p)) return System.IO.Path.GetFullPath(p);
        foreach (string root in new[] { Dir, ProjectRoot, WorkspaceRoot })
        {
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, p));
            if (File.Exists(full) || Directory.Exists(full)) return full;
        }

        return System.IO.Path.GetFullPath(System.IO.Path.Combine(Dir, p));
    }

    /// <summary>m:ss (h:mm:ss from an hour)</summary>
    public static string Clock(float seconds)
    {
        if (!float.IsFinite(seconds)) return "-:--";
        int s = Mathf.Max(0, Mathf.FloorToInt(seconds + 1e-4f));
        return s >= 3600
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", s / 3600, s / 60 % 60, s % 60)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", s / 60, s % 60);
    }

    // ------------------------------------------------------------------ the capture pickers

    static LibraryData pickerCache;

    /// <summary>the capture folders the viewer's own pickers (the HUD list, the digit keys) may offer: the library's films that
    /// are playable. null = there is no library file at all (a plain checkout: every capture, as before). Developer commands
    /// (hm_load, hm_film_show) never go through this.</summary>
    public static string[] PickerCaptureNames()
    {
        if (!Exists) return null;
        DateTime stamp = StampOf();
        if (pickerCache == null || pickerCache.Stamp != stamp) pickerCache = Load();
        return pickerCache.Films.Where(f => !string.IsNullOrEmpty(f.Capture) && Directory.Exists(System.IO.Path.Combine(Application.streamingAssetsPath, f.Capture)))
            .Select(f => f.Capture).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
