using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// A dancer's face block (dancecap face_export: smplx_&lt;role&gt;_face.bin, magic "DCXF"): SMPL-X expression blendshapes
/// (and, when present, FLAME-derived eyelid shapes whose weights are procedural blinks - stylisation, not capture)
/// added to the avatar's runtime mesh and driven per capture frame. The jaw and eye rotations arrive with the motion
/// binary (joints 22-24), so only the shape weights are set here.
///
/// File layout (little-endian, like the other dancecap binaries): "DCXF", uint32 header length, JSON header padded to
/// 4 bytes, then the header's "blocks": vertex_ids (Vh) int32 SMPL-X vertices, deltas (S, Vh, 3) float32 rest-pose
/// offsets in metres already in Unity axes, weights (T, S) float32 per capture frame (signed, may exceed 1:
/// SetBlendShapeWeight(s, 100 w), legacyClampBlendShapeWeights must stay off). The seam-split skin maps its vertices
/// to SMPL-X through its vertex_ids block (unsplit skins: identity).
///
/// Attaches itself to every avatar of a loaded capture that has the file (HeadMovement.AvatarsLoaded). The shapes,
/// weights and textures carry SMPL-X / FLAME-licensed data and the dancers' likeness: StreamingAssets only, never
/// committed.
/// </summary>
[DefaultExecutionOrder(50)]
public class SmplxFace : MonoBehaviour
{
    const string Prefix = "face_";

    /// <summary>scale on the expression weights (1 = as exported, 0 = neutral face)</summary>
    public float Strength = 1f;

    /// <summary>procedural blinks (eyelid shapes) on/off</summary>
    public bool Blinks = true;

    public SmplxAvatar Avatar { get; private set; }
    public string[] ShapeNames { get; private set; }
    public int FrameCount { get; private set; }
    public int VerticesMoved { get; private set; }
    public string Warning { get; private set; }

    SkinnedMeshRenderer smr;
    int[] shapeIndex;
    bool[] isEyelid;
    float[] weights; // [frame * S + s]
    int shownFrame = -1;
    float shownStrength = float.NaN;
    bool shownBlinks;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Hook()
    {
        HeadMovement.AvatarsLoaded -= OnAvatarsLoaded;
        HeadMovement.AvatarsLoaded += OnAvatarsLoaded;
    }

    static void OnAvatarsLoaded(HeadMovement hm)
    {
        foreach (KeyValuePair<Role, SmplxAvatar> kv in hm.Avatars)
        {
            if (kv.Value == null) continue;
            string path = hm.Manifest?.OptionalPath($"smplx_{kv.Key.ToString().ToLowerInvariant()}_face.bin");
            if (path == null) continue;
            try
            {
                SmplxFace face = Create(kv.Value, path);
                if (face.Warning != null) Debug.LogWarning($"{hm.Manifest?.DisplayName}: {kv.Key} face: {face.Warning}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{hm.Manifest?.DisplayName}: {kv.Key} face failed to load: {e.Message}");
            }
        }
    }

    /// <param name="facePath">the capture's smplx_&lt;role&gt;_face.bin</param>
    public static SmplxFace Create(SmplxAvatar avatar, string facePath)
    {
        if (avatar == null) throw new ArgumentNullException(nameof(avatar));
        SmplxFace face = avatar.GetComponent<SmplxFace>();
        if (face == null) face = avatar.gameObject.AddComponent<SmplxFace>();
        face.Build(avatar, SmplxData.ReadSkin(avatar.SkinPath), facePath);
        return face;
    }

