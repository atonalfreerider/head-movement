using HmSplats;
using UnityEditor;

/// <summary>
/// Keeps the run-time Gaussians URP feature (SplatUrpFeature) out of the project's renderer asset on disk: it is taken
/// out when Play mode ends, before a script reload and before any asset is saved (it is put back by the next
/// SplatDancers.SetEnabled(true)). Assets/Bloom_Renderer.asset is shared with the Quest build and must not change.
/// </summary>
[InitializeOnLoad]
public static class SplatFeatureGuard
{
    static SplatFeatureGuard()
    {
        EditorApplication.playModeStateChanged -= OnPlayMode;
        EditorApplication.playModeStateChanged += OnPlayMode;
        AssemblyReloadEvents.beforeAssemblyReload -= SplatUrpFeature.Remove;
        AssemblyReloadEvents.beforeAssemblyReload += SplatUrpFeature.Remove;
    }

    static void OnPlayMode(PlayModeStateChange change)
    {
        if (change is PlayModeStateChange.ExitingPlayMode or PlayModeStateChange.EnteredEditMode) SplatUrpFeature.Remove();
    }

    sealed class SaveHook : AssetModificationProcessor
    {
        static string[] OnWillSaveAssets(string[] paths)
        {
            if (SplatUrpFeature.Injected)
            {
                SplatUrpFeature.Remove();
                if (EditorApplication.isPlaying) EditorApplication.delayCall += () => { if (SplatDancers.Enabled) SplatUrpFeature.Ensure(); };
            }

            return paths;
        }
    }
}
