using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Background loading for a lesson on a headset (docs/QUEST.md): the big pure-data loads (pose JSON, physics JSON, the avatars' motion and skin
/// binaries, the jerk series, the layers' JSON) run on worker threads while the main thread keeps rendering the loading screen, and the
/// loader code takes their results with Take(key, loadInline): the result of the prefetch if one was started under that key, else the
/// load run inline - so every caller behaves exactly as before when nothing was prefetched (the desktop viewer, the recorder).
/// Only code that touches no Unity object may run in a prefetch.
/// </summary>
public static class LoadPrefetch
{
    sealed class Entry
    {
        public Task Task;
        public object Result;
        public Exception Error;
        public double Ms;
    }

    static readonly Dictionary<string, Entry> Entries = new();
    static readonly object Gate = new();
    static readonly SemaphoreSlim Slots = new(2, 2);

    public static int Started { get; private set; }

    /// <summary>start `load` on a worker thread under `key` (a key already started is left alone)</summary>
    public static void Start<T>(string key, Func<T> load)
    {
        lock (Gate)
        {
            if (Entries.ContainsKey(key)) return;
            Entry e = new();
            Entries[key] = e;
            Started++;
            e.Task = Task.Run(() =>
            {
                // at most two loads at a time, and below the main thread's priority: the loading screen is drawn by the main thread and the render thread
                // beside them, and a headset has few big cores - a burst of parsers on all of them starved the frames (the first stalls of a load)
                Slots.Wait();
                Thread th = Thread.CurrentThread;
                System.Threading.ThreadPriority was = th.Priority;
                try
                {
                    th.Priority = System.Threading.ThreadPriority.BelowNormal;
                }
                catch (Exception)
                {
                    // a platform without priorities
                }

                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    e.Result = load();
                }
                catch (Exception ex)
                {
                    e.Error = ex;
                }

                e.Ms = sw.Elapsed.TotalMilliseconds;
                try
                {
                    th.Priority = was;
                }
                catch (Exception)
                {
                    // as above
                }

                Slots.Release();
            });
        }
    }

    public static bool Has(string key)
    {
        lock (Gate) return Entries.ContainsKey(key);
    }

    /// <summary>every prefetch started so far has finished</summary>
    public static bool AllDone
    {
        get
        {
            lock (Gate)
            {
                foreach (Entry e in Entries.Values)
                {
                    if (!e.Task.IsCompleted) return false;
                }

                return true;
            }
        }
    }

    public static bool IsDone(string key)
    {
        lock (Gate) return Entries.TryGetValue(key, out Entry e) && e.Task.IsCompleted;
    }

    /// <summary>the prefetched result for the key (waits if it is still running - the loader waits with IsDone first, a frame at a time), else `inline()`</summary>
    public static T Take<T>(string key, Func<T> inline)
    {
        Entry e;
        lock (Gate) Entries.TryGetValue(key, out e);
        if (e == null) return inline();
        e.Task.Wait();
        if (e.Error != null) throw new InvalidOperationException($"{key}: {e.Error.Message}", e.Error);
        return (T)e.Result;
    }

    public static string Describe()
    {
        lock (Gate)
        {
            List<string> s = new();
            foreach (KeyValuePair<string, Entry> kv in Entries) s.Add($"{kv.Key} {kv.Value.Ms:0} ms{(kv.Value.Error != null ? " FAILED" : "")}");
            return string.Join("; ", s);
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            foreach (Entry e in Entries.Values)
            {
                if (e.Task != null && !e.Task.IsCompleted) continue; // a straggler finishes harmlessly into its own entry
            }

            Entries.Clear();
        }
    }
}
