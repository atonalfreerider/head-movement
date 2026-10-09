using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The dance floor (VIEWER_SPEC 3.1, layer "grid", always on by default): a black translucent plane at y = 0 and a 1 m
/// grid of light-teal crosses at every grid intersection (no continuous lines), aligned to the origin (3.0) - never
/// re-oriented by the dancers. Small and faint (user 2026-10-07: "the floor crosses need to be smaller and fainter"):
/// 8 cm across, 5 mm lines at ~45 % of the earlier brightness (was 14 cm / 7 mm / full), so they mark the floor plane
/// without competing with the leader's T and the floor record. Built once per capture to cover the whole dance area
/// plus a margin; one static additive glow mesh for the crosses and one quad for the plane (2 draw calls).
/// floorYawDeg (user 2026-10-08: "rotate the floor only, by 30 deg CCW so that it lines up with the room in the videos"):
/// the crosses and the plane are turned by YawDeg degrees COUNTER-CLOCKWISE as seen from above (north +Z up, east +X right),
/// about the vertical axis through the origin. Only these floor visuals turn: the dancers, the camera, the room and every
/// overlay tied to the dancers (T axes, pivots, dials, footprints, balance axes) keep the world axes. Per capture:
/// capture.json "floor_yaw_deg" (CaptureManifest.floor_yaw_deg); hm_floor --yaw sets it for a session.
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
    Rect lastArea;
    bool built;

    public int CrossCount { get; private set; }

    /// <summary>world axis-aligned bounds (x, z) of the lattice rectangle the crosses cover</summary>
    public Rect Extent { get; private set; }

    public bool Visible => visible;

    /// <summary>floorYawDeg: degrees the floor visuals are turned counter-clockwise as seen from above (0 = aligned to the world axes)</summary>
    public float YawDeg { get; private set; }

    /// <summary>the lattice's first axis (u) on the floor map (x east, z north): (cos yaw, sin yaw) - counter-clockwise for a positive yaw</summary>
    public Vector2 AxisU => new(Mathf.Cos(YawDeg * Mathf.Deg2Rad), Mathf.Sin(YawDeg * Mathf.Deg2Rad));

    /// <summary>the lattice's second axis (v): u turned 90 degrees counter-clockwise</summary>
    public Vector2 AxisV => new(-AxisU.y, AxisU.x);

    /// <summary>a lattice point (u, v) metres on the floor map (world x, z): world = R(yaw) (u, v), R counter-clockwise</summary>
    public Vector2 ToWorld(float u, float v)
    {
        Vector2 a = AxisU, b = AxisV;
        return new Vector2(a.x * u + b.x * v, a.y * u + b.y * v);
    }

    /// <summary>a world point on the floor map in the lattice frame (the inverse of ToWorld)</summary>
    public Vector2 ToLattice(float x, float z)
    {
        Vector2 a = AxisU, b = AxisV;
        return new Vector2(a.x * x + a.y * z, b.x * x + b.y * z);
    }

    /// <summary>turn the floor visuals to a new yaw (degrees, counter-clockwise from above) and rebuild them</summary>
    public void SetYaw(float deg)
    {
        deg = float.IsFinite(deg) ? deg : 0f;
        if (Mathf.Approximately(deg, YawDeg)) return;
        YawDeg = deg;
        if (built) Build(lastArea);
    }

    /// <summary>(re)build for a dance area (world xz, after the origin offset) at a yaw (the capture's floor_yaw_deg)</summary>
    public void Build(Rect danceArea, float yawDeg)
    {
        YawDeg = float.IsFinite(yawDeg) ? yawDeg : 0f;
        Build(danceArea);
    }

    /// <summary>(re)build for a dance area (world xz, after the origin offset)</summary>
    public void Build(Rect danceArea)
    {
        if (crossMaterial == null) crossMaterial = GlowMesh.NewMaterial("Floor grid crosses", Intensity);
        if (crosses == null) crosses = GlowMesh.Create("Floor grid crosses", transform, crossMaterial);
        lastArea = danceArea;
        built = true;

        // the lattice rectangle: the world dance area plus its margin (and the minimum extent), seen in the lattice frame (u, v)
        float wx0 = Mathf.Min(danceArea.xMin - Margin, -MinHalfExtent), wx1 = Mathf.Max(danceArea.xMax + Margin, MinHalfExtent);
        float wz0 = Mathf.Min(danceArea.yMin - Margin, -MinHalfExtent), wz1 = Mathf.Max(danceArea.yMax + Margin, MinHalfExtent);
        float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
        foreach (Vector2 corner in new[] { new Vector2(wx0, wz0), new Vector2(wx0, wz1), new Vector2(wx1, wz0), new Vector2(wx1, wz1) })
        {
            Vector2 l = ToLattice(corner.x, corner.y);
            u0 = Mathf.Min(u0, l.x);
            u1 = Mathf.Max(u1, l.x);
            v0 = Mathf.Min(v0, l.y);
            v1 = Mathf.Max(v1, l.y);
        }

        // at yaw 0 the corners are the world rectangle itself, so the lattice is the earlier axis-aligned one, point for point
        u0 = Mathf.Floor(u0 + 1e-4f);
        u1 = Mathf.Ceil(u1 - 1e-4f);
        v0 = Mathf.Floor(v0 + 1e-4f);
        v1 = Mathf.Ceil(v1 - 1e-4f);
        float xMin = float.MaxValue, xMax = float.MinValue, zMin = float.MaxValue, zMax = float.MinValue;
        foreach (Vector2 uv in new[] { new Vector2(u0, v0), new Vector2(u0, v1), new Vector2(u1, v0), new Vector2(u1, v1) })
        {
            Vector2 w = ToWorld(uv.x, uv.y);
            xMin = Mathf.Min(xMin, w.x);
            xMax = Mathf.Max(xMax, w.x);
            zMin = Mathf.Min(zMin, w.y);
            zMax = Mathf.Max(zMax, w.y);
        }

        Extent = Rect.MinMaxRect(xMin, zMin, xMax, zMax);

        crosses.Viewer = null;
        crosses.Begin();
        CrossCount = 0;
        Color c = new(Teal.r * Brightness, Teal.g * Brightness, Teal.b * Brightness, 1f);
        Vector2 au = AxisU, av = AxisV;
        Vector3 armU = new Vector3(au.x, 0f, au.y) * CrossArm, armV = new Vector3(av.x, 0f, av.y) * CrossArm;
        for (float u = u0; u <= u1 + 1e-3f; u += 1f)
        {
            for (float v = v0; v <= v1 + 1e-3f; v += 1f)
            {
                Vector2 w = ToWorld(u, v);
                Vector3 p = new(w.x, 0.002f, w.y);
                crosses.FloorStrip(p - armU, p + armU, CrossWidth, c);
                crosses.FloorStrip(p - armV, p + armV, CrossWidth, c);
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
        Vector2 centre = ToWorld(0.5f * (u0 + u1), 0.5f * (v0 + v1));
        // a counter-clockwise turn seen from above is a NEGATIVE rotation about Unity's +Y (left-handed: positive = clockwise)
        plane.transform.SetPositionAndRotation(new Vector3(centre.x, -0.001f, centre.y), Quaternion.Euler(0f, -YawDeg, 0f));
        plane.transform.localScale = new Vector3(u1 - u0 + 2f, 1f, v1 - v0 + 2f);
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
