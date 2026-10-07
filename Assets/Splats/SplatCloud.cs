using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Preview renderer for Atlas Gaussian splats: the per-frame dancer splat sequence (4D), drawn as camera-facing soft
/// discs (Resources/SplatPreview.shader, VR single-pass safe). VIEWER_SPEC 3.2a: splats show the dancers only - the
/// static environment (room) splat is not loaded unless IncludeEnvironment is set - and the layer starts off; hidden
/// splats load nothing. Isotropic discs are an approximation; swap in a full 3DGS renderer for final quality.
/// </summary>
public class SplatCloud : MonoBehaviour
{
    [Tooltip("load the capture's static environment (room) splat too - off: splats show the dancers only")]
    public bool IncludeEnvironment = false;
    public int MaxEnvironmentSplats = 400_000;
    public int MaxFrameSplats = 60_000;
    public int FrameCacheSize = 12;

    Material material;
    GameObject environment;
    MeshFilter frameFilter;

    CaptureManifest.SplatSequenceRef sequence;
    string sequenceDir;
    Matrix4x4 sequenceTransform;
    readonly Dictionary<int, Mesh> frameCache = new();
    readonly LinkedList<int> frameOrder = new();
    Task<SplatPly.Data> pendingFrame;
    int pendingIndex = -1;
    int wantedIndex = -1;
    bool visible;
    int lastFrame = -1;
    float lastPoseFps = 30f;

    public bool HasContent => environment != null || sequence != null;

    void EnsureMaterial()
    {
        if (material != null) return;

        Shader shader = Resources.Load<Shader>("SplatPreview");
        if (shader == null) shader = Shader.Find("HeadMovement/SplatPreview");
        material = new Material(shader);
    }

    public IEnumerator Load(CaptureManifest manifest)
    {
        Clear();
        EnsureMaterial();

        if (IncludeEnvironment && manifest.environment_splat != null && !string.IsNullOrEmpty(manifest.environment_splat.path))
        {
            string path = manifest.PathOf(manifest.environment_splat.path);
            Matrix4x4 toUnity = manifest.environment_splat.SceneToUnity();
            int max = MaxEnvironmentSplats;
            Task<SplatPly.Data> task = Task.Run(() => SplatPly.Load(path, toUnity, max));
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
            {
                Debug.LogError($"environment splat {path}: {task.Exception?.GetBaseException().Message}");
            }
            else
            {
                environment = NewRenderer("Environment Splat", BuildMesh(task.Result)).gameObject;
                Debug.Log($"environment splat: {task.Result.Positions.Length} splats");
            }
        }

        if (manifest.splat_sequence != null && !string.IsNullOrEmpty(manifest.splat_sequence.dir))
        {
            sequence = manifest.splat_sequence;
            sequenceDir = manifest.PathOf(sequence.dir);
            sequenceTransform = sequence.SceneToUnity();
            frameFilter = NewRenderer("Splat Sequence", null);
        }
    }

    /// <summary>pose frame -> show the matching splat frame (loads in the background, shows the latest ready)</summary>
    public void SetFrame(int frame, float poseFps)
    {
        lastFrame = frame;
        lastPoseFps = poseFps;
        if (sequence == null || !visible) return; // hidden splats load nothing

        float fps = sequence.fps > 0 ? sequence.fps : poseFps;
        wantedIndex = Mathf.RoundToInt(frame / poseFps * fps);

        if (frameCache.TryGetValue(wantedIndex, out Mesh cached))
        {
            frameFilter.sharedMesh = cached;
            Touch(wantedIndex);
        }
        else if (pendingFrame == null)
        {
            StartFrameLoad(wantedIndex);
        }
    }

    void StartFrameLoad(int index)
    {
        string path = Path.Combine(sequenceDir, string.Format(sequence.pattern, index));
        if (!File.Exists(path)) return;

        pendingIndex = index;
        Matrix4x4 toUnity = sequenceTransform;
        int max = MaxFrameSplats;
        pendingFrame = Task.Run(() => SplatPly.Load(path, toUnity, max));
    }

