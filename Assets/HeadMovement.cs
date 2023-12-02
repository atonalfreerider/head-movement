using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Shapes;
using Shapes.Lines;
using UnityEngine;

public class HeadMovement : MonoBehaviour
{
    [Header("Input")] public string Figure1JsonPath, Figure2JsonPath;

    static readonly string assetPath = Application.streamingAssetsPath;

    int FRAME_MAX = -1;
    int MAX_DANCERS = -1;

    enum Joints
    {
        Nose = 0,
        L_Eye = 1,
        R_Eye = 2,
        L_Ear = 3,
        R_Ear = 4,
        L_Shoulder = 5,
        R_Shoulder = 6,
        L_Elbow = 7,
        R_Elbow = 8,
        L_Wrist = 9,
        R_Wrist = 10,
        L_Hip = 11,
        R_Hip = 12,
        L_Knee = 13,
        R_Knee = 14,
        L_Ankle = 15,
        R_Ankle = 16
    }

    Dancer Lead;
    Dancer Follow;
    readonly List<StaticLink> AllLinks = new();

    void Start()
    {
        Lead = ReadAllPosesFrom(Figure1JsonPath, "lead");
        Follow = ReadAllPosesFrom(Figure2JsonPath, "follow");

        // initiate dance skeletons
        for (int i = 0; i < 2; i++)
        {
            Dancer dancer = i == 0 ? Lead : Follow;

            for (int j = 0; j < 17; j++)
            {
                Polygon tetra = Instantiate(PolygonFactory.Instance.icosahedron0);
                tetra.gameObject.SetActive(true);
                tetra.transform.SetParent(transform, false);
                tetra.transform.localScale = Vector3.one * .02f;
                dancer.Joints.Add(tetra);
            }
            
            AllLinks.Add(LinkFromTo((int)Joints.Nose, (int)Joints.L_Eye, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.Nose, (int)Joints.R_Eye, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Eye, (int)Joints.R_Eye, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Eye, (int)Joints.L_Ear, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Ear, (int)Joints.L_Shoulder, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Eye, (int)Joints.R_Ear, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Ear, (int)Joints.R_Shoulder, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Hip, (int)Joints.R_Knee, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Knee, (int)Joints.R_Ankle, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Hip, (int)Joints.L_Knee, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Knee, (int)Joints.L_Ankle, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Shoulder, (int)Joints.R_Elbow, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Elbow, (int)Joints.R_Wrist, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Shoulder, (int)Joints.L_Elbow, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Elbow, (int)Joints.L_Wrist, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Shoulder, (int)Joints.L_Shoulder, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Hip, (int)Joints.L_Hip, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.R_Shoulder, (int)Joints.R_Hip, dancer));
            AllLinks.Add(LinkFromTo((int)Joints.L_Shoulder, (int)Joints.L_Hip, dancer));
        }

        StartCoroutine(Iterate(0));
    }

    StaticLink LinkFromTo(int index1, int index2, Dancer dancer)
    {
        StaticLink staticLink = Instantiate(StaticLink.prototypeStaticLink);
        staticLink.gameObject.SetActive(true);
        staticLink.SetColor(dancer.Role == Role.Lead ? Viridis.ViridisColor(0) : Viridis.ViridisColor(1));
        staticLink.transform.SetParent(transform, false);
        staticLink.LinkFromTo(dancer.Joints[index1].transform, dancer.Joints[index2].transform);
        return staticLink;
    }

    IEnumerator Iterate(int frameNumber)
    {
        if (frameNumber >= FRAME_MAX)
        {
            frameNumber = 0;
        }

        Lead.SetPoseToFrame(frameNumber);
        Follow.SetPoseToFrame(frameNumber);

        foreach (StaticLink staticLink in AllLinks)
        {
            staticLink.UpdateLink();
        }

        yield return new WaitForSeconds(.03f);

        frameNumber++;
        StartCoroutine(Iterate(frameNumber));
    }

    Dancer ReadAllPosesFrom(string jsonPath, string role)
    {
        Dancer dancer = new(role == "lead" ? Role.Lead : Role.Follow);

        string jsonString = File.ReadAllText(jsonPath);
        List<List<Float3>> allPoses = JsonConvert.DeserializeObject<List<List<Float3>>>(jsonString);
        List<List<Vector3>> allPosesVector3 = allPoses.Select(pose => pose.Select(float3 => new Vector3(float3.x, float3.y, float3.z)).ToList()).ToList();
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