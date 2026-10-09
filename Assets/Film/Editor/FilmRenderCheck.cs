using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

/// <summary>
/// Checks a film render with ffprobe / ffmpeg (VIEWER_SPEC 8.5: "duration, resolution, fps and audio presence verified
/// after every render") and writes &lt;name&gt;.report.json + &lt;name&gt;.report.md next to the MP4:
///   video: H.264, the preset size, constant fps, frame count = frames recorded, duration = frames / fps, film covered;
///   audio: an AAC stream, its duration = the video's, not silent;
///   sync (director mode): the recorded audio cross-correlated against the film soundtrack mix (8 kHz mono, three 6 s
///   windows, lags +-300 ms): offset in ms (positive = the recording's audio is LATE vs. the film clock), |offset| must
///   be within one frame.
/// The probing runs on a worker thread; FilmRecorder polls Tick from EditorApplication.update.
/// </summary>
public static class FilmRenderCheck
{
    public const string FFmpegBinPref = "HM.Film.FFmpegBin";

    class Input
    {
        public FilmRecorder.Job job;
        public string ffprobe, ffmpeg;
    }

    public class Check
    {
        public string name, detail;
        public bool pass, required = true;
    }

    public class Result
    {
        public bool pass;
        public string error;
        public JObject probe;
        public List<Check> checks = new();
        public Dictionary<string, object> video = new(), audio = new(), sync = new();
    }

    static Task<Result> task;
    static string taskJob;

    public static void Tick(FilmRecorder.Job j, Action<FilmRecorder.Job, string, bool> done)
    {
        if (task == null || taskJob != j.id)
        {
            Input input = new()
            {
                job = JsonConvert.DeserializeObject<FilmRecorder.Job>(JsonConvert.SerializeObject(j)),
                ffprobe = Tool("ffprobe"), ffmpeg = Tool("ffmpeg")
            };
            taskJob = j.id;
            task = Task.Run(() => Run(input));
            return;
        }

        if (!task.IsCompleted) return;
        Result r = task.IsFaulted ? new Result { error = task.Exception?.GetBaseException().ToString() } : task.Result;
        task = null;
        string report = Write(j, r);
        done(j, report, r.pass);
    }

    // ------------------------------------------------------------------ tools

