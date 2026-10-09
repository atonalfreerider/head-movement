using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// One source phone's per-frame pose + zoom (written by the exporter next to the capture: cameras/&lt;id&gt;.track.json).
/// Times are the capture's dance (reference) clock with the phone's video offset already applied; positions are capture
/// coordinates (Unity, DanceOrigin.Offset NOT applied: the caller adds it).
///
/// Every exported sample is kept. The cameras stage localises most frames and FILLS the rest itself (constant-velocity
/// interpolation / smoothing, "ok" = 0 in the file); those filled samples are used, so a short run of unlocalised frames
/// costs nothing, and the exporter's pose policy says how far to trust a long run (<see cref="MaxUnlocalised"/>: seconds
/// between the localised samples that bound the run; fixed-position phones are trusted longer) and when the phone simply
/// was not recording (<see cref="MaxSampleGap"/>: rows further apart than that). The first version dropped the unlocalised
/// rows and refused any hole wider than 0.6f s: a 0.6000023 s hole in phone 06 (float32) blinked the video out of the
/// middle of the only POV shot. TryGetPose, SetVideo and the glyphs all go through <see cref="TryAt"/>, so the pose and the
/// picture cannot disagree about whether a phone is available.
/// </summary>
public sealed class CameraVideoTrack
{
    public string Id;
    public Vector2 Size;          // native frame size in px (the intrinsics' frame)
    public float[] T;
    public Vector3[] Pos;
    public Quaternion[] Rot;
    public float[] Vfov;
    /// <summary>per sample: seconds between the localised samples that bound the run of unlocalised samples it belongs to
    /// (0 = localised)</summary>
    public float[] RunSeconds;

    /// <summary>the longest run of unlocalised frames bridged when the track file carries no policy (older exports);
    /// film/check_direction.py uses the same number as its default</summary>
    public const float MaxBridgeSeconds = 1.5f;
    const float Eps = 1e-3f;

    /// <summary>longest unlocalised run (s between its localised neighbours) a query is answered across</summary>
    public float MaxUnlocalised = MaxBridgeSeconds;
    /// <summary>rows further apart than this: the phone was not recording, no pose between them</summary>
    public float MaxSampleGap = 1.0f;
    public bool FixedPosition;
    public int UnlocalisedCount;
    public float LongestRun;

    public int Count => T?.Length ?? 0;
    public float T0 => T[0];
    public float T1 => T[T.Length - 1];
    public float Aspect => Size.y > 0f ? Size.x / Size.y : 9f / 16f;

    public static CameraVideoTrack Load(string path)
    {
        JObject j = JObject.Parse(File.ReadAllText(path));
        JArray t = (JArray)j["t"], pos = (JArray)j["pos"], fwd = (JArray)j["fwd"], up = (JArray)j["up"], vf = (JArray)j["vfov"];
        JArray ok = j["ok"] as JArray;
        int n = t.Count;
        List<float> ts = new(n), vs = new(n);
        List<Vector3> ps = new(n);
        List<Quaternion> rs = new(n);
        List<bool> localised = new(n);
        for (int i = 0; i < n; i++)
        {
            Vector3 f = V3(fwd[i]), u = V3(up[i]);
            if (f.sqrMagnitude < 0.5f || u.sqrMagnitude < 0.5f) continue;
            ts.Add(t[i].Value<float>());
            ps.Add(V3(pos[i]));
            rs.Add(Quaternion.LookRotation(f, u));
            vs.Add(vf[i].Value<float>());
            localised.Add(ok == null || ok.Count != n || ok[i].Value<int>() != 0);
        }

        int m = ts.Count;
        bool any = false;
        foreach (bool b in localised) any |= b;
        if (!any)
        {
            // nothing localised: use every sample rather than lose the phone
            for (int i = 0; i < m; i++) localised[i] = true;
        }

        float[] run = new float[m];
        int unloc = 0;
        float longest = 0f;
        for (int i = 0; i < m; i++)
        {
            if (localised[i]) continue;
            int k = i;
            while (k + 1 < m && !localised[k + 1]) k++;
            float tPrev = i > 0 ? ts[i - 1] : ts[i], tNext = k + 1 < m ? ts[k + 1] : ts[k];
            float len = tNext - tPrev;
            for (int q = i; q <= k; q++) run[q] = len;
            unloc += k - i + 1;
            longest = Mathf.Max(longest, len);
            i = k;
        }

        JArray size = j["size"] as JArray;
        return new CameraVideoTrack
        {
            Id = j.Value<string>("id"), Size = size != null && size.Count == 2 ? new Vector2(size[0].Value<float>(), size[1].Value<float>()) : new Vector2(9, 16),
            T = ts.ToArray(), Pos = ps.ToArray(), Rot = rs.ToArray(), Vfov = vs.ToArray(), RunSeconds = run,
            MaxUnlocalised = j.Value<float?>("max_unlocalised_s") ?? MaxBridgeSeconds, MaxSampleGap = j.Value<float?>("max_sample_gap_s") ?? 1.0f,
            FixedPosition = j.Value<bool?>("fixed_position") ?? false, UnlocalisedCount = unloc, LongestRun = longest
        };
    }

    static Vector3 V3(JToken a) => new(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>());

