using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// Layer "graph" and the two graph view states (VIEWER_SPEC 3.10, 3.11, 4):
/// - Static: the zouk graph standing in the dance environment (the dance keeps playing inside it).
/// - Path ("Dance graph"): a miniature couple (travel removed) stands at the node of the current move and glides
///   along the link to the next node, arriving exactly at the next segment start; a fading trail marks the path;
///   new links (transitions the graph has no link for) are dashed; caption with the current and next move and the
///   provenance badge. Chase() gives the deterministic chase-camera pose (behind/above, looking ahead).
/// - Fingerprint: the time-independent heat map: node glow and size by dwell seconds, unvisited nodes dim, links
///   thickened by how often they were taken, side panel with the energy-band totals and summary numbers.
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
    string[] captionMain, captionSub;
    readonly List<(int from, int to, int count)> newLinks = new();
    List<int>[] nodeLinks; // link indices touching each node
    readonly Dictionary<string, int> linkIndex = new();
    float[] pathLinkWeight;
    int styledStep = int.MinValue;
    float lastTime = float.NaN;
    int lastFrame = -1;
    string badge;

    public DanceGraphData Data => data;
    public DanceGraphView View => view;
    public Mode Current => mode;
    public int StepIndex { get; private set; } = -1;
    public bool Gliding { get; private set; }
    public bool AtNode { get; private set; }
    public Vector3 MiniPosition { get; private set; }
    public float NodeDistance { get; private set; } = float.NaN;
    public bool HasPath => steps != null && steps.Length > 0;
    public bool HasFingerprint => data?.Print != null;

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
        captionMain = new string[n];
        captionSub = new string[n];
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

            string name = NodeName(steps[k].NodeIndex);
            string next = k + 1 < n ? NodeName(steps[k + 1].NodeIndex) : null;
            captionMain[k] = next != null ? $"{name}   →   {next}" : $"{name}   (last move)";
            StringBuilder sub = new();
            sub.Append($"move {k + 1}/{n}");
            if (steps[k].NewLink) sub.Append("  ·  new link (not in the graph yet)");
            if (steps[k].Reverse) sub.Append("  ·  link walked backwards");
            if (steps[k].ViaIndex.Length > 0) sub.Append("  ·  through a junction");
            string authored = data.Nodes[steps[k].NodeIndex].AuthoredName;
            if (!string.IsNullOrEmpty(authored) && authored != name) sub.Append($"  ·  ({authored})");
            captionSub[k] = sub.ToString();
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

    public void SetMode(Mode m)
    {
        mode = m;
        bool on = m != Mode.Off;
        view.SetVisible(on);
        trail.SetVisible(m == Mode.Path);
        mini.SetVisible(m == Mode.Path && HasPath);
        styledStep = int.MinValue;
        lastFrame = -1;
        switch (m)
        {
            case Mode.Static:
                view.ResetStyle();
                view.RebuildLinks(null, 1f, newLinks);
                hud.SetCaption(null, null);
                hud.SetBadge(null);
                hud.HidePanel();
                break;
            case Mode.Path:
                view.ResetStyle();
                view.LabelFadeNear = 1.4f;
                view.LabelFadeFar = 2.6f;
                view.RebuildLinks(null, 0.8f, newLinks);
                hud.HidePanel();
                hud.SetBadge(HasPath ? badge : "NO MOVE TIMELINE - moves/path.json missing");
                break;
            case Mode.Fingerprint:
                ApplyFingerprint();
                hud.SetCaption(null, null);
                break;
            default:
                hud.Clear();
                break;
        }

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
        if (mode != Mode.Path || !HasPath) return;
        if (frame == lastFrame && Mathf.Approximately(t, lastTime)) return;
        lastFrame = frame;
        lastTime = t;
        Vector3 p = PathPosition(t, out int k, out bool gliding);
        StepIndex = k;
        Gliding = gliding;
        MiniPosition = p;
        Vector3 nodeBase = Base(steps[Mathf.Max(0, k)].NodeIndex);
        NodeDistance = Vector3.Distance(p, nodeBase);
        AtNode = !gliding && NodeDistance < 1e-3f;
        mini.SetPose(frame, p, fade);
        if (k != styledStep) StylePath(k);
        Trail(t, k);
        int ci = Mathf.Clamp(t < steps[0].T0 ? 0 : k, 0, steps.Length - 1);
        hud.SetCaption(captionMain[ci], captionSub[ci]);
    }

    /// <summary>position of the miniature couple at time t: at the node of the current step, gliding along the
    /// link (via junctions) during the last GlideBeats beat before the next step starts</summary>
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

    void StylePath(int k)
    {
        styledStep = k;
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

        view.NodeGlow[current] = 1.6f;
        view.NodeScale[current] = 1.35f;
        view.LabelEmphasis[current] = true;
        if (next >= 0)
        {
            view.NodeGlow[next] = Mathf.Max(view.NodeGlow[next], 0.9f);
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
            Color c = (steps[j].NewLink ? new Color(1f, 1f, 1f) : new Color(1f, 0.8f, 0.3f)) * brightness;
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
                if (steps[j].NewLink) trail.DashedLine(a, b, 0.012f, c, 0.03f, 0.02f);
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

    public Dictionary<string, object> State()
    {
        Dictionary<string, object> s = new()
        {
            ["mode"] = mode.ToString().ToLowerInvariant(), ["nodes"] = data.Nodes.Count, ["links"] = data.Links.Count,
            ["steps"] = steps?.Length ?? 0, ["segments"] = data.Segments.Count, ["newLinks"] = newLinks.Count,
            ["visible"] = view.Visible, ["fade"] = fade, ["drawCalls"] = view.DrawCalls, ["labels"] = view.VisibleLabels,
            ["provenance"] = data.PathProvenance ?? data.LabelsProvenance, ["badge"] = hud.BadgeText,
            ["caption"] = hud.Caption, ["captionSub"] = hud.CaptionSub, ["warnings"] = data.Warnings.ToList()
        };
        if (mode == Mode.Path && HasPath && StepIndex >= -1)
        {
            int k = Mathf.Max(0, StepIndex);
            s["step"] = StepIndex;
            s["currentMove"] = steps[k].Move;
            s["currentNode"] = steps[k].Node;
            s["currentName"] = NodeName(steps[k].NodeIndex);
            s["nextMove"] = k + 1 < steps.Length ? steps[k + 1].Move : null;
            s["gliding"] = Gliding;
            s["atNode"] = AtNode;
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
                ["panel"] = hud.PanelVisible
            };
        }

        return s;
    }

    void OnDestroy()
    {
        if (trailMaterial != null) Destroy(trailMaterial);
    }
}
