using System;
using System.Collections.Generic;
using System.Linq;
using Shapes;
using UnityEngine;

public enum Role
{
    Lead = 0,
    Follow = 1
}

public class Dancer : MonoBehaviour
{
    List<List<Vector3>> PosesByFrame = new();
    readonly Dictionary<int, Polygon> jointPolys = new();
    Role Role;
    PoseType poseType;

    LineRenderer followSpineRenderer;
    LineRenderer followLegsRenderer;
    LineRenderer leadArmsRenderer;

    readonly int[] smplFollowSpine =
    {
        (int)SmplJoint.Spine1, 
        (int)SmplJoint.Spine2, 
        (int)SmplJoint.Spine3, 
        (int)SmplJoint.Neck,
        (int)SmplJoint.Head
    };

    readonly int[] smplFollowLegs =
    {
        (int)SmplJoint.L_Foot,
        (int)SmplJoint.L_Ankle,
        (int)SmplJoint.L_Knee,
        (int)SmplJoint.L_Hip,
        (int)SmplJoint.Pelvis,
        (int)SmplJoint.R_Hip,
        (int)SmplJoint.R_Knee,
        (int)SmplJoint.R_Ankle,
        (int)SmplJoint.R_Foot
    };

    readonly int[] smplLeadArms =
    {
        (int)SmplJoint.L_Hand,
        (int)SmplJoint.L_Wrist,
        (int)SmplJoint.L_Elbow,
        (int)SmplJoint.L_Shoulder,
        (int)SmplJoint.L_Collar,
        (int)SmplJoint.Spine1,
        (int)SmplJoint.R_Collar,
        (int)SmplJoint.R_Shoulder,
        (int)SmplJoint.R_Elbow,
        (int)SmplJoint.R_Wrist,
        (int)SmplJoint.R_Hand
    };

    public Material BloomMat;

    void Awake()
    {
        BloomMat =new Material(Shader.Find("Unlit/Color"));
    }
    
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
                BuildSmpl(role);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(poseType), poseType, null);
        }

        Role = role;
        PosesByFrame = posesByFrame;
    }

    void BuildSmpl(Role role)
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

        switch (role)
        {
            case Role.Follow:
                followSpineRenderer = NewLineRenderer(0.01f);
                followSpineRenderer.transform.SetParent(transform, false);
                followLegsRenderer = NewLineRenderer(0.01f);
                followLegsRenderer.transform.SetParent(transform, false);
                break;
            case Role.Lead:
                leadArmsRenderer = NewLineRenderer(0.01f);
                leadArmsRenderer.transform.SetParent(transform, false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(role), role, null);
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

                switch (Role)
                {
                    case Role.Follow:
                    {
                        Vector3[] spineArray = new Vector3[smplFollowSpine.Length];
                        for (int i = 0; i < smplFollowSpine.Length; i++)
                        {
                            spineArray[i] = pose[smplFollowSpine[i]];
                        }

                        Vector3[] spineBez = BezierCurve(spineArray);
                        followSpineRenderer.positionCount = spineBez.Length;
                        followSpineRenderer.SetPositions(spineBez);
                        
                        Vector3[] legsArray = new Vector3[smplFollowLegs.Length];
                        for (int i = 0; i < smplFollowLegs.Length; i++)
                        {
                            legsArray[i] = pose[smplFollowLegs[i]];
                        }

                        Vector3[] legsBez = BezierCurve(legsArray);
                        followLegsRenderer.positionCount = legsBez.Length;
                        followLegsRenderer.SetPositions(legsBez);
                        
                        break;
                    }
                    case Role.Lead:
                        Vector3[] armsArray = new Vector3[smplLeadArms.Length];
                        for (int i = 0; i < smplLeadArms.Length; i++)
                        {
                            int x = smplLeadArms[i];
                            if (x == (int)SmplJoint.Spine1)
                            {
                                Vector3 spineVector = pose[x];
                                Vector3 hipMidpoint = pose[(int)SmplJoint.Pelvis];
                                Vector3 bodyAxis = hipMidpoint - spineVector;
                                Vector3 forwardVector = Vector3.Cross(spineVector, bodyAxis);
                                Vector3 upVector = Vector3.Cross(spineVector, forwardVector).normalized;
                                forwardVector = Vector3.Cross(upVector, spineVector).normalized;
                                
                                armsArray[i] = forwardVector;
                            }
                            else
                            {
                                armsArray[i] = pose[x];
                            }
                        }

                        Vector3[] armsBez = BezierCurve(armsArray);
                        leadArmsRenderer.positionCount = armsBez.Length;
                        leadArmsRenderer.SetPositions(armsBez);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
    
    public List<Vector3> GetPoseAtFrame(int frameNumber)
    {
        return PosesByFrame[frameNumber];
    }
    
    static LineRenderer NewLineRenderer(float LW)
    {
        GameObject parent = new("line rend");
        LineRenderer lineRenderer = parent.AddComponent<LineRenderer>();
        lineRenderer.material = new Material(Shader.Find("Unlit/Color"));
        lineRenderer.startWidth = LW;
        lineRenderer.endWidth = LW;
        lineRenderer.loop = false;
        lineRenderer.useWorldSpace = false;

        return lineRenderer;
    }
    
    static Vector3[] BezierCurve(Vector3[] points)
    {
        float linearD = 0;
        for (int ii = 0; ii < points.Length - 1; ii++)
        {
            linearD += Vector3.Distance(points[ii], points[ii + 1]);
        }

        int numPts = Convert.ToInt32(.3f * linearD / .03f);
        Vector3[] curvePts = new Vector3[numPts + 1];
        for (int ii = 0; ii <= numPts; ii++)
        {
            curvePts[ii] = BezierPt(points, ii / (float) numPts);
        }

        return curvePts;
    }
    
    static Vector3 BezierPt(Vector3[] points, float t)
    {
        while (true)
        {
            if (points.Length == 1) return points.First();
            Vector3[] lerps = new Vector3[points.Length - 1];
            for (int ii = 0; ii < lerps.Length; ii++)
            {
                lerps[ii] = Vector3.Lerp(points[ii], points[ii + 1], t);
            }

            points = lerps;
        }
    }

}