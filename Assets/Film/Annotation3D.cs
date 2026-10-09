using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The film's call-outs (direction "annotation_kinds"), a pure function of film time + the camera:
/// - arrow3d: a 3D arrow (white shaft + arrowhead with a dark outline, a coloured glow) pointing AT a target that moves
///   with the dancers (FilmTargets: foot, joint, com, couple_com, pivot_foot, counterbalance_axis, leader_t, dial,
///   floor_point, stance_foot, miniature_couple, graph_node). The arrow lies across the view (from the side with room,
///   tail above: pointing down-in), its length and line widths are constant on screen; it shoots in (0.35 s), taps
///   gently toward the target, fades out (0.25 s). Its label is a screen pill at the tail.
/// - floor_ring (a step touchdown, with a ripple), floor_arc (how far around her a step lands: from her facing to the
///   step, swept in), floor_wedge (the sector where the pivot steps landed: + = her left), turn_arc (a rotating curved
///   arrow around a centre: the turn direction, seen from above), pull_arrows (two short floor arrows at the two COMs
///   pointing toward each other: the radial pull, "est."; never a connector line), com_marker (spheres: lead red,
///   follow white, couple yellow on the floor), readout (screen text; stacked above the captions in 9:16, under the top
///   band in 16:9).
/// Arrows draw on top of everything (HM_FilmSolid, ZTest Always) so a COM inside a translucent body stays visible;
/// floor marks are depth tested. Labels stay inside the title-safe area and above the narration caption block, and
/// push apart when they overlap. Colours from direction "style.annotation_colours".
/// </summary>
[AddComponentMenu("")]
public class Annotation3D : MonoBehaviour
{
    FilmDirector director;
    RectTransform labelRoot;
    GlowMesh top, floor, glow;
    Material topMat, floorMat, glowMat;

    Color arrowCore = Color.white, labelText = Color.white, pillColour = new(0f, 0f, 0f, 0.69f);
    Color stepColour = new(0.2f, 0.84f, 1f), wedgeColour = new(0.2f, 0.84f, 1f, 0.2f), leadColour = new(1f, 0.23f, 0.19f);
    Color followColour = Color.white, coupleColour = new(1f, 0.85f, 0.1f), tensionColour = new(1f, 0.42f, 0.08f);
    static readonly Color Dark = new(0.02f, 0.02f, 0.03f, 0.82f);

    class LabelView
    {
        public RectTransform Rt;
        public Image Back, Accent;
        public Text Text;
        public string Shown;
        public int Size;
        public Vector2 Box;
    }

    struct LabelReq
    {
        public string Id, Text;
        public Vector2 Anchor, Dir; // screen px (bottom-left origin), unit push direction
        public Color Accent;
        public float Alpha;
        public bool Readout, Fixed;
        public int Size;
        public Vector2 Box;  // laid-out size
        public Vector2 Pos;  // centre
        public string[] Lines;
        public string[] LinesN; // the narrow wrap (a slim side margin), null when it would not differ
        public Vector2 BoxN;
    }

    readonly List<LabelView> pool = new();
    readonly List<LabelReq> reqs = new();
    readonly List<string> active = new();
    readonly HashSet<string> unresolved = new();
    readonly List<(FilmAnnotation a, Vector3 target, float alpha, Color colour)> arrows = new();
    readonly Dictionary<string, Rect> lastLabelRects = new();
    float unit = 1f;
    Camera cam;
    float film;

    /// <summary>extra screen readouts for this frame (the director's speed pill, the phone name at a POV)</summary>
    public readonly List<(string id, string text, Color accent, float alpha)> Extra = new();

    public IReadOnlyList<string> ActiveIds => active;

