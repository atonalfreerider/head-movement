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
/// Layer "physics": per dancer a COM marker, the extrapolated COM (XCoM) on the floor coloured by the balance margin
/// and the support polygon. The net external force F_net = m(a - g) and its 95 % band are numbers only (HUD line,
/// hm_state): its arrow from the COM (mostly straight up: body weight) with the band ring at the tip, and the plumb
/// line from the COM to the floor, were removed 2026-10-07 - together they drew a gold up-arrow axis through the
/// leader and a white one through the follower (user: the leader never gets an up axis; the follower only gets the
/// white neck axis while her neck is more than 15 deg off-axis, NeckAxisOverlay).
/// </summary>
public class PhysicsOverlay : MonoBehaviour
{
    PhysicsData data;
    CaptureTimeline timeline;
    double[] audioT;
    int frame = -1;
    bool visible = true;

    class Visual
    {
        public GameObject Com, Xcom;
        public LineRenderer Support;
    }

    readonly Dictionary<Role, Visual> visuals = new();
    /// <summary>support polygon line width (m) at full alpha (a LineRenderer's widthMultiplier is its width in metres)</summary>
    public const float SupportWidth = 0.006f;

    bool leadHidden, followHidden;
    float leadAlpha = 1f, followAlpha = 1f;

    public int Frame => frame;

