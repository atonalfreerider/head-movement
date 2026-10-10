using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// One head-worn prop of a role as capture.json describes it (<c>props</c>: a list of { role, kind, ...parameters }, written by
/// dancecap export_unity from the take TOML's [dancers.&lt;role&gt;.props.&lt;kind&gt;]; VIEWER_SPEC 3.2b). Roles only, never names.
/// Every key is optional: a missing one takes the default below (the same numbers as dancecap's HEADSET_DEFAULTS), a value outside
/// its range is clamped. Lengths are metres, colours "#rrggbb" (sRGB), left / right is the dancer's OWN side.
///
/// kind "headset" (the performance microphone teachers wear): an ear hook over the top of the ear, a thin boom along the cheek to the
/// mouth corner, a capsule (the foam windscreen) at its end.
/// </summary>
public sealed class HeadPropSpec
{
    public const string KindHeadset = "headset";

    public string Role = "follow";
    public string Kind = KindHeadset;
    /// <summary>the dancer's own right ear / cheek (false: left)</summary>
    public bool Right;
    /// <summary>arc length from the ear hook to the capsule centre; &lt;= 0: the boom reaches the mouth corner</summary>
    public float BoomLength = -1f;
    public float BoomThickness = 0.0024f;
    /// <summary>the boom axis above the skin along the cheek</summary>
    public float Standoff = 0.006f;
    /// <summary>extra standoff at the middle of the boom (a stiff boom bows away from the cheek)</summary>
    public float Bow = 0.004f;
    public float CapsuleLength = 0.026f;
    public float CapsuleRadius = 0.0078f;
    public bool Hook = true;
    public float HookThickness = 0.0024f;
    public Color CapsuleColour = ParseColour("#0e0d0d", Color.black);
    public Color BoomColour = ParseColour("#c4bbb2", Color.gray);
    public Color HookColour = ParseColour("#c4bbb2", Color.gray);
    /// <summary>added to the avatar's displayed opacity (smoothstep ramp, like the hair's +0.15) so a thin prop still reads at the
    /// translucent default; 0 = exactly the avatar's own opacity</summary>
    public float OpacityBoost = 0.15f;
    /// <summary>optional boom control points relative to the front of the ear: x outward (own side), y up, z forward; null = the
    /// surface-following default path</summary>
    public Vector3[] BoomPath;

    /// <summary>"#rrggbb" (sRGB) -> Color; the fallback for anything else (managed code only: this file is also compiled by the offline geometry harness)</summary>
    public static Color ParseColour(string hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#') return fallback;
        return int.TryParse(hex.Substring(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int rgb)
            ? new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f)
            : fallback;
    }

    static float Num(JObject o, string key, float def, float lo, float hi)
    {
        if (o[key] is not JValue v || v.Type == JTokenType.Null) return def;
        try
        {
            return Mathf.Clamp(v.Value<float>(), lo, hi);
        }
        catch (Exception)
        {
            return def;
        }
    }

    /// <summary>a props entry -> its spec; null (error set) for an entry that is not an object, has no known role, or an unknown kind</summary>
    public static HeadPropSpec FromJson(JToken token, out string error)
    {
        error = null;
        if (token is not JObject o)
        {
            error = "a prop entry is not an object";
            return null;
        }

        HeadPropSpec s = new();
        s.Role = ((string)o["role"] ?? "").Trim().ToLowerInvariant();
        s.Kind = ((string)o["kind"] ?? KindHeadset).Trim().ToLowerInvariant();
        if (s.Role != "lead" && s.Role != "follow")
        {
            error = $"prop role '{s.Role}' is not lead | follow";
            return null;
        }

        if (s.Kind != KindHeadset)
        {
            error = $"unknown prop kind '{s.Kind}' (known: {KindHeadset})";
            return null;
        }

        s.Right = string.Equals((string)o["side"], "right", StringComparison.OrdinalIgnoreCase);
        float len = Num(o, "boom_length_m", -1f, -1f, 0.3f);
        s.BoomLength = len > 0f ? Mathf.Max(0.02f, len) : -1f;
        s.BoomThickness = Num(o, "boom_thickness_m", s.BoomThickness, 0.0005f, 0.01f);
        s.Standoff = Num(o, "standoff_m", s.Standoff, 0f, 0.05f);
        s.Bow = Num(o, "bow_m", s.Bow, 0f, 0.05f);
        s.CapsuleLength = Num(o, "capsule_length_m", s.CapsuleLength, 0.005f, 0.06f);
        s.CapsuleRadius = Num(o, "capsule_radius_m", s.CapsuleRadius, 0.002f, 0.03f);
        s.Hook = o["hook"] is not JValue h || h.Type != JTokenType.Boolean || h.Value<bool>();
        s.HookThickness = Num(o, "hook_thickness_m", s.HookThickness, 0.0005f, 0.01f);
        s.CapsuleColour = ParseColour((string)o["colour"], s.CapsuleColour);
        s.BoomColour = ParseColour((string)o["boom_colour"], s.BoomColour);
        s.HookColour = ParseColour((string)o["hook_colour"], s.BoomColour);
        s.OpacityBoost = Num(o, "opacity_boost", s.OpacityBoost, 0f, 1f);
        if (o["boom_path"] is JArray path && path.Count >= 2)
        {
            List<Vector3> pts = new();
            foreach (JToken p in path)
            {
                if (p is JArray a && a.Count == 3)
                {
                    pts.Add(new Vector3(Mathf.Clamp(a[0].Value<float>(), -0.25f, 0.25f), Mathf.Clamp(a[1].Value<float>(), -0.25f, 0.25f),
                        Mathf.Clamp(a[2].Value<float>(), -0.25f, 0.25f)));
                }
            }

            if (pts.Count >= 2) s.BoomPath = pts.ToArray();
        }

        return s;
    }

    public override string ToString() =>
        $"{Role} {Kind} ({(Right ? "right" : "left")}): boom {(BoomLength > 0f ? $"{BoomLength * 1000f:F0} mm" : "to the mouth corner")}, " +
        $"standoff {Standoff * 1000f:F1} mm, capsule {CapsuleLength * 1000f:F0} x {2f * CapsuleRadius * 1000f:F0} mm";
}