    public static string Tool(string name)
    {
        string exe = name + (Environment.OSVersion.Platform == PlatformID.Win32NT ? ".exe" : "");
        string dir = EditorPrefs.GetString(FFmpegBinPref, "");
        if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe);
        foreach (string p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string c = Path.Combine(p.Trim('"'), exe);
                if (File.Exists(c)) return c;
            }
            catch
            {
                // bad PATH entry
            }
        }

        foreach (string root in new[] { "C:\\", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            try
            {
                foreach (string d in Directory.GetDirectories(root, "ffmpeg*").OrderByDescending(x => x))
                {
                    string c = Path.Combine(d, "bin", exe);
                    if (File.Exists(c)) return c;
                }
            }
            catch
            {
                // no access
            }
        }

        return null;
    }

    static (int code, byte[] stdout, string stderr) RunTool(string exe, string args, int timeoutMs = 300000)
    {
        ProcessStartInfo psi = new(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using Process p = Process.Start(psi)!;
        try
        {
            p.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // best effort
        }

        StringBuilder err = new();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null && err.Length < 20000) err.AppendLine(e.Data);
        };
        p.BeginErrorReadLine();
        using MemoryStream ms = new();
        p.StandardOutput.BaseStream.CopyTo(ms);
        if (!p.WaitForExit(timeoutMs))
        {
            try
            {
                p.Kill();
            }
            catch
            {
                // ignore
            }

            return (-1, ms.ToArray(), "timeout");
        }

        p.WaitForExit();
        return (p.ExitCode, ms.ToArray(), err.ToString());
    }

    static float[] DecodeMono8k(string ffmpeg, string inputArgs)
    {
        // decode as stereo and average here: ffmpeg's own -ac 1 downmix scales each channel by 0.707, which reads centred
        // material 3 dB hot (a false "peak above 0 dBFS")
        (int code, byte[] raw, string err) = RunTool(ffmpeg, $"-v error -nostdin {inputArgs} -vn -ac 2 -ar 8000 -f f32le -");
        if (code != 0) throw new Exception($"ffmpeg decode failed: {err.Trim()}");
        float[] st = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, st, 0, st.Length * 4);
        float[] x = new float[st.Length / 2];
        for (int i = 0; i < x.Length; i++) x[i] = 0.5f * (st[2 * i] + st[2 * i + 1]);
        return x;
    }

    /// <summary>indices of near-white frames (luma mean well above the clip's median), via ffmpeg signalstats</summary>
    static List<int> WhiteFrames(string ffmpeg, string mp4)
    {
        (int code, byte[] raw, string err) = RunTool(ffmpeg,
            $"-v error -nostdin -i \"{mp4}\" -an -vf \"scale=96:-2,signalstats,metadata=mode=print:key=lavfi.signalstats.YAVG:file=-\" -f null -");
        if (code != 0) throw new Exception($"ffmpeg signalstats failed: {err.Trim()}");
        List<double> y = new();
        foreach (string line in Encoding.UTF8.GetString(raw).Split('\n'))
        {
            int k = line.IndexOf("lavfi.signalstats.YAVG=", StringComparison.Ordinal);
            if (k >= 0 && double.TryParse(line.Substring(k + 23).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double val)) y.Add(val);
        }

        if (y.Count == 0) throw new Exception("ffmpeg signalstats printed no frames");
        double median = y.OrderBy(x => x).ElementAt(y.Count / 2);
        double thr = Math.Max(200, median + 0.6 * (235 - median));
        return Enumerable.Range(0, y.Count).Where(i => y[i] >= thr).ToList();
    }

    static string F(double v, string fmt = "0.###") => v.ToString(fmt, CultureInfo.InvariantCulture);

    static double Ratio(string r)
    {
        if (string.IsNullOrEmpty(r)) return double.NaN;
        string[] p = r.Split('/');
        if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
            double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b != 0) return a / b;
        return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
    }

    static double D(JToken t) => t != null && double.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;

    // ------------------------------------------------------------------ the checks (worker thread: no Unity API)

    static Result Run(Input input)
    {
        FilmRecorder.Job j = input.job;
        Result r = new();
        void Add(string name, bool pass, string detail, bool required = true) =>
            r.checks.Add(new Check { name = name, pass = pass, detail = detail, required = required });

        if (input.ffprobe == null || input.ffmpeg == null)
        {
            r.error = "ffprobe/ffmpeg not found (PATH, C:\\ffmpeg*\\bin, or EditorPrefs HM.Film.FFmpegBin)";
            Add("tools", false, r.error);
            return r;
        }

        if (!File.Exists(j.output))
        {
            Add("file", false, $"{j.output} missing");
            return r;
        }

        long bytes = new FileInfo(j.output).Length;
        Add("file", bytes > 1000, $"{bytes / 1048576.0:0.0} MB");

        (int code, byte[] outp, string err) = RunTool(input.ffprobe,
            "-v error -show_entries format=duration,size,bit_rate,format_name,start_time:stream=index,codec_type,codec_name,profile," +
            $"width,height,pix_fmt,r_frame_rate,avg_frame_rate,nb_frames,duration,start_time,sample_rate,channels,bit_rate -of json \"{j.output}\"");
        if (code != 0)
        {
            Add("ffprobe", false, err.Trim());
            return r;
        }

        r.probe = JObject.Parse(Encoding.UTF8.GetString(outp));
        JArray streams = r.probe["streams"] as JArray ?? new JArray();
        JToken v = streams.FirstOrDefault(s => (string)s["codec_type"] == "video");
        JToken a = streams.FirstOrDefault(s => (string)s["codec_type"] == "audio");
        double fmtDur = D(r.probe["format"]?["duration"]);
        double expectedDur = j.frames / (double)j.fps;
        double frame = 1.0 / j.fps;

        if (v == null)
        {
            Add("video stream", false, "none");
        }
        else
        {
            int w = (int?)v["width"] ?? 0, h = (int?)v["height"] ?? 0;
            double fr = Ratio((string)v["avg_frame_rate"]), rfr = Ratio((string)v["r_frame_rate"]);
            double vdur = D(v["duration"]);
            int nb = int.TryParse((string)v["nb_frames"], out int n) ? n : -1;
            r.video = new Dictionary<string, object>
            {
                ["codec"] = (string)v["codec_name"], ["profile"] = (string)v["profile"], ["width"] = w, ["height"] = h,
                ["pix_fmt"] = (string)v["pix_fmt"], ["avg_frame_rate"] = (string)v["avg_frame_rate"], ["r_frame_rate"] = (string)v["r_frame_rate"],
                ["nb_frames"] = nb, ["duration_s"] = vdur, ["bit_rate"] = (string)v["bit_rate"], ["frames_recorded"] = j.frames,
                ["expected_duration_s"] = expectedDur
            };
            double vbr = D(v["bit_rate"]);
            Add("codec", (string)v["codec_name"] == "h264",
                $"{v["codec_name"]} {v["profile"]} {v["pix_fmt"]}, {(double.IsNaN(vbr) ? "?" : F(vbr / 1e6, "0.0"))} Mbps (target {F(j.videoMbps, "0.#")})");
            Add("resolution", w == j.width && h == j.height, $"{w}x{h} (preset {j.width}x{j.height})");
            Add("fps", Math.Abs(fr - j.fps) < 0.01 && Math.Abs(rfr - j.fps) < 0.01, $"avg {v["avg_frame_rate"]}, r {v["r_frame_rate"]} (preset {j.fps})");
            Add("frame count", nb >= 0 && Math.Abs(nb - j.frames) <= 1,
                $"{nb} in the file, {j.frames} film frames shown while recording" +
                (j.recorderFrames >= 0 ? $", Recorder counter {j.recorderFrames} ({j.recorderFramesAtBegin} before the film began)" : ""));
            Add("duration", Math.Abs(vdur - expectedDur) <= 1.5 * frame, $"video {F(vdur)} s, expected {F(expectedDur)} s ({j.frames} / {j.fps})");
            if (j.mode == "director")
            {
                double film = j.filmEnd - j.start;
                Add("film covered", j.aborted ? false : vdur >= film - 1.5 * frame,
                    $"{F(vdur)} s of film {F(j.start)}-{F(j.filmEnd)} s ({F(film)} s){(j.aborted ? " - ABORTED" : "")}");
            }
        }

        if (v != null && j.flashAt is { Length: > 0 })
        {
            // recorder self-test: the white marker frames must sit at their recorded-frame index (video frame 0 = the
            // frame on which the soundtrack / director started)
            try
            {
                List<int> found = WhiteFrames(input.ffmpeg, j.output);
                int[] want = j.flashAt;
                int[] shift = want.Select(w => found.Count > 0 ? found.OrderBy(f => Math.Abs(f - w)).First() - w : int.MinValue).ToArray();
                bool ok = found.Count == want.Length && shift.All(s => s == 0);
                r.video["marker_frames_found"] = found;
                r.video["marker_frames_expected"] = want;
                Add("video frame alignment", ok,
                    $"white marker frames at {(found.Count > 0 ? string.Join(", ", found) : "none")} (expected {string.Join(", ", want)}): " +
                    (ok ? "video frame 0 is the start frame" :
                        shift.All(s => s == shift[0]) && shift[0] != int.MinValue
                            ? $"picture is {(shift[0] < 0 ? "EARLY" : "LATE")} by {Math.Abs(shift[0])} frame(s) vs. the audio"
                            : "markers not found as expected"));
            }
            catch (Exception e)
            {
                Add("video frame alignment", false, e.Message, required: false);
            }
        }

        if (a == null)
        {
            Add("audio stream", false, "none");
        }
        else
        {
            double adur = D(a["duration"]);
            r.audio["codec"] = (string)a["codec_name"];
            r.audio["sample_rate"] = (string)a["sample_rate"];
            r.audio["channels"] = (int?)a["channels"] ?? 0;
            r.audio["duration_s"] = adur;
            r.audio["bit_rate"] = (string)a["bit_rate"];
            r.audio["start_time"] = (string)a["start_time"];
            Add("audio stream", (string)a["codec_name"] == "aac" && ((int?)a["channels"] ?? 0) >= 1,
                $"{a["codec_name"]} {a["sample_rate"]} Hz x{a["channels"]}");
            double vdur = D(v?["duration"]);
            Add("audio duration", !double.IsNaN(adur) && Math.Abs(adur - (double.IsNaN(vdur) ? fmtDur : vdur)) <= 0.25,
                $"audio {F(adur)} s, video {F(vdur)} s");

            try
            {
                float[] rec = DecodeMono8k(input.ffmpeg, $"-i \"{j.output}\"");
                double sum = 0, peak = 0;
                int silentBlocks = 0, blocks = 0;
                for (int i = 0; i < rec.Length; i++)
                {
                    sum += rec[i] * (double)rec[i];
                    peak = Math.Max(peak, Math.Abs(rec[i]));
                }

                for (int b = 0; b + 8000 <= rec.Length; b += 8000)
                {
                    double s = 0;
                    for (int i = b; i < b + 8000; i++) s += rec[i] * (double)rec[i];
                    blocks++;
                    if (10 * Math.Log10(s / 8000 + 1e-12) < -60) silentBlocks++;
                }

                double rms = 10 * Math.Log10(sum / Math.Max(1, rec.Length) + 1e-12);
                r.audio["rms_dbfs"] = Math.Round(rms, 2);
                r.audio["peak_dbfs"] = Math.Round(20 * Math.Log10(peak + 1e-12), 2);
                r.audio["silent_1s_blocks"] = $"{silentBlocks}/{blocks}";
                Add("audio level", rms > -50, $"RMS {F(rms, "0.0")} dBFS, peak {F(20 * Math.Log10(peak + 1e-12), "0.0")} dBFS, {silentBlocks}/{blocks} silent seconds");

                if ((j.mode == "director" || j.plainMix) && File.Exists(j.mix))
                {
                    double len = rec.Length / 8000.0;
                    float[] mix = DecodeMono8k(input.ffmpeg, $"-ss {F(j.start, "0.######")} -t {F(len + 1, "0.###")} -i \"{j.mix}\"");
                    Sync(rec, mix, r, frame);
                    double off = r.sync.TryGetValue("offset_ms_median", out object o) ? Convert.ToDouble(o) : double.NaN;
                    double corr = r.sync.TryGetValue("correlation_min", out object c) ? Convert.ToDouble(c) : double.NaN;
                    // a clip shorter than the sync windows cannot be checked: skipped (not a failure)
                    bool tooShort = r.sync.TryGetValue("note", out object skipNote) && skipNote is string skipText && skipText.StartsWith("too short", StringComparison.Ordinal);
                    if (tooShort)
                    {
                        Add("A/V sync vs. mix", true, "skipped: the clip is shorter than the sync windows (record at least ~12 s to check)", required: false);
                    }
                    else
                    {
                        Add("A/V sync vs. mix", !double.IsNaN(off) && Math.Abs(off) <= 1000 * frame && corr > 0.5,
                            $"audio {F(off, "0.0")} ms vs. the film clock (+ = late), correlation >= {F(corr, "0.00")} " +
                            $"(windows: {(r.sync.TryGetValue("windows", out object ws) && ws is List<string> wl ? string.Join(", ", wl) : (r.sync.TryGetValue("note", out object nt) ? nt : "none"))})");
                    }
                }
                else
                {
                    r.sync["note"] = j.mode == "director" || j.plainMix ? "mix missing - not checked" : "plain test (capture song) - not checked";
                }
            }
            catch (Exception e)
            {
                Add("audio analysis", false, e.Message, required: false);
            }
        }

        r.pass = r.checks.Where(c => c.required).All(c => c.pass);
        return r;
    }

    /// <summary>normalised cross-correlation of the recording against the mix in three windows; lag = recording - mix</summary>
    static void Sync(float[] rec, float[] mix, Result r, double frame)
    {
        const int sr = 8000, maxLag = 2400; // +-300 ms
        int n = Math.Min(rec.Length, mix.Length);
        int win = Math.Min(6 * sr, n / 4);
        List<double> lags = new(), corrs = new();
        List<string> desc = new();
        if (win < sr)
        {
            r.sync["note"] = "too short for a sync check";
            return;
        }

        foreach (double frac in new[] { 0.15, 0.5, 0.85 })
        {
            int c0 = (int)(frac * n) - win / 2;
            int s0 = Math.Max(maxLag, Math.Min(c0, n - win - maxLag - 1));
            if (s0 < maxLag || s0 + win + maxLag >= rec.Length || s0 + win > mix.Length) continue;
            double em = 0;
            for (int i = 0; i < win; i++) em += mix[s0 + i] * (double)mix[s0 + i];
            if (em < 1e-6) continue; // silence in the mix here
            double best = double.NegativeInfinity;
            int bestLag = 0;
            for (int lag = -maxLag; lag <= maxLag; lag++)
            {
                double dot = 0, er = 0;
                int o = s0 + lag;
                for (int i = 0; i < win; i++)
                {
                    double x = rec[o + i];
                    dot += x * mix[s0 + i];
                    er += x * x;
                }

                double cc = dot / Math.Sqrt(em * er + 1e-12);
                if (cc > best)
                {
                    best = cc;
                    bestLag = lag;
                }
            }

            lags.Add(bestLag * 1000.0 / sr);
            corrs.Add(best);
            desc.Add($"{F(s0 / (double)sr, "0.0")} s: {F(bestLag * 1000.0 / sr, "0.0")} ms r={F(best, "0.000")}");
        }

        r.sync["windows"] = desc;
        if (lags.Count == 0)
        {
            r.sync["note"] = "no usable window";
            return;
        }

        List<double> sorted = lags.OrderBy(x => x).ToList();
        r.sync["offset_ms_median"] = sorted[sorted.Count / 2];
        r.sync["offset_ms_spread"] = sorted.Last() - sorted.First();
        r.sync["correlation_min"] = Math.Round(corrs.Min(), 4);
        r.sync["frame_ms"] = Math.Round(1000 * frame, 3);
    }

    // ------------------------------------------------------------------ report (main thread)

    /// <summary>direction.audio.render_notice (the private-render notice of this film), null when absent</summary>
    static string DirectionNotice(string directionPath)
    {
        try
        {
            if (string.IsNullOrEmpty(directionPath) || !File.Exists(directionPath)) return null;
            string notice = JObject.Parse(File.ReadAllText(directionPath))["audio"]?["render_notice"]?.Value<string>();
            return string.IsNullOrWhiteSpace(notice) ? null : notice;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static string Write(FilmRecorder.Job j, Result r)
    {
        string json = Path.ChangeExtension(j.output, ".report.json");
        string md = Path.ChangeExtension(j.output, ".report.md");
        if (r.error != null && r.checks.All(c => c.name != "tools")) r.checks.Add(new Check { name = "validation", pass = false, detail = r.error });
        double fpsReal = j.realElapsed > 0 ? j.frames / j.realElapsed : 0;
        // the notice names nothing: the direction file (git-ignored work data) says what the film contains (audio.render_notice)
        string copyright = DirectionNotice(j.direction) ??
                           "PRIVATE RENDER. It may contain copyrighted music and a cloned narration voice. Keep it out of git " +
                           "and do not publish or share it.";
        JObject o = new()
        {
            ["kind"] = "film_render_report",
            ["pass"] = r.pass && !j.aborted,
            ["output"] = j.output,
            ["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["copyright"] = copyright,
            ["render_speed_fps"] = Math.Round(fpsReal, 3),
            ["job"] = JObject.FromObject(j),
            ["checks"] = JArray.FromObject(r.checks),
            ["video"] = JObject.FromObject(r.video),
            ["audio"] = JObject.FromObject(r.audio),
            ["sync"] = JObject.FromObject(r.sync),
            ["ffprobe"] = r.probe,
            ["error"] = r.error
        };
        File.WriteAllText(json, o.ToString(Formatting.Indented));

        StringBuilder s = new();
        s.AppendLine($"# Film render report: {Path.GetFileName(j.output)}");
        s.AppendLine();
        s.AppendLine($"**{(r.pass && !j.aborted ? "PASS" : j.aborted ? "ABORTED" : "FAIL")}** - {j.capture}, {j.aspect} {j.width}x{j.height} @ {j.fps} fps, " +
                     $"mode {j.mode}{(j.mode == "plain" ? $" (plain pipeline test{(j.plainMix ? " under the film soundtrack" : "")}; director {(j.directorType != null ? "present" : "missing")})" : "")}.");
        s.AppendLine();
        s.AppendLine($"> {copyright}");
        s.AppendLine();
        s.AppendLine("| Check | Result | Detail |");
        s.AppendLine("|---|---|---|");
        foreach (Check c in r.checks)
            s.AppendLine($"| {c.name} | {(c.pass ? "pass" : c.required ? "**FAIL**" : "warn")} | {c.detail?.Replace("|", "/")} |");
        s.AppendLine();
        s.AppendLine("| Item | Value |");
        s.AppendLine("|---|---|");
        s.AppendLine($"| Direction | `{j.direction}` ({j.directionName}) |");
        s.AppendLine($"| Film | {F(j.start)}-{F(j.filmEnd)} s of {F(j.filmDuration)} s; last film time seen {F(j.filmTime)} s |");
        s.AppendLine($"| Soundtrack | `{j.mix}`{(j.soundtrack != null ? $" - {j.soundtrack}" : "")} |");
        s.AppendLine($"| Frames | {j.frames} film frames (Recorder armed on frame {j.armFrame}, film began on frame {j.startFrame}); soundtrack started at film {F(j.soundtrackStartedAt)} s on frame {j.soundtrackStartFrame} |");
        s.AppendLine($"| Render time | {F(j.realElapsed, "0.0")} s real ({F(fpsReal, "0.00")} frames/s; {F(fpsReal / j.fps, "0.00")}x real time) |");
        s.AppendLine($"| Game view at start | {j.screenW}x{j.screenH} |");
        s.AppendLine($"| Director | {(j.directorType ?? "missing")}; Prepare {(j.prepareCalled ? "used" : "not used")}; Begin({(j.begin3 ? "path, aspect, start" : "path, aspect")}) |");
        s.AppendLine($"| Ended | {j.stopReason ?? j.message} |");
        s.AppendLine($"| Unity / Recorder / GPU | {j.unity} / {j.recorderVersion} / {j.gpu} |");
        if (r.sync.Count > 0) s.AppendLine($"| Sync | {string.Join("; ", r.sync.Select(kv => $"{kv.Key}={(kv.Value is List<string> l ? string.Join(", ", l) : kv.Value)}"))} |");
        if (j.warnings.Count > 0)
        {
            s.AppendLine();
            s.AppendLine("Warnings:");
            foreach (string w in j.warnings) s.AppendLine($"- {w}");
        }

        if (r.error != null)
        {
            s.AppendLine();
            s.AppendLine($"Error: {r.error}");
        }

        File.WriteAllText(md, s.ToString());
        return json;
    }
}
