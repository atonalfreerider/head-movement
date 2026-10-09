using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Layer "neck" (VIEWER_SPEC 3.7, user 2026-10-07): the FOLLOWER's neck axis - a white glow line from her neck (SMPL-X
/// joint 12, the base of the neck) along her head's up direction - shown ONLY while her neck is off-axis by more than
/// ThresholdDeg (15 deg). It fades in AND grows with the angle above the threshold:
///     alpha = length / MaxLength = smoothstep(0, 1, (angle - ThresholdDeg) / (FullDeg - ThresholdDeg))
/// so it is invisible and 0 m long at 15 deg and opaque white and MaxLength (0.5 m) long from FullDeg (35 deg) on; it
/// fades out and shrinks the same way when her neck comes back. The leader never gets an axis (nothing here reads him).
///
/// Off-axis angle (Reference.Neutral, the default since 2026-10-07): the angle between her head's up axis expressed in
/// her chest's frame and her NEUTRAL head-on-torso alignment, calibrated per capture from calm frames (the 40 % of
/// frames where that relative axis moves slowest; mean direction, refined once on the calm frames within 25 deg of it).
/// Her natural neck carriage (a fixed offset from the rest pose, so the Torso reference showed the axis
/// half the time) is therefore 0 and the axis marks real head movements.
/// Reference.Torso (the previous default): the angle between her HEAD's up axis (the global rotation of SMPL-X
/// joint 15 - the chain pelvis, spine1, spine2, spine3, neck, head of the motion binary - applied to the rest-pose up;
/// every SMPL-X bone has an identity rest rotation) and her TORSO's up axis (the same for spine3, joint 9). That is the
/// swing of neck + head relative to the chest: 0 in the rest pose by construction, and turning the head left / right
/// (a twist about its own up axis) does not count. Reference.Vertical measures the head's up axis against world up
/// instead (whole-body lean included). Both axes are smoothed per frame by a zero-phase Gaussian (sigma SmoothSeconds,
/// ~0.1 s) before the angle is taken, so fit jitter does not flicker the axis at the threshold.
/// Without the SMPL-X motion binary (v1/v2 captures) the axes come from the joints: head = neck -> head, torso =
/// pelvis -> spine3, minus the take's 5th-percentile angle (her natural neck carriage), source "joints".
/// Precomputed once per capture; per frame one glow strip is rebuilt only when the frame, camera or a setting changes.
/// </summary>
public class NeckAxisOverlay : MonoBehaviour
{
    public enum Reference
    {
        Neutral,
        Torso,
        Vertical
    }

    public float ThresholdDeg = 15f;
    public float FullDeg = 35f;
    public float MaxLength = 0.5f;
    public float Width = 0.012f;
    public float SmoothSeconds = 0.1f;
    public float Intensity = 2.4f;
    public Color Colour = Color.white;
    public Reference Mode = Reference.Neutral;
    public float CalmQuantile = 0.4f;

    Vector3[] start;      // neck joint (world, origin-shifted like the skeleton)
    Vector3[] headUp;     // smoothed head up axis
    float[] angleTorso;   // deg, neck + head relative to the chest
    float[] angleVertical; // deg, head up vs world up
    float[] angleNeutral;  // deg, head up in the chest frame vs her calibrated neutral alignment

    /// <summary>her calibrated neutral head-on-chest alignment: angle from the rest pose (deg) and calm frames used</summary>
    public float NeutralOffsetDeg { get; private set; }

    public int CalmFrames { get; private set; }
    GlowMesh glow;
    Material material;
    int frameCount;
    int lastFrame = -1;
    Vector3 lastEye;
    bool visible = true;
    bool dirty = true;

    public string Source { get; private set; } = "none";
    public int FrameCount => frameCount;
    public bool Visible => visible;
    public int Frame => lastFrame;

    /// <summary>off-axis angle of the shown frame (the selected reference), deg</summary>
    public float AngleDeg { get; private set; }

    public float Alpha { get; private set; }
    public float Length { get; private set; }

    /// <summary>drawn now: layer on, the shown frame above the threshold</summary>
    public bool Shown => visible && Alpha > 1e-3f && Length > 1e-4f;