    void Update()
    {
        if (pendingFrame == null || !pendingFrame.IsCompleted) return;

        if (!pendingFrame.IsFaulted)
        {
            Mesh mesh = BuildMesh(pendingFrame.Result);
            frameCache[pendingIndex] = mesh;
            Touch(pendingIndex);
            while (frameOrder.Count > FrameCacheSize)
            {
                int evict = frameOrder.Last.Value;
                frameOrder.RemoveLast();
                Destroy(frameCache[evict]);
                frameCache.Remove(evict);
            }

            // Playback can advance while disk loading completes. Show the latest
            // completed frame instead of leaving the screen blank indefinitely.
            frameFilter.sharedMesh = mesh;
        }
        else Debug.LogError($"Splat frame {pendingIndex}: {pendingFrame.Exception?.GetBaseException().Message}");

        pendingFrame = null;
        if (wantedIndex != pendingIndex && !frameCache.ContainsKey(wantedIndex)) StartFrameLoad(wantedIndex);
    }

    void Touch(int index)
    {
        frameOrder.Remove(index);
        frameOrder.AddFirst(index);
    }

    public void SetVisible(bool show)
    {
        visible = show;
        if (environment != null) environment.SetActive(show);
        if (frameFilter != null) frameFilter.gameObject.SetActive(show);
        if (show && lastFrame >= 0) SetFrame(lastFrame, lastPoseFps);
    }

    public void Clear()
    {
        if (environment != null) Destroy(environment);
        if (frameFilter != null) Destroy(frameFilter.gameObject);
        foreach (Mesh m in frameCache.Values) Destroy(m);
        frameCache.Clear();
        frameOrder.Clear();
        sequence = null;
        pendingFrame = null;
        wantedIndex = pendingIndex = -1;
    }

    MeshFilter NewRenderer(string objectName, Mesh mesh)
    {
        GameObject go = new(objectName);
        go.transform.SetParent(transform, false);
        MeshFilter filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;
        MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        return filter;
    }

    /// <summary>4 vertices per splat, all at the centre; the shader expands them into a view-facing quad</summary>
    public static Mesh BuildMesh(SplatPly.Data data)
    {
        int n = data.Positions.Length;
        Vector3[] vertices = new Vector3[n * 4];
        Color[] colors = new Color[n * 4];
        Vector2[] corners = new Vector2[n * 4];
        Vector2[] radii = new Vector2[n * 4];
        int[] indices = new int[n * 6];
        Vector2[] quad = { new(-1, -1), new(1, -1), new(1, 1), new(-1, 1) };
        Bounds bounds = n > 0 ? new Bounds(data.Positions[0], Vector3.zero) : new Bounds();
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < 4; k++)
            {
                int v = i * 4 + k;
                vertices[v] = data.Positions[i];
                colors[v] = data.Colors[i];
                corners[v] = quad[k];
                radii[v] = new Vector2(data.Radii[i], 0);
            }

            int t = i * 6;
            indices[t] = i * 4;
            indices[t + 1] = i * 4 + 1;
            indices[t + 2] = i * 4 + 2;
            indices[t + 3] = i * 4;
            indices[t + 4] = i * 4 + 2;
            indices[t + 5] = i * 4 + 3;
            bounds.Encapsulate(data.Positions[i]);
        }

        Mesh mesh = new() { indexFormat = IndexFormat.UInt32 };
        mesh.vertices = vertices;
        mesh.colors = colors;
        mesh.SetUVs(0, corners);
        mesh.SetUVs(1, radii);
        mesh.SetIndices(indices, MeshTopology.Triangles, 0, false);
        bounds.Expand(1f);
        mesh.bounds = bounds;
        return mesh;
    }
}
