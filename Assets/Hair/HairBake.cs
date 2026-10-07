using System;
using System.Collections.Generic;
using HeadMovementHair;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>
/// The hair bake: after load the whole take is simulated in the background, a chunk of capture frames per job
/// (BakeChunkJob: one Burst job over the strands, each strand runs the chunk's frames on its own; the main thread only
/// reads the chunk's kinematics: PoseBones + head pose + capsules, then restores the shown pose). Frame 0 = re-hang +
/// static settle in frame 0's pose. Every frame's guide points are cached relative to the head (half precision:
/// 16 KB per frame for 160 x 17 points, 2.9 MB for the 6 s take, 18.6 MB for a 38 s take). Seek, loop, restart and slow
/// motion are then an O(1) lookup: no synchronous re-hang hitch, deterministic, capture-frame locked as before. A
/// dynamics change (hm_hair set) re-bakes; frames not baked yet use the live simulation.
/// </summary>
public partial class HairStrands
{
    [Header("Bake")]
    public int BakeChunkFrames = 24;

    NativeArray<float3> bkPos, bkPrev, bkRest, bkHeadPos;
    NativeArray<float> bkSegLen, bkBendRest, bkShapeK, bkDt;
    NativeArray<float2> bkInfo;
    NativeArray<int> bkResets;
    NativeArray<Capsule> bkCaps;
    NativeArray<quaternion> bkHeadRot;
    NativeArray<half3> bkCache, bkChunk; // bkChunk: the running job's output (the main thread never reads a job's buffer)
    JobHandle bkHandle;
    bool bkRunning;
    int bkDone, bkChunkEnd, bkFrames, bkChunks;
    double bkWallMs, bkMainMs, bkJobMs;
    readonly Stopwatch bkWatch = new(), bkChunkWatch = new();
    float3[] bkPrevA, bkPrevB;
    Kin bkKin;

    public bool BakeStarted => bkCache.IsCreated;
    public bool BakeComplete => bkCache.IsCreated && bkDone >= bkFrames;
    public int BakedFrames => bkCache.IsCreated ? bkDone : 0;

    bool BakeHas(int f) => UseBake && bkCache.IsCreated && f >= 0 && f < bkDone;

