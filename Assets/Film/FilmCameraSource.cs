using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// The source phones for the film (INTERFACE.md section 5): the camera-video feature (static class FilmCameraVideo,
/// found by reflection so either side compiles alone) supplies per-frame poses, frustum glyphs with thumbnails and the
/// full-frame video layer; without it, the director's fallback reads film/cameras/&lt;take&gt;_cameras.json (poses only)
/// and draws plain frustum glyphs coloured per phone.
/// </summary>
public class FilmCameraSource
{
    public static readonly Color[] PhoneColours =
    {
        new(1f, 0.42f, 0.33f), new(1f, 0.72f, 0.2f), new(0.95f, 0.95f, 0.3f), new(0.45f, 0.95f, 0.45f), new(0.3f, 0.9f, 0.9f),
        new(0.35f, 0.62f, 1f), new(0.68f, 0.5f, 1f), new(1f, 0.5f, 0.85f), new(0.85f, 0.85f, 0.85f)
    };

    // reflection: the camera-video feature
    readonly Type ext;
    readonly MethodInfo mPrepare, mTryGetPose, mSetGlyphs, mSetVideo, mRelease, mHasVideoFor, mTryGetSize;
    readonly PropertyInfo pReady, pCameras, pShowing, pWhyNot;
    bool prepared;
    float prepareStart = -1f;
    string lastVideo;

    /// <summary>where this source reports problems (the director adds them to its warnings)</summary>
    public Action<string> Warn;
    // a POV shot whose phone video is NOT drawn although it was asked for (no pose / no frame / no video): one warning per outage
    string outageCam, outageWhy;
    float outageT0, outageT1;
    readonly List<string> featureIds = new();

    // fallback: tracks exported by film/cameras/export_cameras.py (capture coordinates)
    class Track
    {
        public string Id;
        public float[] T;
        public Vector3[] Pos, Fwd, Up;
        public float[] Fov;
        public Vector2 Size;
    }

    readonly Dictionary<string, Track> tracks = new();
    GlowMesh glyphs;
    Material glyphMaterial;
    readonly List<string> cameraIds = new();

    public string Status { get; private set; } = "none";
    public bool HasFeature => ext != null;
    /// <summary>the feature class can show a phone's video at all (not that this capture has one: see HasVideoOf / VideoShowing)</summary>
    public bool HasVideo => ext != null && mSetVideo != null;

