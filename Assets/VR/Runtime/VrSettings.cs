using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HmVr
{
    /// <summary>The hair budget of the headset tiers: Full (all cards from close up, tip glow), Light (cheaper levels sooner, no tip glow), Off.</summary>
    public enum HairLevel
    {
        Full,
        Light,
        Off
    }

    /// <summary>
    /// The headset's quality tier as DATA: hm_data/quest_settings.json (next to the library, pushed or edited with adb) and the menu's
    /// buttons, so the budget can be tuned on the device without a rebuild. Every key is optional:
    ///
    ///   { "display": "auto" | "passthrough" | "immersive",   // the view the app starts in (the menu toggle is remembered and wins)
    ///     "hair": "full" | "light" | "off",
    ///     "hair_full":  { "lod_scale": 1.0, "min_lod": 0, "tips": true },     // lod_scale &gt; 1 = the cheaper levels from a shorter distance
    ///     "hair_light": { "lod_scale": 1.8, "min_lod": 1, "tips": false },
    ///     "hair_bake": true,           // simulate the whole take in the background after load (false = live simulation only)
    ///     "hair_bake_chunk": 6,        // capture frames per bake step (smaller = shorter main-thread spikes, slower bake)
    ///     "hair_substeps": 0, "hair_iterations": 0,   // simulation quality (0 = the groom's own: 8 and 2)
    ///     "adaptive": true,            // step the hair down one level when a lesson keeps missing the frame budget (never up by itself)
    ///     "msaa": 0,                   // 0 = by device (Quest 3 and later 4x, Quest 2 2x)
    ///     "eye_scale": 0.0,            // 0 = by device (1.0 on Quest 3, 0.9 on Quest 2)
    ///     "refresh_hz": 72,            // asked from the runtime when it offers it
    ///     "perf_log": true,            // frame statistics to logcat every 5 s (tag Unity, prefix HM-PERF)
    ///     "splats": true,              // the splat dancers (photoreal Gaussians) for a lesson whose capture has splats/ (the recap); false = the mesh avatars
    ///     "splat_max": 0,              // at most this many Gaussians per dancer (0 = the Quest default, SplatDancers.QuestDefaultMax; -1 = all)
    ///     "splat_max_sigma": 0.0,      // clamp every Gaussian's standard deviation to this many metres (0 = the Quest default; -1 = none)
    ///     "splat_path": "",            // "composite" | "direct" (empty = the Quest default)
    ///     "splat_depth": "" }          // "on" | "off": the splats write their depth (overlays depth-test against them); empty = the Quest default
    /// Command line (adb ... -e unity "..."): -hmsplats on|off, -hmsplatmax N, -hmsplatsigma M, -hmsplatpath composite|direct, -hmsplatdepth on|off.
    /// </summary>
    public sealed class VrSettings
    {
        public struct HairProfile
        {
            public float LodScale;
            public int MinLod;
            public bool Tips;
        }

        public string Display = "auto";
        public HairLevel Hair = HairLevel.Full;
        public HairProfile Full = new() { LodScale = 1f, MinLod = 0, Tips = true };
        public HairProfile Light = new() { LodScale = 1.8f, MinLod = 1, Tips = false };
        public bool Adaptive = true;
        public bool HairBake = true;
        public int HairBakeChunk = 6, HairSubsteps, HairIterations;
        public int Msaa;
        public float EyeScale;
        public int RefreshHz = 72;
        public bool PerfLog = true;
        public bool Splats = true;
        public int SplatMax;
        public float SplatMaxSigma;
        public string SplatPath = "", SplatDepth = "";
        public string Source = "defaults";

        public static VrSettings Current { get; private set; } = new();

        public static string FilePath => Path.Combine(HmData.Root, "quest_settings.json");

        public HairProfile ProfileOf(HairLevel l) => l == HairLevel.Light ? Light : Full;

        public static VrSettings Load()
        {
            VrSettings s = new();
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    JObject j = JObject.Parse(File.ReadAllText(path));
                    s.Source = path;
                    if (j["display"] != null) s.Display = j.Value<string>("display").Trim().ToLowerInvariant();
                    if (j["hair"] != null) s.Hair = ParseLevel(j.Value<string>("hair"), s.Hair);
                    ReadProfile(j["hair_full"] as JObject, ref s.Full);
                    ReadProfile(j["hair_light"] as JObject, ref s.Light);
                    if (j["adaptive"] != null) s.Adaptive = j.Value<bool>("adaptive");
                    if (j["hair_bake"] != null) s.HairBake = j.Value<bool>("hair_bake");
                    if (j["hair_bake_chunk"] != null) s.HairBakeChunk = Mathf.Clamp(j.Value<int>("hair_bake_chunk"), 1, 64);
                    if (j["hair_substeps"] != null) s.HairSubsteps = j.Value<int>("hair_substeps");
                    if (j["hair_iterations"] != null) s.HairIterations = j.Value<int>("hair_iterations");
                    if (j["msaa"] != null) s.Msaa = j.Value<int>("msaa");
                    if (j["eye_scale"] != null) s.EyeScale = j.Value<float>("eye_scale");
                    if (j["refresh_hz"] != null) s.RefreshHz = j.Value<int>("refresh_hz");
                    if (j["perf_log"] != null) s.PerfLog = j.Value<bool>("perf_log");
                    if (j["splats"] != null) s.Splats = j.Value<bool>("splats");
                    if (j["splat_max"] != null) s.SplatMax = j.Value<int>("splat_max");
                    if (j["splat_max_sigma"] != null) s.SplatMaxSigma = j.Value<float>("splat_max_sigma");
                    if (j["splat_path"] != null) s.SplatPath = j.Value<string>("splat_path").Trim().ToLowerInvariant();
                    if (j["splat_depth"] != null) s.SplatDepth = j.Value<string>("splat_depth").Trim().ToLowerInvariant();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("VrSettings: quest_settings.json unreadable, using defaults: " + e.Message);
            }

            string hair = VrArgs.Get("-hmhair");
            if (hair != null) s.Hair = ParseLevel(hair, s.Hair);
            string splats = VrArgs.Get("-hmsplats");
            if (splats != null) s.Splats = !string.Equals(splats.Trim(), "off", StringComparison.OrdinalIgnoreCase) && splats.Trim() != "0" && !string.Equals(splats.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            if (int.TryParse(VrArgs.Get("-hmsplatmax"), out int smax)) s.SplatMax = smax;
            if (float.TryParse(VrArgs.Get("-hmsplatsigma"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ssig)) s.SplatMaxSigma = ssig;
            string spath = VrArgs.Get("-hmsplatpath");
            if (spath != null) s.SplatPath = spath.Trim().ToLowerInvariant();
            string sdepth = VrArgs.Get("-hmsplatdepth");
            if (sdepth != null) s.SplatDepth = sdepth.Trim().ToLowerInvariant();
            Current = s;
            return s;
        }

        static void ReadProfile(JObject j, ref HairProfile p)
        {
            if (j == null) return;
            if (j["lod_scale"] != null) p.LodScale = Mathf.Clamp(j.Value<float>("lod_scale"), 0.2f, 8f);
            if (j["min_lod"] != null) p.MinLod = Mathf.Clamp(j.Value<int>("min_lod"), 0, 2);
            if (j["tips"] != null) p.Tips = j.Value<bool>("tips");
        }

        public static HairLevel ParseLevel(string s, HairLevel fallback)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "full": return HairLevel.Full;
                case "light": return HairLevel.Light;
                case "off": return HairLevel.Off;
                default: return fallback;
            }
        }

        /// <summary>write the keys the menu changes (hair level, display) back, keeping every other key of the file</summary>
        public void Save()
        {
            try
            {
                string path = FilePath;
                JObject j = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
                j["hair"] = Hair.ToString().ToLowerInvariant();
                j["display"] = Display;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, j.ToString(Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogWarning("VrSettings: could not save quest_settings.json: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Command-line switches for tests on a headset without hands (adb shell am start ... -e unity "-hmplay recap -hmseek 120 -hmhair light"):
    /// -hmplay &lt;film id&gt; starts a lesson by itself, -hmseek &lt;seconds&gt; jumps there a few seconds after it started, -hmhair full|light|off, -hmview start|front|side|overhead|phone:N (a vantage point after the seek; immersive display), -hmsplats on|off (VrSettings).
    /// </summary>
    public static class VrArgs
    {
        static string[] fileArgs;

        /// <summary>the switches of a test run: the command line, else hm_data/quest_args.txt (words, one switch and its value after another: adb push a file; the Android intent extra is not always delivered)</summary>
        public static string Get(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }

            if (fileArgs == null)
            {
                try
                {
                    string path = Path.Combine(HmData.Root, "quest_args.txt");
                    fileArgs = File.Exists(path) ? File.ReadAllText(path).Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries) : new string[0];
                    if (fileArgs.Length > 0) Debug.Log("VrArgs: " + string.Join(" ", fileArgs) + " (from quest_args.txt)");
                }
                catch (Exception)
                {
                    fileArgs = new string[0];
                }
            }

            for (int i = 0; i < fileArgs.Length - 1; i++)
            {
                if (string.Equals(fileArgs[i], name, StringComparison.OrdinalIgnoreCase)) return fileArgs[i + 1];
            }

            return null;
        }
    }

    /// <summary>which headset this is (the tiers differ: a Quest 3 has about twice the GPU of a Quest 2)</summary>
    public static class VrDevice
    {
        public static string Model => SystemInfo.deviceModel ?? "";

        /// <summary>Quest 3 / 3S and anything newer; false for a Quest 2 and on a PC</summary>
        public static bool Quest3Class
        {
            get
            {
                string m = (Model + " " + (SystemInfo.deviceName ?? "")).ToLowerInvariant();
                return m.Contains("quest 3") || m.Contains("quest3") || m.Contains("eureka") || m.Contains("panther") || m.Contains("quest pro") || m.Contains("questpro");
            }
        }
    }
}
