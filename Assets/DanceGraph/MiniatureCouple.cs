using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The miniature couple of the "Dance graph" state (VIEWER_SPEC 3.10): both dancers at ~0.15 scale with their
/// travel removed - the floor-projected couple centre, smoothed over ±0.75 s, is subtracted - so body rotation,
/// sway and limb motion stay while the couple stands on a graph node. Glowing skeletons (lead red, follow white)
/// always; translucent SMPL-X avatars when the capture has them (separate instances of the skins, scaled).
/// </summary>
public class MiniatureCouple : MonoBehaviour
{
    public float Scale = 0.15f;
    public float TravelWindowSeconds = 0.75f;
    public float AvatarAlpha = 0.3f;

    static readonly int[] Bones =
    {
        0, 3, 3, 6, 6, 9, 9, 12, 12, 15, // spine to head
        9, 13, 13, 16, 16, 18, 18, 20, 20, 22, // left arm
        9, 14, 14, 17, 17, 19, 19, 21, 21, 23, // right arm
        0, 1, 1, 4, 4, 7, 7, 10, // left leg
        0, 2, 2, 5, 5, 8, 8, 11 // right leg
    };

    static readonly Color LeadColor = new(1f, 0.16f, 0.1f), FollowColor = new(0.95f, 0.95f, 1f);

    Dancer lead, follow;
    Vector3[] travel; // smoothed couple centre per frame (world, y = 0)
    GlowMesh skeleton;
    Material skeletonMaterial;
    Transform avatarRoot;
    readonly List<SmplxAvatar> avatars = new();
    readonly List<Material> avatarMaterials = new();
    int frames;
    CaptureManifest manifest;
    bool avatarsTried;

    public bool HasAvatars => avatars.Count > 0;
    public Vector3 BasePoint { get; private set; }
    public Vector3 PelvisMid { get; private set; }

    public void Init(Dancer leadDancer, Dancer followDancer, CaptureManifest manifest, CaptureTimeline timeline)
    {
        lead = leadDancer;
        follow = followDancer;
        frames = Mathf.Min(lead.FrameCount, follow.FrameCount);
        travel = SmoothedTravel(timeline);
        skeletonMaterial = GlowMesh.NewMaterial("Miniature skeleton glow", 3f);
        skeleton = GlowMesh.Create("Miniature skeletons", transform, skeletonMaterial);

        avatarRoot = new GameObject("Miniature avatars").transform;
        avatarRoot.SetParent(transform, false);
        avatarRoot.localScale = Vector3.one * Scale;
        this.manifest = manifest;
        SetVisible(false);
    }

    /// <summary>the scaled avatar copies are built on first use (they cost a skin load each)</summary>
    void EnsureAvatars()
    {
        if (avatarsTried) return;
        avatarsTried = true;
        foreach (Role role in new[] { Role.Lead, Role.Follow })
        {
            string skin = manifest?.RolePath(manifest.smplx_skin, role);
            string motion = manifest?.RolePath(manifest.smplx, role);
            if (skin == null || motion == null) continue;
            try
            {
                SmplxAvatar avatar = SmplxAvatar.Create(skin, motion, role, avatarRoot);
                avatar.transform.localPosition = Vector3.zero;
                avatars.Add(avatar);
                foreach (Material m in avatar.GetComponent<Renderer>().sharedMaterials)
                {
                    if (m == null || !m.HasProperty("_BaseColor")) continue;
                    Color c = m.GetColor("_BaseColor");
                    c.a = AvatarAlpha;
                    m.SetColor("_BaseColor", c);
                    avatarMaterials.Add(m);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"miniature {role} avatar failed ({e.Message}) - skeleton only");
            }
        }
    }

    Vector3[] SmoothedTravel(CaptureTimeline timeline)
    {
        Vector3[] raw = new Vector3[frames];
        for (int f = 0; f < frames; f++)
        {
            Vector3 m = (lead.Joint(f, SmplJoint.Pelvis) + follow.Joint(f, SmplJoint.Pelvis)) * 0.5f;
            raw[f] = new Vector3(m.x, 0, m.z);
        }

        float dt = timeline != null && timeline.Count > 1 ? (timeline.Last - timeline.First) / (timeline.Count - 1) : 1f / 30f;
        int half = Mathf.Max(0, Mathf.RoundToInt(TravelWindowSeconds / dt));
        Vector3[] prefix = new Vector3[frames + 1];
        for (int f = 0; f < frames; f++) prefix[f + 1] = prefix[f] + raw[f];
        Vector3[] smooth = new Vector3[frames];
        for (int f = 0; f < frames; f++)
        {
            int a = Mathf.Max(0, f - half), b = Mathf.Min(frames - 1, f + half);
            smooth[f] = (prefix[b + 1] - prefix[a]) / (b - a + 1);
        }

        return smooth;
    }

    public void SetVisible(bool on)
    {
        if (on) EnsureAvatars();
        skeleton.SetVisible(on);
        avatarRoot.gameObject.SetActive(on && avatars.Count > 0);
    }

    /// <summary>place the couple's (travel-removed) frame so its floor point sits at basePoint</summary>
    public void SetPose(int frame, Vector3 basePoint, float fade)
    {
        if (frames == 0) return;
        frame = Mathf.Clamp(frame, 0, frames - 1);
        BasePoint = basePoint;
        Vector3 c = travel[frame];
        skeleton.Begin();
        skeleton.Viewer = DanceText.ViewCamera != null ? DanceText.ViewCamera.transform.position : (Vector3?)null;
        Bonesfor(lead, frame, c, basePoint, LeadColor * fade);
        Bonesfor(follow, frame, c, basePoint, FollowColor * fade);
        skeleton.End();
        Vector3 pm = (lead.Joint(frame, SmplJoint.Pelvis) + follow.Joint(frame, SmplJoint.Pelvis)) * 0.5f;
        PelvisMid = basePoint + (pm - c) * Scale;

        if (avatars.Count == 0) return;
        // avatar local coordinates are capture coordinates: world joint = capture + origin offset
        avatarRoot.position = basePoint + (DanceOrigin.Offset - c) * Scale;
        foreach (SmplxAvatar avatar in avatars) avatar.SetFrame(frame);
        foreach (Material m in avatarMaterials)
        {
            Color col = m.GetColor("_BaseColor");
            float a = AvatarAlpha * fade;
            if (Mathf.Abs(col.a - a) < 0.01f) continue;
            col.a = a;
            m.SetColor("_BaseColor", col);
        }

        bool show = fade > 0.05f;
        if (avatarRoot.gameObject.activeSelf != show && skeleton.Visible) avatarRoot.gameObject.SetActive(show);
    }

    void Bonesfor(Dancer d, int frame, Vector3 c, Vector3 basePoint, Color color)
    {
        float w = 0.011f * Scale / 0.15f;
        for (int b = 0; b < Bones.Length; b += 2)
        {
            Vector3 p0 = basePoint + (d.Joint(frame, (SmplJoint)Bones[b]) - c) * Scale;
            Vector3 p1 = basePoint + (d.Joint(frame, (SmplJoint)Bones[b + 1]) - c) * Scale;
            skeleton.Line(p0, p1, w, color);
        }

        Vector3 head = basePoint + (d.Joint(frame, SmplJoint.Head) - c) * Scale;
        skeleton.Sphere(head + Vector3.up * 0.012f * Scale / 0.15f, 0.014f * Scale / 0.15f, color);
    }

    void OnDestroy()
    {
        if (skeletonMaterial != null) Destroy(skeletonMaterial);
    }
}
