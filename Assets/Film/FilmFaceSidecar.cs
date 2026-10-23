using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HmFilm;
using HmSplats;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The face sidecar of a film recording ("hm film faces" v1, dancecap docs/RECAP_FILM.md "The face sidecar"): one entry per recorded frame, in
/// recording order, with each dancer's 5 face points posed rigidly with the splat skeleton's head (S_15 x the canonical points of
/// face_points_canonical.json) and projected by the recording camera into the recorder's output size, their depths, the face's yaw / pitch /
/// roll relative to the line of sight, the inter-ocular distance, the head size, the visibility of 9 samples (capsules of the partner's body and
/// the dancer's own forearms / hands), the screen rectangles of the overlays (captions, cards, readouts, call-out pills, the legend), the
/// phone video's opacity and every v3 frustum surface drawn this frame (its polygon and corner depths), and (v4.2) per dancer the layer its face
/// is drawn on ("layer": splat / avatar / skeleton / none, "la": that layer's opacity). dancecap.film_faceswap reads it to swap only the splat
/// dancers' faces and never a face on a phone's video, a frustum picture or any other layer.
///
/// The recorder (FilmRecorder, hm_film record --faces on|off|auto) calls Begin after the director's Begin, the director's late pass (FilmLate)
/// calls Tick after the frame's overlays are laid out, and the recorder calls End when it stops the Movie Recorder (the frame it stops on is
/// not captured and not sampled). End writes &lt;recording&gt;.faces.json next to the MP4.
/// </summary>
public sealed class FilmFaceSidecar
{
    public static FilmFaceSidecar Active { get; private set; }

    sealed class Dancer
    {
        public string Key, Name;
        public Role Role;
        public Vector3[] Points5, Samples9;
        public Vector3 HeadCentre;
    }

