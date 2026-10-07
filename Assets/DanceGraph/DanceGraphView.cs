using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The static dance graph in the dance environment (VIEWER_SPEC 3.10): node icons GPU-instanced per shape
/// (Graphics.RenderMeshInstanced, per-instance tone/glow), directed links with arrowheads in one glow mesh
/// (proposed links and new links dashed, proposed nodes outlined dashed), and billboard name labels that fade with
/// distance. True scale, centred on the origin (graph.json display.offset). Node styles (colour, glow, size) and
/// link styles are set by the path / fingerprint modes; nothing here allocates per frame.
/// </summary>
public class DanceGraphView : MonoBehaviour
{
    public float NodeRadius = 0.04f;
    public float LabelHeight = 0.032f;
    public float LabelFadeNear = 2.5f, LabelFadeFar = 5f;

    public enum LabelMode
    {
        Distance, // fade with distance from the camera
        Marked // only nodes marked in LabelMask, kept readable at any distance
    }

    DanceGraphData data;
    Material nodeMaterial, linkMaterial;
    GlowMesh links;
    readonly List<int>[] byShape = new List<int>[GraphShapes.Names.Length];
    Matrix4x4[][] matrices;
    Vector4[][] colors, glows;
    MaterialPropertyBlock[] blocks;
    Mesh[] meshes;
    TextMesh[] labels;
    float[] labelAlpha;

    public Color[] NodeColor;
    public float[] NodeGlow, NodeScale;
    public bool[] LabelMask, LabelEmphasis;
    public LabelMode Labels = LabelMode.Distance;
    float fade = 1f;
    bool visible;
    bool stylesDirty = true;

    public DanceGraphData Data => data;
    public bool Visible => visible;
    public float Fade => fade;
    public int NodeCount => data?.Nodes.Count ?? 0;
    public int DrawCalls { get; private set; }
    public int VisibleLabels { get; private set; }

    static Shader nodeShader;

    public void Init(DanceGraphData graph)
    {
        data = graph;
        if (nodeShader == null) nodeShader = Resources.Load<Shader>("HM_GraphNode");
        if (nodeShader == null) nodeShader = Shader.Find("HeadMovement/GraphNode");
        nodeMaterial = new Material(nodeShader) { name = "Graph nodes", enableInstancing = true };
        linkMaterial = GlowMesh.NewMaterial("Graph links glow", 2.2f);
        linkMaterial.SetVector("_NearFade", new Vector4(0.2f, 0.6f, 0, 0));
        links = GlowMesh.Create("Graph links", transform, linkMaterial);

        int n = data.Nodes.Count;
        NodeColor = new Color[n];
        NodeGlow = new float[n];
        NodeScale = new float[n];
        LabelMask = new bool[n];
        LabelEmphasis = new bool[n];
        labelAlpha = new float[n];
        labels = new TextMesh[n];
        meshes = new Mesh[GraphShapes.Names.Length];
        matrices = new Matrix4x4[GraphShapes.Names.Length][];
        colors = new Vector4[GraphShapes.Names.Length][];
        glows = new Vector4[GraphShapes.Names.Length][];
        blocks = new MaterialPropertyBlock[GraphShapes.Names.Length];
        for (int s = 0; s < byShape.Length; s++) byShape[s] = new List<int>();
        foreach (DanceGraphData.Node node in data.Nodes) byShape[GraphShapes.IndexOf(node.Shape)].Add(node.Index);
        for (int s = 0; s < byShape.Length; s++)
        {
            meshes[s] = GraphShapes.Get(GraphShapes.Names[s]);
            int count = Mathf.Max(1, byShape[s].Count);
            matrices[s] = new Matrix4x4[count];
            colors[s] = new Vector4[count];
            glows[s] = new Vector4[count];
            blocks[s] = new MaterialPropertyBlock();
        }

        Font font = DanceText.Font;
        Transform labelRoot = new GameObject("Graph labels").transform;
        labelRoot.SetParent(transform, false);
        foreach (DanceGraphData.Node node in data.Nodes)
        {
            if (node.Blank || string.IsNullOrEmpty(node.Name)) continue;
            TextMesh tm = DanceText.WorldLabel(labelRoot, node.Name, font, LabelHeight, Color.white);
            tm.text = node.Name;
            tm.gameObject.SetActive(false);
            labels[node.Index] = tm;
        }

        ResetStyle();
        SetVisible(false);
    }

