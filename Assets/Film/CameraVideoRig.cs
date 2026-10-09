using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The source phones inside the viewer (VIEWER_SPEC 3.4 / 5.3), driven by the static FilmCameraVideo class:
///   - frustum glyphs with live thumbnails of every phone (the from-above camera overview),
///   - a phone's own frame as a quad on its image plane: seen from the phone's pose (the director puts the render camera
///     there, vertical FOV = the phone's per-frame zoom) it fills the frame at full height, in 16:9 centred with the 3D
///     continuing at the sides. 3d_over_video draws it first (Background queue, ZTest Always, no depth write), so the
///     skeletons, avatars, floor marks and annotations all paint over the video; video_over_3d draws it last.
/// Frames are decoded from the capture's cameras/&lt;id&gt;.full.hmj / .thumb.hmj strips by TIME (the frame nearest to
/// the dance time the director passes in), so under the Recorder's frame-locked clock every film frame shows the same
/// image every time: no VideoPlayer, no decode thread, no async seek. Speed 0 holds a frame, slow motion steps through
/// the source frames. The phone's pose and its picture are answered by ONE test (CheckVideo / CameraVideoTrack.TryAt), so
/// TryPose, SetVideo and the glyphs cannot disagree; when the picture is not drawn, LastVideoWhyNot says why.
/// </summary>
public sealed class CameraVideoRig : MonoBehaviour
{
    // same palette as the director's fallback glyphs (FilmCameraSource): the switch between them is invisible
    static readonly Color[] PhoneColours =
    {
        new(1f, 0.42f, 0.33f), new(1f, 0.72f, 0.2f), new(0.95f, 0.95f, 0.3f), new(0.45f, 0.95f, 0.45f), new(0.3f, 0.9f, 0.9f),
        new(0.35f, 0.62f, 1f), new(0.68f, 0.5f, 1f), new(1f, 0.5f, 0.85f), new(0.85f, 0.85f, 0.85f)
    };

    /// <summary>a frame further than this from the requested time is not shown (the phone was not recording)</summary>
    public const float MaxFrameGapSeconds = 0.25f;
    /// <summary>distance of the video quad from the phone along its optical axis (m); any value inside the render
    /// camera's near/far planes projects identically</summary>
    public const float ImagePlaneMetres = 1.0f;
    /// <summary>pose queries tolerate this much time outside the track (held end pose)</summary>
    public const float PoseToleranceSeconds = 0.5f;

    sealed class Phone
    {
        public string Id;
        public CameraVideoTrack Track;
        public string FullPath, ThumbPath;
        public CameraVideoStrip Full, Thumb;
        public Texture2D FullTexture, ThumbTexture;
        public int FullIndex = -1, ThumbIndex = -1;
        public Color Colour;
        public MeshRenderer ThumbRenderer;
        public Mesh ThumbMesh;
        public Material ThumbMaterial;
    }

    readonly Dictionary<string, Phone> phones = new();
    readonly List<string> ids = new();
    readonly Vector3[] quad = new Vector3[4];
    readonly Vector2[] uvNormal = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
    readonly Vector2[] uvMirror = { new(1, 0), new(0, 0), new(0, 1), new(1, 1) };
    Shader shader;
    GlowMesh lines;
    Material lineMaterial;
    MeshRenderer videoRenderer;
    Mesh videoMesh;
    Material videoMaterial;
    string videoPhone;
    int videoQueue = -1;
    int lastGlyphFrame = -100, lastVideoFrame = -100;

    public bool Loaded { get; private set; }
    public string Folder { get; private set; }
    public string Status { get; private set; } = "not loaded";
    public string[] Ids => ids.ToArray();
    /// <summary>the dance time and phone of the last video frame shown (diagnostics)</summary>
    public string LastVideoCamera { get; private set; }
    public double LastVideoFrameTime { get; private set; } = double.NaN;
    public int LastVideoFrameIndex { get; private set; } = -1;
    public float LastVideoOpacity { get; private set; }
    /// <summary>why the last SetVideo drew nothing (null = it drew the frame, or nothing was asked for)</summary>
    public string LastVideoWhyNot { get; private set; }
    public bool VideoVisible => videoRenderer != null && videoRenderer.enabled;
    public int GlyphsDrawn { get; private set; }

