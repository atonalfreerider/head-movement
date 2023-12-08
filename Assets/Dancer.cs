using System;
using System.Collections.Generic;
using Shapes;
using Shapes.Lines;
using UnityEngine;

public enum Role
{
    Lead = 0,
    Follow = 1
}

public class Dancer : MonoBehaviour
{
    public enum PoseType
    {
        Coco = 0,
        Halpe = 1
    }

    public enum CocoJoint
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

    /// <summary>
    /// https://github.com/Fang-Haoshu/Halpe-FullBody
    /// </summary>
    public enum Halpe
    {
        Nose = 0,
        LEye = 1,
        REye = 2,
        LEar = 3,
        REar = 4,
        LShoulder = 5,
        RShoulder = 6,
        LElbow = 7,
        RElbow = 8,
        LWrist = 9,
        RWrist = 10,
        LHip = 11,
        RHip = 12,
        LKnee = 13,
        RKnee = 14,
        LAnkle = 15,
        RAnkle = 16,
        Head = 17,
        Neck = 18,
        Hip = 19,
        LBigToe = 20,
        RBigToe = 21,
        LSmallToe = 22,
        RSmallToe = 23,
        LHeel = 24,
        RHeel = 25,

        // 68 Face Keypoints
        Face1 = 26,
        Face2 = 27,
        Face3 = 28,
        Face4 = 29,

        // ...
        Face68 = 93,

        // 21 Left Hand Keypoints
        LHand1 = 94,
        LHand2 = 95,

        // ...
        LHand21 = 114,

        // 21 Right Hand Keypoints
        RHand1 = 115,
        RHand2 = 116,

        // ...
        RHand21 = 135
    }

    List<List<Vector3>> PosesByFrame = new();
    readonly Dictionary<int, Polygon> jointPolys = new();
    StaticLink spinePoly;
    StaticLink followSpineExtension;
    StaticLink followHeadAxis;
    Role Role;
    PoseType poseType;

    readonly List<StaticLink> jointLinks = new();

    FootScorpion leftFootScorpion;
    FootScorpion rightFootScorpion;

    Polygon chestTri;

    public void Init(Role role, List<List<Vector3>> posesByFrame, PoseType poseType)
    {
        this.poseType = poseType;
        switch (poseType)
        {
            case PoseType.Coco:
                BuildCoco();
                break;
            case PoseType.Halpe:
                BuildHalpe();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(poseType), poseType, null);
        }

        Role = role;
        PosesByFrame = posesByFrame;

