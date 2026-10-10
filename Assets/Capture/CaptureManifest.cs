using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Describes one performance folder in StreamingAssets. Reads capture.json (written by atlas_bridge) when present,
/// otherwise falls back to the original layout: video_meta.json, figure1.json, figure2.json, audio.wav and
/// zouk-time-analysis.json (or the "&lt;stem&gt;_zouk-time-analysis.json" name beat_this writes).
/// Version 3 (dancecap export_unity) adds times.json (per-frame reference seconds - playback looks frames up by
/// audio time, never by a constant fps), per-dancer SMPL-X motion + skin binaries, timing.json and physics.json,
/// and optionally per-dancer albedo textures (smplx_albedo), the room mesh (room) and the Quest room splat
/// (environment_splat with its SfM -> Unity matrix).
/// </summary>
public class CaptureManifest
{
    public int version = 1;
    public float fps;
    public int frame_count;
    public float duration;
    public string audio = "audio.wav";

    /// <summary>audio time (seconds) at which pose frame 0 happens</summary>
    public float audio_offset;

    public string beats;
    public List<DancerRef> dancers;
    public SplatRef environment_splat;
    public SplatSequenceRef splat_sequence;
    public string virtual_cameras;

    // ---- version 3 (dancecap) ----
    /// <summary>per-frame reference seconds (T,)</summary>
    public string times;

    /// <summary>audio time = reference time + time_to_audio; absent = audio_offset - times[0]</summary>
    public float? time_to_audio;

    /// <summary>role -> motion binary (transl + 55 local rotations + 55 joint positions per frame)</summary>
    public Dictionary<string, string> smplx;

    /// <summary>role -> shaped SMPL-X skin binary (rest vertices, faces, LBS weights, rest joints, parents)</summary>
    public Dictionary<string, string> smplx_skin;

    /// <summary>role -> photoreal albedo PNG on the SMPL-X UV layout (needs a version-2 seam-split skin)</summary>
    public Dictionary<string, string> smplx_albedo;

    /// <summary>static room mesh for Quest (room_mesh.bin, Unity coordinates, photo textures)</summary>
    public RoomRef room;

    public string timing;
    public string physics;
    public string contacts;
    public string world;

    /// <summary>floorYawDeg: the floor visuals (grid crosses + plane) are turned this many degrees counter-clockwise as seen from
    /// above, about the vertical axis through the dance origin, so they line up with the room in the source videos (0 = world axes).
    /// Written by dancecap export_unity from takes/&lt;take&gt;.toml [export] floor_yaw_deg.</summary>
    public float floor_yaw_deg;

    /// <summary>roleHidden: per-role hidden spans with a fade, in CAPTURE seconds (from the first frame = the viewer HUD's clock):
    /// { "lead": [ { "from": 0.0, "to": 4.0, "fade_in": 1.0 } ] } hides the lead's avatar, skeleton and the overlays derived from his pose
    /// from the first frame up to 4.0 s, then fades them in over 1.0 s. Entries may also be [from, to, fade_in, fade_out] arrays. Written by
    /// dancecap export_unity from takes/&lt;take&gt;.toml [export.role_hidden]; see RoleHiddenSpans.</summary>
    public Newtonsoft.Json.Linq.JObject role_hidden;

    /// <summary>props: head-worn props per ROLE, [ { "role": "follow", "kind": "headset", "side": "left", ...parameters } ] (HeadPropSpec): built
    /// on the avatar's head when the capture loads (Avatar/HeadProps.cs, VIEWER_SPEC 3.2b). Written by dancecap export_unity from
    /// takes/&lt;take&gt;.toml [dancers.&lt;role&gt;.props.&lt;kind&gt;]; absent = no props.</summary>
    public Newtonsoft.Json.Linq.JArray props;

    [JsonIgnore] public string Folder;

    public string DisplayName => Path.GetFileName(Folder);

