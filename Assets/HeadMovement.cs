using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.InputSystem;
using VRTKLite.Controllers;

[RequireComponent(typeof(ContactDetection))]
public class HeadMovement : MonoBehaviour
{
    static readonly string assetPath = Application.streamingAssetsPath;

    public static HeadMovement Instance;

    int FRAME_MAX = -1;

    Dancer Lead;
    Dancer Follow;

    ContactDetection contactDetection;

    float delayBetweenFrames = .03f;
    int frameNumber = 0;

    Coroutine animator;
    public Material BloomMaterial;
    
    CameraControl cameraControl;

    void Awake()
    {
        Instance = this;
        const PoseType poseType = PoseType.Smpl;
        Lead = ReadAllPosesFrom(Path.Combine(assetPath, "figure1.json"), Role.Lead, poseType);
        Follow = ReadAllPosesFrom(Path.Combine(assetPath, "figure2.json"), Role.Follow, poseType);

        contactDetection = GetComponent<ContactDetection>();
        contactDetection.Init(Lead, Follow, poseType);
    }

    void Start()
    {
        cameraControl = GameObject.Find("Simulator").GetComponent<CameraControl>();
        Resume();
    }

    IEnumerator Iterate()
    {
        if (frameNumber >= FRAME_MAX)
        {
            frameNumber = 0;
        }

        SetToFrameNumber();

        yield return new WaitForSeconds(delayBetweenFrames);

        frameNumber++;

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
        Lead.SetPoseToFrame(frameNumber);
        Follow.SetPoseToFrame(frameNumber);

        contactDetection.DetectContact(frameNumber);
    }

    public void SlowDown()
    {
        if (animator != null)
        {
            // play mode
            delayBetweenFrames += .01f;
        }
        else
        {
            // pause mode. single iteration
            frameNumber--;
            if (frameNumber < 0)
            {
                frameNumber = 0;
            }

            SetToFrameNumber();
        }
    }

    public void SpeedUp()
    {
        if (animator != null)
        {
            // play mode
            delayBetweenFrames -= .01f;
            if (delayBetweenFrames < .01f)
            {
                delayBetweenFrames = .01f;
            }
        }
        else
        {
            // pause mode. single iteration
            frameNumber++;
            if (frameNumber >= FRAME_MAX)
            {
                frameNumber = 0;
            }
            
            SetToFrameNumber();
        }
    }

    public void TogglePlayPause()
    {
        if (animator == null)
        {
            Resume();
        }
        else
        {
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

        if (cameraControl.gameObject.activeInHierarchy)
        {
            Vector3 center = Vector3.Lerp(Lead.Center(frameNumber), Follow.Center(frameNumber), .5f);
            center = new Vector3(center.x, 0, center.z);
            cameraControl.Center = center;
        }
    }

    [Serializable]
    class Float3
    {
        public float x;
        public float y;
        public float z;
    }
}