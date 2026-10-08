using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// A dynamic additive-glow mesh rebuilt from simple primitives: floor strips, 3D lines, dashed lines, discs, rings,
/// cones (arrowheads) and small spheres. Vertex/index arrays are reused and only ever grow, so rebuilding every
/// frame allocates nothing (Quest budget). Colours are linear 0..1 (rgb * a), drawn by Resources/HM_Glow.shader
/// (additive: black = invisible, so fading is a colour fade); brightness above 1 comes from the material
/// intensity. Positions are world space (the GameObject stays at the identity transform).
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class GlowMesh : MonoBehaviour
{
    static Shader shader;

    Vector3[] verts = new Vector3[256];
    Color[] cols = new Color[256];
    int[] tris = new int[768];
    int nv, ni;
    Mesh mesh;
    MeshRenderer meshRenderer;

    /// <summary>when set, Line() builds strips facing this point (the camera); otherwise two crossed strips</summary>
    public Vector3? Viewer;

    public int VertexCount => nv;
    public Material Material => meshRenderer.sharedMaterial;

    public static Material NewMaterial(string name, float intensity)
    {
        if (shader == null) shader = Resources.Load<Shader>("HM_Glow");
        if (shader == null) shader = Shader.Find("HeadMovement/Glow");
        Material m = new(shader) { name = name };
        m.SetFloat("_Intensity", intensity);
        m.SetColor("_Tint", Color.white);
        return m;
    }

    public static GlowMesh Create(string name, Transform parent, Material material)
    {
        GameObject go = new(name);
        go.transform.SetParent(parent, false);
        GlowMesh g = go.AddComponent<GlowMesh>();
        g.Init(material);
        return g;
    }

    void Init(Material material)
    {
        mesh = new Mesh { name = gameObject.name, indexFormat = IndexFormat.UInt32 };
        mesh.MarkDynamic();
        GetComponent<MeshFilter>().sharedMesh = mesh;
        meshRenderer = GetComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;
        meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    public void SetVisible(bool on) => meshRenderer.enabled = on;

    public bool Visible => meshRenderer != null && meshRenderer.enabled;

    public void Begin()
    {
        nv = 0;
        ni = 0;
    }

    public void End()
    {
        mesh.Clear(true);
        if (nv > 0)
        {
            mesh.SetVertices(verts, 0, nv);
            mesh.SetColors(cols, 0, nv);
            mesh.SetIndices(tris, 0, ni, MeshTopology.Triangles, 0, false);
        }

        mesh.bounds = new Bounds(Vector3.zero, new Vector3(400, 400, 400));
    }

    void Ensure(int addVerts, int addIndices)
    {
        if (nv + addVerts > verts.Length)
        {
            int n = Mathf.Max(verts.Length * 2, nv + addVerts);
            System.Array.Resize(ref verts, n);
            System.Array.Resize(ref cols, n);
        }

        if (ni + addIndices > tris.Length) System.Array.Resize(ref tris, Mathf.Max(tris.Length * 2, ni + addIndices));
    }

    int V(Vector3 p, Color c)
    {
        verts[nv] = p;
        cols[nv] = c;
        return nv++;
    }

    void T(int a, int b, int c)
    {
        tris[ni++] = a;
        tris[ni++] = b;
        tris[ni++] = c;
    }

    static Vector3 AnyPerpendicular(Vector3 d) =>
        (Mathf.Abs(d.y) < 0.9f ? Vector3.Cross(d, Vector3.up) : Vector3.Cross(d, Vector3.right)).normalized;

    /// <summary>flat strip from a to b lying in the plane with the given normal (floor items: Vector3.up)</summary>
    public void Strip(Vector3 a, Vector3 b, float width, Color ca, Color cb, Vector3 normal)
    {
        Vector3 d = b - a;
        float len = d.magnitude;
        if (len < 1e-5f || (ca.a <= 0 && cb.a <= 0)) return;
        Vector3 side = Vector3.Cross(normal, d / len);
        if (side.sqrMagnitude < 1e-8f) side = AnyPerpendicular(d / len);
        side = side.normalized * (width * 0.5f);
        Ensure(4, 6);
        int i0 = V(a - side, ca), i1 = V(a + side, ca), i2 = V(b + side, cb), i3 = V(b - side, cb);
        T(i0, i1, i2);
        T(i0, i2, i3);
    }

    public void FloorStrip(Vector3 a, Vector3 b, float width, Color c) => Strip(a, b, width, c, c, Vector3.up);

    /// <summary>filled triangle (arrowheads); drawn from both sides (the glow shader does not cull)</summary>
    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        if (color.a <= 0) return;
        Ensure(3, 3);
        int i0 = V(a, color), i1 = V(b, color), i2 = V(c, color);
        T(i0, i1, i2);
    }

    /// <summary>3D line: a strip facing Viewer when set, else two crossed strips (readable from any side)</summary>
    public void Line(Vector3 a, Vector3 b, float width, Color ca, Color cb)
    {
        Vector3 d = b - a;
        float len = d.magnitude;
        if (len < 1e-5f) return;
        Vector3 dir = d / len;
        if (Viewer.HasValue)
        {
            Vector3 toViewer = Viewer.Value - (a + b) * 0.5f;
            Vector3 n = toViewer - Vector3.Dot(toViewer, dir) * dir;
            if (n.sqrMagnitude < 1e-8f) n = AnyPerpendicular(dir);
            Strip(a, b, width, ca, cb, n.normalized);
            return;
        }

        Vector3 n1 = AnyPerpendicular(dir);
        Vector3 n2 = Vector3.Cross(dir, n1);
        Strip(a, b, width, ca, cb, n1);
        Strip(a, b, width, ca, cb, n2);
    }

    public void Line(Vector3 a, Vector3 b, float width, Color c) => Line(a, b, width, c, c);

    /// <summary>dashed 3D line (dash + gap in metres), starting with a dash at a</summary>
    public void DashedLine(Vector3 a, Vector3 b, float width, Color c, float dash, float gap)
    {
        Vector3 d = b - a;
        float len = d.magnitude;
        if (len < 1e-5f) return;
        Vector3 dir = d / len;
        float period = Mathf.Max(1e-3f, dash + gap);
        for (float s = 0; s < len; s += period)
        {
            Line(a + dir * s, a + dir * Mathf.Min(len, s + dash), width, c, c);
        }
    }

    public void DashedFloorStrip(Vector3 a, Vector3 b, float width, Color c, float dash, float gap)
    {
        Vector3 d = b - a;
        float len = d.magnitude;
        if (len < 1e-5f) return;
        Vector3 dir = d / len;
        float period = Mathf.Max(1e-3f, dash + gap);
        for (float s = 0; s < len; s += period)
        {
            FloorStrip(a + dir * s, a + dir * Mathf.Min(len, s + dash), width, c);
        }
    }

    /// <summary>filled horizontal disc (centre colour fading to the rim colour)</summary>
    public void Disc(Vector3 centre, float radius, Color inner, Color outer, int segments = 24)
    {
        if (inner.a <= 0 && outer.a <= 0) return;
        Ensure(segments + 1, segments * 3);
        int c = V(centre, inner);
        int first = nv;
        for (int i = 0; i < segments; i++)
        {
            float a = i * 2f * Mathf.PI / segments;
            V(centre + new Vector3(Mathf.Cos(a) * radius, 0, Mathf.Sin(a) * radius), outer);
        }

        for (int i = 0; i < segments; i++) T(c, first + (i + 1) % segments, first + i);
    }

    /// <summary>horizontal ring of the given line width; dashFraction &lt; 1 draws it dashed</summary>
    public void Ring(Vector3 centre, float radius, float width, Color color, int segments = 48, float dashFraction = 1f)
    {
        if (color.a <= 0) return;
        float r0 = radius - width * 0.5f, r1 = radius + width * 0.5f;
        if (dashFraction >= 0.999f)
        {
            Ensure(segments * 2, segments * 6);
            int first = nv;
            for (int i = 0; i < segments; i++)
            {
                float a = i * 2f * Mathf.PI / segments;
                Vector3 dir = new(Mathf.Cos(a), 0, Mathf.Sin(a));
                V(centre + dir * r0, color);
                V(centre + dir * r1, color);
            }

            for (int i = 0; i < segments; i++)
            {
                int j = (i + 1) % segments;
                T(first + 2 * i, first + 2 * j, first + 2 * i + 1);
                T(first + 2 * i + 1, first + 2 * j, first + 2 * j + 1);
            }

            return;
        }

        float step = 2f * Mathf.PI / segments;
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * step, a1 = a0 + step * dashFraction;
            Vector3 d0 = new(Mathf.Cos(a0), 0, Mathf.Sin(a0)), d1 = new(Mathf.Cos(a1), 0, Mathf.Sin(a1));
            Ensure(4, 6);
            int i0 = V(centre + d0 * r0, color), i1 = V(centre + d0 * r1, color);
            int i2 = V(centre + d1 * r1, color), i3 = V(centre + d1 * r0, color);
            T(i0, i2, i1);
            T(i0, i3, i2);
        }
    }

    /// <summary>ring in an arbitrary plane (normal), e.g. a dashed outline around a node</summary>
    public void Circle(Vector3 centre, Vector3 normal, float radius, float width, Color color, int segments = 32,
        float dashFraction = 1f)
    {
        if (color.a <= 0) return;
        Vector3 u = AnyPerpendicular(normal.normalized), v = Vector3.Cross(normal.normalized, u);
        float step = 2f * Mathf.PI / segments;
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * step, a1 = a0 + step * Mathf.Clamp01(dashFraction);
            Vector3 p0 = centre + (u * Mathf.Cos(a0) + v * Mathf.Sin(a0)) * radius;
            Vector3 p1 = centre + (u * Mathf.Cos(a1) + v * Mathf.Sin(a1)) * radius;
            Strip(p0, p1, width, color, color, normal);
        }
    }

    /// <summary>cone from a base centre to a tip (arrowheads), open base</summary>
    public void Cone(Vector3 baseCentre, Vector3 tip, float radius, Color color, int sides = 6)
    {
        Vector3 axis = tip - baseCentre;
        if (axis.sqrMagnitude < 1e-10f || color.a <= 0) return;
        Vector3 u = AnyPerpendicular(axis.normalized), v = Vector3.Cross(axis.normalized, u);
        Ensure(sides + 1, sides * 3);
        int t = V(tip, color);
        int first = nv;
        for (int i = 0; i < sides; i++)
        {
            float a = i * 2f * Mathf.PI / sides;
            V(baseCentre + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * radius, color * 0.6f);
        }

        for (int i = 0; i < sides; i++) T(t, first + i, first + (i + 1) % sides);
    }

    /// <summary>small low-poly sphere (octahedron subdivided once: 18 vertices)</summary>
    public void Sphere(Vector3 centre, float radius, Color color)
    {
        if (color.a <= 0) return;
        Ensure(SphereDirs.Length, SphereTris.Length);
        int first = nv;
        foreach (Vector3 d in SphereDirs) V(centre + d * radius, color);
        foreach (int i in SphereTris) tris[ni++] = first + i;
    }

    static readonly Vector3[] SphereDirs = BuildSphereDirs();
    static readonly int[] SphereTris = BuildSphereTris();

    static Vector3[] BuildSphereDirs()
    {
        // 6 octahedron vertices + 12 edge midpoints, normalised
        Vector3[] o = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        Vector3[] d = new Vector3[18];
        for (int i = 0; i < 6; i++) d[i] = o[i];
        int k = 6;
        for (int i = 0; i < 6; i++)
        {
            for (int j = i + 1; j < 6; j++)
            {
                if (Vector3.Dot(o[i], o[j]) < -0.5f) continue; // opposite poles
                d[k++] = (o[i] + o[j]).normalized;
            }
        }

        return d;
    }

    static int[] BuildSphereTris()
    {
        Vector3[] d = BuildSphereDirs();
        System.Collections.Generic.List<int> t = new();
        // each octahedron face (one +-x, one +-y, one +-z vertex) splits into 4 triangles
        int[] xs = { 0, 1 }, ys = { 2, 3 }, zs = { 4, 5 };
        foreach (int x in xs)
        {
            foreach (int y in ys)
            {
                foreach (int z in zs)
                {
                    int xy = Mid(d, x, y), yz = Mid(d, y, z), zx = Mid(d, z, x);
                    Add(t, d, x, xy, zx);
                    Add(t, d, y, yz, xy);
                    Add(t, d, z, zx, yz);
                    Add(t, d, xy, yz, zx);
                }
            }
        }

        return t.ToArray();
    }

    static int Mid(Vector3[] d, int a, int b)
    {
        Vector3 m = (d[a] + d[b]).normalized;
        for (int i = 6; i < d.Length; i++)
        {
            if ((d[i] - m).sqrMagnitude < 1e-6f) return i;
        }

        return a;
    }

    static void Add(System.Collections.Generic.List<int> t, Vector3[] d, int a, int b, int c)
    {
        // outward winding (Unity: clockwise seen from outside)
        Vector3 n = Vector3.Cross(d[b] - d[a], d[c] - d[a]);
        if (Vector3.Dot(n, d[a] + d[b] + d[c]) < 0)
        {
            (b, c) = (c, b);
        }

        t.Add(a);
        t.Add(b);
        t.Add(c);
    }

    void OnDestroy()
    {
        if (mesh != null) Destroy(mesh);
    }
}
