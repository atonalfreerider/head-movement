using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// Layer "graph" and the two graph view states (VIEWER_SPEC 3.10, 3.11, 4), driven by the move timeline
/// (moves/labels.json + path.json: for the demo takes the user's narration, provenance "narration"):
/// - Static: the zouk graph standing in the dance environment (the dance keeps playing inside it).
/// - Path ("Dance graph"): a miniature couple (travel removed) stands at the node of the current move and glides
///   along the link to the next node, arriving exactly at the next segment start; a fading trail marks the path;
///   new links (transitions the graph has no link for) are dashed. Where an unlabelled move interrupts the path
///   (a narrated phrase no move matched: labels.json "unresolved", path.json break_before) the couple leaves the node
///   when its move ends and crosses the gap on a dotted grey line, arriving at the next node by its segment start;
///   no node is lit meanwhile. Chase() gives the deterministic chase-camera pose (behind/above, looking ahead).
/// - Fingerprint: the time-independent heat map: node glow and size by dwell seconds, unvisited nodes dim, links
///   thickened by how often they were taken, side panel with the energy-band totals and summary numbers.
/// - The move caption (3.12, layer "moves", throughout directed playback): Brazilian Portuguese name, English alias,
///   confidence (narration: the phrase-to-move mapping confidence), provenance tag, the next move (always in the
///   Dance graph state, else in the last beat); spans with no move show "unlabelled", never a neighbouring move.
///   Below UncertainBelow: "uncertain" with the top two candidates for automatic labels and for narrated items whose
///   NAME match was doubtful; a narrated name that matched exactly / by alias but was heard faintly by the speech
///   recogniser (the low number is the recogniser's word probability) keeps the single narrated name with its low
///   bar and "heard faintly - recogniser NN %": the caption never offers a move nobody named (review 2026-10-08).
///   Changes on the first frame at or after a span start.
/// - The graph inset (3.12) during directed playback outside the graph states: the current node lit, the path so far.
/// </summary>
public class DanceGraphLayer : MonoBehaviour
{
    public enum Mode
    {
        Off,
        Static,
        Path,
        Fingerprint
    }

    public float GlideBeats = 1f;
    public float MiniScale = 0.15f;
    // chase camera (user 2026-10-06: "zoomed out much farther; we can be inside of the graph a little bit"):
    // distance behind / height above the miniature couple, x = while gliding, y = while dwelling on a node (m)
    public Vector2 ChaseBack = new(2.6f, 3.4f);
    public Vector2 ChaseUp = new(1.1f, 1.5f);
    // move caption (VIEWER_SPEC 3.12)
    public float UncertainBelow = 0.5f;
    // a narrated name match at or above this score (exact / alias 100, stem 96) is not in doubt: a low confidence then
    // comes from the recogniser (word probability) and the caption says "heard faintly" instead of "uncertain"
    public float SureNameScore = 95f;
    public float CrossFadeSeconds = 0.15f;
    // a gap shorter than this before a break is crossed like a normal glide (the last beat of the previous move)
    public float MinGapGlideSeconds = 0.25f;

    DanceGraphData data;
    DanceGraphView view;
    MiniatureCouple mini;
    GlowMesh trail;
    Material trailMaterial;
    DanceHud hud;
    float beatSeconds = 0.785f;
    Mode mode = Mode.Off;
    float fade = 1f;

    // path
    DanceGraphData.Step[] steps;
    Vector3[][] polylines; // per step: prev node -> via -> node (base points)
    float[][] cumulative; // arc length along each polyline
    float[] glideStart;
    bool[] gapGlide; // the glide into this step crosses an unlabelled gap (break)
    readonly List<(int from, int to, int count)> newLinks = new();
    List<int>[] nodeLinks; // link indices touching each node
    readonly Dictionary<string, int> linkIndex = new();
    float[] pathLinkWeight;
    int styledStep = int.MinValue;
    bool styledGap;
    float lastTime = float.NaN;
    int lastFrame = -1;
    string badge;

    // move caption + inset
    class MoveCaption
    {
        public string Kind, Tag, Name, Alias, ConfText, Move, Node, Provenance, Phrase, Id, Info;
        public float? Value;
        public float T0, T1;
        public int Step = -1, NodeIndex = -1;
        public Color NameColor = Color.white;
        public string[] Candidates = System.Array.Empty<string>();
    }

    MoveCaption[] spanCaptions;
    MoveCaption noneCaption;
    bool captionOn, insetOn, debugHud;
    int shownSpan = int.MinValue, insetSpan = int.MinValue;
    bool shownNext;
    readonly List<(int from, int to, bool broken)> insetTrail = new();
    readonly List<int> insetVisited = new();

    public DanceGraphData Data => data;
    public DanceGraphView View => view;
    public Mode Current => mode;
    public int StepIndex { get; private set; } = -1;
    public bool Gliding { get; private set; }
    public bool AtNode { get; private set; }
    public bool InGap { get; private set; }
    public Vector3 MiniPosition { get; private set; }
    public float NodeDistance { get; private set; } = float.NaN;
    public bool HasPath => steps != null && steps.Length > 0;
    public bool HasFingerprint => data?.Print != null;
    public bool HasTimeline => data != null && data.Spans.Count > 0;
    public int SpanIndex { get; private set; } = -1;

    /// <summary>the move caption is on screen (moves layer / directed playback, not over the fingerprint)</summary>
    public bool CaptionShown => HasTimeline && mode != Mode.Fingerprint && (captionOn || mode == Mode.Path);

