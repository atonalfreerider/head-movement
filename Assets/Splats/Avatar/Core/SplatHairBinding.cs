using System;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using UnityEngine;

namespace HmSplats
{
    /// <summary>
    /// Where the simulated hair strands come from (the viewer's HairStrands: Assets/Splats/Avatar/HairStrandsSplatSource.cs).
    /// Rest points are in HEAD-BONE space (relative to the avatar skin's head joint 15, T-pose axes = Unity axes), the
    /// frame's points in WORLD space, guides x points, guide-major.
    /// </summary>
    public interface ISplatHairSource
    {
        /// <summary>the simulation runs and has a frame (false: the bound hair falls back to its LBS skinning)</summary>
        bool Ready { get; }
        int Guides { get; }
        int Points { get; }
        void GetRest(float3[] restHeadBone);
        /// <summary>simulated points (world) and the world head pose the simulation used for them</summary>
        void GetFrame(float3[] worldPoints, out float3 simHeadPos, out quaternion simHeadRot);
        /// <summary>the simulation's face box (head-bone space; HairJobs.FaceBox): centre.xyz + head radius r,
        /// (fwdMin, yLo, yHi, halfWidth) in units of r, (margin m, sideBias, enabled 0/1, 0); false: none</summary>
        bool TryGetFaceBox(out float4 centreR, out float4 box, out float4 extra);
    }

    /// <summary>
    /// The hair binding of a splat avatar (SPLAT_AVATAR_FORMAT.md section 6; written offline by dancecap
    /// mono4d/recap_splat_hair.py): every bound hair Gaussian rides on one segment of a simulated guide strand.
    ///
    /// Segment frame (head-bone space, Unity coordinates): columns X, Y, Z with Y = unit tangent point s -> s+1, Z = her
    /// forward (Unity -Z) made orthogonal to Y (Unity +Y when |Y . forward| > 0.999), X = Y x Z. The canonical head-bone
    /// centre of a Gaussian is c = R_s + F_s local_offset and its canonical rotation q = q(F_s) local_rotation (R, F of the
    /// rest the binding was made on: the JSON's hair.binding_info.rest_points).
    ///
    /// Posing, per frame (Segments + the compute kernel SkinHair; Pose is the CPU reference):
    ///   the simulated points re-anchored to the splat skeleton's head: P' = h + H Hs^-1 (P - hs) (h, H = the shown head:
    ///   S_15 J_15 and the rotation of S_15; hs, Hs = the head pose the simulation used) - the hair sits on the head the body
    ///   splats show, also between capture frames (the film's sub-frame poses);
    ///   per segment D_s = minrot(H t_rest_s, t_sim_s) H (t_rest = the simulation's OWN rest tangent: at rest D_s = H);
    ///   the offset c - R_live_s split into its part along the rest tangent (a) and across it (p, the distance to the
    ///   strand): pose from segment s = P'_s + D_s a + K_s p with K_s = nlerp(H, D_s, k), k = 1 - smoothstep(lever.near_m,
    ///   lever.far_m, |p|): hair on the strand turns with it, hair far from its guide (the side hair beside the face is
    ///   5-12 cm from the nearest guide, which drapes behind the shoulder) follows the guide's displacement with the head's
    ///   rotation, no lever arm sweeping it through the head;
    ///   blended with the neighbour segment N (s + 1 when the projection u = local_offset.y / |R_s+1 - R_s| >= 0.5, s - 1
    ///   below) by w = clamp(|u - 0.5|, 0, 0.5) (continuous across the segment joints); rotation = nlerp(K_s, K_N, w) q;
    ///   the face box of the simulation (HairJobs.FaceBox, the box the guides are kept out of in every step) applied to the
    ///   strand pose in the shown head's frame: a bound Gaussian inside it goes out sideways (to the side of the face it
    ///   hangs on at rest) or down below the chin, whichever is nearer - the hair never covers the face, like the cards;
    ///   attached at the top: lerp(head-rigid S_15 mean, strand pose, a) with a = smoothstep of the head-bone height
    ///   (attach.y_top_m down to attach.y_top_m - attach.width_m), so the hanging hair grows out of the rigid scalp hair.
    /// </summary>
    public sealed class SplatHairBinding
    {
        public const int HeadJoint = 15;
        static readonly float3 Forward = new(0, 0, -1), Up = new(0, 1, 0);

