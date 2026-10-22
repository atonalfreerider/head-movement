using System.Collections.Generic;
using UnityEngine;

namespace HmFilm
{
    /// <summary>
    /// The maths of the v3 film flow (dancecap docs/RECAP_FILM.md "What the Unity director must implement"): ONE easing (smootherstep),
    /// the frustum surface envelopes, cylindrical coordinates about the couple, and the composite camera paths of the transitions -
    /// a line along a phone's optical axis, a cylindrical Bezier about the couple's centre, a crane (a vertical arc about the look
    /// point). Pure functions of their inputs: the director evaluates them from LIVE poses every frame, so a seek or a frame-locked
    /// recording shows exactly the same pictures. No Assembly-CSharp types (edit-mode tests: Assets/Film/Flow/Tests).
    /// </summary>
    public static class FlowMath
    {
        /// <summary>6u^5 - 15u^4 + 10u^3 on [0, 1] (zero velocity AND acceleration at both ends)</summary>
        public static float Smootherstep(float u)
        {
            u = Mathf.Clamp01(u);
            return u * u * u * (u * (u * 6f - 15f) + 10f);
        }

        /// <summary>smootherstep of (t - a) / (b - a); a step at a when b &lt;= a</summary>
        public static float Ramp(float t, float a, float b) => b - a <= 1e-6f ? (t >= a ? 1f : 0f) : Smootherstep((t - a) / (b - a));

        /// <summary>the frustum surface's alpha: 0 before a, 0 -> 1 over [a, b], 1 over [b, c], 1 -> 0 over [c, d], 0 from d on.
        /// Degenerate ends are steps: an ENTER envelope [a, b, c, c] is 0 from c (the full-frame video has taken over in that frame),
        /// a LEAVE envelope [a, a, c, d] is 1 from a (the full-frame video handed over in that frame).</summary>
        public static float Envelope(float a, float b, float c, float d, float t)
        {
            if (t < a || t >= d) return 0f;
            if (t < b) return Smootherstep((t - a) / (b - a));
            if (t <= c) return 1f;
            return 1f - Smootherstep((t - c) / (d - c));
        }

        /// <summary>angle wrapped to (-pi, pi]</summary>
        public static float WrapPi(float a)
        {
            a = Mathf.Repeat(a + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
            return a <= -Mathf.PI ? a + 2f * Mathf.PI : a;
        }

        /// <summary>the live azimuth difference (rad, wrapped) taken the plan's way round for a big turn: a planned turn of more than 120 deg keeps its
        /// direction when the live difference wraps past 180 deg (a short way that flips mid-flight would swing the camera round the other side)</summary>
        public static float Branch(float liveDelta, float planDelta)
        {
            float d = WrapPi(liveDelta);
            if (float.IsNaN(planDelta) || Mathf.Abs(planDelta) <= 120f * Mathf.Deg2Rad) return d;
            if (Mathf.Sign(d) != Mathf.Sign(planDelta) && Mathf.Abs(d) > 60f * Mathf.Deg2Rad) d += Mathf.Sign(planDelta) * 2f * Mathf.PI;
            return d;
        }

        /// <summary>(theta = atan2(z, x) of p - c, horizontal radius, absolute height)</summary>
        public static Vector3 ToCyl(Vector3 p, Vector3 c)
        {
            float dx = p.x - c.x, dz = p.z - c.z;
            return new Vector3(Mathf.Atan2(dz, dx), Mathf.Sqrt(dx * dx + dz * dz), p.y);
        }

        public static Vector3 FromCyl(Vector3 cyl, Vector3 c) => new(c.x + cyl.y * Mathf.Cos(cyl.x), cyl.z, c.z + cyl.y * Mathf.Sin(cyl.x));

        /// <summary>a Cartesian direction at p as a (d theta, d r, d y) per metre in cylindrical coordinates about c</summary>
        public static Vector3 CylTangent(Vector3 p, Vector3 c, Vector3 dir)
        {
            Vector3 cyl = ToCyl(p, c);
            float s = Mathf.Sin(cyl.x), co = Mathf.Cos(cyl.x), r = Mathf.Max(0.05f, cyl.y);
            return new Vector3((-s * dir.x + co * dir.z) / r, co * dir.x + s * dir.z, dir.y);
        }

        /// <summary>a unit vector from an azimuth (rad, atan2(z, x)) and an elevation (rad)</summary>
        public static Vector3 Spherical(float az, float el) => new(Mathf.Cos(el) * Mathf.Cos(az), Mathf.Sin(el), Mathf.Cos(el) * Mathf.Sin(az));

        /// <summary>the in-plane up of a camera at elevation el (rad) on the vertical plane at azimuth az looking at the centre: world up on the ground,
        /// the horizontal view direction (toward the centre) straight above - a crane has no roll</summary>
        public static Vector3 CraneUp(float az, float el) => -Mathf.Sin(el) * new Vector3(Mathf.Cos(az), 0f, Mathf.Sin(az)) + Mathf.Cos(el) * Vector3.up;

        public static Vector3 Bezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }

