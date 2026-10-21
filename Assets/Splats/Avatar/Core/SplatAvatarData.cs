using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using UnityEngine;

namespace HmSplats
{
    /// <summary>
    /// One dancer's skinned Gaussian-splat avatar as the dancecap exporters write it: <c>splats/&lt;role&gt;.splatav</c>
    /// (binary, little-endian: 64-byte header "SPAV" + N records of 120 bytes) and <c>splats/&lt;role&gt;.json</c> (skeleton,
    /// canonical pose, provenance). The contract is dancecap/docs/SPLAT_AVATAR_FORMAT.md (version 1). Everything is
    /// already in Unity coordinates ((x, y, -z) of the dancecap world, quaternions (-x, -y, z, w)), canonical pose = the
    /// shaped SMPL-X zero pose, canonical joint world transform of joint j = T(rest_joints[j]).
    /// Likeness data: the files live in git-ignored StreamingAssets only.
    /// </summary>
    public sealed class SplatAvatarData
    {
        public const int Joints = 55;
        public const int HeaderBytes = 64, RecordBytes = 120, Version = 1;
        public const float ShC0 = 0.2820947918f;

        public string Role;
        public int Count;
        public JObject Header;

        // canonical attributes (Unity coordinates)
        public float3[] Mean;
        public float4[] Rotation;     // xyzw, unit
        public float3[] LogScale;
        public float[] OpacityLogit;
        public float3[] Sh0;          // 3DGS f_dc: colour = 0.5 + C0 * sh0 (sRGB)
        public int4[] JointIndex;
        public float4[] Weight;
        public byte[] Hair;
        public int[] Strand, Segment;
        public float3[] LocalOffset;
        public float4[] LocalRotation;

        // skeleton
        public float3[] RestJoints = new float3[Joints];
        public int[] Parents = new int[Joints];

        public int HairCount { get; private set; }
        public int BoundHairCount { get; private set; }
        public string SourcePath { get; private set; }

        public Bounds CanonicalBounds
        {
            get
            {
                if (Count == 0) return default;
                float3 lo = Mean[0], hi = Mean[0];
                for (int i = 1; i < Count; i++)
                {
                    lo = math.min(lo, Mean[i]);
                    hi = math.max(hi, Mean[i]);
                }

                return new Bounds((Vector3)((lo + hi) * 0.5f), (Vector3)(hi - lo));
            }
        }

        /// <summary>the json + splatav pair of a role in a capture folder, or null when the capture has no splat avatar for it</summary>
        public static string JsonPath(string captureFolder, string role)
        {
            string p = Path.Combine(captureFolder, "splats", role.ToLowerInvariant() + ".json");
            return File.Exists(p) ? p : null;
        }

        /// <summary>the json of a role in a named file set of the capture (splats/&lt;set&gt;/&lt;role&gt;.json: the Quest's distilled sets, dancecap
        /// mono4d/quest_distill.py), falling back to splats/&lt;role&gt;.json when the capture has no such set (empty set or "full": the full
        /// file); null when the capture has no splat avatar for the role</summary>
        public static string JsonPath(string captureFolder, string role, string set, out bool fromSet)
        {
            fromSet = false;
            if (!string.IsNullOrWhiteSpace(set) && !string.Equals(set.Trim(), "full", StringComparison.OrdinalIgnoreCase))
            {
                string s = Path.Combine(captureFolder, "splats", set.Trim(), role.ToLowerInvariant() + ".json");
                if (File.Exists(s))
                {
                    fromSet = true;
                    return s;
                }
            }

            return JsonPath(captureFolder, role);
        }

