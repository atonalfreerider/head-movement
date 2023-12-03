using System.Collections.Generic;
using UnityEngine;

public class ContactDetection : MonoBehaviour
{
    Dancer Lead;
    Dancer Follow;
    
    public void Init(Dancer lead, Dancer follow)
    {
        Lead = lead;
        Follow = follow;
    }

    public void DetectContact(int frameNumber)
    {
        List<Vector3> leadPoses = Lead.GetPoseAtFrame(frameNumber);
        List<Vector3> followPoses = Follow.GetPoseAtFrame(frameNumber);
        
        Vector3 leadLeftWristPos = leadPoses[(int)Dancer.CocoJoint.L_Wrist];
        Vector3 leadRightWristPos = leadPoses[(int)Dancer.CocoJoint.R_Wrist];
        Vector3 leadLeftElbowPos = leadPoses[(int)Dancer.CocoJoint.L_Elbow];
        Vector3 leadRightElbowPos = leadPoses[(int)Dancer.CocoJoint.R_Elbow];
        Vector3 leadLeftShoulderPos = leadPoses[(int)Dancer.CocoJoint.L_Shoulder];
        Vector3 leadRightShoulderPos = leadPoses[(int)Dancer.CocoJoint.R_Shoulder];
        
        Vector3 followLeftWristPos = followPoses[(int)Dancer.CocoJoint.L_Wrist];
        Vector3 followRightWristPos = followPoses[(int)Dancer.CocoJoint.R_Wrist];
        Vector3 followLeftElbowPos = followPoses[(int)Dancer.CocoJoint.L_Elbow];
        Vector3 followRightElbowPos = followPoses[(int)Dancer.CocoJoint.R_Elbow];
        Vector3 followLeftShoulderPos = followPoses[(int)Dancer.CocoJoint.L_Shoulder];
        Vector3 followRightShoulderPos = followPoses[(int)Dancer.CocoJoint.R_Shoulder];
        
        
        
    }

}