        /// <summary>LookRotation(f, up) with guards (a degenerate aim returns the fallback)</summary>
        public static Quaternion Aim(Vector3 f, Vector3 up, Quaternion fallback)
        {
            if (f.sqrMagnitude < 1e-8f || up.sqrMagnitude < 1e-8f) return fallback;
            if (Mathf.Abs(Vector3.Dot(f.normalized, up.normalized)) > 0.9995f) return fallback;
            return Quaternion.LookRotation(f, up);
        }

        // ------------------------------------------------------------------ v4 ("hm film flow v2"): C2 ramps of live end states

        /// <summary>the integral of smootherstep from 0 to y: y^4 (2.5 - 3 y + y^2)</summary>
        static float SmootherstepIntegral(float y) => y * y * y * y * (2.5f - 3f * y + y * y);

        /// <summary>
        /// The cruise law (dancecap film_recap_flow.cruise; UNITY_CONTRACT_v4 A): the rate rises by a smootherstep over the first `ease` of x,
        /// holds at 1 / (1 - ease) x the mean, and falls by a smootherstep over the last `ease`. Value, first and second derivative are 0 / 1 / 0
        /// at both ends (C2). ease = 0 is linear; ease is clamped to [0, 0.5].
        /// </summary>
        public static float Cruise(float x, float ease)
        {
            x = Mathf.Clamp01(x);
            float e = Mathf.Clamp(ease, 0f, 0.5f);
            if (e < 1e-5f) return x;
            float s;
            if (x <= e) s = e * SmootherstepIntegral(x / e);
            else if (x <= 1f - e) s = e * 0.5f + (x - e);
            else
            {
                float y = (x - 1f + e) / e;
                s = e * 0.5f + (1f - 2f * e) + e * (y - SmootherstepIntegral(y));
            }

            return s / (1f - e);
        }

        /// <summary>the cruise law over the window [a, b] of u (0 before a, 1 after b)</summary>
        public static float CruiseRamp(float u, float a, float b, float ease) => b - a <= 1e-6f ? (u >= a ? 1f : 0f) : Cruise((u - a) / (b - a), ease);

        /// <summary>the FOV (deg) of the log-tan zoom: tan(fov / 2) = tan(a / 2)^(1 - z) x tan(b / 2)^z (an even zoom speed in the picture)</summary>
        public static float LogTanFov(float a, float b, float z)
        {
            float ta = Mathf.Tan(Mathf.Clamp(a, 1f, 170f) * 0.5f * Mathf.Deg2Rad), tb = Mathf.Tan(Mathf.Clamp(b, 1f, 170f) * 0.5f * Mathf.Deg2Rad);
            float t = Mathf.Exp(Mathf.Lerp(Mathf.Log(ta), Mathf.Log(tb), Mathf.Clamp01(z)));
            return 2f * Mathf.Atan(t) * Mathf.Rad2Deg;
        }

        /// <summary>64 u^3 (1 - u)^3: a bump of height 1 at u = 0.5 whose value, first and second derivative are 0 at both ends (a C2 detour)</summary>
        public static float C2Bump(float u)
        {
            u = Mathf.Clamp01(u);
            float v = u * (1f - u);
            return 64f * v * v * v;
        }

