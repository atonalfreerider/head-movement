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

    int FRAME_MAX = -1;
    float totalSeconds = 0;

    Dancer Lead;
    Dancer Follow;

    ContactDetection contactDetection;

    Coroutine animator;
    public Material BloomMaterial;
    
    CameraControl cameraControl;
    
    AudioSource audioSource;
    bool audioLoaded = false;
    string currentFolder = "";

    void Awake()
    {
        Instance = this;
        const PoseType poseType = PoseType.Smpl;

        string[] captures = Directory.GetDirectories(assetPath);
        currentFolder = captures[0];
        
        Lead = ReadAllPosesFrom(Path.Combine(currentFolder, "figure1.json"), Role.Lead, poseType);
        Follow = ReadAllPosesFrom(Path.Combine(currentFolder, "figure2.json"), Role.Follow, poseType);

        contactDetection = GetComponent<ContactDetection>();
        contactDetection.Init(Lead, Follow, BloomMaterial);
    }

    void Start()
    {
        cameraControl = GameObject.Find("Simulator").GetComponent<CameraControl>();
        StartCoroutine(LoadAudio());
    }
    
    IEnumerator LoadAudio()
    {
        // Build the full path to the WAV file in StreamingAssets
        string filePath = Path.Combine(currentFolder, "audio.wav");

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
            totalSeconds = clip.length;
            audioLoaded = true;
        }
    }

    IEnumerator Iterate()
    {
        SetToFrameNumber();

        yield return null;

        Resume();
    }

    Dancer ReadAllPosesFrom(string jsonPath, Role role, PoseType poseType)
    {
        Dancer dancer = new GameObject(role.ToString()).AddComponent<Dancer>();

        string jsonString = File.ReadAllText(jsonPath);
        List<List<Float3>> allPoses = JsonConvert.DeserializeObject<List<List<Float3>>>(jsonString);
        List<List<Vector3>> allPosesVector3 = allPoses
            .Select(pose => pose.Select(float3 => new Vector3(float3.x, float3.y, float3.z)).ToList()).ToList();
        dancer.Init(role, allPosesVector3, poseType, BloomMaterial);

        FRAME_MAX = allPosesVector3.Count;

        return dancer;
    }

    void SetToFrameNumber()
    {
        int frameNumber = GetFrameNumber();
        Lead.SetPoseToFrame(frameNumber);
        Follow.SetPoseToFrame(frameNumber);

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
        else if (Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            TogglePlayPause();
        }

        if (audioLoaded && cameraControl.gameObject.activeInHierarchy)
        {
            int frameNumber = GetFrameNumber();
            Vector3 center = Vector3.Lerp(Lead.Center(frameNumber), Follow.Center(frameNumber), .5f);
            center = new Vector3(center.x, 0, center.z);
            cameraControl.Center = center;
        }
    }
    
    int GetFrameNumber()
    {
        return (int) Mathf.Round((audioSource.time / totalSeconds) * FRAME_MAX);
    }

    [Serializable]
    class Float3
    {
        public float x;
        public float y;
        public float z;
    }
}