    /// <summary>the graph inset is on screen (directed playback outside the graph states)</summary>
    public bool InsetShown => HasTimeline && insetOn && mode != Mode.Path && mode != Mode.Fingerprint;

    public void Init(DanceGraphData graph, Dancer lead, Dancer follow, CaptureManifest manifest, CaptureTimeline timeline,
        BeatGrid beats, DanceHud danceHud)
    {
        data = graph;
        hud = danceHud;
        if (beats != null && beats.Count > 8)
        {
            beatSeconds = (beats.Times[beats.Count - 1] - beats.Times[0]) / (beats.Count - 1) * 2f; // a beat = 2 eighths
        }

        view = new GameObject("Dance graph").AddComponent<DanceGraphView>();
        view.transform.SetParent(transform, false);
        view.Init(data);

        trailMaterial = GlowMesh.NewMaterial("Graph path trail glow", 3f);
        trailMaterial.SetVector("_NearFade", new Vector4(0.12f, 0.4f, 0, 0));
        trail = GlowMesh.Create("Graph path trail", transform, trailMaterial);
        trail.SetVisible(false);

        mini = new GameObject("Miniature couple").AddComponent<MiniatureCouple>();
        mini.transform.SetParent(transform, false);
        mini.Scale = MiniScale;
        mini.Init(lead, follow, manifest, timeline);

        BuildPath();
        BuildCaptions();
        SetMode(Mode.Off);
    }

    Vector3 Base(int node) => view.NodeWorld(node) + Vector3.up * (view.NodeRadius * 1.6f + 0.002f);

    void BuildPath()
    {
        steps = data.Steps.Where(s => s.NodeIndex >= 0).ToArray();
        int n = steps.Length;
        polylines = new Vector3[n][];
        cumulative = new float[n][];
        glideStart = new float[n];
        gapGlide = new bool[n];
        for (int k = 0; k < n; k++)
        {
            List<Vector3> pts = new();
            if (k > 0 && steps[k].NodeIndex != steps[k - 1].NodeIndex)
            {
                pts.Add(Base(steps[k - 1].NodeIndex));
                foreach (int v in steps[k].ViaIndex) pts.Add(Base(v));
            }

            pts.Add(Base(steps[k].NodeIndex));
            polylines[k] = pts.ToArray();
            cumulative[k] = new float[pts.Count];
            for (int i = 1; i < pts.Count; i++) cumulative[k][i] = cumulative[k][i - 1] + Vector3.Distance(pts[i - 1], pts[i]);

            float previous = k > 0 ? steps[k].T0 - steps[k - 1].T0 : 0f;
            float glide = pts.Count > 1 ? Mathf.Min(GlideBeats * beatSeconds, 0.5f * previous) : 0f;
            glideStart[k] = steps[k].T0 - glide;
            // a break (an unlabelled move between the two steps): leave the node when its move ends, cross the gap
            if (k > 0 && pts.Count > 1 && steps[k].BreakBefore)
            {
                float gapStart = Mathf.Min(steps[k - 1].T1, steps[k].T0);
                if (steps[k].T0 - gapStart >= MinGapGlideSeconds)
                {
                    glideStart[k] = gapStart;
                    gapGlide[k] = true;
                }
            }
        }

        nodeLinks = new List<int>[data.Nodes.Count];
        for (int i = 0; i < nodeLinks.Length; i++) nodeLinks[i] = new List<int>();
        for (int i = 0; i < data.Links.Count; i++)
        {
            nodeLinks[data.Links[i].FromIndex].Add(i);
            nodeLinks[data.Links[i].ToIndex].Add(i);
            if (data.Links[i].Id != null) linkIndex[data.Links[i].Id] = i;
        }

        pathLinkWeight = new float[data.Links.Count];
        newLinks.Clear();
        foreach ((string from, string to, int count) in data.NewLinks)
        {
            int a = data.NodeById.TryGetValue(from ?? "", out DanceGraphData.Node na) ? na.Index : -1;
            int b = data.NodeById.TryGetValue(to ?? "", out DanceGraphData.Node nb) ? nb.Index : -1;
            if (a >= 0 && b >= 0) newLinks.Add((a, b, count));
        }

        badge = DanceGraphData.Badge(data.PathProvenance ?? data.LabelsProvenance, data.PathBanner ?? data.LabelsBanner);
    }

    string NodeName(int node)
    {
        DanceGraphData.Node nd = data.Nodes[node];
        return string.IsNullOrEmpty(nd.Name) ? data.MoveName(nd.Move) ?? "(junction)" : nd.Name;
    }

    // ---------------------------------------------------------------- move caption (VIEWER_SPEC 3.12)

    static readonly Color UncertainColor = new(1f, 0.8f, 0.42f);
    static readonly Color UnlabelledColor = new(0.72f, 0.74f, 0.78f);

    static string ConfidenceText(string provenance, float? value)
    {
        string p = (provenance ?? "").ToLowerInvariant();
        return p switch
        {
            "human" => "labelled",
            "placeholder" => "placeholder",
            "narration" => value.HasValue ? $"narration match {value.Value * 100f:0} %" : "narration match",
            "auto" => value.HasValue ? $"classifier {value.Value * 100f:0} % (calibrated)" : "classifier",
            _ => value.HasValue ? $"{value.Value * 100f:0} %" : null
        };
    }

