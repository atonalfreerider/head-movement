using System;
using System.Collections.Generic;
using Shapes;
using Shapes.Lines;
using UnityEngine;
using Util;

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
    enum Halpe
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

    enum SmplJoint
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

    enum CocoLimbs
    {
        // Precomputed with Szudzik pairing to correspond with joint indices
        R_Upper_Arm = 70,
        L_Upper_Arm = 54,
        R_Forearm = 108,
        L_Forearm = 88,
        R_Thigh = 208,
        L_Thigh = 180,
        R_Calf = 270,
        L_Calf = 238,
        Pelvis = 167,
        Shoulders = 47
    }

    enum SmplLimbs
    {
        L_Calf = 60, // L_Ankle to L_Knee
        R_Calf = 77, // R_Ankle to R_Knee
        L_Thigh = 17, // L_Hip to L_Knee
        R_Thigh = 27, // R_Hip to R_Knee
        L_HipToPelvis = 2, // L_Hip to Pelvis
        R_HipToPelvis = 6, // R_Hip to Pelvis
        L_UpperArm = 340, // L_Shoulder to L_Elbow
        R_UpperArm = 378, // R_Shoulder to R_Elbow
        L_Forearm = 418, // L_Elbow to L_Wrist
        R_Forearm = 460, // R_Elbow to R_Wrist
        PelvisToSpine1 = 9, // Pelvis to Spine1
        Spine3ToSpine2 = 96, // Spine3 to Spine2
        Spine2ToSpine1 = 45, // Spine2 to Spine1
        Spine3ToNeck = 153, // Spine3 to Neck
        NeckToHead = 237, // Neck to Head
        L_Foot = 107, // L_Ankle to L_Foot
        R_Foot = 129, // R_Ankle to R_Foot
        L_Hand = 526, // L_Hand to L_Wrist
        R_Hand = 573, // R_Hand to R_Wrist
        L_CollarToShoulder = 285, // L_Shoulder to L_Collar
        R_CollarToShoulder = 320, // R_Shoulder to R_Collar
        L_CollarToNeck = 194, // L_Collar to Neck
        R_CollarToNeck = 222 // R_Collar to Neck
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

        foreach (SmplLimbs limb in Enum.GetValues(typeof(SmplLimbs)))
        {
            uint[] pair = Szudzik.uintSzudzik2tupleReverse((uint)limb);
            StaticLink limbLink = LinkFromTo((int)pair[0], (int)pair[1]);
            jointLinks.Add(limbLink);
        }

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
            List<Vector3> pose = PosesByFrame[i];
            pastLeftAnklePositions.Add(GetAnklePosition(pose, true));
            pastRightAnklePositions.Add(GetAnklePosition(pose, false));
        }

        for (int i = frameNumber + 1; i < PosesByFrame.Count; i++)
        {
            List<Vector3> pose = PosesByFrame[i];
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

        foreach (CocoLimbs limb in Enum.GetValues(typeof(CocoLimbs)))
        {
            uint[] pair = Szudzik.uintSzudzik2tupleReverse((uint)limb);
            StaticLink limbLink = LinkFromTo((int)pair[0], (int)pair[1]);
            jointLinks.Add(limbLink);
        }

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
        staticLink.name = poseType switch
        {
            PoseType.Coco => $"{((CocoJoint)index1).ToString()}-{((CocoJoint)index2).ToString()}",
            PoseType.Halpe => $"{((Halpe)index1).ToString()}-{((Halpe)index2).ToString()}",
            PoseType.Smpl => $"{((SmplJoint)index1).ToString()}-{((SmplJoint)index2).ToString()}",
            _ => throw new ArgumentOutOfRangeException(nameof(poseType), poseType,
                null) // Handle unexpected poseType values
        };


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