    void Build(SmplxAvatar avatar, SmplxData.Skin skin, string path)
    {
        Avatar = avatar;
        smr = avatar.GetComponent<SkinnedMeshRenderer>();
        if (smr == null || smr.sharedMesh == null) throw new InvalidOperationException("avatar has no skinned mesh");

        (JObject header, byte[] raw, int offset) = SmplxData.Open(path, "DCXF");
        Dictionary<string, (int offset, int count)> blocks = SmplxData.Blocks(header, offset, raw.Length, path);
        int shapes = header.Value<int>("shapes"), vh = header.Value<int>("vertices"), frames = header.Value<int>("frames");
        int[] ids = SmplxData.Ints(raw, blocks["vertex_ids"].offset, blocks["vertex_ids"].count, path);
        float[] deltas = SmplxData.Floats(raw, blocks["deltas"].offset, blocks["deltas"].count, path);
        weights = SmplxData.Floats(raw, blocks["weights"].offset, blocks["weights"].count, path);
        if (ids.Length != vh || deltas.Length != shapes * vh * 3 || weights.Length != frames * shapes)
        {
            throw new System.IO.InvalidDataException($"{path}: block sizes do not match the header");
        }

        JArray names = (JArray)header["names"];
        ShapeNames = new string[shapes];
        isEyelid = new bool[shapes];
        for (int s = 0; s < shapes; s++)
        {
            ShapeNames[s] = names[s].Value<string>();
            isEyelid[s] = ShapeNames[s].StartsWith("eyelid", StringComparison.Ordinal);
        }

        FrameCount = frames;
        if (frames != avatar.FrameCount) Warning = $"face has {frames} frames, motion {avatar.FrameCount}";

        // split vertex -> row of the face block (-1: not moved)
        Dictionary<int, int> row = new(vh);
        for (int i = 0; i < vh; i++) row[ids[i]] = i;
        Mesh mesh = smr.sharedMesh;
        int n = mesh.vertexCount;
        int[] vid = skin.VertexIds;
        if (vid != null && vid.Length != n) throw new System.IO.InvalidDataException("skin vertex_ids do not match the mesh");
        int[] map = new int[n];
        int moved = 0;
        for (int v = 0; v < n; v++)
        {
            map[v] = row.TryGetValue(vid != null ? vid[v] : v, out int i) ? i : -1;
            if (map[v] >= 0) moved++;
        }

        VerticesMoved = moved;
        shapeIndex = new int[shapes];
        Vector3[] d = new Vector3[n];
        for (int s = 0; s < shapes; s++)
        {
            string name = Prefix + ShapeNames[s];
            int existing = mesh.GetBlendShapeIndex(name);
            if (existing >= 0)
            {
                shapeIndex[s] = existing; // rebuilt on the same mesh: reuse
                continue;
            }

            Array.Clear(d, 0, n);
            for (int v = 0; v < n; v++)
            {
                int i = map[v];
                if (i < 0) continue;
                int o = (s * vh + i) * 3;
                d[v] = new Vector3(deltas[o], deltas[o + 1], deltas[o + 2]);
            }

            mesh.AddBlendShapeFrame(name, 100f, d, null, null);
            shapeIndex[s] = mesh.blendShapeCount - 1;
        }

        smr.sharedMesh = mesh; // the renderer picks up the new blendshapes
        shownFrame = -1;
        Apply(Mathf.Max(0, avatar.CurrentFrame));
    }

    void LateUpdate()
    {
        if (Avatar == null || smr == null || FrameCount == 0) return;
        int f = Mathf.Clamp(Avatar.CurrentFrame, 0, FrameCount - 1);
        if (f == shownFrame && Strength == shownStrength && Blinks == shownBlinks) return;
        Apply(f);
    }

    /// <summary>set the shape weights of a capture frame</summary>
    public void Apply(int frame)
    {
        if (weights == null || FrameCount == 0) return;
        frame = Mathf.Clamp(frame, 0, FrameCount - 1);
        int shapes = shapeIndex.Length;
        for (int s = 0; s < shapes; s++)
        {
            float w = weights[frame * shapes + s];
            w = isEyelid[s] ? (Blinks ? w : 0f) : w * Strength;
            smr.SetBlendShapeWeight(shapeIndex[s], 100f * w);
        }

        shownFrame = frame;
        shownStrength = Strength;
        shownBlinks = Blinks;
    }

    /// <summary>the weight of a shape at the shown frame (as set, 0..1 scale)</summary>
    public float ShownWeight(string name)
    {
        int s = Array.IndexOf(ShapeNames, name);
        return s < 0 || smr == null ? float.NaN : smr.GetBlendShapeWeight(shapeIndex[s]) / 100f;
    }
}