    /// <summary>pose at a dance time. False when the time lies more than <paramref name="tolerance"/> s outside the track
    /// (the end poses are held within the tolerance), between two rows further apart than <see cref="MaxSampleGap"/>
    /// (the phone was not recording), or inside a run of unlocalised frames longer than <see cref="MaxUnlocalised"/>.</summary>
    public bool TryAt(float t, float tolerance, out Vector3 pos, out Quaternion rot, out float vfov)
    {
        pos = Vector3.zero;
        rot = Quaternion.identity;
        vfov = 60f;
        int n = Count;
        if (n == 0 || t < T[0] - tolerance || t > T[n - 1] + tolerance) return false;
        if (t <= T[0] || n == 1)
        {
            if (RunSeconds[0] > MaxUnlocalised + Eps) return false;
            pos = Pos[0];
            rot = Rot[0];
            vfov = Vfov[0];
            return true;
        }

        if (t >= T[n - 1])
        {
            if (RunSeconds[n - 1] > MaxUnlocalised + Eps) return false;
            pos = Pos[n - 1];
            rot = Rot[n - 1];
            vfov = Vfov[n - 1];
            return true;
        }

        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (T[mid] <= t) lo = mid;
            else hi = mid;
        }

        float span = T[hi] - T[lo];
        if (span > MaxSampleGap + Eps) return false;
        if (Mathf.Max(RunSeconds[lo], RunSeconds[hi]) > MaxUnlocalised + Eps) return false;
        float k = span > 1e-6f ? Mathf.Clamp01((t - T[lo]) / span) : 0f;
        pos = Vector3.Lerp(Pos[lo], Pos[hi], k);
        rot = Quaternion.Slerp(Rot[lo], Rot[hi], k);
        vfov = Mathf.Lerp(Vfov[lo], Vfov[hi], k);
        return true;
    }

    /// <summary>why <see cref="TryAt"/> says no at a time (null = it says yes); diagnostics and the director's warnings</summary>
    public string WhyNot(float t, float tolerance)
    {
        int n = Count;
        if (n == 0) return "no track samples";
        if (t < T[0] - tolerance || t > T[n - 1] + tolerance) return $"outside the phone's track ({T[0]:0.0}..{T[n - 1]:0.0} s)";
        if (TryAt(t, tolerance, out _, out _, out _)) return null;
        int hi = 0;
        while (hi < n - 1 && T[hi] < t) hi++;
        int lo = Mathf.Max(0, hi - 1);
        if (T[hi] - T[lo] > MaxSampleGap + Eps) return $"the phone was not recording ({T[hi] - T[lo]:0.0} s without frames)";
        return $"{Mathf.Max(RunSeconds[lo], RunSeconds[hi]):0.0} s without a localised frame (limit {MaxUnlocalised:0.0} s)";
    }
}

/// <summary>
/// A phone's frames as written by the exporter (.hmj): JPEG payloads back to back, then a table (uint64 offset, uint32
/// length, float64 reference time) per frame and a 24-byte trailer 'HMJ1', uint32 count, uint64 table offset,
/// uint32 width, uint32 height. Random access, decoded on demand with ImageConversion.LoadImage: exactly reproducible
/// under the Recorder's frame-locked clock (no VideoPlayer, no decode threads, no codec).
/// </summary>
public sealed class CameraVideoStrip : IDisposable
{
    const int TrailerSize = 24;
    public string FilePath { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Count { get; private set; }
    public double[] Time { get; private set; }
    long[] offsets;
    int[] lengths;
    FileStream fs;

    public static CameraVideoStrip Open(string path)
    {
        CameraVideoStrip s = new() { FilePath = path };
        s.fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        BinaryReader r = new(s.fs);
        s.fs.Seek(-TrailerSize, SeekOrigin.End);
        byte[] magic = r.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != 'H' || magic[1] != 'M' || magic[2] != 'J' || magic[3] != '1')
        {
            s.Dispose();
            throw new InvalidDataException($"{path}: not an HMJ1 frame strip");
        }

        int count = (int)r.ReadUInt32();
        long table = (long)r.ReadUInt64();
        s.Width = (int)r.ReadUInt32();
        s.Height = (int)r.ReadUInt32();
        s.Count = count;
        s.offsets = new long[count];
        s.lengths = new int[count];
        s.Time = new double[count];
        s.fs.Seek(table, SeekOrigin.Begin);
        byte[] raw = r.ReadBytes(count * 20);
        for (int i = 0; i < count; i++)
        {
            int o = i * 20;
            s.offsets[i] = (long)BitConverter.ToUInt64(raw, o);
            s.lengths[i] = (int)BitConverter.ToUInt32(raw, o + 8);
            s.Time[i] = BitConverter.ToDouble(raw, o + 12);
        }

        return s;
    }

    public double T0 => Time[0];
    public double T1 => Time[Count - 1];

    /// <summary>index of the frame nearest to t</summary>
    public int Nearest(double t)
    {
        int lo = 0, hi = Count - 1;
        if (t <= Time[0]) return 0;
        if (t >= Time[hi]) return hi;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (Time[mid] <= t) lo = mid;
            else hi = mid;
        }

        return t - Time[lo] <= Time[hi] - t ? lo : hi;
    }

    /// <summary>decode frame i into the texture (any size: LoadImage resizes it)</summary>
    public bool Read(int index, Texture2D target)
    {
        if (fs == null)
        {
            fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        }

        if (index < 0 || index >= Count) return false;
        byte[] buf = new byte[lengths[index]];
        fs.Seek(offsets[index], SeekOrigin.Begin);
        int got = 0;
        while (got < buf.Length)
        {
            int n = fs.Read(buf, got, buf.Length - got);
            if (n <= 0) return false;
            got += n;
        }

        return ImageConversion.LoadImage(target, buf, false);
    }

    /// <summary>close the file handle (re-opened on the next Read)</summary>
    public void Close()
    {
        fs?.Dispose();
        fs = null;
    }

    public void Dispose() => Close();
}
