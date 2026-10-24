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

        // ------------------------------------------------------------------ v4.2: the physics meter's windows (flow.meter)

        static JObject SyntheticV42()
        {
            JObject d = SyntheticV4();
            d["flow"]["revision"] = "4.2";
            d["flow"]["meter"] = new JObject
            {
                ["schema"] = "hm film meter v1", ["fade_in_s"] = 0.3, ["hold_s"] = 4.0, ["fade_out_s"] = 1.0,
                ["place"] = new JObject { ["vertical"] = new JObject { ["scale"] = 1.65 }, ["horizontal"] = new JObject { ["scale"] = 1.5 } },
                ["windows"] = new JArray(
                    new JObject { ["on_s"] = 12.0, ["start_s"] = 12.0, ["full_s"] = 12.3, ["fade_s"] = 16.0, ["end_s"] = 17.0 },
                    new JObject { ["on_s"] = 36.0, ["start_s"] = 38.0, ["full_s"] = 38.3, ["fade_s"] = 39.5, ["end_s"] = 40.5 })
            };
            return d;
        }

        [Test]
        public void MeterFadesInHoldsAndFadesOut()
        {
            FlowTimeline tl = FlowTimeline.Build(SyntheticV42());
            Assert.AreEqual(0, tl.Warnings.Count, string.Join("; ", tl.Warnings));
            Assert.IsTrue(tl.HasMeter);
            Assert.AreEqual(2, tl.MeterWindows.Count);
            Assert.AreEqual(1.65f, tl.MeterScaleVertical, Eps);
            Assert.AreEqual(1.5f, tl.MeterScaleHorizontal, Eps);
            // hidden before its switch-on, up over 0.3 s (smootherstep: half at the middle), shown to 4 s after its start, gone 1 s later
            Assert.AreEqual(0f, tl.MeterAlpha(11.99f), Eps);
            Assert.AreEqual(0f, tl.MeterAlpha(12f), Eps);
            Assert.AreEqual(0.5f, tl.MeterAlpha(12.15f), 1e-3f);
            Assert.AreEqual(1f, tl.MeterAlpha(12.3f), Eps);
            Assert.AreEqual(1f, tl.MeterAlpha(14f), Eps);
            Assert.AreEqual(1f, tl.MeterAlpha(16f), Eps);
            Assert.AreEqual(0.5f, tl.MeterAlpha(16.5f), 1e-3f);
            Assert.AreEqual(0f, tl.MeterAlpha(17f), Eps);
            Assert.AreEqual(0f, tl.MeterAlpha(25f), Eps, "between the windows (the physics colours may still be on) the meter is gone");
            // smooth: no step anywhere (the steepest smootherstep step over the 0.3-s fade in), within 0..1, and flat at the fades' ends (C2 edges)
            float prev = 0f, maxStep = 0f;
            for (float t = 11.9f; t < 17.1f; t += 1f / 300f)
            {
                float a = tl.MeterAlpha(t);
                Assert.GreaterOrEqual(a, -Eps);
                Assert.LessOrEqual(a, 1f + Eps);
                maxStep = Mathf.Max(maxStep, Mathf.Abs(a - prev));
                prev = a;
            }

            Assert.LessOrEqual(maxStep, 1.875f / (0.3f * 300f) + 1e-3f);
            Assert.Less(tl.MeterAlpha(12.01f), 1e-3f);
            Assert.Greater(tl.MeterAlpha(16.99f), 0f);
            Assert.Less(tl.MeterAlpha(16.99f), 1e-3f);
            // a window cut short (its physics view ended early) still fades out, from fade_s to end_s
            Assert.AreEqual(0.5f, tl.MeterAlpha(40f), 1e-3f);
            // without flow.meter (v4.1 and older) the legend follows the physics layer alone: alpha 1
            FlowTimeline old = FlowTimeline.Build(SyntheticV4());
            Assert.IsFalse(old.HasMeter);
            Assert.AreEqual(1f, old.MeterAlpha(25f), Eps);
            // a broken window is reported, not played
            JObject bad = SyntheticV42();
            ((JArray)bad["flow"]["meter"]["windows"]).Add(new JObject { ["start_s"] = 50.0, ["end_s"] = 49.0 });
            FlowTimeline tb = FlowTimeline.Build(bad);
            Assert.AreEqual(2, tb.MeterWindows.Count);
            Assert.AreEqual(1, tb.Warnings.Count);
        }

        [Test]
        public void CloserOrbitEasesFromTheV41DistanceAtAPhoneWay()
        {
            // an orbit between a phone way out (ends 34.75 s) and a phone way in (starts 99.5 s), 0.8 x closer with 6-s eases
            Vector2 ein = new(34.75f, 40.75f), eout = new(93.5f, 99.5f);
            float F(float t) => FlowMath.CloserFactor(0.8f, t, ein, eout);
            Assert.AreEqual(1.25f, F(30f), Eps, "inside the way out: v4.1's distance (the way is v4.1's)");
            Assert.AreEqual(1.25f, F(34.75f), Eps);
            Assert.AreEqual(1.125f, F(37.75f), 1e-4f, "half way through the ease");
            Assert.AreEqual(1f, F(40.75f), Eps);
            Assert.AreEqual(1f, F(60f), Eps, "the settled orbit: 0.8 x v4.1's offset");
            Assert.AreEqual(1f, F(93.5f), Eps);
            Assert.AreEqual(1.25f, F(99.5f), Eps, "at the way in: v4.1's distance again");
            // C2 at the ease's edges: no slope, no curvature where it leaves the way's distance and where it reaches the closeness
            Assert.Less(Mathf.Abs(D1(F, 34.8f, 1e-3f)), 1e-3f);
            Assert.Less(Mathf.Abs(D2(F, 34.85f, 5e-3f)), 2e-2f);
            Assert.Less(Mathf.Abs(D1(F, 40.7f, 1e-3f)), 1e-3f);
            // the dolly is slow: a 0.89-m change of the eye's distance (9:16: 4.46 -> 3.57 m) over 6 s peaks at 1.875 x 0.89 / 6 = 0.28 m/s
            float peak = 0f;
            for (float t = 34.75f; t <= 40.75f; t += 0.01f) peak = Mathf.Max(peak, Mathf.Abs(D1(F, t, 1e-3f)));
            Assert.AreEqual(1.875f * 0.25f / 6f, peak, 2e-3f);
            Assert.Less(peak * 3.571f, 0.3f);
            // not a closer block / no eases
            Assert.AreEqual(1f, FlowMath.CloserFactor(1f, 30f, ein, eout), Eps);
            Assert.AreEqual(1f, FlowMath.CloserFactor(0.8f, 30f, null, null), Eps);
            // the opening orbit (0 - 5.17 s, then the way into the first phone): closer at the start, v4.1's distance at its way
            Assert.AreEqual(1f, FlowMath.CloserFactor(0.8f, 0f, null, new Vector2(0f, 5.17f)), Eps);
            Assert.AreEqual(1.25f, FlowMath.CloserFactor(0.8f, 5.17f, null, new Vector2(0f, 5.17f)), Eps);
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

            // v4.3 = v4.2 + the planned azimuth tracks (UNITY_CONTRACT_v4 K): every v4.2 rule below holds for it too
            if (tl.Revision != "4.2" && tl.Revision != "4.3") return;
            // v4.2: the physics meter - one window per switch-on of the physics view in the layer timeline (none dropped here), each starting at or
            // after its switch-on, 0.3 s up, shown to 4 s after its start, 1 s down; never over a chapter card; gone between the windows
            Assert.IsTrue(tl.HasMeter);
            List<(float start, bool on)> layer = new();
            for (int i = 0; i < segs.Count; i++)
            {
                JObject L = segs[i]["layers"] as JObject;
                bool on = L?.Value<string>("skeleton_mode") == "physics" || (L?.Value<bool?>("physics") ?? false);
                layer.Add((tl.LayerStart(i), on));
            }

            layer = layer.OrderBy(x => x.start).ToList();
            List<float> switchOns = new();
            for (int i = 0; i < layer.Count; i++)
                if (layer[i].on && (i == 0 || !layer[i - 1].on)) switchOns.Add(layer[i].start);
            Assert.AreEqual(switchOns.Count, tl.MeterWindows.Count, $"switch-ons {string.Join(", ", switchOns)}");
            List<(float a, float b)> cards = new();
            foreach (JObject s in segs.Cast<JObject>())
            {
                if (s["chapter_card"] is not JObject cc) continue;
                float f0 = s["film"][0].Value<float>(), f1 = s["film"][1].Value<float>();
                cards.Add((f0 + (cc.Value<float?>("film_in") ?? 0.3f), Mathf.Min(f1, f0 + (cc.Value<float?>("film_out") ?? 2.9f))));
            }

            for (int k = 0; k < tl.MeterWindows.Count; k++)
            {
                FlowMeterWindow m = tl.MeterWindows[k];
                Assert.AreEqual(switchOns[k], m.On, 0.01f);
                Assert.GreaterOrEqual(m.Start, m.On - 1e-3f);
                Assert.AreEqual(0.3f, m.Full - m.Start, 2e-3f);
                Assert.AreEqual(4f, m.Fade - m.Start, 2e-3f);
                Assert.AreEqual(1f, m.End - m.Fade, 2e-3f);
                Assert.IsFalse(cards.Any(c => m.Start < c.b && m.End > c.a), $"meter window {m.Start}-{m.End} over a chapter card");
                Assert.AreEqual(0f, tl.MeterAlpha(m.End + 0.5f), Eps);
            }

            // the slow orbits 0.8 x v4.1's distance about the couple (both aspects), the lens chosen for the v4.1 distance
            foreach (JObject s in segs.Cast<JObject>())
            {
                JObject cam = (JObject)s["camera"];
                if (cam["v4"]?.Value<string>("role") != "slow_orbit") continue;
                JObject v41 = cam["v4"]?["closer"]?["v41"] as JObject;
                Assert.NotNull(v41, $"{s["id"]}: no v4.1 record");
                Assert.AreEqual(0.8f * v41.Value<float>("radius_m"), cam.Value<float>("radius_m"), 2e-3f);
                Assert.AreEqual(0.8f * v41.Value<float>("radius_vertical_m"), cam.Value<float>("radius_vertical_m"), 2e-3f);
                Assert.AreEqual(1.25f, cam.Value<float>("lens_distance_scale"), 1e-3f);
                Assert.GreaterOrEqual(cam.Value<float>("radius_m"), 1.4f);
            }

            // ... and at every phone way the orbit is back at v4.1's distance (the ease ends where the way starts / starts where the way ends)
            Vector2? Win(JToken t) => t is JArray a && a.Count == 2 ? new Vector2(a[0].Value<float>(), a[1].Value<float>()) : null;
            foreach (FlowShot sh in tl.Shots)
            {
                JObject cam = (JObject)segs[sh.FirstSegment]["camera"];
                if (cam["v4"]?.Value<string>("role") != "slow_orbit") continue;
                Vector2? ein = Win(cam["closer_in_s"]), eout = Win(cam["closer_out_s"]);
                if (sh.T != null && sh.T.Type == "phone_out")
                {
                    Assert.IsTrue(ein.HasValue, $"{sh.FirstSegmentId}: no ease after the phone way out");
                    Assert.AreEqual(sh.TransEnd, ein.Value.x, 0.01f);
                    Assert.AreEqual(1.25f, FlowMath.CloserFactor(0.8f, sh.TransEnd, ein, eout), 1e-3f);
                }

                FlowShot next = sh.Index + 1 < tl.Shots.Count ? tl.Shots[sh.Index + 1] : null;
                if (next?.T != null && next.T.Type == "phone_in")
                {
                    Assert.IsTrue(eout.HasValue, $"{sh.FirstSegmentId}: no ease before the phone way in");
                    Assert.AreEqual(next.TransStart, eout.Value.y, 0.01f);
                    Assert.AreEqual(1.25f, FlowMath.CloserFactor(0.8f, next.TransStart, ein, eout), 1e-3f);
                }
            }
        }

        // ------------------------------------------------------------------ v4.3: the planned azimuth tracks (flow.azimuth_tracks, UNITY_CONTRACT_v4 K / L)

        [Test]
        public void TrackAtIsLinearBetweenItsSamplesAndClampedOutside()
        {
            float[] v = { 0f, 1f, 4f, 9f };
            const float t0 = 10f, dt = 0.5f;
            for (int i = 0; i < v.Length; i++) Assert.AreEqual(v[i], FlowMath.TrackAt(t0, dt, v, t0 + i * dt), 1e-5f, $"sample {i}");
            Assert.AreEqual(0.5f, FlowMath.TrackAt(t0, dt, v, 10.25f), 1e-5f, "a midpoint");
            Assert.AreEqual(2.5f, FlowMath.TrackAt(t0, dt, v, 10.75f), 1e-5f);
            Assert.AreEqual(6.5f, FlowMath.TrackAt(t0, dt, v, 11.25f), 1e-5f);
            Assert.AreEqual(1f + 3f * 0.3f, FlowMath.TrackAt(t0, dt, v, 10.65f), 1e-4f, "linear inside a step");
            Assert.AreEqual(0f, FlowMath.TrackAt(t0, dt, v, 9f), Eps, "before the first sample: the first");
            Assert.AreEqual(9f, FlowMath.TrackAt(t0, dt, v, 11.5f), Eps, "the last sample");
            Assert.AreEqual(9f, FlowMath.TrackAt(t0, dt, v, 12f), Eps, "after the last sample: the last");
            Assert.AreEqual(7f, FlowMath.TrackAt(0f, 1f, new[] { 7f }, 3f), Eps, "one sample: a constant");
            Assert.IsTrue(float.IsNaN(FlowMath.TrackAt(0f, 1f, new float[0], 0f)), "an empty table");
            Assert.IsTrue(float.IsNaN(FlowMath.TrackAt(0f, 1f, null, 0f)));
            Assert.IsTrue(float.IsNaN(FlowMath.TrackAt(0f, 0f, v, 0f)), "dt 0");
            // continuous: no step anywhere (never steeper than the table's steepest slope, 10 per second)
            float prev = FlowMath.TrackAt(t0, dt, v, 9.9f);
            for (int k = 1; k <= 1800; k++)
            {
                float x = FlowMath.TrackAt(t0, dt, v, 9.9f + k * 1e-3f);
                Assert.LessOrEqual(Mathf.Abs(x - prev), 10f * 1e-3f + 1e-4f, $"at {9.9f + k * 1e-3f}");
                prev = x;
            }
        }

        /// <summary>the synthetic v4 direction with a planned track on orbit b (30-40 s, law -80 + 3 (t - 30) deg): its window [31, 39], 10-Hz samples
        /// over 30.9 .. 39.1, deg = the law + 20 x bump, lift = 0.3 x bump (bump = C2Bump over the window: 0 with two derivatives at both ends);
        /// short = how many samples the arrays miss (a broken track)</summary>
        static JObject SyntheticV43(int shortBy = 0)
        {
            JObject d = SyntheticV4();
            d["flow"]["revision"] = "4.3";
            const float t0 = 30.9f, dt = 0.1f, a = 31f, b = 39f;
            const int n = 83;
            JArray deg = new(), lift = new();
            for (int i = 0; i < n - shortBy; i++)
            {
                float t = t0 + i * dt, bump = FlowMath.C2Bump((t - a) / (b - a));
                deg.Add(-80f + 3f * (t - 30f) + 20f * bump);
                lift.Add(0.3f * bump);
            }

            d["flow"]["azimuth_tracks"] = new JObject
            {
                ["b2"] = new JObject
                {
                    ["schema"] = "hm film azimuth track v1", ["film"] = new JArray(a, b), ["t0"] = t0, ["dt"] = dt, ["n"] = n, ["deg"] = deg, ["lift_m"] = lift, ["interp"] = "linear",
                    ["law"] = new JObject { ["world_azimuth_deg"] = -80, ["drift_deg_s"] = 3, ["F0"] = 30 },
                    ["rules"] = new JObject
                    {
                        ["rate_min_deg_s"] = 1.0, ["rate_max_deg_s"] = 7.0, ["rate_max_outside_deg_s"] = 6.0, ["accel_max_deg_s2"] = 2.0, ["lift_top_m"] = 2.4
                    },
                    ["crossing"] = new JObject { ["film"] = new JArray(34.0, 36.0) }
                }
            };
            ((JObject)d["segments"][2]["camera"])["azimuth_track"] = "b2";
            return d;
        }

        [Test]
        public void AzimuthTrackPlaysOnlyStrictlyInsideItsWindow()
        {
            FlowTimeline tl = FlowTimeline.Build(SyntheticV43());
            Assert.AreEqual(0, tl.Warnings.Count, string.Join("; ", tl.Warnings));
            Assert.AreEqual("4.3", tl.Revision);
            Assert.AreEqual(4, tl.Shots.Count, "the track key does not split a shot");
            Assert.AreEqual(1, tl.Tracks.Count);
            FlowShot sb = tl.Shots[2];
            Assert.AreEqual("b2", sb.TrackId);
            Assert.NotNull(sb.Track);
            Assert.IsNull(tl.Shots[0].Track);
            Assert.IsNull(tl.Shots[3].Track);
            FlowTrack tr = sb.Track;
            Assert.AreEqual(83, tr.N);
            // at the window's ends and outside it: the block's law (no table value, no planned lift)
            foreach (float t in new[] { 30f, 30.95f, 31f, 39f, 39.05f, 40f })
            {
                Assert.IsFalse(tr.TryAzimuthDeg(t, out _), $"azimuth at {t}");
                Assert.IsFalse(tr.TryLift(t, out _), $"lift at {t}");
            }

            // inside: the table (the law + 20 deg at the middle, lift 0.3 m)
            Assert.IsTrue(tr.TryAzimuthDeg(35f, out float d35));
            Assert.AreEqual(-80f + 3f * 5f + 20f, d35, 1e-3f);
            Assert.IsTrue(tr.TryLift(35f, out float l35));
            Assert.AreEqual(0.3f, l35, 1e-4f);
            // the switch at the ends is invisible: the table equals the law there
            Assert.IsTrue(tr.TryAzimuthDeg(31.0001f, out float dA));
            Assert.AreEqual(tr.LawDeg(31.0001f), dA, 1e-3f);
            Assert.IsTrue(tr.TryAzimuthDeg(38.9999f, out float dB));
            Assert.AreEqual(tr.LawDeg(38.9999f), dB, 1e-3f);
            Assert.IsTrue(tr.TryLift(31.0001f, out float lA));
            Assert.AreEqual(0f, lA, 1e-5f);
            // the rules for the orbit check (UNITY_CONTRACT_v4 L)
            Assert.AreEqual(1f, tr.RateMin, Eps);
            Assert.AreEqual(7f, tr.RateMaxAt(35f), Eps, "inside the crossing");
            Assert.AreEqual(6f, tr.RateMaxAt(32f), Eps, "outside it");
            Assert.AreEqual(6f, tr.RateMaxAt(float.NaN), Eps);
            Assert.IsTrue(tr.InCrossing(34f) && tr.InCrossing(36f));
            Assert.IsFalse(tr.InCrossing(33.9f) || tr.InCrossing(36.1f));
            Assert.AreEqual(2f, tr.AccelMax, Eps);
            Assert.AreEqual(2.4f, tr.LiftTopM, Eps);
        }

        [Test]
        public void ABrokenAzimuthTrackIsDroppedWithAWarning()
        {
            // arrays one sample short of n: the track is dropped, the shot naming it plays its law (two warnings: the track, the shot)
            FlowTimeline tl = FlowTimeline.Build(SyntheticV43(1));
            Assert.AreEqual(0, tl.Tracks.Count);
            Assert.AreEqual("b2", tl.Shots[2].TrackId);
            Assert.IsNull(tl.Shots[2].Track);
            Assert.AreEqual(2, tl.Warnings.Count, string.Join("; ", tl.Warnings));
            // a window not inside its samples
            JObject d = SyntheticV43();
            d["flow"]["azimuth_tracks"]["b2"]["film"] = new JArray(30.5, 39.0);
            FlowTimeline t2 = FlowTimeline.Build(d);
            Assert.AreEqual(0, t2.Tracks.Count);
            Assert.IsTrue(t2.Warnings.Any(w => w.Contains("not inside the samples")), string.Join("; ", t2.Warnings));
            // a lift table of the wrong length
            JObject d3 = SyntheticV43();
            ((JArray)d3["flow"]["azimuth_tracks"]["b2"]["lift_m"]).RemoveAt(0);
            Assert.AreEqual(0, FlowTimeline.Build(d3).Tracks.Count);
            // a shot naming a track that does not exist
            JObject d4 = SyntheticV4();
            ((JObject)d4["segments"][0]["camera"])["azimuth_track"] = "nope";
            FlowTimeline t4 = FlowTimeline.Build(d4);
            Assert.IsNull(t4.Shots[0].Track);
            Assert.AreEqual(1, t4.Warnings.Count, string.Join("; ", t4.Warnings));
        }

        [Test]
        public void V43DirectionAzimuthTrackKeepsItsRules()
        {
            if (!File.Exists(V4Direction)) Assert.Ignore($"no {V4Direction}");
            JObject d = JObject.Parse(File.ReadAllText(V4Direction));
            FlowTimeline tl = FlowTimeline.Build(d);
            Assert.NotNull(tl);
            if (tl.Revision != "4.3") Assert.Ignore($"revision {tl.Revision}: no azimuth tracks");
            Assert.AreEqual(0, tl.Warnings.Count, string.Join("; ", tl.Warnings));
            Assert.GreaterOrEqual(tl.Tracks.Count, 1);
            JArray segs = (JArray)d["segments"];
            foreach (FlowTrack tr in tl.Tracks.Values)
            {
                List<FlowShot> users = tl.Shots.Where(s => s.Track == tr).ToList();
                Assert.AreEqual(1, users.Count, $"track {tr.Id}: played by {users.Count} shots");
                FlowShot sh = users[0];
                Assert.AreEqual("orbit", sh.Mode);
                JObject cam = (JObject)segs[sh.FirstSegment]["camera"];
                // every segment of the shot names the track (the blocks stay identical: still one shot)
                for (int i = sh.FirstSegment; i <= sh.LastSegment; i++) Assert.AreEqual(tr.Id, segs[i]["camera"]?.Value<string>("azimuth_track"), $"segment {i}");
                // the window lies between the arrival of the shot's way in and the start of the next shot's way: no way frame plays the table
                Assert.GreaterOrEqual(tr.A, sh.TransEnd - 1e-3f, "the window starts inside the way in");
                FlowShot next = sh.Index + 1 < tl.Shots.Count ? tl.Shots[sh.Index + 1] : null;
                if (next?.T != null) Assert.LessOrEqual(tr.B, next.TransStart + 1e-3f, "the window reaches into the next way");
                Assert.LessOrEqual(tr.B, sh.F1 + 1e-3f);
                // the law at both ends of the window (the block's keys and the track's own record agree)
                float w0 = cam.Value<float>("world_azimuth_deg"), dr = cam.Value<float>("drift_deg_s");
                Assert.AreEqual(w0, tr.LawAzimuthDeg, 1e-4f);
                Assert.AreEqual(dr, tr.LawDriftDegS, 1e-4f);
                Assert.AreEqual(sh.F0, tr.LawF0, 1e-3f);
                float Law(float t) => w0 + dr * (t - sh.F0);
                Assert.AreEqual(Law(tr.A), FlowMath.TrackAt(tr.T0, tr.Dt, tr.Deg, tr.A), 1e-3f, "the table vs the law at the window's start");
                Assert.AreEqual(Law(tr.B), FlowMath.TrackAt(tr.T0, tr.Dt, tr.Deg, tr.B), 1e-3f, "... at its end");
                // every 1/30-s step turns RateMin .. RateMax its way (never reversing; RateMaxOutside unless both ends are in the crossing), |accel| <= AccelMax
                float sign = Mathf.Sign(dr);
                double rMin = double.MaxValue, rMax = 0.0, rMaxOut = 0.0, aMax = 0.0;
                for (int i = 0; i + 1 < tr.N; i++)
                {
                    double r = sign * ((double)tr.Deg[i + 1] - tr.Deg[i]) / tr.Dt;
                    rMin = System.Math.Min(rMin, r);
                    rMax = System.Math.Max(rMax, r);
                    if (!(tr.InCrossing(tr.T0 + i * tr.Dt) && tr.InCrossing(tr.T0 + (i + 1) * tr.Dt))) rMaxOut = System.Math.Max(rMaxOut, r);
                    if (i + 2 < tr.N) aMax = System.Math.Max(aMax, System.Math.Abs(((double)tr.Deg[i + 2] - 2.0 * tr.Deg[i + 1] + tr.Deg[i]) / ((double)tr.Dt * tr.Dt)));
                }

                Assert.GreaterOrEqual(rMin, tr.RateMin - 0.01, "the slowest step");
                Assert.LessOrEqual(rMax, tr.RateMax + 0.01, "the fastest step");
                if (!float.IsNaN(tr.RateMaxOutside)) Assert.LessOrEqual(rMaxOut, tr.RateMaxOutside + 0.01, "the fastest step outside the crossing");
                Assert.LessOrEqual(aMax, tr.AccelMax + 0.05, "the acceleration (float tables: 0.05 deg/s2 of rounding)");
                // the lift: 0 at both ends of the window, never below 0, never above the ground band's top over the block's eye height
                Assert.NotNull(tr.Lift);
                Assert.AreEqual(0f, FlowMath.TrackAt(tr.T0, tr.Dt, tr.Lift, tr.A), 1e-4f);
                Assert.AreEqual(0f, FlowMath.TrackAt(tr.T0, tr.Dt, tr.Lift, tr.B), 1e-4f);
                Assert.GreaterOrEqual(tr.Lift.Min(), -1e-4f);
                Assert.LessOrEqual(tr.Lift.Max(), tr.LiftTopM - cam.Value<float>("height_m") + 1e-3f);
            }
        }
    }
}