    /// <summary>a phone's source video is drawn right now: the only thing that may be called "the original video" on screen.
    /// False when the capture has no camera export, the phone has no full-frame video, or there is no pose / frame at
    /// this dance time (the shot is then the phone's point of view without the picture).</summary>
    public bool VideoShowing
    {
        get
        {
            if (ext == null || pShowing == null) return false;
            try
            {
                return (bool)pShowing.GetValue(null);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>the export holds a full-frame video for this phone (feature without the member: assume it does)</summary>
    public bool HasVideoOf(string camera)
    {
        if (ext == null || mSetVideo == null) return false;
        if (mHasVideoFor == null) return FeatureHasCameras();
        try
        {
            return (bool)mHasVideoFor.Invoke(null, new object[] { camera });
        }
        catch
        {
            return false;
        }
    }

    string VideoWhyNot()
    {
        try
        {
            return pWhyNot?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }
    public IReadOnlyList<string> Cameras => cameraIds;
    public float GlyphAlpha { get; private set; }

    public FilmCameraSource()
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t;
            try
            {
                t = a.GetType("FilmCameraVideo", false);
            }
            catch
            {
                continue;
            }

            if (t == null) continue;
            ext = t;
            break;
        }

        if (ext == null) return;
        const BindingFlags S = BindingFlags.Public | BindingFlags.Static;
        mPrepare = ext.GetMethod("Prepare", S, null, new[] { typeof(string) }, null);
        mTryGetPose = ext.GetMethods(S).FirstOrDefault(m => m.Name == "TryGetPose" && m.GetParameters().Length >= 5);
        mSetGlyphs = ext.GetMethod("SetGlyphs", S, null, new[] { typeof(float), typeof(float) }, null);
        mSetVideo = ext.GetMethods(S).FirstOrDefault(m => m.Name == "SetVideo" && m.GetParameters().Length >= 3);
        mRelease = ext.GetMethod("Release", S, null, Type.EmptyTypes, null);
        pReady = ext.GetProperty("IsReady", S);
        pCameras = ext.GetProperty("Cameras", S);
        pShowing = ext.GetProperty("VideoShowing", S);
        pWhyNot = ext.GetProperty("VideoWhyNot", S);
        mHasVideoFor = ext.GetMethod("HasVideoFor", S, null, new[] { typeof(string) }, null);
        mTryGetSize = ext.GetMethods(S).FirstOrDefault(m => m.Name == "TryGetSize" && m.GetParameters().Length == 2);
    }

    /// <summary>width / height of a phone's frame (the director's POV crop maps the crop centre to a direction with it); false = unknown</summary>
    public bool TryGetAspect(string camera, out float aspect)
    {
        aspect = 9f / 16f;
        if (ext != null && mTryGetSize != null)
        {
            try
            {
                object[] args = { camera, null };
                if ((bool)mTryGetSize.Invoke(null, args) && args[1] is Vector2 sz && sz.y > 1f)
                {
                    aspect = sz.x / sz.y;
                    return true;
                }
            }
            catch
            {
                // falls through to the fallback tracks
            }
        }

        if (tracks.TryGetValue(camera ?? "", out Track t) && t.Size.y > 1f)
        {
            aspect = t.Size.x / t.Size.y;
            return true;
        }

        return false;
    }

    /// <summary>start loading: the feature's Prepare, and the fallback tracks</summary>
    public void Prepare(string captureFolder, FilmDirection d, Transform parent)
    {
        LoadTracks(d);
        if (ext != null && mPrepare != null && !prepared)
        {
            try
            {
                mPrepare.Invoke(null, new object[] { captureFolder });
                prepared = true;
                prepareStart = Time.realtimeSinceStartup;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"FilmCameraVideo.Prepare failed: {Inner(e).Message}");
            }
        }

        if (glyphs == null)
        {
            glyphMaterial = GlowMesh.NewMaterial("Film camera glyphs", 1.6f);
            glyphs = GlowMesh.Create("Film camera glyphs", parent, glyphMaterial);
            glyphs.SetVisible(false);
        }

        RefreshIds();
        Status = ext != null ? "FilmCameraVideo" : tracks.Count > 0 ? "fallback tracks (no source video)" : "none";
        Preflight(d);
    }

    /// <summary>what the direction needs from the phones against what the capture's export holds: every shot that sits at
    /// a phone must have its video, else the shot shows the director's own point of view without the picture (and the
    /// on-screen label says so)</summary>
    void Preflight(FilmDirection d)
    {
        if (ext == null || Warn == null || d == null) return;
        List<string> pov = new();
        foreach (FilmSegment s in d.Segments)
        {
            if (s.Mode == "fly_to_camera")
            {
                pov.Add(s.Camera.Value<string>("camera") ?? "06");
            }
            else if (s.Mode == "camera_tour" && s.Camera["cameras"] is JArray list)
            {
                foreach (JToken c in list)
                {
                    string id = c.Value<string>("camera");
                    if (!string.IsNullOrEmpty(id)) pov.Add(id);
                }
            }
        }

        pov = pov.Distinct().ToList();
        if (pov.Count == 0) return;
        if (!FeatureHasCameras())
        {
            Warn($"no source video for phone {string.Join(", ", pov)} in capture {d.Capture}: it has no camera export " +
                 "(python -m dancecap.camera_video); the shots show the phones' points of view on the director's own tracks, without the original video");
            return;
        }

        foreach (string cam in pov)
        {
            if (!featureIds.Contains(cam))
            {
                Warn($"no source video for phone {cam} in capture {d.Capture}: the phone is not in the camera export; its shot shows the director's own point of view");
            }
            else if (!HasVideoOf(cam))
            {
                Warn($"no source video for phone {cam} in capture {d.Capture}: the export has its track but no full-frame video " +
                     $"(python -m dancecap.camera_video --cams {cam}); its shot shows the point of view without the original video");
            }
        }
    }

    /// <summary>the feature is ready (or absent, or gave up after 60 s real time)</summary>
    public bool Ready
    {
        get
        {
            if (ext == null || pReady == null) return true;
            try
            {
                if ((bool)pReady.GetValue(null)) return true;
            }
            catch
            {
                return true;
            }

            return prepareStart >= 0 && Time.realtimeSinceStartup - prepareStart > 60f;
        }
    }

    /// <summary>the camera-video feature lists at least one phone (no export for the capture = use the fallback glyphs)</summary>
    bool FeatureHasCameras()
    {
        if (pCameras == null) return true;
        try
        {
            return pCameras.GetValue(null) is string[] ids && ids.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    void RefreshIds()
    {
        cameraIds.Clear();
        featureIds.Clear();
        if (ext != null && pCameras != null)
        {
            try
            {
                if (pCameras.GetValue(null) is string[] ids)
                {
                    cameraIds.AddRange(ids);
                    featureIds.AddRange(ids);
                }
            }
            catch
            {
                // fallback below
            }
        }

        foreach (string id in tracks.Keys)
        {
            if (!cameraIds.Contains(id)) cameraIds.Add(id);
        }

        cameraIds.Sort(StringComparer.Ordinal);
    }

    void LoadTracks(FilmDirection d)
    {
        if (tracks.Count > 0) return;
        string dir = Path.Combine(d.Dir, "cameras");
        if (!Directory.Exists(dir)) return;
        string file = Path.Combine(dir, $"{d.Take}_cameras.json");
        if (!File.Exists(file)) file = Directory.GetFiles(dir, "*_cameras.json").FirstOrDefault();
        if (file == null) return;
        try
        {
            JObject j = JObject.Parse(File.ReadAllText(file));
            foreach (JToken c in j["cameras"] ?? new JArray())
            {
                Track t = new()
                {
                    Id = c.Value<string>("id"), T = c["t"].Select(x => x.Value<float>()).ToArray(), Pos = V3(c["pos"]), Fwd = V3(c["fwd"]),
                    Up = V3(c["up"]), Fov = c["vfov_deg"].Select(x => x.Value<float>()).ToArray()
                };
                if (c["size"] is JArray s && s.Count == 2) t.Size = new Vector2(s[0].Value<float>(), s[1].Value<float>());
                if (t.T.Length > 0) tracks[t.Id] = t;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"film camera tracks unreadable ({e.Message})");
        }
    }

    static Vector3[] V3(JToken arr) => arr.Select(a => new Vector3(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>())).ToArray();

    public static Color ColourOf(string id)
    {
        int n = int.TryParse(id, out int v) ? v : Math.Abs(id?.GetHashCode() ?? 0);
        return PhoneColours[Mathf.Abs(n - 1) % PhoneColours.Length];
    }

    /// <summary>a phone's pose at a dance time (Unity world)</summary>
    public bool TryGetPose(string camera, float dance, out Vector3 pos, out Quaternion rot, out float vfov, out Vector2 lensShift)
    {
        pos = Vector3.zero;
        rot = Quaternion.identity;
        vfov = 60f;
        lensShift = Vector2.zero;
        if (ext != null && mTryGetPose != null)
        {
            try
            {
                object[] args = { camera, dance, null, null, null, null };
                if (mTryGetPose.GetParameters().Length == 5) args = new object[] { camera, dance, null, null, null };
                if ((bool)mTryGetPose.Invoke(null, args))
                {
                    pos = (Vector3)args[2];
                    rot = (Quaternion)args[3];
                    vfov = (float)args[4];
                    if (args.Length > 5 && args[5] is Vector2 ls) lensShift = ls;
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"FilmCameraVideo.TryGetPose failed: {Inner(e).Message}");
            }
        }

        if (!tracks.TryGetValue(camera ?? "", out Track t)) return false;
        int n = t.T.Length;
        if (dance < t.T[0] - 0.5f || dance > t.T[n - 1] + 0.5f) return false;
        int lo = 0, hi = n - 1;
        if (dance <= t.T[0]) hi = 0;
        else if (dance >= t.T[n - 1]) lo = n - 1;
        else
        {
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (t.T[mid] <= dance) lo = mid;
                else hi = mid;
            }
        }

        int a = Mathf.Min(lo, hi), b = Mathf.Max(lo, hi);
        float k = b > a ? Mathf.Clamp01((dance - t.T[a]) / Mathf.Max(1e-5f, t.T[b] - t.T[a])) : 0f;
        pos = Vector3.Lerp(t.Pos[a], t.Pos[b], k) + DanceOrigin.Offset;
        Vector3 f = Vector3.Slerp(t.Fwd[a], t.Fwd[b], k), u = Vector3.Slerp(t.Up[a], t.Up[b], k);
        rot = Quaternion.LookRotation(f, u);
        vfov = Mathf.Lerp(t.Fov[a], t.Fov[b], k);
        return true;
    }

    /// <summary>frustum glyphs of every phone (alpha 0 hides): the feature's (with thumbnails) or plain fallback lines</summary>
    public void SetGlyphs(float alpha, float dance, Camera viewer)
    {
        GlyphAlpha = alpha;
        // the feature draws the glyphs only when its export holds phones for this capture (else the plain fallback below)
        if (ext != null && mSetGlyphs != null && FeatureHasCameras())
        {
            try
            {
                mSetGlyphs.Invoke(null, new object[] { alpha, dance });
                if (glyphs != null) glyphs.SetVisible(false);
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"FilmCameraVideo.SetGlyphs failed: {Inner(e).Message}");
            }
        }

        if (glyphs == null) return;
        bool on = alpha > 0.01f && tracks.Count > 0;
        glyphs.SetVisible(on);
        if (!on) return;
        glyphs.Begin();
        glyphs.Viewer = viewer != null ? viewer.transform.position : null;
        float dist = viewer != null ? 1f : 1f;
        foreach (Track t in tracks.Values)
        {
            if (!TryGetPose(t.Id, dance, out Vector3 p, out Quaternion r, out float fov, out _)) continue;
            Color c = ColourOf(t.Id) * alpha;
            float aspect = t.Size.y > 0 ? t.Size.x / t.Size.y : 9f / 16f;
            float depth = 0.55f * dist;
            float hh = depth * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), hw = hh * aspect;
            Vector3 fw = r * Vector3.forward, up = r * Vector3.up, rt = r * Vector3.right;
            Vector3 cc = p + fw * depth;
            Vector3 c0 = cc + up * hh - rt * hw, c1 = cc + up * hh + rt * hw, c2 = cc - up * hh + rt * hw, c3 = cc - up * hh - rt * hw;
            float w = 0.035f;
            glyphs.Line(p, c0, w, c * 0.8f);
            glyphs.Line(p, c1, w, c * 0.8f);
            glyphs.Line(p, c2, w, c * 0.8f);
            glyphs.Line(p, c3, w, c * 0.8f);
            glyphs.Line(c0, c1, w * 1.3f, c);
            glyphs.Line(c1, c2, w * 1.3f, c);
            glyphs.Line(c2, c3, w * 1.3f, c);
            glyphs.Line(c3, c0, w * 1.3f, c);
            glyphs.Line(c0, c1 + (c0 - c1) * 0f + up * 0f, w, c); // top edge doubled: marks "up"
            glyphs.Triangle(c0 + up * hh * 0.15f, c1 + up * hh * 0.15f, (c0 + c1) * 0.5f + up * hh * 0.45f, c * 0.9f);
            glyphs.Sphere(p, 0.09f, c);
        }

        glyphs.End();
    }

