using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace HmFilm.Tests
{
    /// <summary>edit-mode tests of the v4 flow ("hm film flow v2"): the cruise law and the smootherstep ramps are C2 at their window edges, the
    /// log-tan zoom, a glide from a LIVE turning orbit keeps the orbit's velocity and acceleration at u = 0 and reaches the moving phone anchor
    /// with its own at u = 1, the profile parsing, the faint frustums' opacity (0.15 in the 3D views, 0 over a phone's full-frame video), the
    /// outline curve of the phone entered / left (from the ghost's 0.15 up to 1 and back, never below the ghost), and - when the workspace has it
    /// - the v4 direction itself</summary>
    public class FlowV4Tests
    {
        const float Eps = 1e-4f;

        static float D1(System.Func<float, float> f, float x, float h = 1e-3f) => (f(x + h) - f(x - h)) / (2f * h);
        static float D2(System.Func<float, float> f, float x, float h = 1e-2f) => (f(x + h) - 2f * f(x) + f(x - h)) / (h * h);

        [Test]
        public void CruiseIsC2AndHoldsItsRate()
        {
            const float e = 0.25f;
            Assert.AreEqual(0f, FlowMath.Cruise(0f, e), Eps);
            Assert.AreEqual(1f, FlowMath.Cruise(1f, e), Eps);
            Assert.AreEqual(0.5f, FlowMath.Cruise(0.5f, e), 1e-4f);
            // C2 at the ends: first and second derivative 0 (one-sided: the law is clamped outside [0, 1])
            Assert.Less(FlowMath.Cruise(0.01f, e), 1e-5f);
            Assert.Greater(FlowMath.Cruise(0.99f, e), 1f - 1e-5f);
            // the cruise: the rate holds at 1 / (1 - e) between e and 1 - e
            Assert.AreEqual(1f / (1f - e), D1(x => FlowMath.Cruise(x, e), 0.5f), 1e-3f);
            Assert.AreEqual(1f / (1f - e), D1(x => FlowMath.Cruise(x, e), 0.3f), 1e-3f);
            // continuous rate and acceleration across the joins at e and 1 - e
            Assert.AreEqual(D1(x => FlowMath.Cruise(x, e), e - 2e-3f), D1(x => FlowMath.Cruise(x, e), e + 2e-3f), 0.02f);
            Assert.AreEqual(0f, D2(x => FlowMath.Cruise(x, e), e, 2e-3f), 0.5f);
            float prev = 0f;
            for (int i = 1; i <= 200; i++)
            {
                float v = FlowMath.Cruise(i / 200f, e);
                Assert.GreaterOrEqual(v, prev - 1e-6f);
                prev = v;
            }

            // the python law's numbers (dancecap film_recap_flow.cruise): with 5.1-s cranes and 74.3 deg the pitch peaks at 19.4 deg/s
            Assert.AreEqual(19.42f, 74.3f / (1f - e) / 5.1f, 0.01f);
            Assert.AreEqual(0.5f, FlowMath.CruiseRamp(0.75f, 0.5f, 1.0f, 0f), Eps, "ease 0 is linear over its window");
        }

        [Test]
        public void RampsAreC2AtTheirWindowEdges()
        {
            FlowWin w = FlowWin.Of(0.25f, 0.75f);
            Assert.AreEqual(0f, w.At(0.25f), Eps);
            Assert.AreEqual(1f, w.At(0.75f), Eps);
            Assert.AreEqual(0f, w.At(0.1f), Eps);
            Assert.AreEqual(1f, w.At(0.9f), Eps);
            // value, slope and curvature 0 at the edges: the ramp leaves its window edge with third-order contact, w(a + d) ~ 10 (d / (b - a))^3,
            // so w / d^2 -> 0 (a C1-only ramp would leave it at a finite w / d^2)
            foreach (float d in new[] { 1e-2f, 1e-3f })
            {
                Assert.Less(w.At(0.25f + d) / (d * d), 10f * d / 0.125f + 1e-3f);
                Assert.Less((1f - w.At(0.75f - d)) / (d * d), 10f * d / 0.125f + 1e-3f);
            }
        }

        [Test]
        public void LogTanZoomEndsAndMonotonic()
        {
            Assert.AreEqual(42f, FlowMath.LogTanFov(42f, 18f, 0f), 1e-3f);
            Assert.AreEqual(18f, FlowMath.LogTanFov(42f, 18f, 1f), 1e-3f);
            float prev = 43f;
            for (int i = 0; i <= 20; i++)
            {
                float f = FlowMath.LogTanFov(42f, 18f, i / 20f);
                Assert.Less(f, prev + 1e-4f);
                prev = f;
            }

            // an even zoom speed in the picture: log tan(fov / 2) is linear in z
            float l0 = Mathf.Log(Mathf.Tan(21f * Mathf.Deg2Rad)), l1 = Mathf.Log(Mathf.Tan(9f * Mathf.Deg2Rad));
            Assert.AreEqual(0.5f * (l0 + l1), Mathf.Log(Mathf.Tan(FlowMath.LogTanFov(42f, 18f, 0.5f) * 0.5f * Mathf.Deg2Rad)), 1e-4f);
        }

        // a live orbit (4 deg/s about the couple) into a phone that sways slightly: the phone_in profile glide [0, 0.75], dolly [0.5, 1]
        // (the first way of the v4.1 plan: from the orbit at -79 deg, 4.3 m, 2.15 m high at its start, 5.166 s, into phone 00 at -92 deg)
        static Vector3 Orbit(float t) => new(4.3f * Mathf.Cos((-79f + 4f * (t - 5.166f)) * Mathf.Deg2Rad), 2.15f, 4.3f * Mathf.Sin((-79f + 4f * (t - 5.166f)) * Mathf.Deg2Rad));
        static Vector3 Phone(float t) => new(-0.17f + 0.02f * Mathf.Sin(1.3f * t), 1.69f + 0.01f * Mathf.Sin(2.1f * t), -4.53f);
        static readonly Vector3 PhoneFwd = new Vector3(0.17f, -0.69f, 4.53f).normalized;

        static Vector3 WayEye(float t, float t0, float T)
        {
            float u = Mathf.Clamp01((t - t0) / T);
            float g = FlowMath.Ramp(u, 0f, 0.75f), d = 1f - FlowMath.Ramp(u, 0.5f, 1f);
            Vector3 portal = Phone(t) - PhoneFwd * d;
            if (u <= 0f) return Orbit(t);
            return FlowMath.GlideEye(Vector3.zero, Orbit(t), portal, g, -13.2f * Mathf.Deg2Rad, 1.4f, 0.5f, 2.4f);
        }

        [Test]
        public void GlideKeepsTheOrbitsVelocityAndAccelerationAtTheStart()
        {
            const float t0 = 5.166f, T = 4.5f, h = 0.01f;
            // velocity and acceleration of the way just after its start equal the orbit's (C2: the ramps start with zero slope and curvature)
            // central differences ACROSS the way's start (the orbit's own pose before it): the C2 join
            Vector3 vWay = (WayEye(t0 + h, t0, T) - WayEye(t0 - h, t0, T)) / (2f * h), vOrb = (Orbit(t0 + h) - Orbit(t0 - h)) / (2f * h);
            Assert.Less((vWay - vOrb).magnitude, 0.003f, $"velocity {vWay} vs the orbit's {vOrb}");
            Vector3 aWay = (WayEye(t0 + h, t0, T) - 2f * WayEye(t0, t0, T) + WayEye(t0 - h, t0, T)) / (h * h);
            Vector3 aOrb = (Orbit(t0 + h) - 2f * Orbit(t0) + Orbit(t0 - h)) / (h * h);
            Assert.Less((aWay - aOrb).magnitude, 0.02f, $"acceleration {aWay} vs the orbit's {aOrb}");
            // never a dead stop across the start: the eye keeps moving at the orbit's speed
            Assert.Greater(vWay.magnitude, 0.25f);
            // exact ends: the orbit at u = 0, the phone itself at u = 1 (the dolly brought it from the portal)
            Assert.Less((WayEye(t0, t0, T) - Orbit(t0)).magnitude, 1e-4f);
            Assert.Less((WayEye(t0 + T, t0, T) - Phone(t0 + T)).magnitude, 1e-4f);
            // and the phone's own (handheld) velocity at the arrival
            Vector3 vEnd = (WayEye(t0 + T, t0, T) - WayEye(t0 + T - 2f * h, t0, T)) / (2f * h), vPh = (Phone(t0 + T) - Phone(t0 + T - 2f * h)) / (2f * h);
            Assert.Less((vEnd - vPh).magnitude, 0.01f, $"arrival velocity {vEnd} vs the phone's {vPh}");
            // the speed cap of the contract (1.2 m/s) holds on this 4.5-s way
            float peak = 0f;
            const float fr = 1f / 30f;
            for (int i = 1; i <= 135; i++) peak = Mathf.Max(peak, (WayEye(t0 + i * fr, t0, T) - WayEye(t0 + (i - 1) * fr, t0, T)).magnitude / fr);
            Assert.Less(peak, 1.2f);
        }

        [Test]
        public void CraneEyeIsExactAtItsEndsWithoutRoll()
        {
            Vector3 look = new(0.2f, 0.95f, -0.1f);
            Vector3 e0 = FlowMath.CraneEye(look, 1.0f, 0.4f, 4.3f, 1.05f, 89.9f * Mathf.Deg2Rad, 7.0f, 0f, out float az, out float el);
            Assert.Less((e0 - (look + FlowMath.Spherical(1.0f, 0.4f) * 4.3f)).magnitude, 1e-4f);
            Vector3 e1 = FlowMath.CraneEye(look, 1.0f, 0.4f, 4.3f, 1.05f, 89.9f * Mathf.Deg2Rad, 7.0f, 1f, out az, out el);
            Assert.Less((e1 - (look + FlowMath.Spherical(1.05f, 89.9f * Mathf.Deg2Rad) * 7.0f)).magnitude, 1e-4f);
            // the in-plane up has no roll: it lies in the vertical plane through the eye and the look point, at every s
            for (int i = 0; i <= 10; i++)
            {
                Vector3 e = FlowMath.CraneEye(look, 1.0f, 0.4f, 4.3f, 1.05f, 1.4f, 7.0f, FlowMath.Cruise(i / 10f, 0.25f), out az, out el);
                Vector3 up = FlowMath.CraneUp(az, el), f = (look - e).normalized;
                Assert.AreEqual(0f, Vector3.Dot(up, f), 1e-4f);
                Assert.AreEqual(0f, Vector3.Dot(up, Vector3.Cross(f, Vector3.up).normalized), 1e-4f);
            }
        }

        static JObject Seg(string id, float f0, float f1, JObject cam) => new()
        {
            ["id"] = id, ["film"] = new JArray(f0, f1), ["dance"] = new JArray(f0, f1), ["camera"] = cam
        };

        static JObject Profile(params (string, float, float)[] w)
        {
            JObject o = new();
            foreach ((string k, float a, float b) in w) o[k] = new JArray(a, b);
            return o;
        }

        /// <summary>orbit 0-10 | phone 01 10-30 (way in 5.5-10, way out 30-34.5) | orbit 30-40 | overhead 40-46 (crane up 35.25-40.35)</summary>
        static JObject SyntheticV4()
        {
            JObject orbit = new() { ["mode"] = "orbit", ["world_azimuth_deg"] = -55, ["drift_deg_s"] = 3, ["v4"] = new JObject { ["role"] = "slow_orbit" } };
            JObject phone = new()
            {
                ["mode"] = "camera_tour", ["cameras"] = new JArray(new JObject { ["camera"] = "01", ["dance_t"] = new JArray(5.5, 34.5) }),
                ["transition_in"] = new JObject
                {
                    ["type"] = "phone_in", ["start_s"] = -4.5, ["duration_s"] = 4.5, ["path"] = "arc_then_axis", ["easing"] = "smootherstep",
                    ["video"] = new JObject { ["full_frame_at_s"] = 0.0, ["splats_hidden_at_s"] = 0.0, ["overlays_on_top_in_s"] = 0.5 },
                    ["v4"] = new JObject
                    {
                        ["path"] = "glide_axis",
                        ["profile"] = Profile(("glide", 0f, 0.75f), ("dolly", 0.5f, 1f), ("rot", 0f, 0.8f), ("zoom", 0.25f, 1f), ("picture_in", 0f, 0.35f), ("outline_in", 0f, 0.25f)),
                        ["fov"] = new JObject { ["law"] = "log_tan", ["u"] = new JArray(0.25, 1.0) }
                    }
                }
            };
            JObject orbit2 = new()
            {
                ["mode"] = "orbit", ["world_azimuth_deg"] = -80, ["drift_deg_s"] = 3,
                ["transition_in"] = new JObject
                {
                    ["type"] = "phone_out", ["start_s"] = 0.0, ["duration_s"] = 4.5, ["path"] = "axis_then_arc",
                    ["video_out"] = new JObject { ["surface_from_s"] = 0.0, ["splats_back_in_s"] = 0.5, ["overlays_on_top_off_before_s"] = 0.35 },
                    ["v4"] = new JObject
                    {
                        ["path"] = "axis_glide",
                        ["profile"] = Profile(("dolly_out", 0f, 0.5f), ("glide", 0.25f, 1f), ("rot", 0.2f, 1f), ("zoom", 0f, 0.75f), ("picture_out", 0.65f, 1f), ("outline_out", 0.75f, 1f))
                    }
                }
            };
            JObject over = new()
            {
                ["mode"] = "overhead_floorcraft", ["world_azimuth_deg"] = -50, ["drift_deg_s"] = 3, ["up_policy"] = "orbit",
                ["transition_in"] = new JObject
                {
                    ["type"] = "crane_up", ["start_s"] = -4.75, ["duration_s"] = 5.1, ["path"] = "crane",
                    ["v4"] = new JObject
                    {
                        ["path"] = "crane",
                        ["profile"] = new JObject { ["elevation"] = new JObject { ["window"] = new JArray(0.0, 1.0), ["law"] = "cruise", ["ease"] = 0.25 } }
                    }
                }
            };
            return new JObject
            {
                ["segments"] = new JArray(Seg("a", 0, 10, orbit), Seg("p", 10, 30, phone), Seg("b", 30, 40, orbit2), Seg("o", 40, 46, over)),
                ["flow"] = new JObject
                {
                    ["version"] = 2, ["revision"] = "4.1", ["defaults"] = new JObject { ["frustum_depth_m"] = 0.9, ["ground_m"] = new JArray(0.5, 2.4) },
                    ["ghost_frustums"] = new JObject
                    {
                        ["enabled"] = true, ["opacity"] = 0.15, ["depth_m"] = 0.6, ["phones"] = new JArray("00", "01", "02", "03", "05", "06"),
                        ["views"] = new JObject { ["3d"] = true, ["ways"] = true, ["overhead"] = true, ["phone_full_frame"] = false }
                    },
                    ["frustums"] = new JArray(
                        new JObject
                        {
                            ["camera"] = "01", ["film"] = new JArray(5.5, 7.075, 10.0, 10.0), ["role"] = "enter", ["shot"] = "p",
                            ["v4"] = new JObject { ["outline"] = new JObject { ["from"] = 0.15, ["to"] = 1.0, ["film"] = new JArray(5.5, 6.625) } }
                        },
                        new JObject
                        {
                            ["camera"] = "01", ["film"] = new JArray(30.0, 30.0, 32.925, 34.5), ["role"] = "leave", ["shot"] = "p",
                            ["v4"] = new JObject { ["outline"] = new JObject { ["from"] = 1.0, ["to"] = 0.15, ["film"] = new JArray(33.375, 34.5) } }
                        })
                }
            };
        }

        [Test]
        public void ProfilesParse()
        {
            FlowTimeline tl = FlowTimeline.Build(SyntheticV4());
            Assert.AreEqual(2, tl.Version);
            Assert.AreEqual("4.1", tl.Revision);
            FlowTransition pin = tl.Shots[1].T, pout = tl.Shots[2].T, crane = tl.Shots[3].T;
            Assert.AreEqual("glide_axis", pin.V4Path);
            Assert.IsTrue(pin.IsV4 && pout.IsV4 && crane.IsV4);
            Assert.AreEqual(0f, pin.W("glide", 0f, -1f), Eps);
            Assert.AreEqual(1f, pin.W("glide", 0.75f, -1f), Eps);
            Assert.AreEqual(0.5f, pin.W("glide", 0.375f, -1f), 1e-4f);
            Assert.AreEqual(-1f, pin.W("dolly_in", 0.5f, -1f), Eps, "a missing window gives the fallback");
            Assert.IsTrue(pin.FovWin.Valid);
            Assert.AreEqual(0f, pin.FovWin.At(0.25f), Eps);
            Assert.AreEqual(0f, pout.W("glide", 0.25f, -1f), Eps);
            Assert.AreEqual(1f, pout.W("dolly_out", 0.5f, -1f), Eps);
            Assert.IsTrue(crane.Profile["elevation"].Cruise);
            Assert.AreEqual(FlowMath.Cruise(0.3f, 0.25f), crane.W("elevation", 0.3f, -1f), Eps);
        }

        [Test]
        public void GhostFrustumsAreFaintInThe3DViewsAndGoneOverThePhoneVideo()
        {
            FlowTimeline tl = FlowTimeline.Build(SyntheticV4());
            Assert.AreEqual(0, tl.Warnings.Count, string.Join("; ", tl.Warnings));
            Assert.IsTrue(tl.GhostEnabled);
            Assert.AreEqual(6, tl.GhostPhones.Count);
            Assert.AreEqual(0.6f, tl.GhostDepthM, Eps);
            Assert.AreEqual(0.15f, tl.GhostAlpha(2f), Eps, "a 3D view");
            Assert.AreEqual(0.15f, tl.GhostAlpha(7f), Eps, "a way into a phone");
            Assert.AreEqual(0f, tl.GhostAlpha(10f), Eps, "the phone's full-frame video (arrival)");
            Assert.AreEqual(0f, tl.GhostAlpha(20f), Eps, "the phone's full-frame video");
            Assert.AreEqual(0.15f, tl.GhostAlpha(30f), Eps, "the departure: the way out is a 3D view again");
            Assert.AreEqual(0.15f, tl.GhostAlpha(43f), Eps, "the overhead");
            // never brighter than 0.15 anywhere
            for (float t = 0f; t < 46f; t += 0.05f) Assert.LessOrEqual(tl.GhostAlpha(t), 0.15f + Eps);
        }

        [Test]
        public void OutlineRisesFromTheGhostAndFallsBackToIt()
        {
            FlowTimeline tl = FlowTimeline.Build(SyntheticV4());
            List<FlowSurface> s = new();
            // the way in: the outline starts at the ghost's 0.15 (no step from the faint frustum), reaches 1 at 6.625 s, the picture by 7.075 s
            tl.Surfaces(5.5f, s);
            FlowSurface f = s.Single(x => x.Camera == "01");
            Assert.AreEqual(0.15f, f.Outline, 1e-3f);
            Assert.AreEqual(0f, f.Alpha, 1e-3f);
            tl.Surfaces(6.7f, s);
            f = s.Single(x => x.Camera == "01");
            Assert.AreEqual(1f, f.Outline, Eps);
            Assert.Greater(f.Alpha, 0.8f);
            tl.Surfaces(9.9f, s);
            Assert.AreEqual(1f, s.Single(x => x.Camera == "01").Alpha, Eps);
            // the way out: outline 1 until 33.375 s, then back down to 0.15 by 34.5 s, never below it while the way runs; the picture gone by 34.5 s
            tl.Surfaces(30f, s);
            f = s.Single(x => x.Camera == "01");
            Assert.AreEqual(1f, f.Alpha, Eps);
            Assert.AreEqual(1f, f.Outline, Eps);
            for (float t = 30f; t < 34.5f; t += 0.02f)
            {
                tl.Surfaces(t, s);
                FlowSurface g = s.Single(x => x.Camera == "01");
                Assert.GreaterOrEqual(g.Outline, 0.15f - Eps, $"outline at {t}");
                Assert.LessOrEqual(g.Outline, 1f + Eps);
            }

            tl.Surfaces(34.49f, s);
            f = s.Single(x => x.Camera == "01");
            Assert.AreEqual(0.15f, f.Outline, 2e-3f);
            Assert.Less(f.Alpha, 0.01f);
            tl.Surfaces(34.5f, s);
            Assert.IsFalse(s.Any(x => x.Camera == "01"), "after the way the faint frustum takes over");
            // the hand-off is unchanged: opaque surface + cover the frame before the arrival and at the departure
            tl.Surfaces(10f - 1f / 30f, s);
            Assert.AreEqual(1f, s.Single(x => x.Camera == "01").Alpha, Eps);
            Assert.AreEqual(1f, tl.VideoCover(10f), Eps);
        }

        // ------------------------------------------------------------------ the real v4 direction (skipped without the workspace's file)

        static string V4Direction
        {
            get
            {
                string env = System.Environment.GetEnvironmentVariable("HM_FILM_DIRECTION_V4");
                if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);
                string work = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "dancecap", "work"));
                if (Directory.Exists(work))
                    foreach (string take in Directory.GetDirectories(work))
                    {
                        string p = Path.Combine(take, "v4", "direction", "direction.json");
                        if (File.Exists(p)) return p;
                    }
                return Path.Combine(work, "<take>", "v4", "direction", "direction.json");
            }
        }

        [Test]
        public void V4DirectionHoldsTheOwnerRules()
        {
            if (!File.Exists(V4Direction)) Assert.Ignore($"no {V4Direction}");
            JObject d = JObject.Parse(File.ReadAllText(V4Direction));
            FlowTimeline tl = FlowTimeline.Build(d);
            Assert.NotNull(tl);
            Assert.GreaterOrEqual(tl.Version, 2);
            Assert.AreEqual(0, tl.Warnings.Count, string.Join("; ", tl.Warnings));
            List<FlowShot> ways = tl.Shots.Where(s => s.T != null).ToList();
            Assert.IsTrue(tl.Shots.Skip(1).All(s => s.T != null), "a shot without transition_in (a jump cut)");
            Assert.IsTrue(ways.All(s => s.T.IsV4), "a way without its v4 profile");
            Assert.LessOrEqual(ways.Count, 22, "camera moves (owner: a third of v3.2's 75 or fewer)");
            // every way at least 4 s; the phone holds at least 20 s
            Assert.IsTrue(ways.All(s => s.T.DurationS >= 4f - 1e-3f), "a way shorter than 4 s");
            Assert.IsTrue(tl.Windows.All(w => w.Departure - w.Arrival >= 20f - 0.05f), "a phone hold under 20 s");
            // the cranes: the cruise law at the overheads, the overheads turn with the orbit
            foreach (FlowShot s in ways.Where(s => s.T.V4Path == "crane"))
            {
                Assert.IsTrue(s.T.Has("elevation") && s.T.Profile["elevation"].Cruise, $"crane into {s.FirstSegmentId} without the cruise law");
            }

            JArray segs = (JArray)d["segments"];
            foreach (FlowShot s in tl.Shots.Where(s => s.Mode == "overhead_floorcraft"))
                Assert.AreEqual("orbit", segs[s.FirstSegment]["camera"]?.Value<string>("up_policy"));
            // the faint frustums: every phone, 0.15, outline only; never over a phone's full-frame video
            Assert.IsTrue(tl.GhostEnabled);
            Assert.AreEqual(0.15f, tl.GhostOpacity, Eps);
            foreach (FlowPhoneWindow w in tl.Windows) Assert.AreEqual(0f, tl.GhostAlpha((w.Arrival + w.Departure) * 0.5f), Eps);
            // each frustum's outline starts / ends at the ghost's opacity (no step from the faint frustum)
            foreach (FlowFrustum f in tl.Frustums)
            {
                Assert.IsTrue(f.HasOutline, $"frustum {f.Camera} {f.Role} without its v4 outline");
                Assert.AreEqual(tl.GhostOpacity, f.Enter ? f.OutlineFrom : f.OutlineTo, 1e-3f);
            }

            // the hand-offs keep v3's rule
            List<FlowSurface> s1 = new();
            foreach (FlowPhoneWindow w in tl.Windows)
            {
                tl.Surfaces(w.Arrival - 1e-3f, s1);
                Assert.IsTrue(s1.Any(x => x.Camera == w.Phone && x.Alpha > 0.999f), $"phone {w.Phone}: no opaque surface before the arrival {w.Arrival}");
                if (w.Shot == tl.Shots.Count - 1) continue;
                tl.Surfaces(w.Departure, s1);
                Assert.IsTrue(s1.Any(x => x.Camera == w.Phone && x.Alpha > 0.999f && x.Top > 0.999f), $"phone {w.Phone}: no opaque surface at the departure {w.Departure}");
            }
        }
    }
}
