using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Util;

public enum Role
{
    Lead = 0,
    Follow = 1
}

public class Dancer : MonoBehaviour
{
    List<Vector3>[] PosesByFrame;
    Role Role;
    Color[] colorSpectrum;
    
    Material BloomMat;
    HairSimulation hairSimulation;
    readonly Dictionary<SmplJoint, float[]> jerkByFrameByJoint = new();

    LineRenderer followSpineRenderer;
    LineRenderer followLegsRenderer;
    LineRenderer followShouldersRenderer;
    LineRenderer followLeftArmRenderer;
    LineRenderer followRightArmRenderer;

    LineRenderer leadArmsRenderer;
    LineRenderer leadLeftLegRenderer;
    LineRenderer leadRightLegRenderer;

    #region LIMB DEFINITIONS
    
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
        (int)SmplJoint.L_Shoulder,
        (int)SmplJoint.L_Collar,
        (int)SmplJoint.R_Collar,
        (int)SmplJoint.R_Shoulder
    };

    readonly int[] smplFollowLeftArm =
    {
        (int)SmplJoint.L_Hand,
        (int)SmplJoint.L_Wrist,
        (int)SmplJoint.L_Elbow
    };

    readonly int[] smplFollowRightArm =
    {
        (int)SmplJoint.R_Hand,
        (int)SmplJoint.R_Wrist,
        (int)SmplJoint.R_Elbow
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
    
    #endregion

    /// <param name="frameTimes">seconds of every pose frame (real timestamps; only differences matter)</param>
    /// <param name="precomputedJerk">optional |jerk| per frame per SMPL-24 joint (timing.json spline jerk); when
    /// null or the wrong size, jerk comes from central differences on frameTimes</param>
    /// <param name="legacyHair">the original LineRenderer HairSimulation on the follow (v1/v2 captures). v3 captures
    /// pass false: their skinned avatar carries the groomed hair (Assets/Hair/HairStrands)</param>
    public void Init(Role role, List<List<Vector3>> posesByFrame, Material bloomMat, float[] frameTimes,
        float[][] precomputedJerk = null, bool legacyHair = true)
    {
        BloomMat = bloomMat;
        BuildSmpl(role);

        Role = role;
        PosesByFrame = posesByFrame.Take(frameTimes.Length).ToArray();
        JerkSource = "central differences";

        int jointCount = Enum.GetNames(typeof(SmplJoint)).Length;
        bool usePrecomputed = precomputedJerk != null && precomputedJerk.Length >= PosesByFrame.Length &&
                              precomputedJerk.Take(PosesByFrame.Length).All(f => f != null && f.Length >= jointCount);
        if (usePrecomputed) JerkSource = "timing.json spline";
        float[] times = frameTimes.Take(PosesByFrame.Length).ToArray();

        for (int j = 0; j < jointCount; j++)
        {
            SmplJoint joint = (SmplJoint)j;
            if (usePrecomputed)
            {
                int jj = j;
                jerkByFrameByJoint[joint] = precomputedJerk.Take(PosesByFrame.Length).Select(f => f[jj]).ToArray();
                continue;
            }

            Vector3[] jointPositions = PosesByFrame.Select(pose => pose[j]).ToArray();
            jerkByFrameByJoint[joint] = RhythmPhysics.CalculateJerk(jointPositions, times);
        }

        const int colorScale = 40;
        switch (role)
        {
            case Role.Follow:
            {
                colorSpectrum = new Color[colorScale];
                for (int i = 0; i < colorSpectrum.Length; i++)
                {
                    float multiplier = (float)(i + 1);
                    colorSpectrum[i] = new Color(.2f, .2f, .2f) * multiplier;
                }

                if (legacyHair)
                {
                    hairSimulation = new GameObject("Hair Simulation").AddComponent<HairSimulation>();
                    hairSimulation.transform.SetParent(transform, false);
                }

                break;
            }
            case Role.Lead:
            {
                colorSpectrum = new Color[colorScale];
                for (int i = 0; i < colorSpectrum.Length; i++)
                {
                    float multiplier = (float)(i + 1);
                    colorSpectrum[i] = new Color(0.2f, 0.01f, 0.0f) * multiplier;
                }

                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(role), role, null);
        }
    }

    void BuildSmpl(Role role)
    {
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

        AddSkeletonStencil(role);
    }

    static readonly Material[] skeletonStencil = new Material[2];
    static bool skeletonStencilOn;

    /// <summary>VIEWER_SPEC 3.3 option (SmplxAvatar.SkeletonsOverBodies): the stencil bit each dancer's skeleton lines
    /// write (lead 1, follow 2); SmplxAvatar's translucent colour pass then skips the pixels with its own dancer's bit.</summary>
    public static int SkeletonStencilBit(Role role) => 1 << ((int)role & 1);

    /// <summary>draw the skeleton stencil markers (SmplxAvatar.SkeletonsOverBodies switches this); off = their pass is
    /// disabled, so they cost no draw calls</summary>
    public static void EnableSkeletonStencil(bool on)
    {
        skeletonStencilOn = on;
        foreach (Material m in skeletonStencil)
        {
            if (m != null) m.SetShaderPassEnabled("SRPDefaultUnlit", on);
        }
    }

    static Material SkeletonStencilMaterial(Role role)
    {
        int i = (int)role & 1;
        if (skeletonStencil[i] != null) return skeletonStencil[i];
        Shader shader = Resources.Load<Shader>("HM_SkeletonStencil");
        if (shader == null) shader = Shader.Find("HeadMovement/SkeletonStencil");
        if (shader == null) return null;
        Material m = new(shader) { name = $"{role} skeleton stencil", renderQueue = 2001 }; // right after the opaque lines
        m.SetFloat("_SkelRef", SkeletonStencilBit(role));
        m.SetShaderPassEnabled("SRPDefaultUnlit", skeletonStencilOn);
        skeletonStencil[i] = m;
        return m;
    }

    /// <summary>a second material on every skeleton line: same mesh and culling, writes only this dancer's stencil bit
    /// where the line is visible (Resources/HM_SkeletonStencil)</summary>
    void AddSkeletonStencil(Role role)
    {
        Material marker = SkeletonStencilMaterial(role);
        if (marker == null) return;
        LineRenderer[] lines =
        {
            followSpineRenderer, followLegsRenderer, followShouldersRenderer, followLeftArmRenderer, followRightArmRenderer,
            leadArmsRenderer, leadLeftLegRenderer, leadRightLegRenderer
        };
        foreach (LineRenderer line in lines)
        {
            if (line == null) continue;
            Material glow = line.sharedMaterial;
            line.sharedMaterials = new[] { glow, marker };
        }
    }

    public void SetPoseToFrame(int frameNumber, float beatIntensity)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];

        switch (Role)
        {
            case Role.Follow:
            {
                if (hairSimulation != null)
                {
                    Vector3 head = pose[(int)SmplJoint.Head];
                    hairSimulation.transform.position = head;
                    hairSimulation.transform.LookAt(GetNose(frameNumber));
                    hairSimulation.transform.Rotate(Vector3.right, -60f);
                    hairSimulation.transform.Rotate(Vector3.up, 180f);

                    hairSimulation.Init(BloomMat);
                }

                Vector3[] spineArray = new Vector3[smplFollowSpine.Length];
                for (int i = 0; i < smplFollowSpine.Length; i++)
                {
                    spineArray[i] = pose[smplFollowSpine[i]];
                }

                Vector3[] spineBez = CatmullRomSpline.Generate(spineArray);
                followSpineRenderer.positionCount = spineBez.Length;
                followSpineRenderer.SetPositions(spineBez);
                
                (GradientColorKey[] followSpineColorKeys, GradientAlphaKey[] followSpineAlphaKeys) = IntensityGradient(
                    frameNumber, beatIntensity, smplFollowSpine,
                    Array.Empty<int>());

                followSpineRenderer.colorGradient = new Gradient
                {
                    colorKeys = followSpineColorKeys,
                    alphaKeys = followSpineAlphaKeys
                };

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
                
                (GradientColorKey[] followLegsColorKeys, GradientAlphaKey[] followLegsAlphaKeys) = IntensityGradient(
                    frameNumber, beatIntensity, smplFollowLegs,
                    new[] {0, -1, (int)SmplJoint.L_Ankle, (int)SmplJoint.R_Ankle});

                followLegsRenderer.colorGradient = new Gradient
                {
                    colorKeys = followLegsColorKeys,
                    alphaKeys = followLegsAlphaKeys
                };

                Vector3[] shoulderArray = new Vector3[smplFollowShoulders.Length];
                for (int i = 0; i < smplFollowShoulders.Length; i++)
                {
                    shoulderArray[i] = pose[smplFollowShoulders[i]];
                }

                Vector3[] shoulderBez = CatmullRomSpline.Generate(shoulderArray);
                followShouldersRenderer.positionCount = shoulderBez.Length;
                followShouldersRenderer.SetPositions(shoulderBez);
                
                (GradientColorKey[] followShouldersColorKeys, GradientAlphaKey[] followShouldersAlphaKeys) = IntensityGradient(
                    frameNumber, beatIntensity, smplFollowShoulders, Array.Empty<int>() );

                followShouldersRenderer.colorGradient = new Gradient
                {
                    colorKeys = followShouldersColorKeys,
                    alphaKeys = followShouldersAlphaKeys
                };

                Vector3[] leftArmArray = new Vector3[smplFollowLeftArm.Length];
                for (int i = 0; i < smplFollowLeftArm.Length; i++)
                {
                    leftArmArray[i] = pose[smplFollowLeftArm[i]];
                }

                Vector3[] leftArmBez = CatmullRomSpline.Generate(leftArmArray);
                followLeftArmRenderer.positionCount = leftArmBez.Length;
                followLeftArmRenderer.SetPositions(leftArmBez);
                
                (GradientColorKey[] followLeftArmColorKeys, GradientAlphaKey[] followLeftArmAlphaKeys) = IntensityGradient(
                    frameNumber, beatIntensity, smplFollowLeftArm, Array.Empty<int>() );

                followLeftArmRenderer.colorGradient = new Gradient
                {
                    colorKeys = followLeftArmColorKeys,
                    alphaKeys = followLeftArmAlphaKeys
                };

                Vector3[] rightArmArray = new Vector3[smplFollowRightArm.Length];
                for (int i = 0; i < smplFollowRightArm.Length; i++)
                {
                    rightArmArray[i] = pose[smplFollowRightArm[i]];
                }

                Vector3[] rightArmBez = CatmullRomSpline.Generate(rightArmArray);
                followRightArmRenderer.positionCount = rightArmBez.Length;
                followRightArmRenderer.SetPositions(rightArmBez);
                
                (GradientColorKey[] followRightArmColorKeys, GradientAlphaKey[] followRightArmAlphaKeys) = IntensityGradient(
                    frameNumber, beatIntensity, smplFollowRightArm, Array.Empty<int>() );

                followRightArmRenderer.colorGradient = new Gradient
                {
                    colorKeys = followRightArmColorKeys,
                    alphaKeys = followRightArmAlphaKeys
                };
                
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

                (GradientColorKey[] leadLeftLegColorKeys, GradientAlphaKey[] leadLeftLegAlphaKeys) = IntensityGradient(
                    frameNumber,
                    beatIntensity, 
                    smplLeadLeftLeg,
                    new[] {0});

                leadLeftLegRenderer.colorGradient = new Gradient
                {
                    colorKeys = leadLeftLegColorKeys,
                    alphaKeys = leadLeftLegAlphaKeys
                };

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

                (GradientColorKey[] leadRightLegColorKeys, GradientAlphaKey[] leadRightLegAlphaKeys) = IntensityGradient(
                    frameNumber,
                    beatIntensity, 
                    smplLeadRightLeg,
                    new[] {0});

                leadRightLegRenderer.colorGradient = new Gradient
                {
                    colorKeys = leadRightLegColorKeys,
                    alphaKeys = leadRightLegAlphaKeys
                };

                (GradientColorKey[] leadArmsColorKeys, GradientAlphaKey[] leadArmsAlphaKeys) = IntensityGradient(
                    frameNumber,
                    beatIntensity, 
                    smplLeadArms,
                    new[] {-1});

                leadArmsRenderer.colorGradient = new Gradient
                {
                    colorKeys = leadArmsColorKeys,
                    alphaKeys = leadArmsAlphaKeys
                };

                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    /// <summary>
    /// for each of the smpl joints in the legs, from left foot to right foot, through the hips retrieve the jerk. if
    /// the current beat is 1 or 2, intensify the color of the line renderer at that point along the line renderer
    /// </summary>
    Tuple<GradientColorKey[], GradientAlphaKey[]> IntensityGradient(int frameNumber, float intensity, int[] limbArray, int[] exclusions)
    {
        List<GradientColorKey> colorKeys = new();
        GradientAlphaKey[] alphas = { new(1, 0), new(1, 1) };

        for (int i = 0; i < limbArray.Length; i++)
        {
            int x = limbArray[i];
            if (exclusions.Contains(x)) continue;
            float jerk = jerkByFrameByJoint[(SmplJoint)x][frameNumber] / 200; // jerk can be as high as 8000
            float jerkIntensity = jerk * intensity; // 0 to 40 x 0 to 5
            // get a color index from 0 to 39 based on the jerk intensity
            int colorIndex = Mathf.RoundToInt(jerkIntensity);
            if (colorIndex >= colorSpectrum.Length)
            {
                colorIndex = colorSpectrum.Length - 1;
            }
            Color gradColor =  colorSpectrum[colorIndex];
            colorKeys.Add(new GradientColorKey(gradColor, i / (float)(limbArray.Length - exclusions.Length)));
        }

        return new Tuple<GradientColorKey[], GradientAlphaKey[]>(colorKeys.ToArray(), alphas);
    }

    #region GETTERS
    
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

    Vector3 GetNose(int frameNumber)
    {
        // Grab the relevant joints from your tracked pose
        List<Vector3> pose = PosesByFrame[frameNumber];
        Vector3 head = pose[(int)SmplJoint.Head];

        // chestForward is a WORLD-SPACE point in front of the chest,
        // so let's create a forward direction from the head to that point.
        Vector3 chestForwardPoint = GetChestForward(frameNumber);
        Vector3 forwardDir = (chestForwardPoint - head).normalized;

        // Place the nose 0.2m in front of the head
        return head + forwardDir * 0.2f;
    }
    
    #endregion

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

    public Vector3 Center(int frameNumber) => PosesByFrame[frameNumber][(int)SmplJoint.Spine3];

    public int FrameCount => PosesByFrame.Length;

    /// <summary>where the per-joint jerk driving the glow came from (for hm_state)</summary>
    public string JerkSource { get; private set; }

    /// <summary>true when the original LineRenderer hair is attached (v1/v2 captures only)</summary>
    public bool HasLegacyHair => hairSimulation != null;

    public Role DancerRole => Role;

    public Vector3 Joint(int frameNumber, SmplJoint joint) => PosesByFrame[frameNumber][(int)joint];

    /// <summary>
    /// Mass-weighted torso centre (pelvis/spine), a rough centre of mass for connection physics
    /// </summary>
    public Vector3 CenterOfMass(int frameNumber)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];
        return pose[(int)SmplJoint.Pelvis] * 0.4f + pose[(int)SmplJoint.Spine1] * 0.3f +
               pose[(int)SmplJoint.Spine2] * 0.2f + pose[(int)SmplJoint.Spine3] * 0.1f;
    }
}