    /// <summary>the plain graph: tone colours, a little glow, blank junctions small</summary>
    public void ResetStyle()
    {
        foreach (DanceGraphData.Node node in data.Nodes)
        {
            int i = node.Index;
            bool proposed = node.Status != "scaffold";
            NodeColor[i] = node.Blank ? new Color(0.45f, 0.47f, 0.5f) : node.Tone;
            NodeGlow[i] = node.Blank ? 0.05f : proposed ? 0.45f : 0.18f;
            NodeScale[i] = node.Blank ? 0.45f : 1f;
            LabelMask[i] = !node.Blank;
            LabelEmphasis[i] = false;
        }

        Labels = LabelMode.Distance;
        MarkStylesDirty();
    }

    /// <summary>call after changing NodeColor/NodeGlow/NodeScale/LabelMask/LabelEmphasis</summary>
    public void MarkStylesDirty()
    {
        stylesDirty = true;
        for (int i = 0; i < labelAlpha.Length; i++) labelAlpha[i] = -1f;
    }

    public void SetVisible(bool on)
    {
        visible = on;
        links.SetVisible(on);
        if (!on && labels != null)
        {
            foreach (TextMesh l in labels)
            {
                if (l != null) l.gameObject.SetActive(false);
            }
        }
    }

    public void SetFade(float f)
    {
        f = Mathf.Clamp01(f);
        if (Mathf.Abs(f - fade) < 1e-3f) return;
        fade = f;
        nodeMaterial.SetFloat("_Fade", f);
        linkMaterial.SetColor("_Tint", new Color(1, 1, 1, f));
    }

    public Vector3 NodeWorld(int index) => data.Nodes[index].World;

    public float NodeTop(int index) => NodeRadius * NodeScale[index];

    void ApplyStyles()
    {
        stylesDirty = false;
        for (int s = 0; s < byShape.Length; s++)
        {
            List<int> nodes = byShape[s];
            for (int k = 0; k < nodes.Count; k++)
            {
                DanceGraphData.Node node = data.Nodes[nodes[k]];
                float r = NodeRadius * NodeScale[node.Index];
                matrices[s][k] = Matrix4x4.TRS(node.World, node.Facing, Vector3.one * r);
                Color c = NodeColor[node.Index];
                colors[s][k] = new Vector4(c.r, c.g, c.b, 1f);
                glows[s][k] = new Vector4(NodeGlow[node.Index], 0.6f + NodeGlow[node.Index] * 0.5f, 0, 0);
            }

            blocks[s].SetVectorArray("_NodeColor", colors[s]);
            blocks[s].SetVectorArray("_NodeGlow", glows[s]);
        }
    }

    /// <summary>
    /// rebuild the links glow mesh. linkWeight (optional, per data.Links index) scales width and brightness; dim
    /// multiplies unweighted links. Proposed links and the path's new links are dashed.
    /// </summary>
    public void RebuildLinks(float[] linkWeight = null, float dim = 1f, IList<(int from, int to, int count)> newLinks = null,
        float newLinkBrightness = 0.9f)
    {
        links.Begin();
        links.Viewer = null;
        for (int i = 0; i < data.Links.Count; i++)
        {
            DanceGraphData.Link l = data.Links[i];
            DanceGraphData.Node a = data.Nodes[l.FromIndex], b = data.Nodes[l.ToIndex];
            float w = linkWeight != null ? linkWeight[i] : -1f;
            Color tone = Color.Lerp(a.Blank ? Color.gray : a.Tone, b.Blank ? Color.gray : b.Tone, 0.5f);
            Color c;
            float width;
            if (w >= 0)
            {
                c = w > 0 ? Color.Lerp(tone, Color.white, 0.35f) * (0.55f + 0.45f * w) : tone * 0.15f;
                width = w > 0 ? 0.006f + 0.016f * w : 0.003f;
            }
            else
            {
                c = tone * 0.42f * dim;
                width = 0.0045f;
            }

            bool proposed = l.Status != "scaffold";
            if (proposed) c = Color.Lerp(c, new Color(1f, 0.9f, 0.4f) * 0.6f * dim, 0.6f);
            Arrow(a.Index, b.Index, width, c, proposed);
        }

        if (newLinks != null)
        {
            foreach ((int from, int to, int count) in newLinks)
            {
                if (from < 0 || to < 0) continue;
                Arrow(from, to, 0.006f + 0.004f * Mathf.Min(3, count), new Color(1f, 1f, 1f) * newLinkBrightness, true);
            }
        }

        // proposed nodes: dashed outline until accepted
        foreach (DanceGraphData.Node node in data.Nodes)
        {
            if (node.Status != "proposed") continue;
            float r = NodeRadius * NodeScale[node.Index] * 1.7f;
            links.Circle(node.World, Vector3.up, r, 0.005f, new Color(1f, 0.9f, 0.4f) * 0.8f * dim, 16, 0.55f);
            links.Circle(node.World, Vector3.right, r, 0.005f, new Color(1f, 0.9f, 0.4f) * 0.8f * dim, 16, 0.55f);
        }

        links.End();
    }

