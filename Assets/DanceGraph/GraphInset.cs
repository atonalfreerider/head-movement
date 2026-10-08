using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The graph inset of directed playback (VIEWER_SPEC 3.12): the zouk state machine (3.10) as a small live minimap -
/// every node a dot in its tone seen from the side (oblique view, so height = energy reads as height), the links
/// faint, the path so far as a fading gold trail (a break through an unlabelled move dotted grey), the current node
/// lit with a ring and the candidate next moves (the graph's links out of the current node) glowing faintly. During
/// an unlabelled span no node is lit. One uGUI mesh in the HUD canvas, rebuilt only when the current move changes.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class GraphInset : MaskableGraphic
{
    public float Azimuth = -60f, Elevation = 20f; // the fingerprint shot's opening view

    DanceGraphData data;
    Vector2[] unit; // node positions, normalised to 0..1 in a square-ish box, aspect kept
    float unitAspect = 1f;
    List<int>[] outLinks;
    int current = -1;
    readonly List<(int from, int to, bool broken)> trail = new();
    readonly HashSet<int> visited = new();
    int stamp;

    public DanceGraphData Data => data;
    public int CurrentNode => current;
    public int TrailCount => trail.Count;
    public int Stamp => stamp;

    public void Init(DanceGraphData graph)
    {
        data = graph;
        int n = data.Nodes.Count;
        unit = new Vector2[n];
        outLinks = new List<int>[n];
        for (int i = 0; i < n; i++) outLinks[i] = new List<int>();
        foreach (DanceGraphData.Link l in data.Links) outLinks[l.FromIndex].Add(l.ToIndex);

        Bounds b = new(n > 0 ? data.Nodes[0].World : Vector3.zero, Vector3.zero);
        foreach (DanceGraphData.Node node in data.Nodes) b.Encapsulate(node.World);
        float az = Azimuth * Mathf.Deg2Rad, el = Elevation * Mathf.Deg2Rad;
        Vector3 forward = -new Vector3(Mathf.Cos(el) * Mathf.Cos(az), Mathf.Sin(el), Mathf.Cos(el) * Mathf.Sin(az));
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
        Vector3 up = Vector3.Cross(forward, right);
        Vector2 lo = new(float.MaxValue, float.MaxValue), hi = new(float.MinValue, float.MinValue);
        for (int i = 0; i < n; i++)
        {
            Vector3 d = data.Nodes[i].World - b.center;
            unit[i] = new Vector2(Vector3.Dot(d, right), Vector3.Dot(d, up));
            lo = Vector2.Min(lo, unit[i]);
            hi = Vector2.Max(hi, unit[i]);
        }

        Vector2 size = Vector2.Max(hi - lo, new Vector2(1e-3f, 1e-3f));
        unitAspect = size.x / size.y;
        for (int i = 0; i < n; i++) unit[i] = new Vector2((unit[i].x - lo.x) / size.x, (unit[i].y - lo.y) / size.y);
        current = -1;
        trail.Clear();
        visited.Clear();
        SetVerticesDirty();
    }

    /// <summary>the current node (-1: an unlabelled span) and the path so far (oldest first)</summary>
    public void SetState(int currentNode, List<(int from, int to, bool broken)> path, List<int> visitedNodes)
    {
        current = currentNode;
        trail.Clear();
        trail.AddRange(path);
        visited.Clear();
        foreach (int v in visitedNodes) visited.Add(v);
        stamp++;
        SetVerticesDirty();
    }

    Vector2 At(int node, Rect r)
    {
        // fit the projected graph into the rect, aspect kept, centred, 8 px padding
        float pad = 8f;
        float w = r.width - 2 * pad, h = r.height - 2 * pad;
        float sw = w, sh = w / unitAspect;
        if (sh > h)
        {
            sh = h;
            sw = h * unitAspect;
        }

        Vector2 u = unit[node];
        return new Vector2(r.x + pad + (w - sw) * 0.5f + u.x * sw, r.y + pad + (h - sh) * 0.5f + u.y * sh);
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (data == null || unit == null) return;
        Rect r = GetPixelAdjustedRect();

        // links: faint
        foreach (DanceGraphData.Link l in data.Links)
        {
            DanceGraphData.Node a = data.Nodes[l.FromIndex], b = data.Nodes[l.ToIndex];
            Color c = Color.Lerp(a.Blank ? Color.gray : a.Tone, b.Blank ? Color.gray : b.Tone, 0.5f);
            c.a = 0.28f;
            Line(vh, At(l.FromIndex, r), At(l.ToIndex, r), 1.1f, c, c);
        }

        // the path so far: a fading trail (newest brightest); breaks through an unlabelled move dotted grey
        for (int i = 0; i < trail.Count; i++)
        {
            (int from, int to, bool broken) = trail[i];
            if (from < 0 || to < 0 || from == to) continue;
            float age = trail.Count - 1 - i;
            float k = 0.25f + 0.75f * Mathf.Exp(-age / 3f);
            Vector2 a = At(from, r), b = At(to, r);
            if (broken)
            {
                Dotted(vh, a, b, 1.6f, new Color(0.75f, 0.77f, 0.8f, 0.85f * k), 4f, 4f);
            }
            else
            {
                Color c = new(1f, 0.8f, 0.3f, k);
                Line(vh, a, b, 1.2f + 1.8f * k, new Color(c.r, c.g, c.b, c.a * 0.6f), c);
            }
        }

        // candidate next moves: faint glow on the graph's links out of the current node
        if (current >= 0)
        {
            foreach (int to in outLinks[current])
            {
                DanceGraphData.Node nd = data.Nodes[to];
                if (nd.Blank) continue;
                Color c = nd.Tone;
                c.a = 0.4f;
                Halo(vh, At(to, r), 7f, c);
            }
        }

        // nodes
        for (int i = 0; i < data.Nodes.Count; i++)
        {
            DanceGraphData.Node nd = data.Nodes[i];
            if (i == current) continue;
            bool seen = visited.Contains(i);
            Color c = nd.Blank ? new Color(0.5f, 0.52f, 0.55f, 0.35f) : Color.Lerp(nd.Tone, Color.white, seen ? 0.35f : 0f);
            if (!nd.Blank) c.a = seen ? 1f : 0.7f;
            Disc(vh, At(i, r), nd.Blank ? 1.2f : seen ? 3.4f : 2.3f, c, c);
        }

        // the current node: lit, with a ring
        if (current >= 0)
        {
            Vector2 p = At(current, r);
            Halo(vh, p, 16f, new Color(1f, 0.85f, 0.45f, 0.45f));
            Disc(vh, p, 5.5f, new Color(1f, 0.97f, 0.85f, 1f), new Color(1f, 0.85f, 0.5f, 1f));
            Ring(vh, p, 10f, 1.6f, new Color(1f, 0.9f, 0.55f, 0.95f));
        }
    }

    static void Quad(VertexHelper vh, Vector2 a0, Vector2 a1, Vector2 b1, Vector2 b0, Color ca, Color cb)
    {
        int i = vh.currentVertCount;
        UIVertex v = UIVertex.simpleVert;
        v.color = ca;
        v.position = a0;
        vh.AddVert(v);
        v.position = a1;
        vh.AddVert(v);
        v.color = cb;
        v.position = b1;
        vh.AddVert(v);
        v.position = b0;
        vh.AddVert(v);
        vh.AddTriangle(i, i + 1, i + 2);
        vh.AddTriangle(i, i + 2, i + 3);
    }

    static void Line(VertexHelper vh, Vector2 a, Vector2 b, float width, Color ca, Color cb)
    {
        Vector2 d = b - a;
        if (d.sqrMagnitude < 1e-4f) return;
        Vector2 n = new Vector2(-d.y, d.x).normalized * (width * 0.5f);
        Quad(vh, a - n, a + n, b + n, b - n, ca, cb);
    }

    static void Dotted(VertexHelper vh, Vector2 a, Vector2 b, float width, Color c, float dash, float gap)
    {
        float len = Vector2.Distance(a, b);
        if (len < 1e-3f) return;
        Vector2 dir = (b - a) / len;
        for (float s = 0; s < len; s += dash + gap) Line(vh, a + dir * s, a + dir * Mathf.Min(len, s + dash), width, c, c);
    }

    static void Disc(VertexHelper vh, Vector2 p, float radius, Color inner, Color outer, int segments = 10)
    {
        int centre = vh.currentVertCount;
        UIVertex v = UIVertex.simpleVert;
        v.position = p;
        v.color = inner;
        vh.AddVert(v);
        v.color = outer;
        for (int i = 0; i < segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments;
            v.position = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            vh.AddVert(v);
        }

        for (int i = 0; i < segments; i++) vh.AddTriangle(centre, centre + 1 + i, centre + 1 + (i + 1) % segments);
    }

    static void Halo(VertexHelper vh, Vector2 p, float radius, Color c) => Disc(vh, p, radius, c, new Color(c.r, c.g, c.b, 0f), 16);

    static void Ring(VertexHelper vh, Vector2 p, float radius, float width, Color c, int segments = 24)
    {
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * Mathf.PI * 2f / segments, a1 = (i + 1) * Mathf.PI * 2f / segments;
            Vector2 d0 = new(Mathf.Cos(a0), Mathf.Sin(a0)), d1 = new(Mathf.Cos(a1), Mathf.Sin(a1));
            Quad(vh, p + d0 * (radius - width * 0.5f), p + d0 * (radius + width * 0.5f), p + d1 * (radius + width * 0.5f),
                p + d1 * (radius - width * 0.5f), c, c);
        }
    }
}