        /// <summary>read and validate a json + splatav pair (thread-safe: no Unity API calls)</summary>
        public static SplatAvatarData Load(string jsonPath)
        {
            JObject h = JObject.Parse(File.ReadAllText(jsonPath));
            if ((string)h["format"] != "dancecap splat avatar") throw new InvalidDataException($"{jsonPath}: not a dancecap splat avatar header");
            if ((int?)h["version"] != Version) throw new InvalidDataException($"{jsonPath}: version {(int?)h["version"]} (reader knows {Version})");
            string coords = (string)h["coords"] ?? "";
            if (!coords.StartsWith("unity", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{jsonPath}: coords '{coords}' (Unity coordinates expected)");
            string dataName = (string)h["data"] ?? (string)h["binary"] ?? Path.GetFileNameWithoutExtension(jsonPath) + ".splatav";
            string dataPath = Path.Combine(Path.GetDirectoryName(jsonPath) ?? "", dataName);
            SplatAvatarData d = Parse(File.ReadAllBytes(dataPath), h);
            d.SourcePath = dataPath;
            return d;
        }

        public static SplatAvatarData Parse(byte[] raw, JObject header)
        {
            if (raw.Length < HeaderBytes || raw[0] != 'S' || raw[1] != 'P' || raw[2] != 'A' || raw[3] != 'V')
                throw new InvalidDataException("splatav: bad magic (SPAV expected)");
            uint version = BitConverter.ToUInt32(raw, 4);
            int n = (int)BitConverter.ToUInt32(raw, 8);
            uint recordBytes = BitConverter.ToUInt32(raw, 12);
            uint shRest = BitConverter.ToUInt32(raw, 16);
            uint flags = BitConverter.ToUInt32(raw, 20);
            uint headerBytes = BitConverter.ToUInt32(raw, 24);
            if (version != Version || recordBytes != RecordBytes || headerBytes != HeaderBytes)
                throw new InvalidDataException($"splatav: version {version}, record {recordBytes} B, header {headerBytes} B (expected {Version}, {RecordBytes}, {HeaderBytes})");
            if ((flags & 1) == 0) throw new InvalidDataException("splatav: flags bit 0 (Unity coordinates) not set");
            long need = HeaderBytes + (long)n * RecordBytes + (long)n * shRest * 3 * 4;
            if (n <= 0 || raw.Length < need) throw new InvalidDataException($"splatav: {raw.Length} bytes for {n} Gaussians (need {need})");
            if (header != null && (int?)header["count"] is int hc && hc != n) throw new InvalidDataException($"splatav: header count {hc} != binary {n}");

            SplatAvatarData d = new()
            {
                Count = n,
                Header = header,
                Role = (string)header?["role"],
                Mean = new float3[n], Rotation = new float4[n], LogScale = new float3[n], OpacityLogit = new float[n],
                Sh0 = new float3[n], JointIndex = new int4[n], Weight = new float4[n], Hair = new byte[n],
                Strand = new int[n], Segment = new int[n], LocalOffset = new float3[n], LocalRotation = new float4[n]
            };

            int hair = 0, bound = 0;
            for (int i = 0; i < n; i++)
            {
                int o = HeaderBytes + i * RecordBytes;
                d.Mean[i] = F3(raw, o);
                float4 q = F4(raw, o + 12);
                float qn = math.length(q);
                d.Rotation[i] = qn > 1e-12f && math.isfinite(qn) ? q / qn : new float4(0, 0, 0, 1);
                d.LogScale[i] = F3(raw, o + 28);
                d.OpacityLogit[i] = BitConverter.ToSingle(raw, o + 40);
                d.Sh0[i] = F3(raw, o + 44);
                int4 j = new(BitConverter.ToUInt16(raw, o + 56), BitConverter.ToUInt16(raw, o + 58),
                    BitConverter.ToUInt16(raw, o + 60), BitConverter.ToUInt16(raw, o + 62));
                float4 w = F4(raw, o + 64);
                if (math.any(j >= Joints) || math.any(w < 0) || !math.all(math.isfinite(w)))
                    throw new InvalidDataException($"splatav: Gaussian {i}: joints {j} / weights {w} out of range");
                float ws = math.csum(w);
                if (ws <= 1e-6f) throw new InvalidDataException($"splatav: Gaussian {i}: weights sum {ws}");
                d.JointIndex[i] = j;
                d.Weight[i] = w / ws;
                d.Hair[i] = raw[o + 80];
                d.Strand[i] = BitConverter.ToInt32(raw, o + 84);
                d.Segment[i] = BitConverter.ToInt32(raw, o + 88);
                d.LocalOffset[i] = F3(raw, o + 92);
                d.LocalRotation[i] = F4(raw, o + 104);
                if (d.Hair[i] != 0) hair++;
                if (d.Strand[i] >= 0) bound++;
                if (!math.all(math.isfinite(d.Mean[i])) || !math.all(math.isfinite(d.LogScale[i])) || !math.isfinite(d.OpacityLogit[i]))
                    throw new InvalidDataException($"splatav: Gaussian {i}: non-finite attributes");
            }

            d.HairCount = hair;
            d.BoundHairCount = bound;

            JArray rest = header?["rest_joints"] as JArray;
            JArray parents = header?["parents"] as JArray;
            if (rest == null || rest.Count != Joints || parents == null || parents.Count != Joints)
                throw new InvalidDataException("splatav json: rest_joints (55 x 3) and parents (55) are required");
            for (int k = 0; k < Joints; k++)
            {
                d.RestJoints[k] = new float3((float)rest[k][0], (float)rest[k][1], (float)rest[k][2]);
                d.Parents[k] = (int)parents[k];
                if (k == 0 ? d.Parents[k] >= 0 : d.Parents[k] < 0 || d.Parents[k] >= k)
                    throw new InvalidDataException($"splatav json: parents[{k}] = {d.Parents[k]} is not a SMPL-X tree");
            }

            // canonical joint world transforms: the format's canonical pose is the zero pose, so they must be T(J_j)
            if (header?["canonical_joint_world"] is JArray cjw && cjw.Count == Joints)
            {
                for (int k = 0; k < Joints; k++)
                {
                    JArray m = (JArray)cjw[k];
                    float3 t = new((float)m[3], (float)m[7], (float)m[11]);
                    float rotErr = math.abs((float)m[0] - 1) + math.abs((float)m[5] - 1) + math.abs((float)m[10] - 1) +
                                   math.abs((float)m[1]) + math.abs((float)m[2]) + math.abs((float)m[4]) +
                                   math.abs((float)m[6]) + math.abs((float)m[8]) + math.abs((float)m[9]);
                    if (rotErr > 1e-3f || math.length(t - d.RestJoints[k]) > 1e-4f)
                        throw new InvalidDataException($"splatav json: canonical_joint_world[{k}] is not T(rest_joints[{k}]) (zero canonical pose expected, format v1)");
                }
            }

            return d;
        }

        /// <summary>
        /// A lighter copy for a small GPU (the Quest; load time, thread-safe): keeps the <paramref name="keep"/> Gaussians with
        /// the largest contribution score opacity x (sigma_mid x sigma_max)^0.25 (mostly opacity: faint Gaussians go first,
        /// the size only breaks ties, so the opaque surface layer stays closed) and clamps every standard deviation to
        /// <paramref name="maxSigma"/> metres (the very large Gaussians cost the most fill). keep &lt;= 0 or &gt;= Count keeps all;
        /// maxSigma &lt;= 0 clamps nothing. Records keep their order, joints, weights and hair binding, so the skinning and the
        /// hair pass are unchanged. Returns this object when nothing changes.
        /// </summary>
        public SplatAvatarData Reduce(int keep, float maxSigma, out string report)
        {
            bool prune = keep > 0 && keep < Count;
            float maxLog = maxSigma > 0f ? math.log(maxSigma) : float.PositiveInfinity;
            int clampedCount = 0;
            if (!prune)
            {
                for (int i = 0; i < Count && clampedCount == 0; i++)
                    if (math.cmax(LogScale[i]) > maxLog) clampedCount++;
                if (clampedCount == 0)
                {
                    report = $"{Count} Gaussians (all kept, none above {(maxSigma > 0f ? $"{maxSigma * 100f:0.##} cm" : "any limit")})";
                    return this;
                }
            }

            int n = prune ? keep : Count;
            int[] order = new int[Count];
            for (int i = 0; i < Count; i++) order[i] = i;
            if (prune)
            {
                float[] score = new float[Count];
                for (int i = 0; i < Count; i++)
                {
                    float3 ls = LogScale[i];
                    float lmax = math.cmax(ls), lmin = math.cmin(ls), lmid = math.csum(ls) - lmax - lmin;
                    float opacity = 1f / (1f + math.exp(-OpacityLogit[i]));
                    score[i] = opacity * math.exp(0.25f * (lmid + math.min(lmax, maxLog)));
                }

                Array.Sort((float[])score.Clone(), order);   // ascending score
                Array.Reverse(order);
                Array.Sort(order, 0, n);                       // the kept ones in file order
            }

            SplatAvatarData d = new()
            {
                Count = n, Header = Header, Role = Role, SourcePath = SourcePath, RestJoints = RestJoints, Parents = Parents,
                Mean = new float3[n], Rotation = new float4[n], LogScale = new float3[n], OpacityLogit = new float[n],
                Sh0 = new float3[n], JointIndex = new int4[n], Weight = new float4[n], Hair = new byte[n],
                Strand = new int[n], Segment = new int[n], LocalOffset = new float3[n], LocalRotation = new float4[n]
            };
            int hair = 0, bound = 0, boundBefore = BoundHairCount;
            clampedCount = 0;
            double opacityKept = 0, opacityAll = 0;
            for (int i = 0; i < Count; i++) opacityAll += 1.0 / (1.0 + Math.Exp(-OpacityLogit[i]));
            for (int k = 0; k < n; k++)
            {
                int i = order[k];
                d.Mean[k] = Mean[i];
                d.Rotation[k] = Rotation[i];
                float3 ls = LogScale[i];
                if (math.cmax(ls) > maxLog)
                {
                    ls = math.min(ls, maxLog);
                    clampedCount++;
                }

                d.LogScale[k] = ls;
                d.OpacityLogit[k] = OpacityLogit[i];
                opacityKept += 1.0 / (1.0 + Math.Exp(-OpacityLogit[i]));
                d.Sh0[k] = Sh0[i];
                d.JointIndex[k] = JointIndex[i];
                d.Weight[k] = Weight[i];
                d.Hair[k] = Hair[i];
                d.Strand[k] = Strand[i];
                d.Segment[k] = Segment[i];
                d.LocalOffset[k] = LocalOffset[i];
                d.LocalRotation[k] = LocalRotation[i];
                if (d.Hair[k] != 0) hair++;
                if (d.Strand[k] >= 0) bound++;
            }

            d.HairCount = hair;
            d.BoundHairCount = bound;
            report = $"{n} of {Count} Gaussians kept ({100.0 * opacityKept / Math.Max(opacityAll, 1e-9):0.0} % of the summed opacity), " +
                     $"{clampedCount} clamped to {(maxSigma > 0f ? $"{maxSigma * 100f:0.##} cm" : "-")}, bound hair {bound} of {boundBefore}";
            return d;
        }

        static float3 F3(byte[] b, int o) => new(BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8));

        static float4 F4(byte[] b, int o) => new(BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4),
            BitConverter.ToSingle(b, o + 8), BitConverter.ToSingle(b, o + 12));
    }
}