    string EnglishOf(string slug, string portuguese)
    {
        string en = data.MoveEnglish(slug);
        return string.IsNullOrEmpty(en) || string.Equals(en, portuguese, System.StringComparison.OrdinalIgnoreCase) ? null : en;
    }

    /// <summary>a narrated item whose name match is not in doubt (matched exactly, by alias or stem)</summary>
    bool NameSure(DanceGraphData.Segment seg) =>
        seg.MatchStatus == "matched" && seg.MatchScore.HasValue && seg.MatchScore.Value >= SureNameScore;

    /// <summary>"28 % · heard faintly — recogniser 33 %, name exact": the overall number first (= the bar), then why
    /// it is low</summary>
    static string FaintText(DanceGraphData.Segment seg)
    {
        string how = seg.MatchMethod == "exact" ? "name exact" : "name matched";
        string why = seg.WordProb.HasValue ? $"recogniser {seg.WordProb.Value * 100f:0} %, {how}" : how;
        return $"{seg.Confidence.GetValueOrDefault() * 100f:0} % · heard faintly — {why}";
    }

    void BuildCaptions()
    {
        Dictionary<string, int> stepOfSegment = new();
        for (int k = 0; k < steps.Length; k++)
        {
            if (steps[k].Segment != null) stepOfSegment[steps[k].Segment] = k;
        }

        Dictionary<string, int> nodeOfMove = new();
        foreach (DanceGraphData.Node nd in data.Nodes)
        {
            if (nd.Move != null && !nodeOfMove.ContainsKey(nd.Move)) nodeOfMove[nd.Move] = nd.Index;
        }

        spanCaptions = new MoveCaption[data.Spans.Count];
        for (int i = 0; i < data.Spans.Count; i++)
        {
            DanceGraphData.Span span = data.Spans[i];
            MoveCaption c = new() { T0 = span.T0, T1 = span.T1 };
            if (span.Segment != null)
            {
                DanceGraphData.Segment seg = span.Segment;
                c.Id = seg.Id;
                c.Move = seg.Move;
                c.Provenance = seg.Provenance;
                c.Phrase = seg.Phrase;
                c.Step = seg.Id != null && stepOfSegment.TryGetValue(seg.Id, out int k) ? k : -1;
                c.NodeIndex = c.Step >= 0 ? steps[c.Step].NodeIndex
                    : seg.Node != null && data.NodeById.TryGetValue(seg.Node, out DanceGraphData.Node sn) ? sn.Index
                    : seg.Move != null && nodeOfMove.TryGetValue(seg.Move, out int mi) ? mi : -1;
                c.Node = c.NodeIndex >= 0 ? data.Nodes[c.NodeIndex].Id : seg.Node;
                string name = c.NodeIndex >= 0 ? NodeName(c.NodeIndex) : data.MoveName(seg.Move) ?? "(unknown move)";
                c.Tag = DanceGraphData.Badge(seg.Provenance, null);
                c.Value = seg.Confidence;
                c.ConfText = ConfidenceText(seg.Provenance, seg.Confidence);
                string p = (seg.Provenance ?? "").ToLowerInvariant();
                bool scored = p is "narration" or "auto";
                bool low = scored && seg.Confidence.HasValue && seg.Confidence.Value < UncertainBelow;
                if (low && p == "narration" && NameSure(seg))
                {
                    // the user said this name and it matched exactly: only the recogniser was unsure of the word ->
                    // the single narrated name with its low bar, no alternative nobody named
                    c.Kind = "faint";
                    c.Name = name;
                    c.Alias = EnglishOf(seg.Move, name);
                    c.ConfText = FaintText(seg);
                    c.NameColor = Color.white;
                }
                else if (low)
                {
                    // spec 3.12: below the threshold the caption reads "uncertain" with the top two candidates
                    DanceGraphData.Candidate other = seg.Candidates.FirstOrDefault(x => x.Move != null && x.Move != seg.Move);
                    string otherName = other != null ? data.MoveName(other.Move) ?? other.Name : null;
                    c.Kind = "uncertain";
                    c.Name = otherName != null ? $"uncertain:  {name}  /  {otherName}" : $"uncertain:  {name}?";
                    string en = EnglishOf(seg.Move, name) ?? name;
                    string otherEn = other != null ? EnglishOf(other.Move, otherName) ?? otherName : null;
                    c.Alias = otherEn != null ? $"{en}  /  {otherEn}" : en;
                    c.ConfText = "uncertain · " + c.ConfText;
                    c.NameColor = UncertainColor;
                    c.Candidates = otherName != null ? new[] { seg.Move, other.Move } : new[] { seg.Move };
                }
                else
                {
                    c.Kind = "move";
                    c.Name = name;
                    c.Alias = EnglishOf(seg.Move, name);
                    c.NameColor = Color.white;
                }

                if (c.Step >= 0)
                {
                    DanceGraphData.Step st = steps[c.Step];
                    StringBuilder info = new();
                    info.Append($"move {c.Step + 1}/{steps.Length}");
                    if (st.BreakBefore && st.UnknownBefore.Length > 0) info.Append("  ·  after an unlabelled move");
                    else if (st.BreakBefore && c.Step > 0) info.Append("  ·  path broken before (unlabelled)");
                    if (st.NewLink) info.Append("  ·  new link (not in the graph yet)");
                    if (st.Reverse) info.Append("  ·  link walked backwards");
                    if (st.ViaIndex.Length > 0) info.Append("  ·  through a junction");
                    if (st.Stay) info.Append("  ·  same move again");
                    c.Info = info.ToString();
                }
            }
            else
            {
                DanceGraphData.Gap gap = span.Gap;
                c.Id = gap.Id;
                c.Kind = "unlabelled";
                c.Provenance = gap.Provenance;
                c.Phrase = gap.Phrase;
                c.Name = "unlabelled";
                c.Alias = string.IsNullOrEmpty(gap.Phrase) ? "narrated move not matched yet"
                    : $"narrated “{gap.Phrase}” — no move matched yet";
                c.Tag = DanceGraphData.Badge(gap.Provenance, null);
                c.NameColor = UnlabelledColor;
                c.Info = "path broken: an unlabelled move";
                c.Candidates = gap.Candidates.Select(x => x.Move).ToArray();
            }

            spanCaptions[i] = c;
        }

        noneCaption = new MoveCaption
        {
            Kind = "unlabelled", Name = "unlabelled", Alias = "no move narrated here", NameColor = UnlabelledColor,
            Tag = DanceGraphData.Badge(data.LabelsProvenance, data.LabelsBanner)
        };
    }

