using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Layer "room": the reconstructed studio as a runtime mesh from the capture's room_mesh.bin ("DCXR", written by
/// dancecap export_unity.export_room). Positions are already in Unity capture coordinates (world_from_sfm, then
/// (x, y, -z)); submeshes ("room" walls, "floor") carry their own photo texture, or the vertices carry colours.
/// Lighting is baked in the source frames, so it renders unlit and double-sided (Resources/HM_RoomUnlit) - one
/// opaque draw per submesh, the Quest-friendly alternative to the splat layer.
/// </summary>
public class RoomMesh : MonoBehaviour
{
    readonly List<Object> owned = new();
    public int VertexCount { get; private set; }
    public int TriangleCount { get; private set; }
    public int SubmeshCount { get; private set; }
    public int TextureCount { get; private set; }
    public bool HasContent { get; private set; }
    public Bounds LocalBounds { get; private set; }

    /// <summary>build the room from a capture folder's room_mesh.bin; false when the capture has none</summary>
    public bool Load(CaptureManifest manifest)
    {
        Clear();
        string path = manifest.room != null ? manifest.OptionalPath(manifest.room.mesh) : null;
        if (path == null) return false;

        (JObject header, byte[] raw, int offset) = SmplxData.Open(path, "DCXR");
        Dictionary<string, (int offset, int count)> blocks = SmplxData.Blocks(header, offset, raw.Length, path);
        (int o, int n) b = blocks["positions"];
        Vector3[] positions = SmplxData.ToVectors(SmplxData.Floats(raw, b.o, b.n, path));
        b = blocks["indices"];
        int[] indices = SmplxData.Ints(raw, b.o, b.n, path);

        Mesh mesh = new() { name = "Room", indexFormat = IndexFormat.UInt32 };
        owned.Add(mesh);
        mesh.vertices = positions;
        if (blocks.TryGetValue("uv", out b))
        {
            float[] f = SmplxData.Floats(raw, b.o, b.n, path);
            Vector2[] uv = new Vector2[f.Length / 2];
            for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(f[2 * i], f[2 * i + 1]);
            mesh.uv = uv;
        }

        bool vertexColours = blocks.TryGetValue("colors", out b);
        if (vertexColours)
        {
            Color32[] c = new Color32[b.n / 4];
            for (int i = 0; i < c.Length; i++)
            {
                int k = b.o + 4 * i;
                c[i] = new Color32(raw[k], raw[k + 1], raw[k + 2], raw[k + 3]);
            }

            mesh.colors32 = c;
        }

        JArray subs = (JArray)header["submeshes"];
        mesh.subMeshCount = subs.Count;
        Material[] materials = new Material[subs.Count];
        Shader shader = Resources.Load<Shader>("HM_RoomUnlit");
        if (shader == null) shader = Shader.Find("HeadMovement/RoomUnlit");
        for (int s = 0; s < subs.Count; s++)
        {
            int start = subs[s].Value<int>("start"), count = subs[s].Value<int>("count");
            int[] tri = new int[count];
            System.Array.Copy(indices, start, tri, 0, count);
            mesh.SetIndices(tri, MeshTopology.Triangles, s, false);

            Material mat = new(shader) { name = $"Room {subs[s].Value<string>("name")}" };
            owned.Add(mat);
            string texture = subs[s].Value<string>("texture");
            string texPath = string.IsNullOrEmpty(texture) ? null : manifest.OptionalPath(texture);
            if (texPath != null)
            {
                Texture2D tex = SmplxAvatar.LoadTexture(texPath, texture);
                owned.Add(tex);
                mat.SetTexture("_BaseMap", tex);
                TextureCount++;
            }

            mat.SetFloat("_UseVertexColor", vertexColours ? 1f : 0f);
            materials[s] = mat;
        }

        mesh.RecalculateBounds();
        LocalBounds = mesh.bounds;
        GameObject go = new("Room Mesh");
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer r = go.AddComponent<MeshRenderer>();
        r.sharedMaterials = materials;
        r.shadowCastingMode = ShadowCastingMode.Off; // baked lighting: the room neither casts nor receives
        r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        owned.Add(go);

        VertexCount = positions.Length;
        TriangleCount = indices.Length / 3;
        SubmeshCount = subs.Count;
        HasContent = true;
        return true;
    }

    public void SetVisible(bool visible)
    {
        foreach (Transform child in transform) child.gameObject.SetActive(visible);
    }

    public void Clear()
    {
        foreach (Object o in owned)
        {
            if (o != null) Destroy(o);
        }

        owned.Clear();
        VertexCount = TriangleCount = SubmeshCount = TextureCount = 0;
        HasContent = false;
    }

    void OnDestroy() => Clear();
}
