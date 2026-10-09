using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Layer "balance" (VIEWER_SPEC 3.8 / 3.9, user 2026-10-07: "it's nice to see when the leader's or the follower's body
/// weight, center of gravity, is directly above where they are stepping ... an axis line that lights up from their
/// chest center to their grounded foot"), drawn in PHYSICS mode only (the physics layer / view state):
///  - per dancer, while a foot is grounded: a line from the chest centre (spine3 as the skeleton shows it) down to the
///    support point of the stance foot (floorcraft.json balance: the point of that foot's heel-toe line nearest the
///    COM). It LIGHTS UP with the analysis' alignment score (1 = COM within 4 cm of the foot and the chest-foot line
///    within 3 deg of vertical, 0 at 12 cm / 9 deg): dim neutral grey at 0, bright mint green at 1, a little wider;
///    a small ring marks the support point. No line while no foot is grounded (airborne / no contact).
///  - in a counterbalance pivot: the COUPLE axis from the follower's anchored (orbited) foot up to the couple's
///    combined centre of mass, lighting up the same way when the combined COM is directly above her foot.
/// All numbers are estimates from the fitted bodies. Nothing here rises from the leader in rhythm mode (the default).
/// Brightness = MinBrightness + (1 - MinBrightness) x score, a monotonic function of the score; a lit axis also gets a
/// soft halo (width and strength x score). Below the aligned score (0.5) the line is DASHED (review 2026-10-07: a dim
/// solid line along the stance leg - which physics mode colours - read like no axis at all), so "off the foot" and
/// "no grounded foot" look different; the support ring at the foot stays visible (>= RingMinBrightness). One additive
/// glow mesh, rebuilt only when the frame or camera changes.
/// </summary>
public class BalanceAxisOverlay : MonoBehaviour
{
    [Tooltip("brightness of a fully misaligned axis (score 0): dim but visible")]
    public float MinBrightness = 0.22f;
    public Color Neutral = new(0.55f, 0.58f, 0.62f);
    public Color Lit = new(0.28f, 1f, 0.5f);
    public float WidthDim = 0.008f, WidthLit = 0.02f;
    [Tooltip("m: width of the soft halo around a lit axis (scaled by the score)")]
    public float HaloWidth = 0.07f;
    [Tooltip("draw from the skeleton's spine3 (true) or from the analysis' chest point (false)")]
    public bool SkeletonChest = true;
    [Tooltip("below this alignment score the axis is drawn dashed (not aligned)")]
    public float DashedBelowScore = 0.5f;
    public float Dash = 0.045f, Gap = 0.03f;
    [Tooltip("the support ring at the stance foot never goes dimmer than this")]
    public float RingMinBrightness = 0.45f;

    FloorCraftData.BalanceData data;
    Dancer lead, follow;
    GlowMesh glow;
    Material material;
    bool visible;
    int lastFrame = -1;
    float lastTime = float.NaN;
    Vector3 lastEye;
    bool dirty = true;

    public class Shown
    {
        public bool On, Dashed;
        public int Index = -1;
        public int Stance;
        public float Score = float.NaN, Brightness, Width, TiltDeg = float.NaN, OffsetM = float.NaN, DrawnTiltDeg = float.NaN;
        public float ChestToSkeletonM = float.NaN;
        public Vector3 Top, Bottom;
        public Color Colour;
    }

    public readonly Shown LeadAxis = new(), FollowAxis = new(), CoupleAxis = new();
    bool leadHidden, followHidden;
    float leadAlpha = 1f, followAlpha = 1f;

    /// <summary>roleHidden (RoleHiddenSpans): a role's axis fades with the role's alpha 0..1 and is not drawn at ~0; the couple's (derived
    /// from both) follows the lower of the two</summary>
    public void SetRoleAlpha(float lead, float follow)
    {
        lead = Mathf.Clamp01(float.IsFinite(lead) ? lead : 1f);
        follow = Mathf.Clamp01(float.IsFinite(follow) ? follow : 1f);
        if (Mathf.Abs(leadAlpha - lead) < 1e-4f && Mathf.Abs(followAlpha - follow) < 1e-4f) return;
        leadAlpha = lead;
        followAlpha = follow;
        leadHidden = lead <= RoleHiddenSpans.HiddenBelow;
        followHidden = follow <= RoleHiddenSpans.HiddenBelow;
        dirty = true;
    }

    static void Fade(Shown s, float alpha)
    {
        if (alpha >= 0.9999f) return;
        s.Colour = new Color(s.Colour.r * alpha, s.Colour.g * alpha, s.Colour.b * alpha, s.Colour.a);
    }

    public bool Loaded => data != null;
    public bool Visible => visible;
    public FloorCraftData.BalanceData Data => data;

    public void Init(FloorCraftData.BalanceData balance, Dancer leadDancer, Dancer followDancer)
    {
        data = balance;
        lead = leadDancer;
        follow = followDancer;
        material = GlowMesh.NewMaterial("Balance axis glow", 1.7f); // low enough that the lit green does not bloom to white
        glow = GlowMesh.Create("Balance axes", transform, material);
        glow.SetVisible(visible);
    }

    public void SetVisible(bool on)
    {
        if (visible == on) return;
        visible = on;
        if (glow != null) glow.SetVisible(on);
        dirty = true;
    }

    public float Brightness(float score) => MinBrightness + (1f - MinBrightness) * Mathf.Clamp01(float.IsNaN(score) ? 0f : score);

    public Color ColourFor(float score)
    {
        float s = Mathf.Clamp01(float.IsNaN(score) ? 0f : score);
        Color c = Color.Lerp(Neutral, Lit, s) * Brightness(s);
        c.a = 1f;
        return c;
    }

    /// <param name="time">audio-clock time; the exact sub-frame time while a director drives the clock (the support point,
    /// chest, COM and score then lerp toward the neighbouring analysis frame)</param>
    /// <param name="otherFrame">the dancers' neighbouring pose frame (-1: none) and <paramref name="otherWeight"/> (0..0.5)
    /// its blend weight, for the chest drawn from the skeleton's spine3</param>
    public void SetTime(float time, int frame, Camera viewer, int otherFrame = -1, float otherWeight = 0f)
    {
        if (data == null || glow == null) return;
        Vector3 eye = viewer != null ? viewer.transform.position : Vector3.zero;
        bool cameraMoved = (eye - lastEye).sqrMagnitude > 1e-6f;
        if (!dirty && frame == lastFrame && Mathf.Approximately(time, lastTime) && !cameraMoved) return;
        dirty = false;
        lastFrame = frame;
        lastTime = time;
        lastEye = eye;
        int i = data.Nearest(time);
        int j = -1;
        float w = 0f;
        if (otherFrame >= 0 && i >= 0 && data.T != null && data.T.Length > 1)
        {
            // the analysis frame beside the nearer one, on the side of `time`
            int o = time >= data.T[i] ? i + 1 : i - 1;
            if (o >= 0 && o < data.T.Length && data.T[o] != data.T[i])
            {
                j = o;
                w = Mathf.Min(0.5f, Mathf.Abs(time - data.T[i]) / Mathf.Abs(data.T[o] - data.T[i]));
            }
        }

        blendFrame = otherFrame;
        blendFrameWeight = otherWeight;
        Measure(LeadAxis, data.Lead, lead, i, frame, false, j, w);
        Measure(FollowAxis, data.Follow, follow, i, frame, false, j, w);
        Measure(CoupleAxis, data.Couple, null, i, frame, true, j, w);
        if (leadHidden) LeadAxis.On = false;
        if (followHidden) FollowAxis.On = false;
        if (leadHidden || followHidden) CoupleAxis.On = false;
        Fade(LeadAxis, leadAlpha);
        Fade(FollowAxis, followAlpha);
        Fade(CoupleAxis, Mathf.Min(leadAlpha, followAlpha));
        glow.Begin();
        glow.Viewer = eye;
        if (visible)
        {
            Draw(LeadAxis, 0.05f);
            Draw(FollowAxis, 0.05f);
            Draw(CoupleAxis, 0.075f);
        }

        glow.End();
    }

    // sub-frame blend of the dancers' skeleton frame (spine3) toward blendFrame by blendFrameWeight
    int blendFrame = -1;
    float blendFrameWeight;

    static Vector3 BlendPoint(Vector3 a, Vector3 b, float w) =>
        w <= 0f || float.IsNaN(a.x) || float.IsNaN(b.x) ? a : Vector3.Lerp(a, b, w);

    static float BlendValue(float a, float b, float w) =>
        w <= 0f || float.IsNaN(a) || float.IsNaN(b) ? a : Mathf.Lerp(a, b, w);

    void Measure(Shown s, FloorCraftData.BalanceTrack track, Dancer dancer, int i, int frame, bool couple, int j = -1, float w = 0f)
    {
        s.On = false;
        s.Index = i;
        s.Stance = 0;
        s.Score = s.TiltDeg = s.OffsetM = s.DrawnTiltDeg = s.ChestToSkeletonM = float.NaN;
        if (track == null || i < 0 || i >= track.Stance.Length) return;
        s.Stance = track.Stance[i];
        if (s.Stance == 0) return;
        // the neighbouring analysis frame joins the blend only while it has the same stance foot (a foot change is a step)
        if (j < 0 || j >= track.Stance.Length || track.Stance[j] != s.Stance) w = 0f;
        else if (j >= 0) w = Mathf.Clamp(w, 0f, 0.5f);
        int jj = w > 0f ? j : i;
        Vector3 bottom = BlendPoint(track.Support[i], track.Support[jj], w);
        Vector3 top = couple ? BlendPoint(track.Com[i], track.Com[jj], w) : BlendPoint(track.Chest[i], track.Chest[jj], w);
        if (!couple && dancer != null && frame >= 0 && frame < dancer.FrameCount)
        {
            Vector3 sk = dancer.Joint(frame, SmplJoint.Spine3);
            if (blendFrame >= 0 && blendFrame < dancer.FrameCount && blendFrameWeight > 0f)
            {
                sk = BlendPoint(sk, dancer.Joint(blendFrame, SmplJoint.Spine3), blendFrameWeight);
            }

            if (!float.IsNaN(sk.x))
            {
                if (!float.IsNaN(top.x)) s.ChestToSkeletonM = Vector3.Distance(sk, top);
                if (SkeletonChest || float.IsNaN(top.x)) top = sk;
            }
        }

        if (float.IsNaN(bottom.x) || float.IsNaN(top.x)) return;
        s.On = true;
        s.Score = BlendValue(track.Score[i], track.Score[jj], w);
        s.TiltDeg = BlendValue(track.TiltDeg[i], track.TiltDeg[jj], w);
        s.OffsetM = BlendValue(track.OffsetM[i], track.OffsetM[jj], w);
        s.Top = top;
        s.Bottom = new Vector3(bottom.x, 0.006f, bottom.z);
        Vector3 d = s.Top - s.Bottom;
        s.DrawnTiltDeg = Vector3.Angle(d, Vector3.up);
        float sc = Mathf.Clamp01(float.IsNaN(s.Score) ? 0f : s.Score);
        s.Brightness = Brightness(sc);
        s.Width = Mathf.Lerp(WidthDim, WidthLit, sc);
        s.Colour = ColourFor(sc);
        s.Dashed = sc < DashedBelowScore;
    }

    void Draw(Shown s, float ringRadius)
    {
        if (!s.On) return;
        float score = Mathf.Clamp01(float.IsNaN(s.Score) ? 0f : s.Score);
        if (score > 0.05f)
        {
            // the axis "lights up": a soft halo that grows with the alignment
            Color halo = new(s.Colour.r * 0.2f * score, s.Colour.g * 0.2f * score, s.Colour.b * 0.2f * score, 1f);
            glow.Line(s.Bottom, s.Top, HaloWidth * score, halo, halo);
        }

        if (s.Dashed) glow.DashedLine(s.Bottom, s.Top, s.Width, s.Colour, Dash, Gap);
        else glow.Line(s.Bottom, s.Top, s.Width, s.Colour, s.Colour * 0.85f);
        Color ring = s.Brightness >= RingMinBrightness ? s.Colour : s.Colour * (RingMinBrightness / Mathf.Max(1e-3f, s.Brightness));
        ring.a = 1f;
        glow.Ring(s.Bottom, ringRadius, 0.007f, ring, 24);
        glow.Sphere(s.Top, 0.018f + 0.008f * Mathf.Clamp01(float.IsNaN(s.Score) ? 0f : s.Score), s.Colour);
    }

    static Dictionary<string, object> StateOf(Shown s) => new()
    {
        ["shown"] = s.On, ["index"] = s.Index, ["stance"] = s.Stance == 1 ? "left" : s.Stance == 2 ? "right" : "none",
        ["score"] = s.Score, ["brightness"] = s.On ? s.Brightness : 0f, ["width"] = s.On ? s.Width : 0f, ["tiltDeg"] = s.TiltDeg,
        ["dashed"] = s.On && s.Dashed,
        ["offsetM"] = s.OffsetM, ["drawnTiltDeg"] = s.DrawnTiltDeg, ["chestToSkeletonM"] = s.ChestToSkeletonM,
        ["top"] = s.On ? new[] { s.Top.x, s.Top.y, s.Top.z } : null, ["bottom"] = s.On ? new[] { s.Bottom.x, s.Bottom.z } : null
    };

    public Dictionary<string, object> State() => new()
    {
        ["loaded"] = Loaded, ["visible"] = visible, ["roleHidden"] = new[] { leadHidden, followHidden }, ["vertices"] = glow != null && visible ? glow.VertexCount : 0,
        ["minBrightness"] = MinBrightness, ["frames"] = data?.T?.Length ?? 0,
        ["params"] = data == null ? null : new[] { data.GoodOffsetM, data.BadOffsetM, data.GoodTiltDeg, data.BadTiltDeg },
        ["lead"] = StateOf(LeadAxis), ["follow"] = StateOf(FollowAxis), ["couple"] = StateOf(CoupleAxis),
        ["couplePivot"] = data != null && CoupleAxis.Index >= 0 && CoupleAxis.Index < data.CouplePivot.Length ? data.CouplePivot[CoupleAxis.Index] : -1
    };

    void OnDestroy()
    {
        if (material != null) Destroy(material);
    }
}
