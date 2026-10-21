using UnityEngine;

namespace HmSplats
{
    /// <summary>
    /// The shaders the splat dancers need in a player build (the Quest APK; a Windows player too). The renderer package
    /// (net.kleinbeck.gaussians) and our own code find them through the AssetDatabase, which exists in the Editor only; a
    /// player gets them from this asset, <c>Assets/Splats/Avatar/Resources/HmSplatShaders.asset</c> (a Resources asset, so
    /// the build includes the shaders and compiles them for the target: Vulkan on the Quest). Holds no likeness data.
    /// </summary>
    public sealed class SplatShaderSet : ScriptableObject
    {
        public const string ResourceName = "HmSplatShaders";
        public const string AssetPath = "Assets/Splats/Avatar/Resources/HmSplatShaders.asset";
        const string Package = "Packages/net.kleinbeck.gaussians/Shaders/3DGS/";

        [Header("net.kleinbeck.gaussians 0.4.0 (MIT)")]
        public Shader splats;          // RenderGaussianSplats.shader
        public Shader composite;       // GaussianComposite.shader
        public Shader debugPoints;     // GaussianDebugRenderPoints.shader (the renderer refuses to start without it)
        public Shader debugBoxes;      // GaussianDebugRenderBoxes.shader (same)
        public ComputeShader utilities; // SplatUtilities.compute (view data, radix sort, group keys)

        [Header("HmSplats")]
        public ComputeShader skin;      // HM_SkinSplats.compute (LBS + hair binding)
        public Shader fadeComposite;    // HM_SplatComposite.shader (the package composite + _HmSplatOpacity)

        static SplatShaderSet cached;

        /// <summary>the shader set: the Resources asset; in the Editor, missing entries are filled from the AssetDatabase
        /// (so the Editor works before the asset exists)</summary>
        public static SplatShaderSet Get()
        {
            if (cached != null) return cached;
            cached = Resources.Load<SplatShaderSet>(ResourceName);
#if UNITY_EDITOR
            if (cached == null)
            {
                cached = CreateInstance<SplatShaderSet>();
                cached.hideFlags = HideFlags.DontSave;
            }

            cached.FillFromAssetDatabase();
#endif
            return cached;
        }

        /// <summary>every reference present (the renderer needs all five package shaders)</summary>
        public bool Complete => splats != null && composite != null && debugPoints != null && debugBoxes != null && utilities != null && skin != null;

        public string Missing()
        {
            System.Text.StringBuilder sb = new();
            if (splats == null) sb.Append(" splats");
            if (composite == null) sb.Append(" composite");
            if (debugPoints == null) sb.Append(" debugPoints");
            if (debugBoxes == null) sb.Append(" debugBoxes");
            if (utilities == null) sb.Append(" utilities");
            if (skin == null) sb.Append(" skin");
            if (fadeComposite == null) sb.Append(" fadeComposite");
            return sb.Length == 0 ? "none" : sb.ToString().Trim();
        }

#if UNITY_EDITOR
        public void FillFromAssetDatabase()
        {
            if (splats == null) splats = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(Package + "RenderGaussianSplats.shader");
            if (composite == null) composite = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(Package + "GaussianComposite.shader");
            if (debugPoints == null) debugPoints = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(Package + "GaussianDebugRenderPoints.shader");
            if (debugBoxes == null) debugBoxes = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(Package + "GaussianDebugRenderBoxes.shader");
            if (utilities == null) utilities = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(Package + "SplatUtilities.compute");
            if (skin == null) skin = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(SkinnedSplatAvatar.ComputePath);
            if (fadeComposite == null) fadeComposite = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/Splats/Avatar/Core/Shaders/HM_SplatComposite.shader");
        }

        /// <summary>write (or refresh) the Resources asset from the AssetDatabase; returns what it holds</summary>
        public static string WriteAsset()
        {
            SplatShaderSet s = UnityEditor.AssetDatabase.LoadAssetAtPath<SplatShaderSet>(AssetPath);
            bool create = s == null;
            if (create)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(AssetPath)!);
                s = CreateInstance<SplatShaderSet>();
            }

            s.splats = s.composite = s.debugPoints = s.debugBoxes = s.fadeComposite = null;
            s.utilities = s.skin = null;
            s.FillFromAssetDatabase();
            if (create) UnityEditor.AssetDatabase.CreateAsset(s, AssetPath);
            else UnityEditor.EditorUtility.SetDirty(s);
            UnityEditor.AssetDatabase.SaveAssetIfDirty(s);
            cached = null;
            return $"{AssetPath}: missing {s.Missing()}";
        }
#endif
    }
}
