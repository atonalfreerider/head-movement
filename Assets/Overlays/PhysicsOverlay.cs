using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// physics.json (dancecap physics stage, L0 centroidal dynamics) as data, already converted to Unity coordinates:
/// world (x, y, z) -> (x, y, -z), floor xz -> (x, -z). Times are reference seconds on the smplx grid.
/// </summary>
public class PhysicsData
{
    public class DancerTrack
    {
        public float MassKg;
        public Vector3[] Com, FNet, FNetBand;
        public Vector2[] XcomXZ; // unity x, z
        public float[] BalanceMargin; // m, + = XCoM inside the support polygon
        public string[] Contact; // "L" "R" "LR" ""
        public Vector2[][] Support; // unity x, z polygon (NaN-padded entries dropped)
    }

    public double[] T;
    public readonly Dictionary<Role, DancerTrack> Dancers = new();
    public string[] PartnerStatus;

    public static PhysicsData Load(string path)
    {
        JObject root = JObject.Parse(File.ReadAllText(path));
        PhysicsData d = new() { T = root["t"].Select(x => x.Value<double>()).ToArray() };
        foreach (Role role in new[] { Role.Lead, Role.Follow })
        {
            if (root["dancers"]?[role.ToString().ToLowerInvariant()] is not JObject j) continue;
            d.Dancers[role] = new DancerTrack
            {
                MassKg = Num(j["mass_kg"]),
                Com = Vec3(j["com"]), FNet = Vec3(j["f_net"]), FNetBand = Vec3(j["f_net_band"], false),
                XcomXZ = j["xcom_xz"].Select(Xz).ToArray(),
                BalanceMargin = j["balance_margin"].Select(Num).ToArray(),
                Contact = j["contact"]?.Select(x => x.Type == JTokenType.String ? x.Value<string>() : "").ToArray(),
                Support = j["support_xz"]?.Select(f => f.Type == JTokenType.Array
                    ? f.Select(Xz).Where(p => !float.IsNaN(p.x) && !float.IsNaN(p.y)).ToArray()
                    : new Vector2[0]).ToArray()
            };
        }

        if (root["partner"]?["status"] is JArray status) d.PartnerStatus = status.Select(s => s.Value<string>()).ToArray();
        return d;
    }

    static float Num(JToken t) =>
        t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? t.Value<float>() : float.NaN;

    static Vector2 Xz(JToken t) =>
        t is JArray a && a.Count == 2 ? new Vector2(Num(a[0]), -Num(a[1])) : new Vector2(float.NaN, float.NaN);

    /// <summary>(x, y, z) world -> Unity (x, y, -z); bands are magnitudes, so they keep their sign</summary>
    static Vector3[] Vec3(JToken arr, bool mirror = true) => arr.Select(t =>
        t is JArray a && a.Count == 3
            ? new Vector3(Num(a[0]), Num(a[1]), mirror ? -Num(a[2]) : Num(a[2]))
            : new Vector3(float.NaN, float.NaN, float.NaN)).ToArray();
}

/// <summary>
/// Layer "physics": per dancer a COM marker (with a plumb line to the floor), the extrapolated COM (XCoM) on the
/// floor coloured by the balance margin, the support polygon, and the net external force F_net = m(a - g) as an
/// arrow from the COM with its 95 % band (ring = horizontal band, tick = vertical band at the tip).
/// </summary>
public class PhysicsOverlay : MonoBehaviour
{
    [Tooltip("arrow length per newton (body weight ~ 0.7-0.9 m)")]
    public float MetresPerNewton = 0.001f;

    PhysicsData data;
    CaptureTimeline timeline;
    double[] audioT;
    int frame = -1;
    bool visible = true;

    class Visual
    {
        public GameObject Com, Xcom;
        public LineRenderer Plumb, Arrow, BandRing, BandTick, Support;
    }

    readonly Dictionary<Role, Visual> visuals = new();

    public int Frame => frame;

