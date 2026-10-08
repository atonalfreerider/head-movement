using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Layer "traces" (VIEWER_SPEC 3.7, user 2026-10-07): very brief fading ribbons behind the follower's FREE extremities
/// only - a hand that is not attached to the leader, a foot in a real leg gesture (holding hands, a hand on his body,
/// a standing, stepping, sliding or pivoting foot do not count) - so what remains is the spiral / whip of a free limb,
/// e.g. her swinging leg and free hand in a counterbalance pivot.
///   attached hand: her hand (wrist..hand midpoint) within AttachOnM of the leader's hands, arms, shoulders, chest,
///                  back, neck or head (released beyond AttachOffM: hysteresis);
///   free foot:     a LEG GESTURE, not a step (review 2026-10-07: walking steps drew trails). Foot height h = the lower
///                  of the foot joint and ankle - 6 cm, above her floor level (3rd percentile). A candidate run is a
///                  stretch of frames off the floor band (h &gt; FloorBandM, 4 cm: a foot sliding or pivoting at floor
///                  height counts as on the floor) and not in stance (SkeletonStyle.FootStance); it is drawn only if it
///                  peaks at GestureHeightM (15 cm) or more, or lasts LongRunSeconds (0.6 s) and peaks at LongRunHeightM
///                  (12 cm), or is the free leg of a counterbalance (the foot that is not the pivot foot during an
///                  interval of counterbalance.json) and peaks at CounterbalanceLegHeightM (8 cm). In effect a
///                  hysteresis: on at the gesture height, off back at the floor band, the whole lift-off..landing kept.
///                  An ordinary step (median peak ~6 cm, &lt; 0.6 s) never draws;
///   free hand runs shorter than MinFreeSeconds are ignored (contact flicker);
///   head (optional): its trail shows only while her neck axis is at FULL strength (NeckAxisOverlay ramp = 1, i.e. off
///                  her neutral by FullDeg or more), so it does not double the fading-in neck axis.
/// Each trail covers the last WindowSeconds (~0.35 s) of the current free run, bright at the extremity and fading to
/// nothing at its tail; when the limb attaches, the rest of the trail fades out over the same window. During a
/// counterbalance the free extremities are brighter (emphasis). Precomputed per capture; per frame each line takes up
/// to window+1 points from a reused buffer (no allocations).
/// </summary>
public class FollowerTraces : MonoBehaviour
{
    public float HeadTopOffset = 0.12f;
    public float Width = 0.012f;
    public float WindowSeconds = 0.35f;
    public float AttachOnM = 0.12f;
    public float AttachOffM = 0.17f;
    public float MinFreeSeconds = 0.1f;

    [Tooltip("m: a foot this close to her floor level is on the floor (sliding / pivoting feet do not trail)")]
    public float FloorBandM = 0.04f;

    [Tooltip("m: a free foot run must peak at least this high above her floor (a leg gesture, not a step)")]
    public float GestureHeightM = 0.15f;

    [Tooltip("s: ... or last at least this long and peak at least LongRunHeightM")]
    public float LongRunSeconds = 0.6f;

    public float LongRunHeightM = 0.12f;

    [Tooltip("m: the free leg of a counterbalance needs only this peak")]
    public float CounterbalanceLegHeightM = 0.08f;

    [Tooltip("neck-axis ramp at which the head trail shows (1 = only at full strength)")]
    public float HeadGateMin = 0.999f;

    enum Trace
    {
        LeftWrist,
        RightWrist,
        LeftAnkle,
        RightAnkle,
        Head
    }

    static readonly Color[] Colors =
    {
        new(0.35f, 0.9f, 1f), new(0.35f, 0.9f, 1f), // hands: cyan
        new(1f, 0.45f, 0.9f), new(1f, 0.45f, 0.9f), // feet: magenta
        new(1f, 0.95f, 0.75f) // head: warm white
    };