    /// <summary>name of what follows span i (the next span, or "unlabelled" when nothing starts at its end)</summary>
    string NextName(int i)
    {
        if (i < 0 || i + 1 >= spanCaptions.Length) return null;
        MoveCaption next = spanCaptions[i + 1];
        if (next.T0 - spanCaptions[i].T1 > 0.05f || next.Kind == "unlabelled") return "unlabelled";
        return next.Kind == "uncertain" ? $"{data.MoveName(next.Move)} (uncertain)" : next.Name;
    }

    public void SetCaptionVisible(bool on)
    {
        if (captionOn == on) return;
        captionOn = on;
        lastFrame = -1;
        shownSpan = int.MinValue;
        if (!CaptionShown && hud != null) hud.HideMoveCaption();
    }

    public void SetInsetVisible(bool on)
    {
        if (insetOn == on) return;
        insetOn = on;
        lastFrame = -1;
        insetSpan = int.MinValue;
        if (!InsetShown && hud != null) hud.HideInset();
    }

    /// <summary>HeadMovement's debug HUD (top-left text, beat ticker along the bottom) is up: the caption sits above
    /// the ticker</summary>
    public void SetDebugHud(bool on)
    {
        if (debugHud == on) return;
        debugHud = on;
        lastFrame = -1;
    }

    void ShowCaption(float t, int span, bool path)
    {
        MoveCaption c = span >= 0 ? spanCaptions[span] : noneCaption;
        bool lastBeat = span >= 0 && t >= c.T1 - beatSeconds;
        bool wantNext = path || lastBeat;
        hud.PlaceCaption(debugHud && !path);
        if (span != shownSpan || wantNext != shownNext || !hud.MoveCaptionVisible)
        {
            shownSpan = span;
            shownNext = wantNext;
            string next = null;
            if (wantNext)
            {
                string nn = NextName(span);
                next = nn != null ? $"next:  {nn}" : path && span >= 0 ? "(last move)" : null;
            }

            hud.SetMoveCaption(c.Tag, c.Name, c.Alias, c.Kind == "unlabelled" ? null : c.Value, c.ConfText, next, path ? c.Info : null,
                c.NameColor);
        }

        // short cross-fade in at each span start, in dance time (deterministic when paused / rendering)
        float alpha = span >= 0 && CrossFadeSeconds > 0 ? 0.35f + 0.65f * Mathf.SmoothStep(0f, 1f, (t - c.T0) / CrossFadeSeconds) : 1f;
        hud.SetMoveAlpha(alpha);
    }

