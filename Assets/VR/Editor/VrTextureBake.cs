using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

namespace HmVr.Editor
{
    /// <summary>
    /// Prepares the textures of a deployed data folder for the headset (see BakedTexture): every PNG of a capture folder (the avatars' albedo,
    /// the room and floor photos) gets a GPU-compressed (ASTC 6x6) mip chain and a small JSON next to it, and an avatar's albedo also gets the
    /// luminance bins of its transparency model, measured on its skin. The PNG stays: a device or a viewer that cannot sample the format
    /// loads it as before.
    ///   unity command hm_vr_bake_textures --folder Builds/data
    /// Tools/deploy_data.ps1 -Bake runs it on the folder it lays out.
    /// </summary>
    public static class VrTextureBake
    {
        [CliCommand("hm_vr_bake_textures", "Bake the headset textures of a deployed data folder (Builds/data): ASTC 6x6 mips + the avatars' transparency model next to every PNG")]
        public static string Cli([CliArg("folder", "the deployed data folder under Builds/")] string folder = "Builds/data") => Bake(folder);

        public static string Bake(string folder)
        {
            string root = Directory.GetParent(Application.dataPath)!.FullName;
            string full = Path.GetFullPath(Path.Combine(root, folder));
            if (!full.StartsWith(Path.Combine(root, "Builds"), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("the folder must be under Builds/");
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
            int count = 0;
            long bytes = 0;
            foreach (string png in Directory.GetFiles(full, "*.png", SearchOption.AllDirectories))
            {
                if (png.Contains(Path.DirectorySeparatorChar + "thumbs" + Path.DirectorySeparatorChar)) continue;
                bytes += BakeOne(png);
                count++;
            }

            return $"baked {count} texture(s), {bytes / 1048576.0:0.0} MB of GPU data, in {full}";
        }

        static long BakeOne(string png)
        {
            Texture2D t = new(2, 2, TextureFormat.RGBA32, true, false);
            try
            {
                if (!t.LoadImage(File.ReadAllBytes(png), false)) throw new InvalidDataException(png + ": not a PNG");
                JObject meta = new();
                // an avatar's albedo: <role>_albedo.png beside smplx_<role>_skin.bin - the transparency model is measured now, on the readable pixels
                string name = Path.GetFileNameWithoutExtension(png);
                if (name.EndsWith("_albedo", StringComparison.OrdinalIgnoreCase))
                {
                    string role = name.Substring(0, name.Length - "_albedo".Length);
                    string skinPath = Path.Combine(Path.GetDirectoryName(png)!, $"smplx_{role}_skin.bin");
                    if (File.Exists(skinPath))
                    {
                        SmplxData.Skin skin = SmplxData.ReadSkin(skinPath);
                        if (skin.Textured)
                        {
                            DisplayTransparency.LuminanceBins(t, skin.RestVertices, skin.Triangles, skin.Uv, Color.white, out float[] ys, out float[] ws, out int tris);
                            if (ys != null) meta["transparency"] = new JObject { ["ys"] = new JArray(ys), ["ws"] = new JArray(ws), ["tris"] = tris };
                        }
                    }
                }

                TextureFormat fmt = TextureFormat.ASTC_6x6;
                int w = t.width, h = t.height, mips = t.mipmapCount;
                EditorUtility.CompressTexture(t, fmt, TextureCompressionQuality.Normal);
                byte[] raw = t.GetRawTextureData();
                File.WriteAllBytes(BakedTexture.RawPath(png), raw);
                meta["width"] = w;
                meta["height"] = h;
                meta["mips"] = mips;
                meta["format"] = fmt.ToString();
                File.WriteAllText(BakedTexture.MetaPath(png), meta.ToString(Newtonsoft.Json.Formatting.None));
                return raw.LongLength;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(t);
            }
        }
    }
}
