using System.IO;
using UnityEngine;

/// <summary>
/// Where the viewer reads its data (the captures, the film library, the films' emoji): StreamingAssets in the editor and in a desktop build;
/// on Android (Quest standalone) StreamingAssets is packed inside the APK and cannot be read with file calls, so the data lives in the app's
/// own storage folder, pushed with adb (docs/QUEST.md): Android/data/&lt;package&gt;/files/hm_data. Environment HM_DATA overrides it anywhere.
/// </summary>
public static class HmData
{
    public static string Root
    {
        get
        {
            string env = System.Environment.GetEnvironmentVariable("HM_DATA");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
#if UNITY_ANDROID && !UNITY_EDITOR
            return Path.Combine(Application.persistentDataPath, "hm_data");
#else
            return Application.streamingAssetsPath;
#endif
        }
    }
}
