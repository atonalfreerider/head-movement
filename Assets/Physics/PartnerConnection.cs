using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Tension / compression between partners, estimated from poses alone (no force sensors).
///
/// Each connection is modelled as a spring-damper spanning the lead's anchor (shoulder or chest) and the
/// follow's centre of mass, active while the contact points are close:
///
///   signal = Stretch * (L - L0) / L0            elastic: the frame is longer/shorter than its neutral length
///          + Damping * dL/dt                    viscous: the frame is being pulled open / pushed shut
///          + Inertia * (a_follow . u) / g       Newton: force needed to accelerate the follow along the line
///
/// L0 is the median frame length while that connection is in contact (the couple's neutral frame), u points
/// from the follow towards the lead. Positive = tension (pull), negative = compression (push). The weights are
/// tuning knobs to calibrate against dancers' own sense of the connection, not measured physics.
/// </summary>
public class PartnerConnection : MonoBehaviour
{
    public float Stretch = 4f;
    public float Damping = 0.8f;
    public float Inertia = 1.5f;
    [Tooltip("frames of centred smoothing applied before differentiating (pose noise dominates otherwise)")]
    public int SmoothRadius = 3;
    public float MaxWidth = 0.035f;

    static readonly Color TensionColor = new(1f, 0.35f, 0.05f);
    static readonly Color CompressionColor = new(0.1f, 0.55f, 1f);
    static readonly Color NeutralColor = new(0.35f, 0.35f, 0.4f);

    public class Connection
    {
        public string Name;
        public float ContactDistance;
        public Func<int, Vector3> LeadAnchor;
        public Func<int, Vector3> LeadPoint;
        public Func<int, Vector3>[] FollowPoints;

        public bool[] Active;
        public float[] Signal;
        public Vector3[] FollowContact;
        public LineRenderer Line;
    }

    public readonly List<Connection> Connections = new();
    Dancer lead;
    Dancer follow;
    readonly List<GameObject> spawned = new();

    public void Init(Dancer lead, Dancer follow, Material glowMat, float fps)
    {
        Clear();
        this.lead = lead;
        this.follow = follow;

        Connections.Add(new Connection
        {
            Name = "lead L hand",
            ContactDistance = 0.15f,
            LeadAnchor = f => lead.GetLeftShoulder(f),
            LeadPoint = f => lead.GetLeftHandContact(f),
            FollowPoints = new Func<int, Vector3>[] { follow.GetRightHandContact, follow.GetLeftHandContact }
        });
        Connections.Add(new Connection
        {
            Name = "lead R hand",
            ContactDistance = 0.15f,
            LeadAnchor = f => lead.GetRightShoulder(f),
            LeadPoint = f => lead.GetRightHandContact(f),
            FollowPoints = new Func<int, Vector3>[]
            {
                follow.GetLeftHandContact, follow.GetRightHandContact, follow.GetSpine3, follow.GetNeckNape,
                follow.GetLeftShoulder, follow.GetRightShoulder
            }
        });
        Connections.Add(new Connection
        {
            Name = "chest",
            ContactDistance = 0.4f,
            LeadAnchor = f => lead.GetSpine3(f),
            LeadPoint = f => lead.GetSpine3(f),
            FollowPoints = new Func<int, Vector3>[] { follow.GetSpine3 }
        });

        int n = Mathf.Min(lead.FrameCount, follow.FrameCount);
        Vector3[] com = new Vector3[n];
        for (int f = 0; f < n; f++) com[f] = follow.CenterOfMass(f);
        com = Smooth(com, SmoothRadius);
        Vector3[] comAcceleration = SecondDerivative(com, fps);

        foreach (Connection c in Connections)
        {
            Compute(c, n, fps, com, comAcceleration);
            c.Line = NewLine(glowMat, c.Name);
        }
    }

