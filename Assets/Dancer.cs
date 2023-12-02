using System.Collections.Generic;
using Shapes;
using UnityEngine;

public enum Role
{
    Lead = 0,
    Follow = 1
}

public class Dancer
{
    public List<List<Vector3>> PosesByFrame = new();
    public readonly List<Polygon> Joints = new();
    public readonly Role Role;
    
    public Dancer(Role role)
    {
        Role = role;
    }
        
    public void SetPoseToFrame(int frameNumber)
    {
        List<Vector3> pose = PosesByFrame[frameNumber];
        
        for (int i = 0; i < pose.Count; i++)
        {
            Joints[i].transform.localPosition = pose[i];
        }
    }
}