        public readonly int Guides, Points, SegmentsPerGuide, Bound;
        public readonly int[] Index;          // Gaussian index of bound Gaussian b
        public readonly int[] SegA, SegN;     // global segment index g * SegmentsPerGuide + s
        public readonly float[] W, Attach;
        public readonly float3[] CanonHb;     // canonical centre, head-bone space
        public readonly float4[] QHb;         // canonical rotation (head-bone axes = canonical axes)
        public readonly float3[] BindRest;    // the rest the binding was made on (JSON)
        public readonly float AttachTop, AttachWidth, LeverNear, LeverFar;
        public readonly string RestSha;

        public float3[] LiveRest { get; private set; }
        public float3[] AlongA { get; private set; }
        public float3[] PerpA { get; private set; }
        public float3[] AlongN { get; private set; }
        public float3[] PerpN { get; private set; }
        public float[] K { get; private set; }
        public float RestDiffMaxMm { get; private set; } = float.NaN;
        public float CanonResidualMaxMm { get; private set; }
        public bool Prepared => LiveRest != null;

        float3[] tRest;
        float3[] anchored;
        float3 lastHeadPos;
        quaternion lastHeadRot = quaternion.identity;

        /// <summary>the face box in force (SetFaceBox; enabled = Face1.w... see FaceBox* below)</summary>
        public bool FaceOn { get; private set; }
        public float4 Face0 { get; private set; }   // centre (head-bone) xyz, r
        public float4 Face1 { get; private set; }   // fwdMin, yLo, yHi, halfWidth (x r)
        public float4 Face2 { get; private set; }   // margin (m), sideBias (x r), enabled, 0
        /// <summary>per bound Gaussian: the side of the face it hangs on at rest (+1 / -1 along head-bone x)</summary>
        public float[] Side { get; private set; }

        SplatHairBinding(SplatAvatarData d, JObject bi)
        {
            Guides = (int)bi["guides"];
            Points = (int)bi["points"];
            SegmentsPerGuide = Points - 1;
            JArray rp = (JArray)bi["rest_points"];
            if (rp == null || rp.Count != Guides * Points) throw new InvalidOperationException($"hair binding: rest_points {rp?.Count} != {Guides} x {Points}");
            BindRest = new float3[rp.Count];
            for (int i = 0; i < rp.Count; i++) BindRest[i] = new float3((float)rp[i][0], (float)rp[i][1], (float)rp[i][2]);
            JToken at = bi["attach"];
            AttachTop = at?["y_top_m"] != null ? (float)at["y_top_m"] : 0f;
            AttachWidth = at?["width_m"] != null ? (float)at["width_m"] : 0.05f;
            JToken lv = bi["lever"];
            LeverNear = lv?["near_m"] != null ? (float)lv["near_m"] : 0.02f;
            LeverFar = lv?["far_m"] != null ? (float)lv["far_m"] : 0.07f;
            RestSha = (string)bi["rest_sha256"];

            int n = 0;
            for (int i = 0; i < d.Count; i++)
                if (d.Strand[i] >= 0) n++;
            Bound = n;
            Index = new int[n];
            SegA = new int[n];
            SegN = new int[n];
            W = new float[n];
            Attach = new float[n];
            CanonHb = new float3[n];
            QHb = new float4[n];
            float3 j15 = d.RestJoints[HeadJoint];
            float resid = 0f;
            int b = 0;
            for (int i = 0; i < d.Count; i++)
            {
                int g = d.Strand[i], s = d.Segment[i];
                if (g < 0) continue;
                if (g >= Guides || s < 0 || s >= SegmentsPerGuide) throw new InvalidOperationException($"hair binding: Gaussian {i} strand {g} segment {s} out of range ({Guides} x {SegmentsPerGuide})");
                float3 r0 = BindRest[g * Points + s], r1 = BindRest[g * Points + s + 1];
                float3x3 f = SegmentFrame(r1 - r0);
                float3 off = d.LocalOffset[i];
                float3 c = r0 + math.mul(f, off);
                quaternion lr = math.normalizesafe(new quaternion(d.LocalRotation[i]));
                Index[b] = i;
                CanonHb[b] = c;
                QHb[b] = math.normalize(math.mul(new quaternion(f), lr)).value;
                float u = off.y / math.max(math.length(r1 - r0), 1e-9f);
                int nb = u >= 0.5f ? s + 1 : s - 1;
                bool ok = nb >= 0 && nb < SegmentsPerGuide;
                SegA[b] = g * SegmentsPerGuide + s;
                SegN[b] = g * SegmentsPerGuide + (ok ? nb : s);
                W[b] = ok ? math.clamp(math.abs(u - 0.5f), 0f, 0.5f) : 0f;
                float x = math.saturate((AttachTop - c.y) / math.max(AttachWidth, 1e-6f));
                Attach[b] = x * x * (3 - 2 * x);
                resid = math.max(resid, math.length(c - (d.Mean[i] - j15)));
                b++;
            }

            CanonResidualMaxMm = resid * 1000f;
        }