    const int TraceCount = 5;
    readonly Vector3[][] points = new Vector3[TraceCount][];
    readonly bool[][] free = new bool[TraceCount][];
    readonly LineRenderer[] lines = new LineRenderer[TraceCount];
    readonly bool[] emphasised = new bool[TraceCount];
    readonly Gradient[] gradients = new Gradient[TraceCount];
    readonly GradientColorKey[] colourKeys = new GradientColorKey[2];
    readonly GradientAlphaKey[] alphaKeys = new GradientAlphaKey[3];
    Vector3[] buffer = new Vector3[64];
    double[] times;
    Material material;
    int frameCount;
    int window = 11;
    int lastFrame = -1;
    bool visible = true;
    bool dirty = true;
    float[] headGate;
    readonly float[][] footHeight = new float[2][];
    readonly List<int[]>[] footRuns = { new(), new() }; // per foot: the drawn gesture runs [first, end)
    readonly int[] footCandidates = new int[2];

    public bool HeadFromSmplx { get; private set; }
    public int WindowFrames => window;
    public bool Visible => visible;
    public int FrameCount => frameCount;
    public int EmphasisedCount { get; private set; }
    public int ShownCount { get; private set; }

    public void Init(Dancer follow, Dancer lead, CaptureManifest manifest, CaptureTimeline timeline, float measureSeconds,
        CounterbalanceData counterbalance = null)
    {
        frameCount = follow.FrameCount;
        times = new double[frameCount];
        for (int f = 0; f < frameCount; f++) times[f] = timeline.AudioTimeOf(f);
        float frameInterval = timeline.Count > 1 ? (timeline.Last - timeline.First) / (timeline.Count - 1) : 1f / 30f;
        window = Mathf.Max(3, Mathf.RoundToInt(WindowSeconds / frameInterval));

        Vector3[] up = HeadUp(manifest, follow, frameCount);
        for (int k = 0; k < TraceCount; k++)
        {
            points[k] = new Vector3[frameCount];
            free[k] = new bool[frameCount];
        }

        for (int f = 0; f < frameCount; f++)
        {
            points[(int)Trace.LeftWrist][f] = follow.Joint(f, SmplJoint.L_Wrist);
            points[(int)Trace.RightWrist][f] = follow.Joint(f, SmplJoint.R_Wrist);
            points[(int)Trace.LeftAnkle][f] = follow.Joint(f, SmplJoint.L_Ankle);
            points[(int)Trace.RightAnkle][f] = follow.Joint(f, SmplJoint.R_Ankle);
            points[(int)Trace.Head][f] = follow.Joint(f, SmplJoint.Head) + up[f] * HeadTopOffset;
        }

        // hands: attached to the leader (hysteresis)
        int n = Mathf.Min(frameCount, lead != null ? lead.FrameCount : 0);
        foreach ((Trace tr, bool left) in new[] { (Trace.LeftWrist, true), (Trace.RightWrist, false) })
        {
            bool attached = false;
            for (int f = 0; f < frameCount; f++)
            {
                float d = f < n ? LeadDistance(lead, f, left ? follow.GetLeftHandContact(f) : follow.GetRightHandContact(f)) : float.MaxValue;
                attached = attached ? d < AttachOffM : d < AttachOnM;
                free[(int)tr][f] = !attached;
            }
        }

        // feet: free only in a leg gesture (not a step, not a foot at floor height)
        int minRunFrames = Mathf.Max(1, Mathf.RoundToInt(MinFreeSeconds / frameInterval));
        for (int side = 0; side < 2; side++)
        {
            bool right = side == 1;
            SmplJoint footJ = right ? SmplJoint.R_Foot : SmplJoint.L_Foot, ankleJ = right ? SmplJoint.R_Ankle : SmplJoint.L_Ankle;
            bool[] stance = SkeletonStyle.FootStance(follow, frameCount, frameInterval, footJ, ankleJ);
            float[] h = SkeletonStyle.FootHeight(follow, frameCount, footJ, ankleJ);
            footHeight[side] = h;
            bool[] cbLeg = CounterbalanceFreeLeg(counterbalance, right);
            bool[] fr = free[(int)(right ? Trace.RightAnkle : Trace.LeftAnkle)];
            footRuns[side].Clear();
            footCandidates[side] = 0;
            int i = 0;
            while (i < frameCount)
            {
                if (stance[i] || !(h[i] > FloorBandM))
                {
                    i++;
                    continue;
                }

                int j = i;
                float peak = 0f;
                bool inCounterbalance = false;
                while (j < frameCount && !stance[j] && h[j] > FloorBandM)
                {
                    peak = Mathf.Max(peak, h[j]);
                    inCounterbalance |= cbLeg != null && cbLeg[j];
                    j++;
                }

                footCandidates[side]++;
                float seconds = (j - i) * frameInterval;
                bool gesture = j - i >= minRunFrames && (peak >= GestureHeightM || (seconds >= LongRunSeconds && peak >= LongRunHeightM) ||
                                                         (inCounterbalance && peak >= CounterbalanceLegHeightM));
                if (gesture)
                {
                    for (int k = i; k < j; k++) fr[k] = true;
                    footRuns[side].Add(new[] { i, j });
                }

                i = j;
            }
        }

        for (int f = 0; f < frameCount; f++) free[(int)Trace.Head][f] = false; // until SetHeadGate
        for (int k = 0; k < 2; k++) DropShortRuns(free[k], minRunFrames); // hands (feet: whole gesture runs above)

        material = GlowMesh.NewMaterial("Follower traces glow", 2.2f);
        for (int k = 0; k < TraceCount; k++)
        {
            lines[k] = NewLine($"Follower trace {(Trace)k}", Width);
            gradients[k] = new Gradient();
            SetGradient(k, false, 1f);
        }

        dirty = true;
    }

