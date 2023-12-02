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

    public List<List<Vector3>> PosesByFrame = new();
    readonly List<Polygon> jointPolys = new();
    public Role Role;

    readonly List<StaticLink> jointLinks = new();

    void Awake()
    {
        for (int j = 0; j < Enum.GetNames(typeof(Joints)).Length; j++)
        {
            Polygon joint = Instantiate(PolygonFactory.Instance.icosahedron0);
            joint.gameObject.SetActive(true);
            joint.transform.SetParent(transform, false);
            joint.transform.localScale = Vector3.one * .02f;
            joint.SetColor(Cividis.CividisColor(.7f));
            jointPolys.Add(joint);
        }

        jointLinks.Add(LinkFromTo((int)Joints.Nose, (int)Joints.L_Eye));
        jointLinks.Add(LinkFromTo((int)Joints.Nose, (int)Joints.R_Eye));
        jointLinks.Add(LinkFromTo((int)Joints.L_Eye, (int)Joints.R_Eye));
        jointLinks.Add(LinkFromTo((int)Joints.L_Eye, (int)Joints.L_Ear));
        jointLinks.Add(LinkFromTo((int)Joints.L_Ear, (int)Joints.L_Shoulder));
        jointLinks.Add(LinkFromTo((int)Joints.R_Eye, (int)Joints.R_Ear));
        jointLinks.Add(LinkFromTo((int)Joints.R_Ear, (int)Joints.R_Shoulder));
        jointLinks.Add(LinkFromTo((int)Joints.R_Hip, (int)Joints.R_Knee));
        jointLinks.Add(LinkFromTo((int)Joints.R_Knee, (int)Joints.R_Ankle));
        jointLinks.Add(LinkFromTo((int)Joints.L_Hip, (int)Joints.L_Knee));
        jointLinks.Add(LinkFromTo((int)Joints.L_Knee, (int)Joints.L_Ankle));
        jointLinks.Add(LinkFromTo((int)Joints.R_Shoulder, (int)Joints.R_Elbow));
        jointLinks.Add(LinkFromTo((int)Joints.R_Elbow, (int)Joints.R_Wrist));
        jointLinks.Add(LinkFromTo((int)Joints.L_Shoulder, (int)Joints.L_Elbow));
        jointLinks.Add(LinkFromTo((int)Joints.L_Elbow, (int)Joints.L_Wrist));
        jointLinks.Add(LinkFromTo((int)Joints.R_Shoulder, (int)Joints.L_Shoulder));
        jointLinks.Add(LinkFromTo((int)Joints.R_Hip, (int)Joints.L_Hip));
        jointLinks.Add(LinkFromTo((int)Joints.R_Shoulder, (int)Joints.R_Hip));
        jointLinks.Add(LinkFromTo((int)Joints.L_Shoulder, (int)Joints.L_Hip));
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
    }
}