    public void Init(FilmDirector d, RectTransform root)
    {
        director = d;
        labelRoot = root;
        Shader solid = Resources.Load<Shader>("HM_FilmSolid");
        if (solid == null) solid = Shader.Find("HeadMovement/FilmSolid");
        topMat = new Material(solid) { name = "Film call-outs (on top)" };
        floorMat = new Material(solid) { name = "Film floor marks", renderQueue = 3000 };
        floorMat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.LessEqual);
        glowMat = GlowMesh.NewMaterial("Film call-out glow", 1.6f);
        glow = GlowMesh.Create("Film call-out glow", transform, glowMat);
        floor = GlowMesh.Create("Film floor marks", transform, floorMat);
        top = GlowMesh.Create("Film call-outs", transform, topMat);
        ReadStyle(d.Direction);
    }

    void ReadStyle(FilmDirection d)
    {
        if (d.Root["style"]?["annotation_colours"] is not JObject c) return;
        arrowCore = Hex(c, "arrow", arrowCore);
        labelText = Hex(c, "label_text", labelText);
        pillColour = Hex(c, "label_pill", pillColour);
        stepColour = Hex(c, "step", stepColour);
        wedgeColour = Hex(c, "wedge", wedgeColour);
        leadColour = Hex(c, "lead", leadColour);
        followColour = Hex(c, "follow", followColour);
    }

    static Color Hex(JObject o, string key, Color fallback)
    {
        string s = o.Value<string>(key);
        if (string.IsNullOrEmpty(s) || !s.StartsWith("#")) return fallback;
        return ColorUtility.TryParseHtmlString(s, out Color c) ? c : fallback;
    }

    public void OnAspectChanged()
    {
        foreach (LabelView l in pool) l.Shown = null;
    }

    static Color Lin(Color c) => QualitySettings.activeColorSpace == ColorSpace.Linear ? new Color(c.linear.r, c.linear.g, c.linear.b, c.a) : c;

    static Color A(Color c, float alpha) => new(c.r, c.g, c.b, c.a * alpha);

    /// <summary>additive glow colour (rgb * a)</summary>
    static Color G(Color c, float k) => new(c.r, c.g, c.b, Mathf.Clamp01(k));

    // ------------------------------------------------------------------ per frame

    public void Tick(float filmTime, float dance, Camera camera)
    {
        film = filmTime;
        danceNow = dance;
        cam = camera;
        active.Clear();
        reqs.Clear();
        arrows.Clear();
        avoidSegs.Clear();
        top.Begin();
        floor.Begin();
        glow.Begin();
        if (cam == null)
        {
            top.End();
            floor.End();
            glow.End();
            HideLabels(0);
            return;
        }

        FilmAspect aspect = director.AspectInfo;
        bool vertical = director.Vertical;
        unit = vertical ? Screen.width / 1080f : Screen.height / 1080f;
        Vector3 eye = cam.transform.position;
        top.Viewer = floor.Viewer = glow.Viewer = eye;

        FilmTargets targets = director.Targets;
        foreach (FilmAnnotation a in director.Direction.Annotations)
        {
            if (film < a.F0 || film > a.F1 || a.Pending) continue; // pending (TBD) annotations never play
            float alpha = Mathf.Min(Mathf.SmoothStep(0f, 1f, (film - a.F0) / 0.25f), Mathf.SmoothStep(0f, 1f, (a.F1 - film) / 0.25f));
            if (a.F0 <= 1e-4f) alpha = Mathf.SmoothStep(0f, 1f, (a.F1 - film) / 0.25f);
            if (alpha <= 0.002f) continue;
            try
            {
                switch (a.Kind)
                {
                    case "arrow3d":
                        if (targets.Resolve(a.Target, dance, out Vector3 p))
                        {
                            arrows.Add((a, p, alpha, SubjectColour(a.Target)));
                            active.Add(a.Id);
                        }
                        else unresolved.Add(a.Id);

                        break;
                    case "floor_ring":
                        if (FloorRing(a, dance, alpha)) active.Add(a.Id);
                        else unresolved.Add(a.Id);
                        break;
                    case "floor_arc":
                        if (FloorArc(a, dance, alpha)) active.Add(a.Id);
                        else unresolved.Add(a.Id);
                        break;
                    case "floor_wedge":
                        if (FloorWedge(a, dance, alpha)) active.Add(a.Id);
                        else unresolved.Add(a.Id);
                        break;
                    case "turn_arc":
                        if (TurnArc(a, dance, alpha)) active.Add(a.Id);
                        else unresolved.Add(a.Id);
                        break;
                    case "pull_arrows":
                        if (PullArrows(a, dance, alpha)) active.Add(a.Id);
                        else unresolved.Add(a.Id);
                        break;
                    case "com_marker":
                        if (ComMarkers(a, dance, alpha)) active.Add(a.Id);
                        else unresolved.Add(a.Id);
                        break;
                    case "readout":
                        Readout(a.Id, a.Text, coupleColour, alpha);
                        active.Add(a.Id);
                        break;
                }
            }
            catch (Exception e)
            {
                unresolved.Add($"{a.Id}: {e.Message}");
            }
        }

        DrawArrows(dance);
        foreach ((string id, string text, Color accent, float alpha) in Extra) Readout(id, text, accent, alpha, true);
        Extra.Clear();

        top.End();
        floor.End();
        glow.End();
        top.SetVisible(true);
        floor.SetVisible(true);
        glow.SetVisible(true);
        LayoutLabels(aspect, vertical);
    }

    Color SubjectColour(JObject target)
    {
        if (target == null) return arrowCore;
        string type = target.Value<string>("type") ?? "";
        string who = target.Value<string>("dancer");
        return type switch
        {
            "couple_com" or "counterbalance_axis" or "pivot_foot" or "dial" => coupleColour,
            "floor_point" => stepColour,
            "leader_t" => leadColour,
            "miniature_couple" or "graph_node" => new Color(0.6f, 0.9f, 1f),
            _ => string.Equals(who, "follow", StringComparison.OrdinalIgnoreCase) ? followColour : leadColour
        };
    }

    /// <summary>world size of one screen pixel at p (perspective: grows with the depth)</summary>
    float PixelWorld(Vector3 p)
    {
        float depth = Mathf.Max(cam.nearClipPlane * 2f, Vector3.Dot(p - cam.transform.position, cam.transform.forward));
        return 2f * depth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
    }

    bool OnScreen(Vector3 p, out Vector2 s)
    {
        Vector3 v = cam.WorldToScreenPoint(p);
        s = new Vector2(v.x, v.y);
        return v.z > cam.nearClipPlane;
    }

    // ------------------------------------------------------------------ arrows

    void DrawArrows(float dance)
    {
        if (arrows.Count == 0) return;
        Vector3 couple = director.Targets.CoupleCentre(dance) + Vector3.up * 0.9f;
        OnScreen(couple, out Vector2 coupleS);
        // order by the target's screen x: the left half comes in from the left, the right half from the right
        List<(int i, float x)> order = new();
        for (int i = 0; i < arrows.Count; i++)
        {
            OnScreen(arrows[i].target, out Vector2 s);
            order.Add((i, s.x));
        }

        order.Sort((a, b) => a.x.CompareTo(b.x));
        int leftCount = 0, rightCount = 0;
        for (int r = 0; r < order.Count; r++)
        {
            (FilmAnnotation a, Vector3 target, float alpha, Color colour) = arrows[order[r].i];
            float x = order[r].x;
            int side;
            if (order.Count == 1) side = x >= coupleS.x - 12f * unit ? 1 : -1;
            else side = r < order.Count / 2 || (order.Count % 2 == 1 && r == order.Count / 2 && x < coupleS.x) ? -1 : 1;
            if (x < 0.28f * Screen.width) side = 1;
            if (x > 0.72f * Screen.width) side = -1;
            int rank = side < 0 ? leftCount++ : rightCount++;
            Arrow(a, target, alpha, colour, side, rank);
        }
    }

    void Arrow(FilmAnnotation a, Vector3 target, float alpha, Color colour, int side, int rank)
    {
        Transform ct = cam.transform;
        Vector3 fw = ct.forward;
        Vector3 right = ct.right;
        // "up" in the picture: the world up projected onto the image plane (the camera's up when looking straight down)
        Vector3 up = Vector3.up - fw * Vector3.Dot(Vector3.up, fw);
        up = up.sqrMagnitude < 0.09f ? ct.up : up.normalized;
        float elevation = (35f + 17f * (rank % 3)) * Mathf.Deg2Rad;
        Vector3 dir = (right * side * Mathf.Cos(elevation) + up * Mathf.Sin(elevation) - fw * 0.22f).normalized; // target -> tail
        bool vertical = director.Vertical;
        float pw = PixelWorld(target);
        float lenPx = (vertical ? 165f : 150f) * unit * (1f + 0.22f * (rank / 3));
        float len = Mathf.Clamp(lenPx * pw, 0.08f, 4f);
        float gap = 9f * unit * pw;

        float age = film - a.F0;
        float grow = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(age / 0.35f));
        if (a.F0 <= 1e-4f) grow = 1f;
        float tap = 0.06f * len * (0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 0.85f * Mathf.Max(0f, age - 0.35f)));
        Vector3 tipFinal = target + dir * (gap + tap);
        Vector3 tail = target + dir * (gap + tap + len);
        Vector3 tip = Vector3.Lerp(tail, tipFinal, Mathf.Max(0.05f, grow));
        DrawArrow(tail, tip, colour, alpha);
        Avoid(tail, tip, a.Id);

        if (!string.IsNullOrEmpty(a.Text) && OnScreen(tail, out Vector2 ts) && OnScreen(tipFinal, out Vector2 tps))
        {
            Vector2 d = ts - tps;
            d = d.sqrMagnitude > 1e-4f ? d.normalized : new Vector2(side, 0.6f).normalized;
            reqs.Add(new LabelReq { Id = a.Id, Text = a.Text, Anchor = ts, Dir = d, Accent = colour, Alpha = alpha * Mathf.Clamp01(grow * 1.5f) });
        }
    }

    /// <summary>shaft + arrowhead, all widths in screen pixels (constant thickness on screen)</summary>
    void DrawArrow(Vector3 tail, Vector3 tip, Color colour, float alpha, float shaftPx = 7f, GlowMesh mesh = null)
    {
        mesh ??= top;
        Vector3 d = tip - tail;
        float len = d.magnitude;
        if (len < 1e-4f) return;
        Vector3 dir = d / len;
        Vector3 mid = (tail + tip) * 0.5f;
        float pw = PixelWorld(mid);
        float shaft = shaftPx * unit * pw, outline = 3f * unit * pw;
        float headLen = Mathf.Min(len * 0.55f, 30f * unit * pw), headHalf = 15f * unit * pw;
        Vector3 toCam = (cam.transform.position - mid).normalized;
        Vector3 side = Vector3.Cross(dir, toCam);
        side = side.sqrMagnitude < 1e-8f ? cam.transform.right : side.normalized;
        Vector3 baseC = tip - dir * headLen;

        Color gc = Lin(colour);
        glow.Line(tail, baseC, shaft * 3.6f, G(gc, 0.18f * alpha), G(gc, 0.42f * alpha));
        glow.Triangle(tip + dir * outline * 2.5f, baseC - dir * outline + side * headHalf * 1.7f, baseC - dir * outline - side * headHalf * 1.7f, G(gc, 0.42f * alpha));
        glow.Cone(baseC, tip, headHalf * 0.9f, G(gc, 0.3f * alpha), 10);

        Color dark = A(Dark, alpha);
        Color core = A(Lin(arrowCore), alpha);
        mesh.Line(tail - dir * outline, baseC + dir * outline, shaft + 2f * outline, dark);
        mesh.Triangle(tip + dir * outline * 2.2f, baseC - dir * outline + side * (headHalf + outline * 1.8f),
            baseC - dir * outline - side * (headHalf + outline * 1.8f), dark);
        mesh.Line(tail, baseC + dir * outline * 0.5f, shaft, A(core, 0.92f), core);
        mesh.Triangle(tip, baseC + side * headHalf, baseC - side * headHalf, core);
        // a coloured collar where the shaft meets the head: who the arrow is about
        Color cc = A(Lin(colour), alpha);
        mesh.Line(baseC - dir * Mathf.Min(len * 0.25f, 10f * unit * pw), baseC, shaft * 1.05f, cc);
    }

    // ------------------------------------------------------------------ floor marks

    Vector3 Floor(Vector3 p, float y = 0.012f) => new(p.x, y, p.z);

    bool Resolve(JToken t, float dance, out Vector3 p)
    {
        p = FilmTargets.Nan;
        return t is JObject o && director.Targets.Resolve(o, dance, out p);
    }

    /// <summary>angle (rad, atan2(z, x)) of her facing at a dance time: + angles = toward her left</summary>
    float Bearing(string spec, float dance, Vector3 centre, out bool ok)
    {
        ok = true;
        if (spec is "her_facing" or "follow_facing")
        {
            Vector3 f = director.Targets.Facing(director.Targets.DancerOf("follow"), dance);
            return Mathf.Atan2(f.z, f.x);
        }

        if (spec is "his_facing" or "lead_facing")
        {
            Vector3 f = director.Targets.Facing(director.Targets.DancerOf("lead"), dance);
            return Mathf.Atan2(f.z, f.x);
        }

        ok = false;
        return 0f;
    }

    float FloorWidth(Vector3 p, float px) => px * unit * PixelWorld(p);

    bool FloorRing(FilmAnnotation a, float dance, float alpha)
    {
        if (!Resolve(a.Json["at"] ?? a.Json["target"], dance, out Vector3 p)) return false;
        p = Floor(p);
        float pw = PixelWorld(p);
        float r = Mathf.Max(0.11f, 26f * unit * pw);
        float w = 5f * unit * pw;
        Color c = Lin(stepColour);
        floor.Ring(p, r + w * 0.9f, w * 2.4f, A(Dark, 0.55f * alpha), 48);
        floor.Ring(p, r, w, A(c, alpha), 48);
        glow.Ring(p, r, w * 3.2f, G(c, 0.35f * alpha), 48);
        floor.Disc(p, r * 0.92f, A(c, 0.22f * alpha), A(c, 0.08f * alpha), 32);
        // a ripple when it appears
        float age = film - a.F0;
        if (age < 0.9f)
        {
            float k = age / 0.9f;
            glow.Ring(p, r * (1f + 1.6f * k), w * 1.5f, G(c, 0.5f * (1f - k) * alpha), 48);
        }

        if (!string.IsNullOrEmpty(a.Text) && OnScreen(p, out Vector2 s))
        {
            OnScreen(p + cam.transform.right * r, out Vector2 sr);
            Vector2 d = new(sr.x >= s.x ? 1f : -1f, -0.35f);
            reqs.Add(new LabelReq { Id = a.Id, Text = a.Text, Anchor = s + d.normalized * Mathf.Abs(sr.x - s.x), Dir = d.normalized, Accent = stepColour, Alpha = alpha });
        }

        return true;
    }

    bool FloorArc(FilmAnnotation a, float dance, float alpha)
    {
        if (!Resolve(a.Json["centre"], dance, out Vector3 c)) return false;
        c = Floor(c, 0.014f);
        float from = Bearing(a.Json.Value<string>("from_bearing") ?? "her_facing", dance, c, out bool ok);
        if (!ok) return false;
        float to;
        if (Resolve(a.Json["to"], dance, out Vector3 tp)) to = Mathf.Atan2(tp.z - c.z, tp.x - c.x);
        else if (a.Json["to_deg"] != null) to = from + a.Json.Value<float>("to_deg") * Mathf.Deg2Rad;
        else return false;
        float delta = Mathf.DeltaAngle(from * Mathf.Rad2Deg, to * Mathf.Rad2Deg) * Mathf.Deg2Rad;
        float radius = FilmDirection.F(a.Json["radius_m"], 0.6f);
        float sweep = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((film - a.F0) / 0.6f));
        if (a.F0 <= 1e-4f) sweep = 1f;
        Color col = Lin(stepColour);
        float w = FloorWidth(c, 6f);
        // her front: a short tick on the arc's start
        Vector3 f0 = c + new Vector3(Mathf.Cos(from), 0, Mathf.Sin(from)) * (radius - w * 3f);
        Vector3 f1 = c + new Vector3(Mathf.Cos(from), 0, Mathf.Sin(from)) * (radius + w * 3f);
        floor.FloorStrip(f0, f1, w * 1.2f, A(Color.white, 0.85f * alpha));
        Arc(c, radius, from, delta * sweep, w, col, alpha, true);
        AvoidArc(c, radius, from, delta * sweep, a.Id);
        if (!string.IsNullOrEmpty(a.Text))
        {
            float m = from + delta * 0.5f * sweep;
            Vector3 lp = c + new Vector3(Mathf.Cos(m), 0, Mathf.Sin(m)) * (radius + 0.05f);
            if (OnScreen(lp, out Vector2 s) && OnScreen(c, out Vector2 cs))
            {
                Vector2 d = (s - cs).sqrMagnitude > 1f ? (s - cs).normalized : Vector2.up;
                reqs.Add(new LabelReq { Id = a.Id, Text = a.Text, Anchor = s, Dir = d, Accent = stepColour, Alpha = alpha });
            }
        }

        return true;
    }

    /// <summary>an arc on the floor from angle a0 (rad) over delta, optionally with an arrowhead at its end</summary>
    void Arc(Vector3 c, float radius, float a0, float delta, float w, Color col, float alpha, bool head)
    {
        int n = Mathf.Max(6, Mathf.CeilToInt(Mathf.Abs(delta) * Mathf.Rad2Deg / 4f));
        Vector3 Pt(float ang) => c + new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)) * radius;
        float headAng = head ? Mathf.Sign(delta) * Mathf.Min(Mathf.Abs(delta) * 0.5f, (w * 4.5f) / Mathf.Max(0.05f, radius)) : 0f;
        float end = a0 + delta - headAng;
        for (int i = 0; i < n; i++)
        {
            float u0 = a0 + (end - a0) * i / n, u1 = a0 + (end - a0) * (i + 1) / n;
            floor.FloorStrip(Pt(u0), Pt(u1), w + w * 0.9f, A(Dark, 0.5f * alpha));
        }

        for (int i = 0; i < n; i++)
        {
            float u0 = a0 + (end - a0) * i / n, u1 = a0 + (end - a0) * (i + 1) / n;
            floor.FloorStrip(Pt(u0), Pt(u1), w, A(col, alpha));
            glow.FloorStrip(Pt(u0), Pt(u1), w * 3f, G(col, 0.25f * alpha));
        }

        if (!head || Mathf.Abs(delta) < 1e-3f) return;
        Vector3 tipP = Pt(a0 + delta), baseP = Pt(end);
        Vector3 radial = (baseP - c).normalized;
        float hw = w * 2.2f;
        floor.Triangle(tipP + (tipP - baseP).normalized * w * 0.6f, baseP + radial * (hw + w * 0.6f), baseP - radial * (hw + w * 0.6f), A(Dark, 0.5f * alpha));
        floor.Triangle(tipP, baseP + radial * hw, baseP - radial * hw, A(col, alpha));
        glow.Triangle(tipP, baseP + radial * hw * 1.4f, baseP - radial * hw * 1.4f, G(col, 0.3f * alpha));
    }

    bool FloorWedge(FilmAnnotation a, float dance, float alpha)
    {
        if (!Resolve(a.Json["centre"], dance, out Vector3 c)) return false;
        c = Floor(c, 0.01f);
        float from = Bearing(a.Json.Value<string>("from_bearing") ?? "her_facing", dance, c, out bool ok);
        if (!ok) return false;
        if (a.Json["range_deg"] is not JArray r || r.Count != 2) return false;
        float lo = from + r[0].Value<float>() * Mathf.Deg2Rad, hi = from + r[1].Value<float>() * Mathf.Deg2Rad;
        float radius = FilmDirection.F(a.Json["radius_m"], 0.6f);
        Color col = Lin(wedgeColour);
        Color edge = Lin(stepColour);
        int n = Mathf.Max(4, Mathf.CeilToInt((hi - lo) * Mathf.Rad2Deg / 4f));
        for (int i = 0; i < n; i++)
        {
            float u0 = lo + (hi - lo) * i / n, u1 = lo + (hi - lo) * (i + 1) / n;
            floor.Triangle(c, c + new Vector3(Mathf.Cos(u0), 0, Mathf.Sin(u0)) * radius, c + new Vector3(Mathf.Cos(u1), 0, Mathf.Sin(u1)) * radius,
                new Color(col.r, col.g, col.b, Mathf.Max(col.a, 0.18f) * alpha));
        }

        float w = FloorWidth(c, 3f);
        foreach (float ang in new[] { lo, hi })
        {
            floor.FloorStrip(c, c + new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)) * radius, w, A(edge, 0.55f * alpha));
        }

        Arc(c, radius, lo, hi - lo, w, A(edge, 0.55f), alpha, false);
        if (!string.IsNullOrEmpty(a.Text))
        {
            float m = (lo + hi) * 0.5f;
            Vector3 lp = c + new Vector3(Mathf.Cos(m), 0, Mathf.Sin(m)) * radius * 0.75f;
            if (OnScreen(lp, out Vector2 s) && OnScreen(c, out Vector2 cs))
            {
                Vector2 d = (s - cs).sqrMagnitude > 1f ? (s - cs).normalized : Vector2.down;
                reqs.Add(new LabelReq { Id = a.Id, Text = a.Text, Anchor = s, Dir = d, Accent = stepColour, Alpha = 0.9f * alpha });
            }
        }

        return true;
    }

    bool TurnArc(FilmAnnotation a, float dance, float alpha)
    {
        if (!Resolve(a.Json["centre"], dance, out Vector3 c)) return false;
        c = Floor(c, 0.016f);
        float radius = FilmDirection.F(a.Json["radius_m"], 0.65f);
        bool cw = !string.Equals(a.Json.Value<string>("direction"), "ccw", StringComparison.OrdinalIgnoreCase);
        float sign = cw ? -1f : 1f; // Unity top view: + atan2(z, x) = counter-clockwise seen from above
        // the arc faces the camera (its middle on the camera's side) and turns slowly the way the couple turns
        Vector3 toCam = cam.transform.position - c;
        float mid = Mathf.Atan2(toCam.z, toCam.x);
        if (new Vector2(toCam.x, toCam.z).sqrMagnitude < 0.04f) mid = -Mathf.PI * 0.5f;
        float spin = sign * 50f * Mathf.Deg2Rad * (film - a.F0);
        float span = 200f * Mathf.Deg2Rad;
        float a0 = mid - sign * span * 0.5f + spin;
        Color col = Lin(new Color(1f, 0.85f, 0.1f));
        float w = FloorWidth(c, 7f);
        float grow = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((film - a.F0) / 0.5f));
        Arc(c, radius, a0, sign * span * Mathf.Max(0.08f, grow), w, col, alpha, true);
        AvoidArc(c, radius, a0, sign * span * Mathf.Max(0.08f, grow), a.Id);
        if (!string.IsNullOrEmpty(a.Text))
        {
            Vector3 lp = c + new Vector3(Mathf.Cos(mid), 0, Mathf.Sin(mid)) * (radius + 0.06f);
            if (OnScreen(lp, out Vector2 s) && OnScreen(c, out Vector2 cs))
            {
                Vector2 d = (s - cs).sqrMagnitude > 1f ? (s - cs).normalized : Vector2.down;
                reqs.Add(new LabelReq { Id = a.Id, Text = a.Text, Anchor = s, Dir = d, Accent = coupleColour, Alpha = alpha });
            }
        }

        return true;
    }

    bool PullArrows(FilmAnnotation a, float dance, float alpha)
    {
        if (a.Json["between"] is not JArray b || b.Count != 2) return false;
        if (!Resolve(b[0], dance, out Vector3 p0) || !Resolve(b[1], dance, out Vector3 p1)) return false;
        Vector3 f0 = Floor(p0, 0.02f), f1 = Floor(p1, 0.02f);
        Vector3 d = f1 - f0;
        float dist = d.magnitude;
        if (dist < 0.05f) return false;
        d /= dist;
        float age = film - a.F0;
        float dur = Mathf.Max(0.3f, a.F1 - a.F0);
        // the arrows build over the call-out (the radial pull rising): "scale_from" = the first value / the last value
        // (70 -> 180 N: 0.39); each arrow is about a quarter of the 9:16 width long (a tenth of the 16:9 width), thick and
        // bold, but never reaches past the middle between the two centres of mass
        float scaleFrom = Mathf.Clamp(FilmDirection.F(a.Json["scale_from"], 0.55f), 0.15f, 1f);
        float build = Mathf.Lerp(scaleFrom, 1f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(age / dur)));
        float pulse = 1f + 0.06f * Mathf.Sin(2f * Mathf.PI * 1.6f * age);
        float grow = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(age / 0.35f));
        float pwMid = PixelWorld((f0 + f1) * 0.5f);
        float fullLen = (director.Vertical ? 250f : 190f) * unit * pwMid;
        float len = fullLen * build * pulse * Mathf.Max(0.1f, grow);
        Color col = tensionColour;
        // each COM's arrow is the pull on it: a force vector that starts at its floor point and points at the partner. The
        // two COMs are often only a few decimetres apart, so the arrows run past each other in their own lanes (a little
        // to either side of the line between the two) instead of being squeezed into the gap, at a size that reads
        Vector3 lane = Vector3.Cross(Vector3.up, d).normalized * (15f * unit * pwMid);
        Vector3 laneA = f0 + lane, laneB = f1 - lane;
        FloorArrow(laneA, laneA + d * len, col, alpha, 2.1f);
        FloorArrow(laneB, laneB - d * len, col, alpha, 2.1f);
        Avoid(laneA, laneA + d * len, a.Id);
        Avoid(laneB, laneB - d * len, a.Id);
        foreach (Vector3 p in new[] { f0, f1 })
        {
            float pw = PixelWorld(p);
            floor.Disc(p, 10f * unit * pw, A(Lin(col), alpha), A(Lin(col), 0.6f * alpha), 16);
        }

        if (!string.IsNullOrEmpty(a.Text) && OnScreen((f0 + f1) * 0.5f, out Vector2 s) && OnScreen(f0, out Vector2 s0) && OnScreen(f1, out Vector2 s1))
        {
            Vector2 along = (s1 - s0).sqrMagnitude > 1f ? (s1 - s0).normalized : Vector2.right;
            Vector2 perp = new(-along.y, along.x);
            if (perp.y > 0) perp = -perp; // below the pair on screen
            reqs.Add(new LabelReq { Id = a.Id, Text = a.Text, Anchor = s + perp * 60f * unit, Dir = perp, Accent = col, Alpha = alpha });
        }

        return true;
    }

    /// <summary>a flat arrow lying on the floor (pull arrows): drawn on top, widths in pixels (k = a size factor)</summary>
    void FloorArrow(Vector3 from, Vector3 to, Color colour, float alpha, float k = 1f)
    {
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 1e-4f) return;
        Vector3 dir = d / len;
        float pw = PixelWorld((from + to) * 0.5f);
        float w = 9f * k * unit * pw, o = 3f * unit * pw;
        float headLen = Mathf.Min(len * 0.5f, 26f * k * unit * pw), hw = 15f * k * unit * pw;
        Vector3 side = Vector3.Cross(Vector3.up, dir).normalized;
        Vector3 baseC = to - dir * headLen;
        Color c = Lin(colour);
        top.Strip(from - dir * o, baseC, w + 2 * o, A(Dark, alpha), A(Dark, alpha), Vector3.up);
        top.Triangle(to + dir * o * 2f, baseC - dir * o + side * (hw + o * 1.7f), baseC - dir * o - side * (hw + o * 1.7f), A(Dark, alpha));
        top.Strip(from, baseC + dir * o * 0.5f, w, A(c, alpha), A(c, alpha), Vector3.up);
        top.Triangle(to, baseC + side * hw, baseC - side * hw, A(c, alpha));
        glow.Strip(from, baseC, w * 3f, G(c, 0.3f * alpha), G(c, 0.3f * alpha), Vector3.up);
    }

    bool ComMarkers(FilmAnnotation a, float dance, float alpha)
    {
        if (a.Json["targets"] is not JArray list) return false;
        bool any = false;
        foreach (JToken t in list)
        {
            if (t is not JObject o || !director.Targets.Resolve(o, dance, out Vector3 p)) continue;
            string type = o.Value<string>("type");
            bool couple = type == "couple_com";
            Color col = couple ? coupleColour : string.Equals(o.Value<string>("dancer"), "follow", StringComparison.OrdinalIgnoreCase) ? followColour : leadColour;
            float pw = PixelWorld(p);
            float r = (couple ? 9f : 11f) * unit * pw;
            Color c = Lin(col);
            top.Sphere(p, r + 3f * unit * pw, A(Dark, alpha));
            top.Sphere(p, r, A(c, alpha));
            glow.Sphere(p, r * 2.2f, G(c, 0.3f * alpha));
            if (!couple)
            {
                // where it sits over the floor: a small floor dot in the same colour (no connector line)
                Vector3 fp = Floor(p, 0.012f);
                floor.Disc(fp, 6f * unit * PixelWorld(fp), A(c, 0.7f * alpha), A(c, 0.25f * alpha), 16);
            }

            any = true;
        }

        return any;
    }

    // ------------------------------------------------------------------ labels

    void Readout(string id, string text, Color accent, float alpha, bool small = false)
    {
        if (string.IsNullOrEmpty(text) || alpha <= 0.002f) return;
        reqs.Add(new LabelReq { Id = id, Text = text, Accent = accent, Alpha = alpha, Readout = true, Size = small ? -1 : 0 });
    }

    // ---- label layout state (a pure function of film time + the previous layout: seeks and the first frame snap)
    readonly Dictionary<string, Vector2> lastTarget = new();  // label id -> chosen centre last frame (hysteresis)
    readonly Dictionary<string, int> lastVariant = new();     // label id -> 0 normal wrap, 1 narrow wrap (hysteresis)
    readonly Dictionary<string, Vector2> shownPos = new();    // label id -> displayed centre last frame (smoothing)
    // arrow shafts, arcs and pull arrows of this frame in screen px: a label keeps its text off them
    readonly List<(Vector2 a, Vector2 b, string owner)> avoidSegs = new();
    readonly HashSet<string> shownNow = new();
    readonly List<string> staleIds = new();
    readonly List<Rect> placed = new();
    readonly List<LeaderView> leaders = new();
    float lastLayoutFilm = float.NaN;
    float danceNow;

    /// <summary>screen rectangles (px, bottom-left origin) the labels keep out of: the film overlay's move caption and
    /// graph inset in 16:9. Refilled by FilmOverlay every frame before this layout runs.</summary>
    public readonly List<Rect> Obstacles = new();

    class LeaderView
    {
        public RectTransform Rt;
        public Image Dark, Line;
    }

    /// <summary>register a world segment (an arrow shaft, a pull arrow) as something labels keep their text off</summary>
    void Avoid(Vector3 w0, Vector3 w1, string owner)
    {
        if (OnScreen(w0, out Vector2 s0) && OnScreen(w1, out Vector2 s1)) avoidSegs.Add((s0, s1, owner));
    }

    /// <summary>register a floor arc (a turn arc / floor arc) as a polyline of screen segments</summary>
    void AvoidArc(Vector3 c, float radius, float a0, float delta, string owner)
    {
        const int n = 14;
        Vector3 prev = c + new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) * radius;
        for (int i = 1; i <= n; i++)
        {
            float ang = a0 + delta * i / n;
            Vector3 q = c + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * radius;
            Avoid(prev, q, owner);
            prev = q;
        }
    }

    /// <summary>how many sample points (every ~14 px) of this frame's arrows and arcs lie inside the label rectangle
    /// (grown a little); the label's own arrow is not counted near its anchor, where it starts</summary>
    int ArrowHits(Rect rect, string owner, Vector2 anchor)
    {
        if (avoidSegs.Count == 0) return 0;
        float m = 8f * unit, step = 14f * unit, own = 26f * unit;
        Rect g = new(rect.x - m, rect.y - m, rect.width + 2f * m, rect.height + 2f * m);
        int hits = 0;
        foreach ((Vector2 a, Vector2 b, string o) in avoidSegs)
        {
            // only the part of the segment inside the label's rectangle is sampled (a point just in front of the near
            // plane projects far off screen: the cost stays bounded by the rectangle, never by the segment's length)
            if (!ClipToRect(g, a, b, out Vector2 c0, out Vector2 c1)) continue;
            Vector2 d = c1 - c0;
            int n = Mathf.Clamp(Mathf.CeilToInt(d.magnitude / step), 1, 64);
            for (int k = 0; k <= n; k++)
            {
                Vector2 p = c0 + d * (k / (float)n);
                if (o == owner && (p - anchor).sqrMagnitude < own * own) continue;
                hits++;
            }
        }

        return hits;
    }

    /// <summary>Liang-Barsky: the part of the segment a-b inside the rectangle (false = none)</summary>
    static bool ClipToRect(Rect r, Vector2 a, Vector2 b, out Vector2 c0, out Vector2 c1)
    {
        c0 = a;
        c1 = b;
        float dx = b.x - a.x, dy = b.y - a.y;
        if (!float.IsFinite(dx) || !float.IsFinite(dy) || !float.IsFinite(a.x) || !float.IsFinite(a.y)) return false;
        float t0 = 0f, t1 = 1f;
        if (!ClipEdge(-dx, a.x - r.xMin, ref t0, ref t1) || !ClipEdge(dx, r.xMax - a.x, ref t0, ref t1) ||
            !ClipEdge(-dy, a.y - r.yMin, ref t0, ref t1) || !ClipEdge(dy, r.yMax - a.y, ref t0, ref t1))
        {
            return false;
        }

        c0 = new Vector2(a.x + dx * t0, a.y + dy * t0);
        c1 = new Vector2(a.x + dx * t1, a.y + dy * t1);
        return true;
    }

    static bool ClipEdge(float p, float q, ref float t0, ref float t1)
    {
        if (Mathf.Abs(p) < 1e-9f) return q >= 0f;
        float t = q / p;
        if (p < 0f)
        {
            if (t > t1) return false;
            if (t > t0) t0 = t;
        }
        else
        {
            if (t < t0) return false;
            if (t < t1) t1 = t;
        }

        return true;
    }

    Vector2 BoxOf(string[] lines, int size, Vector2 pad)
    {
        float w = 0f;
        foreach (string l in lines) w = Mathf.Max(w, FilmUi.Width(l, size, FontStyle.Bold));
        return new Vector2(w + 2f * pad.x + 10f * unit, lines.Length * size * 1.18f + 2f * pad.y);
    }

    static float OverlapArea(Rect a, Rect b)
    {
        float w = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
        float h = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
        return w > 0f && h > 0f ? w * h : 0f;
    }

    static readonly SmplJoint[] CoupleJoints =
    {
        SmplJoint.Head, SmplJoint.Pelvis, SmplJoint.Spine3, SmplJoint.L_Ankle, SmplJoint.R_Ankle, SmplJoint.L_Wrist, SmplJoint.R_Wrist
    };

    /// <summary>screen rectangle of both dancers (head, pelvis, feet, hands) at the shown dance time, padded: the area
    /// labels prefer not to cover</summary>
    bool CoupleRect(out Rect rect)
    {
        rect = default;
        FilmTargets t = director.Targets;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        int n = 0;
        foreach (string who in new[] { "lead", "follow" })
        {
            Dancer d = t.DancerOf(who);
            foreach (SmplJoint j in CoupleJoints)
            {
                Vector3 p = t.Joint(d, j, danceNow);
                if (float.IsNaN(p.x) || !OnScreen(p, out Vector2 s)) continue;
                x0 = Mathf.Min(x0, s.x);
                x1 = Mathf.Max(x1, s.x);
                y0 = Mathf.Min(y0, s.y);
                y1 = Mathf.Max(y1, s.y);
                n++;
            }
        }

        if (n < 4) return false;
        float pad = 28f * unit;
        rect = Rect.MinMaxRect(x0 - pad, y0 - pad, x1 + pad, y1 + pad);
        return true;
    }

    /// <summary>size, wrap and place every label request inside the safe area. Readouts stack (fixed: 9:16 upward from just
    /// above the captions, 16:9 down the top-left corner, off the dancers' heads); every other label is placed greedily, in
    /// annotation order, at the best of ~130 candidates (~64 positions around its anchor, each with its normal and, when
    /// the margins beside the couple are slim, a narrower wrap): no overlap with the readouts, the HUD boxes or the labels
    /// already placed, no arrow shaft / arc running through it, the couple's rectangle kept clear (the side margins take
    /// the labels), close to the anchor, and where it was last frame (hysteresis); it then glides there (14 /s) and a
    /// thin leader line ties it to its arrow when it sits away from it</summary>
    void LayoutLabels(FilmAspect aspect, bool vertical)
    {
        float W = Screen.width, H = Screen.height;
        FilmOverlay ov = director.Overlay;
        float floorY = ov != null ? ov.LabelFloor : (vertical ? 0.32f * H : 0.17f * H);
        float topY = vertical ? (1f - aspect.SafeTop) * H - 8f * unit : (1f - Mathf.Max(0.04f, aspect.SafeTop)) * H;
        if (ov != null) topY = Mathf.Min(topY, ov.LabelCeiling);
        Rect safe = Rect.MinMaxRect(vertical ? 0.035f * W : 0.03f * W, floorY, vertical ? 0.965f * W : 0.97f * W, topY);
        int baseSize = Mathf.RoundToInt((vertical ? 36f : 29f) * unit);
        float maxW = vertical ? 0.54f * W : 0.30f * W;
        Vector2 pad = new Vector2(16f, 8f) * unit;

        // the couple's rectangle and the margins left beside it: a label may wrap narrower to sit in a slim margin
        bool haveCouple = CoupleRect(out Rect couple);
        float marginW = haveCouple ? Mathf.Max(couple.xMin - safe.xMin, safe.xMax - couple.xMax) - 14f * unit : 0f;
        float narrowW = Mathf.Min(maxW, marginW) - 2f * pad.x - 10f * unit;
        bool canNarrow = haveCouple && marginW > 0.2f * W && narrowW > 0.1f * W;

        for (int i = 0; i < reqs.Count; i++)
        {
            LabelReq r = reqs[i];
            r.Size = r.Size < 0 ? Mathf.RoundToInt(baseSize * 0.82f) : baseSize;
            r.Lines = Wrap(r.Text, r.Size, maxW - 2 * pad.x);
            r.Box = BoxOf(r.Lines, r.Size, pad);
            r.LinesN = null;
            if (!r.Readout && canNarrow)
            {
                string[] narrow = Wrap(r.Text, r.Size, narrowW);
                if (narrow.Length > r.Lines.Length)
                {
                    r.LinesN = narrow;
                    r.BoxN = BoxOf(narrow, r.Size, pad);
                }
            }

            reqs[i] = r;
        }

        placed.Clear();
        // readouts: a stack (9:16: upward from just above the captions; 16:9: downward from the top of the safe area)
        float stackY = vertical ? floorY : topY;
        foreach (int i in Enumerable.Range(0, reqs.Count).Where(i => reqs[i].Readout).OrderBy(i => reqs[i].Size))
        {
            LabelReq r = reqs[i];
            float y = vertical ? stackY + r.Box.y * 0.5f : stackY - r.Box.y * 0.5f;
            // 16:9: down the top-left corner (the couple stands mid-frame, the raised arms reach the top centre)
            r.Pos = new Vector2(vertical ? W * 0.5f : safe.xMin + r.Box.x * 0.5f, y);
            r.Fixed = true;
            stackY += vertical ? r.Box.y + 8f * unit : -(r.Box.y + 8f * unit);
            reqs[i] = r;
            placed.Add(new Rect(r.Pos - r.Box * 0.5f, r.Box));
        }

        foreach (Rect o in Obstacles) placed.Add(o);

        float dt = film - lastLayoutFilm;
        bool smooth = !float.IsNaN(lastLayoutFilm) && dt > 0f && dt < 0.25f;
        lastLayoutFilm = film;
        float follow = smooth ? 1f - Mathf.Exp(-14f * dt) : 1f;
        shownNow.Clear();

        for (int i = 0; i < reqs.Count; i++)
        {
            LabelReq r = reqs[i];
            if (r.Readout) continue;
            string id = r.Id ?? $"label{i}";
            Vector2 pref = r.Dir.sqrMagnitude > 1e-6f ? r.Dir.normalized : Vector2.up;
            Vector2 best = Vector2.zero;
            int bestVariant = 0;
            float bestScore = float.MaxValue;
            bool hasPrev = smooth && lastTarget.ContainsKey(id);
            Vector2 prev = hasPrev ? lastTarget[id] : Vector2.zero;
            int prevVariant = hasPrev && lastVariant.TryGetValue(id, out int pv) ? pv : 0;
            int variants = r.LinesN != null ? 2 : 1;
            // candidates: the natural spot, last frame's spot, then 16 bearings x 4 distances around the anchor; each with
            // the normal wrap and (when the margins beside the couple are slim) the narrow one
            int total = 2 + 16 * 4;
            for (int v = 0; v < variants; v++)
            {
                Vector2 box = v == 0 ? r.Box : r.BoxN;
                Vector2 half = box * 0.5f;
                for (int c = 0; c < total; c++)
                {
                    Vector2 dir;
                    float extra;
                    if (c == 0)
                    {
                        dir = pref;
                        extra = 0f;
                    }
                    else if (c == 1)
                    {
                        if (!hasPrev) continue;
                        dir = Vector2.zero;
                        extra = 0f;
                    }
                    else
                    {
                        int k = c - 2;
                        float ang = (k % 16) * Mathf.PI * 2f / 16f;
                        dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                        extra = (k / 16) * 70f * unit;
                    }

                    Vector2 pos = c == 1
                        ? prev
                        : r.Anchor + dir * (Mathf.Abs(dir.x) * half.x + Mathf.Abs(dir.y) * half.y + 10f * unit + extra);
                    Vector2 clamped = Clamp(pos, half, safe);
                    float clampMove = (clamped - pos).magnitude;
                    pos = clamped;
                    Rect rect = new(pos - half, box);
                    float area = Mathf.Max(1f, rect.width * rect.height);
                    float score = clampMove * 2.2f;
                    foreach (Rect p in placed)
                    {
                        Rect grown = new(p.x - 6f * unit, p.y - 6f * unit, p.width + 12f * unit, p.height + 12f * unit);
                        score += 6000f * OverlapArea(rect, grown) / area;
                    }

                    if (haveCouple) score += 2600f * OverlapArea(rect, couple) / area;  // the keep-out round the dancers
                    score += 90f * ArrowHits(rect, id, r.Anchor);                        // an arrow shaft / arc through the text
                    score += 0.7f * (pos - r.Anchor).magnitude / unit;
                    if (c > 1) score += 35f * (1f - Vector2.Dot(dir, pref)); // bearing away from the natural side
                    if (c == 1) score -= 90f;                                // hysteresis: stay where it was
                    if (v == 1) score += 18f;                                // the narrow wrap only when it earns it
                    if (hasPrev && v != prevVariant) score += 25f;           // ... and keep the wrap it had
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = pos;
                        bestVariant = v;
                    }
                }
            }

            if (bestVariant == 1)
            {
                r.Lines = r.LinesN;
                r.Box = r.BoxN;
            }

            placed.Add(new Rect(best - r.Box * 0.5f, r.Box));
            lastTarget[id] = best;
            lastVariant[id] = bestVariant;
            Vector2 shown = smooth && shownPos.TryGetValue(id, out Vector2 sp) ? Vector2.Lerp(sp, best, follow) : best;
            shownPos[id] = shown;
            r.Pos = shown;
            reqs[i] = r;
            shownNow.Add(id);
        }

        // forget labels that were not shown this frame (a returning label snaps instead of gliding from far away)
        staleIds.Clear();
        foreach (string id in shownPos.Keys)
        {
            if (!shownNow.Contains(id)) staleIds.Add(id);
        }

        foreach (string id in staleIds)
        {
            shownPos.Remove(id);
            lastTarget.Remove(id);
            lastVariant.Remove(id);
        }

        lastLabelRects.Clear();
        int shownCount = 0, leaderCount = 0;
        foreach (LabelReq r in reqs)
        {
            LabelView v = View(shownCount++);
            Show(v, r);
            Rect rect = new(r.Pos - r.Box * 0.5f, r.Box);
            lastLabelRects[r.Id ?? $"label{shownCount}"] = rect;
            if (r.Readout) continue;
            Vector2 q = new(Mathf.Clamp(r.Anchor.x, rect.xMin, rect.xMax), Mathf.Clamp(r.Anchor.y, rect.yMin, rect.yMax));
            if ((q - r.Anchor).magnitude > 16f * unit) Leader(leaderCount++, r.Anchor, q, r.Accent, r.Alpha);
        }

        HideLabels(shownCount);
        for (int i = leaderCount; i < leaders.Count; i++)
        {
            if (leaders[i].Rt.gameObject.activeSelf) leaders[i].Rt.gameObject.SetActive(false);
        }
    }

    /// <summary>a thin line on screen from a label to its arrow's tail (drawn only when the label sits away from it)</summary>
    void Leader(int i, Vector2 from, Vector2 to, Color accent, float alpha)
    {
        while (leaders.Count <= i)
        {
            RectTransform rt = FilmUi.Rect(labelRoot, $"Call-out leader {leaders.Count}");
            leaders.Add(new LeaderView { Rt = rt, Dark = FilmUi.Box(rt, "Dark", null, Color.black), Line = FilmUi.Box(rt, "Line", null, Color.white) });
        }

        LeaderView l = leaders[i];
        if (!l.Rt.gameObject.activeSelf) l.Rt.gameObject.SetActive(true);
        Vector2 d = to - from;
        float len = d.magnitude;
        float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        RectTransform r = l.Rt;
        r.anchorMin = r.anchorMax = Vector2.zero;
        r.pivot = new Vector2(0.5f, 0.5f);
        r.anchoredPosition = (from + to) * 0.5f;
        r.sizeDelta = new Vector2(len, 1f);
        r.localRotation = Quaternion.Euler(0f, 0f, angle);
        // the children are centred in the leader's own (rotated) space
        SetCentred(l.Dark.rectTransform, new Vector2(len, 6f * unit));
        SetCentred(l.Line.rectTransform, new Vector2(len, 2.6f * unit));
        l.Dark.color = new Color(0.02f, 0.02f, 0.03f, 0.55f * alpha);
        Color c = accent;
        c.a = 0.95f * alpha;
        l.Line.color = c;
    }

    static void SetCentred(RectTransform rt, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = size;
    }

    static Vector2 Clamp(Vector2 p, Vector2 half, Rect safe)
    {
        p.x = Mathf.Clamp(p.x, safe.xMin + half.x, Mathf.Max(safe.xMin + half.x, safe.xMax - half.x));
        p.y = Mathf.Clamp(p.y, safe.yMin + half.y, Mathf.Max(safe.yMin + half.y, safe.yMax - half.y));
        return p;
    }

    static string[] Wrap(string text, int size, float maxW)
    {
        if (FilmUi.Width(text, size, FontStyle.Bold) <= maxW) return new[] { text };
        string[] words = text.Split(' ');
        List<string> lines = new();
        string cur = "";
        foreach (string w in words)
        {
            string tryLine = cur.Length == 0 ? w : cur + " " + w;
            if (cur.Length > 0 && FilmUi.Width(tryLine, size, FontStyle.Bold) > maxW)
            {
                lines.Add(cur);
                cur = w;
            }
            else
            {
                cur = tryLine;
            }
        }

        if (cur.Length > 0) lines.Add(cur);
        return lines.ToArray();
    }

    LabelView View(int i)
    {
        while (pool.Count <= i)
        {
            RectTransform rt = FilmUi.Rect(labelRoot, $"Call-out label {pool.Count}");
            Image back = rt.gameObject.AddComponent<Image>();
            back.sprite = FilmUi.Rounded;
            back.type = Image.Type.Sliced;
            back.raycastTarget = false;
            Image accent = FilmUi.Box(rt, "Accent", FilmUi.Rounded, Color.white);
            Text t = FilmUi.Label(rt, "Text", 30, Color.white, TextAnchor.MiddleCenter, true);
            t.alignment = TextAnchor.MiddleCenter;
            pool.Add(new LabelView { Rt = rt, Back = back, Accent = accent, Text = t });
        }

        return pool[i];
    }

    void Show(LabelView v, LabelReq r)
    {
        if (!v.Rt.gameObject.activeSelf) v.Rt.gameObject.SetActive(true);
        string text = string.Join("\n", r.Lines);
        if (v.Shown != text || v.Size != r.Size)
        {
            v.Shown = text;
            v.Size = r.Size;
            v.Text.text = text;
            v.Text.fontSize = r.Size;
            v.Text.lineSpacing = 1.0f;
        }

        FilmUi.Place(v.Rt, r.Pos, r.Box);
        v.Back.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 12f * unit);
        Color pc = pillColour;
        pc.a *= r.Alpha;
        v.Back.color = pc;
        float accentW = 6f * unit;
        RectTransform at = v.Accent.rectTransform;
        at.anchorMin = new Vector2(0f, 0.5f);
        at.anchorMax = new Vector2(0f, 0.5f);
        at.pivot = new Vector2(0f, 0.5f);
        at.anchoredPosition = new Vector2(7f * unit, 0f);
        at.sizeDelta = new Vector2(accentW, r.Box.y - 14f * unit);
        v.Accent.pixelsPerUnitMultiplier = 24f / Mathf.Max(1f, 3f * unit);
        Color ac = r.Accent;
        ac.a = r.Alpha;
        v.Accent.color = ac;
        RectTransform tt = v.Text.rectTransform;
        tt.anchorMin = Vector2.zero;
        tt.anchorMax = Vector2.one;
        tt.pivot = new Vector2(0.5f, 0.5f);
        tt.offsetMin = new Vector2(10f * unit, 0f);
        tt.offsetMax = Vector2.zero;
        Color tc = r.Readout && r.Size < Mathf.RoundToInt((director.Vertical ? 36f : 29f) * unit) ? new Color(1f, 0.9f, 0.45f) : labelText;
        tc.a = r.Alpha;
        v.Text.color = tc;
        v.Box = r.Box;
    }

    void HideLabels(int from)
    {
        for (int i = from; i < pool.Count; i++)
        {
            if (pool[i].Rt.gameObject.activeSelf) pool[i].Rt.gameObject.SetActive(false);
        }
    }

    public Dictionary<string, object> State() => new()
    {
        ["active"] = active.ToList(),
        ["labels"] = lastLabelRects.ToDictionary(kv => kv.Key, kv => (object)new[]
        {
            Mathf.Round(kv.Value.x), Mathf.Round(kv.Value.y), Mathf.Round(kv.Value.width), Mathf.Round(kv.Value.height)
        }),
        ["unresolved"] = unresolved.ToList(),
        ["vertices"] = top != null ? top.VertexCount + floor.VertexCount + glow.VertexCount : 0
    };

    void OnDestroy()
    {
        if (topMat != null) Destroy(topMat);
        if (floorMat != null) Destroy(floorMat);
        if (glowMat != null) Destroy(glowMat);
    }
}
