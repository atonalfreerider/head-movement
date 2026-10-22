using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace HmFilm.Tests
{
    /// <summary>edit-mode tests of the v3 film flow maths and timing (HmFilmFlow): the easing, the surface envelopes and their hand-off to the
    /// full-frame video, the composite paths (exact ends, continuity, G1 into a phone's axis, the clear radius, a crane without roll), the
    /// timeline's camera states and the face sidecar geometry; and the real v3 direction when the workspace has it</summary>
    public class FlowTests
    {
        const float Eps = 1e-4f;

        [Test]
        public void SmootherstepEndsAndShape()
        {
            Assert.AreEqual(0f, FlowMath.Smootherstep(0f), Eps);
            Assert.AreEqual(1f, FlowMath.Smootherstep(1f), Eps);
            Assert.AreEqual(0.5f, FlowMath.Smootherstep(0.5f), Eps);
            Assert.AreEqual(0f, FlowMath.Smootherstep(-3f), Eps);
            Assert.AreEqual(1f, FlowMath.Smootherstep(4f), Eps);
            float prev = 0f;
            for (int i = 1; i <= 100; i++)
            {
                float v = FlowMath.Smootherstep(i / 100f);
                Assert.GreaterOrEqual(v, prev);
                prev = v;
            }

            // zero velocity at the ends: the first and last 1 % of the time move less than 0.01 %
            Assert.Less(FlowMath.Smootherstep(0.01f), 1e-4f);
            Assert.Greater(FlowMath.Smootherstep(0.99f), 1f - 1e-4f);
        }

        [Test]
        public void EnvelopeEnterHandsOverAtItsEnd()
        {
            // enter [a, b, c, c]: 0 before a, 1 just before c, 0 from c (the full-frame video carries the picture from that frame)
            Assert.AreEqual(0f, FlowMath.Envelope(7.4f, 7.95f, 9.3f, 9.3f, 7.39f), Eps);
            Assert.AreEqual(0.5f, FlowMath.Envelope(7.4f, 8.0f, 9.3f, 9.3f, 7.7f), 1e-3f);
            Assert.AreEqual(1f, FlowMath.Envelope(7.4f, 7.95f, 9.3f, 9.3f, 9.2999f), Eps);
            Assert.AreEqual(0f, FlowMath.Envelope(7.4f, 7.95f, 9.3f, 9.3f, 9.3f), Eps);
        }

        [Test]
        public void EnvelopeLeaveTakesOverAtItsStart()
        {
            // leave [a, a, c, d]: 0 before a (the full-frame video), 1 from a, fading to 0 at d
            Assert.AreEqual(0f, FlowMath.Envelope(12.8f, 12.8f, 14.25f, 14.92f, 12.7999f), Eps);
            Assert.AreEqual(1f, FlowMath.Envelope(12.8f, 12.8f, 14.25f, 14.92f, 12.8f), Eps);
            Assert.AreEqual(1f, FlowMath.Envelope(12.8f, 12.8f, 14.25f, 14.92f, 14.25f), Eps);
            Assert.AreEqual(0f, FlowMath.Envelope(12.8f, 12.8f, 14.25f, 14.92f, 14.92f), Eps);
        }

        [Test]
        public void BranchKeepsTheTurnOfABigPlannedArc()
        {
            float d = FlowMath.Branch(-170f * Mathf.Deg2Rad, 166f * Mathf.Deg2Rad);
            Assert.AreEqual(190f, d * Mathf.Rad2Deg, 1e-3f);
            // a small turn takes the short way whatever the plan says
            Assert.AreEqual(-10f, FlowMath.Branch(-10f * Mathf.Deg2Rad, 166f * Mathf.Deg2Rad) * Mathf.Rad2Deg, 1e-3f);
            Assert.AreEqual(-150f, FlowMath.Branch(210f * Mathf.Deg2Rad, 100f * Mathf.Deg2Rad) * Mathf.Rad2Deg, 1e-3f);
        }

        static FlowPath PhoneInPath(out Vector3 start, out Vector3 phone, out Vector3 fwd, out Quaternion r0, out Quaternion r1)
        {
            Vector3 c = Vector3.zero;
            start = new Vector3(4.5f, 1.7f, -2.0f);                          // a 3D orbit eye
            phone = new Vector3(-2.2f, 1.0f, 2.0f);                          // a phone on the far side
            fwd = (new Vector3(0f, 1.0f, 0f) - phone).normalized;            // looking at the couple
            Vector3 portal = phone - fwd * 1f;
            r0 = Quaternion.LookRotation(new Vector3(0, 1, 0) - start, Vector3.up);
            r1 = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(-4f, 6f, 0f); // the cropped view
            FlowPath p = new();
            p.Cyl(c, start, null, portal, fwd, 120f * Mathf.Deg2Rad, 1.4f, 0.5f, 2.4f, r0, r1, Vector3.up, Vector3.up, Vector3.up, Vector3.up);
            p.Line(portal, phone, r1);
            return p;
        }

        [Test]
        public void PhoneInPathIsExactAtItsEndsAndContinuous()
        {
            FlowPath p = PhoneInPath(out Vector3 start, out Vector3 phone, out Vector3 fwd, out Quaternion r0, out Quaternion r1);
            p.Sample(0f, out Vector3 e0, out Quaternion q0);
            p.Sample(1f, out Vector3 e1, out Quaternion q1);
            Assert.Less(Vector3.Distance(e0, start), 1e-5f);
            Assert.Less(Vector3.Distance(e1, phone), 1e-5f);
            Assert.Less(Quaternion.Angle(q0, r0), 0.01f);
            Assert.Less(Quaternion.Angle(q1, r1), 0.01f);
            // 30 fps over a 1.4 s smootherstep: no frame moves more than 1.6 x the mean step (no jump anywhere along the path)
            int frames = 42;
            Vector3 prev = e0;
            float total = 0f, max = 0f;
            for (int i = 1; i <= frames; i++)
            {
                p.Sample(FlowMath.Smootherstep(i / (float)frames), out Vector3 e, out _);
                float d = Vector3.Distance(prev, e);
                total += d;
                max = Mathf.Max(max, d);
                prev = e;
            }

            Assert.Less(max, 2.0f * total / frames);
        }

        [Test]
        public void PhoneInPathArrivesAlongTheOpticalAxisOutsideTheClearRadius()
        {
            FlowPath p = PhoneInPath(out _, out Vector3 phone, out Vector3 fwd, out _, out _);
            p.Sample(0.97f, out Vector3 a, out _);
            p.Sample(0.995f, out Vector3 b, out _);
            Assert.Greater(Vector3.Dot((b - a).normalized, fwd), 0.999f);
            for (int i = 0; i <= 200; i++)
            {
                p.Sample(i / 200f, out Vector3 e, out _);
                Assert.GreaterOrEqual(new Vector2(e.x, e.z).magnitude, 1.4f - 1e-3f, $"inside the clear radius at {i / 200f}");
                Assert.That(e.y, Is.InRange(0.5f - 1e-3f, 2.4f + 1e-3f));
            }
        }

        [Test]
        public void CraneHasNoRoll()
        {
            Vector3 look = new(0.3f, 0f, -0.2f);
            float az = 40f * Mathf.Deg2Rad;
            float el0 = 20f * Mathf.Deg2Rad, el1 = 89.9f * Mathf.Deg2Rad;
            Vector3 e0 = look + FlowMath.Spherical(az, el0) * 4f, e1 = look + FlowMath.Spherical(az, el1) * 7f;
            Quaternion r0 = Quaternion.LookRotation(look - e0, Vector3.up), r1 = Quaternion.LookRotation(look - e1, FlowMath.CraneUp(az, el1));
            FlowPath p = new();
            p.Crane(look, look, az, az, el0, el1, 4f, 7f, r0, r1);
            Vector3 plane = new(-Mathf.Sin(az), 0f, Mathf.Cos(az)); // the normal of the crane's vertical plane
            for (int i = 0; i <= 50; i++)
            {
                p.Sample(i / 50f, out Vector3 e, out Quaternion q);
                // the camera's right axis stays horizontal (no roll) and its up / forward stay in the vertical plane
                Assert.Less(Mathf.Abs(Vector3.Dot(q * Vector3.right, Vector3.up)), 0.02f, $"roll at {i / 50f}");
                Assert.Less(Mathf.Abs(Vector3.Dot(q * Vector3.forward, plane)), 0.03f);
                Assert.Less(Mathf.Abs(Vector3.Dot(e - look, plane)), 1e-3f);
            }
        }

        [Test]
        public void PlanGoesRoundAPictureItWouldCross()
        {
            // a phone at 4 m from the couple looking at it, its picture 0.3 m in front; the 3D view to reach stands 0.7 m in front of the phone on
            // its axis: back along the axis 1 m, then the way must go round the picture, not through it, and not double back on itself
            Vector3 c = Vector3.zero, phone = new(0f, 1.6f, -4f), fwd = Vector3.forward, portal = phone - fwd, e = phone + fwd * 0.7f;
            float hh = 0.3f * Mathf.Tan(25f * Mathf.Deg2Rad), hw = hh * 9f / 16f;
            FlowPath.CylGeo g = new() { C = c, P0 = portal, T0 = -fwd, P3 = e, PlanDelta = 0f, RMin = 1.4f, YLo = 0.5f, YHi = 2.4f };
            g.Rects.Add((phone + fwd * 0.3f, Vector3.right, Vector3.up, fwd, hw + 0.2f, hh + 0.2f));
            Assert.GreaterOrEqual(FlowPath.CylBadness(c, portal, -fwd, e, null, 0f, 1.4f, 0.5f, 2.4f, 0f, 0f, g.Rects), 1000f, "the straight way crosses");
            Vector2 plan = FlowPath.AvoidPlan(new[] { g }, out float bad);
            Assert.Less(bad, 1000f, $"plan {plan} still crosses");
            Assert.Less(FlowPath.CylBadness(c, portal, -fwd, e, null, 0f, 1.4f, 0.5f, 2.4f, plan.x, plan.y, g.Rects), 1000f);
            Assert.Greater(Mathf.Abs(plan.x) + plan.y, 0f);
        }

        // ------------------------------------------------------------------ the timeline

        static JObject Seg(string id, float f0, float f1, JObject cam, float d0 = float.NaN, float d1 = float.NaN) => new()
        {
            ["id"] = id, ["film"] = new JArray(f0, f1), ["dance"] = new JArray(float.IsNaN(d0) ? f0 : d0, float.IsNaN(d1) ? f1 : d1), ["camera"] = cam
        };

        /// <summary>orbit 0-10, phone 05 10-16 (way in -0.5 s, 1.0 s), phone 00 16-20 (phone to phone centred), physics 20-30 (way out at 20)</summary>
        static JObject Synthetic()
        {
            JObject orbit = new() { ["mode"] = "orbit", ["world_azimuth_deg"] = 10 };
            JObject p05 = new()
            {
                ["mode"] = "camera_tour", ["cameras"] = new JArray(new JObject { ["camera"] = "05", ["dance_t"] = new JArray(9.5, 16.6) }),
                ["transition_in"] = new JObject { ["type"] = "phone_in", ["start_s"] = -0.5, ["duration_s"] = 1.0, ["path"] = "arc_then_axis", ["layers_switch_s"] = 0.5,
                    ["video"] = new JObject { ["full_frame_at_s"] = 0.5, ["splats_hidden_at_s"] = 0.5, ["overlays_on_top_in_s"] = 0.35 } }
            };
            JObject p00 = new()
            {
                ["mode"] = "camera_tour", ["cameras"] = new JArray(new JObject { ["camera"] = "00", ["dance_t"] = new JArray(15.4, 20.8) }),
                ["transition_in"] = new JObject { ["type"] = "phone_to_phone", ["start_s"] = -0.6, ["duration_s"] = 1.2, ["path"] = "axis_arc_axis", ["layers_switch_s"] = 0.6,
                    ["video"] = new JObject { ["full_frame_at_s"] = 0.6 },
                    ["video_out"] = new JObject { ["surface_from_s"] = -0.6, ["splats_back_in_s"] = 0.35, ["overlays_on_top_off_before_s"] = 0.25 },
                    ["transit_layers"] = new JObject { ["film"] = new JArray(15.4, 16.6), ["layers"] = new JObject { ["floor"] = true } } }
            };
            JObject phys = new()
            {
                ["mode"] = "physics_3q", ["world_azimuth_deg"] = -80,
                ["transition_in"] = new JObject { ["type"] = "phone_out", ["start_s"] = 0.0, ["duration_s"] = 0.9, ["path"] = "axis_then_arc", ["layers_switch_s"] = 0.0,
                    ["video_out"] = new JObject { ["surface_from_s"] = 0.0, ["splats_back_in_s"] = 0.35, ["overlays_on_top_off_before_s"] = 0.25 } }
            };
            return new JObject
            {
                ["segments"] = new JArray(Seg("a", 0, 10, orbit), Seg("b", 10, 13, p05), Seg("b2", 13, 16, (JObject)p05.DeepClone()), Seg("c", 16, 20, p00), Seg("d", 20, 30, phys)),
                ["flow"] = new JObject
                {
                    ["version"] = 1, ["defaults"] = new JObject { ["frustum_depth_m"] = 0.9, ["ground_m"] = new JArray(0.5, 2.4) },
                    ["frustums"] = new JArray(
                        new JObject { ["camera"] = "05", ["film"] = new JArray(9.0, 9.6, 10.5, 10.5), ["role"] = "enter", ["shot"] = "b" },
                        new JObject { ["camera"] = "05", ["film"] = new JArray(15.4, 15.4, 16.2, 16.6), ["role"] = "leave", ["shot"] = "b" },
                        new JObject { ["camera"] = "00", ["film"] = new JArray(15.5, 15.9, 16.6, 16.6), ["role"] = "enter", ["shot"] = "c" },
                        new JObject { ["camera"] = "00", ["film"] = new JArray(20, 20, 20.6, 21.3), ["role"] = "leave", ["shot"] = "c" })
                }
            };
        }

        [Test]
        public void TimelineShotsWindowsAndCameraStates()
        {
            FlowTimeline tl = FlowTimeline.Build(Synthetic());
            Assert.NotNull(tl);
            Assert.AreEqual(4, tl.Shots.Count, "segments b and b2 share their camera block: one shot");
            Assert.AreEqual(0, tl.Warnings.Count, string.Join("; ", tl.Warnings));
            Assert.AreEqual(2, tl.Windows.Count);
            Assert.AreEqual(10.5f, tl.Windows[0].Arrival, Eps);
            Assert.AreEqual(15.4f, tl.Windows[0].Departure, Eps);
            Assert.AreEqual(16.6f, tl.Windows[1].Arrival, Eps);
            Assert.AreEqual(20f, tl.Windows[1].Departure, Eps);

            tl.CameraAt(9.0f, out int own, out int trans, out _);
            Assert.AreEqual((0, -1), (own, trans));
            tl.CameraAt(9.7f, out own, out trans, out float u);
            Assert.AreEqual((-1, 1), (own, trans));
            Assert.AreEqual(0.2f, u, 1e-3f);
            tl.CameraAt(12f, out own, out trans, out _);
            Assert.AreEqual((1, -1), (own, trans));
            tl.CameraAt(16f, out own, out trans, out _);
            Assert.AreEqual((-1, 2), (own, trans));
            tl.CameraAt(20.5f, out own, out trans, out _);
            Assert.AreEqual((-1, 3), (own, trans));
            Assert.AreEqual(10.5f, tl.LayerStart(1), Eps);
            Assert.AreEqual(13f, tl.LayerStart(2), Eps);
            Assert.AreEqual(16.6f, tl.LayerStart(3), Eps);
            Assert.NotNull(tl.TransitAt(16f));
        }

        [Test]
        public void TimelineHandOffIsCoveredOnBothSides()
        {
            FlowTimeline tl = FlowTimeline.Build(Synthetic());
            List<FlowSurface> s = new();
            // the frame before the arrival: the entering surface alone, opaque, its cover at 1
            tl.Surfaces(10.5f - 1f / 30f, s);
            FlowSurface enter = s.Single(x => x.Camera == "05");
            Assert.AreEqual(1f, enter.Alpha, Eps);
            Assert.Greater(enter.Top, 0.95f);
            // the arrival frame: no surface, the video cover at 1, the splats hidden
            tl.Surfaces(10.5f, s);
            Assert.IsFalse(s.Any(x => x.Camera == "05"));
            Assert.AreEqual(1f, tl.VideoCover(10.5f), Eps);
            Assert.AreEqual(0f, tl.SplatVisibility(10.5f), Eps);
            Assert.AreEqual(1f, tl.SplatVisibility(10.49f), Eps);
            // overlays fade in over 0.35 s, out over 0.25 s before the departure
            Assert.AreEqual(1f, tl.VideoCover(10.5f + 1f / 30f), Eps, "the cover holds a frame after the arrival");
            Assert.AreEqual(0f, tl.VideoCover(10.5f + FlowTimeline.CoverHoldS + 0.35f), Eps);
            Assert.AreEqual(0f, tl.VideoCover(13f), Eps);
            Assert.Greater(tl.VideoCover(15.4f - 0.01f), 0.99f);
            // the departure frame: the leaving surface opaque with its cover, the full-frame video gone
            tl.Surfaces(15.4f, s);
            FlowSurface leave = s.Single(x => x.Camera == "05");
            Assert.AreEqual(1f, leave.Alpha, Eps);
            Assert.AreEqual(1f, leave.Top, Eps);
            Assert.AreEqual(0f, tl.VideoCover(15.4f), Eps);
            Assert.AreEqual(0f, tl.SplatVisibility(15.4f), Eps);
            Assert.AreEqual(1f, tl.SplatVisibility(15.4f + 0.35f), Eps);
        }

        [Test]
        public void TimelineIsNullWithoutFlow()
        {
            JObject d = Synthetic();
            d.Remove("flow");
            Assert.IsNull(FlowTimeline.Build(d));
        }

        // ------------------------------------------------------------------ the face sidecar geometry

        [Test]
        public void FaceAnglesFollowTheLineOfSight()
        {
            Vector3 cam = new(0, 1.6f, -3f), head = new(0, 1.6f, 0);
            // facing the camera (face forward = -z of the head toward the camera)
            FaceSidecarMath.FaceAngles(head, cam, Vector3.right, Vector3.up, Vector3.back, Vector3.up, out float yaw, out float pitch, out float roll);
            Assert.AreEqual(0f, yaw, 1e-3f);
            Assert.AreEqual(0f, pitch, 1e-3f);
            Assert.AreEqual(0f, roll, 1e-3f);
            // turned to the image's right
            FaceSidecarMath.FaceAngles(head, cam, Vector3.right, Vector3.up, Vector3.right, Vector3.up, out yaw, out _, out _);
            Assert.AreEqual(90f, yaw, 1e-3f);
            // the back of the head
            FaceSidecarMath.FaceAngles(head, cam, Vector3.right, Vector3.up, Vector3.forward, Vector3.up, out yaw, out _, out _);
            Assert.AreEqual(180f, Mathf.Abs(yaw), 1e-3f);
            // face up 30 deg
            Vector3 up30 = new(0f, Mathf.Sin(30f * Mathf.Deg2Rad), -Mathf.Cos(30f * Mathf.Deg2Rad));
            FaceSidecarMath.FaceAngles(head, cam, Vector3.right, Vector3.up, up30, Vector3.up, out _, out pitch, out _);
            Assert.AreEqual(30f, pitch, 1e-2f);
        }

        [Test]
        public void ClipNearCutsTheRectangle()
        {
            List<Vector3> q = new() { new(-1, 1, 2), new(1, 1, 2), new(1, -1, -1), new(-1, -1, -1) };
            List<Vector3> c = FaceSidecarMath.ClipNear(q, 0.1f);
            Assert.AreEqual(4, c.Count);
            Assert.IsTrue(c.All(p => p.z >= 0.1f - 1e-5f));
            Assert.AreEqual(4, FaceSidecarMath.ClipNear(new List<Vector3> { new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1) }, 0.1f).Count);
            Assert.AreEqual(0, FaceSidecarMath.ClipNear(new List<Vector3> { new(0, 0, -1), new(1, 0, -1), new(1, 1, -1) }, 0.1f).Count);
        }

        [Test]
        public void CapsuleBlocksOnlyInFront()
        {
            Vector3 cam = new(0, 1.6f, -3f), face = new(0, 1.6f, 0);
            Assert.IsTrue(FaceSidecarMath.Blocks(cam, face, new Vector3(-0.3f, 1.6f, -1f), new Vector3(0.3f, 1.6f, -1f), 0.05f), "an arm across the line of sight");
            Assert.IsFalse(FaceSidecarMath.Blocks(cam, face, new Vector3(-0.3f, 1.6f, 1f), new Vector3(0.3f, 1.6f, 1f), 0.05f), "behind the face");
            Assert.IsFalse(FaceSidecarMath.Blocks(cam, face, new Vector3(-0.3f, 2.2f, -1f), new Vector3(0.3f, 2.2f, -1f), 0.05f), "above the line");
            Assert.IsTrue(FaceSidecarMath.Inside(new Vector2(0.5f, 0.5f), new List<Vector2> { new(0, 0), new(1, 0), new(1, 1), new(0, 1) }));
        }

        // ------------------------------------------------------------------ the real v3 direction (skipped without the workspace's file)

        // HM_FILM_DIRECTION names a direction.json; else the first take under the sibling dancecap/work that has a v3 one
        // (the take folder is not named here: this repository is public)
        static string V3Direction
        {
            get
            {
                string env = System.Environment.GetEnvironmentVariable("HM_FILM_DIRECTION");
                if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);
                string work = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "dancecap", "work"));
                if (Directory.Exists(work))
                    foreach (string take in Directory.GetDirectories(work))
                    {
                        string p = Path.Combine(take, "v3", "direction", "direction.json");
                        if (File.Exists(p)) return p;
                    }
                return Path.Combine(work, "<take>", "v3", "direction", "direction.json");
            }
        }

        [Test]
        public void V3DirectionTimelineHoldsTheOwnerRules()
        {
            if (!File.Exists(V3Direction)) Assert.Ignore($"no {V3Direction}");
            FlowTimeline tl = FlowTimeline.Build(JObject.Parse(File.ReadAllText(V3Direction)));
            Assert.NotNull(tl);
            Assert.AreEqual(0, tl.Warnings.Count, string.Join("; ", tl.Warnings));
            // no jump cut: every shot but the first has a transition, one easing
            Assert.IsTrue(tl.Shots.Skip(1).All(s => s.T != null), "a shot without transition_in");
            Assert.IsTrue(tl.Shots.Skip(1).All(s => s.T.Easing == "smootherstep"));
            // the crane only at the 4 overheads: 8 crane ways
            List<FlowShot> cranes = tl.Shots.Where(s => s.T != null && (s.T.CranePath || s.T.Crane)).ToList();
            Assert.AreEqual(8, cranes.Count);
            (float, float)[] overheads = { (173.5f, 179.4f), (237.8f, 243.3f), (279.0f, 284.3f), (333.2f, 339.4f) };
            foreach (FlowShot s in cranes)
                Assert.IsTrue(overheads.Any(o => s.TransEnd >= o.Item1 - 2.6f && s.TransStart <= o.Item2 + 0.1f), $"crane way {s.FirstSegmentId} not at an overhead");
            // phone holds >= 1.4 s full frame; every frustum mapped onto its shot
            Assert.IsTrue(tl.Windows.All(w => w.Departure - w.Arrival >= 1.4f - 2e-3f), "a phone hold under 1.4 s");
            Assert.IsTrue(tl.Frustums.All(f => f.ShotIndex >= 0));
            // the envelopes are long enough to read (the geometric check runs in Play mode: hm_film_flow)
            foreach (FlowFrustum f in tl.Frustums)
            {
                float half = f.Enter ? f.C - (f.A + f.B) * 0.5f : (f.C + f.D) * 0.5f - f.B;
                Assert.GreaterOrEqual(half, 0.6f, $"frustum {f.Camera} {f.Role} at {f.A}");
            }

            // the hand-off at every window: surface at 1 the frame before the arrival and at the departure, covers at 1
            List<FlowSurface> s1 = new();
            foreach (FlowPhoneWindow w in tl.Windows)
            {
                tl.Surfaces(w.Arrival - 1e-3f, s1);
                Assert.IsTrue(s1.Any(x => x.Camera == w.Phone && x.Alpha > 0.999f), $"phone {w.Phone}: no opaque surface before the arrival {w.Arrival}");
                if (w.Shot == tl.Shots.Count - 1) continue; // the film ends on this phone: no way out
                tl.Surfaces(w.Departure, s1);
                Assert.IsTrue(s1.Any(x => x.Camera == w.Phone && x.Alpha > 0.999f && x.Top > 0.999f), $"phone {w.Phone}: no opaque surface at the departure {w.Departure}");
                Assert.AreEqual(1f, tl.VideoCover(w.Arrival), Eps);
            }
        }
    }
}
