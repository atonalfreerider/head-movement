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
    LineRenderer followShouldersRenderer;
    LineRenderer followLeftArmRenderer;
    LineRenderer followRightArmRenderer;
    
    LineRenderer leadArmsRenderer;
    LineRenderer leadLeftLegRenderer;
    LineRenderer leadRightLegRenderer;

    readonly int[] smplFollowSpine =
    {
        (int)SmplJoint.Pelvis,
        (int)SmplJoint.Spine1,
        (int)SmplJoint.Spine2,
        (int)SmplJoint.Spine3,
        (int)SmplJoint.Neck,
        (int)SmplJoint.Head
    };

    readonly int[] smplFollowLegs =
    {
        (int)SmplJoint.L_Ankle,
        0,
        (int)SmplJoint.L_Foot,
        (int)SmplJoint.L_Ankle,
        (int)SmplJoint.L_Knee,
        (int)SmplJoint.L_Hip,
        (int)SmplJoint.R_Hip,
        (int)SmplJoint.R_Knee,
        (int)SmplJoint.R_Ankle,
        (int)SmplJoint.R_Foot,
        -1,
        (int)SmplJoint.R_Ankle
    };

    readonly int[] smplFollowShoulders =
    {
        (int) SmplJoint.L_Shoulder,
        (int) SmplJoint.L_Collar,
        (int) SmplJoint.R_Collar,
        (int) SmplJoint.R_Shoulder
    };

    readonly int[] smplFollowLeftArm =
    {
        (int) SmplJoint.L_Hand,
        (int) SmplJoint.L_Wrist,
        (int) SmplJoint.L_Elbow
    };
    
    readonly int[] smplFollowRightArm =
    {
        (int) SmplJoint.R_Hand,
        (int) SmplJoint.R_Wrist,
        (int) SmplJoint.R_Elbow
    };

    readonly int[] smplLeadArms =
    {
        (int)SmplJoint.L_Hand,
        (int)SmplJoint.L_Wrist,
        (int)SmplJoint.L_Elbow,
        (int)SmplJoint.L_Shoulder,
        -1,
        (int)SmplJoint.R_Shoulder,
        (int)SmplJoint.R_Elbow,
        (int)SmplJoint.R_Wrist,
        (int)SmplJoint.R_Hand
    };

    readonly int[] smplLeadLeftLeg =
    {
        (int)SmplJoint.L_Ankle,
        0,
        (int)SmplJoint.L_Foot,
        (int)SmplJoint.L_Ankle,
        (int)SmplJoint.L_Knee,
        (int)SmplJoint.L_Hip
    };
    
    readonly int[] smplLeadRightLeg =
    {
        (int)SmplJoint.R_Ankle,
        0,
        (int)SmplJoint.R_Foot,
        (int)SmplJoint.R_Ankle,
        (int)SmplJoint.R_Knee,
        (int)SmplJoint.R_Hip
    };

    Material BloomMat;

    public void Init(Role role, List<List<Vector3>> posesByFrame, PoseType poseType, Material bloomMat)
    {
        this.poseType = poseType;
        BloomMat = bloomMat;

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

        const float alpha = 1f;
        const float intensity = 3f;
        switch (role)
        {
            case Role.Follow:
                followSpineRenderer = NewLineRenderer(0.01f, BloomMat);
                followSpineRenderer.transform.SetParent(transform, false);
                
                followLegsRenderer = NewLineRenderer(0.01f, BloomMat);
                followLegsRenderer.transform.SetParent(transform, false);

                Gradient followLegsGrad = new();
                Color endColor = Color.red; // intensify on HDR
                Color midColor = Color.white * Mathf.Pow(2, intensity);

                followLegsGrad.SetKeys(
                    new[]
                    {
                        new GradientColorKey(endColor, 0.0f),
                        new GradientColorKey(midColor, 0.5f),
                        new GradientColorKey(endColor, 1.0f)
                    },
                    new[]
                    {
                        new GradientAlphaKey(alpha, 0.0f),
                        new GradientAlphaKey(alpha, 0.5f),
                        new GradientAlphaKey(alpha, 1.0f)
                    }
                );

                AnimationCurve followLegsCurve = new AnimationCurve();
                followLegsCurve.AddKey(new Keyframe(0.0f, 0.01f));
                followLegsCurve.AddKey(new Keyframe(0.5f, 0.03f));
                followLegsCurve.AddKey(new Keyframe(1.0f, 0.01f));

                followLegsRenderer.widthCurve = followLegsCurve;
                followLegsRenderer.colorGradient = followLegsGrad;
                
                followShouldersRenderer = NewLineRenderer(0.01f, BloomMat);
                followShouldersRenderer.transform.SetParent(transform, false);
                
                followShouldersRenderer.widthCurve = followLegsCurve;
                followShouldersRenderer.colorGradient = followLegsGrad;
                
                AnimationCurve followArmCurve = new AnimationCurve();
                followArmCurve.AddKey(new Keyframe(0.0f, 0.01f));
                followArmCurve.AddKey(new Keyframe(1.0f, 0.02f));
                
                followLeftArmRenderer = NewLineRenderer(0.01f, BloomMat);
                followLeftArmRenderer.transform.SetParent(transform, false);
                
                followLeftArmRenderer.widthCurve = followArmCurve;
                followLeftArmRenderer.colorGradient = followLegsGrad;
                
                followRightArmRenderer = NewLineRenderer(0.01f, BloomMat);
                followRightArmRenderer.transform.SetParent(transform, false);
                
                followRightArmRenderer.widthCurve = followArmCurve;
                followRightArmRenderer.colorGradient = followLegsGrad;
                
                break;
            case Role.Lead:
                leadArmsRenderer = NewLineRenderer(0.01f, BloomMat);
                leadArmsRenderer.transform.SetParent(transform, false);

                Gradient leadArmGradient = new();

                Color color = Color.red; // intensify on HDR 

                leadArmGradient.SetKeys(
                    new[]
                    {
                        new GradientColorKey(color, 0.0f),
                        new GradientColorKey(color, 1.0f)
                    },
                    new[]
                    {
                        new GradientAlphaKey(alpha, 0.0f),
                        new GradientAlphaKey(alpha, 1.0f)
                    }
                );

                AnimationCurve leadArmCurve = new AnimationCurve();
                leadArmCurve.AddKey(new Keyframe(0.0f, 0.01f));
                leadArmCurve.AddKey(new Keyframe(0.25f, 0.03f));
                leadArmCurve.AddKey(new Keyframe(0.75f, 0.03f));
                leadArmCurve.AddKey(new Keyframe(1.0f, 0.01f));

                leadArmsRenderer.widthCurve = leadArmCurve;
                leadArmsRenderer.colorGradient = leadArmGradient;
                
                leadLeftLegRenderer = NewLineRenderer(0.01f, BloomMat);
                leadLeftLegRenderer.transform.SetParent(transform, false);
                
                Gradient leadLegGradient = new();
                
                leadLegGradient.SetKeys(
                    new[]
                    {
                        new GradientColorKey(color, 0.0f),
                        new GradientColorKey(color, 1.0f)
                    },
                    new[]
                    {
                        new GradientAlphaKey(alpha, 0.0f),
                        new GradientAlphaKey(alpha, 1.0f)
                    }
                );

                AnimationCurve leadLegCurve = new AnimationCurve();
                leadLegCurve.AddKey(new Keyframe(0.0f, 0.01f));
                leadLegCurve.AddKey(new Keyframe(0.25f, 0.03f));
                leadLegCurve.AddKey(new Keyframe(0.75f, 0.05f));
                leadLegCurve.AddKey(new Keyframe(1.0f, 0.03f));

                leadLeftLegRenderer.widthCurve = leadLegCurve;
                leadLeftLegRenderer.colorGradient = leadLegGradient;
                
                leadRightLegRenderer = NewLineRenderer(0.01f, BloomMat);
                leadRightLegRenderer.transform.SetParent(transform, false);

                leadRightLegRenderer.widthCurve = leadLegCurve;
                leadRightLegRenderer.colorGradient = leadLegGradient;

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

                        Vector3[] spineBez = CatmullRomSpline.Generate(spineArray);
                        followSpineRenderer.positionCount = spineBez.Length;
                        followSpineRenderer.SetPositions(spineBez);

                        Vector3[] legsArray = new Vector3[smplFollowLegs.Length];
                        for (int i = 0; i < smplFollowLegs.Length; i++)
                        {
                            int x = smplFollowLegs[i];
                            legsArray[i] = x switch
                            {
                                (int)SmplJoint.L_Hip => Vector3.LerpUnclamped(pose[(int)SmplJoint.R_Hip], pose[x],
                                    1.5f),
                                (int)SmplJoint.R_Hip => Vector3.LerpUnclamped(pose[(int)SmplJoint.L_Hip], pose[x],
                                    1.5f),
                                0 => Vector3.LerpUnclamped(pose[(int)SmplJoint.L_Knee], pose[(int)SmplJoint.L_Ankle],
                                    1.2f),
                                -1 => Vector3.LerpUnclamped(pose[(int)SmplJoint.R_Knee], pose[(int)SmplJoint.R_Ankle],
                                    1.2f),
                                _ => pose[x]
                            };
                        }

                        Vector3[] legsBez = CatmullRomSpline.Generate(legsArray);

                        followLegsRenderer.positionCount = legsBez.Length;
                        followLegsRenderer.SetPositions(legsBez);
                        
                        Vector3[] shoulderArray = new Vector3[smplFollowShoulders.Length];
                        for (int i = 0; i < smplFollowShoulders.Length; i++)
                        {
                            shoulderArray[i] = pose[smplFollowShoulders[i]];
                        }    
                        
                        Vector3[] shoulderBez = CatmullRomSpline.Generate(shoulderArray);
                        followShouldersRenderer.positionCount = shoulderBez.Length;
                        followShouldersRenderer.SetPositions(shoulderBez);
                        
                        Vector3[] leftArmArray = new Vector3[smplFollowLeftArm.Length];
                        for (int i = 0; i < smplFollowLeftArm.Length; i++)
                        {
                            leftArmArray[i] = pose[smplFollowLeftArm[i]];
                        }
                        
                        Vector3[] leftArmBez = CatmullRomSpline.Generate(leftArmArray);
                        followLeftArmRenderer.positionCount = leftArmBez.Length;
                        followLeftArmRenderer.SetPositions(leftArmBez);
                        
                        Vector3[] rightArmArray = new Vector3[smplFollowRightArm.Length];
                        for (int i = 0; i < smplFollowRightArm.Length; i++)
                        {
                            rightArmArray[i] = pose[smplFollowRightArm[i]];
                        }
                        
                        Vector3[] rightArmBez = CatmullRomSpline.Generate(rightArmArray);
                        followRightArmRenderer.positionCount = rightArmBez.Length;
                        followRightArmRenderer.SetPositions(rightArmBez);

                        break;
                    }
                    case Role.Lead:
                        Vector3[] armsArray = new Vector3[smplLeadArms.Length];
                        for (int i = 0; i < smplLeadArms.Length; i++)
                        {
                            int x = smplLeadArms[i];
                            if (x == -1)
                            {
                                Vector3 lShoulder = pose[(int)SmplJoint.L_Shoulder];
                                Vector3 rShoulder = pose[(int)SmplJoint.R_Shoulder];
                                Vector3 shoulderMidpoint = Vector3.Lerp(lShoulder, rShoulder, 0.5f);
                                Vector3 hipMidpoint = pose[(int)SmplJoint.Pelvis];

                                // Calculate the body axis and forward vector based on the person's orientation
                                Vector3 bodyAxis = hipMidpoint - shoulderMidpoint;
                                Vector3 rightVector =
                                    rShoulder - lShoulder; // Right direction from left to right shoulder
                                Vector3 forwardVector = Vector3.Cross(bodyAxis, rightVector).normalized;

                                // Ensure the forward vector is perpendicular to the plane of the shoulders and hips
                                Vector3 upVector = Vector3.Cross(rightVector, forwardVector).normalized;
                                forwardVector = Vector3.Cross(upVector, rightVector).normalized;

                                // Offset the shoulder midpoint by the forward vector to place the point 0.125m in front of the chest
                                armsArray[i] = shoulderMidpoint + forwardVector * 0.125f;
                            }
                            else
                            {
                                armsArray[i] = pose[x];
                            }
                        }

                        Vector3[] armsBez = CatmullRomSpline.Generate(armsArray);
                        leadArmsRenderer.positionCount = armsBez.Length;
                        leadArmsRenderer.SetPositions(armsBez);
                        
                        // LEFT LEG
                        Vector3[] leftLegArray = new Vector3[smplLeadLeftLeg.Length];
                        for (int i = 0; i < smplLeadLeftLeg.Length; i++)
                        {
                            int x = smplLeadLeftLeg[i];
                            leftLegArray[i] = x switch
                            {
                                0 => Vector3.LerpUnclamped(pose[(int)SmplJoint.L_Knee], pose[(int)SmplJoint.L_Ankle],
                                    1.2f),
                                (int)SmplJoint.L_Hip => Vector3.LerpUnclamped(pose[(int)SmplJoint.R_Hip], pose[x], 1.2f),
                                _ => pose[x]
                            };
                        }

                        Vector3[] leftLegBez = CatmullRomSpline.Generate(leftLegArray);

                        leadLeftLegRenderer.positionCount = leftLegBez.Length;
                        leadLeftLegRenderer.SetPositions(leftLegBez);
                        
                        // RIGHT LEG
                        Vector3[] rightLegArray = new Vector3[smplLeadRightLeg.Length];
                        for (int i = 0; i < smplLeadRightLeg.Length; i++)
                        {
                            int x = smplLeadRightLeg[i];
                            rightLegArray[i] = x switch
                            {
                                0 => Vector3.LerpUnclamped(pose[(int)SmplJoint.L_Knee], pose[(int)SmplJoint.R_Ankle],
                                    1.2f),
                                (int)SmplJoint.R_Hip => Vector3.LerpUnclamped(pose[(int)SmplJoint.L_Hip], pose[x], 1.2f),
                                _ => pose[x]
                            };
                        }

                        Vector3[] rightLegBez = CatmullRomSpline.Generate(rightLegArray);

                        leadRightLegRenderer.positionCount = rightLegBez.Length;
                        leadRightLegRenderer.SetPositions(rightLegBez);
                        
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

    static LineRenderer NewLineRenderer(float LW, Material mat)
    {
        GameObject parent = new("line rend");
        LineRenderer lineRenderer = parent.AddComponent<LineRenderer>();
        lineRenderer.material = mat;
        lineRenderer.startWidth = LW;
        lineRenderer.endWidth = LW;
        lineRenderer.loop = false;
        lineRenderer.useWorldSpace = false;

        return lineRenderer;
    }
}