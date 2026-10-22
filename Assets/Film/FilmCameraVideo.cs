using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The source-phone video feature of the film (VIEWER_SPEC 3.4 / 5.3; film/INTERFACE.md section 5). The film director
/// finds this static class BY REFLECTION (global namespace, any assembly), so either side compiles without the other;
/// every member below is optional for the director (a missing one falls back to its own tracks / plain glyphs).
///
///   Prepare(captureFolder)      load &lt;capture&gt;/cameras/ (written by `python -m dancecap.camera_video`)
///   IsReady, Cameras            loaded; the phone ids that have a track ("01".."09")
///   TryGetPose(...)             a phone's per-frame pose + vertical FOV (zoom) + lens shift at a dance time, Unity WORLD
///   SetGlyphs(alpha, t)         frustum glyphs with live thumbnails of every phone (the from-above overview)
///   SetVideo(cam, op, t, speed, composite)  the phone's own frame, full-frame at its POV, 3D over (or under) it
///   Release()                   hide everything
///
/// HasVideoFor / VideoShowing / VideoWhyNot / VideoStatus are optional extras: the director reads them (also by reflection)
/// so that it never says "the original video" over a picture that is not there.
///
/// Times are dance (reference) seconds. All state is driven by the director's film clock each frame; nothing here reads
/// wall-clock or audio time, so a frame-locked recording is exactly reproducible. Without an export for the capture
/// every query answers "nothing" (TryGetPose false, no glyphs, no video) and the director's fallback takes over.
/// </summary>
public static class FilmCameraVideo
{
    static CameraVideoRig rig;
    static string preparedFolder;

    public static bool IsReady => rig != null && rig.Loaded;

    public static string[] Cameras => rig != null ? rig.Ids : Array.Empty<string>();

    /// <summary>"01".."09" and what the export holds (diagnostics)</summary>
    public static string Status => rig != null ? rig.Status : "not prepared";

    /// <summary>load the capture's phones once (tracks now, frames on demand); captureFolder = the loaded capture's
    /// absolute folder (HeadMovement.Manifest.Folder)</summary>
    public static void Prepare(string captureFolder)
    {
        if (rig == null) rig = CameraVideoRig.Create();
        if (rig.Loaded && string.Equals(preparedFolder, captureFolder, StringComparison.OrdinalIgnoreCase)) return;
        rig.Load(captureFolder);
        preparedFolder = captureFolder;
    }

    /// <summary>the phone's pose and lens at a dance time (reference seconds): Unity world position (DanceOrigin applied),
    /// rotation, vertical FOV in degrees from the per-frame focal length (zoom), principal-point offset as
    /// Camera.lensShift (the phones' principal points sit at the frame centre: always (0,0)). False = no pose then.</summary>
    public static bool TryGetPose(string camera, float danceTime, out Vector3 position, out Quaternion rotation,
        out float verticalFovDeg, out Vector2 lensShift)
    {
        lensShift = Vector2.zero;
        position = Vector3.zero;
        rotation = Quaternion.identity;
        verticalFovDeg = 60f;
        return rig != null && rig.Loaded && rig.TryPose(camera, danceTime, out position, out rotation, out verticalFovDeg);
    }

    /// <summary>frustum glyph + live thumbnail for every phone; alpha 0 hides. Called every frame while shown.</summary>
    public static void SetGlyphs(float alpha, float danceTime)
    {
        if (rig == null || !rig.Loaded)
        {
            return;
        }

        rig.SetGlyphs(alpha, danceTime);
    }

    /// <summary>
    /// The phone's own video, full-frame at the phone's POV: <paramref name="opacity"/> 0..1 (0 = hidden),
    /// <paramref name="composite"/> "3d_over_video" (default: the video is the backdrop, skeletons / avatars / overlays
    /// paint over it) or "video_over_3d". The frame shown is the one nearest to <paramref name="danceTime"/>
    /// (speed 0 simply keeps the time: a paused frame; slow motion steps through the source frames); speed is only
    /// informational. camera null hides the video. Called every frame while shown.
    /// </summary>
    public static void SetVideo(string camera, float opacity, float danceTime, float speed, string composite)
    {
        if (rig == null || !rig.Loaded) return;
        rig.SetVideo(camera, opacity, danceTime, composite);
    }

    // ------------------------------------------------------------------ v3 flow (optional members the director reads by reflection)

    /// <summary>the phones' frustums with their live video at depth m along the optical axis (alpha 0..1 each; top = the hand-off cover share),
    /// CameraVideoRig.SetSurfaces. Called every frame while any is shown; an empty list hides them.</summary>
    public static void SetSurfaces(string[] cameras, float[] alphas, float[] tops, float[] depths, float danceTime) =>
        SetSurfaces(cameras, alphas, tops, depths, danceTime, null);

    /// <summary>SetSurfaces with the frustum lines' own alphas (v4 outline curves; null = the lines follow the pictures)</summary>
    public static void SetSurfaces(string[] cameras, float[] alphas, float[] tops, float[] depths, float danceTime, float[] outlines)
    {
        if (rig == null || !rig.Loaded) return;
        if (cameras == null || cameras.Length == 0)
        {
            rig.HideSurfaces();
            return;
        }

        rig.SetSurfaces(cameras, alphas, tops, depths, danceTime, outlines);
    }

