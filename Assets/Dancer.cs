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
    List<List<Vector3>> PosesByFrame = new();
    readonly Dictionary<int, Polygon> jointPolys = new();
    StaticLink spinePoly;
    StaticLink followSpineExtension;
    StaticLink followHeadAxis;
    Role Role;
    PoseType poseType;

    readonly List<StaticLink> jointLinks = new();
    
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
                //followHeadAxis.gameObject.SetActive(true);
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
        spinePoly.name = "spine poly";
        spinePoly.gameObject.SetActive(true);
        spinePoly.SetColor(Cividis.CividisColor(.8f));
        spinePoly.transform.SetParent(transform, false);

        followHeadAxis = Instantiate(StaticLink.prototypeStaticLink);
        followHeadAxis.name = "follow head axis";
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
        chestTri = Instantiate(PolygonFactory.Instance.tri);
        chestTri.gameObject.SetActive(false);
        chestTri.transform.SetParent(transform, false);
        chestTri.SetColor(Cividis.CividisColor(.5f));

        followSpineExtension = Instantiate(StaticLink.prototypeStaticLink);
        followSpineExtension.name = "follow spine extension";
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
    }
    
    public List<Vector3> GetPoseAtFrame(int frameNumber)
    {
        return PosesByFrame[frameNumber];
    }
}