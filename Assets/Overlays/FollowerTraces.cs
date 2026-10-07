using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Layer "traces" (VIEWER_SPEC 3.7): fading 3D ribbons of the follower's wrists, ankles and head over a window of
/// ~2 measures (bright = now), plus the head axis - a ray from the top of her head along the head's up direction
/// (SMPL-X head orientation from the motion binary; neck->head without it), ~0.6 m into space, whose tip leaves
/// its own trace. During a counterbalance the free extremities (the swinging foot and the free hand) are
/// emphasised: brighter and twice the window. Trace points are precomputed once per capture; per frame the line
/// renderers take a slice of them (no allocations).
/// </summary>
public class FollowerTraces : MonoBehaviour
{
    public float AxisLength = 0.6f;
    public float HeadTopOffset = 0.12f;
    public float Width = 0.009f;

    enum Trace
    {
        LeftWrist,
        RightWrist,
        LeftAnkle,
        RightAnkle,
        Head,
        AxisTip
    }

    static readonly Color[] Colors =
    {
        new(0.35f, 0.9f, 1f), new(0.35f, 0.9f, 1f), // hands: cyan
        new(1f, 0.45f, 0.9f), new(1f, 0.45f, 0.9f), // feet: magenta
        new(1f, 0.95f, 0.75f), // head: warm white
        new(0.6f, 1f, 0.55f) // head-axis tip: green
    };

    const int TraceCount = 6;
    readonly NativeArray<Vector3>[] points = new NativeArray<Vector3>[TraceCount];
    readonly LineRenderer[] lines = new LineRenderer[TraceCount];
    readonly bool[] emphasised = new bool[TraceCount];
    LineRenderer axis;
    Material material;
    int frameCount;
    int window = 120;
    int lastFrame = -1;
    bool visible = true;
    bool dirty = true;

    public bool HeadFromSmplx { get; private set; }
    public int WindowFrames => window;
    public bool Visible => visible;
    public int FrameCount => frameCount;
    public int EmphasisedCount { get; private set; }

    public void Init(Dancer follow, CaptureManifest manifest, CaptureTimeline timeline, float measureSeconds)
    {
        frameCount = follow.FrameCount;
        float frameInterval = timeline.Count > 1 ? (timeline.Last - timeline.First) / (timeline.Count - 1) : 1f / 30f;
        window = Mathf.Max(8, Mathf.RoundToInt(2f * (measureSeconds > 0.5f ? measureSeconds : 3.14f) / frameInterval));

        Vector3[] up = HeadUp(manifest, follow, frameCount);
        for (int k = 0; k < TraceCount; k++) points[k] = new NativeArray<Vector3>(frameCount, Allocator.Persistent);
        for (int f = 0; f < frameCount; f++)
        {
            points[(int)Trace.LeftWrist][f] = follow.Joint(f, SmplJoint.L_Wrist);
            points[(int)Trace.RightWrist][f] = follow.Joint(f, SmplJoint.R_Wrist);
            points[(int)Trace.LeftAnkle][f] = follow.Joint(f, SmplJoint.L_Ankle);
            points[(int)Trace.RightAnkle][f] = follow.Joint(f, SmplJoint.R_Ankle);
            Vector3 top = follow.Joint(f, SmplJoint.Head) + up[f] * HeadTopOffset;
            points[(int)Trace.Head][f] = top;
            points[(int)Trace.AxisTip][f] = top + up[f] * AxisLength;
        }

        material = GlowMesh.NewMaterial("Follower traces glow", 2.2f);
        for (int k = 0; k < TraceCount; k++)
        {
            lines[k] = NewLine($"Follower trace {(Trace)k}", k == (int)Trace.AxisTip ? Width * 0.7f : Width);
            SetGradient(k, false);
        }

        axis = NewLine("Follower head axis", Width * 1.2f);
        axis.positionCount = 2;
        Color ac = Colors[(int)Trace.AxisTip];
        axis.startColor = ac * 0.8f;
        axis.endColor = ac;
        dirty = true;
    }

    LineRenderer NewLine(string name, float width)
    {
        GameObject go = new(name);
        go.transform.SetParent(transform, false);
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.sharedMaterial = material;
        line.useWorldSpace = true;
        line.widthMultiplier = width;
        line.numCapVertices = 0;
        line.numCornerVertices = 0;
        line.positionCount = 0;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.lightProbeUsage = LightProbeUsage.Off;
        line.reflectionProbeUsage = ReflectionProbeUsage.Off;
        return line;
    }