    /// <summary>v4, 16:9: the side fill's alpha per surface of the next SetSurfaces (null = none) and whether the full-frame view's side fill
    /// stays under the hand-off cover</summary>
    public static void SetSurfaceFills(float[] alphas)
    {
        if (rig == null) return;
        rig.SurfaceFillAlpha = alphas;
    }

    /// <summary>v4: the full-frame view's side fill keeps the video's opacity under the hand-off cover (the frustum surface carries it on)</summary>
    public static void SetSideFillUnderCover(bool on)
    {
        if (rig != null) rig.SideFillUnderCover = on;
    }

    /// <summary>v4: the faint frustum outlines of these phones (alpha, e.g. 0.15; depth m), CameraVideoRig.SetGhosts. Every frame while shown;
    /// an empty list or alpha 0 hides them.</summary>
    public static void SetGhosts(string[] cameras, float alpha, float depth, float danceTime)
    {
        if (rig == null || !rig.Loaded) return;
        rig.SetGhosts(cameras, alpha, depth, danceTime);
    }

    /// <summary>the faint frustums drawn this frame</summary>
    public static IReadOnlyList<(string phone, float alpha)> GhostsDrawn => rig != null ? rig.GhostsDrawn : Array.Empty<(string, float)>();

    /// <summary>the full-frame video drawn again over every overlay (0..1; FlowTimeline.VideoCover); set before SetVideo</summary>
    public static void SetVideoCover(float cover)
    {
        if (rig != null) rig.VideoCover = cover;
    }

    /// <summary>a phone's image rectangle at depth (m): apex and the corners top-left, top-right, bottom-right, bottom-left (world)</summary>
    public static bool TryGetSurfaceCorners(string camera, float danceTime, float depth, out Vector3 apex, out Vector3 tl, out Vector3 tr, out Vector3 br, out Vector3 bl)
    {
        apex = tl = tr = br = bl = Vector3.zero;
        return rig != null && rig.Loaded && rig.SurfaceCorners(camera, danceTime, depth, out apex, out tl, out tr, out br, out bl);
    }

    /// <summary>the surfaces drawn this frame (the face sidecar)</summary>
    public static IReadOnlyList<(string phone, float alpha, Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl)> SurfacesDrawn =>
        rig != null ? rig.SurfacesDrawn : Array.Empty<(string, float, Vector3, Vector3, Vector3, Vector3)>();

    // ------------------------------------------------------------------ availability (optional members the director reads by reflection)
    // (these four are read by FilmCameraSource: the on-screen "original video" label depends on VideoShowing)

    /// <summary>the phone has a full-frame video in this capture's export (a phone with a track only shows its point of view)</summary>
    public static bool HasVideoFor(string camera) => rig != null && rig.Loaded && rig.HasVideo(camera);

    /// <summary>the phone video layer is drawn right now (set by the last SetVideo: it hides itself, and says why in
    /// <see cref="VideoWhyNot"/>, when there is no pose, no frame or no video for that dance time)</summary>
    public static bool VideoShowing => rig != null && rig.VideoVisible;

    /// <summary>why the last SetVideo drew nothing although it was asked to (null = it drew, or was not asked)</summary>
    public static string VideoWhyNot => rig?.LastVideoWhyNot;

    /// <summary>null = SetVideo(camera, ., danceTime) would draw the phone's frame; else the reason it would not</summary>
    public static string VideoStatus(string camera, float danceTime) =>
        rig != null && rig.Loaded ? rig.VideoWhyNot(camera, danceTime) : "no camera export loaded (python -m dancecap.camera_video)";

    /// <summary>test switch: Prepare treats the capture as having no camera export (the director's fallback tracks / glyphs
    /// take over, nothing on disk changes). Set it, then <see cref="Unload"/> and Prepare again.</summary>
    public static bool IgnoreExport;

    /// <summary>hide glyphs and video, close the frame files (film stop)</summary>
    public static void Release()
    {
        if (rig != null) rig.ReleaseAll();
    }

    // ------------------------------------------------------------------ diagnostics / checks (not part of the director contract)

    public static bool TryGetSize(string camera, out Vector2 size)
    {
        size = Vector2.zero;
        return rig != null && rig.Loaded && rig.TrySize(camera, out size);
    }

    /// <summary>index and time of the strip frame that SetVideo / SetGlyphs would show for a dance time</summary>
    public static bool TryGetFrame(string camera, float danceTime, bool thumbnail, out int index, out double frameTime)
    {
        index = -1;
        frameTime = double.NaN;
        return rig != null && rig.Loaded && rig.TryFrameFor(camera, danceTime, thumbnail, out index, out frameTime);
    }

    public static Dictionary<string, object> State() => rig != null ? rig.State() : new Dictionary<string, object> { ["loaded"] = false };

    /// <summary>destroy the rig and its textures (tests; the director never needs this)</summary>
    public static void Unload()
    {
        if (rig != null)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(rig.gameObject);
            else UnityEngine.Object.DestroyImmediate(rig.gameObject);
        }

        rig = null;
        preparedFolder = null;
    }
}
