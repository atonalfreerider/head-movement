using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using UnityEditor.Recorder;
using UnityEngine.LowLevel;
using UnityEngine.Rendering;

/// <summary>
/// Frame order in the film recording (FilmRecorder, Job.orderedReadback).
///
/// The Movie Recorder (5.1.7) reads every captured frame back with an AsyncGPUReadback request and hands the pixels to the encoder
/// in the request's COMPLETION callback, taking the frame's timestamp from a FIFO at that moment (MovieRecorder.WriteCPUFrame /
/// BaseTextureRecorder.DequeueTimeStamp): the file is only in film order when the callbacks come in request order. With the splat
/// dancers (D3D12, a GPU busy for 20-25 ms a frame) several requests are in flight, and two that complete in the same update were
/// now and then delivered the other way round: two neighbouring frames swapped in the MP4, picture AND captions (splat films of
/// 2026-10-20: 6-7 swapped pairs in 11032 frames, e.g. vertical frames 154/155 = film 5.13 s are the avatar film's 155/154; the
/// D3D11 avatar films have none).
///
/// While a recording runs, a step in the player loop placed right BEFORE Unity's own readback update (the subsystem whose name has
/// "Readback" in it; the start of Update when none is found) polls the Recorder's outstanding requests in request order
/// (AsyncGPUReadbackRequest.Update + done, no forced flush) until each is done. When Unity's update then dispatches the callbacks,
/// the previous frame's request is the only one there is (the next one is made at the end of this frame), so the frames reach the
/// encoder in film order. The price: the CPU waits for the previous frame's GPU work (a slower render, the same pictures).
/// AsyncGPUReadbackRequest.WaitForCompletion is NOT used: called from Update on D3D12 it crashed the editor on the first frame
/// ("failed to Close a command list (80070057)", 2026-10-21). A request that is not done within PollTimeoutMs is left to the Recorder
/// (counted in readbackTimeouts); after MaxTimeouts the step gives up for the rest of the recording.
/// Statistics go into the job: frames polled, the largest number of frames still unwritten at the step (ordered: 1), frames with
/// more than one, frames whose request was not done in time.
/// </summary>
public static class OrderedReadback
{
    const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public const int PollTimeoutMs = 1000, MaxTimeouts = 20;

    static IList requests;     // MovieRecorder.asyncReadback.requestOrder (List<AsyncGPUReadbackRequest>, request order)
    static ICollection stamps; // BaseTextureRecorder.m_AsyncReadbackTimeStamps (Queue<float>: one per frame requested, not yet written)
    static int next;
    static FilmRecorder.Job job;
    static bool installed;
    static PlayerLoopSystem savedLoop;
    static bool gaveUp;

    /// <summary>the player-loop step's marker type</summary>
    public struct FilmOrderedReadbackStep
    {
    }

    /// <summary>find the Movie Recorder's request list and timestamp queue (after StartRecording) and put the step into the player
    /// loop; returns a status line</summary>
    public static string Begin(RecorderController controller, FilmRecorder.Job j, bool poll = true)
    {
        End();
        job = j;
        next = 0;
        gaveUp = false;
        observeOnly = !poll;
        string bound = Bind(controller);
        if (requests == null) return bound + ": not ordered (the Recorder's own order)";
        bound = observeOnly ? "observe only (the Recorder's own order, statistics only): " + bound : "ordered (polled in request order): " + bound;
        return bound + "; " + Install();
    }

    // --readback observe: the step only counts (how often more than one frame waits for Unity's readback update: the
    // condition under which two frames can be written the other way round), it never polls
    static bool observeOnly;

    static string Bind(RecorderController controller)
    {
        requests = null;
        stamps = null;
        try
        {
            if (controller == null) return "no controller";
            if (typeof(RecorderController).GetField("m_RecordingSessions", Inst)?.GetValue(controller) is not IEnumerable sessions)
                return "RecorderController.m_RecordingSessions not found";
            foreach (object session in sessions)
            {
                object rec = session?.GetType().GetField("recorder", Inst)?.GetValue(session);
                if (rec == null) continue;
                object pool = Field(rec, "asyncReadback");
                if (pool == null) continue;
                requests = Field(pool, "requestOrder") as IList;
                stamps = Field(rec, "m_AsyncReadbackTimeStamps") as ICollection;
                if (requests != null)
                    return $"{rec.GetType().Name}'s requests found" +
                           (stamps != null ? "" : " (timestamp queue not found: no pending statistics)");
            }

            return "the Movie Recorder's request list was not found";
        }
        catch (Exception e)
        {
            requests = null;
            stamps = null;
            return $"reflection failed ({e.Message})";
        }
    }

