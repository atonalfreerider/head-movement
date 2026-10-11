using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace HmVr.Editor
{
    /// <summary>
    /// The app's own look in the headset's library, all generated (no third-party art): an adaptive icon - a dark background, a teal ring
    /// and a yellow T on the floor with two dancers in a closed hold - and the player splash settings (the Unity logo stays only where the
    /// licence requires it: the editor decides, this only asks for the custom splash to be off). Called by hm_vr_setup.
    /// </summary>
    public static class VrBranding
    {
        const string Folder = "Assets/Icons";

        public static void Apply(StringBuilder log)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                Texture2D bg = Make("hm_icon_background", 432, false);
                Texture2D fg = Make("hm_icon_foreground", 432, true);
                Texture2D full = Make("hm_icon_square", 512, null);
                NamedBuildTarget android = NamedBuildTarget.Android;
                foreach ((PlatformIconKind kind, Texture2D[] layers) in new[]
                {
                    (UnityEditor.Android.AndroidPlatformIconKind.Adaptive, new[] { bg, fg })
                })
                {
                    PlatformIcon[] icons = PlayerSettings.GetPlatformIcons(android, kind);
                    foreach (PlatformIcon icon in icons) icon.SetTextures(layers);
                    PlayerSettings.SetPlatformIcons(android, kind, icons);
                }

                log.AppendLine("Android icon: generated adaptive icon (Assets/Icons)");
            }
            catch (Exception e)
            {
                log.AppendLine("Android icon not set: " + e.Message);
            }

            try
            {
                PlayerSettings.SplashScreen.show = false;
                PlayerSettings.SplashScreen.showUnityLogo = false;
                log.AppendLine($"Player splash: show={PlayerSettings.SplashScreen.show}, Unity logo={PlayerSettings.SplashScreen.showUnityLogo} (as the licence allows)");
            }
            catch (Exception e)
            {
                log.AppendLine("Player splash settings not applied: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ drawing

        static float Sdf(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 pa = p - a, ba = b - a;
            float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
            return (pa - ba * h).magnitude;
        }

        static Color Over(Color under, Color c, float cover)
        {
            cover = Mathf.Clamp01(cover) * c.a;
            return new Color(Mathf.Lerp(under.r, c.r, cover), Mathf.Lerp(under.g, c.g, cover), Mathf.Lerp(under.b, c.b, cover), Mathf.Max(under.a, cover));
        }

        /// <summary>background only (false), foreground only on transparent (true), or both (null)</summary>
        static Texture2D Make(string name, int size, bool? foregroundOnly)
        {
            Texture2D t = new(size, size, TextureFormat.RGBA32, false, false);
            Color teal = new(0.10f, 0.76f, 0.84f, 1f), yellow = new(1f, 0.839f, 0.039f, 1f), white = new(0.96f, 0.97f, 1f, 1f);
            Color[] px = new Color[size * size];
            float aa = 1.5f / size;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 p = new((x + 0.5f) / size - 0.5f, (y + 0.5f) / size - 0.5f); // -0.5..0.5, y up
                    Color c = foregroundOnly == true ? new Color(0, 0, 0, 0) : Color.Lerp(new Color(0.03f, 0.04f, 0.07f), new Color(0.09f, 0.10f, 0.15f), y / (float)size);
                    if (foregroundOnly != false)
                    {
                        // the adaptive icon keeps the middle two thirds visible: everything inside 0.33 of the centre
                        float ring = Mathf.Abs(p.magnitude - 0.27f);
                        c = Over(c, teal, 1f - Mathf.SmoothStep(0.012f, 0.012f + aa * 2f, ring));
                        // the T on the floor: a bar and a stem under the couple
                        float bar = Sdf(p, new Vector2(-0.12f, -0.17f), new Vector2(0.12f, -0.17f)), stem = Sdf(p, new Vector2(0f, -0.17f), new Vector2(0f, -0.27f));
                        c = Over(c, yellow, 1f - Mathf.SmoothStep(0.012f, 0.012f + aa * 2f, Mathf.Min(bar, stem)));
                        // two dancers leaning together: heads, spines and the joined arms
                        for (int d = -1; d <= 1; d += 2)
                        {
                            Vector2 hip = new(d * 0.075f, -0.07f), neck = hip + new Vector2(-d * 0.02f, 0.13f), head = neck + new Vector2(-d * 0.008f, 0.045f);
                            float body = Mathf.Min(Sdf(p, hip, neck), Mathf.Min(Sdf(p, hip, hip + new Vector2(-d * 0.03f, -0.1f)), Sdf(p, hip, hip + new Vector2(d * 0.035f, -0.1f))));
                            c = Over(c, white, 1f - Mathf.SmoothStep(0.011f, 0.011f + aa * 2f, body));
                            c = Over(c, white, 1f - Mathf.SmoothStep(0.034f, 0.034f + aa * 2f, (p - head).magnitude));
                        }

                        float hold = Sdf(p, new Vector2(-0.07f, 0.045f), new Vector2(0.07f, 0.045f));
                        c = Over(c, white, 1f - Mathf.SmoothStep(0.010f, 0.010f + aa * 2f, hold));
                    }

                    px[y * size + x] = c;
                }
            }

            t.SetPixels(px);
            t.Apply();
            string path = $"{Folder}/{name}.png";
            File.WriteAllBytes(path, t.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            TextureImporter imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp != null)
            {
                imp.textureType = TextureImporterType.Default;
                imp.isReadable = true;
                imp.mipmapEnabled = false;
                imp.textureCompression = TextureImporterCompression.Uncompressed;
                imp.alphaIsTransparency = true;
                imp.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