    /// <summary>head trail gate per frame (0..1, the neck axis ramp): the head trail shows only while it is at least
    /// HeadGateMin (full strength by default)</summary>
    public void SetHeadGate(float[] gate)
    {
        headGate = gate;
        if (gate == null) return;
        bool[] h = free[(int)Trace.Head];
        for (int f = 0; f < frameCount && f < gate.Length; f++) h[f] = gate[f] >= HeadGateMin;
        dirty = true;
    }

    /// <summary>per frame: this foot is the free (non-pivot) leg of an active counterbalance interval (null = none)</summary>
    bool[] CounterbalanceFreeLeg(CounterbalanceData cb, bool right)
    {
        if (cb == null || cb.Intervals.Count == 0) return null;
        bool[] o = new bool[frameCount];
        bool any = false;
        foreach (CounterbalanceData.Interval iv in cb.Intervals)
        {
            if (!CounterbalanceOverlay.FollowerFreeLimbs(iv, out bool freeFootRight, out _, out bool footKnown) || !footKnown) continue;
            if (freeFootRight != right) continue;
            for (int f = 0; f < frameCount; f++)
            {
                if (times[f] >= iv.T0 && times[f] <= iv.T1)
                {
                    o[f] = true;
                    any = true;
                }
            }
        }

        return any ? o : null;
    }

    static void DropShortRuns(bool[] a, int minRun)
    {
        int i = 0;
        while (i < a.Length)
        {
            if (!a[i])
            {
                i++;
                continue;
            }

            int j = i;
            while (j < a.Length && a[j]) j++;
            if (j - i < minRun)
            {
                for (int k = i; k < j; k++) a[k] = false;
            }

            i = j;
        }
    }