    public Vector3 Start { get; private set; }
    public Vector3 Tip { get; private set; }

    /// <summary>fade-in / growth factor of an angle: 0 up to the threshold, smoothstep to 1 at full</summary>
    public static float Ramp(float angleDeg, float thresholdDeg, float fullDeg)
    {
        if (float.IsNaN(angleDeg)) return 0f;
        float t = Mathf.Clamp01((angleDeg - thresholdDeg) / Mathf.Max(1e-3f, fullDeg - thresholdDeg));
        return t * t * (3f - 2f * t);
    }

    public float RampOf(float angleDeg) => Ramp(angleDeg, ThresholdDeg, FullDeg);

    public void Init(Dancer follow, SmplxData.Motion motion, CaptureTimeline timeline)
    {
        frameCount = follow.FrameCount;
        start = new Vector3[frameCount];
        headUp = new Vector3[frameCount];
        angleTorso = new float[frameCount];
        angleVertical = new float[frameCount];
        Vector3[] torsoUp = new Vector3[frameCount];
        Quaternion[] torsoRot = new Quaternion[frameCount];
        Vector3[] relUp = new Vector3[frameCount];
        angleNeutral = new float[frameCount];
        float frameInterval = timeline != null && timeline.Count > 1 ? (timeline.Last - timeline.First) / (timeline.Count - 1) : 1f / 30f;

        bool fromMotion = motion != null && motion.FrameCount > 0;
        Source = fromMotion ? "smplx rotations" : "joints";
        int[] torsoChain = { 0, 3, 6, 9 };
        int[] neckHead = { 12, 15 };
        for (int f = 0; f < frameCount; f++)
        {
            start[f] = follow.Joint(f, SmplJoint.Neck);
            if (fromMotion)
            {
                int o = Mathf.Min(f, motion.FrameCount - 1) * SmplxData.Joints;
                Quaternion q = Quaternion.identity;
                foreach (int j in torsoChain) q *= motion.Rotations[o + j];
                torsoUp[f] = (q * Vector3.up).normalized;
                torsoRot[f] = q;
                Quaternion rel = Quaternion.identity;
                foreach (int j in neckHead) rel *= motion.Rotations[o + j];
                relUp[f] = (rel * Vector3.up).normalized; // head up in the chest frame
                q *= rel;
                headUp[f] = (q * Vector3.up).normalized;
            }
            else
            {
                headUp[f] = Direction(follow.Joint(f, SmplJoint.Neck), follow.Joint(f, SmplJoint.Head));
                torsoUp[f] = Direction(follow.Joint(f, SmplJoint.Pelvis), follow.Joint(f, SmplJoint.Spine3));
                torsoRot[f] = Quaternion.FromToRotation(Vector3.up, torsoUp[f]);
                relUp[f] = Quaternion.Inverse(torsoRot[f]) * headUp[f];
            }
        }

        float sigma = SmoothSeconds / Mathf.Max(1e-3f, frameInterval);
        headUp = Smooth(headUp, sigma);
        torsoUp = Smooth(torsoUp, sigma);
        relUp = Smooth(relUp, sigma);
        for (int f = 0; f < frameCount; f++)
        {
            angleTorso[f] = Vector3.Angle(headUp[f], torsoUp[f]);
            angleVertical[f] = Vector3.Angle(headUp[f], Vector3.up);
        }

        Vector3 neutral = Calibrate(relUp, frameInterval);
        NeutralOffsetDeg = Vector3.Angle(neutral, Vector3.up);
        for (int f = 0; f < frameCount; f++) angleNeutral[f] = Vector3.Angle(relUp[f], neutral);

        if (!fromMotion)
        {
            // joints only: the neck -> head segment is not parallel to pelvis -> spine3 at rest; remove her natural carriage
            float baseline = Percentile(angleTorso, 0.05f);
            for (int f = 0; f < frameCount; f++) angleTorso[f] = Mathf.Max(0f, angleTorso[f] - baseline);
        }

        material = GlowMesh.NewMaterial("Follower neck axis glow", Intensity);
        glow = GlowMesh.Create("Follower neck axis", transform, material);
        glow.SetVisible(visible);
        dirty = true;
    }

