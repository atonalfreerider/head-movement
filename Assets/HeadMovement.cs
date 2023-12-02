using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

public class HeadMovement : MonoBehaviour
{
    static readonly string assetPath = Application.streamingAssetsPath;

    int FRAME_MAX = -1;

    Dancer Lead;
    Dancer Follow;

    void Awake()
    {
        Lead = ReadAllPosesFrom(Path.Combine(assetPath, "figure1.json"), "lead");
        Follow = ReadAllPosesFrom(Path.Combine(assetPath, "figure2.json"), "follow");
    }

    void Start()
    {
        StartCoroutine(Iterate(0));
    }

    IEnumerator Iterate(int frameNumber)
    {
        if (frameNumber >= FRAME_MAX)
        {
            frameNumber = 0;
        }

        Lead.SetPoseToFrame(frameNumber);
        Follow.SetPoseToFrame(frameNumber);

        yield return new WaitForSeconds(.03f);

        frameNumber++;
        StartCoroutine(Iterate(frameNumber));
    }

    Dancer ReadAllPosesFrom(string jsonPath, string role)
    {
        Dancer dancer = new GameObject(role).AddComponent<Dancer>();
        dancer.Role = role == "lead" ? Role.Lead : Role.Follow;

        string jsonString = File.ReadAllText(jsonPath);
        List<List<Float3>> allPoses = JsonConvert.DeserializeObject<List<List<Float3>>>(jsonString);
        List<List<Vector3>> allPosesVector3 = allPoses
            .Select(pose => pose.Select(float3 => new Vector3(float3.x, float3.y, float3.z)).ToList()).ToList();
        dancer.PosesByFrame = allPosesVector3;

        FRAME_MAX = allPosesVector3.Count;

        return dancer;
    }

    [Serializable]
    class Float3
    {
        public float x;
        public float y;
        public float z;
    }
}