    public static CameraVideoRig Create()
    {
        GameObject go = new("FilmCameraVideo");
        // edit-mode callers (checks without Play mode) must not dirty a scene
        if (!Application.isPlaying) go.hideFlags = HideFlags.HideAndDontSave;
        return go.AddComponent<CameraVideoRig>();
    }

    public static Color ColourOf(string id)
    {
        int n = int.TryParse(id, out int v) ? v : Math.Abs(id?.GetHashCode() ?? 0);
        return PhoneColours[Mathf.Abs(n - 1) % PhoneColours.Length];
    }

    // ------------------------------------------------------------------ loading

    /// <summary>read &lt;capture&gt;/cameras/cameras.json and every phone's track (strips open on first use)</summary>
    public void Load(string captureFolder)
    {
        Unload();
        Folder = captureFolder;
        if (FilmCameraVideo.IgnoreExport)
        {
            Status = "camera export ignored (FilmCameraVideo.IgnoreExport, a test switch)";
            Loaded = true;
            return;
        }

        string dir = Path.Combine(captureFolder ?? "", "cameras");
        string manifest = Path.Combine(dir, "cameras.json");
        if (!File.Exists(manifest))
        {
            Status = $"no camera export in {dir} (python -m dancecap.camera_video)";
            Loaded = true;
            return;
        }

        try
        {
            JObject m = JObject.Parse(File.ReadAllText(manifest));
            foreach (JToken c in m["cameras"] ?? new JArray())
            {
                string id = c.Value<string>("id");
                string trackFile = c["track"]?.Value<string>("file");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(trackFile) || !File.Exists(Path.Combine(dir, trackFile))) continue;
                CameraVideoTrack tr = CameraVideoTrack.Load(Path.Combine(dir, trackFile));
                if (tr.Count == 0) continue;
                tr.Id = id;
                Phone p = new() { Id = id, Track = tr, Colour = ColourOf(id) };
                string full = c["full"]?.Value<string>("file"), thumb = c["thumb"]?.Value<string>("file");
                if (full != null && File.Exists(Path.Combine(dir, full))) p.FullPath = Path.Combine(dir, full);
                if (thumb != null && File.Exists(Path.Combine(dir, thumb))) p.ThumbPath = Path.Combine(dir, thumb);
                phones[id] = p;
                ids.Add(id);
            }

            ids.Sort(StringComparer.Ordinal);
            int withVideo = 0;
            foreach (Phone p in phones.Values)
            {
                if (p.FullPath != null) withVideo++;
            }

            Status = $"{ids.Count} phones, {withVideo} with full-frame video";
        }
        catch (Exception e)
        {
            Status = $"failed: {e.Message}";
            Debug.LogWarning($"FilmCameraVideo: camera export unreadable ({e.Message}); the director falls back to its own tracks");
            phones.Clear();
            ids.Clear();
        }