    readonly List<string> frames = new();
    SkeletonLegend[] legends;
    readonly List<Dancer> dancers = new();
    string output, pointsRef, aspect, directionName, capture;
    int width, height;
    float fps;
    public int Frames => frames.Count;
    public string Path { get; private set; }
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] LandmarkNames = { "eye_r", "eye_l", "nose_tip", "mouth_r", "mouth_l" };

    /// <summary>start sampling (null on success, else why not)</summary>
    public static string Begin(string outputMp4, int width, int height, float fps, string aspect)
    {
        Active = null;
        FilmDirector fd = FilmDirector.Instance;
        if (fd == null || fd.Direction == null) return "no film director";
        string pts = FindFacePoints(fd);
        if (pts == null) return "no face_points_canonical.json (capture splats/, <direction>/../faceswap/, work/<take>/v3/faceswap/)";
        FilmFaceSidecar s = new()
        {
            output = outputMp4, width = width, height = height, fps = fps, aspect = aspect, directionName = fd.Direction.Name, capture = fd.Direction.Capture
        };
        try
        {
            JObject j = JObject.Parse(File.ReadAllText(pts));
            foreach ((string key, Role role) in new[] { ("lead", Role.Lead), ("follow", Role.Follow) })
            {
                JObject r = j["roles"]?[key] as JObject;
                if (r == null) return $"{pts} has no role {key}";
                s.dancers.Add(new Dancer
                {
                    Key = key, Role = role, Name = r.Value<string>("name") ?? key, Points5 = V3s(r["points5"]), Samples9 = V3s(r["samples9"]),
                    HeadCentre = V3(r["head_centre"])
                });
            }

            using SHA256 sha = SHA256.Create();
            string hex = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(pts))).Replace("-", "").ToLowerInvariant();
            s.pointsRef = $"{pts.Replace('\\', '/')} sha256 {hex}";
        }
        catch (Exception e)
        {
            return $"{pts}: {e.Message}";
        }

        if (s.dancers.Any(d => d.Points5.Length != 5)) return $"{pts}: points5 must hold 5 points per dancer";
        s.Path = System.IO.Path.ChangeExtension(outputMp4, ".faces.json");
        Active = s;
        return null;
    }

    /// <summary>stop sampling and write the file; returns its path (null when nothing was sampled or it could not be written)</summary>
    public static string End(out int frameCount, out string error)
    {
        frameCount = 0;
        error = null;
        FilmFaceSidecar s = Active;
        Active = null;
        if (s == null) return null;
        frameCount = s.frames.Count;
        try
        {
            StringBuilder b = new(256 + s.frames.Sum(f => f.Length + 2));
            b.Append("{\"format\": \"hm film faces\", \"version\": 1, \"recording\": ").Append(Q(System.IO.Path.GetFileName(s.output)));
            b.Append(", \"direction\": ").Append(Q(s.directionName)).Append(", \"capture\": ").Append(Q(s.capture));
            b.Append(", \"aspect\": ").Append(Q(s.aspect)).Append(", \"width\": ").Append(s.width).Append(", \"height\": ").Append(s.height);
            b.Append(", \"fps\": ").Append(N(s.fps, 3)).Append(", \"frames\": ").Append(s.frames.Count);
            b.Append(", \"landmarks\": [").Append(string.Join(", ", LandmarkNames.Select(Q))).Append(']');
            b.Append(", \"face_points\": ").Append(Q(s.pointsRef));
            b.Append(", \"dancers\": [").Append(string.Join(", ", s.dancers.Select(d => $"{{\"key\": {Q(d.Key)}, \"name\": {Q(d.Name)}}}"))).Append(']');
            b.Append(",\n \"f\": [\n");
            for (int i = 0; i < s.frames.Count; i++)
            {
                b.Append("  ").Append(s.frames[i]);
                b.Append(i + 1 < s.frames.Count ? ",\n" : "\n");
            }

            b.Append(" ]}\n");
            File.WriteAllText(s.Path, b.ToString());
            return s.Path;
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>FilmLate, after the director's overlays: sample the frame being rendered</summary>
    public static void Tick()
    {
        FilmFaceSidecar s = Active;
        if (s == null) return;
        try
        {
            s.Sample();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"FilmFaceSidecar: frame {s.frames.Count}: {e.Message}");
            s.frames.Add($"{{\"t\": null, \"error\": {Q(e.Message)}}}");
        }
    }

    // ------------------------------------------------------------------ one frame

    void Sample()
    {
        FilmDirector fd = FilmDirector.Instance;
        Camera cam = DanceText.ViewCamera;
        StringBuilder b = new(1024);
        if (fd == null || cam == null)
        {
            frames.Add("{\"t\": null}");
            return;
        }

        float sx = width / (float)Mathf.Max(1, cam.pixelWidth), sy = height / (float)Mathf.Max(1, cam.pixelHeight);
        FilmCamera fc = fd.FilmCam;
        bool showing = fd.Sources != null && fd.Sources.VideoShowing;
        float video = fc != null && fc.PovCamera != null && showing ? Mathf.Clamp01(fc.PovVideoOpacity) : 0f;
        float splats = SplatDancers.Enabled ? Mathf.Clamp01(SplatDancers.FilmVideoFade()) : 0f;
        string view = video < 0.001f ? "3d" : video > 0.999f ? "video" : "mix";
        Transform ct = cam.transform;
        Quaternion q = ct.rotation;
        b.Append("{\"t\": ").Append(N(fd.FilmTime, 4)).Append(", \"dt\": ").Append(N(fd.DanceTime, 4));
        b.Append(", \"view\": ").Append(Q(view)).Append(", \"video_alpha\": ").Append(N(video, 3)).Append(", \"splats\": ").Append(N(splats, 3));
        b.Append(", \"phone\": ").Append(video > 0f ? Q(fc.PovCamera) : "null");
        b.Append(", \"cam\": [").Append(N(ct.position.x, 4)).Append(", ").Append(N(ct.position.y, 4)).Append(", ").Append(N(ct.position.z, 4)).Append(", ")
            .Append(N(q.x, 6)).Append(", ").Append(N(q.y, 6)).Append(", ").Append(N(q.z, 6)).Append(", ").Append(N(q.w, 6)).Append(", ").Append(N(cam.fieldOfView, 4)).Append(']');

        // the screen overlays
        b.Append(", \"ui\": [");
        List<Rect> ui = UiRects(fd, sx, sy);
        for (int i = 0; i < ui.Count; i++)
        {
            if (i > 0) b.Append(", ");
            b.Append('[').Append(N(ui[i].x, 1)).Append(", ").Append(N(ui[i].y, 1)).Append(", ").Append(N(ui[i].width, 1)).Append(", ").Append(N(ui[i].height, 1)).Append(']');
        }

        b.Append(']');

        // the frustum surfaces
        b.Append(", \"surfaces\": [");
        if (fd.Sources != null)
        {
            bool first = true;
            foreach ((string phone, float alpha, Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl) in fd.Sources.SurfacesDrawn)
            {
                string role = "enter";
                foreach (FlowSurface fs in fd.FlowSurfacesNow)
                {
                    if (fs.Camera == phone) role = fs.Role;
                }

                if (!Surface(cam, sx, sy, tl, tr, br, bl, out List<Vector2> poly, out List<float> zs, out bool full)) continue;
                if (!first) b.Append(", ");
                first = false;
                b.Append("{\"phone\": ").Append(Q(phone)).Append(", \"alpha\": ").Append(N(alpha, 3)).Append(", \"quad\": [");
                for (int i = 0; i < poly.Count; i++)
                {
                    if (i > 0) b.Append(", ");
                    b.Append('[').Append(N(poly[i].x, 2)).Append(", ").Append(N(poly[i].y, 2)).Append(']');
                }

                b.Append("], \"z\": [").Append(string.Join(", ", zs.Select(z => N(z, 4)))).Append("], \"role\": ").Append(Q(role)).Append(", \"full\": ").Append(full ? 1 : 0).Append('}');
            }
        }

        b.Append(']');

        // the dancers
        b.Append(", \"d\": [");
        for (int i = 0; i < dancers.Count; i++)
        {
            if (i > 0) b.Append(", ");
            DancerEntry(b, fd, cam, sx, sy, dancers[i]);
        }

        b.Append("]}");
        frames.Add(b.ToString());
    }

    /// <summary>S_15: the splat skinning's head matrix (splats on), else the avatar's head bone x its bindpose (the same within millimetres)</summary>
    static bool HeadMatrix(Role role, out Matrix4x4 s)
    {
        s = Matrix4x4.identity;
        SkinnedSplatAvatar sp = SplatDancers.SplatOf(role);
        if (sp != null && sp.TrySkinNow(15, out s)) return true;
        HeadMovement hm = HeadMovement.Instance;
        if (hm == null || hm.Avatars == null || !hm.Avatars.TryGetValue(role, out SmplxAvatar av) || av == null || av.BoneCount <= 15) return false;
        Transform head = av.Bone(15);
        if (head == null) return false;
        s = head.localToWorldMatrix * Matrix4x4.Translate(-av.RestJoint(15));
        return true;
    }

    void DancerEntry(StringBuilder b, FilmDirector fd, Camera cam, float sx, float sy, Dancer d)
    {
        b.Append("{\"k\": ").Append(Q(d.Key));
        if (!HeadMatrix(d.Role, out Matrix4x4 s))
        {
            b.Append(", \"in\": 0, \"layer\": \"none\", \"la\": 0}");
            return;
        }

        // v4.2 (the face swap applies to the splat dancers only): the layer this dancer's face is drawn on now and its opacity
        string layer = FaceLayer(d.Role, out float layerAlpha);
        b.Append(", \"layer\": ").Append(Q(layer)).Append(", \"la\": ").Append(N(layerAlpha, 3));

        float near = cam.nearClipPlane;
        Vector2[] lm = new Vector2[5];
        float[] z = new float[5];
        bool front = true;
        for (int i = 0; i < 5; i++)
        {
            Vector3 w = s.MultiplyPoint3x4(d.Points5[i]);
            lm[i] = Px(cam, w, sx, sy, out z[i]);
            front &= z[i] > near;
        }

        Vector3 hc = s.MultiplyPoint3x4(d.HeadCentre);
        Vector2 hp = Px(cam, hc, sx, sy, out float hz);
        bool inFrame = hz > near && hp.x >= 0f && hp.x <= width && hp.y >= 0f && hp.y <= height;
        Transform ct = cam.transform;
        FaceSidecarMath.FaceAngles(hc, ct.position, ct.right, ct.up, s.MultiplyVector(Vector3.back), s.MultiplyVector(Vector3.up), out float yaw, out float pitch, out float roll);
        float iod = (lm[0] - lm[1]).magnitude;
        float focal = 0.5f * height / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float size = hz > 1e-3f ? 0.2f * focal / hz : 0f;
        Visibility(fd, ct.position, s, d, out float vis, out string by);
        b.Append(", \"in\": ").Append(front && inFrame ? 1 : 0).Append(", \"lm\": [");
        for (int i = 0; i < 5; i++)
        {
            if (i > 0) b.Append(", ");
            b.Append('[').Append(N(lm[i].x, 2)).Append(", ").Append(N(lm[i].y, 2)).Append(']');
        }

        b.Append("], \"z\": [").Append(string.Join(", ", z.Select(v => N(v, 4)))).Append(']');
        b.Append(", \"yaw\": ").Append(N(yaw, 2)).Append(", \"pitch\": ").Append(N(pitch, 2)).Append(", \"roll\": ").Append(N(roll, 2));
        b.Append(", \"iod\": ").Append(N(iod, 2)).Append(", \"size\": ").Append(N(size, 2)).Append(", \"vis\": ").Append(N(vis, 3)).Append(", \"by\": ").Append(Q(by)).Append('}');
    }

    /// <summary>v4.2: which layer a dancer's face is drawn on this frame - "splat" (its splats are drawn: SplatDancers.ShownFor, alpha = their composite
    /// opacity), "avatar" (the mesh avatar instead, alpha = its opacity), "skeleton" (neither: only lines / overlays, or the body hidden under a phone's
    /// video). dancecap.film_faceswap swaps a face only on "splat"</summary>
    static string FaceLayer(Role role, out float alpha)
    {
        alpha = SplatDancers.ShownFor(role);
        if (alpha > 0.001f) return "splat";
        alpha = 0f;
        HeadMovement hm = HeadMovement.Instance;
        if (SplatDancers.SplatOf(role) == null && hm != null && hm.Avatars != null && hm.Avatars.TryGetValue(role, out SmplxAvatar av) && av != null && av.Visible)
        {
            alpha = Mathf.Clamp01(av.Opacity);
            if (alpha > 0.001f) return "avatar";
        }

        alpha = 0f;
        return "skeleton";
    }

    /// <summary>the share of the 9 samples a ray from the camera reaches without hitting the partner's body (head 0.11 m, torso 0.15, upper arm /
    /// forearm / hand 0.05) or the dancer's own forearms / hands; by = the occluder of most hidden samples</summary>
    static void Visibility(FilmDirector fd, Vector3 camPos, Matrix4x4 s, Dancer d, out float vis, out string by)
    {
        vis = 1f;
        by = "";
        FilmTargets t = fd.Targets;
        if (t == null || d.Samples9 == null || d.Samples9.Length == 0) return;
        float dance = fd.DanceTime;
        global::Dancer self = t.DancerOf(d.Key), partner = t.DancerOf(d.Key == "lead" ? "follow" : "lead");
        List<(Vector3 a, Vector3 b, float r)> own = new(), other = new();
        Limbs(t, self, dance, own, false);
        Limbs(t, partner, dance, other, true);
        int hidden = 0, byPartner = 0, bySelf = 0;
        foreach (Vector3 c in d.Samples9)
        {
            Vector3 p = s.MultiplyPoint3x4(c);
            bool hp = other.Any(x => FaceSidecarMath.Blocks(camPos, p, x.a, x.b, x.r));
            bool hs = !hp && own.Any(x => FaceSidecarMath.Blocks(camPos, p, x.a, x.b, x.r));
            if (hp) byPartner++;
            if (hs) bySelf++;
            if (hp || hs) hidden++;
        }

        vis = 1f - hidden / (float)d.Samples9.Length;
        by = hidden == 0 ? "" : byPartner >= bySelf ? "partner" : "self";
    }

    static void Limbs(FilmTargets t, global::Dancer dn, float dance, List<(Vector3, Vector3, float)> o, bool whole)
    {
        if (dn == null) return;
        Vector3 J(SmplJoint j) => t.Joint(dn, j, dance);
        void Add(Vector3 a, Vector3 b, float r)
        {
            if (!float.IsNaN(a.x) && !float.IsNaN(b.x)) o.Add((a, b, r));
        }

        foreach ((SmplJoint sh, SmplJoint el, SmplJoint wr) in new[] { (SmplJoint.L_Shoulder, SmplJoint.L_Elbow, SmplJoint.L_Wrist), (SmplJoint.R_Shoulder, SmplJoint.R_Elbow, SmplJoint.R_Wrist) })
        {
            Vector3 e = J(el), w = J(wr);
            if (whole) Add(J(sh), e, 0.05f);
            Add(e, w, 0.05f);
            if (!float.IsNaN(e.x) && !float.IsNaN(w.x)) Add(w, w + (w - e).normalized * 0.15f, 0.05f);
        }

        if (!whole) return;
        Add(J(SmplJoint.Pelvis), J(SmplJoint.Neck), 0.15f);
        Vector3 head = J(SmplJoint.Head);
        if (!float.IsNaN(head.x)) Add(head, head + Vector3.up * 0.12f, 0.11f);
    }

    // ------------------------------------------------------------------ surfaces, overlays

    /// <summary>a frustum surface's polygon in output pixels (clipped against the near plane) and each point's camera depth</summary>
    bool Surface(Camera cam, float sx, float sy, Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl, out List<Vector2> poly, out List<float> zs, out bool full)
    {
        poly = new List<Vector2>();
        zs = new List<float>();
        full = false;
        Matrix4x4 w2c = cam.worldToCameraMatrix, c2w = cam.cameraToWorldMatrix;
        List<Vector3> pts = new();
        foreach (Vector3 p in new[] { tl, tr, br, bl })
        {
            Vector3 v = w2c.MultiplyPoint(p);
            pts.Add(new Vector3(v.x, v.y, -v.z));
        }

        List<Vector3> clipped = FaceSidecarMath.ClipNear(pts, cam.nearClipPlane + 1e-4f);
        if (clipped.Count < 3) return false;
        foreach (Vector3 c in clipped)
        {
            Vector3 world = c2w.MultiplyPoint(new Vector3(c.x, c.y, -c.z));
            poly.Add(Px(cam, world, sx, sy, out _));
            zs.Add(c.z);
        }

        List<Vector2> pp = poly;
        full = new[] { new Vector2(0, 0), new Vector2(width, 0), new Vector2(width, height), new Vector2(0, height) }.All(x => FaceSidecarMath.Inside(x, pp));
        return true;
    }

    /// <summary>pixel rectangles (x, y from the top, w, h) of the visible screen overlays: every uGUI graphic of the film's canvas with an alpha of at
    /// least 0.05 (backings, words, pills; full-screen flashes and fades left out), merged where they touch; plus the physics legend</summary>
    List<Rect> UiRects(FilmDirector fd, float sx, float sy)
    {
        List<Rect> rects = new();
        Canvas canvas = fd.Overlay != null ? fd.Overlay.Canvas : null;
        if (canvas != null)
        {
            Vector3[] c = new Vector3[4];
            foreach (Graphic g in canvas.GetComponentsInChildren<Graphic>(false))
            {
                if (!g.enabled || !g.gameObject.activeInHierarchy) continue;
                float a = g.color.a * g.canvasRenderer.GetInheritedAlpha() * g.canvasRenderer.GetAlpha();
                if (a < 0.05f) continue;
                if (g is Text tx && string.IsNullOrWhiteSpace(tx.text)) continue;
                g.rectTransform.GetWorldCorners(c);
                float x0 = Mathf.Min(c[0].x, c[2].x) * sx, x1 = Mathf.Max(c[0].x, c[2].x) * sx;
                float y0 = height - Mathf.Max(c[0].y, c[2].y) * sy, y1 = height - Mathf.Min(c[0].y, c[2].y) * sy;
                x0 = Mathf.Max(0f, x0);
                y0 = Mathf.Max(0f, y0);
                x1 = Mathf.Min(width, x1);
                y1 = Mathf.Min(height, y1);
                if (x1 - x0 < 1f || y1 - y0 < 1f) continue;
                if ((x1 - x0) * (y1 - y0) > 0.5f * width * height) continue;
                rects.Add(Rect.MinMaxRect(x0, y0, x1, y1));
            }
        }

        // the legends are looked up once (and again when one is gone), not every recorded frame
        if (legends == null || legends.Any(x => x == null)) legends = UnityEngine.Object.FindObjectsByType<SkeletonLegend>(FindObjectsInactive.Include);
        foreach (SkeletonLegend l in legends)
        {
            // v4.2: the meter fades in and out (SkeletonLegend.Alpha): listed only while it shows
            if (l == null || !l.Show || !l.isActiveAndEnabled || SkeletonLegend.Alpha < 0.05f) continue;
            Rect r = SkeletonLegend.Area;
            rects.Add(new Rect(r.x * sx, r.y * sy, r.width * sx, r.height * sy));
        }

        // merge rectangles that overlap or nearly touch (a caption's words and its backing become one block)
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < rects.Count && !merged; i++)
            {
                for (int j = i + 1; j < rects.Count; j++)
                {
                    Rect a = rects[i], bb = rects[j];
                    Rect grown = Rect.MinMaxRect(a.xMin - 4f, a.yMin - 4f, a.xMax + 4f, a.yMax + 4f);
                    if (!grown.Overlaps(bb)) continue;
                    rects[i] = Rect.MinMaxRect(Mathf.Min(a.xMin, bb.xMin), Mathf.Min(a.yMin, bb.yMin), Mathf.Max(a.xMax, bb.xMax), Mathf.Max(a.yMax, bb.yMax));
                    rects.RemoveAt(j);
                    merged = true;
                    break;
                }
            }
        }

        return rects;
    }

    // ------------------------------------------------------------------ helpers

    Vector2 Px(Camera cam, Vector3 world, float sx, float sy, out float depth)
    {
        Vector3 sp = cam.WorldToScreenPoint(world);
        depth = sp.z;
        return new Vector2(sp.x * sx, height - sp.y * sy);
    }

    static string FindFacePoints(FilmDirector fd)
    {
        List<string> c = new();
        HeadMovement hm = HeadMovement.Instance;
        if (hm?.Manifest?.Folder != null) c.Add(System.IO.Path.Combine(hm.Manifest.Folder, "splats", "face_points_canonical.json"));
        if (fd.Direction.Dir != null)
        {
            c.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(fd.Direction.Dir, "..", "faceswap", "face_points_canonical.json")));
            c.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(fd.Direction.Dir, "..", "v3", "faceswap", "face_points_canonical.json")));
        }

        string work = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "..", "dancecap", "work"));
        if (!string.IsNullOrEmpty(fd.Direction.Take)) c.Add(System.IO.Path.Combine(work, fd.Direction.Take, "v3", "faceswap", "face_points_canonical.json"));
        return c.FirstOrDefault(File.Exists);
    }

    static Vector3 V3(JToken t) => t is JArray a && a.Count == 3 ? new Vector3(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>()) : Vector3.zero;

    static Vector3[] V3s(JToken t) => t is JArray a ? a.Select(V3).ToArray() : Array.Empty<Vector3>();

    static string N(float v, int digits) => float.IsFinite(v) ? Math.Round(v, digits).ToString("0.######", Inv) : "null";

    static string Q(string s) => s == null ? "null" : JsonConvertString(s);

    static string JsonConvertString(string s) => Newtonsoft.Json.JsonConvert.ToString(s);
}
