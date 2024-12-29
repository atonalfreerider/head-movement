using System.Collections.Generic;
using UnityEngine;

public class SkeletonColliders : MonoBehaviour
{
    struct CollisionPrimitive
    {
        public Vector3 Center;
        public Vector3 Size;
        public int Type; // 0 = Capsule, 1 = Box, 2 = Sphere
    }

    readonly List<CollisionPrimitive> collisionPrimitives = new();

    public void UpdateColliders(List<Vector3> pose3D)
    {
        collisionPrimitives.Clear();

        // Head (sphere)
        AddSphere(pose3D[(int)SmplJoint.Head], 0.2f); // Larger head sphere

        // Torso (box)
        Vector3 torsoCenter = (pose3D[(int)SmplJoint.Spine2] + pose3D[(int)SmplJoint.Spine1]) * 0.5f;
        Vector3 torsoSize = new(
            Vector3.Distance(pose3D[(int)SmplJoint.L_Shoulder], pose3D[(int)SmplJoint.R_Shoulder]) * 0.6f,
            Vector3.Distance(pose3D[(int)SmplJoint.Spine3], pose3D[(int)SmplJoint.Pelvis]) * 0.6f,
            0.3f // Deeper torso
        );
        AddBox(torsoCenter, torsoSize);

        // Arms and legs (thicker capsules)
        float limbRadius = 0.08f; // Increased from 0.05f
        AddLimbCapsules(pose3D, SmplJoint.L_Shoulder, SmplJoint.L_Elbow, SmplJoint.L_Wrist, limbRadius);
        AddLimbCapsules(pose3D, SmplJoint.R_Shoulder, SmplJoint.R_Elbow, SmplJoint.R_Wrist, limbRadius);
        AddLimbCapsules(pose3D, SmplJoint.L_Hip, SmplJoint.L_Knee, SmplJoint.L_Ankle, limbRadius);
        AddLimbCapsules(pose3D, SmplJoint.R_Hip, SmplJoint.R_Knee, SmplJoint.R_Ankle, limbRadius);
    }

    void AddSphere(Vector3 center, float radius)
    {
        collisionPrimitives.Add(new CollisionPrimitive
        {
            Center = center,
            Size = new Vector3(radius, radius, radius),
            Type = 2
        });
    }

    void AddBox(Vector3 center, Vector3 size)
    {
        collisionPrimitives.Add(new CollisionPrimitive
        {
            Center = center,
            Size = size,
            Type = 1
        });
    }

    void AddLimbCapsules(List<Vector3> pose, SmplJoint joint1, SmplJoint joint2, SmplJoint joint3, float radius)
    {
        // Upper segment
        Vector3 upper = pose[(int)joint1];
        Vector3 middle = pose[(int)joint2];
        AddCapsule(upper, middle, radius);

        // Lower segment
        Vector3 lower = pose[(int)joint3];
        AddCapsule(middle, lower, radius);
    }

    void AddCapsule(Vector3 start, Vector3 end, float radius)
    {
        collisionPrimitives.Add(new CollisionPrimitive
        {
            Center = (start + end) * 0.5f,
            Size = new Vector3(radius, Vector3.Distance(start, end) * 0.5f, 
                Vector3.SignedAngle(Vector3.up, end - start, Vector3.forward)),
            Type = 0
        });
    }

    public Matrix4x4 GetCollisionMatrix()
    {
        // Pack collision data into 4x4 matrix for shader
        // Row 1-3: Each containing data for one primitive
        // Row 4: Count of primitives in x component
        Matrix4x4 matrix = Matrix4x4.identity;
        for (int i = 0; i < Mathf.Min(collisionPrimitives.Count, 3); i++)
        {
            CollisionPrimitive primitive = collisionPrimitives[i];
            matrix.SetRow(i, new Vector4(
                primitive.Center.x, primitive.Center.y, primitive.Center.z,
                primitive.Size.x
            ));
        }
        matrix.SetRow(3, new Vector4(collisionPrimitives.Count, 0, 0, 0));
        return matrix;
    }
}
