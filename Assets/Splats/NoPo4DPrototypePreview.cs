using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Standalone inspection of experimental splats, without invented skeletons.</summary>
public class NoPo4DPrototypePreview : MonoBehaviour
{
    public string DataDirectory;
    public Camera ViewCamera;
    public string PreviewTitle = "Carlos / Aline";
    public string ComparisonVideo;
    public string MosaicVideo;
    public bool AutoPlay;
    UnityEngine.Video.VideoPlayer video;
    RenderTexture videoTexture;
    bool showVideo;
    SplatCloud cloud;
    PreviewInfo info;
    int frame;
    bool playing;
    float time;
    Vector3 pivot;
    [Serializable] public class PreviewInfo
    {
        public int frame_count;
        public float fps;
        public float[] camera_position;
        public float[] camera_rotation;
        public float[] pivot;
        public float vertical_fov;
    }

    IEnumerator Start()
    {
        if (string.IsNullOrEmpty(DataDirectory))
            DataDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../prototype/nopo4d_full"));
        string path = Path.Combine(DataDirectory, "preview.json");
        if (!File.Exists(path)) { Debug.LogError("Missing NoPo4D preview.json: " + path); yield break; }
        info = JsonUtility.FromJson<PreviewInfo>(File.ReadAllText(path));
        ViewCamera.transform.SetPositionAndRotation(V(info.camera_position),
            new Quaternion(info.camera_rotation[0], info.camera_rotation[1], info.camera_rotation[2], info.camera_rotation[3]));
        ViewCamera.fieldOfView = info.vertical_fov;
        ViewCamera.nearClipPlane = .001f;
        ViewCamera.farClipPlane = 100f;
        pivot = V(info.pivot);
        cloud = gameObject.AddComponent<SplatCloud>();
        cloud.MaxFrameSplats = 120000;
        yield return cloud.Load(new CaptureManifest {
            Folder = DataDirectory, fps = info.fps, frame_count = info.frame_count,
            splat_sequence = new CaptureManifest.SplatSequenceRef {
                dir = "splats", fps = info.fps,
                scene_to_unity = new float[] {1,0,0,0, 0,-1,0,0, 0,0,1,0, 0,0,0,1}
            }
        });
        cloud.SetFrame(0, info.fps);
        playing = AutoPlay;
        if (!string.IsNullOrEmpty(ComparisonVideo))
        {
            videoTexture = new RenderTexture(672,588,0);
            videoTexture.Create();
            video = gameObject.AddComponent<UnityEngine.Video.VideoPlayer>();
            video.playOnAwake = false;
            video.isLooping = true;
            video.audioOutputMode = UnityEngine.Video.VideoAudioOutputMode.None;
            video.renderMode = UnityEngine.Video.VideoRenderMode.RenderTexture;
            video.targetTexture = videoTexture;
        }
    }

    static Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
    void Update()
    {
        if (info == null || cloud == null) return;
        var keys = Keyboard.current;
        if (keys == null) return;
        if (keys.spaceKey.wasPressedThisFrame) playing = !playing;
        if (playing) { time = (time + Time.deltaTime) % (info.frame_count / info.fps); frame = Mathf.FloorToInt(time * info.fps); }
        if (keys.rightArrowKey.wasPressedThisFrame) { playing = false; frame = Mathf.Min(frame + 1, info.frame_count - 1); }
        if (keys.leftArrowKey.wasPressedThisFrame) { playing = false; frame = Mathf.Max(frame - 1, 0); }
        if (!playing) time = frame / info.fps;
        float orbit = (keys.dKey.isPressed ? 1 : 0) - (keys.aKey.isPressed ? 1 : 0);
        ViewCamera.transform.RotateAround(pivot, Vector3.up, orbit * 25 * Time.deltaTime);
        float move = (keys.wKey.isPressed ? 1 : 0) - (keys.sKey.isPressed ? 1 : 0);
        ViewCamera.transform.position += ViewCamera.transform.forward * move * Vector3.Distance(ViewCamera.transform.position,pivot) * Time.deltaTime * .3f;
        cloud.SetFrame(frame, info.fps);
    }

    void OnGUI()
    {
        if (showVideo && videoTexture != null)
            GUI.DrawTexture(new Rect(0,145,Screen.width,Mathf.Max(1,Screen.height-145)),videoTexture,ScaleMode.ScaleToFit);
        GUI.Box(new Rect(12,12,620,128), "NoPo4D • " + PreviewTitle + " • experimental reconstruction");
        if (info == null) { GUI.Label(new Rect(22,40,600,25), "Loading splat sequence..."); return; }
        GUI.Label(new Rect(22,38,600,22), "Unvalidated geometry / arbitrary scale. DC-only splat preview. No fitted skeletons.");
        GUI.Label(new Rect(22,61,600,22), $"Frame {frame + 1}/{info.frame_count}   Space: play   ← →: step   A/D: orbit   W/S: zoom");
        int selected = Mathf.RoundToInt(GUI.HorizontalSlider(new Rect(22,91,590,20),frame,0,info.frame_count-1));
        if (selected != frame) { playing = false; frame = selected; }
        if (video != null)
        {
            if (GUI.Button(new Rect(22,112,150,22),"3D splats")) { showVideo=false; video.Stop(); ViewCamera.cullingMask=-1; }
            if (GUI.Button(new Rect(180,112,205,22),"Original (left) / novel (right)")) PlayVideo(ComparisonVideo);
            if (GUI.Button(new Rect(393,112,210,22),"Eight synchronized cameras")) PlayVideo(MosaicVideo);
        }
    }

    void PlayVideo(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        showVideo=true; ViewCamera.cullingMask=0;
        video.Stop(); video.url=path; video.Play();
    }

    void OnDestroy()
    {
        if (video != null) video.Stop();
        if (videoTexture != null) { videoTexture.Release(); Destroy(videoTexture); }
    }
}
