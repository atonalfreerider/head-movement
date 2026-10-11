using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// A texture the deploy step has already prepared for the headset (Tools/deploy_data.ps1 -Bake, editor command hm_vr_bake_textures): the PNG
/// next to which <c>name.png.hmtex</c> (the GPU-compressed mip chain, raw) and <c>name.png.hmtex.json</c> (size, format, and for an avatar's
/// albedo the luminance bins of its transparency model) lie. Loading it is a file read and an upload: no PNG decode, no CPU compression,
/// no sampling of 20 000 triangles - together a few hundred milliseconds per avatar that would freeze a headset. Used only where the
/// device can sample the baked format (the Quest); everywhere else, and whenever the sidecar is missing, the PNG is loaded as before.
/// </summary>
public static class BakedTexture
{
    public static string RawPath(string png) => png + ".hmtex";
    public static string MetaPath(string png) => png + ".hmtex.json";

    public static bool TryLoad(string pngPath, string name, out Texture2D tex, out DisplayTransparency display)
    {
        tex = null;
        display = null;
        try
        {
            string raw = RawPath(pngPath), meta = MetaPath(pngPath);
            if (!File.Exists(raw) || !File.Exists(meta)) return false;
            JObject j = JObject.Parse(File.ReadAllText(meta));
            if (!Enum.TryParse(j.Value<string>("format"), out TextureFormat fmt) || !SystemInfo.SupportsTextureFormat(fmt)) return false;
            if (j["transparency"] is JObject t)
            {
                float[] ys = t["ys"].ToObject<float[]>(), ws = t["ws"].ToObject<float[]>();
                display = DisplayTransparency.FromSamples(ys, ws, t.Value<int>("tris"));
            }

            byte[] bytes = File.ReadAllBytes(raw);
            int w = j.Value<int>("width"), h = j.Value<int>("height"), mips = j.Value<int>("mips");
            tex = new Texture2D(w, h, fmt, mips, false) { name = name + " (baked)" };
            tex.LoadRawTextureData(bytes);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 2;
            tex.Apply(false, true);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"BakedTexture: {pngPath}: {e.Message}; loading the PNG instead");
            if (tex != null) UnityEngine.Object.Destroy(tex);
            tex = null;
            display = null;
            return false;
        }
    }
}
