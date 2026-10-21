using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace HmSplats.Tests
{
    /// <summary>Edit-mode tests of the hair binding (SPLAT_AVATAR_FORMAT.md section 6, SplatHairBinding and the SkinHair
    /// kernel of HM_SkinSplats.compute). Synthetic data: 3 guides x 5 points hanging from a head, hair Gaussians bound the
    /// way dancecap mono4d/recap_splat_hair.py binds them (segment frame, local offset / rotation).</summary>
    public class SplatHairTests
    {
        const int G = 3, P = 5, Head = 15;

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

        /// <summary>rest guides in head-bone space: roots over the head, hanging down and back, a bend at point 2</summary>
        static float3[] Guides()
        {
            float3[] r = new float3[G * P];
            for (int g = 0; g < G; g++)
            for (int i = 0; i < P; i++)
                r[g * P + i] = new float3(0.03f * (g - 1), 0.08f - 0.04f * i, 0.04f + 0.012f * i + (i > 2 ? 0.02f * (i - 2) : 0f));
            return r;
        }

        /// <summary>n Gaussians: every other one a hair Gaussian bound to a random guide segment (skinned to the head), the
        /// rest body Gaussians on the head; the header carries hair.binding_info</summary>
        static SplatAvatarData Synthetic(int n, float attachTop = 0.3f, int seed = 5)
        {
            System.Random rng = new(seed);
            float3[] rest = RestJoints();
            float3[] guides = Guides();
            byte[] raw = new byte[SplatAvatarData.HeaderBytes + n * SplatAvatarData.RecordBytes];
            float R() => (float)rng.NextDouble();
            using (BinaryWriter w = new(new MemoryStream(raw)))
            {
                w.Write((byte)'S'); w.Write((byte)'P'); w.Write((byte)'A'); w.Write((byte)'V');
                w.Write(1u); w.Write((uint)n); w.Write(120u); w.Write(0u); w.Write(1u); w.Write(64u);
                w.Seek(64, SeekOrigin.Begin);
                for (int i = 0; i < n; i++)
                {
                    bool hair = i % 2 == 0;
                    int g = rng.Next(G), s = rng.Next(P - 1);
                    float3 r0 = guides[g * P + s], r1 = guides[g * P + s + 1];
                    float3 c = hair ? math.lerp(r0, r1, R()) + (new float3(R(), R(), R()) - 0.5f) * 0.02f : (new float3(R(), R(), R()) - 0.5f) * 0.15f;
                    quaternion q = quaternion.Euler(R() * 3, R() * 3, R() * 3);
                    float3 m = rest[Head] + c;
                    w.Write(m.x); w.Write(m.y); w.Write(m.z);
                    w.Write(q.value.x); w.Write(q.value.y); w.Write(q.value.z); w.Write(q.value.w);
                    w.Write(-5f); w.Write(-5.5f); w.Write(-6f);
                    w.Write(1.5f);
                    w.Write(0.1f); w.Write(-0.2f); w.Write(0.3f);
                    w.Write((ushort)Head); w.Write((ushort)12); w.Write((ushort)0); w.Write((ushort)0);
                    w.Write(hair ? 1f : 0.8f); w.Write(hair ? 0f : 0.2f); w.Write(0f); w.Write(0f);
                    w.Write((byte)(hair ? 1 : 0)); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
                    if (hair)
                    {
                        float3x3 f = SplatHairBinding.SegmentFrame(r1 - r0);
                        float3 off = math.mul(math.transpose(f), c - r0);
                        quaternion lr = math.mul(math.inverse(new quaternion(f)), q);
                        w.Write(g); w.Write(s);
                        w.Write(off.x); w.Write(off.y); w.Write(off.z);
                        w.Write(lr.value.x); w.Write(lr.value.y); w.Write(lr.value.z); w.Write(lr.value.w);
                    }
                    else
                    {
                        w.Write(-1); w.Write(-1);
                        w.Write(0f); w.Write(0f); w.Write(0f);
                        w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f);
                    }
                }
            }

            JArray restJson = new(), rp = new();
            for (int k = 0; k < 55; k++) restJson.Add(new JArray(rest[k].x, rest[k].y, rest[k].z));
            foreach (float3 p in guides) rp.Add(new JArray(p.x, p.y, p.z));
            JObject h = new()
            {
                ["format"] = "dancecap splat avatar", ["version"] = 1, ["role"] = "follow", ["count"] = n,
                ["coords"] = "unity (x, y, -z) of the Y-up right-handed dancecap world",
                ["rest_joints"] = restJson, ["parents"] = new JArray(SmplxParents),
                ["hair"] = new JObject
                {
                    ["binding"] = "unity_hair_strands",
                    ["binding_info"] = new JObject
                    {
                        ["guides"] = G, ["points"] = P, ["rest_points"] = rp,
                        ["attach"] = new JObject { ["y_top_m"] = attachTop, ["width_m"] = 0.05f }
                    }
                }
            };
            return SplatAvatarData.Parse(raw, h);
        }

        sealed class FakeSource : ISplatHairSource
        {
            public float3[] Rest, World;
            public float3 HeadPos;
            public quaternion HeadRot = quaternion.identity;
            public bool Ready => true;
            public int Guides => G;
            public int Points => P;
            public void GetRest(float3[] r) => System.Array.Copy(Rest, r, Rest.Length);

            public void GetFrame(float3[] w, out float3 hp, out quaternion hr)
            {
                System.Array.Copy(World, w, World.Length);
                hp = HeadPos;
                hr = HeadRot;
            }

            public float4 Face0, Face1, Face2;

            public bool TryGetFaceBox(out float4 c, out float4 box, out float4 extra)
            {
                c = Face0;
                box = Face1;
                extra = Face2;
                return Face2.z > 0;
            }
        }

        [Test]
        public void Binding_ReconstructsTheCanonicalCentres()
        {
            SplatAvatarData d = Synthetic(200);
            SplatHairBinding b = SplatHairBinding.FromData(d);
            Assert.NotNull(b);
            Assert.AreEqual(100, b.Bound);
            Assert.Less(b.CanonResidualMaxMm, 1e-3f);
            for (int k = 0; k < b.Bound; k++) Assert.Greater(math.abs(math.dot(b.QHb[k], d.Rotation[b.Index[k]])), 1 - 1e-5f);
        }

        [Test]
        public void Binding_SimulationAtRestIsHeadRigid()
        {
            SplatAvatarData d = Synthetic(200);
            SplatHairBinding b = SplatHairBinding.FromData(d);
            float3[] rest = Guides();
            b.Prepare(rest);
            // the simulation's head somewhere, the shown head elsewhere (re-anchoring)
            quaternion hs = quaternion.Euler(0.3f, 1.1f, -0.2f), hn = quaternion.Euler(-0.4f, 2.0f, 0.5f);
            float3 ps = new(1, 1.5f, 2), pn = new(-0.5f, 1.4f, 0.3f);
            float3[] sim = new float3[rest.Length];
            for (int i = 0; i < rest.Length; i++) sim[i] = ps + math.mul(hs, rest[i]);
            float4[] seg = new float4[2 * G * (P - 1)];
            b.Segments(sim, ps, hs, pn, hn, seg);
            for (int k = 0; k < b.Bound; k++)
            {
                float3 expect = pn + math.mul(hn, b.CanonHb[k]);
                b.Pose(k, seg, expect, hn, out float3 p, out float4 q);
                Assert.Less(math.length(p - expect), 1e-5f, $"bound {k}");
                Assert.Greater(math.abs(math.dot(q, math.mul(hn, new quaternion(b.QHb[k])).value)), 1 - 1e-5f);
            }
        }

        [Test]
        public void Binding_RigidlySwungStrandCarriesItsHair()
        {
            SplatAvatarData d = Synthetic(300);
            SplatHairBinding b = SplatHairBinding.FromData(d);
            float3[] rest = Guides();
            b.Prepare(rest);
            // guide 1 swung 70 deg about its root around X (perpendicular to all its tangents: the minimal rotations are that
            // rotation, no twist; head at the origin, unrotated); the others at rest
            quaternion r = quaternion.AxisAngle(new float3(1f, 0f, 0f), math.radians(70));
            float3 root = rest[1 * P];
            float3[] sim = (float3[])rest.Clone();
            for (int i = 0; i < P; i++) sim[P + i] = root + math.mul(r, rest[P + i] - root);
            float4[] seg = new float4[2 * G * (P - 1)];
            b.Segments(sim, float3.zero, quaternion.identity, float3.zero, quaternion.identity, seg);
            int checkedCount = 0;
            for (int k = 0; k < b.Bound; k++)
            {
                if (b.SegA[k] / (P - 1) != 1) continue;
                float3 expect = root + math.mul(r, b.CanonHb[k] - root);
                b.Pose(k, seg, b.CanonHb[k], quaternion.identity, out float3 p, out float4 q);
                Assert.Less(math.length(p - expect), 1e-5f, $"bound {k}");
                Assert.Greater(math.abs(math.dot(q, math.mul(r, new quaternion(b.QHb[k])).value)), 1 - 1e-5f);
                checkedCount++;
            }

            Assert.Greater(checkedCount, 10);
        }

        [Test]
        public void Binding_AttachBlendKeepsTheTopOnTheHead()
        {
            // attach at y = 0.0 (head-bone): the Gaussians above it stay head-rigid however the strands move
            SplatAvatarData d = Synthetic(300, attachTop: 0.0f);
            SplatHairBinding b = SplatHairBinding.FromData(d);
            b.Prepare(Guides());
            float3[] sim = Guides();
            for (int i = 0; i < sim.Length; i++) sim[i] += new float3(0.05f, 0, 0) * (i % P);
            float4[] seg = new float4[2 * G * (P - 1)];
            b.Segments(sim, float3.zero, quaternion.identity, float3.zero, quaternion.identity, seg);
            int top = 0;
            for (int k = 0; k < b.Bound; k++)
            {
                if (b.CanonHb[k].y < 0f) continue;
                Assert.AreEqual(0f, b.Attach[k]);
                b.Pose(k, seg, b.CanonHb[k], quaternion.identity, out float3 p, out _);
                Assert.Less(math.length(p - b.CanonHb[k]), 1e-6f);
                top++;
            }

            Assert.Greater(top, 5);
        }

        [Test]
        public void FacePush_MatchesTheSimulationsFaceBox()
        {
            SplatAvatarData d = Synthetic(20);
            SplatHairBinding b = SplatHairBinding.FromData(d);
            b.SetFaceBox(new float4(0, 0.06f, 0, 0.08f), new float4(0.25f, -1.55f, 0.45f, 0.8f), new float4(0.006f, 0.35f, 1f, 0f));
            // in front of the face, a little to her left (+x): out to +x past the half width + margin
            float3 q = new(0.01f, 0.05f, -0.05f);
            float3 push = b.FacePush(q, 1f);
            Assert.AreEqual(0.8f * 0.08f + 0.006f - 0.01f, push.x, 1e-6f);
            Assert.AreEqual(0f, push.y);
            // low in the box (under the chin): straight down
            q = new float3(0f, 0.06f - 1.5f * 0.08f, -0.05f);
            push = b.FacePush(q, -1f);
            Assert.AreEqual(0f, push.x);
            Assert.Less(push.y, 0f);
            // behind the face plane: nothing
            Assert.IsTrue(math.all(b.FacePush(new float3(0f, 0.05f, 0.01f), 1f) == 0f));
        }

        [Test]
        public void GpuHairPass_MatchesTheCpuReference()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("no compute shaders on this device");
            ComputeShader cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(SkinnedSplatAvatar.ComputePath);
            Assert.NotNull(cs, SkinnedSplatAvatar.ComputePath);
            SplatAvatarData d = Synthetic(2000, attachTop: 0.02f);
            GameObject rootGo = new("splat hair test bones");
            SkinnedSplatAvatar s = null;
            try
            {
                Transform[] bones = new Transform[55];
                System.Random rng = new(13);
                for (int j = 0; j < 55; j++)
                {
                    int p = d.Parents[j];
                    bones[j] = new GameObject($"b{j}").transform;
                    bones[j].SetParent(p < 0 ? rootGo.transform : bones[p], false);
                    bones[j].localPosition = (Vector3)(p < 0 ? d.RestJoints[j] : d.RestJoints[j] - d.RestJoints[p]);
                    bones[j].localRotation = Quaternion.Euler((float)rng.NextDouble() * 40 - 20, (float)rng.NextDouble() * 40 - 20, (float)rng.NextDouble() * 40 - 20);
                }

                rootGo.transform.SetPositionAndRotation(new Vector3(0.5f, 0, -1), Quaternion.Euler(0, -30, 0));
                float3[] rest = Guides();
                // simulated: head-carried rest of the avatar head (a few mm from the splat head) + a swing
                Transform head = bones[Head];
                FakeSource src = new() { Rest = rest, World = new float3[rest.Length], HeadPos = head.position, HeadRot = head.rotation };
                for (int i = 0; i < rest.Length; i++)
                    src.World[i] = (float3)head.position + math.mul(src.HeadRot, rest[i]) + new float3(0.04f, -0.01f, 0.02f) * (i % P) * (i % P) * 0.25f;
                // a face box (head-bone space) over the middle guide: part of the hair is pushed out of it
                src.Face0 = new float4(0f, 0f, 0.2f, 0.1f);
                src.Face1 = new float4(0.25f, -5f, 5f, 0.3f);
                src.Face2 = new float4(0.006f, 0.35f, 1f, 0f);
                s = SkinnedSplatAvatar.Create(d, bones, null, "gpu hair test splat", cs);
                Assert.NotNull(s.HairBinding);
                s.SetHairSource(src);
                s.SkinNow(true);
                Assert.IsTrue(s.HairLive, s.HairStatus);
                SplatHairBinding b = s.HairBinding;
                Assert.IsTrue(b.FaceOn);
                Matrix4x4 s15 = s.CurrentSkin[Head];
                float worst = 0, worstRot = 0, moved = 0;
                float4[] segCpu = s.HairSegmentsCpu();
                for (int k = 0; k < b.Bound; k++)
                {
                    int i = b.Index[k];
                    float4[] g = s.ReadBack(i, 1);
                    float3 rigid = s15.MultiplyPoint3x4((Vector3)d.Mean[i]);
                    b.Pose(k, segCpu, rigid, s.HairHeadRot, out float3 p, out float4 q);
                    worst = math.max(worst, math.length(g[0].xyz - p));
                    worstRot = math.max(worstRot, 1 - math.abs(math.dot(math.normalize(g[2]), q)));
                    moved = math.max(moved, math.length(p - rigid));
                }

                // after the push nothing bound is left inside the box (the shown head's frame)
                int insideAfter = 0;
                Matrix4x4 h15 = s.CurrentSkin[Head];
                float3 hp = h15.MultiplyPoint3x4((Vector3)d.RestJoints[Head]);
                for (int k = 0; k < b.Bound; k++)
                {
                    float3 loc = math.mul(math.inverse(s.HairHeadRot), s.ReadBack(b.Index[k], 1)[0].xyz - hp);
                    if (b.Attach[k] >= 1f && math.any(b.FacePush(loc, b.Side[k]) != 0f)) insideAfter++;
                }

                Assert.AreEqual(0, insideAfter, "bound Gaussians left inside the face box");
                Assert.Less(worst, 1e-5f, "bound centres GPU vs CPU (m)");
                Assert.Less(worstRot, 1e-5f, "bound rotations GPU vs CPU (1 - |dot|)");
                Assert.Greater(moved, 0.01f, "the swing moved the hair");
                // unbound Gaussians are untouched by the hair pass (LBS)
                SplatSkinning.Pose(d, s.CurrentSkin, 1, out float3 lbs, out _);
                Assert.Less(math.length(s.ReadBack(1, 1)[0].xyz - lbs), 1e-5f);
                // hair motion off: the bound hair is back on its LBS
                s.HairMotion = false;
                s.SkinNow(true);
                SplatSkinning.Pose(d, s.CurrentSkin, b.Index[0], out float3 lbs0, out _);
                Assert.Less(math.length(s.ReadBack(b.Index[0], 1)[0].xyz - lbs0), 1e-5f);
            }
            finally
            {
                if (s != null)
                {
                    s.Dispose();
                    Object.DestroyImmediate(s.gameObject);
                }

                Object.DestroyImmediate(rootGo);
            }
        }
    }
}