        /// <summary>
        /// A v4 glide (UNITY_CONTRACT_v4 2, "Eye"): the eye between two LIVE anchors in cylindrical coordinates about the couple's centre c -
        /// azimuth a + g x delta (delta = the anchors' difference the plan's way round, FlowMath.Branch), radius and height lerped by g, the
        /// radius never below rMin (unless an anchor is closer), the height inside [yLo, yHi] (unless an anchor is outside). g = 0 / 1 give the
        /// anchors exactly, so with a smootherstep g the eye leaves A and reaches B with their own velocity and acceleration (C2).
        /// </summary>
        public static Vector3 GlideEye(Vector3 c, Vector3 eA, Vector3 eB, float g, float planDelta, float rMin, float yLo, float yHi)
        {
            if (g <= 0f) return eA;
            if (g >= 1f) return eB;
            Vector3 a = ToCyl(eA, c), b = ToCyl(eB, c);
            float delta = Branch(b.x - a.x, planDelta);
            float r = Mathf.Max(Mathf.Lerp(a.y, b.y, g), Mathf.Min(rMin, a.y, b.y));
            float y = Mathf.Clamp(Mathf.Lerp(a.z, b.z, g), Mathf.Min(yLo, a.z, b.z), Mathf.Max(yHi, a.z, b.z));
            return FromCyl(new Vector3(a.x + delta * g, r, y), c);
        }

        /// <summary>a crane's eye about its look point (UNITY_CONTRACT_v4 2, "Crane"): azimuth, elevation and distance between the two ends by s
        /// (the cruise law), the azimuth the short way</summary>
        public static Vector3 CraneEye(Vector3 look, float az0, float el0, float d0, float az1, float el1, float d1, float s, out float az, out float el)
        {
            az = az0 + WrapPi(az1 - az0) * s;
            el = Mathf.Lerp(el0, el1, s);
            return look + Spherical(az, el) * Mathf.Lerp(d0, d1, s);
        }
    }

    /// <summary>
    /// A composite camera path sampled as a polyline of (eye, rotation) keys, evaluated by a cost-length fraction (metres moved plus 0.25 m per radian
    /// turned): the transition's eased progress k is the fraction of that cost covered, so the speed is continuous across the parts (the arc into a
    /// phone's optical axis and the dolly along it) and a part that only turns still takes time. Inside a part the rotation is eased (zero turn
    /// rate at its ends), so it joins a dolly at a fixed rotation without a jolt. Endpoint keys are exact (the live poses).
    /// </summary>
    public sealed class FlowPath
    {
        public const float TurnCostPerRad = 0.25f;
        readonly List<Vector3> eyes = new();
        readonly List<Quaternion> rots = new();
        readonly List<float> cum = new();

        public int Count => eyes.Count;
        public float Total => cum.Count > 0 ? cum[^1] : 0f;
        public Vector3 EyeAt(int i) => eyes[i];
        public Quaternion RotAt(int i) => rots[i];

        public void Clear()
        {
            eyes.Clear();
            rots.Clear();
            cum.Clear();
        }

        public void Add(Vector3 eye, Quaternion rot)
        {
            if (eyes.Count > 0)
            {
                // keep every rotation on the same quaternion hemisphere as its neighbour (slerp between keys stays the short way)
                if (Quaternion.Dot(rots[^1], rot) < 0f) rot = new Quaternion(-rot.x, -rot.y, -rot.z, -rot.w);
                float c = Vector3.Distance(eyes[^1], eye) + TurnCostPerRad * Quaternion.Angle(rots[^1], rot) * Mathf.Deg2Rad;
                cum.Add(cum[^1] + c);
            }
            else
            {
                cum.Add(0f);
            }

            eyes.Add(eye);
            rots.Add(rot);
        }

