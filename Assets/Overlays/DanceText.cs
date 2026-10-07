using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// World-space labels for the dance layers: legacy TextMesh with the built-in font (TMP essentials are not
/// imported in this project). Labels billboard towards the camera and fade by colour alpha; text/alpha are only
/// written when they change, so idle labels cost no allocations.
/// </summary>
public static class DanceText
{
    static Font font;

    public static Font Font
    {
        get
        {
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return font;
        }
    }

    /// <param name="height">approximate line height in metres</param>
    public static TextMesh WorldLabel(Transform parent, string name, Font f, float height, Color color)
    {
        f ??= Font;
        GameObject go = new(name);
        go.transform.SetParent(parent, false);
        MeshRenderer r = go.AddComponent<MeshRenderer>();
        TextMesh tm = go.AddComponent<TextMesh>();
        tm.font = f;
        r.sharedMaterial = f.material;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off;
        tm.fontSize = 64;
        tm.characterSize = height * 10f / tm.fontSize;
        tm.anchor = TextAnchor.LowerCenter;
        tm.alignment = TextAlignment.Center;
        tm.richText = false;
        tm.color = color;
        return tm;
    }

    public static void SetHeight(TextMesh tm, float height) => tm.characterSize = height * 10f / Mathf.Max(1, tm.fontSize);

    public static void Billboard(Transform t)
    {
        Camera cam = ViewCamera;
        if (cam != null) t.rotation = cam.transform.rotation;
    }

    static Camera viewCamera;

    /// <summary>the camera that renders the view: MainCamera if tagged, else the desktop orbit rig's camera, else
    /// the first active camera (cached; the scene's camera is not tagged MainCamera)</summary>
    public static Camera ViewCamera
    {
        get
        {
            if (viewCamera != null && viewCamera.isActiveAndEnabled) return viewCamera;
            viewCamera = Camera.main;
            if (viewCamera == null && HeadMovement.Instance != null && HeadMovement.Instance.OrbitCamera != null)
            {
                viewCamera = HeadMovement.Instance.OrbitCamera.GetComponentInChildren<Camera>();
            }

            if (viewCamera == null && Camera.allCamerasCount > 0) viewCamera = Camera.allCameras[0];
            return viewCamera;
        }
    }

    public static void SetAlpha(TextMesh tm, float alpha)
    {
        Color c = tm.color;
        if (Mathf.Abs(c.a - alpha) < 0.02f) return;
        c.a = alpha;
        tm.color = c;
    }

    public static void SetColor(TextMesh tm, Color color)
    {
        Color c = tm.color;
        if (Mathf.Abs(c.r - color.r) + Mathf.Abs(c.g - color.g) + Mathf.Abs(c.b - color.b) + Mathf.Abs(c.a - color.a) < 0.02f) return;
        tm.color = color;
    }
}
