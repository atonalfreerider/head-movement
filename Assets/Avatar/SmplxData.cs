using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Readers for the dancecap v3 SMPL-X binaries (dancecap/dancecap/export_unity.py). Both files are little-endian:
/// 4-byte magic, uint32 header length, a JSON header (space-padded to 4 bytes), then raw blocks. Everything is
/// already in Unity coordinates ((x, y, -z) of the Y-up right-handed world; quaternions (-qx, -qy, qz, qw)).
/// The skin is SMPL-X-derived (non-commercial) data generated locally into StreamingAssets - never commit it.
/// </summary>
public static class SmplxData
{
    public const int Joints = 55;

    /// <summary>SMPL-X joint names (smplx.joint_names.JOINT_NAMES[:55]) for readable bone names</summary>
    public static readonly string[] JointNames =
    {
        "pelvis", "left_hip", "right_hip", "spine1", "left_knee", "right_knee", "spine2", "left_ankle", "right_ankle",
        "spine3", "left_foot", "right_foot", "neck", "left_collar", "right_collar", "head", "left_shoulder",
        "right_shoulder", "left_elbow", "right_elbow", "left_wrist", "right_wrist", "jaw", "left_eye_smplhf",
        "right_eye_smplhf", "left_index1", "left_index2", "left_index3", "left_middle1", "left_middle2",
        "left_middle3", "left_pinky1", "left_pinky2", "left_pinky3", "left_ring1", "left_ring2", "left_ring3",
        "left_thumb1", "left_thumb2", "left_thumb3", "right_index1", "right_index2", "right_index3",
        "right_middle1", "right_middle2", "right_middle3", "right_pinky1", "right_pinky2", "right_pinky3",
        "right_ring1", "right_ring2", "right_ring3", "right_thumb1", "right_thumb2", "right_thumb3"
    };

    public class Motion
    {
        public JObject Header;
        public int FrameCount;
        public Vector3[] Transl; // [frame]
        public Quaternion[] Rotations; // [frame * 55 + joint], local
        public Vector3[] JointPositions; // [frame * 55 + joint], world (Unity)
    }

    public class Skin
    {
        public JObject Header;
        public Vector3[] RestVertices;
        public int[] Triangles; // flattened (F * 3), Unity winding
        public float[] Weights; // V * 55 dense
        public Vector3[] RestJoints;
        public int[] Parents; // -1 for the root
    }

    public static Motion ReadMotion(string path)
    {
        (JObject header, byte[] raw, int offset) = Open(path, "DCXM");
        int frames = header.Value<int>("frames");
        int stride = header.Value<int>("frame_floats");
        if (header.Value<int>("joints") != Joints || stride != 3 + Joints * 7)
        {
            throw new InvalidDataException($"{path}: unexpected layout (joints {header["joints"]}, stride {stride})");
        }

        float[] f = Floats(raw, offset, frames * stride, path);
        Motion m = new()
        {
            Header = header, FrameCount = frames, Transl = new Vector3[frames],
            Rotations = new Quaternion[frames * Joints], JointPositions = new Vector3[frames * Joints]
        };
        for (int t = 0; t < frames; t++)
        {
            int o = t * stride;
            m.Transl[t] = new Vector3(f[o], f[o + 1], f[o + 2]);
            for (int j = 0; j < Joints; j++)
            {
                int q = o + 3 + j * 4;
                m.Rotations[t * Joints + j] = new Quaternion(f[q], f[q + 1], f[q + 2], f[q + 3]);
                int p = o + 3 + Joints * 4 + j * 3;
                m.JointPositions[t * Joints + j] = new Vector3(f[p], f[p + 1], f[p + 2]);
            }
        }

        return m;
    }

    public static Skin ReadSkin(string path)
    {
        (JObject header, byte[] raw, int offset) = Open(path, "DCXS");
        int v = header.Value<int>("vertices");
        int faces = header.Value<int>("faces");
        if (header.Value<int>("joints") != Joints) throw new InvalidDataException($"{path}: expected {Joints} joints");

        Skin s = new() { Header = header };
        float[] verts = Floats(raw, offset, v * 3, path);
        offset += v * 12;
        s.Triangles = Ints(raw, offset, faces * 3, path);
        offset += faces * 12;
        s.Weights = Floats(raw, offset, v * Joints, path);
        offset += v * Joints * 4;
        float[] joints = Floats(raw, offset, Joints * 3, path);
        offset += Joints * 12;
        s.Parents = Ints(raw, offset, Joints, path);
        s.RestVertices = ToVectors(verts);
        s.RestJoints = ToVectors(joints);
        return s;
    }

    static (JObject header, byte[] raw, int offset) Open(string path, string magic)
    {
        byte[] raw = File.ReadAllBytes(path);
        if (raw.Length < 8 || Encoding.ASCII.GetString(raw, 0, 4) != magic)
        {
            throw new InvalidDataException($"{path}: not a {magic} file");
        }

        int n = BitConverter.ToInt32(raw, 4);
        JObject header = JObject.Parse(Encoding.UTF8.GetString(raw, 8, n));
        return (header, raw, 8 + n);
    }

    static float[] Floats(byte[] raw, int offset, int count, string path)
    {
        if (offset + count * 4 > raw.Length) throw new InvalidDataException($"{path}: truncated");
        float[] f = new float[count];
        Buffer.BlockCopy(raw, offset, f, 0, count * 4);
        return f;
    }

    static int[] Ints(byte[] raw, int offset, int count, string path)
    {
        if (offset + count * 4 > raw.Length) throw new InvalidDataException($"{path}: truncated");
        int[] f = new int[count];
        Buffer.BlockCopy(raw, offset, f, 0, count * 4);
        return f;
    }

    static Vector3[] ToVectors(float[] f)
    {
        Vector3[] v = new Vector3[f.Length / 3];
        for (int i = 0; i < v.Length; i++) v[i] = new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]);
        return v;
    }
}
