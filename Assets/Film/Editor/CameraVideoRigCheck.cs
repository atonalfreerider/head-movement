using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// Checks for the source-phone video feature (FilmCameraVideo), Play mode with a capture loaded:
///
///   unity command hm_camvideo --action state
///   unity command hm_camvideo --action prepare                     # load &lt;capture&gt;/cameras/
///   unity command hm_camvideo --action pose --cam 06 --at 49.3     # pose, FOV, strip frame chosen for that time
///   unity command hm_camvideo --action sweep --cam 06 --from 31 --to 68 --step 1 --out C:/.../sweep_06.json
///         # the avatars' body joints projected through the phone's pose into its frame (pixels, three ways: closed-form
///         # pinhole, a real Camera component, and the joint list) for comparison with the people detector (Python)
///   unity command hm_camvideo --action viewproject                 # the joints through the RENDER camera as the film
///         # director left it (viewport coordinates + frame mapping), for a POV shot paused at a film time
///   unity command hm_camvideo --action frames --cam 06 --from 31 --to 68 --step 0.5   # frame time vs requested time
///   unity command hm_camvideo --action vpprobe --cam 06      # can Unity's VideoPlayer play cameras/06.mp4? (ExternalTime stepping)
///   unity command hm_camvideo --action vpresult
///   unity command hm_camvideo --action coverage --cam 06 --from 48.7 --to 62.3 [--to2 ...]   # the regression sweep: at every
///         # 1/30 s step TryGetPose must answer, and (--video true) the strip must hold a frame within 0.25 s; every miss is listed
///   unity command hm_camvideo --action noexport / export   # test switch: pretend the capture has no camera export (then
///         # unload + prepare again / restart the film), nothing on disk changes
/// </summary>
public static class CameraVideoRigCheck
{
    // SMPL-X joints that have a COCO-17 counterpart (shoulders, elbows, wrists, hips, knees, ankles) + the pelvis
    static readonly int[] Joints = { 0, 16, 17, 18, 19, 20, 21, 1, 2, 4, 5, 7, 8 };

    [CliCommand("hm_camvideo", "Source-phone video feature checks (Play mode): state | prepare | pose | sweep | viewproject | frames. --cam 06 --at <dance s> --from --to --step --out <json>")]
    public static string Run(
        [CliArg("action", "state | prepare | pose | sweep | viewproject | frames | coverage | noexport | export | unload")] string action = "state",
        [CliArg("cam", "phone id")] string cam = "06",
        [CliArg("at", "dance (reference) seconds")] float at = 40f,
        [CliArg("from", "sweep: first dance second")] float from = 0f,
        [CliArg("to", "sweep: last dance second")] float to = 0f,
        [CliArg("step", "sweep: step in s")] float step = 1f,
        [CliArg("out", "sweep: output json path")] string @out = null,
        [CliArg("video", "coverage: also require the video frame (and the full-frame video) at every step")] bool video = true)
    {
        string a = (action ?? "state").Trim().ToLowerInvariant();
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("enter Play mode first (unity command editor_play) and load a capture");
        HeadMovement hm = HeadMovement.Instance;
        switch (a)
        {
            case "state":
                return Json(FilmCameraVideo.State());
            case "unload":
                FilmCameraVideo.Unload();
                return Json(new { unloaded = true });
            case "noexport":
            case "export":
                FilmCameraVideo.IgnoreExport = a == "noexport";
                FilmCameraVideo.Unload();
                return Json(new { ignoreExport = FilmCameraVideo.IgnoreExport, note = "unloaded; the next Prepare (or film start) reloads" });
            case "coverage":
                return Coverage(hm, cam, from, to > from ? to : from + 1f, step > 0.0334f || step < 0.001f ? 1f / 30f : step, video);
            case "prepare":
                if (hm == null || hm.Manifest == null) throw new InvalidOperationException("no capture loaded");
                FilmCameraVideo.Prepare(hm.Manifest.Folder);
                return Json(new Dictionary<string, object> { ["ready"] = FilmCameraVideo.IsReady, ["status"] = FilmCameraVideo.Status, ["cameras"] = FilmCameraVideo.Cameras });
            case "pose":
            {
                Ensure(hm);
                bool ok = FilmCameraVideo.TryGetPose(cam, at, out Vector3 p, out Quaternion r, out float fov, out Vector2 shift);
                FilmCameraVideo.TryGetSize(cam, out Vector2 size);
                bool f = FilmCameraVideo.TryGetFrame(cam, at, false, out int fi, out double ft);
                return Json(new Dictionary<string, object>
                {
                    ["ok"] = ok, ["position"] = new[] { p.x, p.y, p.z }, ["euler"] = new[] { r.eulerAngles.x, r.eulerAngles.y, r.eulerAngles.z },
                    ["forward"] = V(r * Vector3.forward), ["up"] = V(r * Vector3.up), ["vfov"] = fov, ["lensShift"] = new[] { shift.x, shift.y },
                    ["whyNot"] = FilmCameraVideo.VideoStatus(cam, at), ["hasVideo"] = FilmCameraVideo.HasVideoFor(cam),
                    ["size"] = new[] { size.x, size.y }, ["frameIndex"] = f ? fi : -1, ["frameTime"] = f ? ft : double.NaN,
                    ["frameMinusRequested_ms"] = f ? (ft - at) * 1000.0 : double.NaN, ["danceOrigin"] = V(DanceOrigin.Offset)
                });
            }
            case "frames":
            {
                Ensure(hm);
                List<double> dev = new(), devThumb = new();
                if (to <= from) to = from + 1f;
                for (float t = from; t <= to + 1e-4f; t += Mathf.Max(0.01f, step))
                {
                    if (FilmCameraVideo.TryGetFrame(cam, t, false, out _, out double ft)) dev.Add((ft - t) * 1000.0);
                    if (FilmCameraVideo.TryGetFrame(cam, t, true, out _, out double tt)) devThumb.Add((tt - t) * 1000.0);
                }

                return Json(new Dictionary<string, object> { ["full"] = Stats(dev), ["thumb"] = Stats(devThumb) });
            }
            case "vpprobe":
            {
                Ensure(hm);
                string mp4 = Path.Combine(hm.Manifest.Folder, "cameras", cam + ".mp4");
                if (!File.Exists(mp4)) throw new FileNotFoundException(mp4);
                VideoPlayerProbe.Start(mp4);
                return Json(new { started = mp4 });
            }
            case "vpresult":
                return VideoPlayerProbe.Result ?? Json(new { running = VideoPlayerProbe.Running });
            case "sweep":
                return Sweep(hm, cam, from, to > from ? to : from + 1f, step, @out);
            case "viewproject":
                return ViewProject(hm);
            default:
                throw new ArgumentException($"unknown action '{action}'");
        }
    }