    public string PathOf(string relative) => string.IsNullOrEmpty(relative) ? null : Path.Combine(Folder, relative);

    public string JointsPath(Role role) =>
        PathOf(dancers.First(d => string.Equals(d.role, role.ToString(), StringComparison.OrdinalIgnoreCase)).joints);

    /// <summary>path of a role-keyed file (smplx / smplx_skin), or null when absent on disk</summary>
    public string RolePath(Dictionary<string, string> map, Role role)
    {
        if (map == null) return null;
        foreach (KeyValuePair<string, string> kv in map)
        {
            if (string.Equals(kv.Key, role.ToString(), StringComparison.OrdinalIgnoreCase)) return OptionalPath(kv.Value);
        }

        return null;
    }

    /// <summary>path of an optional file, or null when it is not declared or missing</summary>
    public string OptionalPath(string relative)
    {
        string path = PathOf(relative);
        return path != null && File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Per-frame audio times. v3: times.json + time_to_audio. Older captures: a uniform grid audio_offset + i / fps.
    /// </summary>
    public CaptureTimeline BuildTimeline(int frameCount)
    {
        string timesPath = OptionalPath(times);
        if (timesPath == null) return CaptureTimeline.Uniform(frameCount, fps, audio_offset);

        double[] reference = JsonConvert.DeserializeObject<double[]>(File.ReadAllText(timesPath));
        double toAudio = time_to_audio ?? (reference.Length > 0 ? audio_offset - reference[0] : 0);
        int n = Mathf.Min(frameCount, reference.Length);
        double[] audioTimes = new double[n];
        for (int i = 0; i < n; i++) audioTimes[i] = reference[i] + toAudio;
        return new CaptureTimeline(audioTimes, (float)toAudio, true);
    }

    public static CaptureManifest Load(string folder)
    {
        string manifestPath = Path.Combine(folder, "capture.json");
        CaptureManifest manifest;
        if (File.Exists(manifestPath))
        {
            manifest = JsonConvert.DeserializeObject<CaptureManifest>(File.ReadAllText(manifestPath));
        }
        else
        {
            HeadMovement.VideoMetadata videoMetadata = JsonConvert.DeserializeObject<HeadMovement.VideoMetadata>(
                File.ReadAllText(Path.Combine(folder, "video_meta.json")));
            manifest = new CaptureManifest
            {
                frame_count = videoMetadata.frame_count,
                duration = videoMetadata.duration
            };
        }

        manifest.Folder = folder;
        manifest.dancers ??= new List<DancerRef>
        {
            new() { role = "lead", joints = "figure1.json" },
            new() { role = "follow", joints = "figure2.json" }
        };
        if (manifest.fps <= 0 && manifest.duration > 0)
        {
            manifest.fps = manifest.frame_count / manifest.duration;
        }

        if (string.IsNullOrEmpty(manifest.beats) || !File.Exists(manifest.PathOf(manifest.beats)))
        {
            manifest.beats = FindBeats(folder);
        }

        return manifest;
    }

    static string FindBeats(string folder)
    {
        if (File.Exists(Path.Combine(folder, "zouk-time-analysis.json"))) return "zouk-time-analysis.json";

        string produced = Directory.GetFiles(folder, "*_zouk-time-analysis.json").FirstOrDefault();
        return produced == null ? null : Path.GetFileName(produced);
    }

    [Serializable]
    public class DancerRef
    {
        public string role;
        public string joints;
    }

    [Serializable]
    public class SplatRef
    {
        public string path;

        /// <summary>row-major 4x4 from the Atlas scene frame to Unity; null = already in Unity space</summary>
        public float[] scene_to_unity;

        public Matrix4x4 SceneToUnity() => ToMatrix(scene_to_unity);
    }

    [Serializable]
    public class RoomRef
    {
        public string mesh;
        public string shading;
        public int triangles;
        public int vertices;
        public List<string> textures;
    }

    [Serializable]
    public class SplatSequenceRef
    {
        public string dir;
        public string pattern = "frame_{0:D5}.ply";
        public float fps;
        public float[] scene_to_unity;

        public Matrix4x4 SceneToUnity() => ToMatrix(scene_to_unity);
    }

    static Matrix4x4 ToMatrix(float[] rowMajor)
    {
        if (rowMajor == null || rowMajor.Length != 16) return Matrix4x4.identity;

        Matrix4x4 m = new();
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                m[r, c] = rowMajor[r * 4 + c];
            }
        }

