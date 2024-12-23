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
        (int)SmplJoint.Neck
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
    readonly Color darkGrey = new(0.1f, 0.1f, 0.1f);

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
        const float alpha = 1f;
        const float intensity = 3f;
        switch (role)
        {
            case Role.Follow:

                AnimationCurve followLegsCurve = new();
                followLegsCurve.AddKey(new Keyframe(0.0f, 0.01f));
                followLegsCurve.AddKey(new Keyframe(0.5f, 0.03f));
                followLegsCurve.AddKey(new Keyframe(1.0f, 0.01f));
                
                followSpineRenderer = NewLineRenderer(0.01f, BloomMat);
                followSpineRenderer.transform.SetParent(transform, false);
             
                followLegsRenderer = NewLineRenderer(0.01f, BloomMat);
                followLegsRenderer.transform.SetParent(transform, false);

                followLegsRenderer.widthCurve = followLegsCurve;
                
                followShouldersRenderer = NewLineRenderer(0.01f, BloomMat);
                followShouldersRenderer.transform.SetParent(transform, false);
                
                followShouldersRenderer.widthCurve = followLegsCurve;
                
                AnimationCurve followArmCurve = new();
                followArmCurve.AddKey(new Keyframe(0.0f, 0.01f));
                followArmCurve.AddKey(new Keyframe(1.0f, 0.02f));
                
                followLeftArmRenderer = NewLineRenderer(0.01f, BloomMat);
                followLeftArmRenderer.transform.SetParent(transform, false);
                
                followLeftArmRenderer.widthCurve = followArmCurve;
                
                followRightArmRenderer = NewLineRenderer(0.01f, BloomMat);
                followRightArmRenderer.transform.SetParent(transform, false);
                
                followRightArmRenderer.widthCurve = followArmCurve;
                
                break;
            case Role.Lead:
                leadArmsRenderer = NewLineRenderer(0.01f, BloomMat);
                leadArmsRenderer.transform.SetParent(transform, false);

                AnimationCurve leadArmCurve = new();
                leadArmCurve.AddKey(new Keyframe(0.0f, 0.01f));
                leadArmCurve.AddKey(new Keyframe(0.25f, 0.03f));
                leadArmCurve.AddKey(new Keyframe(0.75f, 0.03f));
                leadArmCurve.AddKey(new Keyframe(1.0f, 0.01f));

                leadArmsRenderer.widthCurve = leadArmCurve;
                
                leadLeftLegRenderer = NewLineRenderer(0.01f, BloomMat);
                leadLeftLegRenderer.transform.SetParent(transform, false);
                


                AnimationCurve leadLegCurve = new();
                leadLegCurve.AddKey(new Keyframe(0.0f, 0.01f));
                leadLegCurve.AddKey(new Keyframe(0.25f, 0.03f));
                leadLegCurve.AddKey(new Keyframe(0.75f, 0.05f));
                leadLegCurve.AddKey(new Keyframe(1.0f, 0.03f));

                leadLeftLegRenderer.widthCurve = leadLegCurve;
                
                leadRightLegRenderer = NewLineRenderer(0.01f, BloomMat);
                leadRightLegRenderer.transform.SetParent(transform, false);

                leadRightLegRenderer.widthCurve = leadLegCurve;

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

    public void SetPoseToFrame(int frameNumber, int currentBeat)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];
        const float alpha = 1f;
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
                        
                        Gradient followGradient = new();
                        Color endColor = Color.Lerp(darkGrey, Color.magenta, 0.2f) * Mathf.Pow(2, currentBeat); // intensify on HDR 
                        
                        followGradient.SetKeys(
                            new[]
                            {
                                new GradientColorKey(endColor, 0.0f),
                                new GradientColorKey(endColor, 1.0f)
                            },
                            new[]
                            {
                                new GradientAlphaKey(alpha, 0.0f),
                                new GradientAlphaKey(alpha, 1.0f)
                            }
                        );
                        
                        followSpineRenderer.colorGradient = followGradient;
                        followLegsRenderer.colorGradient = followGradient;
                        followShouldersRenderer.colorGradient = followGradient;
                        followLeftArmRenderer.colorGradient = followGradient;
                        followRightArmRenderer.colorGradient = followGradient;

                        break;
                    }
                    case Role.Lead:
                        Vector3[] armsArray = new Vector3[smplLeadArms.Length];
                        for (int i = 0; i < smplLeadArms.Length; i++)
                        {
                            int x = smplLeadArms[i];
                            if (x == -1)
                            {
                                armsArray[i] = GetChestForward(frameNumber);
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
                        
                        Gradient leadGradient = new();
                        Color color = Color.Lerp(darkGrey, Color.red, 0.2f) * Mathf.Pow(2, currentBeat); // intensify on HDR 
                        leadGradient.SetKeys(
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
                        
                        leadArmsRenderer.colorGradient = leadGradient;
                        leadLeftLegRenderer.colorGradient = leadGradient;
                        leadRightLegRenderer.colorGradient = leadGradient;
                        
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public Vector3 GetLeftHandContact(int frameNumber)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];
        return Vector3.Lerp(pose[(int)SmplJoint.L_Hand], pose[(int)SmplJoint.L_Wrist], 0.5f);
    }
    
    public Vector3 GetRightHandContact(int frameNumber)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];
        return Vector3.Lerp(pose[(int)SmplJoint.R_Hand], pose[(int)SmplJoint.R_Wrist], 0.5f);
    }

    public Vector3 GetLeftForearm(int frameNumber)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];
        return Vector3.Lerp(pose[(int)SmplJoint.L_Wrist], pose[(int)SmplJoint.L_Elbow], 0.5f);
    }
    
    public Vector3 GetRightForearm(int frameNumber)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];
        return Vector3.Lerp(pose[(int)SmplJoint.R_Wrist], pose[(int)SmplJoint.R_Elbow], 0.5f);
    }

    public Vector3 GetLeftElbow(int frameNumber)
    {
        return PosesByFrame[frameNumber][(int)SmplJoint.L_Elbow];
    }
    
    public Vector3 GetRightElbow(int frameNumber)
    {
        return PosesByFrame[frameNumber][(int)SmplJoint.R_Elbow];
    }
    
    public Vector3 GetLeftUpperArm(int frameNumber)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];
        return Vector3.Lerp(pose[(int)SmplJoint.L_Elbow], pose[(int)SmplJoint.L_Shoulder], 0.5f);
    }
    
    public Vector3 GetRightUpperArm(int frameNumber)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];
        return Vector3.Lerp(pose[(int)SmplJoint.R_Elbow], pose[(int)SmplJoint.R_Shoulder], 0.5f);
    }
    
    public Vector3 GetLeftShoulder(int frameNumber)
    {
        return PosesByFrame[frameNumber][(int)SmplJoint.L_Shoulder];
    }
    
    public Vector3 GetRightShoulder(int frameNumber)
    {
        return PosesByFrame[frameNumber][(int)SmplJoint.R_Shoulder];
    }

    public Vector3 GetRightChin(int frameNumber)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];
        return Vector3.Lerp(pose[(int)SmplJoint.Head], pose[(int)SmplJoint.R_Shoulder], 0.15f);
    }
    
    public Vector3 GetLeftChin(int frameNumber)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];
        return Vector3.Lerp(pose[(int)SmplJoint.Head], pose[(int)SmplJoint.L_Shoulder], 0.15f);
    }

    public Vector3 GetRightCollar(int frameNumber)
    {
        return PosesByFrame[frameNumber][(int)SmplJoint.R_Collar];
    }
    
    public Vector3 GetLeftCollar(int frameNumber)
    {
        return PosesByFrame[frameNumber][(int)SmplJoint.L_Collar];
    }

    Vector3 GetChestForward(int frameNumber)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];
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
        return shoulderMidpoint + forwardVector * 0.125f;
    }

    public Vector3 GetNeckNape(int frameNumber)
    {
        Vector3 chestForward = GetChestForward(frameNumber);
        List<Vector3> pose = PosesByFrame[frameNumber];
        Vector3 neck = pose[(int)SmplJoint.Neck];
        return Vector3.LerpUnclamped(chestForward, neck, 1.2f);
    }

    public Vector3 GetRightPectoral(int frameNumber)
    {
        Vector3 chestForward = GetChestForward(frameNumber);
        List<Vector3> pose = PosesByFrame[frameNumber];
        Vector3 rightCollar = pose[(int)SmplJoint.R_Collar];
        return Vector3.Lerp(chestForward, rightCollar, .1f);
    }
    
    public Vector3 GetLeftPectoral(int frameNumber)
    {
        Vector3 chestForward = GetChestForward(frameNumber);
        List<Vector3> pose = PosesByFrame[frameNumber];
        Vector3 leftCollar = pose[(int)SmplJoint.L_Collar];
        return Vector3.Lerp(chestForward, leftCollar, .1f);
    }
    
    public Vector3 GetRightHip(int frameNumber)
    {
        return PosesByFrame[frameNumber][(int)SmplJoint.R_Hip];
    }
    
    public Vector3 GetLeftHip(int frameNumber)
    {
        return PosesByFrame[frameNumber][(int)SmplJoint.L_Hip];
    }
    
    public Vector3 GetRightThigh(int frameNumber)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];
        return Vector3.Lerp(pose[(int)SmplJoint.R_Hip], pose[(int)SmplJoint.R_Knee], 0.5f);
    }
    
    public Vector3 GetLeftThigh(int frameNumber)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];
        return Vector3.Lerp(pose[(int)SmplJoint.L_Hip], pose[(int)SmplJoint.L_Knee], 0.5f);
    }
    
    public Vector3 GetRightKnee(int frameNumber)
    {
        return PosesByFrame[frameNumber][(int)SmplJoint.R_Knee];
    }
    
    public Vector3 GetLeftKnee(int frameNumber)
    {
        return PosesByFrame[frameNumber][(int)SmplJoint.L_Knee];
    }
    
    public Vector3 GetSpine1(int frameNumber)
    {
        return PosesByFrame[frameNumber][(int)SmplJoint.Spine1];
    }
    
    public Vector3 GetSpine2(int frameNumber)
    {
        return PosesByFrame[frameNumber][(int)SmplJoint.Spine2];
    }
    
    public Vector3 GetSpine3(int frameNumber)
    {
        return PosesByFrame[frameNumber][(int)SmplJoint.Spine3];
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

    public Vector3 Center(int frameNumber) => PosesByFrame[frameNumber][(int) SmplJoint.Spine3];
}