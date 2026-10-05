using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class LarissaKaduPreviewScene
{
    [MenuItem("Head Movement/Open Larissa Kadu NoPo4D Test")]
    public static void Open()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new System.InvalidOperationException("Exit Play mode before opening the preview.");
        // Preserve unsaved scene work as a separate copy before switching scenes.
        if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets","Scenes");
        for (int i=0;i<SceneManager.sceneCount;i++)
        {
            var current=SceneManager.GetSceneAt(i);
            if (current.isDirty)
            {
                string backup=AssetDatabase.GenerateUniqueAssetPath("Assets/Scenes/BeforeLarissaPreview.unity");
                if (!EditorSceneManager.SaveScene(current,backup,true))
                    throw new IOException("Could not preserve unsaved scene: " + current.name);
                Debug.Log("Preserved unsaved scene copy: " + backup);
            }
        }
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        var cam=new GameObject("Larissa Kadu Preview Camera").AddComponent<Camera>();
        cam.tag="MainCamera"; cam.clearFlags=CameraClearFlags.SolidColor;
        cam.backgroundColor=new Color(.06f,.065f,.08f);
        var viewer=new GameObject("Larissa Kadu NoPo4D Test").AddComponent<NoPo4DPrototypePreview>();
        string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../../prototype/larissa_kadu"));
        viewer.DataDirectory=Path.Combine(root,"unity_preview");
        viewer.ViewCamera=cam;
        viewer.PreviewTitle="Larissa / Kadu | John 00:58-01:02";
        viewer.ComparisonVideo=Path.Combine(root,"nopo4d_four_second/original_vs_novel.mp4");
        viewer.MosaicVideo=Path.Combine(root,"synchronized_mosaic.mp4");
        viewer.AutoPlay=true;
        EditorSceneManager.SaveScene(scene,"Assets/Scenes/LarissaKaduNoPo4D.unity");
        EditorWindow.GetWindow(System.Type.GetType("UnityEditor.GameView,UnityEditor")).Show();
        Debug.Log("Larissa Kadu NoPo4D preview ready");
    }
}