    /// <summary>her neutral head-on-chest direction: mean of the calm frames (slowest CalmQuantile of the relative axis'
    /// angular speed), refined once on the calm frames within 25 deg of the first estimate</summary>
    Vector3 Calibrate(Vector3[] rel, float frameInterval)
    {
        int n = rel.Length;
        if (n < 3) return Vector3.up;
        float[] speed = new float[n];
        for (int f = 0; f < n; f++)
        {
            int a = Mathf.Max(0, f - 1), b = Mathf.Min(n - 1, f + 1);
            speed[f] = Vector3.Angle(rel[a], rel[b]) / Mathf.Max(1e-4f, (b - a) * frameInterval);
        }

        float cut = Percentile(speed, CalmQuantile);
        Vector3 m = Vector3.zero;
        int c = 0;
        for (int f = 0; f < n; f++)
        {
            if (speed[f] > cut) continue;
            m += rel[f];
            c++;
        }

        if (c == 0 || m.sqrMagnitude < 1e-8f) return Vector3.up;
        m.Normalize();
        Vector3 m2 = Vector3.zero;
        int c2 = 0;
        for (int f = 0; f < n; f++)
        {
            if (speed[f] > cut || Vector3.Angle(rel[f], m) > 25f) continue;
            m2 += rel[f];
            c2++;
        }

        CalmFrames = c2 > 0 ? c2 : c;
        return c2 > 0 && m2.sqrMagnitude > 1e-8f ? m2.normalized : m;
    }

    /// <summary>the axis ramp (alpha) of every frame with the current reference (the head trace gate)</summary>
    public float[] Gate()
    {
        float[] g = new float[frameCount];
        for (int f = 0; f < frameCount; f++) g[f] = RampOf(AngleAt(f, Mode));
        return g;
    }

    static Vector3 Direction(Vector3 a, Vector3 b)
    {
        Vector3 d = b - a;
        return float.IsNaN(d.x) || d.sqrMagnitude < 1e-10f ? Vector3.up : d.normalized;
    }

    /// <summary>zero-phase Gaussian smoothing of unit vectors (renormalised), clamped at the ends</summary>
    static Vector3[] Smooth(Vector3[] v, float sigma)
    {
        int n = v.Length;
        if (n == 0 || sigma < 0.3f) return v;
        int r = Mathf.CeilToInt(3f * sigma);
        float[] k = new float[2 * r + 1];
        for (int i = -r; i <= r; i++) k[i + r] = Mathf.Exp(-0.5f * i * i / (sigma * sigma));
        Vector3[] o = new Vector3[n];
        for (int f = 0; f < n; f++)
        {
            Vector3 s = Vector3.zero;
            for (int i = -r; i <= r; i++) s += v[Mathf.Clamp(f + i, 0, n - 1)] * k[i + r];
            o[f] = s.sqrMagnitude > 1e-12f ? s.normalized : v[f];
        }

        return o;
    }

    static float Percentile(float[] a, float q)
    {
        if (a.Length == 0) return 0f;
        float[] s = (float[])a.Clone();
        Array.Sort(s);
        return s[Mathf.Clamp((int)(q * (s.Length - 1)), 0, s.Length - 1)];
    }

    public float AngleAt(int frame, Reference reference) =>
        frame < 0 || frame >= frameCount ? float.NaN
        : reference == Reference.Vertical ? angleVertical[frame]
        : reference == Reference.Torso ? angleTorso[frame]
        : angleNeutral[frame];

    public void SetVisible(bool on)
    {
        if (visible == on) return;
        visible = on;
        if (glow != null) glow.SetVisible(on);
        dirty = true;
    }

    /// <summary>roleHidden (RoleHiddenSpans): her alpha 0..1 scales the axis (the leader never gets one)</summary>
    public void SetRoleAlpha(float alpha)
    {
        if (glow != null) glow.SetOpacity(alpha);
    }

    /// <summary>threshold / full angle / max length / reference changed (CLI)</summary>
    public void MarkDirty() => dirty = true;

