using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// One dancer's sneaker: style (sole stack, toe spring, construction details) + colours. Read from the capture's
/// optional shoes.json (dancecap/dancecap/shoes.py: colours sampled from the dancers' video and texture bake - likeness
/// data, git-ignored StreamingAssets only). Without the file a generic neutral sneaker is used; the code holds no
/// dancer colours.
///
/// Styles
///   court_platform  chunky flat cupsole (4.0 / 3.5 cm), broad rounded toe, smooth leather, stitched sole groove,
///                   contrasting heel tab
///   runner          sculpted foam midsole with a 10 mm drop and a contrasting heel unit, knit upper, swept side stripe,
///                   strong toe spring, rubber outsole band
///   neutral         a plain sneaker between the two
/// </summary>
public class SneakerStyle
{
    public string Style = "neutral";

    // sRGB colours as authored (Unity Color in gamma space)
    public Color Upper = Hex("#d9d6cf");
    public Color Sole = Hex("#ecebe6");
    public Color SoleHeel = Hex("#ecebe6");
    public Color Outsole = Hex("#5a5a5a");
    public Color Accent = Hex("#9a9a9a");
    public Color Lace = Hex("#d9d6cf");
    public Color Lining = Hex("#b8b4ac");
    public Color HeelTab = Hex("#8a8a8a");

    // geometry (metres above the fitted foot's sole plane, which is the outsole bottom)
    public float SoleHeelM = 0.030f;
    public float SoleForeM = 0.024f;
    public float ToeSpringM = 0.009f;
    public float HeelBevelM = 0.005f;

    /// <summary>scene light share on the shoe material (the atlas is flat colour + painted AO, not photo-lit)</summary>
    public float LightInfluence = 0.5f;

    public string Source = "default";

    public bool Platform => Style == "court_platform";
    public bool Runner => Style == "runner";

    /// <summary>sole flare beyond the upper (chunky cupsoles stick out more)</summary>
    public float Flare => Platform ? 0.0045f : Runner ? 0.004f : 0.0035f;

    /// <summary>outward bulge of the runner's heel unit over the rear of the midsole</summary>
    public float HeelBump => Runner ? 0.004f : 0f;

    /// <summary>height of the rubber outsole band on the sole wall</summary>
    public float OutsoleBandM => Runner ? 0.006f : Platform ? 0.003f : 0.004f;

    /// <summary>collar raise for platform soles (the foot sits higher in the shoe)</summary>
    public float CollarRaise => Platform ? 0.5f * Mathf.Max(0f, SoleHeelM - 0.025f) : 0f;

    public static SneakerStyle Default(Role role) => new() { Source = "default (no shoes.json)" };

    /// <param name="path">shoes.json (null or missing -> Default)</param>
    public static SneakerStyle FromJson(string path, Role role)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Default(role);
        JObject root = JObject.Parse(File.ReadAllText(path));
        if (root.Value<int?>("version") != 1) throw new InvalidDataException($"{path}: unsupported shoes.json version {root["version"]}");
        if (root["dancers"]?[role.ToString().ToLowerInvariant()] is not JObject d) return Default(role);
        SneakerStyle s = new() { Source = Path.GetFileName(path) };
        s.Style = d.Value<string>("style") ?? s.Style;
        s.Upper = Col(d, "upper", s.Upper);
        s.Sole = Col(d, "sole", s.Sole);
        s.SoleHeel = Col(d, "sole_heel", s.Sole);
        s.Outsole = Col(d, "outsole", s.Outsole);
        s.Accent = Col(d, "accent", s.Accent);
        s.Lace = Col(d, "lace", s.Lace);
        s.Lining = Col(d, "lining", s.Lining);
        s.HeelTab = Col(d, "heel_tab", s.HeelTab);
        s.SoleHeelM = Num(d, "sole_heel_m", s.SoleHeelM, 0.01f, 0.07f);
        s.SoleForeM = Num(d, "sole_fore_m", s.SoleForeM, 0.008f, 0.06f);
        s.ToeSpringM = Num(d, "toe_spring_m", s.ToeSpringM, 0f, 0.025f);
        s.HeelBevelM = Num(d, "heel_bevel_m", s.HeelBevelM, 0f, 0.015f);
        s.LightInfluence = Num(d, "light_influence", s.LightInfluence, 0f, 1f);
        return s;
    }

    static float Num(JObject d, string key, float fallback, float lo, float hi) =>
        d[key] != null ? Mathf.Clamp(d.Value<float>(key), lo, hi) : fallback;

    static Color Col(JObject d, string key, Color fallback)
    {
        string h = d.Value<string>(key);
        return string.IsNullOrEmpty(h) ? fallback : Hex(h);
    }

    public static Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) throw new InvalidDataException($"colour '{hex}' is not #rrggbb");
        int v = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, 1f);
    }

    public static string ToHex(Color c) =>
        $"#{Mathf.RoundToInt(c.r * 255):x2}{Mathf.RoundToInt(c.g * 255):x2}{Mathf.RoundToInt(c.b * 255):x2}";
}