    void StartBake()
    {
        StopBake();
        bkFrames = avatar != null && avatar.Motion != null ? avatar.FrameCount : 0;
        if (bkFrames <= 0 || !restLocal.IsCreated || colliderDefs == null) return;
        int n = guides * points;
        bkPos = new NativeArray<float3>(n, Allocator.Persistent);
        bkPrev = new NativeArray<float3>(n, Allocator.Persistent);
        bkRest = new NativeArray<float3>(restLocal, Allocator.Persistent);
        bkSegLen = new NativeArray<float>(segLen, Allocator.Persistent);
        bkBendRest = new NativeArray<float>(bendRest, Allocator.Persistent);
        UpdateShapeStiffness();
        UpdateGuideInfo();
        bkShapeK = new NativeArray<float>(shapeK, Allocator.Persistent);
        bkInfo = new NativeArray<float2>(guideInfo, Allocator.Persistent);
        bkResets = new NativeArray<int>(guides, Allocator.Persistent);
        bkCaps = new NativeArray<Capsule>(bkFrames * colliderDefs.Length, Allocator.Persistent);
        bkHeadPos = new NativeArray<float3>(bkFrames, Allocator.Persistent);
        bkHeadRot = new NativeArray<quaternion>(bkFrames, Allocator.Persistent);
        bkDt = new NativeArray<float>(bkFrames, Allocator.Persistent);
        bkCache = new NativeArray<half3>(bkFrames * n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        bkChunk = new NativeArray<half3>(Math.Max(1, BakeChunkFrames) * n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        bkPrevA = new float3[colliderDefs.Length];
        bkPrevB = new float3[colliderDefs.Length];
        bkKin = NewKin();
        bkDone = 0;
        bkChunks = 0;
        bkWallMs = bkMainMs = bkJobMs = 0;
        bkWatch.Restart();
    }

    void StopBake()
    {
        if (bkRunning) bkHandle.Complete();
        bkRunning = false;
        foreach (IDisposable d in new IDisposable[] { bkPos, bkPrev, bkRest, bkHeadPos, bkSegLen, bkBendRest, bkShapeK, bkDt, bkInfo, bkResets, bkCaps, bkHeadRot, bkCache, bkChunk })
        {
            try
            {
                d.Dispose();
            }
            catch (Exception)
            {
                // not allocated
            }
        }

        bkPos = default;
        bkPrev = default;
        bkRest = default;
        bkHeadPos = default;
        bkSegLen = default;
        bkBendRest = default;
        bkShapeK = default;
        bkDt = default;
        bkInfo = default;
        bkResets = default;
        bkCaps = default;
        bkHeadRot = default;
        bkCache = default;
        bkChunk = default;
        bkDone = 0;
    }

    /// <summary>finish a completed chunk and schedule the next one; block = run the whole bake to the end now</summary>
    public void PumpBake(bool block)
    {
        if (!bkCache.IsCreated) return;
        while (true)
        {
            if (bkRunning)
            {
                if (!block && !bkHandle.IsCompleted) return;
                bkHandle.Complete();
                bkRunning = false;
                bkJobMs += bkChunkWatch.Elapsed.TotalMilliseconds;
                int was = bkDone;
                int per = guides * points;
                NativeArray<half3>.Copy(bkChunk, 0, bkCache, was * per, (bkChunkEnd - was) * per);
                bkDone = bkChunkEnd;
                if (simFrame >= was && simFrame < bkDone) meshDirty = true; // the shown frame is baked now: switch to it
                if (bkDone >= bkFrames) bkWallMs = bkWatch.Elapsed.TotalMilliseconds;
            }

            if (bkDone >= bkFrames) return;
            ScheduleChunk();
            if (!block) return;
        }
    }

    void ScheduleChunk()
    {
        Stopwatch main = Stopwatch.StartNew();
        int nc = colliderDefs.Length;
        int k0 = bkDone, k1 = Math.Min(bkFrames, k0 + Math.Max(1, bkChunk.Length / (guides * points)));
        for (int f = k0; f < k1; f++)
        {
            avatar.PoseBones(f);
            ReadKinematics(ref bkKin);
            quaternion q = bkKin.HeadRot;
            if (f > 0 && math.dot(bkHeadRot[f - 1].value, q.value) < 0f) q = new quaternion(-q.value);
            bkHeadPos[f] = bkKin.HeadPos;
            bkHeadRot[f] = q;
            bkDt[f] = FrameDt(f);
            for (int c = 0; c < nc; c++)
            {
                float3 a0 = f > 0 ? bkPrevA[c] : bkKin.A[c], b0 = f > 0 ? bkPrevB[c] : bkKin.B[c];
                bkCaps[f * nc + c] = MakeCapsule(c, a0, b0, bkKin.A[c], bkKin.B[c]);
                bkPrevA[c] = bkKin.A[c];
                bkPrevB[c] = bkKin.B[c];
            }
        }

        if (avatar.CurrentFrame >= 0) avatar.PoseBones(avatar.CurrentFrame); // the shown pose
        SimParams p = BaseParams();
        p.capCount = nc;
        BakeChunkJob job = new()
        {
            pos = bkPos, prev = bkPrev, restLocal = bkRest, segLen = bkSegLen, bendRest = bkBendRest, shapeK = bkShapeK,
            guideInfo = bkInfo, capsules = bkCaps, headPos = bkHeadPos, headRot = bkHeadRot, frameDt = bkDt, resets = bkResets,
            cache = bkChunk, p = p, frameStart = k0, frameEnd = k1, guides = guides, capCount = nc,
            settleSubsteps = SettleSeconds > 0f ? Mathf.Max(1, Mathf.RoundToInt(SettleSeconds * 120f)) : 0,
            settleSeconds = SettleSeconds, settleDamping = 10f
        };
        bkMainMs += main.Elapsed.TotalMilliseconds;
        bkChunkWatch.Restart();
        bkHandle = job.Schedule(guides, 1);
        JobHandle.ScheduleBatchedJobs();
        bkRunning = true;
        bkChunkEnd = k1;
        bkChunks++;
    }

    /// <summary>the baked guide points of frame f -> pos (and the head pose the cards / cap / face box use)</summary>
    void BakeLoad(int f)
    {
        float3 h = bkHeadPos[f];
        int c = f * guides * points;
        for (int i = 0; i < guides * points; i++) pos[i] = h + (float3)bkCache[c + i];
        curHeadPos = h;
        curHeadRot = bkHeadRot[f];
    }

    Dictionary<string, object> BakeState()
    {
        int resetsBaked = 0;
        if (bkResets.IsCreated && !bkRunning)
            for (int g = 0; g < guides; g++) resetsBaked += bkResets[g];
        return new Dictionary<string, object>
        {
            ["enabled"] = UseBake, ["started"] = BakeStarted, ["complete"] = BakeComplete, ["frames"] = bkFrames,
            ["bakedFrames"] = BakedFrames, ["chunks"] = bkChunks, ["chunkFrames"] = BakeChunkFrames,
            ["wallMs"] = bkWallMs, ["jobMs"] = bkJobMs, ["mainThreadMs"] = bkMainMs,
            ["msPerFrame"] = bkDone > 0 ? (bkJobMs + bkMainMs) / bkDone : 0.0,
            ["cacheBytes"] = bkCache.IsCreated ? (long)bkCache.Length * 6 : 0L, ["strandResets"] = resetsBaked
        };
    }

    /// <summary>re-bake now (blocking): returns the bake numbers</summary>
    public Dictionary<string, object> Rebake(bool block)
    {
        StartBake();
        if (block) PumpBake(true);
        meshDirty = true;
        return BakeState();
    }
}