    public void SetFrame(int frame, Camera viewer)
    {
        if (frameCount == 0 || frame < 0 || glow == null) return;
        frame = Mathf.Min(frame, frameCount - 1);
        Vector3 eye = viewer != null ? viewer.transform.position : Vector3.zero;
        bool cameraMoved = (eye - lastEye).sqrMagnitude > 1e-6f;
        if (!dirty && frame == lastFrame && !cameraMoved) return;
        dirty = false;
        lastFrame = frame;
        lastEye = eye;

        AngleDeg = AngleAt(frame, Mode);
        float k = RampOf(AngleDeg);
        Vector3 s = start[frame];
        if (float.IsNaN(s.x)) k = 0f;
        Alpha = k;
        Length = MaxLength * k;
        Start = s;
        Tip = s + headUp[frame] * Length;

        glow.Begin();
        glow.Viewer = eye;
        if (visible && k > 1e-3f)
        {
            Color c = Colour * k;
            glow.Line(Start, Tip, Width, c, c * 0.75f);
        }

        glow.End();
    }

    /// <summary>frames of the take where the axis shows (playtests: the up / tilted frames)</summary>
    public (int upright, int tilted, int full) Extremes()
    {
        int up = -1, tilted = -1, full = -1;
        float minA = float.MaxValue, bestTilt = float.MaxValue;
        for (int f = 0; f < frameCount; f++)
        {
            float a = AngleAt(f, Mode);
            if (float.IsNaN(a)) continue;
            if (a < minA) (minA, up) = (a, f);
            // a frame part-way up the ramp (the axis fading in): closest to the middle of threshold..full
            float mid = 0.5f * (ThresholdDeg + FullDeg);
            if (a > ThresholdDeg && Mathf.Abs(a - mid) < bestTilt) (bestTilt, tilted) = (Mathf.Abs(a - mid), f);
            if (a >= FullDeg && (full < 0 || a > AngleAt(full, Mode))) full = f;
        }

        return (up, tilted, full);
    }

    public Dictionary<string, object> State()
    {
        int over = 0;
        float max = 0f;
        int maxFrame = -1;
        for (int f = 0; f < frameCount; f++)
        {
            float a = AngleAt(f, Mode);
            if (a > ThresholdDeg) over++;
            if (a > max) (max, maxFrame) = (a, f);
        }

        (int up, int tilted, int full) = Extremes();
        return new Dictionary<string, object>
        {
            ["visible"] = visible, ["shown"] = Shown, ["frame"] = lastFrame, ["source"] = Source,
            ["reference"] = Mode.ToString().ToLowerInvariant(),
            ["angleDeg"] = AngleDeg, ["alpha"] = Alpha, ["lengthM"] = Length,
            ["angleTorsoDeg"] = AngleAt(lastFrame, Reference.Torso), ["angleVerticalDeg"] = AngleAt(lastFrame, Reference.Vertical),
            ["angleNeutralDeg"] = AngleAt(lastFrame, Reference.Neutral), ["neutralOffsetDeg"] = NeutralOffsetDeg, ["calmFrames"] = CalmFrames,
            ["shownFraction"] = ShownFraction(Mode), ["shownFractionTorso"] = ShownFraction(Reference.Torso),
            ["shownFractionNeutral"] = ShownFraction(Reference.Neutral),
            ["thresholdDeg"] = ThresholdDeg, ["fullDeg"] = FullDeg, ["maxLengthM"] = MaxLength, ["smoothSeconds"] = SmoothSeconds,
            ["start"] = lastFrame >= 0 ? V(Start) : null, ["tip"] = lastFrame >= 0 ? V(Tip) : null,
            ["framesOverThreshold"] = over, ["frames"] = frameCount, ["maxAngleDeg"] = max, ["maxAngleFrame"] = maxFrame,
            ["uprightFrame"] = up, ["rampFrame"] = tilted, ["fullFrame"] = full,
            ["vertices"] = glow != null ? glow.VertexCount : 0, ["role"] = "follow"
        };
    }

    /// <summary>share of the take's frames where the axis shows (angle above the threshold) with a reference</summary>
    public float ShownFraction(Reference reference)
    {
        if (frameCount == 0) return 0f;
        int c = 0;
        for (int f = 0; f < frameCount; f++) c += AngleAt(f, reference) > ThresholdDeg ? 1 : 0;
        return c / (float)frameCount;
    }

    static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };

    void OnDestroy()
    {
        if (material != null) Destroy(material);
    }
}
