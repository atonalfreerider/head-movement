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
    MeshRenderer videoRenderer, fillRenderer, coverRenderer;
    Mesh videoMesh, fillMesh;
    Material videoMaterial, fillMaterial, coverMaterial;
    string videoPhone, fillPhone;
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

    /// <summary>v3 hand-off cover (FilmDirector, FlowTimeline.VideoCover): the share of the full-frame video drawn a second time OVER every 3D
    /// overlay (queue 4500, ZTest Always). 1 at a phone's arrival (the picture is the video alone, as the frustum surface showed it the frame
    /// before), fading to 0 over overlays_on_top_in_s - the skeleton lines, arrows and floor marks registered on the video fade in together -
    /// and back to 1 before the departure. 0 (the default) = no cover: older films unchanged.</summary>
    public float VideoCover;

    /// <summary>the frustum surfaces drawn by the last SetSurfaces (diagnostics, the face sidecar): phone, alpha, world corners top-left,
    /// top-right, bottom-right, bottom-left of the phone's image</summary>
    public readonly List<(string phone, float alpha, Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl)> SurfacesDrawn = new();

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
    /// <summary>start parsing every phone's track file on worker threads (the headset's lesson load); Load takes the results</summary>
    public static void PrefetchTracks(string captureFolder)
    {
        try
        {
            string dir = Path.Combine(captureFolder ?? "", "cameras");
            string manifest = Path.Combine(dir, "cameras.json");
            if (!File.Exists(manifest)) return;
            JObject m = JObject.Parse(File.ReadAllText(manifest));
            foreach (JToken c in m["cameras"] ?? new JArray())
            {
                string trackFile = c["track"]?.Value<string>("file");
                if (string.IsNullOrEmpty(trackFile)) continue;
                string trackPath = Path.Combine(dir, trackFile);
                if (File.Exists(trackPath)) LoadPrefetch.Start("camtrack:" + trackPath, () => CameraVideoTrack.Load(trackPath));
            }
        }
        catch (Exception)
        {
            // the load itself reports what is wrong
        }
    }

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
                string trackPath = Path.Combine(dir, trackFile);
                CameraVideoTrack tr = LoadPrefetch.Take("camtrack:" + trackPath, () => CameraVideoTrack.Load(trackPath));
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
        fillPhone = null;
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
        SetCover(p, opacity * Mathf.Clamp01(VideoCover));
        SetSideFill(p, opacity * (SideFillUnderCover ? 1f : 1f - Mathf.Clamp01(VideoCover)), over, pos, rot, hh, hw, c);
        LastVideoCamera = p.Id;
        LastVideoFrameTime = strip.Time[idx];
        LastVideoFrameIndex = idx;
        LastVideoOpacity = opacity;
    }

    public void HideVideo()
    {
        if (videoRenderer != null && videoRenderer.enabled) videoRenderer.enabled = false;
        if (fillRenderer != null && fillRenderer.enabled) fillRenderer.enabled = false;
        if (coverRenderer != null && coverRenderer.enabled) coverRenderer.enabled = false;
        LastVideoOpacity = 0f;
    }

    /// <summary>the video quad drawn again over everything (VideoCover): same mesh, texture and projection as the backdrop</summary>
    void SetCover(Phone p, float alpha)
    {
        if (alpha <= 0.001f)
        {
            if (coverRenderer != null && coverRenderer.enabled) coverRenderer.enabled = false;
            return;
        }

        if (coverRenderer == null)
        {
            coverMaterial = NewMaterial("Film camera video cover", CoverQueue, CompareFunction.Always);
            coverRenderer = NewQuad("Film camera video cover", transform, videoMesh, coverMaterial);
        }

        coverMaterial.mainTexture = p.FullTexture;
        coverMaterial.SetFloat("_Alpha", Mathf.Clamp01(alpha));
        coverRenderer.enabled = true;
    }

    /// <summary>true while the side fill is drawn behind the phone's picture (diagnostics)</summary>
    public bool SideFillVisible => fillRenderer != null && fillRenderer.enabled;

    /// <summary>Portrait phone clip in the 16:9 output (opt-in: the direction's horizontal preset `phone_side_fill` = brightness 0..1, 0 = off): the clip
    /// stays a centred panel with the 3D registered on it, and the empty sides show the same frame magnified to cover the frame, blurred and dimmed.
    /// A second quad on the same image plane, drawn just before the picture; nothing changes in the 9:16 output or without the switch.</summary>
    void SetSideFill(Phone p, float opacity, bool videoOver, Vector3 pos, Quaternion rot, float hh, float hw, Vector3 centre)
    {
        FilmDirector fd = FilmDirector.Instance;
        float dim = fd != null && !fd.Vertical ? fd.AspectInfo.PhoneSideFill : 0f;
        if (dim <= 0.001f || videoOver || p.Track.Aspect >= 1.5f)
        {
            if (fillRenderer != null && fillRenderer.enabled) fillRenderer.enabled = false;
            return;
        }

        if (fillRenderer == null)
        {
            fillMesh = NewQuadMesh("Film camera video side fill");
            fillMaterial = NewMaterial("Film camera video side fill", 999, CompareFunction.Always);
            fillRenderer = NewQuad("Film camera video side fill", transform, fillMesh, fillMaterial);
        }

        // the quad covers a 16:9 view of the phone's own lens at any zoom >= 1 (with a margin for the crop's turn); the picture magnified to cover it
        float halfH = 1.3f * hh, halfW = halfH * 16f / 9f * 1.05f;
        Vector3 right = rot * Vector3.right, up = rot * Vector3.up;
        quad[0] = centre - right * halfW - up * halfH;
        quad[1] = centre + right * halfW - up * halfH;
        quad[2] = centre + right * halfW + up * halfH;
        quad[3] = centre - right * halfW + up * halfH;
        float scale = halfW / hw;                      // the picture's width over the quad's width
        float fv = Mathf.Clamp01(halfH / (hh * scale)); // the share of the picture's height the quad shows
        const float vCentre = 0.46f;
        float v0 = Mathf.Clamp(vCentre - fv * 0.5f, 0f, 1f - fv), v1 = v0 + fv;
        fillMesh.vertices = quad;
        fillMesh.uv = new Vector2[] { new(0, v0), new(1, v0), new(1, v1), new(0, v1) };
        fillMesh.bounds = new Bounds(centre, new Vector3(1000, 1000, 1000));
        if (fillPhone != p.Id)
        {
            fillMaterial.mainTexture = p.FullTexture;
            fillPhone = p.Id;
        }

        // blur radius in texture units (about 5 % of the picture's width: the magnified frame is already soft), isotropic on the screen
        fillMaterial.SetVector("_BlurUV", new Vector4(0.05f, 0.05f * p.Track.Aspect, 0f, 0f));
        fillMaterial.SetFloat("_Dim", Mathf.Clamp01(dim));
        fillMaterial.SetFloat("_Alpha", Mathf.Clamp01(opacity));
        fillRenderer.enabled = true;
    }

    // ------------------------------------------------------------------ v3 frustum surfaces (RECAP_FILM.md "What the Unity director must implement" 3)

    /// <summary>queue of an opaque-able frustum surface: after the splat composite, the over-the-splats lines and the transparent queue (a line
    /// behind a fading surface is dimmed by it, not drawn over it), before the overlay queue</summary>
    public const int SurfaceQueue = 3100;
    /// <summary>queue of the hand-off covers (surface top copy, video cover): over every 3D overlay incl. the call-outs (Overlay + 10)</summary>
    public const int CoverQueue = 4500;

    sealed class SurfaceView
    {
        public Mesh Mesh, FillMesh;
        public Material Mat, TopMat, FillMat;
        public MeshRenderer Renderer, Top, Fill;
    }

    /// <summary>v4, 16:9 only: the side fill's alpha per surface of the next SetSurfaces (0 = none). A portrait phone's picture in its frustum
    /// gets the blurred, dimmed side fill of the full-frame view around it, in the picture's own plane: seen from the phone it is exactly the
    /// full-frame side fill, so the departure / arrival frame does not change, and it fades over the WHOLE way (the independent check of the
    /// v3 films: the side fills went to black in about 4 frames at every departure)</summary>
    public float[] SurfaceFillAlpha;

    /// <summary>v4: the full-frame view's side fill stays at the video's opacity under the hand-off cover (v3 faded it with the cover)</summary>
    public bool SideFillUnderCover;

    readonly Dictionary<string, SurfaceView> surfaceViews = new();
    GlowMesh frustumLines;
    Material frustumLineMaterial;
    int lastSurfaceFrame = -100;

    /// <summary>a phone's image rectangle at depth (m) along its optical axis at a dance time: world corners of the image's top-left, top-right,
    /// bottom-right, bottom-left (the full frame: the phone's own vertical FOV and aspect, not the crop); false without a pose</summary>
    public bool SurfaceCorners(string camera, float dance, float depth, out Vector3 apex, out Vector3 tl, out Vector3 tr, out Vector3 br, out Vector3 bl)
    {
        apex = tl = tr = br = bl = Vector3.zero;
        if (camera == null || !phones.TryGetValue(camera, out Phone p)) return false;
        if (!p.Track.TryAt(dance, PoseToleranceSeconds, out Vector3 pos, out Quaternion rot, out float vfov)) return false;
        pos += DanceOrigin.Offset;
        float hh = depth * Mathf.Tan(vfov * 0.5f * Mathf.Deg2Rad), hw = hh * p.Track.Aspect;
        Vector3 c = pos + rot * Vector3.forward * depth, right = rot * Vector3.right * hw, up = rot * Vector3.up * hh;
        apex = pos;
        tl = c - right + up;
        tr = c + right + up;
        br = c + right - up;
        bl = c - right - up;
        return true;
    }

    /// <summary>
    /// The phones' frustums with their live video standing in the 3D world (the ways into and out of a phone's point of view): for each phone, the
    /// frustum lines (apex at the phone, the four edges to the corners of its full image rectangle at depth m along the optical axis, and the rim)
    /// in the phone's colour, and its live full strip frame (the frame nearest the dance time) on that rectangle at alpha. At alpha 1 the
    /// rectangle is opaque and writes depth (what stands behind it is hidden); while fading it is alpha-blended. top[i] = the share of it drawn
    /// again over every overlay (the hand-off cover). Seen from the phone's own pose the rectangle projects exactly onto the full-frame video
    /// quad (SetVideo): the switch between them does not change the picture. Called every frame while any is shown.
    /// </summary>
    public void SetSurfaces(IList<string> cameras, IList<float> alphas, IList<float> tops, IList<float> depths, float dance) =>
        SetSurfaces(cameras, alphas, tops, depths, dance, null);

    /// <summary>SetSurfaces with the frustum lines' own alphas (v4: the outline curve, from the faint ghost's 0.15 up to 1 on the way into a
    /// phone and back down on the way out; the picture keeps its own alpha). outlines null = the lines follow the picture (v3).</summary>
    public void SetSurfaces(IList<string> cameras, IList<float> alphas, IList<float> tops, IList<float> depths, float dance, IList<float> outlines)
    {
        lastSurfaceFrame = Time.frameCount;
        SurfacesDrawn.Clear();
        HashSet<string> shown = null;
        if (frustumLines == null)
        {
            frustumLineMaterial = GlowMesh.NewMaterial("Film frustum lines", 1.6f);
            frustumLineMaterial.renderQueue = SurfaceQueue + 10;
            frustumLines = GlowMesh.Create("Film frustum lines", transform, frustumLineMaterial);
        }

        Camera view = DanceText.ViewCamera;
        frustumLines.Viewer = view != null ? view.transform.position : null;
        frustumLines.Begin();
        for (int i = 0; i < cameras.Count; i++)
        {
            string cam = cameras[i];
            float alpha = Mathf.Clamp01(alphas[i]);
            float outline = outlines != null && i < outlines.Count ? Mathf.Clamp01(outlines[i]) : -1f;
            float depth = depths != null && i < depths.Count && depths[i] > 0.05f ? depths[i] : 0.9f;
            if ((alpha <= 1e-4f && outline <= 1e-4f) || !SurfaceCorners(cam, dance, depth, out Vector3 apex, out Vector3 tl, out Vector3 tr, out Vector3 br, out Vector3 bl)) continue;
            Vector3 eyeNow = view != null ? view.transform.position : apex + (tl - apex) * 10f;
            // an eye passing close beside or in front of the picture (a way that swings round it) sees it dissolve instead of sweeping across
            // the lens as a hard wipe: alpha x smoothstep(0.25 .. 0.9 m from the rectangle). Not on the phone's side of the picture inside its
            // outline (the dolly along the axis and the hand-off, where it must stay opaque)
            alpha *= ProximityFade(eyeNow, apex, tl, tr, br, bl);
            float lineAlpha = outline >= 0f ? outline : alpha;
            if (alpha <= 1e-4f && lineAlpha <= 1e-4f) continue;
            Color col = ColourOf(cam) * lineAlpha;
            // line widths about constant on screen (thin close by: a 1.2 cm line half a metre from the lens is a bar across the picture)
            float W(Vector3 m) => Mathf.Clamp(0.0045f * Vector3.Distance(eyeNow, m), 0.002f, 0.012f);
            // the four edges meet at the phone: as the eye comes within 2 m of it (the dolly along the optical axis) they would pass next to the
            // lens - they fade out there (the rim stays: it frames the picture to the end)
            float near = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.6f, 2.0f, Vector3.Distance(eyeNow, apex)));
            if (near > 0.002f)
            {
                Color edge = col * (0.8f * near);
                frustumLines.Line(apex, tl, W((apex + tl) * 0.5f), edge);
                frustumLines.Line(apex, tr, W((apex + tr) * 0.5f), edge);
                frustumLines.Line(apex, br, W((apex + br) * 0.5f), edge);
                frustumLines.Line(apex, bl, W((apex + bl) * 0.5f), edge);
                frustumLines.Sphere(apex, Mathf.Clamp(0.008f * Vector3.Distance(eyeNow, apex), 0.008f, 0.035f), col * near);
            }

            float w = W((tl + br) * 0.5f);

            // the rim a centimetre toward the apex (never coplanar with the picture); it goes with the hand-off cover (at the phone's pose it lies on
            // the frame's edge, half outside the cover)
            Vector3 Rim(Vector3 v) => Vector3.Lerp(v, apex, 0.012f);
            Color rim = col * (1f - Mathf.Clamp01(tops != null && i < tops.Count ? tops[i] : 0f));
            frustumLines.Line(Rim(tl), Rim(tr), w * 1.3f, rim);
            frustumLines.Line(Rim(tr), Rim(br), w * 1.3f, rim);
            frustumLines.Line(Rim(br), Rim(bl), w * 1.3f, rim);
            frustumLines.Line(Rim(bl), Rim(tl), w * 1.3f, rim);

            // the picture: the same frame the full-frame video would show (same texture, same frame index)
            if (alpha <= 1e-4f) continue;
            string why = CheckVideo(cam, dance, out Phone p, out CameraVideoStrip strip, out int idx, out _, out _, out _);
            if (why != null) continue;
            if (p.FullTexture == null) p.FullTexture = NewTexture($"phone {p.Id} frame");
            if (idx != p.FullIndex)
            {
                if (!strip.Read(idx, p.FullTexture)) continue;
                p.FullIndex = idx;
            }

            if (!surfaceViews.TryGetValue(cam, out SurfaceView sv))
            {
                sv = new SurfaceView { Mesh = NewQuadMesh($"phone {cam} frustum surface") };
                sv.Mat = NewMaterial($"phone {cam} frustum surface", SurfaceQueue, CompareFunction.LessEqual);
                sv.TopMat = NewMaterial($"phone {cam} frustum surface cover", CoverQueue, CompareFunction.Always);
                sv.Renderer = NewQuad($"phone {cam} frustum surface", transform, sv.Mesh, sv.Mat);
                sv.Top = NewQuad($"phone {cam} frustum surface cover", transform, sv.Mesh, sv.TopMat);
                surfaceViews[cam] = sv;
            }

            quad[0] = bl;
            quad[1] = br;
            quad[2] = tr;
            quad[3] = tl;
            sv.Mesh.vertices = quad;
            sv.Mesh.uv = uvNormal;
            sv.Mesh.bounds = new Bounds((tl + br) * 0.5f, new Vector3(1000, 1000, 1000));
            sv.Mat.mainTexture = p.FullTexture;
            sv.Mat.SetFloat("_Alpha", alpha);
            sv.Mat.SetFloat("_ZWrite", alpha >= 0.999f ? 1f : 0f);
            sv.Renderer.enabled = true;
            float top = alpha * Mathf.Clamp01(tops != null && i < tops.Count ? tops[i] : 0f);
            sv.TopMat.mainTexture = p.FullTexture;
            sv.TopMat.SetFloat("_Alpha", top);
            sv.Top.enabled = top > 0.001f;
            SurfaceFill(sv, p, i, alpha, tl, tr, br, bl);
            (shown ??= new HashSet<string>()).Add(cam);
            SurfacesDrawn.Add((cam, alpha, tl, tr, br, bl));
        }

        frustumLines.End();
        frustumLines.SetVisible(true);
        foreach (KeyValuePair<string, SurfaceView> kv in surfaceViews)
        {
            if (shown != null && shown.Contains(kv.Key)) continue;
            if (kv.Value.Renderer.enabled) kv.Value.Renderer.enabled = false;
            if (kv.Value.Top.enabled) kv.Value.Top.enabled = false;
            if (kv.Value.Fill != null && kv.Value.Fill.enabled) kv.Value.Fill.enabled = false;
        }
    }

    /// <summary>the side fill of surface i (SurfaceFillAlpha) around its picture, in the picture's plane (v4, 16:9, a portrait clip)</summary>
    void SurfaceFill(SurfaceView sv, Phone p, int i, float pictureAlpha, Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl)
    {
        float fillA = SurfaceFillAlpha != null && i < SurfaceFillAlpha.Length ? SurfaceFillAlpha[i] * pictureAlpha : 0f;
        FilmDirector fd = FilmDirector.Instance;
        float dim = fd != null && !fd.Vertical ? fd.AspectInfo.PhoneSideFill : 0f;
        if (fillA <= 0.001f || dim <= 0.001f || p.Track.Aspect >= 1.5f)
        {
            if (sv.Fill != null && sv.Fill.enabled) sv.Fill.enabled = false;
            return;
        }

        if (sv.Fill == null)
        {
            sv.FillMesh = NewQuadMesh($"phone {p.Id} frustum side fill");
            sv.FillMat = NewMaterial($"phone {p.Id} frustum side fill", SurfaceQueue - 1, CompareFunction.LessEqual);
            sv.Fill = NewQuad($"phone {p.Id} frustum side fill", transform, sv.FillMesh, sv.FillMat);
        }

        // SetSideFill's geometry at this depth: the quad covers a 16:9 view of the phone's lens (+ a margin), the picture magnified to cover it
        Vector3 centre = (tl + br) * 0.5f, right = tr - tl, up = tl - bl;
        float hw = right.magnitude * 0.5f, hh = up.magnitude * 0.5f;
        if (hw < 1e-5f || hh < 1e-5f) return;
        right /= 2f * hw;
        up /= 2f * hh;
        float halfH = 1.3f * hh, halfW = halfH * 16f / 9f * 1.05f;
        quad[0] = centre - right * halfW - up * halfH;
        quad[1] = centre + right * halfW - up * halfH;
        quad[2] = centre + right * halfW + up * halfH;
        quad[3] = centre - right * halfW + up * halfH;
        float scale = halfW / hw;
        float fv = Mathf.Clamp01(halfH / (hh * scale));
        const float vCentre = 0.46f;
        float v0 = Mathf.Clamp(vCentre - fv * 0.5f, 0f, 1f - fv), v1 = v0 + fv;
        sv.FillMesh.vertices = quad;
        sv.FillMesh.uv = new Vector2[] { new(0, v0), new(1, v0), new(1, v1), new(0, v1) };
        sv.FillMesh.bounds = new Bounds(centre, new Vector3(1000, 1000, 1000));
        sv.FillMat.mainTexture = p.FullTexture;
        sv.FillMat.SetVector("_BlurUV", new Vector4(0.05f, 0.05f * p.Track.Aspect, 0f, 0f));
        sv.FillMat.SetFloat("_Dim", Mathf.Clamp01(dim));
        sv.FillMat.SetFloat("_Alpha", Mathf.Clamp01(fillA));
        sv.Fill.enabled = true;
    }

    /// <summary>1, or less while the eye is within 0.9 m of the picture rectangle and not on the phone's side of it inside its outline</summary>
    public static float ProximityFade(Vector3 eye, Vector3 apex, Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl)
    {
        Vector3 centre = (tl + br) * 0.5f, right = tr - tl, up = tl - bl;
        float hw = right.magnitude * 0.5f, hh = up.magnitude * 0.5f;
        if (hw < 1e-4f || hh < 1e-4f) return 1f;
        right /= hw * 2f;
        up /= hh * 2f;
        Vector3 fwd = (centre - apex).normalized, d = eye - centre;
        float x = Vector3.Dot(d, right), y = Vector3.Dot(d, up), z = Vector3.Dot(d, fwd);
        if (z <= 0f && Mathf.Abs(x) <= hw && Mathf.Abs(y) <= hh) return 1f;
        Vector3 q = centre + Mathf.Clamp(x, -hw, hw) * right + Mathf.Clamp(y, -hh, hh) * up;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.9f, Vector3.Distance(eye, q)));
    }

    public void HideSurfaces()
    {
        SurfacesDrawn.Clear();
        if (frustumLines != null && frustumLines.Visible) frustumLines.SetVisible(false);
        foreach (SurfaceView sv in surfaceViews.Values)
        {
            if (sv.Renderer.enabled) sv.Renderer.enabled = false;
            if (sv.Top.enabled) sv.Top.enabled = false;
            if (sv.Fill != null && sv.Fill.enabled) sv.Fill.enabled = false;
        }
    }

    // ------------------------------------------------------------------ v4 faint frustums (dancecap UNITY_CONTRACT_v4 4: flow.ghost_frustums)

    /// <summary>queue of the faint frustums: in the opaque range, after the room and the floor (ZTest LEqual: the scene hides them) and BEFORE
    /// the Gaussian splat composite (BeforeRenderingTransparents), so the splat dancers are always drawn over them - a ghost line never crosses
    /// a dancer's face or body</summary>
    public const int GhostQueue = 2450;

    GlowMesh ghostLines;
    Material ghostLineMaterial;
    int lastGhostFrame = -100;

    /// <summary>the ghosts drawn by the last SetGhosts (diagnostics / the face sidecar): phone, alpha</summary>
    public readonly List<(string phone, float alpha)> GhostsDrawn = new();

    /// <summary>
    /// Every listed phone's frustum OUTLINE (apex, the four edges, the rim at depth m), faint (alpha, e.g. 0.15), in the phone's colour, no picture,
    /// no label, so the viewer keeps track of where the camera is among the phones. About 1.5 px wide on screen; each line is cut into short
    /// pieces that fade out between 2 m and 1 m from the eye (the orbit passes close to some phones). Called every frame while shown.
    /// </summary>
    public void SetGhosts(IList<string> cameras, float alpha, float depth, float dance)
    {
        lastGhostFrame = Time.frameCount;
        GhostsDrawn.Clear();
        if (cameras == null || cameras.Count == 0 || alpha <= 1e-4f)
        {
            HideGhosts();
            return;
        }

        if (ghostLines == null)
        {
            ghostLineMaterial = GlowMesh.NewMaterial("Film ghost frustums", 1.6f);
            ghostLineMaterial.renderQueue = GhostQueue;
            ghostLines = GlowMesh.Create("Film ghost frustums", transform, ghostLineMaterial);
        }

        Camera view = DanceText.ViewCamera;
        if (view == null)
        {
            HideGhosts();
            return;
        }

        Vector3 eye = view.transform.position;
        // about 1.5 px at 1080 px across the frame's height: the world width of a pixel at distance d is 2 d tan(fov / 2) / H
        float pxPerM = view.pixelHeight / Mathf.Max(1e-3f, 2f * Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad));
        ghostLines.Viewer = eye;
        ghostLines.Begin();
        foreach (string cam in cameras)
        {
            if (!SurfaceCorners(cam, dance, depth, out Vector3 apex, out Vector3 tl, out Vector3 tr, out Vector3 br, out Vector3 bl)) continue;
            Color col = ColourOf(cam) * Mathf.Clamp01(alpha);
            void Seg(Vector3 a, Vector3 b)
            {
                const int n = 8;
                for (int k = 0; k < n; k++)
                {
                    Vector3 p0 = Vector3.Lerp(a, b, k / (float)n), p1 = Vector3.Lerp(a, b, (k + 1) / (float)n), m = (p0 + p1) * 0.5f;
                    float d = Vector3.Distance(eye, m);
                    float near = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.0f, 2.0f, d));
                    if (near < 0.002f) continue;
                    ghostLines.Line(p0, p1, Mathf.Clamp(1.5f * d / pxPerM, 0.0008f, 0.01f), col * near);
                }
            }

            Seg(apex, tl);
            Seg(apex, tr);
            Seg(apex, br);
            Seg(apex, bl);
            Seg(tl, tr);
            Seg(tr, br);
            Seg(br, bl);
            Seg(bl, tl);
            GhostsDrawn.Add((cam, alpha));
        }

        ghostLines.End();
        ghostLines.SetVisible(true);
    }

    public void HideGhosts()
    {
        GhostsDrawn.Clear();
        if (ghostLines != null && ghostLines.Visible) ghostLines.SetVisible(false);
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
        HideSurfaces();
        HideGhosts();
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
        if (Time.frameCount - lastSurfaceFrame > 2) HideSurfaces();
        if (Time.frameCount - lastGhostFrame > 2) HideGhosts();
    }

    void OnDestroy()
    {
        foreach (Phone p in phones.Values) DisposePhone(p);
        phones.Clear();
        if (videoMesh != null) Destroy(videoMesh);
        if (videoMaterial != null) Destroy(videoMaterial);
        if (fillMesh != null) Destroy(fillMesh);
        if (fillMaterial != null) Destroy(fillMaterial);
        if (coverMaterial != null) Destroy(coverMaterial);
        if (lineMaterial != null) Destroy(lineMaterial);
        if (frustumLineMaterial != null) Destroy(frustumLineMaterial);
        if (ghostLineMaterial != null) Destroy(ghostLineMaterial);
        foreach (SurfaceView sv in surfaceViews.Values)
        {
            if (sv.Mesh != null) Destroy(sv.Mesh);
            if (sv.Mat != null) Destroy(sv.Mat);
            if (sv.TopMat != null) Destroy(sv.TopMat);
            if (sv.FillMat != null) Destroy(sv.FillMat);
            if (sv.FillMesh != null) Destroy(sv.FillMesh);
        }

        surfaceViews.Clear();
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
            ["videoFrameTime"] = LastVideoFrameTime, ["videoOpacity"] = LastVideoOpacity, ["videoWhyNot"] = LastVideoWhyNot,
            ["videoCover"] = VideoCover, ["surfaces"] = SurfacesDrawn.ConvertAll(x => $"{x.phone}:{x.alpha:0.###}")
        };
    }
}