    void SetGradient(int k, bool emphasis)
    {
        emphasised[k] = emphasis;
        Color c = Colors[k] * (emphasis ? 1f : 0.55f);
        c.a = 1;
        lines[k].colorGradient = new Gradient
        {
            colorKeys = new[] { new GradientColorKey(c, 0f), new GradientColorKey(c, 1f) },
            alphaKeys = new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.35f, 0.6f), new GradientAlphaKey(1f, 1f) }
        };
        lines[k].widthMultiplier = (k == (int)Trace.AxisTip ? Width * 0.7f : Width) * (emphasis ? 1.6f : 1f);
    }

    /// <summary>head up axis per frame: SMPL-X global head rotation * up (pelvis->spine1->spine2->spine3->neck->head)</summary>
    Vector3[] HeadUp(CaptureManifest manifest, Dancer follow, int n)
    {
        Vector3[] up = new Vector3[n];
        string motionPath = manifest?.RolePath(manifest.smplx, Role.Follow);
        SmplxData.Motion motion = null;
        if (motionPath != null)
        {
            try
            {
                motion = SmplxData.ReadMotion(motionPath);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"follower traces: SMPL-X motion unreadable ({e.Message}) - head axis from neck->head");
            }
        }

        int[] chain = { 0, 3, 6, 9, 12, 15 };
        HeadFromSmplx = motion != null && motion.FrameCount > 0;
        for (int f = 0; f < n; f++)
        {
            if (HeadFromSmplx)
            {
                int mf = Mathf.Min(f, motion.FrameCount - 1);
                Quaternion q = Quaternion.identity;
                foreach (int j in chain) q *= motion.Rotations[mf * SmplxData.Joints + j];
                up[f] = (q * Vector3.up).normalized;
            }
            else
            {
                Vector3 d = follow.Joint(f, SmplJoint.Head) - follow.Joint(f, SmplJoint.Neck);
                up[f] = d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.up;
            }
        }

        return up;
    }

    public void SetVisible(bool on)
    {
        if (visible == on) return;
        visible = on;
        foreach (LineRenderer l in lines)
        {
            if (l != null) l.enabled = on;
        }

        if (axis != null) axis.enabled = on;
        dirty = true;
    }

    /// <summary>emphasise the free extremities of a counterbalance (null = none)</summary>
    public void SetEmphasis(CounterbalanceData.Interval active)
    {
        bool any = CounterbalanceOverlay.FollowerFreeLimbs(active, out bool footRight, out bool handRight, out bool footKnown);
        bool[] want = new bool[TraceCount]; // only allocated when an interval starts/ends below
        if (any)
        {
            want[handRight ? (int)Trace.RightWrist : (int)Trace.LeftWrist] = true;
            if (footKnown) want[footRight ? (int)Trace.RightAnkle : (int)Trace.LeftAnkle] = true;
        }

        bool changed = false;
        for (int k = 0; k < TraceCount; k++) changed |= want[k] != emphasised[k];
        if (!changed) return;
        EmphasisedCount = 0;
        for (int k = 0; k < TraceCount; k++)
        {
            if (want[k] != emphasised[k]) SetGradient(k, want[k]);
            if (want[k]) EmphasisedCount++;
        }

        dirty = true;
    }

    /// <summary>cheap check before SetEmphasis (avoids the small array when nothing changes)</summary>
    public bool EmphasisMatches(CounterbalanceData.Interval active)
    {
        if (active == null)
        {
            for (int k = 0; k < TraceCount; k++)
            {
                if (emphasised[k]) return false;
            }

            return true;
        }

        CounterbalanceOverlay.FollowerFreeLimbs(active, out bool footRight, out bool handRight, out bool footKnown);
        int hand = handRight ? (int)Trace.RightWrist : (int)Trace.LeftWrist;
        int foot = footKnown ? (footRight ? (int)Trace.RightAnkle : (int)Trace.LeftAnkle) : -1;
        for (int k = 0; k < TraceCount; k++)
        {
            bool want = k == hand || k == foot;
            if (want != emphasised[k]) return false;
        }

        return true;
    }

    public void SetFrame(int frame)
    {
        if (frameCount == 0 || frame < 0) return;
        frame = Mathf.Min(frame, frameCount - 1);
        if (!dirty && frame == lastFrame) return;
        dirty = false;
        lastFrame = frame;
        if (!visible) return;
        for (int k = 0; k < TraceCount; k++)
        {
            int w = emphasised[k] ? 2 * window : window;
            int start = Mathf.Max(0, frame - w + 1);
            int count = frame - start + 1;
            if (count < 2)
            {
                lines[k].positionCount = 0;
                continue;
            }

            lines[k].positionCount = count;
            lines[k].SetPositions(new NativeSlice<Vector3>(points[k], start, count));
        }

        axis.SetPosition(0, points[(int)Trace.Head][frame]);
        axis.SetPosition(1, points[(int)Trace.AxisTip][frame]);
    }

    public Dictionary<string, object> State() => new()
    {
        ["visible"] = visible, ["windowFrames"] = window, ["headAxisFromSmplx"] = HeadFromSmplx,
        ["emphasised"] = EmphasisedCount, ["frame"] = lastFrame,
        ["points"] = lines[0] != null ? lines[0].positionCount : 0,
        ["headTop"] = lastFrame >= 0 ? V(points[(int)Trace.Head][lastFrame]) : null,
        ["axisTip"] = lastFrame >= 0 ? V(points[(int)Trace.AxisTip][lastFrame]) : null
    };

    static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };

    void OnDestroy()
    {
        for (int k = 0; k < TraceCount; k++)
        {
            if (points[k].IsCreated) points[k].Dispose();
        }

        if (material != null) Destroy(material);
    }
}