        /// <summary>the pose at cost fraction k (0 = the first key, 1 = the last)</summary>
        public void Sample(float k, out Vector3 eye, out Quaternion rot)
        {
            int n = eyes.Count;
            if (n == 0)
            {
                eye = Vector3.zero;
                rot = Quaternion.identity;
                return;
            }

            if (n == 1 || k <= 0f)
            {
                eye = eyes[0];
                rot = rots[0];
                return;
            }

            if (k >= 1f)
            {
                eye = eyes[n - 1];
                rot = rots[n - 1];
                return;
            }

            float total = cum[n - 1];
            if (total < 1e-6f)
            {
                float x = k * (n - 1);
                int i0 = Mathf.Min(n - 2, Mathf.FloorToInt(x));
                eye = Vector3.Lerp(eyes[i0], eyes[i0 + 1], x - i0);
                rot = Quaternion.Slerp(rots[i0], rots[i0 + 1], x - i0);
                return;
            }

            float target = k * total;
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (cum[mid] <= target) lo = mid;
                else hi = mid;
            }

            float seg = cum[hi] - cum[lo];
            float u = seg > 1e-7f ? (target - cum[lo]) / seg : 0f;
            eye = Vector3.Lerp(eyes[lo], eyes[hi], u);
            rot = Quaternion.Slerp(rots[lo], rots[hi], u);
        }

        // ------------------------------------------------------------------ parts (each appends keys for lambda in (0, 1]; the first part also its start)

        public const int PartSamples = 64;

        void Start(Vector3 eye, Quaternion rot)
        {
            if (eyes.Count == 0) Add(eye, rot);
        }

        /// <summary>a straight dolly from p0 to p1 at a fixed rotation (a phone's optical axis)</summary>
        public void Line(Vector3 p0, Vector3 p1, Quaternion rot, int n = 16)
        {
            Start(p0, rot);
            for (int i = 1; i <= n; i++) Add(Vector3.Lerp(p0, p1, i / (float)n), rot);
        }

