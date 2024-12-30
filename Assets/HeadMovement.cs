using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using VRTKLite.Controllers;

[RequireComponent(typeof(ContactDetection))]
public class HeadMovement : MonoBehaviour
{
    static readonly string assetPath = Application.streamingAssetsPath;

    public static HeadMovement Instance;

    int FrameCount = -1;
    float TotalSeconds = -1;

    Dancer Lead;
    Dancer Follow;

    ContactDetection contactDetection;

    Coroutine animator;
    public Material BloomMaterial;

    CameraControl cameraControl;

    AudioSource audioSource;
    bool audioLoaded = false;
    int currentFolder = -1;

    Dictionary<int, float> beatIntensityByFrame;
    List<List<float>> zoukTime;
    int currentFrame = -1;

    string[] captures;

    void Awake()
    {
        Instance = this;
        captures = Directory.GetDirectories(assetPath);
    }

    void Start()
    {
        cameraControl = GameObject.Find("Simulator").GetComponent<CameraControl>();
    }

    void LoadCapture(int selection)
    {
        if (Lead != null)
        {
            Destroy(Lead.gameObject);
        }

        if (Follow != null)
        {
            Destroy(Follow.gameObject);
        }

        currentFolder = selection;
        currentFrame = -1;
        
        VideoMetadata videoMetadata = JsonConvert.DeserializeObject<VideoMetadata>(
            File.ReadAllText(Path.Combine(captures[currentFolder], "video_meta.json")));
        FrameCount = videoMetadata.frame_count;
        TotalSeconds = videoMetadata.duration;

        Lead = ReadAllPosesFrom(Path.Combine(captures[currentFolder], "figure1.json"), Role.Lead);
        Follow = ReadAllPosesFrom(Path.Combine(captures[currentFolder], "figure2.json"), Role.Follow);

        string zoukTimeString = File.ReadAllText(Path.Combine(captures[currentFolder], "zouk-time-analysis.json"));
        zoukTime = JsonConvert.DeserializeObject<List<List<float>>>(zoukTimeString);

        contactDetection = GetComponent<ContactDetection>();
        contactDetection.Reset();
        contactDetection.Init(Lead, Follow, BloomMaterial);

        StartCoroutine(LoadAudio());
    }
    
    [Serializable]
    public class VideoMetadata
    {
        public float duration;
        public int frame_count;
    }

