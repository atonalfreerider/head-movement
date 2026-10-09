using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// A film direction (dancecap work/&lt;take&gt;/film/direction*.json, written by film/build_direction.py) and its
/// narration timeline (film/narration/timeline*.json, the ElevenLabs lines with word timings): segments on the FILM
/// clock (seconds into the rendered video) with the DANCE time they show (reference seconds), speed / ramps / holds /
/// replays, the camera shot, view state and layers, 3D annotations, titles; and the caption chunks for both aspects.
/// Read-only data; FilmDirector plays it (Assets/Film, INTERFACE.md in the film folder).
/// </summary>
public class FilmDirection
{
    public string Path, Dir, Name, Title, Capture, Take, Created;
    public float FilmDuration;
    public Vector2 DanceRange;
    public JObject Root;
    public readonly List<FilmSegment> Segments = new();
    public readonly List<FilmAnnotation> Annotations = new();
    public readonly HashSet<string> SkippedAnnotations = new(); // pending (TBD / null / {placeholder}) annotation ids
    public readonly Dictionary<string, FilmAspect> Aspects = new();
    public FilmCaptionStyle CaptionStyle = new();
    public FilmNarration Narration;
    public readonly List<string> Warnings = new();

    public FilmAspect Aspect(string name) =>
        Aspects.TryGetValue(FilmAspect.Normalise(name), out FilmAspect a) ? a : Aspects.Values.FirstOrDefault() ?? new FilmAspect();

    public static FilmDirection Load(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        JObject root = JObject.Parse(File.ReadAllText(path));
        FilmDirection d = new()
        {
            Path = path, Dir = System.IO.Path.GetDirectoryName(path), Root = root, Name = root.Value<string>("name") ?? "film",
            Title = root.Value<string>("title") ?? "", Capture = root.Value<string>("capture"), Take = root.Value<string>("take"),
            Created = root.Value<string>("created")
        };
        if (root["dance_range"] is JArray dr && dr.Count == 2) d.DanceRange = new Vector2(F(dr[0]), F(dr[1]));

        foreach (string aspect in new[] { "vertical", "horizontal" })
        {
            FilmAspect a = new() { Name = aspect, Vertical = aspect == "vertical" };
            if (root["presets"]?[aspect] is JObject p)
            {
                a.Width = p.Value<int?>("width") ?? a.Width;
                a.Height = p.Value<int?>("height") ?? a.Height;
                a.Fps = p.Value<float?>("fps") ?? 30f;
                a.SafeTop = p["safe"]?.Value<float?>("top_frac") ?? a.SafeTop;
                a.SafeBottom = p["safe"]?.Value<float?>("bottom_frac") ?? a.SafeBottom;
            }
            else
            {
                (a.Width, a.Height) = aspect == "vertical" ? (1080, 1920) : (1920, 1080);
            }

            JObject c = root["captions"]?[aspect == "vertical" ? "vertical_9x16" : "horizontal_16x9"] as JObject;
            if (c != null)
            {
                a.CaptionCentreY = c.Value<float?>("block_centre_y_frac") ?? a.CaptionCentreY;
                a.CaptionBottomY = c.Value<float?>("block_bottom_y_frac") ?? a.CaptionBottomY;
                a.CaptionBaselineY = c.Value<float?>("baseline_y_frac") ?? a.CaptionBaselineY;
                a.CaptionMaxLines = c.Value<int?>("max_lines") ?? a.CaptionMaxLines;
                a.CaptionMaxChars = c.Value<int?>("max_chars_per_line") ?? a.CaptionMaxChars;
                a.CaptionFontPx = c.Value<float?>(a.Vertical ? "font_px_at_1080w" : "font_px_at_1080h") ?? a.CaptionFontPx;
                a.CaptionWidthFrac = c.Value<float?>("width_frac") ?? a.CaptionWidthFrac;
            }
            else if (!a.Vertical)
            {
                a.SafeTop = 0.05f;
                a.SafeBottom = 0.1f;
            }

            d.Aspects[aspect] = a;
        }

        if (root["captions"] is JObject caps)
        {
            FilmCaptionStyle s = d.CaptionStyle;
            if (caps["current_word"] is JObject cw)
            {
                s.Highlight = Hex(cw.Value<string>("highlight"), s.Highlight);
                s.HighlightText = Hex(cw.Value<string>("text"), s.HighlightText);
                if (cw["box_padding_px"] is JArray bp && bp.Count == 2) s.BoxPadding = new Vector2(F(bp[0]), F(bp[1]));
                s.CornerPx = cw.Value<float?>("corner_px") ?? s.CornerPx;
                s.TransitionMs = cw.Value<float?>("transition_ms") ?? s.TransitionMs;
            }

            if (caps["other_words"] is JObject ow)
            {
                s.Text = Hex(ow.Value<string>("text"), s.Text);
                s.OutlinePx = ow.Value<float?>("outline_px") ?? s.OutlinePx;
                s.Outline = Hex(ow.Value<string>("outline"), s.Outline);
            }
        }

        float filmEnd = 0f;
        if (root["segments"] is JArray segs)
        {
            foreach (JToken t in segs)
            {
                if (t is not JObject so) continue;
                FilmSegment s = FilmSegment.Read(so, d.Segments.Count);
                d.Segments.Add(s);
                filmEnd = Mathf.Max(filmEnd, s.F1);
            }
        }

        d.FilmDuration = root["timeline"]?.Value<float?>("film_duration_s") ?? filmEnd;
        if (d.FilmDuration <= 0) d.FilmDuration = filmEnd;
        d.ReadAnnotations();
        d.Narration = FilmNarration.Find(d);
        if (d.Narration == null) d.Warnings.Add("no narration timeline (narration/timeline*.json with direction = " + d.Name + ")");
        return d;
    }