        /// <summary>the binding of a splat avatar, or null when it has no bound hair Gaussian (format v1: JSON
        /// hair.binding_info with guides, points, rest_points)</summary>
        public static SplatHairBinding FromData(SplatAvatarData d)
        {
            if (d == null || d.BoundHairCount == 0) return null;
            if (d.Header?["hair"]?["binding_info"] is not JObject bi) throw new InvalidOperationException("hair binding: records are bound but the JSON has no hair.binding_info");
            return new SplatHairBinding(d, bi);
        }

        /// <summary>segment frame of a rest tangent (columns X, Y, Z; see the class summary)</summary>
        public static float3x3 SegmentFrame(float3 tangent)
        {
            float3 y = math.normalizesafe(tangent, Up);
            float3 r = math.abs(math.dot(y, Forward)) > 0.999f ? Up : Forward;
            float3 z = math.normalizesafe(r - math.dot(r, y) * y);
            float3 x = math.cross(y, z);
            return new float3x3(x, y, z);
        }

        /// <summary>the minimal rotation taking direction a to direction b (180 deg about a perpendicular axis when opposite)</summary>
        public static quaternion MinRotation(float3 a, float3 b)
        {
            a = math.normalizesafe(a);
            b = math.normalizesafe(b);
            float d = math.dot(a, b);
            if (d < -1f + 1e-6f)
            {
                float3 p = math.normalizesafe(math.cross(a, math.abs(a.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0)));
                return new quaternion(new float4(p, 0f));
            }

            return math.normalize(new quaternion(new float4(math.cross(a, b), 1f + d)));
        }

        /// <summary>bind to the simulation's own rest (guides x points, head-bone space): the per-Gaussian offsets from
        /// the live segment origins and the live rest tangents</summary>
        public void Prepare(float3[] liveRest)
        {
            if (liveRest == null || liveRest.Length != Guides * Points) throw new ArgumentException($"live rest {liveRest?.Length} points, binding has {Guides} x {Points}");
            float diff = 0f;
            for (int i = 0; i < liveRest.Length; i++) diff = math.max(diff, math.length(liveRest[i] - BindRest[i]));
            RestDiffMaxMm = diff * 1000f;
            LiveRest = (float3[])liveRest.Clone();
            tRest = new float3[Guides * SegmentsPerGuide];
            for (int gs = 0; gs < tRest.Length; gs++)
            {
                int p = PointOf(gs);
                tRest[gs] = math.normalizesafe(LiveRest[p + 1] - LiveRest[p], Up);
            }

            AlongA = new float3[Bound];
            PerpA = new float3[Bound];
            AlongN = new float3[Bound];
            PerpN = new float3[Bound];
            K = new float[Bound];
            for (int b = 0; b < Bound; b++)
            {
                float3 oa = CanonHb[b] - LiveRest[PointOf(SegA[b])], on = CanonHb[b] - LiveRest[PointOf(SegN[b])];
                float3 ta = tRest[SegA[b]], tn = tRest[SegN[b]];
                AlongA[b] = math.dot(oa, ta) * ta;
                PerpA[b] = oa - AlongA[b];
                AlongN[b] = math.dot(on, tn) * tn;
                PerpN[b] = on - AlongN[b];
                float x = math.saturate((math.length(PerpA[b]) - LeverNear) / math.max(LeverFar - LeverNear, 1e-6f));
                K[b] = 1f - x * x * (3 - 2 * x);
            }

            anchored = new float3[Guides * Points];
        }

        /// <summary>use the simulation's face box (null-safe: enabled = false turns it off)</summary>
        public void SetFaceBox(float4 centreR, float4 box, float4 extra)
        {
            Face0 = centreR;
            Face1 = box;
            Face2 = extra;
            FaceOn = extra.z > 0.5f && centreR.w > 0f;
            Side = new float[Bound];
            for (int b = 0; b < Bound; b++) Side[b] = CanonHb[b].x >= centreR.x ? 1f : -1f;
        }