    /// <summary>index of the last path step starting at or before t (-1: none)</summary>
    int StepAt(float t)
    {
        int lo = 0, hi = steps.Length - 1, k = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (steps[mid].T0 <= t)
            {
                k = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return k;
    }

    void ShowInset(float t, int span)
    {
        hud.ShowInset(data);
        MoveCaption c = span >= 0 ? spanCaptions[span] : noneCaption;
        // the path so far: steps up to the current one (in an unlabelled span: up to the last step before it)
        int upTo = c.Step >= 0 ? c.Step : StepAt(t);
        int key = span * 4096 + upTo;
        if (key == insetSpan && hud.Inset.Stamp > 0) return;
        insetSpan = key;
        int current = c.Kind == "unlabelled" ? -1 : c.NodeIndex;
        insetTrail.Clear();
        insetVisited.Clear();
        for (int k = 0; k <= upTo && k < steps.Length; k++)
        {
            insetVisited.Add(steps[k].NodeIndex);
            if (k > 0 && steps[k].NodeIndex != steps[k - 1].NodeIndex)
            {
                insetTrail.Add((steps[k - 1].NodeIndex, steps[k].NodeIndex, steps[k].BreakBefore));
            }
        }

        if (insetTrail.Count > 24) insetTrail.RemoveRange(0, insetTrail.Count - 24);
        hud.Inset.SetState(current, insetTrail, insetVisited);
    }

    // ---------------------------------------------------------------- modes

    public void SetMode(Mode m)
    {
        mode = m;
        bool on = m != Mode.Off;
        view.SetVisible(on);
        trail.SetVisible(m == Mode.Path);
        mini.SetVisible(m == Mode.Path && HasPath);
        styledStep = int.MinValue;
        lastFrame = -1;
        shownSpan = int.MinValue;
        insetSpan = int.MinValue;
        switch (m)
        {
            case Mode.Static:
                view.ResetStyle();
                view.RebuildLinks(null, 1f, newLinks);
                hud.SetBadge(null);
                hud.HidePanel();
                break;
            case Mode.Path:
                view.ResetStyle();
                view.LabelFadeNear = 1.4f;
                view.LabelFadeFar = 2.6f;
                view.RebuildLinks(null, 0.8f, newLinks);
                hud.HidePanel();
                // the provenance tag rides on the move caption; only a missing timeline gets the top badge
                hud.SetBadge(HasPath ? null : "NO MOVE TIMELINE - moves/path.json missing");
                break;
            case Mode.Fingerprint:
                ApplyFingerprint();
                break;
            default:
                hud.SetBadge(null);
                hud.HidePanel();
                break;
        }

        if (!CaptionShown) hud.HideMoveCaption();
        if (!InsetShown) hud.HideInset();
        if (m != Mode.Path)
        {
            view.LabelFadeNear = 2.5f;
            view.LabelFadeFar = 5f;
        }
    }

    public void SetFade(float f)
    {
        fade = Mathf.Clamp01(f);
        view.SetFade(fade);
        trailMaterial.SetColor("_Tint", new Color(1, 1, 1, fade));
    }

    /// <summary>update for the shown frame</summary>
    public void SetTime(float t, int frame)
    {
        bool path = mode == Mode.Path && HasPath;
        bool caption = CaptionShown, inset = InsetShown;
        if (!path && !caption && !inset) return;
        if (frame == lastFrame && Mathf.Approximately(t, lastTime)) return;
        lastFrame = frame;
        lastTime = t;
        int span = data.SpanAt(t);
        SpanIndex = span;
        if (path)
        {
            Vector3 p = PathPosition(t, out int k, out bool gliding);
            StepIndex = k;
            Gliding = gliding;
            MiniPosition = p;
            // in a gap (an unlabelled move, or past the step's segment end): no node is the current one
            InGap = span >= 0 ? spanCaptions[span].Kind == "unlabelled" : k >= 0 && t >= steps[k].T1;
            Vector3 nodeBase = Base(steps[Mathf.Max(0, k)].NodeIndex);
            NodeDistance = Vector3.Distance(p, nodeBase);
            AtNode = !gliding && NodeDistance < 1e-3f;
            mini.SetPose(frame, p, fade);
            if (k != styledStep || InGap != styledGap) StylePath(k, InGap);
            Trail(t, k);
        }

        if (caption) ShowCaption(t, span, path);
        if (inset) ShowInset(t, span);
    }

    /// <summary>position of the miniature couple at time t: at the node of the current step, gliding along the
    /// link (via junctions) during the last GlideBeats beat before the next step starts, or across the whole gap when an
    /// unlabelled move comes in between</summary>
    public Vector3 PathPosition(float t, out int step, out bool gliding)
    {
        gliding = false;
        int n = steps.Length;
        int k = -1;
        int lo = 0, hi = n - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (steps[mid].T0 <= t)
            {
                k = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        step = k;
        int at = Mathf.Max(0, k);
        if (k + 1 < n && t >= glideStart[k + 1] && polylines[k + 1].Length > 1 && k >= 0)
        {
            float span = Mathf.Max(1e-4f, steps[k + 1].T0 - glideStart[k + 1]);
            float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - glideStart[k + 1]) / span));
            gliding = u < 1f;
            return Along(k + 1, u);
        }