    IEnumerator LoadAudio()
    {
        // Build the full path to the WAV file in StreamingAssets
        string filePath = Path.Combine(captures[currentFolder], "audio.wav");

        // On most platforms, we need the "file://" prefix to read directly
        // For Android, UnityWebRequest can handle it without the prefix, but
        // using "file://" will work cross-platform (except WebGL).
        if (!filePath.Contains("://"))
        {
            filePath = "file://" + filePath;
        }

        // Use UnityWebRequestMultimedia to download the WAV file as an AudioClip
        using UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(filePath, AudioType.WAV);
        yield return www.SendWebRequest();

        // Check for errors
        if (www.result is UnityWebRequest.Result.ConnectionError or UnityWebRequest.Result.ProtocolError)
        {
            Debug.LogError($"Error loading audio: {www.error}");
        }
        else
        {
            // Get the AudioClip from the download handler
            AudioClip clip = DownloadHandlerAudioClip.GetContent(www);

            // Create an AudioSource (if one doesn't exist)
            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
            }

            // Assign the clip and play
            audioSource.clip = clip;
            audioLoaded = true;

            List<int> framesWithUpOrDownbeat = new List<int>();
            foreach (List<float> floatPair in zoukTime)
            {
                if ((int)floatPair[1] == 3) continue; // filter out high-hat

                int frameBeat = (int)Mathf.Round((floatPair[0] / TotalSeconds) * FrameCount);
                framesWithUpOrDownbeat.Add(frameBeat);
            }

            beatIntensityByFrame = new Dictionary<int, float>();
            float currentIntensity = 0;
            const int intensityFalloffPeriod = 5;
            for (int i = 0; i < FrameCount; i++)
            {
                if (framesWithUpOrDownbeat.Contains(i))
                {
                    currentIntensity = 10;
                }
                else if (currentIntensity > 0)
                {
                    currentIntensity -= 10f / intensityFalloffPeriod;
                }

                beatIntensityByFrame[i] = currentIntensity;
            }

            SetToFrameNumber();
            Debug.Log($"Loaded performance: {captures[currentFolder]}");
        }
    }

    IEnumerator Iterate()
    {
        SetToFrameNumber();

        yield return null;

        Resume();
    }

    Dancer ReadAllPosesFrom(string jsonPath, Role role)
    {
        Dancer dancer = new GameObject(role.ToString()).AddComponent<Dancer>();

        string jsonString = File.ReadAllText(jsonPath);
        List<List<Float3>> allPoses = JsonConvert.DeserializeObject<List<List<Float3>>>(jsonString);
        List<List<Vector3>> allPosesVector3 = allPoses
            .Select(pose => pose.Select(float3 => new Vector3(float3.x, float3.y, float3.z)).ToList()).ToList();
        dancer.Init(role, allPosesVector3, BloomMaterial, TotalSeconds);

        return dancer;
    }

    void SetToFrameNumber()
    {
        int frameNumber = GetFrameNumber();
        if (currentFrame == frameNumber) return;
        currentFrame = frameNumber;

        float currentBeatIntensity = beatIntensityByFrame.GetValueOrDefault(frameNumber, 0);

        Lead.SetPoseToFrame(frameNumber, currentBeatIntensity);
        Follow.SetPoseToFrame(frameNumber, currentBeatIntensity);

        contactDetection.DetectContact(frameNumber);
    }

    public void SlowDown()
    {
    }

    public void SpeedUp()
    {
    }

    public void TogglePlayPause()
    {
        if (animator == null)
        {
            audioSource.Play();
            Resume();
        }
        else
        {
            audioSource.Pause();
            Pause();
        }
    }

    void Pause()
    {
        if (animator != null)
        {
            StopCoroutine(animator);
            animator = null;
        }
    }

    void Resume()
    {
        animator = StartCoroutine(Iterate());
    }

    void Update()
    {
        if (Keyboard.current.rightArrowKey.wasPressedThisFrame)
        {
            SpeedUp();
        }
        else if (Keyboard.current.leftArrowKey.wasPressedThisFrame)
        {
            SlowDown();
        }
        else if (audioLoaded && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            TogglePlayPause();
        }

        if (audioLoaded && cameraControl.gameObject.activeInHierarchy)
        {
            int frameNumber = GetFrameNumber();
            Vector3 center = Vector3.Lerp(Lead.Center(frameNumber), Follow.Center(frameNumber), .5f);
            center = new Vector3(center.x, center.y - 0.5f, center.z);
            cameraControl.Center = center;
        }

        int selection = -1;
        if (Keyboard.current.digit1Key.wasPressedThisFrame)
        {
            selection = 0;
        }

        if (Keyboard.current.digit2Key.wasPressedThisFrame)
        {
            selection = 1;
        }

        if (Keyboard.current.digit3Key.wasPressedThisFrame)
        {
            selection = 2;
        }

        if (Keyboard.current.digit4Key.wasPressedThisFrame)
        {
            selection = 3;
        }

        if (Keyboard.current.digit5Key.wasPressedThisFrame)
        {
            selection = 4;
        }

        if (selection > -1)
        {
            LoadCapture(selection);
        }
    }

    int GetFrameNumber()
    {
        return (int)Mathf.Round((audioSource.time / TotalSeconds) * FrameCount);
    }

    [Serializable]
    class Float3
    {
        public float x;
        public float y;
        public float z;
    }
}