        Loaded = true;
    }

    void Unload()
    {
        Loaded = false;
        HideVideo();
        HideGlyphs();
        foreach (Phone p in phones.Values) DisposePhone(p);
        phones.Clear();
        ids.Clear();
        videoPhone = null;
    }

    static void DisposePhone(Phone p)
    {
        p.Full?.Dispose();
        p.Thumb?.Dispose();
        if (p.FullTexture != null) Destroy(p.FullTexture);
        if (p.ThumbTexture != null) Destroy(p.ThumbTexture);
        if (p.ThumbMesh != null) Destroy(p.ThumbMesh);
        if (p.ThumbMaterial != null) Destroy(p.ThumbMaterial);
        if (p.ThumbRenderer != null) Destroy(p.ThumbRenderer.gameObject);
        p.Full = p.Thumb = null;
    }

    Shader VideoShader
    {
        get
        {
            if (shader == null) shader = Resources.Load<Shader>("HM_CameraVideo");
            if (shader == null) shader = Shader.Find("HeadMovement/CameraVideo");
            return shader;
        }
    }

    static Texture2D NewTexture(string name) =>
        new(2, 2, TextureFormat.RGB24, false, false)
        {
            name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave
        };

    Material NewMaterial(string name, int queue, CompareFunction zTest)
    {
        Material m = new(VideoShader) { name = name, hideFlags = HideFlags.HideAndDontSave };
        m.renderQueue = queue;
        m.SetFloat("_ZTest", (float)zTest);
        m.SetFloat("_Alpha", 1f);
        return m;
    }

    static MeshRenderer NewQuad(string name, Transform parent, Mesh mesh, Material material)
    {
        GameObject go = new(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        r.enabled = false;
        return r;
    }

    static Mesh NewQuadMesh(string name)
    {
        Mesh m = new() { name = name, hideFlags = HideFlags.HideAndDontSave };
        m.MarkDynamic();
        m.vertices = new Vector3[4];
        m.uv = new Vector2[4];
        m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        m.bounds = new Bounds(Vector3.zero, new Vector3(2000, 2000, 2000));
        return m;
    }

    void EnsureVideoLayer()
    {
        if (videoRenderer != null) return;
        videoMesh = NewQuadMesh("Film camera video");
        videoMaterial = NewMaterial("Film camera video", 1000, CompareFunction.Always);
        videoQueue = 1000;
        videoRenderer = NewQuad("Film camera video", transform, videoMesh, videoMaterial);
    }

    void EnsureGlyphs()
    {
        if (lines != null) return;
        lineMaterial = GlowMesh.NewMaterial("Film camera video glyphs", 1.6f);
        lines = GlowMesh.Create("Film camera video glyph lines", transform, lineMaterial);
        lines.SetVisible(false);
    }

    // ------------------------------------------------------------------ pose

    /// <summary>a phone's pose at a dance time in Unity WORLD space (DanceOrigin applied)</summary>
    public bool TryPose(string camera, float dance, out Vector3 pos, out Quaternion rot, out float vfov)
    {
        pos = Vector3.zero;
        rot = Quaternion.identity;
        vfov = 60f;
        if (camera == null || !phones.TryGetValue(camera, out Phone p)) return false;
        if (!p.Track.TryAt(dance, PoseToleranceSeconds, out pos, out rot, out vfov)) return false;
        pos += DanceOrigin.Offset;
        return true;
    }

    public bool TrySize(string camera, out Vector2 size)
    {
        size = Vector2.zero;
        if (camera == null || !phones.TryGetValue(camera, out Phone p)) return false;
        size = p.Track.Size;
        return true;
    }

    /// <summary>the strip frame shown for a time: index, frame time (NaN without a strip)</summary>
    public bool TryFrameFor(string camera, float dance, bool thumb, out int index, out double frameTime)
    {
        index = -1;
        frameTime = double.NaN;
        if (camera == null || !phones.TryGetValue(camera, out Phone p)) return false;
        CameraVideoStrip s = OpenStrip(p, thumb);
        if (s == null) return false;
        index = s.Nearest(dance);
        frameTime = s.Time[index];
        return true;
    }

    static CameraVideoStrip OpenStrip(Phone p, bool thumb)
    {
        try
        {
            if (thumb)
            {
                if (p.Thumb == null && p.ThumbPath != null) p.Thumb = CameraVideoStrip.Open(p.ThumbPath);
                return p.Thumb;
            }

            if (p.Full == null && p.FullPath != null) p.Full = CameraVideoStrip.Open(p.FullPath);
            return p.Full;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"FilmCameraVideo: {(thumb ? p.ThumbPath : p.FullPath)} unreadable ({e.Message})");
            if (thumb) p.ThumbPath = null;
            else p.FullPath = null;
            return null;
        }
    }

    // ------------------------------------------------------------------ the full-frame video layer

    /// <summary>null = the phone's video would be shown at this dance time; else the reason it would not (nothing here
    /// draws anything: the same test SetVideo applies, for sweeps and the director's warnings)</summary>
    public string VideoWhyNot(string camera, float dance) => CheckVideo(camera, dance, out _, out _, out _, out _, out _, out _);

    /// <summary>the phone has a full-frame video in this capture's export (a track alone shows only its point of view)</summary>
    public bool HasVideo(string camera) => camera != null && phones.TryGetValue(camera, out Phone p) && p.FullPath != null;

    string CheckVideo(string camera, float dance, out Phone p, out CameraVideoStrip strip, out int idx, out Vector3 pos, out Quaternion rot, out float vfov)
    {
        strip = null;
        idx = -1;
        pos = Vector3.zero;
        rot = Quaternion.identity;
        vfov = 60f;
        p = null;
        if (string.IsNullOrEmpty(camera)) return "no phone named";
        if (!phones.TryGetValue(camera, out p)) return $"phone {camera} is not in this capture's camera export";
        strip = OpenStrip(p, false);
        if (strip == null) return $"phone {camera} has no full-frame video in this capture's camera export";
        // ONE rule for the pose and the picture (the director asks TryPose for the camera and SetVideo for the frame)
        if (!p.Track.TryAt(dance, PoseToleranceSeconds, out pos, out rot, out vfov)) return $"no pose for phone {camera}: {p.Track.WhyNot(dance, PoseToleranceSeconds)}";
        idx = strip.Nearest(dance);
        double gap = strip.Time[idx] - dance;
        if (Math.Abs(gap) > MaxFrameGapSeconds) return $"no video frame of phone {camera} within {MaxFrameGapSeconds:0.00} s of dance {dance:0.00} s (nearest {strip.Time[idx]:0.00} s)";
        return null;
    }

    public void SetVideo(string camera, float opacity, float dance, string composite)
    {
        lastVideoFrame = Time.frameCount;
        if (string.IsNullOrEmpty(camera) || opacity <= 0.001f)
        {
            LastVideoWhyNot = null;
            HideVideo();
            return;
        }

        string why = CheckVideo(camera, dance, out Phone p, out CameraVideoStrip strip, out int idx, out Vector3 pos, out Quaternion rot, out float vfov);
        if (why != null)
        {
            LastVideoWhyNot = why;
            HideVideo();
            return;
        }

        if (p.FullTexture == null) p.FullTexture = NewTexture($"phone {p.Id} frame");
        if (idx != p.FullIndex)
        {
            if (!strip.Read(idx, p.FullTexture))
            {
                LastVideoWhyNot = $"frame {idx} of phone {p.Id} could not be decoded";
                HideVideo();
                return;
            }

            p.FullIndex = idx;
        }

        LastVideoWhyNot = null;
        EnsureVideoLayer();
        bool over = string.Equals(composite, "video_over_3d", StringComparison.OrdinalIgnoreCase);
        int queue = over ? 4000 : 1000;
        if (queue != videoQueue)
        {
            videoMaterial.renderQueue = queue;
            videoQueue = queue;
        }

        pos += DanceOrigin.Offset;
        float d = ImagePlaneMetres, hh = d * Mathf.Tan(vfov * 0.5f * Mathf.Deg2Rad), hw = hh * p.Track.Aspect;
        Vector3 c = pos + rot * Vector3.forward * d, right = rot * Vector3.right * hw, up = rot * Vector3.up * hh;
        quad[0] = c - right - up;
        quad[1] = c + right - up;
        quad[2] = c + right + up;
        quad[3] = c - right + up;
        videoMesh.vertices = quad;
        videoMesh.uv = uvNormal;
        videoMesh.bounds = new Bounds(c, new Vector3(1000, 1000, 1000));
        if (videoPhone != p.Id)
        {
            videoMaterial.mainTexture = p.FullTexture;
            videoPhone = p.Id;
        }

        videoMaterial.SetFloat("_Alpha", Mathf.Clamp01(opacity));
        videoRenderer.enabled = true;
        LastVideoCamera = p.Id;
        LastVideoFrameTime = strip.Time[idx];
        LastVideoFrameIndex = idx;
        LastVideoOpacity = opacity;
    }

    public void HideVideo()
    {
        if (videoRenderer != null && videoRenderer.enabled) videoRenderer.enabled = false;
        LastVideoOpacity = 0f;
    }

    // ------------------------------------------------------------------ frustum glyphs + live thumbnails

    public void SetGlyphs(float alpha, float dance)
    {
        lastGlyphFrame = Time.frameCount;
        if (alpha <= 0.01f || phones.Count == 0)
        {
            HideGlyphs();
            return;
        }

        EnsureGlyphs();
        Camera view = DanceText.ViewCamera;
        Vector3 eye = view != null ? view.transform.position : Vector3.zero;
        float viewTan = Mathf.Tan((view != null ? view.fieldOfView : 60f) * 0.5f * Mathf.Deg2Rad);
        lines.SetVisible(true);
        lines.Viewer = view != null ? eye : null;
        lines.Begin();
        int drawn = 0;
        foreach (string id in ids)
        {
            Phone p = phones[id];
            if (!p.Track.TryAt(dance, PoseToleranceSeconds, out Vector3 pos, out Quaternion rot, out float fov))
            {
                SetThumbVisible(p, false);
                continue;
            }

            pos += DanceOrigin.Offset;
            float dist = view != null ? Vector3.Distance(eye, pos) : 10f;
            // about 11 % of the view height whatever the distance (the overview is from 10 m up), never tiny close by
            float depth = Mathf.Clamp(0.11f * dist * viewTan / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), 0.4f, 3.2f);
            float hh = depth * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), hw = hh * p.Track.Aspect;
            Vector3 fw = rot * Vector3.forward, up = rot * Vector3.up, rt = rot * Vector3.right;
            Vector3 cc = pos + fw * depth;
            Vector3 c0 = cc + up * hh - rt * hw, c1 = cc + up * hh + rt * hw, c2 = cc - up * hh + rt * hw, c3 = cc - up * hh - rt * hw;
            Color col = p.Colour * alpha;
            float w = Mathf.Clamp(0.03f * depth, 0.02f, 0.09f);
            lines.Line(pos, c0, w, col * 0.8f);
            lines.Line(pos, c1, w, col * 0.8f);
            lines.Line(pos, c2, w, col * 0.8f);
            lines.Line(pos, c3, w, col * 0.8f);
            lines.Line(c0, c1, w * 1.4f, col);
            lines.Line(c1, c2, w * 1.4f, col);
            lines.Line(c2, c3, w * 1.4f, col);
            lines.Line(c3, c0, w * 1.4f, col);
            lines.Sphere(pos, 0.09f * Mathf.Max(1f, depth), col);
            drawn++;
            UpdateThumb(p, dance, alpha, c0, c1, c2, c3, Vector3.Dot(eye - cc, fw) > 0f);
        }

        lines.End();
        GlyphsDrawn = drawn;
    }

    void UpdateThumb(Phone p, float dance, float alpha, Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl, bool mirror)
    {
        CameraVideoStrip strip = OpenStrip(p, true);
        if (strip == null)
        {
            SetThumbVisible(p, false);
            return;
        }

        int idx = strip.Nearest(dance);
        if (Mathf.Abs((float)(strip.Time[idx] - dance)) > MaxFrameGapSeconds)
        {
            SetThumbVisible(p, false);
            return;
        }

        if (p.ThumbTexture == null) p.ThumbTexture = NewTexture($"phone {p.Id} thumbnail");
        if (idx != p.ThumbIndex)
        {
            if (!strip.Read(idx, p.ThumbTexture))
            {
                SetThumbVisible(p, false);
                return;
            }

            p.ThumbIndex = idx;
        }

        if (p.ThumbRenderer == null)
        {
            p.ThumbMesh = NewQuadMesh($"phone {p.Id} thumbnail");
            p.ThumbMaterial = NewMaterial($"phone {p.Id} thumbnail", 3100, CompareFunction.LessEqual);
            p.ThumbMaterial.mainTexture = p.ThumbTexture;
            p.ThumbRenderer = NewQuad($"phone {p.Id} thumbnail", transform, p.ThumbMesh, p.ThumbMaterial);
        }

        quad[0] = bl;
        quad[1] = br;
        quad[2] = tr;
        quad[3] = tl;
        p.ThumbMesh.vertices = quad;
        p.ThumbMesh.uv = mirror ? uvMirror : uvNormal;
        p.ThumbMesh.bounds = new Bounds((tl + br) * 0.5f, new Vector3(1000, 1000, 1000));
        p.ThumbMaterial.SetFloat("_Alpha", Mathf.Clamp01(alpha));
        p.ThumbRenderer.enabled = true;
    }

    static void SetThumbVisible(Phone p, bool on)
    {
        if (p.ThumbRenderer != null && p.ThumbRenderer.enabled != on) p.ThumbRenderer.enabled = on;
    }

    public void HideGlyphs()
    {
        if (lines != null && lines.Visible) lines.SetVisible(false);
        foreach (Phone p in phones.Values) SetThumbVisible(p, false);
        GlyphsDrawn = 0;
    }

    /// <summary>hide everything and let go of the files (the next use re-opens them)</summary>
    public void ReleaseAll()
    {
        HideVideo();
        HideGlyphs();
        foreach (Phone p in phones.Values)
        {
            p.Full?.Close();
            p.Thumb?.Close();
        }
    }

    void LateUpdate()
    {
        // the director calls SetGlyphs / SetVideo every frame while they are shown: stale = stopped
        if (Time.frameCount - lastGlyphFrame > 2) HideGlyphs();
        if (Time.frameCount - lastVideoFrame > 2) HideVideo();
    }

    void OnDestroy()
    {
        foreach (Phone p in phones.Values) DisposePhone(p);
        phones.Clear();
        if (videoMesh != null) Destroy(videoMesh);
        if (videoMaterial != null) Destroy(videoMaterial);
        if (lineMaterial != null) Destroy(lineMaterial);
    }

    public Dictionary<string, object> State()
    {
        List<object> list = new();
        foreach (string id in ids)
        {
            Phone p = phones[id];
            list.Add(new Dictionary<string, object>
            {
                ["id"] = id, ["samples"] = p.Track.Count, ["t0"] = p.Track.T0, ["t1"] = p.Track.T1, ["size"] = new[] { p.Track.Size.x, p.Track.Size.y },
                ["unlocalisedFrames"] = p.Track.UnlocalisedCount, ["longestUnlocalisedRun_s"] = p.Track.LongestRun, ["maxUnlocalised_s"] = p.Track.MaxUnlocalised,
                ["maxSampleGap_s"] = p.Track.MaxSampleGap, ["fixedPosition"] = p.Track.FixedPosition,
                ["fullVideo"] = p.FullPath != null, ["thumbnail"] = p.ThumbPath != null,
                ["fullFrames"] = p.Full != null ? p.Full.Count : -1, ["fullSize"] = p.Full != null ? new[] { p.Full.Width, p.Full.Height } : null
            });
        }

        return new Dictionary<string, object>
        {
            ["loaded"] = Loaded, ["status"] = Status, ["folder"] = Folder, ["phones"] = list, ["glyphsDrawn"] = GlyphsDrawn,
            ["videoVisible"] = VideoVisible, ["videoCamera"] = LastVideoCamera, ["videoFrameIndex"] = LastVideoFrameIndex,
            ["videoFrameTime"] = LastVideoFrameTime, ["videoOpacity"] = LastVideoOpacity, ["videoWhyNot"] = LastVideoWhyNot
        };
    }
}
