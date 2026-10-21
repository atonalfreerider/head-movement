using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace HmSplats.Tests
{
    /// <summary>Edit-mode tests of the splat-avatar reader, the posing maths and the GPU skinning pass
    /// (SPLAT_AVATAR_FORMAT.md v1). Synthetic data only, plus a load check of every capture's splats/*.json present in StreamingAssets (git-ignored data).</summary>
    public class SplatAvatarTests
    {
        static readonly int[] SmplxParents =
        {
            -1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19, 15, 15, 15, 20, 25, 26, 20, 28, 29,
            20, 31, 32, 20, 34, 35, 20, 37, 38, 21, 40, 41, 21, 43, 44, 21, 46, 47, 21, 49, 50, 21, 52, 53
        };

        static float3[] RestJoints()
        {
            float3[] j = new float3[55];
            for (int k = 0; k < 55; k++)
            {
                int p = SmplxParents[k];
                j[k] = p < 0 ? new float3(0.0f, 0.95f, 0.0f) : j[p] + new float3(0.03f * ((k % 5) - 2), k % 2 == 0 ? 0.08f : -0.06f, 0.01f * (k % 3));
            }

            return j;
        }

        /// <summary>a synthetic json + splatav pair in memory: n Gaussians on random joints</summary>
        static (byte[] raw, JObject header) Synthetic(int n, int seed = 7)
        {
            System.Random rng = new(seed);
            float3[] rest = RestJoints();
            byte[] raw = new byte[SplatAvatarData.HeaderBytes + n * SplatAvatarData.RecordBytes];
            using (BinaryWriter w = new(new MemoryStream(raw)))
            {
                w.Write((byte)'S'); w.Write((byte)'P'); w.Write((byte)'A'); w.Write((byte)'V');
                w.Write(1u); w.Write((uint)n); w.Write(120u); w.Write(0u); w.Write(1u); w.Write(64u);
                w.Seek(64, SeekOrigin.Begin);
                for (int i = 0; i < n; i++)
                {
                    int j0 = rng.Next(55), j1 = SmplxParents[j0] < 0 ? j0 : SmplxParents[j0];
                    float a = (float)rng.NextDouble();
                    float3 m = rest[j0] + new float3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 0.1f;
                    quaternion q = quaternion.Euler((float)rng.NextDouble() * 3, (float)rng.NextDouble() * 3, (float)rng.NextDouble() * 3);
                    w.Write(m.x); w.Write(m.y); w.Write(m.z);
                    w.Write(q.value.x); w.Write(q.value.y); w.Write(q.value.z); w.Write(q.value.w);
                    w.Write(-5f); w.Write(-5.5f); w.Write(-6f);
                    w.Write(1.5f);
                    w.Write(0.1f); w.Write(-0.2f); w.Write(0.3f);
                    w.Write((ushort)j0); w.Write((ushort)j1); w.Write((ushort)0); w.Write((ushort)0);
                    w.Write(a); w.Write(1 - a); w.Write(0f); w.Write(0f);
                    w.Write((byte)(i % 10 == 0 ? 1 : 0)); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
                    w.Write(-1); w.Write(-1);
                    w.Write(0f); w.Write(0f); w.Write(0f);
                    w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f);
                }
            }

            JArray restJson = new(), cjw = new();
            for (int k = 0; k < 55; k++)
            {
                restJson.Add(new JArray(rest[k].x, rest[k].y, rest[k].z));
                cjw.Add(new JArray(1, 0, 0, rest[k].x, 0, 1, 0, rest[k].y, 0, 0, 1, rest[k].z, 0, 0, 0, 1));
            }

            JObject h = new()
            {
                ["format"] = "dancecap splat avatar", ["version"] = 1, ["role"] = "lead", ["count"] = n,
                ["coords"] = "unity (x, y, -z) of the Y-up right-handed dancecap world",
                ["rest_joints"] = restJson, ["parents"] = new JArray(SmplxParents), ["canonical_joint_world"] = cjw
            };
            return (raw, h);
        }

        static Matrix4x4[] RestSkin(SplatAvatarData d, Quaternion[] local = null)
        {
            Matrix4x4[] skin = new Matrix4x4[55];
            local ??= new Quaternion[55];
            for (int j = 0; j < 55; j++)
            {
                Quaternion q = local[j];
                if (q.x == 0 && q.y == 0 && q.z == 0 && q.w == 0) local[j] = Quaternion.identity; // Quaternion == is a dot test
            }

            SplatSkinning.SkinMatrices(Matrix4x4.TRS((Vector3)d.RestJoints[0], local[0], Vector3.one), local, d.RestJoints, d.Parents, skin);
            return skin;
        }

        [Test]
        public void Reader_ParsesRecordsAndNormalisesWeights()
        {
            (byte[] raw, JObject h) = Synthetic(500);
            SplatAvatarData d = SplatAvatarData.Parse(raw, h);
            Assert.AreEqual(500, d.Count);
            Assert.AreEqual(50, d.HairCount);
            Assert.AreEqual(0, d.BoundHairCount);
            for (int i = 0; i < d.Count; i++)
            {
                Assert.AreEqual(1f, math.csum(d.Weight[i]), 1e-5f);
                Assert.AreEqual(1f, math.length(d.Rotation[i]), 1e-4f);
            }
        }

        [Test]
        public void Reader_RejectsBadMagicAndCount()
        {
            (byte[] raw, JObject h) = Synthetic(10);
            byte[] bad = (byte[])raw.Clone();
            bad[0] = (byte)'X';
            Assert.Throws<InvalidDataException>(() => SplatAvatarData.Parse(bad, h));
            h["count"] = 11;
            Assert.Throws<InvalidDataException>(() => SplatAvatarData.Parse(raw, h));
        }

        [Test]
        public void Reader_RejectsNonZeroCanonicalPose()
        {
            (byte[] raw, JObject h) = Synthetic(10);
            ((JArray)h["canonical_joint_world"])[3][0] = 0.5f; // a rotated joint: not the v1 zero canonical pose
            Assert.Throws<InvalidDataException>(() => SplatAvatarData.Parse(raw, h));
        }

        [Test]
        public void Pose_CanonicalPoseReturnsTheCanonicalMeans()
        {
            (byte[] raw, JObject h) = Synthetic(300);
            SplatAvatarData d = SplatAvatarData.Parse(raw, h);
            Matrix4x4[] skin = RestSkin(d);
            for (int i = 0; i < d.Count; i++)
            {
                SplatSkinning.Pose(d, skin, i, out float3 p, out float4 q);
                Assert.Less(math.length(p - d.Mean[i]), 1e-5f);
                Assert.Greater(math.abs(math.dot(q, d.Rotation[i])), 1 - 1e-5f);
            }
        }

        [Test]
        public void Pose_SingleJointRotationMovesAboutThatJoint()
        {
            (byte[] raw, JObject h) = Synthetic(50);
            SplatAvatarData d = SplatAvatarData.Parse(raw, h);
            // one Gaussian rigidly on the left elbow (18), 10 cm off the joint along +x
            d.JointIndex[0] = new int4(18, 0, 0, 0);
            d.Weight[0] = new float4(1, 0, 0, 0);
            d.Mean[0] = d.RestJoints[18] + new float3(0.1f, 0, 0);
            d.Rotation[0] = new float4(0, 0, 0, 1);
            Quaternion[] local = new Quaternion[55];
            local[18] = Quaternion.AngleAxis(90, Vector3.up);
            Matrix4x4[] skin = RestSkin(d, local);
            SplatSkinning.Pose(d, skin, 0, out float3 p, out float4 q);
            Vector3 expect = (Vector3)d.RestJoints[18] + Quaternion.AngleAxis(90, Vector3.up) * new Vector3(0.1f, 0, 0);
            Assert.Less(((Vector3)p - expect).magnitude, 1e-5f);
            Quaternion r = Quaternion.AngleAxis(90, Vector3.up);
            Assert.Greater(math.abs(math.dot(q, new float4(r.x, r.y, r.z, r.w))), 1 - 1e-5f);
        }

        [Test]
        public void QuatFromMatrix_MatchesUnity()
        {
            System.Random rng = new(3);
            for (int k = 0; k < 200; k++)
            {
                Quaternion r = Quaternion.Euler((float)rng.NextDouble() * 360, (float)rng.NextDouble() * 360, (float)rng.NextDouble() * 360);
                float3x3 m = new float3x3(new quaternion(r.x, r.y, r.z, r.w));
                float4 q = SplatSkinning.QuatFromMatrix(m);
                Assert.Greater(math.abs(math.dot(q, new float4(r.x, r.y, r.z, r.w))), 1 - 1e-5f, $"case {k}");
            }
        }

        [Test]
        public void GpuSkinning_MatchesTheCpuReference()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("no compute shaders on this device");
            ComputeShader cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(SkinnedSplatAvatar.ComputePath);
            Assert.NotNull(cs, SkinnedSplatAvatar.ComputePath);
            (byte[] raw, JObject h) = Synthetic(2000);
            SplatAvatarData d = SplatAvatarData.Parse(raw, h);

            // a posed bone hierarchy (rest offsets from the data, random local rotations)
            GameObject rootGo = new("splat test bones");
            SkinnedSplatAvatar s = null;
            try
            {
                Transform[] bones = new Transform[55];
                System.Random rng = new(11);
                for (int j = 0; j < 55; j++)
                {
                    int p = d.Parents[j];
                    bones[j] = new GameObject($"b{j}").transform;
                    bones[j].SetParent(p < 0 ? rootGo.transform : bones[p], false);
                    bones[j].localPosition = (Vector3)(p < 0 ? d.RestJoints[j] + new float3(0.3f, 0, -0.2f) : d.RestJoints[j] - d.RestJoints[p]);
                    bones[j].localRotation = Quaternion.Euler((float)rng.NextDouble() * 60 - 30, (float)rng.NextDouble() * 60 - 30, (float)rng.NextDouble() * 60 - 30);
                }

                rootGo.transform.SetPositionAndRotation(new Vector3(1, 0, 2), Quaternion.Euler(0, 40, 0));
                s = SkinnedSplatAvatar.Create(d, bones, null, "gpu test splat", cs);
                s.SkinNow(true);
                float4[] g = s.ReadBack(0, d.Count);
                float worst = 0, worstRot = 0;
                for (int i = 0; i < d.Count; i++)
                {
                    SplatSkinning.Pose(d, s.CurrentSkin, i, out float3 p, out float4 q);
                    worst = math.max(worst, math.length(g[i * 3].xyz - p));
                    worstRot = math.max(worstRot, 1 - math.abs(math.dot(math.normalize(g[i * 3 + 2]), q)));
                    Assert.AreEqual(1f / (1f + math.exp(-d.OpacityLogit[i])), g[i * 3].w, 1e-5f);
                    Assert.Less(math.length(g[i * 3 + 1].xyz - math.exp(d.LogScale[i])), 1e-6f);
                }

                Assert.Less(worst, 1e-5f, "posed centres GPU vs CPU (m)");
                Assert.Less(worstRot, 1e-5f, "posed rotations GPU vs CPU (1 - |dot|)");

                // the pelvis world transform is what the avatar's bone 0 says
                Assert.Less((s.CurrentSkin[0].MultiplyPoint3x4((Vector3)d.RestJoints[0]) - bones[0].position).magnitude, 1e-5f);
            }
            finally
            {
                if (s != null)
                {
                    s.Dispose();
                    UnityEngine.Object.DestroyImmediate(s.gameObject);
                }
                UnityEngine.Object.DestroyImmediate(rootGo);
            }
        }

        [Test]
        public void CaptureExports_LoadWhenPresent()
        {
            string root = Application.streamingAssetsPath;
            string[] headers = Directory.Exists(root) ? Directory.GetFiles(root, "*.json", SearchOption.AllDirectories) : Array.Empty<string>();
            int checkedCount = 0;
            foreach (string json in headers)
            {
                if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(json)), "splats", StringComparison.OrdinalIgnoreCase)) continue;
                string role = Path.GetFileNameWithoutExtension(json).ToLowerInvariant();
                if (role != "lead" && role != "follow") continue;
                SplatAvatarData d = SplatAvatarData.Load(json);
                Assert.Greater(d.Count, 0, json);
                Bounds b = d.CanonicalBounds;
                Assert.That(b.size.y, Is.InRange(1.3f, 2.3f), $"standing height of the canonical (zero pose) cloud: {json}");
                checkedCount++;
            }

            if (checkedCount == 0) Assert.Ignore("no splat avatar exports in StreamingAssets");
        }

        [Test]
        public void Reduce_KeepsTheMostOpaqueAndTheHairBinding()
        {
            string root = Application.streamingAssetsPath;
            string[] headers = Directory.Exists(root) ? Directory.GetFiles(root, "*.json", SearchOption.AllDirectories) : Array.Empty<string>();
            int checkedCount = 0;
            foreach (string json in headers)
            {
                if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(json)), "splats", StringComparison.OrdinalIgnoreCase)) continue;
                string role = Path.GetFileNameWithoutExtension(json).ToLowerInvariant();
                if (role != "lead" && role != "follow") continue;
                SplatAvatarData d = SplatAvatarData.Load(json);
                int keep = d.Count / 2;
                SplatAvatarData r = d.Reduce(keep, 0.02f, out string report);
                Assert.AreEqual(keep, r.Count, report);
                float maxLog = Mathf.Log(0.02f) + 1e-5f;
                double keptMean = 0, allMean = 0;
                for (int i = 0; i < r.Count; i++)
                {
                    Assert.LessOrEqual(math.cmax(r.LogScale[i]), maxLog, "clamped sigma");
                    keptMean += 1.0 / (1.0 + Math.Exp(-r.OpacityLogit[i]));
                }

                for (int i = 0; i < d.Count; i++) allMean += 1.0 / (1.0 + Math.Exp(-d.OpacityLogit[i]));
                // the dropped half is the fainter one: the kept Gaussians are more opaque on average
                Assert.Greater(keptMean / r.Count, allMean / d.Count, report);
                Assert.LessOrEqual(r.BoundHairCount, d.BoundHairCount);
                SplatHairBinding hb = SplatHairBinding.FromData(r);
                if (r.BoundHairCount > 0) Assert.AreEqual(r.BoundHairCount, hb.Bound, "binding over the kept records");
                Assert.AreSame(d, d.Reduce(0, 0f, out _), "nothing to do: the same data");
                checkedCount++;
            }

            if (checkedCount == 0) Assert.Ignore("no splat avatar exports in StreamingAssets");
        }
    }
}