    /// <summary>distance from a point to the leader's hands, arms, shoulders, torso, neck and head (segments)</summary>
    static float LeadDistance(Dancer lead, int f, Vector3 p)
    {
        float best = float.MaxValue;
        (SmplJoint a, SmplJoint b)[] segs =
        {
            (SmplJoint.L_Hand, SmplJoint.L_Wrist), (SmplJoint.L_Wrist, SmplJoint.L_Elbow), (SmplJoint.L_Elbow, SmplJoint.L_Shoulder),
            (SmplJoint.R_Hand, SmplJoint.R_Wrist), (SmplJoint.R_Wrist, SmplJoint.R_Elbow), (SmplJoint.R_Elbow, SmplJoint.R_Shoulder),
            (SmplJoint.L_Shoulder, SmplJoint.R_Shoulder), (SmplJoint.Pelvis, SmplJoint.Spine3), (SmplJoint.Spine3, SmplJoint.Neck),
            (SmplJoint.Neck, SmplJoint.Head)
        };
        foreach ((SmplJoint a, SmplJoint b) in segs)
        {
            Vector3 pa = lead.Joint(f, a), pb = lead.Joint(f, b);
            if (float.IsNaN(pa.x) || float.IsNaN(pb.x)) continue;
            Vector3 ab = pb - pa;
            float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - pa, ab) / ab.sqrMagnitude) : 0f;
            float d = Vector3.Distance(p, pa + ab * t);
            // the torso / head are thick: measure to their surface (~10 cm), the limbs to ~4 cm
            bool trunk = a == SmplJoint.Pelvis || a == SmplJoint.Spine3 || a == SmplJoint.Neck;
            d -= trunk ? 0.10f : 0.04f;
            if (d < best) best = d;
        }

        return best;
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
        AnimationCurve taper = new();
        taper.AddKey(0f, 0.25f);
        taper.AddKey(1f, 1f);
        line.widthCurve = taper;
        return line;
    }

    void SetGradient(int k, bool emphasis, float fade)
    {
        emphasised[k] = emphasis;
        Color c = Colors[k] * (emphasis ? 1.3f : 0.9f) * fade;
        c.a = 1;
        colourKeys[0] = new GradientColorKey(c, 0f);
        colourKeys[1] = new GradientColorKey(c, 1f);
        // fast fade: nothing at the tail, a quarter at 60 %, full at the extremity
        alphaKeys[0] = new GradientAlphaKey(0f, 0f);
        alphaKeys[1] = new GradientAlphaKey(0.25f, 0.6f);
        alphaKeys[2] = new GradientAlphaKey(1f, 1f);
        gradients[k].SetKeys(colourKeys, alphaKeys);
        lines[k].colorGradient = gradients[k];
        lines[k].widthMultiplier = Width * (emphasis ? 1.5f : 1f);
    }

    /// <summary>head up direction per frame (where the top of her head is): SMPL-X global head rotation * up
    /// (pelvis->spine1->spine2->spine3->neck->head)</summary>
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
                Debug.LogWarning($"follower traces: SMPL-X motion unreadable ({e.Message}) - head direction from neck->head");
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

        dirty = true;
    }

    readonly bool[] want = new bool[TraceCount];

    /// <summary>emphasise the free extremities of a counterbalance (null = none)</summary>
    public void SetEmphasis(CounterbalanceData.Interval active)
    {
        bool any = CounterbalanceOverlay.FollowerFreeLimbs(active, out bool footRight, out bool handRight, out bool footKnown);
        for (int k = 0; k < TraceCount; k++) want[k] = false;
        if (any)
        {
            want[handRight ? (int)Trace.RightWrist : (int)Trace.LeftWrist] = true;
            if (footKnown) want[footRight ? (int)Trace.RightAnkle : (int)Trace.LeftAnkle] = true;
        }

        EmphasisedCount = 0;
        for (int k = 0; k < TraceCount; k++)
        {
            if (want[k] != emphasised[k])
            {
                emphasised[k] = want[k];
                dirty = true;
            }

            if (want[k]) EmphasisedCount++;
        }
    }

    /// <summary>cheap check before SetEmphasis</summary>
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
            bool w = k == hand || k == foot;
            if (w != emphasised[k]) return false;
        }

        return true;
    }

    int lastOther = -1;
    float lastOtherWeight;

    /// <param name="otherFrame">sub-frame time (the film director's slow motion): the neighbouring pose frame the
    /// trail's live end blends toward (-1: none), by <paramref name="otherWeight"/> (0..0.5)</param>
    public void SetFrame(int frame, int otherFrame = -1, float otherWeight = 0f)
    {
        if (frameCount == 0 || frame < 0) return;
        frame = Mathf.Min(frame, frameCount - 1);
        if (otherFrame >= frameCount) otherFrame = -1;
        if (!dirty && frame == lastFrame && otherFrame == lastOther && Mathf.Abs(otherWeight - lastOtherWeight) < 1e-4f) return;
        dirty = false;
        lastFrame = frame;
        lastOther = otherFrame;
        lastOtherWeight = otherWeight;
        ShownCount = 0;
        if (!visible) return;
        if (buffer.Length < window + 2) buffer = new Vector3[window + 2];
        for (int k = 0; k < TraceCount; k++)
        {
            // the newest free frame within the window, then back along its run
            int newest = -1;
            for (int f = frame; f >= Mathf.Max(0, frame - window); f--)
            {
                if (free[k][f])
                {
                    newest = f;
                    break;
                }
            }

            if (newest < 0)
            {
                lines[k].positionCount = 0;
                continue;
            }

            int oldest = newest;
            while (oldest - 1 >= 0 && oldest - 1 >= frame - window && free[k][oldest - 1]) oldest--;
            int count = newest - oldest + 1;
            if (count < 2)
            {
                lines[k].positionCount = 0;
                continue;
            }

            // attached since: the remaining trail fades out over the window
            float since = (float)(times[frame] - times[newest]);
            float fade = Mathf.Clamp01(1f - since / Mathf.Max(1e-3f, WindowSeconds));
            if (fade <= 0.01f)
            {
                lines[k].positionCount = 0;
                continue;
            }

            for (int i = 0; i < count; i++) buffer[i] = points[k][oldest + i];
            if (newest == frame && otherFrame >= 0 && otherWeight > 0f && free[k][otherFrame])
            {
                // the live end of the trail glides with the (blended) body between two pose frames
                Vector3 blended = Vector3.Lerp(points[k][frame], points[k][otherFrame], otherWeight);
                if (otherFrame > frame) buffer[count++] = blended;
                else buffer[count - 1] = blended;
            }

            SetGradient(k, emphasised[k], fade);
            lines[k].positionCount = count;
            lines[k].SetPositions(buffer);
            ShownCount++;
        }
    }

    /// <summary>fraction of the take's frames where an extremity's trail shows (free now)</summary>
    public float FreeFraction(int k)
    {
        if (frameCount == 0) return 0;
        int c = 0;
        for (int f = 0; f < frameCount; f++) c += free[k][f] ? 1 : 0;
        return c / (float)frameCount;
    }

    public bool IsFree(int k, int frame) => frame >= 0 && frame < frameCount && free[k][frame];

    public Dictionary<string, object> State()
    {
        Dictionary<string, object> s = new()
        {
            ["visible"] = visible, ["windowFrames"] = window, ["windowSeconds"] = WindowSeconds, ["headAxisFromSmplx"] = HeadFromSmplx,
            ["emphasised"] = EmphasisedCount, ["frame"] = lastFrame, ["shown"] = ShownCount,
            ["points"] = lines[0] != null ? lines[0].positionCount : 0,
            ["headTop"] = lastFrame >= 0 ? V(points[(int)Trace.Head][lastFrame]) : null,
            ["headAxis"] = false, // removed 2026-10-07: the neck axis (NeckAxisOverlay, layer "neck") replaces it
            ["headGated"] = headGate != null
        };
        Dictionary<string, object> per = new();
        for (int k = 0; k < TraceCount; k++)
        {
            per[((Trace)k).ToString()] = new Dictionary<string, object>
            {
                ["freeNow"] = IsFree(k, lastFrame), ["points"] = lines[k] != null ? lines[k].positionCount : 0,
                ["freeFraction"] = FreeFraction(k)
            };
        }

        // feet: the drawn gesture runs [first, end, peak m, seconds] (playtest: no step-like run is drawn)
        float dt = frameCount > 1 ? (float)((times[frameCount - 1] - times[0]) / (frameCount - 1)) : 1f / 30f;
        for (int side = 0; side < 2; side++)
        {
            if (footHeight[side] == null) continue;
            Dictionary<string, object> e = (Dictionary<string, object>)per[side == 0 ? "LeftAnkle" : "RightAnkle"];
            float[] h = footHeight[side];
            e["footHeightM"] = lastFrame >= 0 && lastFrame < frameCount ? h[lastFrame] : float.NaN;
            e["candidateRuns"] = footCandidates[side];
            e["runs"] = footRuns[side].Select(r =>
            {
                float peak = 0;
                for (int f = r[0]; f < r[1]; f++) peak = Mathf.Max(peak, h[f]);
                return (object)new object[] { r[0], r[1], peak, (r[1] - r[0]) * dt };
            }).ToList();
        }

        s["footRule"] = new Dictionary<string, object>
        {
            ["floorBandM"] = FloorBandM, ["gestureHeightM"] = GestureHeightM, ["longRunSeconds"] = LongRunSeconds,
            ["longRunHeightM"] = LongRunHeightM, ["counterbalanceLegHeightM"] = CounterbalanceLegHeightM
        };
        s["headGateMin"] = HeadGateMin;
        s["extremities"] = per;
        return s;
    }

    static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };

    void OnDestroy()
    {
        if (material != null) Destroy(material);
    }
}
