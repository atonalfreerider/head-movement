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
    enum CocoJoint
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

    public List<List<Vector3>> PosesByFrame = new();
    readonly List<Polygon> jointPolys = new();
    public Role Role;

    readonly List<StaticLink> jointLinks = new();

    FootScorpion leftFootScorpion;
    FootScorpion rightFootScorpion;

    void Awake()
    {
        for (int j = 0; j < Enum.GetNames(typeof(CocoJoint)).Length; j++)
        {
            Polygon joint = Instantiate(PolygonFactory.Instance.icosahedron0);
            joint.gameObject.SetActive(true);
            joint.transform.SetParent(transform, false);
            joint.transform.localScale = Vector3.one * .02f;
            joint.SetColor(Cividis.CividisColor(.7f));
            jointPolys.Add(joint);
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
        jointLinks.Add(LinkFromTo((int)CocoJoint.R_Shoulder, (int)CocoJoint.R_Hip));
        jointLinks.Add(LinkFromTo((int)CocoJoint.L_Shoulder, (int)CocoJoint.L_Hip));

        leftFootScorpion = gameObject.AddComponent<FootScorpion>();
        rightFootScorpion = gameObject.AddComponent<FootScorpion>();
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
        List<Vector3> pose = PosesByFrame[frameNumber];

        for (int i = 0; i < pose.Count; i++)
        {
            jointPolys[i].transform.localPosition = pose[i];
        }

        foreach (StaticLink staticLink in jointLinks)
        {
            staticLink.UpdateLink();
        }

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
}