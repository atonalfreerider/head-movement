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

    List<List<Vector3>> PosesByFrame = new();
    readonly Dictionary<int, Polygon> jointPolys = new();
    StaticLink spinePoly;
    StaticLink followSpineExtension;
    StaticLink followHeadAxis;
    Role Role;

    readonly List<StaticLink> jointLinks = new();

    FootScorpion leftFootScorpion;
    FootScorpion rightFootScorpion;

    Polygon chestTri;

    void Awake()
    {
        for (int j = 5; j < Enum.GetNames(typeof(CocoJoint)).Length; j++)
        {
            Polygon joint = Instantiate(PolygonFactory.Instance.icosahedron0);
            joint.gameObject.SetActive(true);
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

        leftFootScorpion = gameObject.AddComponent<FootScorpion>();
        rightFootScorpion = gameObject.AddComponent<FootScorpion>();
        
        chestTri = Instantiate(PolygonFactory.Instance.tri);
        chestTri.gameObject.SetActive(false);
        chestTri.transform.SetParent(transform, false);
        chestTri.SetColor(Cividis.CividisColor(.5f));
        
        spinePoly = Instantiate(StaticLink.prototypeStaticLink);
        spinePoly.gameObject.SetActive(true);
        spinePoly.SetColor(Cividis.CividisColor(.8f));
        spinePoly.transform.SetParent(transform, false);
        
        followSpineExtension = Instantiate(StaticLink.prototypeStaticLink);
        followSpineExtension.gameObject.SetActive(false);
        followSpineExtension.LW = .005f;
        followSpineExtension.SetColor(Cividis.CividisColor(.8f));
        followSpineExtension.transform.SetParent(transform, false);
        
        followHeadAxis = Instantiate(StaticLink.prototypeStaticLink);
        followHeadAxis.gameObject.SetActive(false);
        followHeadAxis.SetColor(Cividis.CividisColor(.8f));
        followHeadAxis.transform.SetParent(transform, false);
    }
    
    public void Init(Role role, List<List<Vector3>> posesByFrame)
    {
        Role = role;
        PosesByFrame = posesByFrame;
        
        if (Role == Role.Lead)
        {
            chestTri.gameObject.SetActive(true);
        }
        else
        {
            followSpineExtension.gameObject.SetActive(true);
            followHeadAxis.gameObject.SetActive(true);
        }
    }

    StaticLink LinkFromTo(int index1, int index2)
    {
        StaticLink staticLink = Instantiate(StaticLink.prototypeStaticLink);
        staticLink.gameObject.SetActive(true);
        staticLink.SetColor(Cividis.CividisColor(.8f));
        staticLink.transform.SetParent(transform, false);
        staticLink.LinkFromTo(jointPolys[index1].transform, jointPolys[index2].transform);
        return staticLink;
    }

    public void SetPoseToFrame(int frameNumber)
    {
        // SKELETON POSE
        List<Vector3> pose = PosesByFrame[frameNumber];

        for (int i = 5; i < pose.Count; i++)
        {
            jointPolys[i].transform.localPosition = pose[i];
        }

        foreach (StaticLink staticLink in jointLinks)
        {
            staticLink.UpdateLink();
        }
        
        // CHEST AND SPINE
        Vector3 lShoulder = pose[(int)CocoJoint.L_Shoulder];
        Vector3 rShoulder = pose[(int)CocoJoint.R_Shoulder];
        Vector3 shoulderMidpoint = (lShoulder + rShoulder) / 2;
        
        chestTri.transform.position = shoulderMidpoint;
        
        Vector3 hipMidpoint = (pose[(int)CocoJoint.L_Hip] +  pose[(int)CocoJoint.R_Hip]) / 2;
        
        spinePoly.DrawFromTo(hipMidpoint, shoulderMidpoint);
        
        Vector3 bodyAxis = hipMidpoint - shoulderMidpoint;
        Vector3 shoulderVector = lShoulder - rShoulder;
        Vector3 forwardVector = Vector3.Cross(shoulderVector, bodyAxis);
        Vector3 upVector = Vector3.Cross(shoulderVector, forwardVector).normalized;
        forwardVector = Vector3.Cross(upVector, shoulderVector).normalized;

        chestTri.transform.rotation = Quaternion.LookRotation(forwardVector, upVector);
        chestTri.transform.localScale = new Vector3(Vector3.Distance(lShoulder, rShoulder) * .5f, .01f, .085f);
        chestTri.transform.Translate(Vector3.forward * .075f);
        
        followSpineExtension.DrawFromTo(shoulderMidpoint, shoulderMidpoint + upVector * .1f);

        Vector3 headCenter = (pose[(int)CocoJoint.L_Ear] + pose[(int)CocoJoint.R_Ear]) / 2;
        
        followHeadAxis.DrawFromTo(shoulderMidpoint, headCenter);
                
        // FOOT SCORPION
        leftFootScorpion.SyncToAnkleAndKnee(
            pose[(int)CocoJoint.L_Ankle],
            pose[(int)CocoJoint.L_Knee],
            pose[(int)CocoJoint.L_Hip]);
        rightFootScorpion.SyncToAnkleAndKnee(
            pose[(int)CocoJoint.R_Ankle],
            pose[(int)CocoJoint.R_Knee],
            pose[(int)CocoJoint.R_Hip]);

        List<Vector3> pastLeftAnklePositions = new();
        for (int i = frameNumber - 1; i >= 0; i--)
        {
            pastLeftAnklePositions.Add(PosesByFrame[i][(int)CocoJoint.L_Ankle]);
        }

        List<Vector3> futureLeftAnklePositions = new();
        for (int i = frameNumber + 1; i < PosesByFrame.Count; i++)
        {
            futureLeftAnklePositions.Add(PosesByFrame[i][(int)CocoJoint.L_Ankle]);
        }

        List<Vector3> pastRightAnklePositions = new();
        for (int i = frameNumber - 1; i >= 0; i--)
        {
            pastRightAnklePositions.Add(PosesByFrame[i][(int)CocoJoint.R_Ankle]);
        }

        List<Vector3> futureRightAnklePositions = new();
        for (int i = frameNumber + 1; i < PosesByFrame.Count; i++)
        {
            futureRightAnklePositions.Add(PosesByFrame[i][(int)CocoJoint.R_Ankle]);
        }

        leftFootScorpion.SetPastAndFuture(
            futureLeftAnklePositions, 
            pastLeftAnklePositions,
            pose[(int)CocoJoint.L_Ankle]);
        rightFootScorpion.SetPastAndFuture(
            futureRightAnklePositions, 
            pastRightAnklePositions,
            pose[(int)CocoJoint.R_Ankle]);

        if (pose[(int)CocoJoint.L_Ankle].y < pose[(int)CocoJoint.R_Ankle].y)
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