using System;

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

public enum CocoLimbs
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

public enum SmplLimbs
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

public static class Extensions
{
    public static int GetRightHand(PoseType poseType)
    {
        return poseType switch
        {
            PoseType.Coco => (int)CocoJoint.R_Wrist,
            PoseType.Halpe => (int)Halpe.RWrist,
            PoseType.Smpl => (int)SmplJoint.R_Hand,
            _ => throw new ArgumentOutOfRangeException(nameof(poseType), poseType, null)
        };
    }

    public static int GetLeftHand(PoseType poseType)
    {
        return poseType switch
        {
            PoseType.Coco => (int)CocoJoint.L_Wrist,
            PoseType.Halpe => (int)Halpe.LWrist,
            PoseType.Smpl => (int)SmplJoint.L_Hand,
            _ => throw new ArgumentOutOfRangeException(nameof(poseType), poseType, null)
        };
    }

    public static int GetSmplJointIndex(string jointName)
    {
        return jointName switch
        {
            "Pelvis" => (int)SmplJoint.Pelvis,
            "L_Hip" => (int)SmplJoint.L_Hip,
            "R_Hip" => (int)SmplJoint.R_Hip,
            "Spine1" => (int)SmplJoint.Spine1,
            "L_Knee" => (int)SmplJoint.L_Knee,
            "R_Knee" => (int)SmplJoint.R_Knee,
            "Spine2" => (int)SmplJoint.Spine2,
            "L_Ankle" => (int)SmplJoint.L_Ankle,
            "R_Ankle" => (int)SmplJoint.R_Ankle,
            "Spine3" => (int)SmplJoint.Spine3,
            "L_Foot" => (int)SmplJoint.L_Foot,
            "R_Foot" => (int)SmplJoint.R_Foot,
            "Neck" => (int)SmplJoint.Neck,
            "L_Collar" => (int)SmplJoint.L_Collar,
            "R_Collar" => (int)SmplJoint.R_Collar,
            "Head" => (int)SmplJoint.Head,
            "L_Shoulder" => (int)SmplJoint.L_Shoulder,
            "R_Shoulder" => (int)SmplJoint.R_Shoulder,
            "L_Elbow" => (int)SmplJoint.L_Elbow,
            "R_Elbow" => (int)SmplJoint.R_Elbow,
            "L_Wrist" => (int)SmplJoint.L_Wrist,
            "R_Wrist" => (int)SmplJoint.R_Wrist,
            "L_Hand" => (int)SmplJoint.L_Hand,
            "R_Hand" => (int)SmplJoint.R_Hand,
            _ => throw new ArgumentOutOfRangeException(nameof(jointName), jointName, null)
        };
    }
}