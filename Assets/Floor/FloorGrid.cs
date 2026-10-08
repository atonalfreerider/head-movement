using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The dance floor (VIEWER_SPEC 3.1, layer "grid", always on by default): a black translucent plane at y = 0 and a 1 m
/// grid of light-teal crosses at every grid intersection (no continuous lines), aligned to the origin (3.0) - never
/// re-oriented by the dancers. Small and faint (user 2026-10-07: "the floor crosses need to be smaller and fainter"):
/// 8 cm across, 5 mm lines at ~45 % of the earlier brightness (was 14 cm / 7 mm / full), so they mark the floor plane
/// without competing with the leader's T and the floor record. Built once per capture to cover the whole dance area
/// plus a margin; one static additive glow mesh for the crosses and one quad for the plane (2 draw calls).
/// </summary>
public class FloorGrid : MonoBehaviour
{
    public float PlaneAlpha = 0.6f;
    public float CrossArm = 0.04f;   // half-length of each cross arm (m): crosses are 8 cm across (14 cm before 2026-10-07 late)
    public float CrossWidth = 0.005f;
    [Tooltip("brightness of the crosses (1 = the earlier, brighter look)")]
    public float Brightness = 0.45f;
    public float Margin = 2f;
    public float MinHalfExtent = 4f;
    public Color Teal = new(0.30f, 0.86f, 0.92f); // light teal (spec #19C3D6, lightened: user "light teal crosses")
    public float Intensity = 1.1f;

    GlowMesh crosses;
    Material crossMaterial, planeMaterial;
    GameObject plane;
    bool visible = true;

    public int CrossCount { get; private set; }
    public Rect Extent { get; private set; }
    public bool Visible => visible;

    /// <summary>(re)build for a dance area (world xz, after the origin offset)</summary>
    public void Build(Rect danceArea)
    {
        if (crossMaterial == null) crossMaterial = GlowMesh.NewMaterial("Floor grid crosses", Intensity);
        if (crosses == null) crosses = GlowMesh.Create("Floor grid crosses", transform, crossMaterial);
        float x0 = Mathf.Floor(Mathf.Min(danceArea.xMin - Margin, -MinHalfExtent));
        float x1 = Mathf.Ceil(Mathf.Max(danceArea.xMax + Margin, MinHalfExtent));
        float z0 = Mathf.Floor(Mathf.Min(danceArea.yMin - Margin, -MinHalfExtent));
        float z1 = Mathf.Ceil(Mathf.Max(danceArea.yMax + Margin, MinHalfExtent));
        Extent = Rect.MinMaxRect(x0, z0, x1, z1);

        crosses.Viewer = null;
        crosses.Begin();
        CrossCount = 0;
        Color c = new(Teal.r * Brightness, Teal.g * Brightness, Teal.b * Brightness, 1f);
        for (float x = x0; x <= x1 + 1e-3f; x += 1f)
        {
            for (float z = z0; z <= z1 + 1e-3f; z += 1f)
            {
                Vector3 p = new(x, 0.002f, z);
                crosses.FloorStrip(p - Vector3.right * CrossArm, p + Vector3.right * CrossArm, CrossWidth, c);
                crosses.FloorStrip(p - Vector3.forward * CrossArm, p + Vector3.forward * CrossArm, CrossWidth, c);
                CrossCount++;
            }
        }

        crosses.End();

        if (plane == null)
        {
            plane = new GameObject("Floor plane");
            plane.transform.SetParent(transform, false);
            Mesh m = new() { name = "Floor plane" };
            m.vertices = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(-0.5f, 0, 0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(0.5f, 0, -0.5f) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateBounds();
            plane.AddComponent<MeshFilter>().sharedMesh = m;
            MeshRenderer r = plane.AddComponent<MeshRenderer>();
            Shader s = Resources.Load<Shader>("HM_FloorPlane");
            if (s == null) s = Shader.Find("HeadMovement/FloorPlane");
            planeMaterial = new Material(s) { name = "Floor plane" };
            r.sharedMaterial = planeMaterial;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        planeMaterial.SetColor("_Color", new Color(0, 0, 0, PlaneAlpha));
        plane.transform.position = new Vector3(Extent.center.x, -0.001f, Extent.center.y);
        plane.transform.localScale = new Vector3(Extent.width + 2f, 1f, Extent.height + 2f);
        SetVisible(visible);
    }

    public void SetPlaneAlpha(float alpha)
    {
        PlaneAlpha = Mathf.Clamp01(alpha);
        if (planeMaterial != null) planeMaterial.SetColor("_Color", new Color(0, 0, 0, PlaneAlpha));
    }

    public void SetVisible(bool on)
    {
        visible = on;
        if (crosses != null) crosses.SetVisible(on);
        if (plane != null) plane.SetActive(on);
    }

    void OnDestroy()
    {
        if (crossMaterial != null) Destroy(crossMaterial);
        if (planeMaterial != null) Destroy(planeMaterial);
    }
}