    void Arrow(int from, int to, float width, Color c, bool dashed)
    {
        Vector3 a = data.Nodes[from].World, b = data.Nodes[to].World;
        Vector3 d = b - a;
        float len = d.magnitude;
        if (len < 1e-4f) return;
        Vector3 dir = d / len;
        float ra = NodeRadius * NodeScale[from] + 0.006f, rb = NodeRadius * NodeScale[to] + 0.006f;
        if (len <= ra + rb + 0.02f) return;
        Vector3 start = a + dir * ra, tip = b - dir * rb;
        float head = Mathf.Min(0.045f, (len - ra - rb) * 0.4f);
        Vector3 headBase = tip - dir * head;
        if (dashed) links.DashedLine(start, headBase, width, c, 0.035f, 0.025f);
        else links.Line(start, headBase, width, c);
        links.Cone(headBase, tip, Mathf.Max(width * 1.8f, 0.011f), c * 1.2f);
    }

    void LateUpdate()
    {
        if (!visible || data == null) return;
        if (stylesDirty) ApplyStyles();
        DrawCalls = 0;
        RenderParams rp = new(nodeMaterial)
        {
            shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false,
            worldBounds = new Bounds(Vector3.up * 1.5f, new Vector3(20, 10, 20))
        };
        for (int s = 0; s < byShape.Length; s++)
        {
            int count = byShape[s].Count;
            if (count == 0) continue;
            rp.matProps = blocks[s];
            Graphics.RenderMeshInstanced(rp, meshes[s], 0, matrices[s], count);
            DrawCalls++;
        }

        UpdateLabels();
    }

    void UpdateLabels()
    {
        Camera cam = DanceText.ViewCamera;
        if (cam == null) return;
        Vector3 eye = cam.transform.position;
        Quaternion rot = cam.transform.rotation;
        int shown = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            TextMesh tm = labels[i];
            if (tm == null) continue;
            DanceGraphData.Node node = data.Nodes[i];
            float dist = Vector3.Distance(eye, node.World);
            float a;
            float height = LabelHeight;
            // labels right in front of the camera fade out too (the chase camera flies through the graph)
            float near = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.7f, dist));
            if (Labels == LabelMode.Marked)
            {
                a = LabelMask[i] ? 1f : 0f;
                height *= Mathf.Clamp(dist / 1.2f, 1f, 6f);
            }
            else
            {
                a = LabelMask[i] ? (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(LabelFadeNear, LabelFadeFar, dist))) * near : 0f;
            }

            if (LabelEmphasis[i])
            {
                a = Labels == LabelMode.Marked ? 1f : near;
                height = LabelHeight * 1.6f * Mathf.Clamp(dist / 1.5f, 1f, 4f);
            }

            a *= fade;
            bool on = a > 0.02f;
            if (tm.gameObject.activeSelf != on) tm.gameObject.SetActive(on);
            if (!on) continue;
            shown++;
            if (Mathf.Abs(labelAlpha[i] - a) > 0.02f)
            {
                labelAlpha[i] = a;
                Color c = LabelEmphasis[i] ? new Color(1f, 0.95f, 0.6f, a) : new Color(0.92f, 0.95f, 1f, a);
                tm.color = c;
            }

            float wanted = height * 10f / tm.fontSize;
            if (Mathf.Abs(tm.characterSize - wanted) > wanted * 0.05f) tm.characterSize = wanted;
            Transform t = tm.transform;
            t.position = node.World + Vector3.up * (NodeTop(i) + 0.012f);
            t.rotation = rot;
        }

        VisibleLabels = shown;
    }

    void OnDestroy()
    {
        if (nodeMaterial != null) Destroy(nodeMaterial);
        if (linkMaterial != null) Destroy(linkMaterial);
    }
}