    /// <summary>roleHidden (RoleHiddenSpans): a role's COM / XCoM / support markers grow in with its alpha 0..1 (they are solid
    /// primitives: a size fade) and are not drawn at ~0</summary>
    public void SetRoleAlpha(float lead, float follow)
    {
        lead = Mathf.Clamp01(float.IsFinite(lead) ? lead : 1f);
        follow = Mathf.Clamp01(float.IsFinite(follow) ? follow : 1f);
        if (Mathf.Abs(leadAlpha - lead) < 1e-4f && Mathf.Abs(followAlpha - follow) < 1e-4f) return;
        leadAlpha = lead;
        followAlpha = follow;
        leadHidden = lead <= RoleHiddenSpans.HiddenBelow;
        followHidden = follow <= RoleHiddenSpans.HiddenBelow;
        if (visible && frame >= 0) Apply(frame, blendOther, blendK);
    }

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
                Support = OverlayDraw.Line(transform, $"{role} support polygon", glow, SupportWidth, 2, true)
            };
            v.Xcom.transform.localScale = new Vector3(0.06f, 0.002f, 0.06f);
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
            Apply(f, blendOther, blendK);
        }
    }

    public void SetTime(float audioTime)
    {
        int f = CaptureTimeline.Nearest(audioT, audioTime);
        if (f == frame && blendK == 0f) return;
        if (!visible)
        {
            frame = f;
            blendOther = -1;
            blendK = 0f;
            return;
        }

        Apply(f, -1, 0f);
    }

    /// <summary>sub-frame time (the film director's slow motion): the COM and XCoM markers lerp between the two
    /// neighbouring frames, so they glide with the (also blended) skeleton instead of stepping at the capture rate. The
    /// support polygon, balance colour and everything else stay on the nearer frame.</summary>
    public void SetExactTime(float audioTime)
    {
        if (audioT == null || audioT.Length == 0) return;
        int f = CaptureTimeline.Nearest(audioT, audioTime);
        int other = -1;
        float k = 0f;
        if (f >= 0)
        {
            int o = audioTime >= audioT[f] ? f + 1 : f - 1;
            if (o >= 0 && o < audioT.Length && audioT[o] != audioT[f])
            {
                other = o;
                k = Mathf.Clamp01((float)(System.Math.Abs(audioTime - audioT[f]) / System.Math.Abs(audioT[o] - audioT[f])));
            }
        }

        if (f == frame && other == blendOther && Mathf.Abs(k - blendK) < 1e-4f) return;
        if (!visible)
        {
            frame = f;
            blendOther = other;
            blendK = k;
            return;
        }

        Apply(f, other, k);
    }

    // sub-frame blend of the shown frame toward the neighbour `blendOther` by blendK (0..0.5; -1 = none)
    int blendOther = -1;
    float blendK;

    static Vector3 BlendCom(Vector3[] a, int f, int o, float k)
    {
        Vector3 nan = new(float.NaN, float.NaN, float.NaN);
        Vector3 v = a != null && f < a.Length ? a[f] : nan;
        if (o < 0 || k <= 0f || a == null || o >= a.Length) return v;
        Vector3 w = a[o];
        return Finite(v) && Finite(w) ? Vector3.Lerp(v, w, k) : v;
    }

    static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

    void Apply(int f, int other, float k)
    {
        frame = f;
        blendOther = other;
        blendK = k;
        foreach ((Role role, Visual v) in visuals)
        {
            if (role == Role.Lead ? leadHidden : followHidden)
            {
                v.Com.SetActive(false);
                v.Xcom.SetActive(false);
                v.Support.gameObject.SetActive(false);
                continue;
            }

            PhysicsData.DancerTrack d = data.Dancers[role];
            // physics.py writes non-finite values (flight, gaps, repaired frames) as null -> NaN here. Every visual
            // of a dancer is shown only when its inputs are finite this frame: nothing keeps a stale position and
            // no LineRenderer ever receives a NaN point.
            Vector3 nan3 = new(float.NaN, float.NaN, float.NaN);
            Vector3 com = d.Com != null ? BlendCom(d.Com, f, other, k) : nan3;
            Vector2 x = d.XcomXZ != null && f < d.XcomXZ.Length ? d.XcomXZ[f] : new Vector2(float.NaN, float.NaN);
            if (other >= 0 && k > 0f && d.XcomXZ != null && other < d.XcomXZ.Length)
            {
                Vector2 xo = d.XcomXZ[other];
                if (Finite(x.x) && Finite(x.y) && Finite(xo.x) && Finite(xo.y)) x = Vector2.Lerp(x, xo, k);
            }
            Vector2[] poly = d.Support != null && f < d.Support.Length ? d.Support[f] : null;
            bool comOk = Finite(com);
            bool xcomOk = Finite(x.x) && Finite(x.y);
            bool supportOk = comOk && poly != null && poly.Length >= 2;

            v.Com.SetActive(comOk);
            v.Xcom.SetActive(xcomOk);
            v.Support.gameObject.SetActive(supportOk);

            float fade = role == Role.Lead ? leadAlpha : followAlpha; // roleHidden fade-in: the solid markers grow in
            v.Com.transform.localScale = Vector3.one * (0.07f * Mathf.Max(fade, 0.001f));
            v.Xcom.transform.localScale = new Vector3(0.06f * Mathf.Max(fade, 0.001f), 0.002f, 0.06f * Mathf.Max(fade, 0.001f));
            v.Support.widthMultiplier = SupportWidth * fade; // a FRACTION of the line's own width: the fade must never become the width (1 = a 1 m wide band)
            if (comOk) v.Com.transform.position = com;

            if (xcomOk)
            {
                v.Xcom.transform.position = new Vector3(x.x, 0.004f, x.y);
                float margin = d.BalanceMargin != null && f < d.BalanceMargin.Length ? d.BalanceMargin[f] : float.NaN;
                OverlayDraw.SetMarkerColor(v.Xcom, !Finite(margin) ? Color.gray
                    : margin >= 0.03f ? new Color(0.2f, 1f, 0.4f)
                    : margin >= 0f ? new Color(1f, 0.8f, 0.1f) : new Color(1f, 0.15f, 0.1f));
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
        GameObject[] all = { v.Com, v.Xcom, v.Support.gameObject };
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

    /// <summary>vertical lines / arrows this overlay draws through a dancer (playtests: 0 - no F_net arrow, no plumb)</summary>
    public int AxisLines(Role role)
    {
        if (!visuals.TryGetValue(role, out Visual v)) return 0;
        int n = 0;
        foreach (LineRenderer l in GetComponentsInChildren<LineRenderer>())
        {
            if (l == v.Support || !l.gameObject.activeInHierarchy || !l.name.StartsWith(role.ToString(), System.StringComparison.Ordinal)) continue;
            n++;
        }

        return n;
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
            ["activeVisuals"] = ActiveVisuals(role), ["axisLines"] = AxisLines(role),
            // sizes of what is drawn (m): the playtest asserts they stay foot-sized or smaller (a 1 m wide support line once
            // painted discs under the dancers when the roleHidden fade replaced its width)
            ["supportWidthM"] = visuals[role].Support.widthMultiplier,
            ["comDiameterM"] = visuals[role].Com.transform.localScale.x,
            ["xcomDiameterM"] = visuals[role].Xcom.transform.localScale.x
        };
    }
}