    /// <summary>annotations of every segment on the film clock; one id shown across consecutive segments with touching
    /// windows is merged into one instance (no fade at the segment boundary)</summary>
    void ReadAnnotations()
    {
        foreach (FilmSegment s in Segments)
        {
            if (s.Json["annotations"] is not JArray arr) continue;
            foreach (JToken t in arr)
            {
                if (t is not JObject a) continue;
                FilmAnnotation an = FilmAnnotation.Read(a, s);
                if (an.Pending)
                {
                    // never played: it would have to guess (see FilmAnnotation.PendingReason)
                    string why = $"annotation {an.Id} (segment {s.Id}) skipped: {an.PendingReason}";
                    if (!Warnings.Contains(why)) Warnings.Add(why);
                    SkippedAnnotations.Add(an.Id);
                    continue;
                }

                FilmAnnotation prev = Annotations.LastOrDefault(x => x.Id == an.Id && x.Kind == an.Kind);
                if (prev != null && an.F0 <= prev.F1 + 0.1f && an.F1 >= prev.F1)
                {
                    prev.F1 = an.F1;
                    prev.SegmentIds.Add(s.Id);
                    continue;
                }

                Annotations.Add(an);
            }
        }
    }

    /// <summary>the reaction title card's opacity at film time f (0..1): the same windows and fades as FilmOverlay's title
    /// card (a pure function of film time), so the camera can make room for the text while it is up</summary>
    public float TitleAlphaAt(float film)
    {
        float a = 0f;
        foreach (FilmSegment s in Segments)
        {
            if (s.TitleJson == null) continue;
            float f0 = s.F0 + F(s.TitleJson["film_in"], 0f);
            float f1 = s.TitleJson["film_out"] != null ? s.F0 + F(s.TitleJson["film_out"], s.Duration) : s.F1;
            f1 = Mathf.Min(f1, s.F1);
            if (film < f0 || film > f1) continue;
            bool product = s.TitleJson["product"] != null;
            float fadeIn = product ? 0.8f : 0.6f, fadeOut = s.Index == Segments.Count - 1 ? 0f : 0.6f;
            float k = fadeIn > 0f ? Mathf.SmoothStep(0f, 1f, (film - f0) / fadeIn) : 1f;
            if (fadeOut > 0f) k = Mathf.Min(k, Mathf.SmoothStep(0f, 1f, (f1 - film) / fadeOut));
            a = Mathf.Max(a, k);
        }

        return a;
    }

    public int SegmentIndexAt(float film)
    {
        if (Segments.Count == 0) return -1;
        if (film <= Segments[0].F0) return 0;
        int lo = 0, hi = Segments.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (Segments[mid].F0 <= film) lo = mid;
            else hi = mid - 1;
        }

