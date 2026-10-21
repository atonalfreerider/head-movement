using Unity.Mathematics;
using UnityEngine;

namespace HmSplats
{
    /// <summary>
    /// The posing maths of SPLAT_AVATAR_FORMAT.md section 4, on the CPU: the reference for the compute pass
    /// (Shaders/HM_SkinSplats.compute), the edit-mode tests and the bounds. Unity coordinates throughout.
    ///
    ///   G_0 = the avatar's pelvis bone world transform (rotation + posed pelvis: rest J_0 of the AVATAR skin + transl)
    ///   G_j = G_parent(j) * [R_j | J_j - J_parent(j)]   with J = the splat file's own rest joints (its 10-beta skeleton)
    ///   S_j = G_j * T(-J_j)                               (canonical pose = zero pose, canonical joint world = T(J_j))
    ///   M   = sum_k w_k S_joint_k ;  posed centre = M * mean ;  posed rotation = q(M_3x3) * rotation
    /// </summary>
    public static class SplatSkinning
    {
        public const int Joints = SplatAvatarData.Joints;

        /// <summary>skin matrices from the bones the avatar posed this frame: only bones[0]'s world transform and the
        /// local rotations of bones[1..54] are read (the splat skeleton's own rest offsets replace the avatar's)</summary>
        public static void SkinMatrices(Transform[] bones, float3[] restJoints, int[] parents, Matrix4x4[] skin, Matrix4x4[] world = null)
        {
            world ??= new Matrix4x4[Joints];
            world[0] = bones[0].localToWorldMatrix;
            for (int j = 1; j < Joints; j++)
            {
                int p = parents[j];
                world[j] = world[p] * Matrix4x4.TRS((Vector3)(restJoints[j] - restJoints[p]), bones[j].localRotation, Vector3.one);
            }

            for (int j = 0; j < Joints; j++) skin[j] = world[j] * Matrix4x4.Translate(-(Vector3)restJoints[j]);
        }

        /// <summary>same, from explicit local rotations and the pelvis world transform (tests, offline checks)</summary>
        public static void SkinMatrices(Matrix4x4 pelvisWorld, Quaternion[] localRotations, float3[] restJoints, int[] parents, Matrix4x4[] skin)
        {
            Matrix4x4[] world = new Matrix4x4[Joints];
            world[0] = pelvisWorld;
            for (int j = 1; j < Joints; j++)
            {
                int p = parents[j];
                world[j] = world[p] * Matrix4x4.TRS((Vector3)(restJoints[j] - restJoints[p]), localRotations[j], Vector3.one);
            }

            for (int j = 0; j < Joints; j++) skin[j] = world[j] * Matrix4x4.Translate(-(Vector3)restJoints[j]);
        }

        public static float4x4 Blend(Matrix4x4[] skin, int4 joints, float4 w) =>
            w.x * (float4x4)skin[joints.x] + w.y * (float4x4)skin[joints.y] + w.z * (float4x4)skin[joints.z] + w.w * (float4x4)skin[joints.w];

        /// <summary>posed centre and rotation of Gaussian i</summary>
        public static void Pose(SplatAvatarData d, Matrix4x4[] skin, int i, out float3 position, out float4 rotation)
        {
            float4x4 m = Blend(skin, d.JointIndex[i], d.Weight[i]);
            position = math.mul(m, new float4(d.Mean[i], 1)).xyz;
            rotation = QuatMul(QuatFromMatrix(new float3x3(m.c0.xyz, m.c1.xyz, m.c2.xyz)), d.Rotation[i]);
            rotation = math.normalize(rotation);
        }

        /// <summary>trace-based (Shepperd) rotation of a 3x3 that may be slightly non-orthonormal (a blend of rotations);
        /// xyzw, normalised. The same branches as HM_SkinSplats.compute.</summary>
        public static float4 QuatFromMatrix(float3x3 m)
        {
            float m00 = m.c0.x, m11 = m.c1.y, m22 = m.c2.z;
            float m01 = m.c1.x, m02 = m.c2.x, m10 = m.c0.y, m12 = m.c2.y, m20 = m.c0.z, m21 = m.c1.z;
            float tr = m00 + m11 + m22;
            float4 q;
            if (tr > 0)
            {
                float s = math.sqrt(tr + 1) * 2;
                q = new float4((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = math.sqrt(math.max(1 + m00 - m11 - m22, 1e-12f)) * 2;
                q = new float4(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                float s = math.sqrt(math.max(1 + m11 - m00 - m22, 1e-12f)) * 2;
                q = new float4((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                float s = math.sqrt(math.max(1 + m22 - m00 - m11, 1e-12f)) * 2;
                q = new float4((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
            }

            return math.normalize(q);
        }

        /// <summary>Hamilton product a * b (b applied first), xyzw</summary>
        public static float4 QuatMul(float4 a, float4 b) => new(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        /// <summary>world bounds of the posed centres (every stride-th Gaussian), grown by the largest 3-sigma extent</summary>
        public static Bounds PosedBounds(SplatAvatarData d, Matrix4x4[] skin, int stride = 64, float margin = 0.15f)
        {
            Pose(d, skin, 0, out float3 p0, out _);
            float3 lo = p0, hi = p0;
            for (int i = stride; i < d.Count; i += stride)
            {
                Pose(d, skin, i, out float3 p, out _);
                lo = math.min(lo, p);
                hi = math.max(hi, p);
            }

            return new Bounds((Vector3)((lo + hi) * 0.5f), (Vector3)(hi - lo + 2 * margin));
        }
    }
}
