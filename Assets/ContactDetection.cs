using System.Collections.Generic;
using UnityEngine;

public class ContactDetection : MonoBehaviour
{
    Dancer Lead;
    Dancer Follow;
    PoseType poseType;
    
    public void Init(Dancer lead, Dancer follow, PoseType poseType)
    {
        Lead = lead;
        Follow = follow;
        this.poseType = poseType;
    }

    public void DetectContact(int frameNumber)
    {
        List<Vector3> leadPoses = Lead.GetPoseAtFrame(frameNumber);
        List<Vector3> followPoses = Follow.GetPoseAtFrame(frameNumber);
        
        Vector3 leadLeftHandPos = leadPoses[Extensions.GetLeftHand(poseType)];
        Vector3 leadRightHandPos = leadPoses[Extensions.GetRightHand(poseType)];
        
    }

}