    void Compute(Connection c, int n, float fps, Vector3[] com, Vector3[] comAcceleration)
    {
        c.Active = new bool[n];
        c.Signal = new float[n];
        c.FollowContact = new Vector3[n];

        float[] length = new float[n];
        Vector3[] axis = new Vector3[n];
        List<float> activeLengths = new();
        for (int f = 0; f < n; f++)
        {
            Vector3 leadPoint = c.LeadPoint(f);
            float best = float.MaxValue;
            foreach (Func<int, Vector3> candidate in c.FollowPoints)
            {
                Vector3 p = candidate(f);
                float d = Vector3.Distance(leadPoint, p);
                if (d < best)
                {
                    best = d;
                    c.FollowContact[f] = p;
                }
            }

            c.Active[f] = best < c.ContactDistance;
            Vector3 span = c.LeadAnchor(f) - com[f];
            length[f] = span.magnitude;
            axis[f] = span.sqrMagnitude > 1e-8f ? span.normalized : Vector3.zero;
            if (c.Active[f]) activeLengths.Add(length[f]);
        }

        if (activeLengths.Count == 0) return;

        activeLengths.Sort();
        float restLength = activeLengths[activeLengths.Count / 2];
        length = Smooth(length, SmoothRadius);
        for (int f = 0; f < n; f++)
        {
            if (!c.Active[f]) continue;

            float dL = (length[Mathf.Min(f + 1, n - 1)] - length[Mathf.Max(f - 1, 0)]) * fps * 0.5f;
            float inertial = Vector3.Dot(comAcceleration[f], axis[f]) / 9.81f;
            c.Signal[f] = Stretch * (length[f] - restLength) / restLength + Damping * dL + Inertia * inertial;
        }
    }

    public void SetFrame(int frame)
    {
        foreach (Connection c in Connections)
        {
            if (c.Line == null || c.Active == null || frame >= c.Active.Length) continue;

            bool active = c.Active[frame];
            c.Line.enabled = active;
            if (!active) continue;

            float s = Mathf.Clamp(c.Signal[frame], -1f, 1f);
            Color color = s >= 0
                ? Color.Lerp(NeutralColor, TensionColor, s)
                : Color.Lerp(NeutralColor, CompressionColor, -s);
            float width = Mathf.Lerp(0.006f, MaxWidth, Mathf.Abs(s));

            // lead anchor -> contact -> follow centre of mass, so the line shows the whole frame under load
            c.Line.positionCount = 3;
            c.Line.SetPosition(0, c.LeadAnchor(frame));
            c.Line.SetPosition(1, Vector3.Lerp(c.LeadPoint(frame), c.FollowContact[frame], 0.5f));
            c.Line.SetPosition(2, follow.CenterOfMass(frame));
            c.Line.startColor = c.Line.endColor = color;
            c.Line.widthMultiplier = width;
        }
    }

    public void SetVisible(bool visible)
    {
        foreach (GameObject go in spawned)
        {
            if (go != null) go.SetActive(visible);
        }
    }

    public void Clear()
    {
        foreach (GameObject go in spawned)
        {
            if (go != null) Destroy(go);
        }

        spawned.Clear();
        Connections.Clear();
    }

    LineRenderer NewLine(Material mat, string lineName)
    {
        GameObject go = new($"Connection {lineName}");
        spawned.Add(go);
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.material = mat;
        line.useWorldSpace = true;
        line.numCapVertices = 4;
        line.enabled = false;
        AnimationCurve taper = new();
        taper.AddKey(0f, 0.3f);
        taper.AddKey(0.5f, 1f);
        taper.AddKey(1f, 0.3f);
        line.widthCurve = taper;
        return line;
    }

    static float[] Smooth(float[] x, int radius)
    {
        float[] y = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            int a = Mathf.Max(0, i - radius), b = Mathf.Min(x.Length - 1, i + radius);
            float sum = 0;
            for (int j = a; j <= b; j++) sum += x[j];
            y[i] = sum / (b - a + 1);
        }

        return y;
    }

    static Vector3[] Smooth(Vector3[] x, int radius)
    {
        Vector3[] y = new Vector3[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            int a = Mathf.Max(0, i - radius), b = Mathf.Min(x.Length - 1, i + radius);
            Vector3 sum = Vector3.zero;
            for (int j = a; j <= b; j++) sum += x[j];
            y[i] = sum / (b - a + 1);
        }

        return y;
    }

    static Vector3[] SecondDerivative(Vector3[] x, float fps)
    {
        Vector3[] a = new Vector3[x.Length];
        for (int i = 1; i < x.Length - 1; i++)
        {
            a[i] = (x[i + 1] - 2 * x[i] + x[i - 1]) * (fps * fps);
        }

        if (x.Length > 2)
        {
            a[0] = a[1];
            a[^1] = a[^2];
        }

        return a;
    }
}