        if (Role == Role.Lead)
        {
            chestTri.gameObject.SetActive(true);
        }
        else
        {
            followSpineExtension.gameObject.SetActive(true);
            if (poseType == PoseType.Coco)
            {
                followHeadAxis.gameObject.SetActive(true);
            }
        }
    }

    void BuildCoco()
    {
        for (int j = 5; j < Enum.GetNames(typeof(CocoJoint)).Length; j++) // ignore head
        {
            Polygon joint = Instantiate(PolygonFactory.Instance.icosahedron0);
            joint.gameObject.SetActive(true);
            joint.name = ((CocoJoint)j).ToString();
            joint.transform.SetParent(transform, false);
            joint.transform.localScale = Vector3.one * .02f;
            joint.SetColor(Cividis.CividisColor(.7f));
            jointPolys.Add(j, joint);
        }

        jointLinks.Add(LinkFromTo((int)CocoJoint.R_Hip, (int)CocoJoint.R_Knee));
        jointLinks.Add(LinkFromTo((int)CocoJoint.R_Knee, (int)CocoJoint.R_Ankle));
        jointLinks.Add(LinkFromTo((int)CocoJoint.L_Hip, (int)CocoJoint.L_Knee));
        jointLinks.Add(LinkFromTo((int)CocoJoint.L_Knee, (int)CocoJoint.L_Ankle));
        jointLinks.Add(LinkFromTo((int)CocoJoint.R_Shoulder, (int)CocoJoint.R_Elbow));
        jointLinks.Add(LinkFromTo((int)CocoJoint.R_Elbow, (int)CocoJoint.R_Wrist));
        jointLinks.Add(LinkFromTo((int)CocoJoint.L_Shoulder, (int)CocoJoint.L_Elbow));
        jointLinks.Add(LinkFromTo((int)CocoJoint.L_Elbow, (int)CocoJoint.L_Wrist));
        jointLinks.Add(LinkFromTo((int)CocoJoint.R_Shoulder, (int)CocoJoint.L_Shoulder));
        jointLinks.Add(LinkFromTo((int)CocoJoint.R_Hip, (int)CocoJoint.L_Hip));

        spinePoly = Instantiate(StaticLink.prototypeStaticLink);
        spinePoly.gameObject.SetActive(true);
        spinePoly.SetColor(Cividis.CividisColor(.8f));
        spinePoly.transform.SetParent(transform, false);

        followHeadAxis = Instantiate(StaticLink.prototypeStaticLink);
        followHeadAxis.gameObject.SetActive(false);
        followHeadAxis.SetColor(Cividis.CividisColor(.8f));
        followHeadAxis.transform.SetParent(transform, false);

        BuildShared();
    }

    void BuildHalpe()
    {
        for (int j = 0; j < 26; j++) // ignore face and hands
        {
            Polygon joint = Instantiate(PolygonFactory.Instance.icosahedron0);
            joint.gameObject.SetActive(true);
            joint.name = ((Halpe)j).ToString();
            joint.transform.SetParent(transform, false);
            joint.transform.localScale = Vector3.one * .02f;
            joint.SetColor(Cividis.CividisColor(.7f));
            jointPolys.Add(j, joint);
        }

        jointLinks.Add(LinkFromTo((int)Halpe.RHip, (int)Halpe.RKnee));
        jointLinks.Add(LinkFromTo((int)Halpe.RKnee, (int)Halpe.RAnkle));
        jointLinks.Add(LinkFromTo((int)Halpe.LHip, (int)Halpe.LKnee));
        jointLinks.Add(LinkFromTo((int)Halpe.LKnee, (int)Halpe.LAnkle));
        jointLinks.Add(LinkFromTo((int)Halpe.RShoulder, (int)Halpe.RElbow));
        jointLinks.Add(LinkFromTo((int)Halpe.RElbow, (int)Halpe.RWrist));
        jointLinks.Add(LinkFromTo((int)Halpe.LShoulder, (int)Halpe.LElbow));
        jointLinks.Add(LinkFromTo((int)Halpe.LElbow, (int)Halpe.LWrist));
        jointLinks.Add(LinkFromTo((int)Halpe.RShoulder, (int)Halpe.Neck));
        jointLinks.Add(LinkFromTo((int)Halpe.LShoulder, (int)Halpe.Neck));
        jointLinks.Add(LinkFromTo((int)Halpe.RHip, (int)Halpe.Hip));
        jointLinks.Add(LinkFromTo((int)Halpe.LHip, (int)Halpe.Hip));
        jointLinks.Add(LinkFromTo((int)Halpe.Hip, (int)Halpe.Neck));

        BuildShared();
    }

    void BuildShared()
    {
        leftFootScorpion = gameObject.AddComponent<FootScorpion>();
        rightFootScorpion = gameObject.AddComponent<FootScorpion>();

        chestTri = Instantiate(PolygonFactory.Instance.tri);
        chestTri.gameObject.SetActive(false);
        chestTri.transform.SetParent(transform, false);
        chestTri.SetColor(Cividis.CividisColor(.5f));

        followSpineExtension = Instantiate(StaticLink.prototypeStaticLink);
        followSpineExtension.gameObject.SetActive(false);
        followSpineExtension.LW = .005f;
        followSpineExtension.SetColor(Cividis.CividisColor(.8f));
        followSpineExtension.transform.SetParent(transform, false);
    }

    StaticLink LinkFromTo(int index1, int index2)
    {
        StaticLink staticLink = Instantiate(StaticLink.prototypeStaticLink);
        staticLink.gameObject.SetActive(true);
        staticLink.name = poseType == PoseType.Coco
            ? $"{((CocoJoint)index1).ToString()}-{((CocoJoint)index2).ToString()}"
            : $"{((Halpe)index1).ToString()}-{((Halpe)index2).ToString()}";
        staticLink.SetColor(Cividis.CividisColor(.8f));
        staticLink.transform.SetParent(transform, false);
        staticLink.LinkFromTo(jointPolys[index1].transform, jointPolys[index2].transform);
        return staticLink;
    }

    public void SetPoseToFrame(int frameNumber)
    {
        // SKELETON POSE
        List<Vector3> pose = PosesByFrame[frameNumber];

        if (poseType == PoseType.Coco)
        {
            for (int i = 5; i < pose.Count; i++) // ignoring head
            {
                jointPolys[i].transform.localPosition = pose[i];
            }
        }
        else if (poseType == PoseType.Halpe)
        {
            for (int i = 0; i < 26; i++) // ignoring face and hands
            {
                jointPolys[i].transform.localPosition = pose[i];
            }
        }

        foreach (StaticLink staticLink in jointLinks)
        {
            staticLink.UpdateLink();
        }

        // CHEST AND SPINE
        int lShoulderIndex = poseType == PoseType.Coco ? (int)CocoJoint.L_Shoulder : (int)Halpe.LShoulder;
        int rShoulderIndex = poseType == PoseType.Coco ? (int)CocoJoint.R_Shoulder : (int)Halpe.RShoulder;
        
        Vector3 lShoulder = pose[lShoulderIndex];
        Vector3 rShoulder = pose[rShoulderIndex];
        Vector3 shoulderMidpoint = (lShoulder + rShoulder) / 2;

        chestTri.transform.position = shoulderMidpoint;

        int lHipIndex = poseType == PoseType.Coco ? (int)CocoJoint.L_Hip : (int)Halpe.LHip;
        int rHipIndex = poseType == PoseType.Coco ? (int)CocoJoint.R_Hip : (int)Halpe.RHip;
        
        Vector3 hipMidpoint = (pose[lHipIndex] + pose[rHipIndex]) / 2;

        if (poseType == PoseType.Coco)
        {
            spinePoly.DrawFromTo(hipMidpoint, shoulderMidpoint);
        }

        Vector3 bodyAxis = hipMidpoint - shoulderMidpoint;
        Vector3 shoulderVector = lShoulder - rShoulder;
        Vector3 forwardVector = Vector3.Cross(shoulderVector, bodyAxis);
        Vector3 upVector = Vector3.Cross(shoulderVector, forwardVector).normalized;
        forwardVector = Vector3.Cross(upVector, shoulderVector).normalized;

        chestTri.transform.rotation = Quaternion.LookRotation(forwardVector, upVector);
        chestTri.transform.localScale = new Vector3(Vector3.Distance(lShoulder, rShoulder) * .5f, .01f, .085f);
        chestTri.transform.Translate(Vector3.forward * .075f);

        followSpineExtension.DrawFromTo(shoulderMidpoint, shoulderMidpoint + upVector * .1f);

        if (poseType == PoseType.Coco)
        {
            Vector3 headCenter = (pose[(int)CocoJoint.L_Ear] + pose[(int)CocoJoint.R_Ear]) / 2;
            followHeadAxis.DrawFromTo(shoulderMidpoint, headCenter);
        }

        // FOOT SCORPION
        int lAnkleIndex = poseType == PoseType.Coco ? (int)CocoJoint.L_Ankle : (int)Halpe.LAnkle;
        int rAnkleIndex = poseType == PoseType.Coco ? (int)CocoJoint.R_Ankle : (int)Halpe.RAnkle;
        int lKneeIndex = poseType == PoseType.Coco ? (int)CocoJoint.L_Knee : (int)Halpe.LKnee;
        int rKneeIndex = poseType == PoseType.Coco ? (int)CocoJoint.R_Knee : (int)Halpe.RKnee;
        
        leftFootScorpion.SyncToAnkleAndKnee(
            pose[lAnkleIndex],
            pose[lKneeIndex],
            pose[lHipIndex]);
        rightFootScorpion.SyncToAnkleAndKnee(
            pose[rAnkleIndex],
            pose[rKneeIndex],
            pose[rHipIndex]);

        List<Vector3> pastLeftAnklePositions = new();
        for (int i = frameNumber - 1; i >= 0; i--)
        {
            pastLeftAnklePositions.Add(PosesByFrame[i][lAnkleIndex]);
        }

        List<Vector3> futureLeftAnklePositions = new();
        for (int i = frameNumber + 1; i < PosesByFrame.Count; i++)
        {
            futureLeftAnklePositions.Add(PosesByFrame[i][lAnkleIndex]);
        }

        List<Vector3> pastRightAnklePositions = new();
        for (int i = frameNumber - 1; i >= 0; i--)
        {
            pastRightAnklePositions.Add(PosesByFrame[i][rAnkleIndex]);
        }

        List<Vector3> futureRightAnklePositions = new();
        for (int i = frameNumber + 1; i < PosesByFrame.Count; i++)
        {
            futureRightAnklePositions.Add(PosesByFrame[i][rAnkleIndex]);
        }

        leftFootScorpion.SetPastAndFuture(
            futureLeftAnklePositions,
            pastLeftAnklePositions,
            pose[lAnkleIndex]);
        rightFootScorpion.SetPastAndFuture(
            futureRightAnklePositions,
            pastRightAnklePositions,
            pose[rAnkleIndex]);

        if (pose[lAnkleIndex].y < pose[rAnkleIndex].y)
        {
            leftFootScorpion.SetGroundTriState(true);
            rightFootScorpion.SetGroundTriState(false);
        }
        else
        {
            leftFootScorpion.SetGroundTriState(false);
            rightFootScorpion.SetGroundTriState(true);
        }
    }

    public void SetJointTemperature(int jointIndex, float temperature)
    {
        jointPolys[jointIndex].SetColor(Cividis.CividisColor(1 - temperature));
    }

    public List<Vector3> GetPoseAtFrame(int frameNumber)
    {
        return PosesByFrame[frameNumber];
    }
}