        return polylines[at][^1];
    }

    Vector3 Along(int k, float u)
    {
        Vector3[] pts = polylines[k];
        float[] cum = cumulative[k];
        float s = u * cum[^1];
        for (int i = 1; i < pts.Length; i++)
        {
            if (s <= cum[i] || i == pts.Length - 1)
            {
                float seg = Mathf.Max(1e-6f, cum[i] - cum[i - 1]);
                return Vector3.Lerp(pts[i - 1], pts[i], Mathf.Clamp01((s - cum[i - 1]) / seg));
            }
        }

        return pts[^1];
    }

    void StylePath(int k, bool inGap)
    {
        styledStep = k;
        styledGap = inGap;
        view.ResetStyle();
        int current = steps[Mathf.Max(0, k)].NodeIndex;
        int next = k + 1 < steps.Length ? steps[k + 1].NodeIndex : -1;
        // labels: the path so far, the next move and the current node's neighbourhood; the rest stays unlabelled
        for (int i = 0; i < view.LabelMask.Length; i++) view.LabelMask[i] = false;
        for (int i = 0; i < pathLinkWeight.Length; i++) pathLinkWeight[i] = -1f;
        foreach (int li in nodeLinks[current])
        {
            pathLinkWeight[li] = 0.4f;
            DanceGraphData.Link l = data.Links[li];
            view.LabelMask[l.FromIndex] = view.LabelMask[l.ToIndex] = !data.Nodes[l.FromIndex].Blank || !data.Nodes[l.ToIndex].Blank;
        }

        for (int j = 0; j <= k + 1 && j < steps.Length; j++)
        {
            int ni = steps[j].NodeIndex;
            if (j <= k) view.NodeGlow[ni] = Mathf.Max(view.NodeGlow[ni], 0.55f);
            view.LabelMask[ni] = true;
            foreach (string id in steps[j].Links)
            {
                if (linkIndex.TryGetValue(id, out int li)) pathLinkWeight[li] = j <= k ? 1f : 0.7f;
            }
        }

        view.RebuildLinks(pathLinkWeight, 0.55f, newLinks, 0.6f);

        if (!inGap)
        {
            view.NodeGlow[current] = 1.6f;
            view.NodeScale[current] = 1.35f;
            view.LabelEmphasis[current] = true;
        }

        // during an unlabelled move no node is the current one: the previous stays a visited node, the next is the goal
        if (next >= 0)
        {
            view.NodeGlow[next] = Mathf.Max(view.NodeGlow[next], inGap ? 1.1f : 0.9f);
            view.NodeScale[next] = Mathf.Max(view.NodeScale[next], 1.15f);
            view.LabelEmphasis[next] = true;
        }

        view.MarkStylesDirty();
    }

    void Trail(float t, int k)
    {
        trail.Begin();
        trail.Viewer = DanceText.ViewCamera != null ? DanceText.ViewCamera.transform.position : (Vector3?)null;
        for (int j = 1; j <= k + 1 && j < steps.Length; j++)
        {
            Vector3[] pts = polylines[j];
            if (pts.Length < 2) continue;
            float age = k - j + 1;
            float brightness = 0.2f + 0.8f * Mathf.Exp(-age / 3f);
            bool broken = gapGlide[j] || steps[j].BreakBefore;
            Color c = (broken ? new Color(0.62f, 0.64f, 0.68f) : steps[j].NewLink ? new Color(1f, 1f, 1f) : new Color(1f, 0.8f, 0.3f)) * brightness;
            float until = j <= k ? float.MaxValue : -1f;
            if (j == k + 1)
            {
                // the glide in progress: draw up to the couple
                if (t < glideStart[j]) break;
                float span = Mathf.Max(1e-4f, steps[j].T0 - glideStart[j]);
                until = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - glideStart[j]) / span)) * cumulative[j][^1];
            }

            for (int i = 1; i < pts.Length; i++)
            {
                float s0 = cumulative[j][i - 1], s1 = cumulative[j][i];
                if (s0 >= until) break;
                Vector3 a = pts[i - 1], b = s1 <= until ? pts[i] : Vector3.Lerp(pts[i - 1], pts[i], (until - s0) / Mathf.Max(1e-6f, s1 - s0));
                if (broken) trail.DashedLine(a, b, 0.009f, c, 0.012f, 0.022f); // dotted: through an unlabelled move
                else if (steps[j].NewLink) trail.DashedLine(a, b, 0.012f, c, 0.03f, 0.02f);
                else trail.Line(a, b, 0.012f, c);
            }
        }

        trail.End();
    }

    /// <summary>deterministic chase-camera pose: behind and above the couple's direction of travel, looking ahead;
    /// pulls back while the couple dwells on a node</summary>
    public void Chase(float t, out Vector3 eye, out Vector3 target)
    {
        Vector3 p = PathPosition(t, out int k, out _);
        Vector3 d = PathPosition(t + 1.2f, out _, out _) - PathPosition(t - 1.2f, out _, out _);
        Vector3 h = new(d.x, 0, d.z);
        int cur = steps[Mathf.Max(0, k)].NodeIndex;
        int next = k + 1 < steps.Length ? steps[k + 1].NodeIndex : -1;
        if (h.magnitude < 0.05f && next >= 0)
        {
            Vector3 nn = Base(next) - Base(cur);
            h = new Vector3(nn.x, 0, nn.z);
        }

        if (h.magnitude < 0.01f) h = new Vector3(p.x, 0, p.z);
        if (h.magnitude < 0.01f) h = Vector3.forward;
        h.Normalize();

        // 0 while gliding, 1 when the next glide is far away (dwelling)
        float nearGlide = 0f;
        if (k + 1 < steps.Length) nearGlide = Mathf.Clamp01(1f - Mathf.Max(0f, glideStart[k + 1] - t) / 1.5f);
        if (k >= 0) nearGlide = Mathf.Max(nearGlide, Mathf.Clamp01(1f - (t - steps[k].T0) / 1.0f));
        float dwell = 1f - nearGlide;
        float back = Mathf.Lerp(ChaseBack.x, ChaseBack.y, dwell), up = Mathf.Lerp(ChaseUp.x, ChaseUp.y, dwell);
        eye = p - h * back + Vector3.up * up;
        Vector3 ahead = next >= 0 ? Vector3.Lerp(p, Base(next), 0.5f) : p + h * 0.6f;
        target = Vector3.Lerp(p, ahead, 0.6f) + Vector3.up * 0.1f;
    }

    /// <summary>graph centre and radius for the fingerprint orbit</summary>
    public void Bounds(out Vector3 centre, out float radius)
    {
        Bounds b = new(data.Nodes.Count > 0 ? data.Nodes[0].World : Vector3.zero, Vector3.zero);
        foreach (DanceGraphData.Node n in data.Nodes) b.Encapsulate(n.World);
        centre = b.center;
        radius = b.extents.magnitude;
    }

    void ApplyFingerprint()
    {
        view.ResetStyle();
        DanceGraphData.Fingerprint p = data.Print;
        if (p == null)
        {
            hud.SetBadge("NO FINGERPRINT - moves/fingerprint.json missing");
            view.RebuildLinks(null, 0.6f, newLinks);
            return;
        }

        float max = 0;
        foreach (var kv in p.Nodes) max = Mathf.Max(max, kv.Value.seconds);
        foreach (DanceGraphData.Node node in data.Nodes)
        {
            int i = node.Index;
            if (p.Nodes.TryGetValue(node.Id, out var dwell) && dwell.seconds > 0 && max > 0)
            {
                float h = Mathf.Sqrt(dwell.seconds / max);
                view.NodeColor[i] = Color.Lerp(node.Tone, new Color(1f, 0.85f, 0.55f), 0.15f * h);
                view.NodeGlow[i] = 0.3f + 1.3f * h;
                view.NodeScale[i] = 1f + 1.6f * h;
                view.LabelMask[i] = true;
            }
            else
            {
                view.NodeColor[i] = (node.Blank ? Color.gray : node.Tone) * 0.3f;
                view.NodeGlow[i] = 0f;
                view.NodeScale[i] = node.Blank ? 0.35f : 0.75f;
                view.LabelMask[i] = false;
            }
        }

        view.Labels = DanceGraphView.LabelMode.Marked;
        view.MarkStylesDirty();

        // link weights from the path's taken links (count per link id), normalised
        Dictionary<string, int> counts = new();
        foreach (DanceGraphData.Step s in data.Steps)
        {
            foreach (string id in s.Links) counts[id] = counts.TryGetValue(id, out int c) ? c + 1 : 1;
        }

        if (counts.Count == 0)
        {
            foreach (var kv in p.Links)
            {
                string[] ab = kv.Key.Split('|');
                if (ab.Length != 2) continue;
                foreach (DanceGraphData.Link l in data.Links)
                {
                    if (l.From == ab[0] && l.To == ab[1] || l.From == ab[1] && l.To == ab[0]) counts[l.Id] = kv.Value;
                }
            }
        }

        int maxCount = counts.Count > 0 ? counts.Values.Max() : 1;
        float[] weights = new float[data.Links.Count];
        for (int i = 0; i < data.Links.Count; i++)
        {
            weights[i] = data.Links[i].Id != null && counts.TryGetValue(data.Links[i].Id, out int c) ? (float)c / maxCount : 0f;
        }

        view.RebuildLinks(weights, 1f, newLinks, 1f);

        string[] bandColors = { "#3b6fd8", "#3fb0c9", "#58c46b", "#e5b52a", "#f07a1a" };
        var rows = p.Bands.Select(b => (
            label: b.label ?? $"band {b.band}", b.seconds, b.share,
            color: ColorUtility.TryParseHtmlString(bandColors[Mathf.Clamp(b.band, 0, bandColors.Length - 1)], out Color bc) ? bc : Color.white)).ToArray();
        float labelsSum = data.Segments.Sum(s => Mathf.Max(0, s.T1 - s.T0));
        StringBuilder body = new();
        body.AppendLine($"labelled {p.LabelledSeconds:0.0} s in {data.Segments.Count} segments");
        if (data.Unresolved.Count > 0)
        {
            float gapSeconds = data.Unresolved.Sum(g => Mathf.Max(0, g.T1 - g.T0));
            body.AppendLine($"unlabelled moves {data.Unresolved.Count} ({gapSeconds:0.0} s, not counted)");
        }

        body.AppendLine($"distinct moves {p.DistinctMoves}   transitions {p.Transitions}");
        body.AppendLine(p.CounterbalanceShare.HasValue
            ? $"counterbalance {p.CounterbalanceShare.Value * 100:0.0} % of {(float.IsNaN(p.CounterbalanceAnalysedSeconds) ? p.LabelledSeconds : p.CounterbalanceAnalysedSeconds):0.0} s analysed"
            : "counterbalance: not analysed");
        if (!float.IsNaN(p.LongestPhraseSeconds))
        {
            string moves = string.Join(" → ", p.LongestPhraseMoves.Select(m => data.MoveName(m)));
            body.AppendLine($"longest phrase {p.LongestPhraseSeconds:0.0} s: {moves}");
        }

        if (newLinks.Count > 0) body.AppendLine($"new links (dashed): {newLinks.Count}");
        hud.ShowPanel($"Dance fingerprint · {Mathf.RoundToInt(labelsSum)} s", rows, body.ToString());
        hud.SetBadge(DanceGraphData.Badge(p.Provenance, p.Banner));
    }

    /// <summary>the caption of time t for hm_state (the live move ribbon): kind move | faint | uncertain | unlabelled</summary>
    public Dictionary<string, object> CaptionAt(float t)
    {
        if (!HasTimeline) return null;
        int span = data.SpanAt(t);
        MoveCaption c = span >= 0 ? spanCaptions[span] : noneCaption;
        return new Dictionary<string, object>
        {
            ["span"] = span, ["kind"] = c.Kind, ["name"] = c.Name, ["alias"] = c.Alias, ["move"] = c.Move, ["node"] = c.Node,
            ["provenance"] = c.Provenance, ["confidence"] = c.Value, ["confidenceText"] = c.ConfText, ["tag"] = c.Tag,
            ["phrase"] = c.Phrase, ["id"] = c.Id, ["t0"] = span >= 0 ? c.T0 : (float?)null, ["t1"] = span >= 0 ? c.T1 : (float?)null,
            ["candidates"] = c.Candidates, ["next"] = span >= 0 ? NextName(span) : null
        };
    }

    public Dictionary<string, object> State()
    {
        Dictionary<string, object> s = new()
        {
            ["mode"] = mode.ToString().ToLowerInvariant(), ["nodes"] = data.Nodes.Count, ["links"] = data.Links.Count,
            ["steps"] = steps?.Length ?? 0, ["segments"] = data.Segments.Count, ["unresolved"] = data.Unresolved.Count,
            ["newLinks"] = newLinks.Count, ["visible"] = view.Visible, ["fade"] = fade, ["drawCalls"] = view.DrawCalls,
            ["labels"] = view.VisibleLabels, ["provenance"] = data.PathProvenance ?? data.LabelsProvenance,
            ["badge"] = hud.BadgeText ?? hud.CaptionTag, ["topBadge"] = hud.BadgeText,
            ["caption"] = hud.Caption, ["captionSub"] = hud.CaptionSub, ["warnings"] = data.Warnings.ToList()
        };
        s["moveCaption"] = new Dictionary<string, object>
        {
            ["shown"] = hud.MoveCaptionVisible, ["layer"] = captionOn, ["span"] = SpanIndex, ["tag"] = hud.CaptionTag,
            ["name"] = hud.Caption, ["alias"] = hud.CaptionSub, ["confidence"] = hud.CaptionConfidence,
            ["confidenceBar"] = hud.CaptionConfidenceValue, ["next"] = hud.CaptionNext, ["info"] = hud.CaptionInfo,
            ["alpha"] = hud.CaptionAlpha, ["lift"] = hud.CaptionLift
        };
        s["inset"] = new Dictionary<string, object>
        {
            ["shown"] = hud.InsetVisible, ["on"] = insetOn,
            ["currentNode"] = hud.InsetVisible && hud.Inset.CurrentNode >= 0 ? data.Nodes[hud.Inset.CurrentNode].Id : null,
            ["trail"] = hud.InsetVisible ? hud.Inset.TrailCount : 0
        };
        if (HasTimeline)
        {
            s["spans"] = spanCaptions.Select(c => new Dictionary<string, object>
            {
                ["t0"] = c.T0, ["t1"] = c.T1, ["kind"] = c.Kind, ["move"] = c.Move, ["name"] = c.Name, ["id"] = c.Id
            }).ToList();
        }

        if (mode == Mode.Path && HasPath && StepIndex >= -1)
        {
            int k = Mathf.Max(0, StepIndex);
            s["step"] = StepIndex;
            // inside an unlabelled gap no node is current (the couple is crossing it): last* name the node it left
            s["currentMove"] = InGap ? null : steps[k].Move;
            s["currentNode"] = InGap ? null : steps[k].Node;
            s["currentName"] = InGap ? null : NodeName(steps[k].NodeIndex);
            s["lastMove"] = steps[k].Move;
            s["lastNode"] = steps[k].Node;
            s["nextMove"] = k + 1 < steps.Length ? steps[k + 1].Move : null;
            s["gliding"] = Gliding;
            s["atNode"] = AtNode;
            s["inGap"] = InGap;
            s["nodeDistanceM"] = NodeDistance;
            s["mini"] = new[] { MiniPosition.x, MiniPosition.y, MiniPosition.z };
            s["miniAvatars"] = mini.HasAvatars;
            s["newLinkStep"] = steps[k].NewLink;
        }

        if (HasPath)
        {
            // every segment start: the couple stands on that step's node (VIEWER_SPEC 14)
            float worst = 0;
            foreach (var st in steps.Select((st, i) => (st, i)))
            {
                Vector3 p = PathPosition(st.st.T0 + 1e-4f, out int k, out bool g);
                worst = Mathf.Max(worst, Vector3.Distance(p, Base(st.st.NodeIndex)));
            }

            s["pathArrivalMaxErrorM"] = worst;
            s["stepTimes"] = steps.Select(st => new[] { st.T0, st.T1 }).ToList();
            s["stepNodes"] = steps.Select(st => st.Node).ToList();
            s["stepNewLink"] = steps.Select(st => st.NewLink).ToList();
            s["stepBreak"] = steps.Select(st => st.BreakBefore).ToList();
            s["stepGapGlide"] = gapGlide.ToList();
            s["segmentsMapped"] = data.Segments.Count(seg => steps.Any(st => st.Segment == seg.Id || Mathf.Abs(st.T0 - seg.T0) < 1e-3f));
        }

        if (data.Print != null)
        {
            DanceGraphData.Fingerprint p = data.Print;
            s["fingerprint"] = new Dictionary<string, object>
            {
                ["labelledSeconds"] = p.LabelledSeconds, ["nodeSecondsSum"] = p.Nodes.Values.Sum(v => v.seconds),
                ["bandSecondsSum"] = p.Bands.Sum(b => b.seconds),
                ["labelsSecondsSum"] = data.Segments.Sum(seg => Mathf.Max(0, seg.T1 - seg.T0)),
                ["shareSum"] = p.Nodes.Values.Sum(v => v.share), ["distinctMoves"] = p.DistinctMoves,
                ["transitions"] = p.Transitions, ["counterbalanceShare"] = p.CounterbalanceShare,
                ["provenance"] = p.Provenance, ["panel"] = hud.PanelVisible, ["panelText"] = hud.PanelVisible ? hud.PanelText : null
            };
        }

        return s;
    }

    void OnDestroy()
    {
        if (trailMaterial != null) Destroy(trailMaterial);
    }
}
