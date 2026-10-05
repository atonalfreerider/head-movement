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

    [JsonIgnore] public string Folder;

    public string DisplayName => Path.GetFileName(Folder);

    public string PathOf(string relative) => string.IsNullOrEmpty(relative) ? null : Path.Combine(Folder, relative);

    public string JointsPath(Role role) =>
        PathOf(dancers.First(d => string.Equals(d.role, role.ToString(), StringComparison.OrdinalIgnoreCase)).joints);

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