    /// <summary>full-frame video of a phone at the render camera's POV (feature only); camera null hides it</summary>
    public bool SetVideo(string camera, float opacity, float dance, float speed, string composite)
    {
        if (ext == null || mSetVideo == null) return false;
        if (camera == null && lastVideo == null)
        {
            FlushOutage();
            return true;
        }

        try
        {
            int n = mSetVideo.GetParameters().Length;
            object[] args = n switch
            {
                3 => new object[] { camera, opacity, dance },
                4 => new object[] { camera, opacity, dance, speed },
                _ => new object[] { camera, opacity, dance, speed, composite ?? "3d_over_video" }
            };
            mSetVideo.Invoke(null, args);
            lastVideo = opacity > 0 ? camera : null;
            TrackOutage(camera, opacity, dance);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"FilmCameraVideo.SetVideo failed: {Inner(e).Message}");
            return false;
        }
    }

    /// <summary>a phone's video is asked for (the pill is up) but not drawn: remember the outage, report it once when it ends</summary>
    void TrackOutage(string camera, float opacity, float dance)
    {
        if (camera != null && opacity > 0.05f && !VideoShowing)
        {
            if (outageCam != camera) FlushOutage();
            if (outageCam == null)
            {
                outageCam = camera;
                outageT0 = dance;
                outageWhy = VideoWhyNot() ?? "no reason given";
            }

            outageT1 = dance;
        }
        else
        {
            FlushOutage();
        }
    }

    void FlushOutage()
    {
        if (outageCam == null) return;
        Warn?.Invoke($"the original video of phone {outageCam} is missing for dance {outageT0:0.0}-{outageT1:0.0} s ({outageWhy}); " +
                     "the shot shows the phone's point of view without it");
        outageCam = null;
    }

    public void Release()
    {
        FlushOutage();
        if (glyphs != null) glyphs.SetVisible(false);
        if (ext != null && mRelease != null)
        {
            try
            {
                mRelease.Invoke(null, null);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"FilmCameraVideo.Release failed: {Inner(e).Message}");
            }
        }

        lastVideo = null;
    }

    public void Destroy()
    {
        Release();
        if (glyphs != null) UnityEngine.Object.Destroy(glyphs.gameObject);
        if (glyphMaterial != null) UnityEngine.Object.Destroy(glyphMaterial);
        glyphs = null;
    }

    static Exception Inner(Exception e) => e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
}