        return lo;
    }

    public static float F(JToken t, float fallback = float.NaN) =>
        t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? t.Value<float>() : fallback;

    public static Color Hex(string hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        if (!hex.StartsWith("#")) hex = "#" + hex;
        return ColorUtility.TryParseHtmlString(hex, out Color c) ? c : fallback;
    }
}

/// <summary>output aspect: preset size, title-safe bands and the caption block (direction "presets" + "captions")</summary>
public class FilmAspect
{
    public string Name = "horizontal";
    public bool Vertical;
    public int Width = 1920, Height = 1080;
    public float Fps = 30f;
    public float SafeTop = 0.14f, SafeBottom = 0.2f;
    public float CaptionCentreY = 0.735f, CaptionBottomY = 0.79f, CaptionBaselineY = 0.925f;
    public int CaptionMaxLines = 2, CaptionMaxChars = 24;
    public float CaptionFontPx = 62f, CaptionWidthFrac = 0.86f;

    public static string Normalise(string aspect)
    {
        string a = (aspect ?? "").Trim().ToLowerInvariant();
        return a is "vertical" or "v" or "9:16" or "9x16" or "portrait" ? "vertical" : "horizontal";
    }
}

public class FilmCaptionStyle
{
    public Color Highlight = new(1f, 0.839f, 0.039f), HighlightText = new(0.067f, 0.067f, 0.067f);
    public Color Text = Color.white, Outline = new(0, 0, 0, 0.75f);
    public Vector2 BoxPadding = new(10, 3);
    public float CornerPx = 8f, TransitionMs = 60f, OutlinePx = 3f;
}

public class FilmSegment
{
    public int Index;
    public string Id, Mode, ViewState, Audio, Status, Notes;
    public float F0, F1, D0, D1, S0 = 1f, S1 = 1f, HoldS;
    public bool Replay;
    public JObject Json, Camera, Layers, TitleJson;
    public readonly List<string> Narration = new();

    public float Duration => F1 - F0;
    public bool Hold => Mathf.Abs(D1 - D0) < 1e-4f && (S0 <= 1e-4f && S1 <= 1e-4f || HoldS > 0);
    public bool Ramp => Mathf.Abs(S1 - S0) > 1e-4f;

    public static FilmSegment Read(JObject j, int index)
    {
        FilmSegment s = new()
        {
            Index = index, Json = j, Id = j.Value<string>("id") ?? $"seg{index}", Camera = j["camera"] as JObject ?? new JObject(),
            Layers = j["layers"] as JObject ?? new JObject(), ViewState = j.Value<string>("view_state") ?? "orbit",
            Audio = j.Value<string>("audio") ?? "locked", Status = j.Value<string>("status"), Notes = j.Value<string>("notes"),
            Replay = j.Value<bool?>("replay") ?? false, HoldS = FilmDirection.F(j["hold_s"], 0f), TitleJson = j["title"] as JObject
        };
        s.Mode = s.Camera.Value<string>("mode") ?? "orbit";
        if (j["film"] is JArray f && f.Count == 2)
        {
            s.F0 = FilmDirection.F(f[0], 0f);
            s.F1 = FilmDirection.F(f[1], s.F0);
        }

        if (j["dance"] is JArray d && d.Count == 2)
        {
            s.D0 = FilmDirection.F(d[0], 0f);
            s.D1 = FilmDirection.F(d[1], s.D0);
        }

        JToken sp = j["speed"];
        if (sp is JArray sa && sa.Count == 2)
        {
            s.S0 = FilmDirection.F(sa[0], 1f);
            s.S1 = FilmDirection.F(sa[1], 1f);
        }
        else
        {
            s.S0 = s.S1 = FilmDirection.F(sp, 1f);
        }

        if (j["narration"] is JArray n)
        {
            foreach (JToken x in n) s.Narration.Add(x.ToString());
        }

        return s;
    }

    /// <summary>dance time of this segment at film time f WITHOUT smoothing: constant speed, linear speed ramp
    /// (quadratic dance time, exact at both ends) or a hold</summary>
    public float RawDance(float f)
    {
        float dur = Duration;
        if (dur <= 1e-5f) return D0;
        float u = Mathf.Clamp01((f - F0) / dur);
        if (Mathf.Abs(D1 - D0) < 1e-6f) return D0;
        if (!Ramp) return D0 + (D1 - D0) * u;
        float mean = (S0 + S1) * 0.5f;
        if (mean <= 1e-5f) return D0 + (D1 - D0) * u;
        float g = (S0 * u + (S1 - S0) * u * u * 0.5f) / mean;
        return D0 + (D1 - D0) * g;
    }

