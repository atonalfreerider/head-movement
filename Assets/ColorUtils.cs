using UnityEngine;

public static class ColorFormatter
{
    public static string AsHexString(this Color color) =>
        AsHexString((Color32) color);

    static string AsHexString(this Color32 color) =>
        $"#{color.r:X2}{color.g:X2}{color.b:X2}{color.a:X2}";
}

public static class ColorTransformer
{
    public static Color WithAlpha(this Color color, float alpha) =>
        new(color.r, color.g, color.b, alpha);
}

public static class Viridis
{
    // https://cran.r-project.org/web/packages/viridis/vignettes/intro-to-viridis.html
    static readonly Color32[] viridis = {
        new(253, 231, 37, 255),
        new(220, 227, 25, 255),
        new(184, 222, 41, 255),
        new(149, 216, 64, 255),
        new(115, 208, 85, 255),
        new(85, 198, 103, 255),
        new(60, 187, 117, 255),
        new(41, 175, 127, 255),
        new(32, 163, 135, 255),
        new(31, 150, 139, 255),
        new(35, 138, 141, 255),
        new(40, 125, 142, 255),
        new(45, 112, 142, 255),
        new(51, 99, 141, 255),
        new(57, 86, 140, 255),
        new(64, 71, 136, 255),
        new(69, 55, 129, 255),
        new(72, 38, 119, 255),
        new(72, 21, 103, 255),
        new(68, 1, 84, 255)
    };
        
    public static Color ViridisColor(float prct)
    {
        return viridis[Mathf.RoundToInt(Mathf.Min(prct, 1) * (viridis.Length - 1))];
    }
}
    
public static class Cividis
{
    // https://github.com/marcosci/cividis
    static readonly Color32[] cividis = {
        new(255, 233, 69, 255),
        new(245, 221, 77, 255),
        new(230, 208, 89, 255),
        new(216, 197, 97, 255),
        new(202, 185, 105, 255),
        new(188, 174, 110, 255),
        new(174, 163, 115, 255),
        new(161, 152, 118, 255),
        new(149, 143, 120, 255),
        new(136, 133, 120, 255),
        new(123, 123, 120, 255),
        new(112, 113, 115, 255),
        new(101, 103, 111, 255),
        new(90, 95, 109, 255),
        new(77, 85, 107, 255),
        new(64, 76, 107, 255),
        new(47, 67, 107, 255),
        new(23, 57, 109, 255),
        new(0, 49, 111, 255),
        new(0, 40, 97, 255),
        new(0, 32, 76, 255)
    };
        
    public static Color CividisColor(float prct)
    {
        return cividis[Mathf.RoundToInt(Mathf.Min(prct, 1) * (cividis.Length - 1))];
    }
}