    /// <summary>a private field of the object's type or any base type</summary>
    static object Field(object o, string name)
    {
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name, Inst | BindingFlags.DeclaredOnly);
            if (f != null) return f.GetValue(o);
        }

        return null;
    }

    static string Install()
    {
        PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
        savedLoop = root;
        PlayerLoopSystem step = new() { type = typeof(FilmOrderedReadbackStep), updateDelegate = Step };
        string where = null;
        PlayerLoopSystem[] phases = root.subSystemList;
        for (int p = 0; phases != null && p < phases.Length && where == null; p++)
        {
            PlayerLoopSystem[] subs = phases[p].subSystemList;
            if (subs == null) continue;
            for (int s = 0; s < subs.Length; s++)
            {
                if (subs[s].type == null || subs[s].type.Name.IndexOf("Readback", StringComparison.OrdinalIgnoreCase) < 0) continue;
                List<PlayerLoopSystem> list = new(subs);
                list.Insert(s, step);
                phases[p].subSystemList = list.ToArray();
                where = $"step before {phases[p].type?.Name}.{subs[s].type.Name}";
                break;
            }
        }

        if (where == null)
        {
            // no readback subsystem by that name: the start of Update
            for (int p = 0; phases != null && p < phases.Length; p++)
            {
                if (phases[p].type != typeof(UnityEngine.PlayerLoop.Update)) continue;
                List<PlayerLoopSystem> list = new(phases[p].subSystemList ?? Array.Empty<PlayerLoopSystem>());
                list.Insert(0, step);
                phases[p].subSystemList = list.ToArray();
                where = "no readback subsystem found: step at the start of Update";
                break;
            }
        }

        if (where == null) return "player loop not changed (no Update phase?)";
        root.subSystemList = phases;
        PlayerLoop.SetPlayerLoop(root);
        installed = true;
        return where;
    }

    /// <summary>take the step out of the player loop (the recording stopped, Play mode ends)</summary>
    public static void End()
    {
        if (installed)
        {
            try
            {
                PlayerLoop.SetPlayerLoop(savedLoop);
            }
            catch (Exception)
            {
                PlayerLoop.SetPlayerLoop(PlayerLoop.GetDefaultPlayerLoop());
            }

            installed = false;
        }

        job = null;
        requests = null;
        stamps = null;
    }

    /// <summary>frames requested by the Recorder and not yet handed to the encoder (-1 = unknown)</summary>
    public static int Pending => stamps?.Count ?? -1;

    static void Step()
    {
        FilmRecorder.Job j = job;
        if (j == null || requests == null || gaveUp) return;
        int before = Pending;
        if (before > j.readbackMaxPending) j.readbackMaxPending = before;
        if (before > 1) j.readbackPendingFrames++;
        if (observeOnly)
        {
            j.readbackFrames++;
            return;
        }

        try
        {
            Stopwatch w = Stopwatch.StartNew();
            for (; next < requests.Count; next++)
            {
                if (requests[next] is not AsyncGPUReadbackRequest r) continue;
                long t0 = w.ElapsedMilliseconds;
                r.Update();
                while (!r.done)
                {
                    if (w.ElapsedMilliseconds - t0 > PollTimeoutMs)
                    {
                        j.readbackTimeouts++;
                        if (j.readbackTimeouts >= MaxTimeouts)
                        {
                            gaveUp = true;
                            j.readbackStatus += $"; gave up after {MaxTimeouts} requests not done within {PollTimeoutMs} ms";
                        }

                        break;
                    }

                    Thread.Sleep(0);
                    r.Update();
                }
            }

            j.readbackWaitMs += w.Elapsed.TotalMilliseconds;
        }
        catch (Exception e)
        {
            gaveUp = true;
            j.readbackStatus += $"; the poll failed ({e.Message}), the Recorder's own order from here";
        }

        j.readbackFrames++;
    }
}