        /// <summary>rotation from r0 to r1 with the aim pulled toward the look point mid-way (FilmCamera.Fly's bump: 0 at both ends)</summary>
        public static Quaternion Turn(Quaternion r0, Quaternion r1, float lambda, Vector3 eye, Vector3 look, Vector3 up, bool locked = false)
        {
            Quaternion q = Quaternion.Slerp(r0, r1, FlowMath.Smootherstep(lambda));
            float bump = Mathf.Sin(Mathf.PI * Mathf.Clamp01(lambda));
            if (bump < 1e-4f) return q;
            Quaternion aim = FlowMath.Aim(look - eye, up, q);
            // locked (a crane): the aim takes over within the first and last tenth (the eye's elevation and the slerped pitch do not keep pace:
            // the couple left the frame at the top while the camera tilted to the floor)
            float w = locked ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(3f * bump)) : 0.8f * bump * bump;
            return Quaternion.Slerp(q, aim, w);
        }

        /// <summary>
        /// A cubic Bezier in cylindrical coordinates about the couple's centre c from p0 to p3: the azimuth the plan's way round (FlowMath.Branch),
        /// the radius never below rMin, the height inside [yLo, yHi]. t0 / t3 (unit Cartesian directions, optional) make the curve leave p0 / reach
        /// p3 along them (a phone's optical axis: G1 with the dolly); without one the handle is the straight cylindrical interpolation.
        /// </summary>
        public void Cyl(Vector3 c, Vector3 p0, Vector3? t0, Vector3 p3, Vector3? t3, float planDelta, float rMin, float yLo, float yHi,
            Quaternion r0, Quaternion r1, Vector3 look0, Vector3 look1, Vector3 up0, Vector3 up1, int n = PartSamples, float bulge = 0f, float lift = 0f)
        {
            Start(p0, r0);
            // a detour (bulge or lift: round or over a phone's picture) keeps the aim locked on the couple, as a crane does: with the gentle bump
            // the couple slid to the frame's edge while the eye swung sideways
            bool locked = Mathf.Abs(bulge) > 1e-4f || lift > 1e-4f;
            for (int i = 1; i <= n; i++)
            {
                float l = i / (float)n;
                Vector3 eye = i == n ? p3 : CylPoint(c, p0, t0, p3, t3, planDelta, rMin, yLo, yHi, bulge, lift, l);
                Vector3 up = Vector3.Slerp(up0, up1, l);
                Quaternion rot = i == n ? r1 : Turn(r0, r1, l, eye, Vector3.Lerp(look0, look1, l), up, locked);
                Add(eye, rot);
            }
        }

        /// <summary>
        /// A point of the cylindrical Bezier of Cyl at lambda. bulge (rad) adds bulge x sin^2(pi lambda) to the azimuth and lift (m) adds
        /// lift x sin^2(pi lambda) to the height: the curve swings round the side of a phone's frustum surface or over it instead of through it,
        /// and loops instead of doubling back on itself; sin^2 keeps the tangents at both ends (G1 with the dolly along the axis).
        /// </summary>
        public static Vector3 CylPoint(Vector3 c, Vector3 p0, Vector3? t0, Vector3 p3, Vector3? t3, float planDelta, float rMin, float yLo, float yHi,
            float bulge, float lift, float l)
        {
            Vector3 a = FlowMath.ToCyl(p0, c), b = FlowMath.ToCyl(p3, c);
            float delta = FlowMath.Branch(b.x - a.x, planDelta);
            Vector3 P0 = a, P3 = new(a.x + delta, b.y, b.z);
            float chord = Vector3.Distance(p0, p3);
            Vector3 dir = p3 - p0;
            // a handle that must turn the curve round (the 3D end beside or in front of the phone: leaving / reaching its optical axis means a
            // U-turn) grows with that turn, so the U-turn is a round loop instead of a hairpin at the axis
            float Handle(Vector3 t, bool atEnd)
            {
                float phi = Vector3.Angle(atEnd ? t : t, dir);
                float k = Mathf.InverseLerp(100f, 170f, atEnd ? phi : phi);
                return Mathf.Clamp(chord * Mathf.Lerp(0.35f, 1.0f, k), 0.25f, 2.5f);
            }

            Vector3 P1 = t0.HasValue ? P0 + FlowMath.CylTangent(p0, c, t0.Value) * Handle(t0.Value, false) : P0 + (P3 - P0) / 3f;
            Vector3 P2 = t3.HasValue ? P3 - FlowMath.CylTangent(p3, c, t3.Value) * Handle(t3.Value, true) : P3 - (P3 - P0) / 3f;
            Vector3 q = FlowMath.Bezier(P0, P1, P2, P3, l);
            float s = Mathf.Sin(Mathf.PI * l);
            q.x += bulge * s * s;
            q.z += lift * s * s;
            q.y = Mathf.Max(q.y, rMin);
            q.z = Mathf.Clamp(q.z, yLo, yHi);
            return FlowMath.FromCyl(q, c);
        }

        /// <summary>the segment a-b passes through the rectangle (centre, unit right / up / normal, half sizes incl. a margin)</summary>
        public static bool Crosses(Vector3 a, Vector3 b, Vector3 centre, Vector3 right, Vector3 up, Vector3 normal, float hw, float hh)
        {
            float sa = Vector3.Dot(a - centre, normal), sb = Vector3.Dot(b - centre, normal);
            if (sa * sb > 0f || Mathf.Abs(sa - sb) < 1e-7f) return false;
            Vector3 x = Vector3.Lerp(a, b, sa / (sa - sb)) - centre;
            return Mathf.Abs(Vector3.Dot(x, right)) <= hw && Mathf.Abs(Vector3.Dot(x, up)) <= hh;
        }

        /// <summary>
        /// The plan (bulge rad, lift m) of the least badness for the cylindrical part: crossing a frustum surface (rectangles: centre, right, up,
        /// normal, half width, half height) costs 1000, a hairpin (more than 30 degrees between two of 48 samples) 50 per 10 degrees over, a
        /// stall (a sample step under a fifth of the mean: the curve stops and turns round) 20, plus the effort (lift + bulge as metres round
        /// the couple). Lifts 0 - 1.5 m, bulges 0 - 100 deg either way round. (0, 0) when nothing is needed.
        /// </summary>
        public static Vector2 AvoidPlan(Vector3 c, Vector3 p0, Vector3? t0, Vector3 p3, Vector3? t3, float planDelta, float rMin, float yLo, float yHi,
            IList<(Vector3 centre, Vector3 right, Vector3 up, Vector3 normal, float hw, float hh)> rects)
        {
            float rMid = 0.5f * (FlowMath.ToCyl(p0, c).y + FlowMath.ToCyl(p3, c).y);
            float best = float.PositiveInfinity;
            Vector2 plan = Vector2.zero;
            float[] lifts = { 0f, 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f };
            float[] bulges = { 0f, 10f, -10f, 20f, -20f, 30f, -30f, 45f, -45f, 60f, -60f, 80f, -80f, 100f, -100f };
            foreach (float lift in lifts)
            {
                foreach (float bd in bulges)
                {
                    float bulge = bd * Mathf.Deg2Rad;
                    float effort = lift + Mathf.Abs(bulge) * Mathf.Max(1f, rMid);
                    if (effort >= best) continue;
                    float score = effort + CylBadness(c, p0, t0, p3, t3, planDelta, rMin, yLo, yHi, bulge, lift, rects);
                    if (score >= best) continue;
                    best = score;
                    plan = new Vector2(bulge, lift);
                }
            }

            return plan;
        }

        /// <summary>one geometry of a way's cylindrical part (the live poses at one instant): AvoidPlan(IList) sums the badness over several</summary>
        public sealed class CylGeo
        {
            public Vector3 C, P0, P3;
            public Vector3? T0, T3;
            public float PlanDelta, RMin, YLo, YHi;
            public readonly List<(Vector3 centre, Vector3 right, Vector3 up, Vector3 normal, float hw, float hh)> Rects = new();
        }

        /// <summary>AvoidPlan over the geometries of a way at several instants (its start, middle and end: the live ends move during it): the plan
        /// of the least summed badness + effort</summary>
        public static Vector2 AvoidPlan(IList<CylGeo> geos, out float badness)
        {
            badness = 0f;
            if (geos == null || geos.Count == 0) return Vector2.zero;
            float rMid = 0f;
            foreach (CylGeo g in geos) rMid += 0.5f * (FlowMath.ToCyl(g.P0, g.C).y + FlowMath.ToCyl(g.P3, g.C).y) / geos.Count;
            float best = float.PositiveInfinity;
            Vector2 plan = Vector2.zero;
            float[] lifts = { 0f, 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f };
            float[] bulges = { 0f, 10f, -10f, 20f, -20f, 30f, -30f, 45f, -45f, 60f, -60f, 80f, -80f, 100f, -100f };
            foreach (float lift in lifts)
            {
                foreach (float bd in bulges)
                {
                    float bulge = bd * Mathf.Deg2Rad;
                    float score = lift + Mathf.Abs(bulge) * Mathf.Max(1f, rMid);
                    foreach (CylGeo g in geos)
                    {
                        if (score >= best) break;
                        score += CylBadness(g.C, g.P0, g.T0, g.P3, g.T3, g.PlanDelta, g.RMin, g.YLo, g.YHi, bulge, lift, g.Rects);
                    }

                    if (score >= best) continue;
                    best = score;
                    plan = new Vector2(bulge, lift);
                }
            }

            badness = best;
            return plan;
        }

        /// <summary>the badness of a cylindrical part (AvoidPlan): crossings, hairpins, stalls (0 = a clean curve)</summary>
        public static float CylBadness(Vector3 c, Vector3 p0, Vector3? t0, Vector3 p3, Vector3? t3, float planDelta, float rMin, float yLo, float yHi, float bulge, float lift,
            IList<(Vector3 centre, Vector3 right, Vector3 up, Vector3 normal, float hw, float hh)> rects)
        {
            const int n = 48;
            Vector3 prev = p0, prevStep = Vector3.zero;
            float total = 0f, min = float.PositiveInfinity, maxTurn = 0f, bad = 0f;
            for (int i = 1; i <= n; i++)
            {
                Vector3 p = CylPoint(c, p0, t0, p3, t3, planDelta, rMin, yLo, yHi, bulge, lift, i / (float)n);
                if (rects != null && bad < 1000f)
                {
                    foreach ((Vector3 centre, Vector3 right, Vector3 up, Vector3 normal, float hw, float hh) r in rects)
                    {
                        if (Crosses(prev, p, r.centre, r.right, r.up, r.normal, r.hw, r.hh))
                        {
                            bad = 1000f;
                            break;
                        }
                    }
                }

                Vector3 step = p - prev;
                float d = step.magnitude;
                total += d;
                if (i > 1 && step.sqrMagnitude > 1e-8f && prevStep.sqrMagnitude > 1e-8f) maxTurn = Mathf.Max(maxTurn, Vector3.Angle(step, prevStep));
                prevStep = step;
                // the ends of a free (non-tangent) end move slowly by design; only the inner samples are tested
                if (i > 2 && i < n - 1) min = Mathf.Min(min, d);
                prev = p;
            }

            if (maxTurn > 30f) bad += 5f * (maxTurn - 30f);
            if (total > 0.3f && min < 0.2f * total / n) bad += 20f;
            return bad;
        }

        /// <summary>a crane: a vertical arc at azimuth az (rad) about a look point moving from l0 to l1, elevation el0 -> el1 (rad), distance
        /// d0 -> d1; the rotation from r0 to r1 aimed at the look point with the in-plane up (no roll)</summary>
        public void Crane(Vector3 l0, Vector3 l1, float az, float el0, float el1, float d0, float d1, Quaternion r0, Quaternion r1, int n = PartSamples) =>
            Crane(l0, l1, az, az, el0, el1, d0, d1, r0, r1, n);

        /// <summary>a crane between two poses given as (look point, azimuth, elevation, distance) about their look points: exact at both ends</summary>
        public void CraneBetween(Vector3 eye0, Vector3 look0, Quaternion r0, Vector3 eye1, Vector3 look1, Quaternion r1, float planDelta, int n = PartSamples)
        {
            Vector3 v0 = eye0 - look0, v1 = eye1 - look1;
            float d0 = Mathf.Max(1e-3f, v0.magnitude), d1 = Mathf.Max(1e-3f, v1.magnitude);
            float el0 = Mathf.Asin(Mathf.Clamp(v0.y / d0, -1f, 1f)), el1 = Mathf.Asin(Mathf.Clamp(v1.y / d1, -1f, 1f));
            float az0 = Mathf.Atan2(v0.z, v0.x), az1 = Mathf.Atan2(v1.z, v1.x);
            az1 = az0 + FlowMath.Branch(az1 - az0, planDelta);
            Crane(look0, look1, az0, az1, el0, el1, d0, d1, r0, r1, n);
        }

        /// <summary>a crane whose azimuth turns from az0 to az1 near the ground end (the first half going up, the second half coming down)</summary>
        public void Crane(Vector3 l0, Vector3 l1, float az0, float az1, float el0, float el1, float d0, float d1, Quaternion r0, Quaternion r1, int n = PartSamples)
        {
            Vector3 e0 = l0 + FlowMath.Spherical(az0, el0) * d0;
            Start(e0, r0);
            bool up = el1 > el0; // up: the elevation starts with zero slope (the ground end); down: it ends with zero slope
            for (int i = 1; i <= n; i++)
            {
                float l = i / (float)n;
                float az = Mathf.Lerp(az0, az1, up ? Mathf.SmoothStep(0f, 1f, l * 2f) : Mathf.SmoothStep(0f, 1f, l * 2f - 1f));
                float el = Mathf.Lerp(el0, el1, up ? l * l : 1f - (1f - l) * (1f - l));
                Vector3 look = Vector3.Lerp(l0, l1, l);
                Vector3 eye = look + FlowMath.Spherical(az, el) * Mathf.Lerp(d0, d1, l);
                Quaternion rot = i == n ? r1 : Turn(r0, r1, l, eye, look, FlowMath.CraneUp(az, el), true);
                Add(eye, rot);
            }
        }
    }
}