    public void Init(PhysicsData physics, CaptureTimeline frames, Material glow)
    {
        data = physics;
        timeline = frames;
        audioT = data.T.Select(t => timeline.ToAudio(t)).ToArray();
        foreach (Role role in data.Dancers.Keys)
        {
            Color c = role == Role.Lead ? new Color(1f, 0.45f, 0.2f) : new Color(0.55f, 0.85f, 1f);
            Visual v = new()
            {
                Com = OverlayDraw.Marker(transform, $"{role} COM", PrimitiveType.Sphere, 0.07f, c),
                Xcom = OverlayDraw.Marker(transform, $"{role} XCoM", PrimitiveType.Cylinder, 1f, Color.green),
                Plumb = OverlayDraw.Line(transform, $"{role} COM plumb", glow, 0.004f),
                Arrow = OverlayDraw.Line(transform, $"{role} F_net", glow, 0.014f, 5),
                BandRing = OverlayDraw.Line(transform, $"{role} F_net band", glow, 0.005f, 24, true),
                BandTick = OverlayDraw.Line(transform, $"{role} F_net band (vertical)", glow, 0.006f),
                Support = OverlayDraw.Line(transform, $"{role} support polygon", glow, 0.006f, 2, true)
            };
            v.Xcom.transform.localScale = new Vector3(0.06f, 0.002f, 0.06f);
            OverlayDraw.SetColor(v.Plumb, c * 0.6f);
            OverlayDraw.SetColor(v.Arrow, c * 2.2f);
            OverlayDraw.SetColor(v.BandRing, c * 1.2f);
            OverlayDraw.SetColor(v.BandTick, c * 1.2f);
            OverlayDraw.SetColor(v.Support, c * 0.9f);
            visuals[role] = v;
        }
    }

    public void SetVisible(bool on)
    {
        visible = on;
        foreach (Transform child in transform) child.gameObject.SetActive(on);
        if (on && frame >= 0)
        {
            int f = frame;
            frame = -1;
            Apply(f);
        }
    }

    public void SetTime(float audioTime)
    {
        int f = CaptureTimeline.Nearest(audioT, audioTime);
        if (f == frame) return;
        if (!visible)
        {
            frame = f;
            return;
        }

        Apply(f);
    }

    static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

    void Apply(int f)
    {
        frame = f;
        Vector3 viewer = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
        foreach ((Role role, Visual v) in visuals)
        {
            PhysicsData.DancerTrack d = data.Dancers[role];
            // physics.py writes non-finite values (flight, gaps, repaired frames) as null -> NaN here. Every visual
            // of a dancer is shown only when its inputs are finite this frame: nothing keeps a stale position and
            // no LineRenderer ever receives a NaN point.
            Vector3 nan3 = new(float.NaN, float.NaN, float.NaN);
            Vector3 com = d.Com != null && f < d.Com.Length ? d.Com[f] : nan3;
            Vector3 force = d.FNet != null && f < d.FNet.Length ? d.FNet[f] : nan3;
            Vector3 band = d.FNetBand != null && f < d.FNetBand.Length ? d.FNetBand[f] : nan3;
            Vector2 x = d.XcomXZ != null && f < d.XcomXZ.Length ? d.XcomXZ[f] : new Vector2(float.NaN, float.NaN);
            Vector2[] poly = d.Support != null && f < d.Support.Length ? d.Support[f] : null;
            bool comOk = Finite(com);
            bool forceOk = comOk && Finite(force);
            bool bandOk = forceOk && Finite(band);
            bool xcomOk = Finite(x.x) && Finite(x.y);
            bool supportOk = comOk && poly != null && poly.Length >= 2;

            v.Com.SetActive(comOk);
            v.Plumb.gameObject.SetActive(comOk);
            v.Arrow.gameObject.SetActive(forceOk);
            v.BandRing.gameObject.SetActive(bandOk);
            v.BandTick.gameObject.SetActive(bandOk);
            v.Xcom.SetActive(xcomOk);
            v.Support.gameObject.SetActive(supportOk);

            if (comOk)
            {
                v.Com.transform.position = com;
                v.Plumb.SetPosition(0, com);
                v.Plumb.SetPosition(1, new Vector3(com.x, 0.003f, com.z));
            }

            if (xcomOk)
            {
                v.Xcom.transform.position = new Vector3(x.x, 0.004f, x.y);
                float margin = d.BalanceMargin != null && f < d.BalanceMargin.Length ? d.BalanceMargin[f] : float.NaN;
                OverlayDraw.SetMarkerColor(v.Xcom, !Finite(margin) ? Color.gray
                    : margin >= 0.03f ? new Color(0.2f, 1f, 0.4f)
                    : margin >= 0f ? new Color(1f, 0.8f, 0.1f) : new Color(1f, 0.15f, 0.1f));
            }

            if (forceOk)
            {
                Vector3 tip = com + force * MetresPerNewton;
                OverlayDraw.Arrow(v.Arrow, com, tip, viewer);
                if (bandOk)
                {
                    Vector3 b = band * MetresPerNewton;
                    float ring = new Vector2(b.x, b.z).magnitude; // finite: bandOk
                    float tick = Mathf.Abs(b.y);
                    OverlayDraw.Ring(v.BandRing, tip, ring > 0.01f ? ring : 0.01f);
                    v.BandTick.SetPosition(0, tip - Vector3.up * (tick > 0.005f ? tick : 0.005f));
                    v.BandTick.SetPosition(1, tip + Vector3.up * (tick > 0.005f ? tick : 0.005f));
                }
            }

            if (supportOk)
            {
                v.Support.positionCount = poly.Length;
                for (int i = 0; i < poly.Length; i++) v.Support.SetPosition(i, new Vector3(poly[i].x, 0.005f, poly[i].y));
            }
        }
    }

