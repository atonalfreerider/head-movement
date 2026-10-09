using System;
using UnityEngine;

/// <summary>
/// Per-frame hook for the editor-side film recorder (Assets/Film/Editor/FilmRecorder.cs): the Unity Recorder API is
/// editor-only, but the recording has to start, poll and stop on GAME frames (frame-locked capture), so the recorder
/// hangs its state machine on this component's Update. Runs before every other script (the director's Begin and the
/// recorder's StartRecording happen in the same frame, before the director's own Update of that frame).
/// </summary>
[DefaultExecutionOrder(-10000)]
[AddComponentMenu("")]
public class FilmRecordingDriver : MonoBehaviour
{
    public static FilmRecordingDriver Instance { get; private set; }

    public Action OnFrame;

    /// <summary>recorder self-test only (plain tests): Time.frameCount values on which the whole Game view is painted
    /// white for one frame, so the render check can locate those frames in the MP4 (video frame alignment)</summary>
    public int[] FlashFrames;

    static Texture2D white;

    public static FilmRecordingDriver Ensure()
    {
        if (Instance != null) return Instance;
        GameObject go = new("Film Recording");
        DontDestroyOnLoad(go);
        return go.AddComponent<FilmRecordingDriver>();
    }

    void Awake() => Instance = this;

    void Update()
    {
        try
        {
            OnFrame?.Invoke();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    void OnGUI()
    {
        if (FlashFrames == null || Event.current.type != EventType.Repaint || Array.IndexOf(FlashFrames, Time.frameCount) < 0) return;
        if (white == null)
        {
            white = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            white.SetPixel(0, 0, Color.white);
            white.Apply();
        }

        GUI.depth = -10000;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), white);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
