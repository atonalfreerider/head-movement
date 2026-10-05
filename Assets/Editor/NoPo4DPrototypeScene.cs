using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using System.IO;
using UnityEngine.Rendering;

public static class NoPo4DPrototypeScene
{
    public static void Verify()
    {
        Create();
        string dir = Path.GetFullPath(Path.Combine(Application.dataPath,"../../prototype/nopo4d_full"));
        var info = JsonUtility.FromJson<NoPo4DPrototypePreview.PreviewInfo>(File.ReadAllText(Path.Combine(dir,"preview.json")));
        Matrix4x4 flip = Matrix4x4.Scale(new Vector3(1,-1,1));
        foreach (int i in new[] {0,67,133})
        {
            var check = SplatPly.Load(Path.Combine(dir,$"splats/frame_{i:D5}.ply"),flip,120000);
            if (check.Positions.Length == 0) throw new System.Exception("Empty PLY frame " + i);
            foreach (Vector3 point in check.Positions)
                if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z))
                    throw new System.Exception("Nonfinite PLY point");
            Debug.Log($"NoPo4D PLY check frame {i}: {check.Positions.Length} splats");
        }
        var data = SplatPly.Load(Path.Combine(dir,"splats/frame_00000.ply"),flip,120000);
        var cloud = new GameObject("Verification Frame");
        cloud.AddComponent<MeshFilter>().sharedMesh = SplatCloud.BuildMesh(data);
        cloud.AddComponent<MeshRenderer>().sharedMaterial = new Material(Resources.Load<Shader>("SplatPreview"));
        Camera cam = Camera.main;
        cam.transform.SetPositionAndRotation(new Vector3(info.camera_position[0],info.camera_position[1],info.camera_position[2]),
            new Quaternion(info.camera_rotation[0],info.camera_rotation[1],info.camera_rotation[2],info.camera_rotation[3]));
        cam.nearClipPlane=.001f;
        cam.farClipPlane=100f;
        cam.fieldOfView=info.vertical_fov;
        var target = new RenderTexture(896,504,24);
        target.Create();
        RenderPipeline.SubmitRenderRequest(cam,new RenderPipeline.StandardRequest {destination=target});
        RenderTexture.active=target;
        var texture=new Texture2D(896,504,TextureFormat.RGB24,false);
        texture.ReadPixels(new Rect(0,0,896,504),0,0); texture.Apply();
        File.WriteAllBytes(Path.Combine(dir,"unity_preview.png"),texture.EncodeToPNG());
        RenderTexture.active=null; target.Release();
        Object.DestroyImmediate(texture); Object.DestroyImmediate(target); Object.DestroyImmediate(cloud);
        Debug.Log("NoPo4D verification complete");
    }

    [MenuItem("Head Movement/Open NoPo4D Prototype")]
    public static void Create()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var camera = new GameObject("Prototype Camera").AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.12f,.12f,.14f);
        camera.tag = "MainCamera";
        var viewer = new GameObject("NoPo4D Prototype").AddComponent<NoPo4DPrototypePreview>();
        viewer.ViewCamera = camera;
        if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets","Scenes");
        EditorSceneManager.SaveScene(scene, "Assets/Scenes/NoPo4DPrototype.unity");
        Debug.Log("NoPo4D prototype scene created successfully");
    }
}
