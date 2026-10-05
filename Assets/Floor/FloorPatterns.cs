using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Glowing footprints on the floor for every detected step, coloured by how close the step landed to the
/// nearest zouk beat. Upcoming steps are drawn faintly so the floor pattern of the figure is visible before
/// it is danced; recent steps flare up and fade.
/// </summary>
public class FloorPatterns : MonoBehaviour
{
    [Header("Step detection (ankle contact with hysteresis)")]
    [Tooltip("ankle height (m) above its floor level at which a descending foot counts as landed")]
    public float ContactHeight = 0.02f;
    [Tooltip("ankle height (m) above its floor level at which the foot counts as lifted again")]
    public float LiftHeight = 0.04f;
    [Tooltip("max |vertical ankle speed| (m/s) for a landing - the foot has stopped descending")]
    public float LandingVerticalSpeed = 0.15f;
    [Tooltip("max horizontal ankle speed (m/s) for a landing")]
    public float PlantSpeed = 0.35f;
    [Tooltip("horizontal ankle speed (m/s) that ends a contact even without lifting (gliding steps)")]
    public float SlideSpeed = 0.6f;

    [Header("Timing")]
    [Tooltip("bit mask of beat types steps are judged against (bit n = zouk beat type n); default all eighths")]
    public int BeatTypeMask = (1 << 1) | (1 << 2) | (1 << 3);
    public float OnBeatMs = 45f;
    public float NearBeatMs = 100f;

    [Header("Display")]
    public float RecentSeconds = 2.5f;
    public float FutureBrightness = 0.07f;
    public float PastBrightness = 0.22f;

    static readonly Color OnBeat = new(0.15f, 1f, 0.55f);
    static readonly Color NearBeat = new(1f, 0.65f, 0.1f);
    static readonly Color OffBeat = new(1f, 0.12f, 0.08f);
    static readonly Color NoBeats = new(0.6f, 0.6f, 0.6f);

    public struct Step
    {
        public Role Role;
        public bool Left;
        public int Frame;
        public float AudioTime;
        public Vector3 Position;
        public Vector3 Heading;
        public float ErrorMs; // + = late, - = early, NaN without beats
    }

    public readonly List<Step> Steps = new();

    readonly List<GameObject> spawned = new();
    readonly Dictionary<Role, (Mesh mesh, Color[] colors, List<int> steps)> meshes = new();
    const int VertsPerPrint = 7;
    float fps;
    float audioOffset;
    int lastFrame = -1;

    public void Init(Dancer lead, Dancer follow, Material glowMat, BeatGrid beats, float fps, float audioOffset)
    {
        Clear();
        this.fps = fps;
        this.audioOffset = audioOffset;

        foreach (Dancer dancer in new[] { lead, follow })
        {
            DetectSteps(dancer, beats, SmplJoint.L_Foot, SmplJoint.L_Ankle, true);
            DetectSteps(dancer, beats, SmplJoint.R_Foot, SmplJoint.R_Ankle, false);
        }

        Steps.Sort((a, b) => a.Frame.CompareTo(b.Frame));

        foreach (Role role in new[] { Role.Lead, Role.Follow })
        {
            BuildMesh(role, glowMat);
            BuildTrail(role, glowMat);
        }

        lastFrame = -1;
    }

    public void Clear()
    {
        foreach (GameObject go in spawned)
        {
            if (go != null) Destroy(go);
        }

        spawned.Clear();
        meshes.Clear();
        Steps.Clear();
    }

    public void SetVisible(bool visible)
    {
        foreach (GameObject go in spawned)
        {
            if (go != null) go.SetActive(visible);
        }
    }

    void DetectSteps(Dancer dancer, BeatGrid beats, SmplJoint foot, SmplJoint ankle, bool left)
    {
        int n = dancer.FrameCount;
        if (n < 3) return;

        // floor level of this ankle: a low percentile rather than the minimum, robust to pose jitter
        float[] heights = new float[n];
        for (int f = 0; f < n; f++) heights[f] = dancer.Joint(f, ankle).y;
        Array.Sort(heights);
        float floorY = heights[Mathf.FloorToInt((n - 1) * 0.02f)];

        bool inContact = true; // don't count the stance the take starts in as a step
        for (int f = 1; f < n - 1; f++)
        {
            float h = dancer.Joint(f, ankle).y - floorY;
            Vector3 v = (dancer.Joint(f + 1, ankle) - dancer.Joint(f - 1, ankle)) * (fps * 0.5f);
            float horizontalSpeed = new Vector2(v.x, v.z).magnitude;

            if (inContact)
            {
                if (h > LiftHeight || horizontalSpeed > SlideSpeed) inContact = false;
                continue;
            }

            bool landed = h < ContactHeight && Mathf.Abs(v.y) < LandingVerticalSpeed && horizontalSpeed < PlantSpeed;
            if (!landed) continue;

            inContact = true;
            int landing = f;
            Vector3 position = dancer.Joint(landing, foot);
            Vector3 heading = position - dancer.Joint(landing, ankle);
            heading.y = 0;

            float audioTime = audioOffset + landing / fps;
            float errorMs = float.NaN;
            if (beats != null && beats.Count > 0)
            {
                int nearest = beats.Nearest(audioTime, BeatTypeMask);
                if (nearest >= 0) errorMs = (audioTime - beats.Times[nearest]) * 1000f;
            }

            Steps.Add(new Step
            {
                Role = dancer.DancerRole,
                Left = left,
                Frame = landing,
                AudioTime = audioTime,
                Position = new Vector3(position.x, 0.003f, position.z),
                Heading = heading.sqrMagnitude > 1e-6f ? heading.normalized : Vector3.forward,
                ErrorMs = errorMs
            });
        }
    }