    /// <summary>visual objects of a dancer that are currently shown (playtests: none may hold a stale frame)</summary>
    public int ActiveVisuals(Role role)
    {
        if (!visuals.TryGetValue(role, out Visual v)) return 0;
        GameObject[] all =
        {
            v.Com, v.Xcom, v.Plumb.gameObject, v.Arrow.gameObject, v.BandRing.gameObject, v.BandTick.gameObject,
            v.Support.gameObject
        };
        return all.Count(g => g.activeInHierarchy);
    }

    void OnDestroy()
    {
        // OverlayDraw.Marker creates one material per marker: free them with the overlay (capture reloads)
        foreach (Visual v in visuals.Values)
        {
            foreach (GameObject marker in new[] { v.Com, v.Xcom })
            {
                if (marker == null) continue;
                Renderer r = marker.GetComponent<Renderer>();
                if (r != null && r.sharedMaterial != null) Destroy(r.sharedMaterial);
            }
        }

        visuals.Clear();
    }

    public IEnumerable<string> HudLines()
    {
        if (frame < 0) yield break;
        foreach ((Role role, PhysicsData.DancerTrack d) in data.Dancers)
        {
            Vector3 fN = d.FNet[frame], b = d.FNetBand[frame];
            if (float.IsNaN(fN.x) || float.IsNaN(d.Com[frame].x))
            {
                yield return $"{role}: no physics this frame (gap / flight)";
                continue;
            }

            float margin = d.BalanceMargin[frame];
            string contact = d.Contact != null && frame < d.Contact.Length ? d.Contact[frame] : "?";
            yield return $"{role}: F_net {fN.magnitude:0} N (vert {fN.y:0} ±{b.y:0}, horiz ±{new Vector2(b.x, b.z).magnitude:0})" +
                         $"  margin {margin * 100:+0.0;-0.0} cm  contact {(string.IsNullOrEmpty(contact) ? "flight" : contact)}";
        }

        if (data.PartnerStatus != null && frame < data.PartnerStatus.Length)
        {
            yield return $"partner force: {data.PartnerStatus[frame].Replace('_', ' ')}";
        }
    }

    /// <summary>com/xcom/margin of a dancer at the current frame, for playtests</summary>
    public Dictionary<string, object> State(Role role)
    {
        if (frame < 0 || !data.Dancers.TryGetValue(role, out PhysicsData.DancerTrack d)) return null;
        Vector3 com = d.Com[frame];
        return new Dictionary<string, object>
        {
            ["com"] = new[] { com.x, com.y, com.z }, ["fNetN"] = d.FNet[frame].magnitude,
            ["balanceMargin"] = d.BalanceMargin[frame], ["comMarkerActive"] = visuals[role].Com.activeInHierarchy,
            ["activeVisuals"] = ActiveVisuals(role)
        };
    }
}