        /// <summary>HairJobs.FaceBox.Push with radius 0, head-bone space: the offset that moves q out of the box</summary>
        public float3 FacePush(float3 q, float side)
        {
            if (!FaceOn) return float3.zero;
            float r = Face0.w;
            float3 d = q - Face0.xyz;
            bool inside = -d.z > Face1.x * r && d.y > Face1.y * r && d.y < Face1.z * r && math.abs(d.x) < Face1.w * r;
            if (!inside) return float3.zero;
            float edge = Face1.w * r + Face2.x;
            float s = d.x + Face2.y * r * side;
            s = s > 0f ? 1f : s < 0f ? -1f : (side >= 0f ? 1f : -1f);
            float dx = s * edge - d.x;
            float dy = Face1.y * r - Face2.x - d.y;
            return -dy < math.abs(dx) ? new float3(0, dy, 0) : new float3(dx, 0, 0);
        }

        /// <summary>the simulation's own rest tangent of a global segment (after Prepare)</summary>
        public float3 RestTangent(int globalSegment) => tRest[globalSegment];

        public int PointOf(int globalSegment) => globalSegment / SegmentsPerGuide * Points + globalSegment % SegmentsPerGuide;

        /// <summary>this frame's segments: seg[2 gs] = (origin P'_s, 0), seg[2 gs + 1] = D_s (xyzw), world space</summary>
        public void Segments(float3[] simWorld, float3 simHeadPos, quaternion simHeadRot, float3 headPos, quaternion headRot, float4[] seg)
        {
            if (!Prepared) throw new InvalidOperationException("Prepare first");
            lastHeadPos = headPos;
            lastHeadRot = headRot;
            quaternion a = math.normalize(math.mul(headRot, math.inverse(simHeadRot)));
            for (int p = 0; p < anchored.Length; p++) anchored[p] = headPos + math.mul(a, simWorld[p] - simHeadPos);
            for (int gs = 0; gs < tRest.Length; gs++)
            {
                int p = PointOf(gs);
                float3 ts = anchored[p + 1] - anchored[p];
                float3 tc = math.mul(headRot, tRest[gs]);
                quaternion d = math.normalize(math.mul(MinRotation(tc, ts), headRot));
                seg[2 * gs] = new float4(anchored[p], 0f);
                seg[2 * gs + 1] = d.value;
            }
        }

        /// <summary>normalised lerp from a to b (b sign-aligned with a)</summary>
        public static quaternion Nlerp(quaternion a, quaternion b, float t)
        {
            float4 vb = math.dot(a.value, b.value) < 0 ? -b.value : b.value;
            return math.normalize(new quaternion(math.lerp(a.value, vb, t)));
        }

        /// <summary>CPU reference of the SkinHair kernel for bound Gaussian b: rigidPos = S_15 mean (the head-rigid end),
        /// headRot = the rotation of S_15</summary>
        public void Pose(int b, float4[] seg, float3 rigidPos, quaternion headRot, out float3 position, out float4 rotation)
        {
            int a = SegA[b], n = SegN[b];
            quaternion qa = new(seg[2 * a + 1]), qn = new(seg[2 * n + 1]);
            quaternion ka = Nlerp(headRot, qa, K[b]), kn = Nlerp(headRot, qn, K[b]);
            float3 pa = seg[2 * a].xyz + math.mul(qa, AlongA[b]) + math.mul(ka, PerpA[b]);
            float3 pn = seg[2 * n].xyz + math.mul(qn, AlongN[b]) + math.mul(kn, PerpN[b]);
            float w = W[b];
            float3 p = math.lerp(pa, pn, w);
            quaternion dq = Nlerp(ka, kn, w);
            if (FaceOn)
            {
                // the face box in the shown head's frame (the head of the last Segments call)
                float3 local = math.mul(math.inverse(lastHeadRot), p - lastHeadPos);
                p += math.mul(lastHeadRot, FacePush(local, Side[b]));
            }

            quaternion q = math.mul(dq, new quaternion(QHb[b]));
            float t = Attach[b];
            quaternion qr = math.mul(headRot, new quaternion(QHb[b]));
            float4 vq = math.dot(qr.value, q.value) < 0 ? -q.value : q.value;
            position = math.lerp(rigidPos, p, t);
            rotation = math.normalize(math.lerp(qr.value, vq, t));
        }
    }
}