    Color TimingColor(float errorMs)
    {
        if (float.IsNaN(errorMs)) return NoBeats;

        float e = Mathf.Abs(errorMs);
        if (e <= OnBeatMs) return OnBeat;
        if (e <= NearBeatMs) return Color.Lerp(NearBeat, OffBeat, (e - OnBeatMs) / (NearBeatMs - OnBeatMs) * 0.5f);
        return OffBeat;
    }

    void BuildMesh(Role role, Material glowMat)
    {
        List<int> indices = new();
        for (int i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Role == role) indices.Add(i);
        }

        // lead: broader print, follow: narrower - so the two patterns read apart where they overlap
        float length = role == Role.Lead ? 0.12f : 0.10f;
        float width = role == Role.Lead ? 0.065f : 0.045f;

        Vector3[] vertices = new Vector3[indices.Count * VertsPerPrint];
        int[] triangles = new int[indices.Count * 6 * 3];
        for (int k = 0; k < indices.Count; k++)
        {
            Step s = Steps[indices[k]];
            Vector3 forward = s.Heading;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            int v0 = k * VertsPerPrint;
            vertices[v0] = s.Position;
            for (int j = 0; j < 6; j++)
            {
                float a = j * Mathf.PI / 3f;
                vertices[v0 + 1 + j] = s.Position + forward * (Mathf.Cos(a) * length * 0.5f) +
                                       right * (Mathf.Sin(a) * width * 0.5f);
                int t = (k * 6 + j) * 3;
                triangles[t] = v0;
                triangles[t + 1] = v0 + 1 + j;
                triangles[t + 2] = v0 + 1 + (j + 1) % 6;
            }
        }

        Mesh mesh = new() { name = $"{role} footprints", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        Color[] colors = new Color[vertices.Length];
        mesh.colors = colors;
        mesh.RecalculateBounds();

        GameObject go = new($"{role} Floor Pattern");
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = glowMat;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        spawned.Add(go);
        meshes[role] = (mesh, colors, indices);
    }

    void BuildTrail(Role role, Material glowMat)
    {
        List<Vector3> points = new();
        foreach (Step s in Steps)
        {
            if (s.Role == role) points.Add(s.Position + Vector3.up * 0.001f);
        }

        if (points.Count < 2) return;

        GameObject go = new($"{role} Floor Trail");
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.material = glowMat;
        line.useWorldSpace = true;
        line.widthMultiplier = 0.004f;
        line.positionCount = points.Count;
        line.SetPositions(points.ToArray());
        line.alignment = LineAlignment.TransformZ;
        go.transform.rotation = Quaternion.Euler(90, 0, 0); // lie flat on the floor
        Color c = role == Role.Lead ? new Color(0.2f, 0.01f, 0f) : new Color(0.12f, 0.12f, 0.12f);
        line.startColor = line.endColor = c;
        spawned.Add(go);
    }

    public void SetFrame(int frame)
    {
        if (frame == lastFrame) return;
        lastFrame = frame;

        foreach ((Mesh mesh, Color[] colors, List<int> indices) in meshes.Values)
        {
            for (int k = 0; k < indices.Count; k++)
            {
                Step s = Steps[indices[k]];
                float age = (frame - s.Frame) / fps;
                float brightness = age < 0 ? FutureBrightness
                    : age < RecentSeconds ? Mathf.Lerp(1f, PastBrightness, age / RecentSeconds)
                    : PastBrightness;
                Color c = TimingColor(s.ErrorMs) * brightness;
                c.a = 1;
                for (int j = 0; j < VertsPerPrint; j++)
                {
                    // hotter centre
                    colors[k * VertsPerPrint + j] = j == 0 ? c * 1.6f : c;
                }
            }

            mesh.colors = colors;
        }
    }

    /// <summary>the most recent step at or before frame for a role (for the HUD)</summary>
    public bool LatestStep(Role role, int frame, out Step step)
    {
        for (int i = Steps.Count - 1; i >= 0; i--)
        {
            if (Steps[i].Role == role && Steps[i].Frame <= frame)
            {
                step = Steps[i];
                return true;
            }
        }

        step = default;
        return false;
    }
}