    public float LayerFloat(string key, float fallback)
    {
        JToken t = Layers[key];
        if (t == null) return fallback;
        if (t.Type == JTokenType.Boolean) return t.Value<bool>() ? 1f : 0f;
        return FilmDirection.F(t, fallback);
    }

    public bool LayerBool(string key, bool fallback)
    {
        JToken t = Layers[key];
        if (t == null) return fallback;
        return t.Type switch
        {
            JTokenType.Boolean => t.Value<bool>(),
            JTokenType.Integer or JTokenType.Float => t.Value<float>() > 0.5f,
            JTokenType.String => !string.Equals(t.Value<string>(), "off", StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(t.Value<string>(), "false", StringComparison.OrdinalIgnoreCase),
            _ => fallback
        };
    }

    public string LayerString(string key, string fallback) => Layers[key]?.Type == JTokenType.String ? Layers.Value<string>(key) : fallback;

    public float Cam(string key, float fallback) => FilmDirection.F(Camera[key], fallback);
    public string CamS(string key, string fallback) => Camera[key]?.Type == JTokenType.String ? Camera.Value<string>(key) : fallback;
}

/// <summary>one 3D / screen annotation of the shot list on the film clock (absolute film window F0..F1)</summary>
public class FilmAnnotation
{
    public string Id, Kind, Text;
    public float F0, F1, D0 = float.NaN, D1 = float.NaN;
    public JObject Json;
    public readonly List<string> SegmentIds = new();

    public JObject Target => Json["target"] as JObject;

    /// <summary>why this annotation is not final (null = playable): a "TBD" field, a null value or an unfilled
    /// {placeholder} in its text. The film never guesses (a TBD foot used to draw on the left foot, a TBD turn direction
    /// clockwise, a TBD window the whole segment): such an annotation is skipped and reported.</summary>
    public string PendingReason { get; private set; }

    public bool Pending => PendingReason != null;

    static readonly System.Text.RegularExpressions.Regex Placeholder = new(@"\{[A-Za-z_]+\}");

    static string FindPending(JToken t, string key)
    {
        switch (t.Type)
        {
            case JTokenType.Object:
                foreach (JProperty p in ((JObject)t).Properties())
                {
                    if (p.Name is "note" or "status" or "resolve" or "id" or "kind") continue;
                    string r = FindPending(p.Value, p.Name);
                    if (r != null) return r;
                }

                return null;
            case JTokenType.Array:
                foreach (JToken c in (JArray)t)
                {
                    string r = FindPending(c, key);
                    if (r != null) return r;
                }

                return null;
            case JTokenType.Null:
                return $"{key} is null";
            case JTokenType.String:
                string s = t.Value<string>() ?? "";
                if (s.TrimStart().StartsWith("TBD", StringComparison.OrdinalIgnoreCase)) return $"{key} is TBD";
                if (key == "text" && Placeholder.IsMatch(s)) return $"text has an unfilled placeholder ({s})";
                return null;
            default:
                return null;
        }
    }

    public static FilmAnnotation Read(JObject a, FilmSegment s)
    {
        FilmAnnotation an = new() { Json = a, Id = a.Value<string>("id") ?? "", Kind = a.Value<string>("kind") ?? "arrow3d", Text = a.Value<string>("text") };
        an.PendingReason = FindPending(a, "annotation");
        an.SegmentIds.Add(s.Id);
        if (a["film_in_segment"] is JArray w && w.Count == 2)
        {
            an.F0 = s.F0 + FilmDirection.F(w[0], 0f);
            an.F1 = s.F0 + FilmDirection.F(w[1], s.Duration);
        }
        else
        {
            an.F0 = s.F0;
            an.F1 = s.F1;
        }

        an.F0 = Mathf.Max(an.F0, s.F0);
        an.F1 = Mathf.Min(an.F1, s.F1);
        if (a["dance_t"] is JArray d && d.Count == 2)
        {
            an.D0 = FilmDirection.F(d[0]);
            an.D1 = FilmDirection.F(d[1]);
        }

        return an;
    }
}

/// <summary>narration timeline (film/narration/timeline*.json): line wavs at clip_start and caption chunks with
/// per-word film times, per aspect</summary>
public class FilmNarration
{
    public class Word
    {
        public string Text;
        public float Start, End;
        public int Line;
    }