    static void Ensure(HeadMovement hm)
    {
        if (hm == null || hm.Manifest == null) throw new InvalidOperationException("no capture loaded");
        if (!FilmCameraVideo.IsReady) FilmCameraVideo.Prepare(hm.Manifest.Folder);
    }

    static object Stats(List<double> v)
    {
        if (v.Count == 0) return new { n = 0 };
        v.Sort();
        double sum = 0, abs = 0;
        foreach (double x in v)
        {
            sum += x;
            abs += Math.Abs(x);
        }

        return new { n = v.Count, mean_ms = sum / v.Count, mean_abs_ms = abs / v.Count, min_ms = v[0], max_ms = v[v.Count - 1] };
    }

    static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };

    static string Json(object o) => JsonConvert.SerializeObject(o, Formatting.None, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });

    /// <summary>pixel of a world point in the phone's frame: closed-form pinhole through the phone's pose</summary>
    static bool Pinhole(Vector3 world, Vector3 pos, Quaternion rot, float vfov, Vector2 size, out Vector2 px)
    {
        Vector3 c = Quaternion.Inverse(rot) * (world - pos);
        px = Vector2.zero;
        if (c.z <= 0.05f) return false;
        float f = size.y * 0.5f / Mathf.Tan(vfov * 0.5f * Mathf.Deg2Rad);
        px = new Vector2((size.x - 1f) * 0.5f + f * c.x / c.z, (size.y - 1f) * 0.5f - f * c.y / c.z);
        return true;
    }

    /// <summary>the regression sweep for a POV shot: TryGetPose must answer at every step (the director sets the render camera
    /// from it) and, with the video asked for, SetVideo's own test must say yes (a frame within 0.25 s and the pose): every
    /// miss is listed with its reason. A hole here is a blink in the film.</summary>
    static string Coverage(HeadMovement hm, string cam, float from, float to, float step, bool wantVideo)
    {
        Ensure(hm);
        int n = 0, noPose = 0, noVideo = 0;
        List<object> misses = new();
        string runWhy = null;
        float runStart = 0f, runEnd = 0f;
        void Flush()
        {
            if (runWhy == null) return;
            misses.Add(new Dictionary<string, object> { ["from"] = runStart, ["to"] = runEnd, ["why"] = runWhy });
            runWhy = null;
        }

        int steps = Mathf.RoundToInt((to - from) / step);
        for (int i = 0; i <= steps; i++)
        {
            float t = from + i * step;
            n++;
            bool pose = FilmCameraVideo.TryGetPose(cam, t, out _, out _, out _, out _);
            string why = null;
            if (!pose)
            {
                noPose++;
                why = "no pose";
            }
            else if (wantVideo)
            {
                why = FilmCameraVideo.VideoStatus(cam, t);
                if (why != null) noVideo++;
            }

            if (why == null)
            {
                Flush();
                continue;
            }

            if (runWhy != null && runWhy != why) Flush();
            if (runWhy == null)
            {
                runWhy = why;
                runStart = t;
            }

            runEnd = t;
        }

        Flush();
        return Json(new Dictionary<string, object>
        {
            ["cam"] = cam, ["from"] = from, ["to"] = to, ["step"] = step, ["samples"] = n, ["noPose"] = noPose, ["noVideo"] = noVideo,
            ["ok"] = noPose == 0 && noVideo == 0, ["misses"] = misses, ["hasVideo"] = FilmCameraVideo.HasVideoFor(cam)
        });
    }

    static string Sweep(HeadMovement hm, string cam, float from, float to, float step, string outPath)
    {
        Ensure(hm);
        FilmTargets targets = new(hm);
        FilmCameraVideo.TryGetSize(cam, out Vector2 size);
        GameObject go = new("camvideo check camera") { hideFlags = HideFlags.HideAndDontSave };
        Camera real = go.AddComponent<Camera>();
        real.enabled = false;
        List<object> rows = new();
        double worstCamDelta = 0;
        try
        {
            for (float t = from; t <= to + 1e-4f; t += Mathf.Max(0.01f, step))
            {
                if (!FilmCameraVideo.TryGetPose(cam, t, out Vector3 pos, out Quaternion rot, out float fov, out _)) continue;
                go.transform.SetPositionAndRotation(pos, rot);
                real.fieldOfView = fov;
                real.aspect = size.x / size.y;
                real.nearClipPlane = 0.03f;
                real.farClipPlane = 200f;
                foreach (string role in new[] { "lead", "follow" })
                {
                    Dancer d = targets.DancerOf(role);
                    Dictionary<string, object> joints = new();
                    foreach (int j in Joints)
                    {
                        Vector3 w = targets.Joint(d, (SmplJoint)j, t);
                        if (float.IsNaN(w.x)) continue;
                        if (!Pinhole(w, pos, rot, fov, size, out Vector2 px)) continue;
                        Vector3 vp = real.WorldToViewportPoint(w);
                        Vector2 px2 = new(vp.x * size.x - 0.5f, (1f - vp.y) * size.y - 0.5f);
                        worstCamDelta = Math.Max(worstCamDelta, (px - px2).magnitude);
                        joints[j.ToString(CultureInfo.InvariantCulture)] = new[] { px.x, px.y, w.x, w.y, w.z };
                    }

                    rows.Add(new Dictionary<string, object> { ["t"] = t, ["role"] = role, ["vfov"] = fov, ["joints"] = joints });
                }
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }

        Dictionary<string, object> result = new()
        {
            ["cam"] = cam, ["size"] = new[] { size.x, size.y }, ["origin"] = V(DanceOrigin.Offset), ["rows"] = rows,
            ["pinhole_vs_camera_component_max_px"] = worstCamDelta, ["note"] = "joint arrays: pixel x, pixel y (phone frame, y down), world x y z"
        };
        if (!string.IsNullOrEmpty(outPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
            File.WriteAllText(outPath, Json(result));
            return Json(new Dictionary<string, object> { ["wrote"] = outPath, ["rows"] = rows.Count, ["pinhole_vs_camera_component_max_px"] = worstCamDelta });
        }

        return Json(result);
    }

    /// <summary>the joints through the film's RENDER camera as it stands now (director paused on a POV shot)</summary>
    static string ViewProject(HeadMovement hm)
    {
        Ensure(hm);
        Camera view = DanceText.ViewCamera;
        FilmDirector dir = FilmDirector.Instance;
        if (view == null || dir == null) throw new InvalidOperationException("no render camera / film director (hm_film_show --action start, then seek --pause true)");
        float dance = dir.DanceTime;
        FilmTargets targets = new(hm);
        Dictionary<string, object> roles = new();
        foreach (string role in new[] { "lead", "follow" })
        {
            Dancer d = targets.DancerOf(role);
            Dictionary<string, object> joints = new();
            foreach (int j in Joints)
            {
                Vector3 w = targets.Joint(d, (SmplJoint)j, dance);
                if (float.IsNaN(w.x)) continue;
                Vector3 vp = view.WorldToViewportPoint(w);
                joints[j.ToString(CultureInfo.InvariantCulture)] = new[] { vp.x, vp.y, vp.z };
            }

            roles[role] = joints;
        }

        bool pose = false;
        string povCam = null;
        Dictionary<string, object> state = FilmCameraVideo.State();
        if (state.TryGetValue("videoCamera", out object vc)) povCam = vc as string;
        Vector3 pp = Vector3.zero;
        Quaternion pr = Quaternion.identity;
        float pf = 0f;
        if (povCam != null) pose = FilmCameraVideo.TryGetPose(povCam, dance, out pp, out pr, out pf, out _);
        return Json(new Dictionary<string, object>
        {
            ["dance"] = dance, ["film"] = dir.FilmTime, ["segment"] = dir.SegmentId, ["phone"] = povCam, ["pixelWidth"] = view.pixelWidth, ["pixelHeight"] = view.pixelHeight,
            ["aspect"] = view.aspect, ["fov"] = view.fieldOfView, ["position"] = V(view.transform.position), ["forward"] = V(view.transform.forward),
            ["up"] = V(view.transform.up), ["lensShift"] = new[] { view.lensShift.x, view.lensShift.y }, ["usePhysical"] = view.usePhysicalProperties,
            ["phonePose"] = pose ? new Dictionary<string, object> { ["position"] = V(pp), ["forward"] = V(pr * Vector3.forward), ["vfov"] = pf } : null,
            ["cameraMinusPhone_m"] = pose ? (view.transform.position - pp).magnitude : float.NaN,
            ["cameraAngleToPhone_deg"] = pose ? Quaternion.Angle(view.transform.rotation, pr) : float.NaN,
            ["fovMinusPhone_deg"] = pose ? view.fieldOfView - pf : float.NaN, ["video"] = state, ["roles"] = roles
        });
    }
}

/// <summary>
/// Does the exported .mp4 play in Unity's VideoPlayer, and how closely does ExternalTime follow a clock that steps one
/// frame per editor update? (The film itself does not use VideoPlayer: see CameraVideoRig.)
/// </summary>
static class VideoPlayerProbe
{
    static GameObject go;
    static VideoPlayer vp;
    static int step;
    static double started;
    static readonly List<int> Lag = new();
    static string stage;
    public static string Result;
    public static bool Running => stage != null;

    public static void Start(string path)
    {
        Stop();
        Result = null;
        go = new GameObject("video player probe") { hideFlags = HideFlags.HideAndDontSave };
        vp = go.AddComponent<VideoPlayer>();
        vp.playOnAwake = false;
        vp.source = VideoSource.Url;
        vp.url = "file:///" + path.Replace("\\", "/");
        vp.audioOutputMode = VideoAudioOutputMode.None;
        vp.renderMode = VideoRenderMode.APIOnly;
        vp.timeReference = VideoTimeReference.ExternalTime;
        vp.skipOnDrop = false;
        vp.Prepare();
        started = EditorApplication.timeSinceStartup;
        stage = "preparing";
        step = 0;
        Lag.Clear();
        EditorApplication.update += Tick;
    }

    static void Tick()
    {
        if (vp == null)
        {
            Stop();
            return;
        }

        double now = EditorApplication.timeSinceStartup;
        if (stage == "preparing")
        {
            if (vp.isPrepared)
            {
                stage = "stepping";
                started = now;
                vp.externalReferenceTime = 1.0;
                vp.Play();
            }
            else if (now - started > 30)
            {
                Finish(false, "not prepared within 30 s");
            }

            return;
        }

        if (stage == "stepping")
        {
            vp.externalReferenceTime = 1.0 + step / 30.0;
            if (step > 5) Lag.Add((int)(vp.frame - (30 + step)));
            step++;
            if (step >= 120) Finish(true, null);
        }
    }

    static void Finish(bool ok, string error)
    {
        Dictionary<string, object> r = new()
        {
            ["prepared"] = ok, ["error"] = error, ["width"] = vp != null ? vp.width : 0, ["height"] = vp != null ? vp.height : 0,
            ["frameCount"] = vp != null ? (long)vp.frameCount : 0, ["frameRate"] = vp != null ? vp.frameRate : 0f,
            ["length_s"] = vp != null ? vp.length : 0.0
        };
        if (Lag.Count > 0)
        {
            double sum = 0, abs = 0;
            int exact = 0, min = int.MaxValue, max = int.MinValue;
            foreach (int l in Lag)
            {
                sum += l;
                abs += Math.Abs(l);
                if (l == 0) exact++;
                min = Math.Min(min, l);
                max = Math.Max(max, l);
            }

            r["externalTime"] = new Dictionary<string, object>
            {
                ["samples"] = Lag.Count, ["exactFraction"] = (double)exact / Lag.Count, ["meanLagFrames"] = sum / Lag.Count,
                ["meanAbsLagFrames"] = abs / Lag.Count, ["minLag"] = min, ["maxLag"] = max,
                ["note"] = "vp.frame minus the frame the stepped external clock asks for, one step per editor update"
            };
        }

        Result = JsonConvert.SerializeObject(r);
        Stop();
    }

    static void Stop()
    {
        EditorApplication.update -= Tick;
        stage = null;
        if (go != null) UnityEngine.Object.DestroyImmediate(go);
        go = null;
        vp = null;
    }
}

