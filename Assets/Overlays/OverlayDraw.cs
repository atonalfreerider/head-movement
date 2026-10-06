using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Small helpers shared by the timing and physics overlays: glow lines, rings, arrows, unlit markers,
/// HUD rectangles. Line colours go through the vertex colour of the bloom material, as the skeletons do.</summary>
public static class OverlayDraw
{
    public static LineRenderer Line(Transform parent, string name, Material glow, float width, int points = 2,
        bool loop = false)
    {
        GameObject go = new(name);
        go.transform.SetParent(parent, false);
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.sharedMaterial = glow;
        line.useWorldSpace = true;
        line.widthMultiplier = width;
        line.positionCount = points;
        line.loop = loop;
        line.numCapVertices = 2;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        return line;
    }

    public static void SetColor(LineRenderer line, Color c)
    {
        line.startColor = c;
        line.endColor = c;
    }

    /// <summary>horizontal circle (floor ring) of radius r around centre</summary>
    public static void Ring(LineRenderer line, Vector3 centre, float r)
    {
        int n = line.positionCount;
        for (int i = 0; i < n; i++)
        {
            float a = i * 2f * Mathf.PI / n;
            line.SetPosition(i, centre + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r));
        }
    }

    /// <summary>arrow as a 5-point polyline: tail -> tip -> head barb -> tip -> other barb</summary>
    public static void Arrow(LineRenderer line, Vector3 tail, Vector3 tip, Vector3 viewer)
    {
        line.positionCount = 5;
        Vector3 dir = tip - tail;
        float len = dir.magnitude;
        if (len < 1e-4f)
        {
            for (int i = 0; i < 5; i++) line.SetPosition(i, tail);
            return;
        }

        dir /= len;
        Vector3 side = Vector3.Cross(dir, viewer - tip);
        if (side.sqrMagnitude < 1e-6f) side = Vector3.Cross(dir, Vector3.right);
        side.Normalize();
        float head = Mathf.Min(0.08f, len * 0.3f);
        line.SetPosition(0, tail);
        line.SetPosition(1, tip);
        line.SetPosition(2, tip - dir * head + side * head * 0.5f);
        line.SetPosition(3, tip);
        line.SetPosition(4, tip - dir * head - side * head * 0.5f);
    }

    public static GameObject Marker(Transform parent, string name, PrimitiveType type, float size, Color color)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localScale = Vector3.one * size;
        Renderer r = go.GetComponent<Renderer>();
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        Material m = new(shader) { name = name };
        m.SetColor("_BaseColor", color);
        m.SetColor("_Color", color);
        r.sharedMaterial = m;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        return go;
    }

    public static void SetMarkerColor(GameObject marker, Color color)
    {
        Material m = marker.GetComponent<Renderer>().sharedMaterial;
        m.SetColor("_BaseColor", color);
        m.SetColor("_Color", color);
    }

    /// <summary>filled HUD rectangle (call from OnGUI)</summary>
    public static void Rect(Rect r, Color c)
    {
        Color old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    /// <summary>asynchrony colour: green on the beat, amber/red late, cyan/blue early (saturates at 120 ms)</summary>
    public static Color AsyncColor(float asyncMs, float onBeatMs = 30f)
    {
        if (float.IsNaN(asyncMs)) return new Color(0.6f, 0.6f, 0.6f);
        float e = Mathf.Abs(asyncMs);
        Color onBeat = new(0.15f, 1f, 0.45f);
        if (e <= onBeatMs) return onBeat;
        float k = Mathf.Clamp01((e - onBeatMs) / (120f - onBeatMs));
        return asyncMs > 0
            ? Color.Lerp(new Color(1f, 0.8f, 0.1f), new Color(1f, 0.15f, 0.08f), k)
            : Color.Lerp(new Color(0.2f, 0.85f, 1f), new Color(0.25f, 0.35f, 1f), k);
    }
}