    public class Chunk
    {
        public string LineId;
        public float Show, Hide;
        public string[] Lines;
        public Word[] Words;
    }

    public class Line
    {
        public string Id, Text, Audio, Status;
        public float ClipStart, ClipEnd, FilmStart, SpeechEnd;
    }

    public string Path, Voice, Model;
    public readonly List<Line> Lines = new();
    public readonly Dictionary<string, List<Chunk>> Captions = new();

    public List<Chunk> ChunksFor(string aspect) =>
        Captions.TryGetValue(FilmAspect.Normalise(aspect), out List<Chunk> c) ? c : Captions.Values.FirstOrDefault() ?? new List<Chunk>();

    /// <summary>narration/timeline*.json in the direction's folder whose "direction" is the direction's name</summary>
    public static FilmNarration Find(FilmDirection d)
    {
        string dir = System.IO.Path.Combine(d.Dir, "narration");
        if (!Directory.Exists(dir)) return null;
        foreach (string f in Directory.GetFiles(dir, "timeline*.json").OrderBy(x => x.Length))
        {
            try
            {
                JObject j = JObject.Parse(File.ReadAllText(f));
                if (!string.Equals(j.Value<string>("direction"), d.Name, StringComparison.Ordinal)) continue;
                return Read(j, f);
            }
            catch (Exception e)
            {
                d.Warnings.Add($"{System.IO.Path.GetFileName(f)} unreadable: {e.Message}");
            }
        }

        return null;
    }

    static FilmNarration Read(JObject j, string path)
    {
        FilmNarration n = new() { Path = path, Voice = j.Value<string>("voice"), Model = j.Value<string>("model") };
        if (j["lines"] is JArray lines)
        {
            foreach (JToken t in lines)
            {
                n.Lines.Add(new Line
                {
                    Id = t.Value<string>("id"), Text = t.Value<string>("text"), Audio = t.Value<string>("audio"), Status = t.Value<string>("status"),
                    ClipStart = FilmDirection.F(t["clip_start"], 0f), ClipEnd = FilmDirection.F(t["clip_end"], 0f),
                    FilmStart = FilmDirection.F(t["film_start"], 0f), SpeechEnd = FilmDirection.F(t["speech_end"], 0f)
                });
            }
        }

        if (j["captions"] is JObject caps)
        {
            foreach (KeyValuePair<string, JToken> kv in caps)
            {
                if (kv.Value is not JArray arr) continue;
                List<Chunk> list = new();
                foreach (JToken c in arr)
                {
                    string[] ls = c["lines"] is JArray la ? la.Select(x => x.ToString()).ToArray() : Array.Empty<string>();
                    List<Word> words = new();
                    if (c["words"] is JArray wa)
                    {
                        foreach (JToken w in wa)
                        {
                            words.Add(new Word
                            {
                                Text = w.Value<string>("text") ?? "", Start = FilmDirection.F(w["start"], 0f), End = FilmDirection.F(w["end"], 0f)
                            });
                        }
                    }

                    AssignLines(ls, words);
                    list.Add(new Chunk
                    {
                        LineId = c.Value<string>("line"), Show = FilmDirection.F(c["show"], 0f), Hide = FilmDirection.F(c["hide"], 0f),
                        Lines = ls, Words = words.ToArray()
                    });
                }

                list.Sort((a, b) => a.Show.CompareTo(b.Show));
                n.Captions[FilmAspect.Normalise(kv.Key)] = list;
            }
        }

        return n;
    }

    /// <summary>words to caption lines in order by each line's word count (the chunker splits lines at word boundaries)</summary>
    static void AssignLines(string[] lines, List<Word> words)
    {
        int w = 0;
        for (int l = 0; l < lines.Length && w < words.Count; l++)
        {
            int count = lines[l].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            for (int i = 0; i < count && w < words.Count; i++) words[w++].Line = l;
        }

        int last = Mathf.Max(0, lines.Length - 1);
        for (; w < words.Count; w++) words[w].Line = last;
    }

    public static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