        return m;
    }
}

/// <summary>
/// Maps audio time to pose frames by binary search over the real per-frame timestamps (nearest frame), so captures
/// with dropped/irregular frames or a non-zero time origin play in sync with the audio.
/// </summary>
public class CaptureTimeline
{
    /// <summary>audio-clock seconds of every pose frame, strictly increasing</summary>
    public readonly double[] AudioTimes;

    /// <summary>audio time = reference time + TimeToAudio</summary>
    public readonly float TimeToAudio;

    /// <summary>true when built from times.json (v3), false for the uniform fps fallback</summary>
    public readonly bool FromTimes;

    public CaptureTimeline(double[] audioTimes, float timeToAudio, bool fromTimes)
    {
        if (audioTimes == null || audioTimes.Length == 0) throw new ArgumentException("timeline needs at least one frame");
        for (int i = 1; i < audioTimes.Length; i++)
        {
            if (!(audioTimes[i] > audioTimes[i - 1])) throw new ArgumentException($"frame times must increase (frame {i})");
        }

        AudioTimes = audioTimes;
        TimeToAudio = timeToAudio;
        FromTimes = fromTimes;
    }

    public static CaptureTimeline Uniform(int frameCount, float fps, float audioOffset)
    {
        double[] t = new double[Mathf.Max(1, frameCount)];
        for (int i = 0; i < t.Length; i++) t[i] = audioOffset + i / (double)fps;
        return new CaptureTimeline(t, 0f, false);
    }

    public int Count => AudioTimes.Length;
    public float First => (float)AudioTimes[0];
    public float Last => (float)AudioTimes[^1];

    public float AudioTimeOf(int frame) => (float)AudioTimes[Mathf.Clamp(frame, 0, AudioTimes.Length - 1)];

    /// <summary>audio time of a reference-clock time (timing.json / physics.json t)</summary>
    public double ToAudio(double referenceTime) => referenceTime + TimeToAudio;

    /// <summary>nearest frame to an audio time (clamped to the capture)</summary>
    public int FrameAt(double audioTime) => Nearest(AudioTimes, audioTime);

    /// <summary>true when an audio time lies within half a frame of the capture's first..last frame</summary>
    public bool Covers(double audioTime)
    {
        double pad = Count > 1 ? 0.5 * (AudioTimes[^1] - AudioTimes[0]) / (Count - 1) : 0;
        return audioTime >= AudioTimes[0] - pad && audioTime <= AudioTimes[^1] + pad;
    }

    /// <summary>index of the sample nearest to x in a strictly increasing array (binary search)</summary>
    public static int Nearest(double[] sorted, double x)
    {
        int n = sorted.Length;
        if (n == 0) return -1;
        if (x <= sorted[0]) return 0;
        if (x >= sorted[n - 1]) return n - 1;
        int lo = 0, hi = n - 1; // sorted[lo] <= x < sorted[hi]
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (sorted[mid] <= x) lo = mid;
            else hi = mid;
        }

        return x - sorted[lo] <= sorted[hi] - x ? lo : hi;
    }

    /// <summary>frame times relative to frame 0 (seconds), for derivatives on the real timestamps</summary>
    public float[] RelativeSeconds()
    {
        float[] t = new float[AudioTimes.Length];
        for (int i = 0; i < t.Length; i++) t[i] = (float)(AudioTimes[i] - AudioTimes[0]);
        return t;
    }
}
