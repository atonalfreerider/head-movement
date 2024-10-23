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
        Halpe = 1,
        Smpl = 2
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

    public enum SmplJoint
    {
        Pelvis = 0,
        L_Hip = 1,
        R_Hip = 2,
        Spine1 = 3,
        L_Knee = 4,
        R_Knee = 5,
        Spine2 = 6,
        L_Ankle = 7,
        R_Ankle = 8,
        Spine3 = 9,
        L_Foot = 10,
        R_Foot = 11,
        Neck = 12,
        L_Collar = 13,
        R_Collar = 14,
        Head = 15,
        L_Shoulder = 16,
        R_Shoulder = 17,
        L_Elbow = 18,
        R_Elbow = 19,
        L_Wrist = 20,
        R_Wrist = 21,
        L_Hand = 22,
        R_Hand = 23
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
            case PoseType.Smpl:
                BuildSmpl();
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

    void BuildSmpl()
    {
        for (int j = 0; j < Enum.GetNames(typeof(SmplJoint)).Length; j++)
        {
            Polygon joint = Instantiate(PolygonFactory.Instance.icosahedron0);
            joint.gameObject.SetActive(true);
            joint.name = ((SmplJoint)j).ToString();
            joint.transform.SetParent(transform, false);
            joint.transform.localScale = Vector3.one * .02f;
            joint.SetColor(Cividis.CividisColor(.7f));
            jointPolys.Add(j, joint);
        }

        // Add skeletal connections based on SMPL structure
        jointLinks.Add(LinkFromTo((int)SmplJoint.L_Hip, (int)SmplJoint.L_Knee));
        jointLinks.Add(LinkFromTo((int)SmplJoint.L_Knee, (int)SmplJoint.L_Ankle));
        jointLinks.Add(LinkFromTo((int)SmplJoint.L_Ankle, (int)SmplJoint.L_Foot));
        jointLinks.Add(LinkFromTo((int)SmplJoint.R_Hip, (int)SmplJoint.R_Knee));
        jointLinks.Add(LinkFromTo((int)SmplJoint.R_Knee, (int)SmplJoint.R_Ankle));
        jointLinks.Add(LinkFromTo((int)SmplJoint.R_Ankle, (int)SmplJoint.R_Foot));

        jointLinks.Add(LinkFromTo((int)SmplJoint.L_Shoulder, (int)SmplJoint.L_Elbow));
        jointLinks.Add(LinkFromTo((int)SmplJoint.L_Elbow, (int)SmplJoint.L_Wrist));
        jointLinks.Add(LinkFromTo((int)SmplJoint.L_Wrist, (int)SmplJoint.L_Hand));
        jointLinks.Add(LinkFromTo((int)SmplJoint.R_Shoulder, (int)SmplJoint.R_Elbow));
        jointLinks.Add(LinkFromTo((int)SmplJoint.R_Elbow, (int)SmplJoint.R_Wrist));
        jointLinks.Add(LinkFromTo((int)SmplJoint.R_Wrist, (int)SmplJoint.R_Hand));

        jointLinks.Add(LinkFromTo((int)SmplJoint.L_Collar, (int)SmplJoint.L_Shoulder));
        jointLinks.Add(LinkFromTo((int)SmplJoint.R_Collar, (int)SmplJoint.R_Shoulder));

        jointLinks.Add(LinkFromTo((int)SmplJoint.Pelvis, (int)SmplJoint.L_Hip));
        jointLinks.Add(LinkFromTo((int)SmplJoint.Pelvis, (int)SmplJoint.R_Hip));

        // Spine chain
        jointLinks.Add(LinkFromTo((int)SmplJoint.Pelvis, (int)SmplJoint.Spine1));
        jointLinks.Add(LinkFromTo((int)SmplJoint.Spine1, (int)SmplJoint.Spine2));
        jointLinks.Add(LinkFromTo((int)SmplJoint.Spine2, (int)SmplJoint.Spine3));
        jointLinks.Add(LinkFromTo((int)SmplJoint.Spine3, (int)SmplJoint.Neck));
        jointLinks.Add(LinkFromTo((int)SmplJoint.Neck, (int)SmplJoint.Head));

        BuildShared();
    }

    private void UpdateFootScorpions(int frameNumber, Vector3 lAnkle, Vector3 rAnkle, Vector3 lKnee, Vector3 rKnee,
        Vector3 lHip, Vector3 rHip)
    {
        leftFootScorpion.SyncToAnkleAndKnee(lAnkle, lKnee, lHip);
        rightFootScorpion.SyncToAnkleAndKnee(rAnkle, rKnee, rHip);

        List<Vector3> pastLeftAnklePositions = new();
        List<Vector3> futureLeftAnklePositions = new();
        List<Vector3> pastRightAnklePositions = new();
        List<Vector3> futureRightAnklePositions = new();

        for (int i = frameNumber - 1; i >= 0; i--)
        {
            var pose = PosesByFrame[i];
            pastLeftAnklePositions.Add(GetAnklePosition(pose, true));
            pastRightAnklePositions.Add(GetAnklePosition(pose, false));
        }

        for (int i = frameNumber + 1; i < PosesByFrame.Count; i++)
        {
            var pose = PosesByFrame[i];
            futureLeftAnklePositions.Add(GetAnklePosition(pose, true));
            futureRightAnklePositions.Add(GetAnklePosition(pose, false));
        }

        leftFootScorpion.SetPastAndFuture(futureLeftAnklePositions, pastLeftAnklePositions, lAnkle);
        rightFootScorpion.SetPastAndFuture(futureRightAnklePositions, pastRightAnklePositions, rAnkle);

        if (lAnkle.y < rAnkle.y)
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
        List<Vector3> pose = PosesByFrame[frameNumber];

        switch (poseType)
        {
            case PoseType.Coco:
                for (int i = 5; i < pose.Count; i++)
                {
                    jointPolys[i].transform.localPosition = pose[i];
                }

                break;
            case PoseType.Halpe:
                for (int i = 0; i < 24; i++)
                {
                    jointPolys[i].transform.localPosition = pose[i];
                }

                break;
            case PoseType.Smpl:
                for (int i = 0; i < pose.Count; i++)
                {
                    jointPolys[i].transform.localPosition = pose[i];
                }

                break;
        }

        foreach (StaticLink staticLink in jointLinks)
        {
            staticLink.UpdateLink();
        }

        // Update chest and spine visualization
        Vector3 lShoulder, rShoulder, lHip, rHip;

        switch (poseType)
        {
            case PoseType.Coco:
                lShoulder = pose[(int)CocoJoint.L_Shoulder];
                rShoulder = pose[(int)CocoJoint.R_Shoulder];
                lHip = pose[(int)CocoJoint.L_Hip];
                rHip = pose[(int)CocoJoint.R_Hip];
                break;
            case PoseType.Halpe:
                lShoulder = pose[(int)Halpe.LShoulder];
                rShoulder = pose[(int)Halpe.RShoulder];
                lHip = pose[(int)Halpe.LHip];
                rHip = pose[(int)Halpe.RHip];
                break;
            case PoseType.Smpl:
                lShoulder = pose[(int)SmplJoint.L_Shoulder];
                rShoulder = pose[(int)SmplJoint.R_Shoulder];
                lHip = pose[(int)SmplJoint.L_Hip];
                rHip = pose[(int)SmplJoint.R_Hip];
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        Vector3 shoulderMidpoint = (lShoulder + rShoulder) / 2;
        Vector3 hipMidpoint = (lHip + rHip) / 2;

        chestTri.transform.position = shoulderMidpoint;

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

        // Update foot scorpion positions
        Vector3 lAnkle, rAnkle, lKnee, rKnee;

        switch (poseType)
        {
            case PoseType.Coco:
                lAnkle = pose[(int)CocoJoint.L_Ankle];
                rAnkle = pose[(int)CocoJoint.R_Ankle];
                lKnee = pose[(int)CocoJoint.L_Knee];
                rKnee = pose[(int)CocoJoint.R_Knee];
                break;
            case PoseType.Halpe:
                lAnkle = pose[(int)Halpe.LAnkle];
                rAnkle = pose[(int)Halpe.RAnkle];
                lKnee = pose[(int)Halpe.LKnee];
                rKnee = pose[(int)Halpe.RKnee];
                break;
            case PoseType.Smpl:
                lAnkle = pose[(int)SmplJoint.L_Ankle];
                rAnkle = pose[(int)SmplJoint.R_Ankle];
                lKnee = pose[(int)SmplJoint.L_Knee];
                rKnee = pose[(int)SmplJoint.R_Knee];
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        UpdateFootScorpions(frameNumber, lAnkle, rAnkle, lKnee, rKnee, lHip, rHip);
    }
    
    private Vector3 GetAnklePosition(List<Vector3> pose, bool isLeft)
    {
        switch (poseType)
        {
            case PoseType.Coco:
                return pose[isLeft ? (int)CocoJoint.L_Ankle : (int)CocoJoint.R_Ankle];
            case PoseType.Halpe:
                return pose[isLeft ? (int)Halpe.LAnkle : (int)Halpe.RAnkle];
            case PoseType.Smpl:
                return pose[isLeft ? (int)SmplJoint.L_Ankle : (int)SmplJoint.R_Ankle];
            default:
                throw new ArgumentOutOfRangeException();
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